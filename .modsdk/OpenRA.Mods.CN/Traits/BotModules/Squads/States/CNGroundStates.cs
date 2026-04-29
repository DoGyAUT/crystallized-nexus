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

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	// ---------------------------------------------------------------------------
	// Base class: leader election, flee, shared target finding
	// ---------------------------------------------------------------------------

	abstract class CNGroundStateBase : CNStateBase
	{
		const int AssaultStagingMinCells = 6;
		const int AssaultStagingMaxCells = 11;
		const int AssaultThreatRadiusCells = 6;

		Actor leader;

		/// <summary>
		/// Elects a leader for the squad. Prefers the most restrictive locomotor
		/// to avoid nominating a hovercraft as leader for a tank squad.
		/// The leader persists until it leaves the squad or a new one is forced.
		/// </summary>
		protected Actor Leader(CNSquad squad)
		{
			if (leader == null || !squad.Units.Contains(leader) || leader.IsDead)
				leader = ElectNewLeader(squad);
			return leader;
		}

		protected void ForceNewLeader() { leader = null; }

		static Actor ElectNewLeader(CNSquad squad)
		{
			IEnumerable<Actor> candidates = squad.OrderableUnits.Where(a => !a.IsDead).ToList();
			if (!candidates.Any())
				return null;

			var leastCommon = candidates
				.Select(a => a.TraitOrDefault<Mobile>()?.Locomotor)
				.Where(l => l != null)
				.MinByOrDefault(l => l.Info.TerrainSpeeds.Count)
				?.Info.TerrainSpeeds.Count;

			if (leastCommon != null)
				candidates = candidates
					.Where(a => a.TraitOrDefault<Mobile>()?.Locomotor.Info.TerrainSpeeds.Count == leastCommon)
					.ToList();

			var center = candidates.Select(a => a.CenterPosition).Average();
			return candidates.MinBy(a => (a.CenterPosition - center).LengthSquared);
		}

		protected override bool ShouldFlee(CNSquad squad)
		{
			return ShouldFlee(squad, enemies =>
				!CNAttackOrFleeFuzzy.Default.CanAttack(squad.Units, enemies));
		}

		/// <summary>
		/// Finds the best attack target:
		/// 1. PriorityTargetCapabilities from template (if configured)
		/// 2. Closest visible enemy unit
		/// 3. Closest enemy building (no shroud check)
		/// </summary>
		protected static Actor FindTarget(CNSquad squad)
			=> CNSquadHelper.FindTarget(squad);

		protected static Actor FindNearbyEnemyBuilding(CNSquad squad, WPos center, int radiusCells)
		{
			return squad.World
				.FindActorsInCircle(center, WDist.FromCells(radiusCells))
				.Where(a => squad.SquadManager.IsLiveEnemyActor(a) &&
				            a.Info.HasTraitInfo<BuildingInfo>() &&
				            !a.Info.HasTraitInfo<LineBuildInfo>())
				.MinByOrDefault(a => (a.CenterPosition - center).LengthSquared);
		}

		protected static CPos? FindAssaultStagingCell(CNSquad squad, Actor target)
		{
			if (target == null)
				return null;

			var leader = squad.CenterUnit();
			var mobile = leader?.TraitOrDefault<Mobile>();
			if (leader == null || mobile == null)
				return null;

			var map = squad.World.Map;
			var targetCell = map.CellContaining(target.CenterPosition);
			var baseCell = squad.SquadManager.GetRandomBaseCenter();
			CPos? bestCell = null;
			var bestScore = int.MaxValue;

			for (var dy = -AssaultStagingMaxCells; dy <= AssaultStagingMaxCells; dy++)
			{
				for (var dx = -AssaultStagingMaxCells; dx <= AssaultStagingMaxCells; dx++)
				{
					var distanceSquared = dx * dx + dy * dy;
					if (distanceSquared < AssaultStagingMinCells * AssaultStagingMinCells ||
						distanceSquared > AssaultStagingMaxCells * AssaultStagingMaxCells)
						continue;

					var candidate = targetCell + new CVec(dx, dy);
					if (!map.Contains(candidate) || !mobile.CanEnterCell(candidate))
						continue;

					var score = ScoreAssaultStagingCell(squad, candidate, targetCell, baseCell);
					if (score >= bestScore)
						continue;

					bestScore = score;
					bestCell = candidate;
				}
			}

			return bestCell;
		}

		protected static Actor FindAssaultEntryTarget(CNSquad squad, CPos stagingCell, Actor defaultTarget)
		{
			var stagingPos = squad.World.Map.CenterOfCell(stagingCell);
			Actor bestTarget = null;
			var bestScore = int.MaxValue;

			foreach (var actor in squad.World.FindActorsInCircle(stagingPos, WDist.FromCells(8)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor) || !actor.CanBeViewedByPlayer(squad.Bot.Player))
					continue;

				if (!actor.Info.HasTraitInfo<BuildingInfo>())
					continue;

				var score = ScoreAssaultEntryTarget(squad, stagingPos, actor);
				if (score >= bestScore)
					continue;

				bestScore = score;
				bestTarget = actor;
			}

			return bestTarget ?? defaultTarget;
		}

		protected static Actor FindRushTarget(CNSquad squad, Actor defaultTarget)
		{
			var leader = squad.CenterUnit();
			if (leader == null || leader.IsDead || !leader.IsInWorld)
				return defaultTarget;

			Actor bestTarget = null;
			var bestScore = int.MaxValue;

			void CheckCandidate(Actor actor)
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor) || !actor.CanBeViewedByPlayer(squad.Bot.Player))
					return;
				var score = ScoreRushTarget(leader, actor);
				if (score < bestScore)
				{
					bestScore = score;
					bestTarget = actor;
				}
			}

			foreach (var actor in squad.World.ActorsHavingTrait<Mobile>())
				CheckCandidate(actor);
			foreach (var actor in squad.World.ActorsHavingTrait<Aircraft>())
				CheckCandidate(actor);
			foreach (var actor in squad.SquadManager.GetCachedEnemyBuildings())
			{
				if (!actor.CanBeViewedByPlayer(squad.Bot.Player))
					continue;
				var score = ScoreRushTarget(leader, actor);
				if (score < bestScore)
				{
					bestScore = score;
					bestTarget = actor;
				}
			}

			return bestTarget ?? defaultTarget;
		}

		static int ScoreAssaultStagingCell(CNSquad squad, CPos candidate, CPos targetCell, CPos baseCell)
		{
			var world = squad.World;
			var candidatePos = world.Map.CenterOfCell(candidate);
			var score = 0;

			score += (candidate - targetCell).LengthSquared * 10;

			var baseToCandidate = (candidate - baseCell).LengthSquared;
			var baseToTarget = (targetCell - baseCell).LengthSquared;
			if (baseToCandidate < baseToTarget)
				score += (baseToTarget - baseToCandidate) * 5;

			foreach (var actor in world.FindActorsInCircle(candidatePos, WDist.FromCells(AssaultThreatRadiusCells)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor))
					continue;

				var isBuilding = actor.Info.HasTraitInfo<BuildingInfo>();
				var canAttack = actor.Info.HasTraitInfo<AttackBaseInfo>();

				if (isBuilding && canAttack)
					score += 300;
				else if (canAttack)
					score += 90;
				else if (isBuilding)
					score -= 20;
			}

			return score;
		}

		static int ScoreAssaultEntryTarget(CNSquad squad, WPos stagingPos, Actor target)
		{
			var score = (int)((target.CenterPosition - stagingPos).LengthSquared / 65536);

			foreach (var actor in squad.World.FindActorsInCircle(target.CenterPosition, WDist.FromCells(5)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor))
					continue;

				var isBuilding = actor.Info.HasTraitInfo<BuildingInfo>();
				var canAttack = actor.Info.HasTraitInfo<AttackBaseInfo>();

				if (isBuilding && canAttack)
					score += 220;
				else if (canAttack)
					score += 70;
			}

			return score;
		}

		static int ScoreRushTarget(Actor leader, Actor target)
		{
			if (leader == null || target == null || leader.IsDead || target.IsDead || !leader.IsInWorld || !target.IsInWorld)
				return int.MaxValue;

			var score = (int)((target.CenterPosition - leader.CenterPosition).LengthSquared / 65536);

			if (target.Info.HasTraitInfo<BuildingInfo>())
				score -= 120;

			if (target.Info.HasTraitInfo<AttackBaseInfo>())
				score += 90;

			if (target.Info.Name.Contains("harv", StringComparison.OrdinalIgnoreCase) ||
				target.Info.Name.Contains("proc", StringComparison.OrdinalIgnoreCase) ||
				target.Info.Name.Contains("ref", StringComparison.OrdinalIgnoreCase))
				score -= 220;

			return score;
		}
	}

	// ---------------------------------------------------------------------------
	// Idle: find target → issue AttackMove → AttackMoveState
	// No CanAttack check here — flee decisions happen in AttackMoveState where
	// the squad is close to actual enemies, not evaluated against a distant base.
	// ---------------------------------------------------------------------------

	sealed class CNGroundIdleState : CNGroundStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (!squad.IsTargetValid)
			{
				var enemy = FindTarget(squad);
				if (enemy == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundFleeState());
					return;
				}

				squad.SetActorToTarget(enemy);
			}

			if (squad.Type == CNSquadType.Assault && squad.TargetActor.Info.HasTraitInfo<BuildingInfo>())
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AssaultStagingState());
				return;
			}

			if (squad.Type == CNSquadType.Rush)
			{
				var rushTarget = FindRushTarget(squad, squad.TargetActor);
				squad.SetActorToTarget(rushTarget);
			}

			// Issue move order and hand off to AttackMoveState for regrouping + engagement
			squad.Bot.QueueOrder(new Order("AttackMove", null, squad.Target, false,
				groupedActors: squad.OrderableUnits.ToArray()));
			squad.FuzzyStateMachine.ChangeState(squad, new CNGroundAttackMoveState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AssaultStagingState : CNGroundStateBase, ICNState
	{
		CPos? stagingCell;
		int waitTicks;
		const int MaxStageTicks = 6;
		const int GatherRadiusCells = 4;

		public void Activate(CNSquad squad)
		{
			waitTicks = 0;
			stagingCell = squad.IsTargetValid ? FindAssaultStagingCell(squad, squad.TargetActor) : null;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (!squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
				return;
			}

			if (!stagingCell.HasValue)
			{
				squad.Bot.QueueOrder(new Order("AttackMove", null, squad.Target, false,
					groupedActors: squad.OrderableUnits.ToArray()));
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundAttackMoveState());
				return;
			}

			waitTicks++;
			var stagingPos = squad.World.Map.CenterOfCell(stagingCell.Value);
			var gatherRadius = WDist.FromCells(GatherRadiusCells);
			var packedCount = squad.OrderableUnits.Count(u =>
				(u.CenterPosition - stagingPos).Length <= gatherRadius.Length);
			var totalCount = squad.OrderableUnits.Count();

			if (packedCount < totalCount)
			{
				foreach (var unit in squad.OrderableUnits)
				{
					if (!unit.IsIdle)
						continue;

					squad.Bot.QueueOrder(new Order("Move", unit, Target.FromCell(squad.World, stagingCell.Value), false));
				}
			}

			if ((totalCount > 0 && packedCount >= Math.Max(2, totalCount * 2 / 3)) || waitTicks >= MaxStageTicks)
			{
				var entryTarget = FindAssaultEntryTarget(squad, stagingCell.Value, squad.TargetActor);
				squad.SetActorToTarget(entryTarget);
				squad.Bot.QueueOrder(new Order("AttackMove", null, squad.Target, false,
					groupedActors: squad.OrderableUnits.ToArray()));
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundAttackMoveState());
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	// ---------------------------------------------------------------------------
	// AttackMove: march toward target with regrouping.
	// Switches to AttackState when enemies are within AttackScanRadius.
	// Vanilla port — stuck detection uses WorldTick, timeout raised to 200
	// (vanilla uses 63 which would always trigger at our 75-tick update interval).
	// ---------------------------------------------------------------------------

	sealed class CNGroundAttackMoveState : CNGroundStateBase, ICNState
	{
		int lastUpdatedTick;
		CPos? lastLeaderLocation;
		Actor lastTarget;
		const int StuckTimeoutTicks = 200;

		public void Activate(CNSquad squad)
		{
			lastUpdatedTick = squad.World.WorldTick;
			lastLeaderLocation = null;
			lastTarget = null;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (!squad.IsTargetValid)
			{
				ForceNewLeader();
				var enemy = squad.Type == CNSquadType.Rush
					? FindRushTarget(squad, FindTarget(squad))
					: FindTarget(squad);
				if (enemy == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundFleeState());
					return;
				}

				squad.SetActorToTarget(enemy);
			}

			var leader = Leader(squad);
			if (leader == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
				return;
			}

			// Reset stuck clock on leader movement or target change
			if (leader.Location != lastLeaderLocation)
			{
				lastLeaderLocation = leader.Location;
				lastUpdatedTick = squad.World.WorldTick;
			}

			if (squad.TargetActor != lastTarget)
			{
				lastTarget = squad.TargetActor;
				lastUpdatedTick = squad.World.WorldTick;
			}

			// Stuck detection — drop back to idle to re-evaluate target/path
			if (squad.World.WorldTick > lastUpdatedTick + StuckTimeoutTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
				return;
			}

			// Regroup: stop leader so stragglers can catch up.
			// Minimum 3 cells so small squads (2–4 units) aren't permanently stuck regrouping.
			var regroupCells = Math.Max(3, squad.Units.Count / 3);
			var regroupRadius = WDist.FromCells(regroupCells);
			var nearPack = squad.World
				.FindActorsInCircle(leader.CenterPosition, regroupRadius)
				.Where(squad.Units.Contains)
				.ToHashSet();

			if (nearPack.Count < squad.Units.Count)
			{
				squad.Bot.QueueOrder(new Order("Stop", leader, false));
				var stragglers = squad.OrderableUnits.Where(a => !nearPack.Contains(a)).ToArray();
				squad.Bot.QueueOrder(new Order("AttackMove", null,
					Target.FromCell(squad.World, leader.Location), false,
					groupedActors: stragglers));
			}
			else
			{
				// All together — switch to direct attack if enemies are close
				var nearEnemy = squad.SquadManager.FindClosestEnemy(leader,
					WDist.FromCells(squad.SquadManager.Info.AttackScanRadius));
				nearEnemy ??= FindNearbyEnemyBuilding(squad, leader.CenterPosition,
					squad.SquadManager.Info.AttackScanRadius * 2);
				if (nearEnemy != null)
				{
					squad.SetActorToTarget(nearEnemy);
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundAttackState());
				}
				else
				{
					squad.Bot.QueueOrder(new Order("AttackMove", null, squad.Target, false,
						groupedActors: squad.OrderableUnits.ToArray()));
				}
			}

			if (ShouldFlee(squad))
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundFleeState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	// ---------------------------------------------------------------------------
	// Attack: per-unit orders once enemies are in range.
	// When target dies: scan locally first, then fall back to FindClosestEnemyBuilding.
	// This prevents chasing stray units across the map ("camping" bug).
	// ---------------------------------------------------------------------------

	sealed class CNGroundAttackState : CNGroundStateBase, ICNState
	{
		int lastUpdatedTick;
		CPos? lastLeaderLocation;
		Actor lastTarget;
		const int StuckTimeoutTicks = 200;

		public void Activate(CNSquad squad)
		{
			lastUpdatedTick = squad.World.WorldTick;
			lastLeaderLocation = null;
			lastTarget = null;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (!squad.IsTargetValid)
			{
				ForceNewLeader();

				// Step 1: nearby enemy (units + buildings within AttackScanRadius)
				var squadCenter = squad.CenterPosition();
				var next = squad.World
					.FindActorsInCircle(squadCenter, WDist.FromCells(squad.SquadManager.Info.AttackScanRadius))
					.Where(a => squad.SquadManager.IsPreferredEnemyUnit(a))
					.MinByOrDefault(a => (a.CenterPosition - squadCenter).LengthSquared);

				// Step 1b: when pushing into a base, widen the local building scan so the squad
				// keeps clearing nearby structures instead of jumping to a distant fallback target.
				next ??= FindNearbyEnemyBuilding(squad, squadCenter, squad.SquadManager.Info.AttackScanRadius * 2);

				// Step 2: any enemy building on the map (no shroud check)
				next ??= FindClosestEnemyBuilding(squad);

				if (next == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
					return;
				}

				squad.SetActorToTarget(next);
			}

			var leader = Leader(squad);
			if (leader == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
				return;
			}

			var nearbyBuilding = FindNearbyEnemyBuilding(squad, leader.CenterPosition,
				squad.SquadManager.Info.AttackScanRadius * 2);
			if (nearbyBuilding != null && nearbyBuilding != squad.TargetActor)
			{
				squad.SetActorToTarget(nearbyBuilding);
				lastTarget = nearbyBuilding;
				lastUpdatedTick = squad.World.WorldTick;
			}

			// Stuck detection
			if (leader.Location != lastLeaderLocation)
			{
				lastLeaderLocation = leader.Location;
				lastUpdatedTick = squad.World.WorldTick;
			}

			if (squad.TargetActor != lastTarget)
			{
				lastTarget = squad.TargetActor;
				lastUpdatedTick = squad.World.WorldTick;
			}

			if (squad.World.WorldTick > lastUpdatedTick + StuckTimeoutTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
				return;
			}

			// Per-unit orders — only issue if not already attacking (avoids interrupting shots)
			foreach (var unit in squad.OrderableUnits)
				if (!BusyAttack(unit))
					squad.Bot.QueueOrder(new Order("AttackMove", unit, squad.Target, false));

			if (ShouldFlee(squad))
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundFleeState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	// ---------------------------------------------------------------------------
	// Flee: return to base, dissolve squad
	// ---------------------------------------------------------------------------

	sealed class CNGroundFleeState : CNGroundStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			Retreat(squad, flee: true, rearm: true, repair: true);
			squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
		}

		public void Deactivate(CNSquad squad)
		{
			squad.SquadManager.UnregisterSquad(squad);
		}
	}

	// ---------------------------------------------------------------------------
	// Protection states (vanilla port)
	// Reactive defense squads: attack, flee when target lost, dissolve.
	// ---------------------------------------------------------------------------

	sealed class ProtectionIdleState : CNGroundStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }
		public void Tick(CNSquad squad) { squad.FuzzyStateMachine.ChangeState(squad, new ProtectionAttackState()); }
		public void Deactivate(CNSquad squad) { }
	}

	sealed class ProtectionAttackState : CNGroundStateBase, ICNState
	{
		int backoff = BackoffTicks;
		const int BackoffTicks = 4;

		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var leader = Leader(squad);
			if (leader == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new ProtectionFleeState());
				return;
			}

			if (!squad.IsTargetValid)
			{
				var target = squad.SquadManager.FindClosestEnemy(leader,
					WDist.FromCells(squad.SquadManager.Info.ProtectionScanRadius));
				squad.SetActorToTarget(target);
				if (target == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new ProtectionFleeState());
					return;
				}
			}

			squad.Bot.QueueOrder(new Order("AttackMove", null, squad.Target, false,
				groupedActors: squad.OrderableUnits.ToArray()));

			if (!squad.IsTargetVisible)
			{
				if (backoff < 0)
				{
					backoff = BackoffTicks;
					squad.FuzzyStateMachine.ChangeState(squad, new ProtectionFleeState());
					return;
				}

				backoff--;
			}
			else
			{
				backoff = BackoffTicks;
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class ProtectionFleeState : CNGroundStateBase, ICNState
	{
		int waitTicks;
		WPos returnPos;

		// 15 update-cycles × 75 game ticks = ~37 seconds — enough for units to walk home.
		const int MaxWaitTicks = 15;
		const int ArrivalRadiusCells = 6;

		public void Activate(CNSquad squad)
		{
			waitTicks = 0;

			// Lock destination once — use the SAME cell for both the move order and the
			// arrival check. Previously GetRandomBaseCenter() and GoToRandomOwnBuilding()
			// picked independent random buildings, so units walked to building B while
			// arrival was checked against building A → allArrived never became true.
			var returnCell = RandomBuildingLocation(squad);
			returnPos = squad.World.Map.CenterOfCell(returnCell);

			var target = Target.FromCell(squad.World, returnCell);
			foreach (var a in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Move", a, target, false));

			Retreat(squad, flee: false, rearm: true, repair: true);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
			{
				squad.SquadManager.UnregisterSquad(squad);
				return;
			}

			waitTicks++;

			var arrivalDist = WDist.FromCells(ArrivalRadiusCells);
			var units = squad.Units.Where(u => !u.IsDead).ToList();
			var allArrived = units.All(u => (u.CenterPosition - returnPos).Length <= arrivalDist.Length);

			if (allArrived || waitTicks >= MaxWaitTicks)
				squad.SquadManager.UnregisterSquad(squad);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
