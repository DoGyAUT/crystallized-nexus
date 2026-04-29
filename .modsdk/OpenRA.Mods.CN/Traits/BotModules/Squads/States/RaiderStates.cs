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
using OpenRA.Primitives;
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
		int rethinkTicks;
		const int RethinkInterval = 3;

		public void Activate(CNSquad squad) { rethinkTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			rethinkTicks--;
			if (rethinkTicks > 0 && squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderAttackState());
				return;
			}

			rethinkTicks = RethinkInterval;
			var center = squad.CenterUnit();
			if (center == null)
				return;

			Actor target = null;

			if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

			target ??= FindClosestEnemyUnit(squad);

			if (target == null)
				return;

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
		int stuckTicks;
		const int StuckThreshold = 8;
		CPos lastPos;

		public void Activate(CNSquad squad)
		{
			stuckTicks = 0;
			lastPos = CPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (!squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderIdleState());
				return;
			}

			if (ShouldFlee(squad))
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
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderIdleState());
				return;
			}

			foreach (var unit in squad.OrderableUnits)
				if (!BusyAttack(unit))
					squad.Bot.QueueOrder(new Order("AttackMove", unit, squad.Target, false));
		}

		public void Deactivate(CNSquad squad) { }

		protected override bool ShouldFlee(CNSquad squad)
		{
			return ShouldFlee(squad, enemies =>
				!CNAttackOrFleeFuzzy.Raider.CanAttack(squad.Units, enemies));
		}
	}

	/// <summary>
	/// Flee: sprint back to base, then reform and return to Idle.
	/// </summary>
	sealed class RaiderFleeState : CNStateBase, ICNState
	{
		int fleeStartTick;
		const int RegroupWaitTicks = 100;
		const int MinRetreatCells = 5;
		const int MaxRetreatCells = 12;

		public void Activate(CNSquad squad)
		{
			fleeStartTick = squad.World.WorldTick;

			var retreatCell = FindRetreatCell(squad);
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

			var enemy = squad.SquadManager.FindClosestEnemy(center,
				WDist.FromCells(squad.SquadManager.Info.DangerScanRadius));

			if (enemy == null || squad.World.WorldTick - fleeStartTick >= RegroupWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, new RaiderIdleState());
		}

		public void Deactivate(CNSquad squad) { }

		static CPos? FindRetreatCell(CNSquad squad)
		{
			var leader = squad.CenterUnit();
			if (leader == null)
				return null;

			var mobile = leader.TraitOrDefault<Mobile>();
			if (mobile == null)
				return null;

			var map = squad.World.Map;
			var origin = leader.Location;
			var baseCell = squad.SquadManager.GetRandomBaseCenter();
			CPos? bestCell = null;
			var bestScore = int.MinValue;

			for (var dy = -MaxRetreatCells; dy <= MaxRetreatCells; dy++)
			{
				for (var dx = -MaxRetreatCells; dx <= MaxRetreatCells; dx++)
				{
					var distanceSquared = dx * dx + dy * dy;
					if (distanceSquared < MinRetreatCells * MinRetreatCells ||
						distanceSquared > MaxRetreatCells * MaxRetreatCells)
						continue;

					var candidate = origin + new CVec(dx, dy);
					if (!map.Contains(candidate) || !mobile.CanEnterCell(candidate))
						continue;

					var score = ScoreRetreatCell(squad, candidate, baseCell);
					if (score <= bestScore)
						continue;

					bestScore = score;
					bestCell = candidate;
				}
			}

			return bestCell;
		}

		static int ScoreRetreatCell(CNSquad squad, CPos candidate, CPos baseCell)
		{
			var candidatePos = squad.World.Map.CenterOfCell(candidate);
			var closestEnemyDistance = int.MaxValue;

			foreach (var actor in squad.World.FindActorsInCircle(candidatePos,
				WDist.FromCells(squad.SquadManager.Info.DangerScanRadius + 4)))
			{
				if (!squad.SquadManager.IsPreferredEnemyUnit(actor))
					continue;

				var distance = (int)(actor.CenterPosition - candidatePos).LengthSquared;
				if (distance < closestEnemyDistance)
					closestEnemyDistance = distance;
			}

			var score = closestEnemyDistance == int.MaxValue ? 1000000 : closestEnemyDistance;
			score -= (candidate - baseCell).LengthSquared * 4;
			return score;
		}
	}
}
