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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	/// <summary>
	/// Idle: find a priority target using PreferredTargetCapabilities from the team template,
	/// then fall back to the closest visible enemy unit.
	/// Re-evaluates every RethinkInterval ticks.
	/// </summary>
	sealed class RaiderIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;
			if (!squad.IsOperational)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundFleeState());
				return;
			}

			// Already have a usable target — go straight to the attack run.
			if (squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderAttackState());
				return;
			}

			var center = squad.CenterUnit();
			if (center == null)
				return;

			Actor target = null;

			if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

			target ??= FindClosestEnemyUnit(squad);

			if (target == null)
			{
				// No target this pass — reposition to a forward chokepoint instead of sitting
				// still, so the squad is more likely to catch enemy traffic on a later scan.
				var chokepoint = FindAmbushChokepoint(squad, center);
				if (chokepoint.HasValue)
					squad.Bot.QueueOrder(new Order("Move", null, Target.FromCell(squad.World, chokepoint.Value), false,
						groupedActors: squad.OrderableUnits.ToArray()));

				return;
			}

			squad.SetActorToTarget(target);
			squad.FuzzyStateMachine.ChangeState(squad, new RaiderAttackState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Attack: issue AttackMove on priority target.
	/// Uses the Raider fuzzy (aggressive flee threshold — runs at Equal power).
	/// </summary>
	sealed class RaiderAttackState : CNStateBase, ICNState
	{
		const int StuckThreshold = 8;
		const int KiteThreatRadiusCells = 5;
		int stuckTicks;
		CPos lastPos;

		public void Activate(CNSquad squad)
		{
			stuckTicks = 0;
			lastPos = CPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;
			if (!squad.IsOperational)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundFleeState());
				return;
			}

			if (!squad.IsTargetValid)
			{
				var nextTarget = FindNextTarget(squad);
				if (nextTarget == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new RaiderFleeState());
					return;
				}

				squad.SetActorToTarget(nextTarget);
			}

			if (ShouldKite(squad) || ShouldFlee(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderFleeState());
				return;
			}

			// Stuck detection
			var currentPos = squad.CenterUnit()?.Location ?? CPos.Zero;
			if (currentPos == lastPos && currentPos != CPos.Zero)
				stuckTicks++;
			else
			{
				stuckTicks = 0;
				lastPos = currentPos;
			}

			if (stuckTicks > StuckThreshold)
			{
				squad.SetActorToTarget(null);
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderFleeState());
				return;
			}

			foreach (var unit in squad.OrderableUnits)
				if (!BusyAttack(unit))
					squad.Bot.QueueOrder(new Order("AttackMove", unit, squad.Target, false));
		}

		public void Deactivate(CNSquad squad) { }

		static Actor FindNextTarget(CNSquad squad)
		{
			var center = squad.CenterUnit();
			if (center == null)
				return null;

			Actor target = null;
			if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

			return target ?? FindClosestEnemyUnit(squad);
		}

		protected override bool ShouldFlee(CNSquad squad)
		{
			return ShouldFlee(squad, (friendlies, enemies) =>
				!CNAttackOrFleeFuzzy.Raider.CanAttack(friendlies, enemies, squad.SquadManager.GetAttackFuzzyBoost()));
		}

		// Kiting only pays off when backing away actually buys something: the threat must be able
		// to shoot us, and we must outrange it, so the retreat ends with us still firing and the
		// threat unable to answer.
		//
		// The previous version kited on mere proximity — any armed enemy within KiteThreatRadiusCells,
		// no matter its range or ours. Since RaiderFleeState re-engages once the threat is
		// ReengageThreatDistanceCells away, short-ranged raiders drove into their target, kited out,
		// drove back in, and never got a shot off. Same failure mode as the old STNK flee trigger.
		static bool ShouldKite(CNSquad squad)
		{
			var center = squad.CenterUnit();
			if (center == null || !squad.IsTargetValid)
				return false;

			var threat = FindClosestThreat(squad, center, KiteThreatRadiusCells);
			if (threat == null)
				return false;

			// A threat we can't be hit by is no reason to disengage.
			if (!squad.OrderableUnits.Any(u => CanAttackTarget(threat, u)))
				return false;

			return MaxWeaponRange(squad.OrderableUnits) > MaxWeaponRange([threat]);
		}

		static WDist MaxWeaponRange(IEnumerable<Actor> actors)
		{
			var best = WDist.Zero;
			foreach (var actor in actors)
			{
				foreach (var armament in actor.TraitsImplementing<Armament>())
				{
					if (armament.IsTraitDisabled || armament.IsTraitPaused)
						continue;

					var range = armament.MaxRange();
					if (range > best)
						best = range;
				}
			}

			return best;
		}

		internal static Actor FindClosestThreat(CNSquad squad, Actor source, int radiusCells)
		{
			return squad.World.FindActorsInCircle(source.CenterPosition, WDist.FromCells(radiusCells))
				.Where(a =>
					squad.SquadManager.IsPreferredEnemyUnit(a) &&
					a.Info.HasTraitInfo<AttackBaseInfo>())
				.MinByOrDefault(a => (a.CenterPosition - source.CenterPosition).LengthSquared);
		}
	}

	/// <summary>
	/// Flee: sprint back to base, then reform and return to Idle.
	/// </summary>
	sealed class RaiderFleeState : CNStateBase, ICNState
	{
		const int RegroupWaitTicks = 80;
		const int MinRetreatCells = 6;
		const int MaxRetreatCells = 14;
		const int ReengageThreatDistanceCells = 9;
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

			Retreat(squad, flee: false, rearm: true, repair: true);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			var enemy = RaiderAttackState.FindClosestThreat(squad, center, squad.SquadManager.Info.DangerScanRadius);

			if (enemy == null ||
				HasOpenedKiteDistance(center, enemy) ||
				squad.World.WorldTick - fleeStartTick >= RegroupWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderIdleState());
		}

		public void Deactivate(CNSquad squad) { }

		static bool HasOpenedKiteDistance(Actor center, Actor enemy)
		{
			var minDistance = WDist.FromCells(ReengageThreatDistanceCells).Length;
			return HorizontalLengthSquared(center.CenterPosition - enemy.CenterPosition) >= (long)minDistance * minDistance;
		}
	}
}
