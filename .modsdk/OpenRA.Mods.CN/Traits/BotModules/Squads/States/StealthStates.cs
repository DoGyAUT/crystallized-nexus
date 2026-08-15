#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	static class StealthHelpers
	{
		public static bool IsCloakedOrUncloakable(Actor actor)
		{
			foreach (var cloak in actor.TraitsImplementing<Cloak>())
			{
				if (cloak.IsTraitDisabled)
					continue;

				if (!cloak.Cloaked)
					return false;
			}

			return true;
		}
	}

	sealed class StealthIdleState : CNStateBase, ICNState
	{
		// Game ticks, not update cycles: how long a target picked here is kept before the squad
		// looks for a better one.
		const int RethinkInterval = 225;
		int rethinkTicks;

		public void Activate(CNSquad squad) { rethinkTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			rethinkTicks -= squad.TicksSinceLastUpdate;
			if (rethinkTicks > 0 && squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthApproachState());
				return;
			}

			rethinkTicks = RethinkInterval;
			var center = squad.CenterUnit();
			if (center == null)
				return;

			// Killing a soft target often returns here before the first-volley commitment expires. Do
			// not start a fresh commitment window for every building in the base: a revealed squad that
			// is now losing the local fight should disengage and cloak before choosing the next victim.
			if (squad.OrderableUnits.Any(u => !StealthHelpers.IsCloakedOrUncloakable(u)) && ShouldFlee(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthFleeState());
				return;
			}

			Actor target = null;
			if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

			target ??= CNSquadHelper.FindUnprotectedTarget(squad);

			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new StealthApproachState());
				return;
			}

			// No target this pass — reposition to a forward chokepoint instead of sitting
			// still, so cloaked units are more likely to catch enemy traffic on a later scan.
			var chokepoint = FindAmbushChokepoint(squad, center);
			if (chokepoint.HasValue)
				squad.Bot.QueueOrder(new Order("Move", null, Target.FromCell(squad.World, chokepoint.Value), false,
					groupedActors: squad.OrderableUnits.ToArray()));
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class StealthApproachState : CNStateBase, ICNState
	{
		// A stealth strike that reevaluates the fight on the same tick as its first volley often turns
		// around before the missiles land. Give the ambush enough time to kill its focus target, then
		// restore the normal threat/health decision instead of making the squad permanently fearless.
		const int MinimumCommitTicks = 150;
		const int MaxStuckTicks = 225;

		bool ordersIssued;
		int firstRevealTick;
		int lastActivityTick;
		CPos lastCenterPos;

		public void Activate(CNSquad squad)
		{
			ordersIssued = false;
			firstRevealTick = 0;
			lastActivityTick = squad.World.WorldTick;
			lastCenterPos = squad.CenterUnit()?.Location ?? CPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
				return;
			}

			if (!ordersIssued)
				IssueApproachOrders(squad);

			var allCloaked = squad.OrderableUnits.All(StealthHelpers.IsCloakedOrUncloakable);
			if (!allCloaked && firstRevealTick == 0)
				firstRevealTick = squad.World.WorldTick;

			var committed = firstRevealTick > 0 &&
				squad.World.WorldTick - firstRevealTick < MinimumCommitTicks;

			// A still-cloaked squad has not joined the local fight yet. Letting nearby enemies feed the
			// fuzzy flee check here made covert routes peel away merely because they passed a defended
			// area. Once revealed, the squad commits to one volley window and then judges the real fight.
			if (!allCloaked && !committed && ShouldFlee(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthFleeState());
				return;
			}

			var currentPos = squad.CenterUnit()?.Location ?? CPos.Zero;
			var anyAiming = IsAnyUnitAiming(squad);

			if (currentPos != lastCenterPos || anyAiming)
			{
				lastActivityTick = squad.World.WorldTick;
				lastCenterPos = currentPos;
			}

			if (squad.World.WorldTick > lastActivityTick + MaxStuckTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
				return;
			}

			foreach (var unit in squad.OrderableUnits)
				if (unit.IsIdle)
					squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
		}

		public void Deactivate(CNSquad squad) { }

		protected override bool ShouldFlee(CNSquad squad)
		{
			// Default (not Raider) profile: stealth tanks are hard-hitting ambushers that should
			// trade with an even or weaker enemy and only flee when injured/outgunned. The Raider
			// profile fled even at full health against a healthy enemy, which made them too timid.
			return ShouldFlee(squad, (friendlies, enemies) =>
				CannotAttackEvenTogether(CNAttackOrFleeFuzzy.Default, squad, friendlies, enemies));
		}

		void IssueApproachOrders(CNSquad squad)
		{
			var center = squad.CenterUnit();
			if (center == null)
				return;

			var units = squad.OrderableUnits.OrderBy(u => u.ActorID).ToArray();
			if (units.Length == 0)
				return;

			// A direct Attack order asks the pathfinder for the shortest road. Against a rear building
			// that road commonly enters through the front and crosses the whole enemy base. Reuse the
			// pinned infiltration route so the cloaked group reaches the selected weak entrance first.
			var route = squad.TargetActor.Info.HasTraitInfo<BuildingInfo>()
				? squad.SquadManager.BuildTransportApproachRoute(center.Location, squad.TargetActor, center.Info)
				: [];
			var queued = false;
			foreach (var waypoint in route)
			{
				squad.Bot.QueueOrder(new Order("Move", null,
					Target.FromCell(squad.World, waypoint), queued, groupedActors: units));
				queued = true;
			}

			foreach (var unit in units)
				squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, queued));

			ordersIssued = true;
		}

		static bool IsAnyUnitAiming(CNSquad squad)
		{
			foreach (var unit in squad.OrderableUnits)
			{
				foreach (var attack in unit.TraitsImplementing<AttackBase>())
					if (!attack.IsTraitDisabled && attack.IsAiming)
						return true;
			}

			return false;
		}
	}

	sealed class StealthFleeState : CNStateBase, ICNState
	{
		const int RecloakWaitTicks = 150;
		const int MinRetreatCells = 6;
		const int MaxRetreatCells = 14;
		const int ReengageThreatDistanceCells = 10;
		int fleeStartTick;

		public void Activate(CNSquad squad)
		{
			fleeStartTick = squad.World.WorldTick;

			var retreatCell = FindRetreatCell(squad, MinRetreatCells, MaxRetreatCells);
			if (retreatCell.HasValue)
			{
				var target = Target.FromCell(squad.World, retreatCell.Value);
				foreach (var unit in squad.OrderableUnits)
					squad.Bot.QueueOrder(new Order("Move", unit, target, false));
			}
			else
				GoToRandomOwnBuilding(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			var enemy = RaiderAttackState.FindClosestThreat(squad, center, squad.SquadManager.Info.DangerScanRadius);
			var allCloaked = squad.OrderableUnits.All(StealthHelpers.IsCloakedOrUncloakable);

			if (enemy == null ||
				(allCloaked && HasOpenedKiteDistance(center, enemy)) ||
				squad.World.WorldTick - fleeStartTick >= RecloakWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
		}

		public void Deactivate(CNSquad squad) { }

		static bool HasOpenedKiteDistance(Actor center, Actor enemy)
		{
			var minDistance = WDist.FromCells(ReengageThreatDistanceCells).Length;
			return HorizontalLengthSquared(center.CenterPosition - enemy.CenterPosition) >= (long)minDistance * minDistance;
		}
	}
}
