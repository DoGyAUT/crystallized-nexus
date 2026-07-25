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
	sealed class StealthIdleState : CNStateBase, ICNState
	{
		const int RethinkInterval = 3;
		int rethinkTicks;

		public void Activate(CNSquad squad) { rethinkTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (--rethinkTicks > 0 && squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthApproachState());
				return;
			}

			rethinkTicks = RethinkInterval;
			var center = squad.CenterUnit();
			if (center == null)
				return;

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
		const int MaxStuckTicks = 120;
		int lastActivityTick;
		CPos lastCenterPos;

		public void Activate(CNSquad squad)
		{
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

			// Stealth units uncloak to fire (UncloakOn: Attack), so being "revealed" is the normal
			// state mid-attack — not a reason to bail. Previously any uncloaked unit triggered a
			// flee, so the squad retreated right after its first shot and never committed to a kill.
			// Fleeing is now driven purely by the threat/health assessment, so the squad presses the
			// attack and only pulls back when it is actually being beaten.
			if (ShouldFlee(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthFleeState());
				return;
			}

			var currentPos = squad.CenterUnit()?.Location ?? CPos.Zero;
			var anyAttacking = squad.Units.Any(u => !u.IsDead && !u.IsIdle);

			if (currentPos != lastCenterPos || anyAttacking)
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
			return ShouldFlee(squad, enemies =>
				!CNAttackOrFleeFuzzy.Default.CanAttack(squad.Units, enemies, squad.SquadManager.GetAttackFuzzyBoost()));
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
			var allCloaked = squad.OrderableUnits.All(IsCloakedOrUncloakable);

			if (enemy == null ||
				(allCloaked && HasOpenedKiteDistance(center, enemy)) ||
				squad.World.WorldTick - fleeStartTick >= RecloakWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
		}

		public void Deactivate(CNSquad squad) { }

		static bool IsCloakedOrUncloakable(Actor actor)
		{
			var cloaks = actor.TraitsImplementing<Cloak>().Where(c => !c.IsTraitDisabled).ToList();
			return cloaks.Count == 0 || cloaks.All(c => c.Cloaked);
		}

		static bool HasOpenedKiteDistance(Actor center, Actor enemy)
		{
			var minDistance = WDist.FromCells(ReengageThreatDistanceCells).Length;
			return HorizontalLengthSquared(center.CenterPosition - enemy.CenterPosition) >= (long)minDistance * minDistance;
		}
	}
}
