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
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	// ---------------------------------------------------------------------------
	// Base class: leader election, flee, shared target finding
	// ---------------------------------------------------------------------------
	abstract class CNGroundStateBase : CNStateBase
	{
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

		/// <summary>
		/// True if the unit sits behind the leader as seen along the march direction, so calling it
		/// forward to the leader is genuinely forward motion. With no usable direction (no target)
		/// every straggler counts as behind, which is the old, direction-blind behaviour.
		/// </summary>
		protected static bool IsBehindLeader(Actor unit, Actor leader, WVec marchDirection)
		{
			if (marchDirection == WVec.Zero)
				return true;

			var offset = unit.CenterPosition - leader.CenterPosition;
			return (long)offset.X * marchDirection.X + (long)offset.Y * marchDirection.Y <= 0;
		}

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
			return ShouldFlee(squad, (friendlies, enemies) =>
			{
				// Only react to enemies that are actively attacking (have an Attack or FlyAttack
				// activity). Nearby units that are patrolling or targeting other forces should not
				// trigger a retreat — the squad holds ground unless it is under direct fire.
				var activeAttackers = enemies.Where(BusyAttack).ToList();
				if (activeAttackers.Count == 0)
					return false;

				return CannotAttackEvenTogether(CNAttackOrFleeFuzzy.Default, squad, friendlies, activeAttackers);
			});
		}

		/// <summary>
		/// Finds the best attack target:
		/// 1. PriorityTargetCapabilities from template (if configured)
		/// 2. Closest visible enemy unit
		/// 3. Closest enemy building (no shroud check).
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

		protected static Actor FindRushTarget(CNSquad squad, Actor defaultTarget)
		{
			var leader = squad.CenterUnit();
			if (leader == null || leader.IsDead || !leader.IsInWorld)
				return defaultTarget;

			Actor bestTarget = null;
			var bestScore = int.MaxValue;

			// Both caches are shared per-tick across all squads searching (see GetCachedEnemyUnits).
			// GetCachedEnemyUnits already filters by IsLiveEnemyActor + visibility; GetCachedEnemyBuildings
			// only filters IsLiveEnemyActor, so the buildings loop still checks visibility itself.
			foreach (var actor in squad.SquadManager.GetCachedEnemyUnits())
			{
				var score = ScoreRushTarget(squad, leader, actor);
				if (score < bestScore)
				{
					bestScore = score;
					bestTarget = actor;
				}
			}

			foreach (var actor in squad.SquadManager.GetCachedEnemyBuildings())
			{
				if (!actor.CanBeViewedByPlayer(squad.Bot.Player))
					continue;
				var score = ScoreRushTarget(squad, leader, actor);
				if (score < bestScore)
				{
					bestScore = score;
					bestTarget = actor;
				}
			}

			return bestTarget ?? defaultTarget;
		}

		static int ScoreRushTarget(CNSquad squad, Actor leader, Actor target)
		{
			if (leader == null || target == null || leader.IsDead || target.IsDead || !leader.IsInWorld || !target.IsInWorld)
				return int.MaxValue;

			var score = (int)((target.CenterPosition - leader.CenterPosition).LengthSquared / 65536);

			if (target.Info.HasTraitInfo<BuildingInfo>())
				score -= 120;

			if (target.Info.HasTraitInfo<AttackBaseInfo>())
				score += 90;

			// Economy targets, read from the capability system rather than from actor-name fragments.
			// The substring match missed Nod's WEED (it carries Harvester, but its name contains none
			// of "harv"/"proc"/"ref") and the tiberium silo, while matching harvester husks, an
			// editor-only placeholder and a colour-picker actor — none of them battlefield targets.
			var caps = target.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (caps != null && (caps.Contains("Harvester") || caps.Contains("Economy")))
				score -= 220;

			// Prefer targets the squad's actual composition can hit well, so a squad heavy on
			// anti-armor doesn't keep picking infantry it can barely scratch when armor is nearby.
			score -= (int)(CNSquadHelper.SquadEngageFraction(squad, target) * 150);

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
		// Units this close to ANY own building are considered "at base" and won't be
		// dissolved into CNGroundFleeState when no target exists, preventing the
		// idle→flee→random-building-move cycle that causes visible base jitter.
		const int HomeRadiusCells = 20;

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

			if (!squad.IsTargetValid)
			{
				var enemy = FindTarget(squad);
				if (enemy == null)
				{
					// No visible enemy. If units are already near base, hold position
					// rather than issuing a move-to-random-building on every update cycle.
					var buildings = squad.SquadManager.GetCachedOwnBuildings();
					var homeRadiusSq = (long)WDist.FromCells(HomeRadiusCells).Length *
									   WDist.FromCells(HomeRadiusCells).Length;
					var allHome = squad.Units
						.Where(u => !u.IsDead && u.IsInWorld)
						.All(u => buildings.Any(b =>
							(u.CenterPosition - b.CenterPosition).LengthSquared <= homeRadiusSq));
					if (allHome)
						return;

					// Having no target is not a retreat. Walk home but stay in this state: the flee
					// state now regroups and returns instead of dissolving, so routing "nothing to
					// attack" through it would bounce the squad between idle and flee forever, and
					// each pass through flee would reset the manager's no-target release timer.
					// Staying idle lets ReleaseStaleNoTargetSquad free the units if this persists.
					Retreat(squad, flee: true, rearm: true, repair: true);
					return;
				}

				squad.SetActorToTarget(enemy);
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

	// ---------------------------------------------------------------------------
	// AttackMove: march toward target with regrouping.
	// Switches to AttackState when enemies are within AttackScanRadius.
	// Vanilla port — stuck detection uses WorldTick, timeout raised to 200
	// (vanilla uses 63 which would always trigger at our 75-tick update interval).
	// ---------------------------------------------------------------------------
	sealed class CNGroundAttackMoveState : CNGroundStateBase, ICNState
	{
		const int StuckTimeoutTicks = 200;

		// Regrouping is entered and left at different fill levels. With one threshold for both, a
		// squad sitting at the boundary flipped every evaluation - halt, gather, march, spread,
		// halt - and crawled to the target in a series of reversals. Entering needs the squad to be
		// genuinely torn apart; leaving only needs it reasonably closed up again.
		const int RegroupEnterPercent = 50;
		const int RegroupLeavePercent = 75;

		int lastUpdatedTick;
		CPos? lastLeaderLocation;
		Actor lastTarget;
		bool regrouping;

		public void Activate(CNSquad squad)
		{
			lastUpdatedTick = squad.World.WorldTick;
			lastLeaderLocation = null;
			lastTarget = null;
			regrouping = false;
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
				ForceNewLeader();
				var enemy = squad.Type == CNSquadType.Rush
					? FindRushTarget(squad, FindTarget(squad))
					: FindTarget(squad);
				if (enemy == null)
				{
					// Out of targets, not beaten — hand back to idle, which walks the squad home and
					// lets the manager's no-target release decide its fate. Routing this through the
					// flee state would bounce between the two now that fleeing regroups.
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
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

			// Regroup: hold the leader only while the squad is genuinely torn apart.
			var regroupCells = Math.Max(4, squad.Units.Count / 2);
			var regroupRadius = WDist.FromCells(regroupCells);
			var nearPack = squad.World
				.FindActorsInCircle(leader.CenterPosition, regroupRadius)
				.Where(squad.Units.Contains)
				.ToHashSet();

			var liveCount = squad.Units.Count(a => a != null && !a.IsDead && a.IsInWorld);
			var packedPercent = liveCount > 0 ? nearPack.Count * 100 / liveCount : 100;
			regrouping = regrouping
				? packedPercent < RegroupLeavePercent
				: packedPercent < RegroupEnterPercent;

			if (regrouping)
			{
				squad.Bot.QueueOrder(new Order("Stop", leader, false));

				// Only units BEHIND the leader are called back to it. "Straggler" used to mean
				// anything outside the radius regardless of direction, so units that had pushed
				// ahead were ordered to drive back to the leader's cell - and forward again on the
				// next evaluation. That reversal, not the halt, is what made a squad visibly
				// oscillate. Anything already ahead simply holds while the rear closes up.
				var marchDirection = squad.TargetActor != null && !squad.TargetActor.IsDead && squad.TargetActor.IsInWorld
					? squad.TargetActor.CenterPosition - leader.CenterPosition
					: WVec.Zero;

				var behind = squad.OrderableUnits
					.Where(a => !nearPack.Contains(a) && IsBehindLeader(a, leader, marchDirection))
					.ToArray();

				if (behind.Length > 0)
					squad.Bot.QueueOrder(new Order("AttackMove", null,
						Target.FromCell(squad.World, leader.Location), false,
						groupedActors: behind));
			}
			else
			{
				// Majority together — switch to direct attack if enemies are close
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
		const int StuckTimeoutTicks = 200;
		int lastUpdatedTick;
		CPos? lastLeaderLocation;
		Actor lastTarget;

		public void Activate(CNSquad squad)
		{
			lastUpdatedTick = squad.World.WorldTick;
			lastLeaderLocation = null;
			lastTarget = null;
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
				ForceNewLeader();

				// Step 1: nearby enemy (units + buildings within AttackScanRadius)
				var squadCenter = squad.CenterPosition();
				var next = squad.World
					.FindActorsInCircle(squadCenter, WDist.FromCells(squad.SquadManager.Info.AttackScanRadius))
					.Where(a => squad.SquadManager.IsPreferredEnemyUnit(a) && !a.Info.HasTraitInfo<LineBuildInfo>())
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

			// Kill-securing takes priority over re-targeting a nearby building this tick — a
			// near-dead enemy in easy reach is finished off before the squad moves on.
			var killSecureTarget = FindKillSecureTarget(squad, leader.CenterPosition);
			if (killSecureTarget != null && killSecureTarget != squad.TargetActor)
			{
				squad.SetActorToTarget(killSecureTarget);
				lastTarget = killSecureTarget;
				lastUpdatedTick = squad.World.WorldTick;
			}
			else
			{
				var nearbyBuilding = FindNearbyEnemyBuilding(squad, leader.CenterPosition,
					squad.SquadManager.Info.AttackScanRadius * 2);
				if (nearbyBuilding != null && nearbyBuilding != squad.TargetActor)
				{
					squad.SetActorToTarget(nearbyBuilding);
					lastTarget = nearbyBuilding;
					lastUpdatedTick = squad.World.WorldTick;
				}
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

			// Send the badly hurt home before deciding whether the squad as a whole should leave: a
			// squad that keeps a nearly dead unit in the firing line loses it, and losing it is what
			// pushes the squad under strength and triggers the full retreat in the first place.
			WithdrawDamagedUnits(squad);

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
	// ---------------------------------------------------------------------------
	// Flee: pull back, let the wounded repair, then either return to the fight or
	// dissolve if too little of the squad is left to be worth reforming.
	// ---------------------------------------------------------------------------
	sealed class CNGroundFleeState : CNGroundStateBase, ICNState
	{
		// Game ticks. Long enough to actually disengage and start repairs, capped so a squad that
		// retreated into a quiet corner does not sit there for the rest of the match.
		const int MaxRegroupTicks = 750;

		int fleeStartTick;
		bool dissolving;

		public void Activate(CNSquad squad)
		{
			fleeStartTick = squad.World.WorldTick;

			// Decided once, on entry: a squad this far below strength is not worth walking back out,
			// so its survivors are better off in the pool feeding a fresh full-strength squad. This
			// is the case the old unconditional dissolve was written for — it just applied it to
			// every retreat, including the ones a squad could have come back from.
			//
			// IsOperational is part of the bar because losing a whole template slot is permanent for
			// an attack squad: it is never topped up mid-mission (see AllowsOperationalReinforcement).
			// A squad that came back still non-operational would be sent straight back here by the
			// very check that dispatched it. So regrouping is for the squad that is intact but
			// outgunned, and dissolving for the one that has actually been broken up.
			dissolving = !CanRegroup(squad) || !squad.IsOperational;

			Retreat(squad, flee: true, rearm: true, repair: true);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid || dissolving)
			{
				squad.SquadManager.UnregisterSquad(squad);
				return;
			}

			// Hold the retreat until nothing is chasing us any more, or the clock runs out. Returning
			// while the threat is still in range would just trip the flee check again and leave the
			// squad shuffling back and forth in front of it.
			var center = squad.CenterUnit();
			var pursuer = center != null
				? squad.SquadManager.FindClosestEnemy(center,
					WDist.FromCells(squad.SquadManager.Info.DangerScanRadius))
				: null;

			if (pursuer != null && squad.World.WorldTick - fleeStartTick < MaxRegroupTicks)
				return;

			// Losses during the retreat can still take it below the bar.
			if (!CanRegroup(squad) || !squad.IsOperational)
			{
				squad.SquadManager.UnregisterSquad(squad);
				return;
			}

			squad.SquadManager.InitializeSquadStateForRole(squad);
		}

		public void Deactivate(CNSquad squad) { }

		/// <summary>
		/// True if enough of the squad's template strength survives to be worth sending back out.
		/// Squads without a template have no nominal strength to measure against, so any that still
		/// has units regroups.
		/// </summary>
		static bool CanRegroup(CNSquad squad)
		{
			var live = squad.Units.Count(u => u != null && !u.IsDead && u.IsInWorld);
			if (live == 0)
				return false;

			var nominal = squad.SlotAssignments
				.Where(a => a != null && !a.SlotInfo.Optional)
				.Sum(a => a.SlotInfo.Count);

			if (nominal <= 0)
				return true;

			var percent = System.Math.Clamp(squad.SquadManager.Info.SquadRegroupStrengthPercent, 0, 100);
			return live * 100 >= nominal * percent;
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
		// Game ticks, not update cycles: how long the squad keeps chasing a target it can no longer
		// see before giving up on it.
		const int BackoffTicks = 300;
		int backoff = BackoffTicks;

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

			if (squad.IsTargetValid && !CNSquadHelper.CanSquadEngage(squad, squad.TargetActor))
				squad.SetActorToTarget(null);

			if (!squad.IsTargetValid)
			{
				var target = squad.SquadManager.FindClosestEnemy(leader,
					WDist.FromCells(squad.SquadManager.Info.ProtectionScanRadius),
					a => CNSquadHelper.CanSquadEngage(squad, a));
				squad.SetActorToTarget(target);
				if (target == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new ProtectionFleeState());
					return;
				}
			}

			var attackers = squad.OrderableUnits
				.Where(u => !u.IsDead && CanAttackTarget(u, squad.TargetActor))
				.ToArray();
			if (attackers.Length == 0)
			{
				squad.SetActorToTarget(null);
				squad.FuzzyStateMachine.ChangeState(squad, new ProtectionFleeState());
				return;
			}

			squad.Bot.QueueOrder(new Order("AttackMove", null, squad.Target, false,
				groupedActors: attackers));

			if (!squad.IsTargetVisible)
			{
				if (backoff < 0)
				{
					backoff = BackoffTicks;
					squad.FuzzyStateMachine.ChangeState(squad, new ProtectionFleeState());
					return;
				}

				backoff -= squad.TicksSinceLastUpdate;
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
		// Game ticks, not update cycles: ~45 seconds, enough for units to walk home.
		const int MaxWaitTicks = 1125;
		const int ArrivalRadiusCells = 6;
		int waitTicks;
		WPos returnPos;

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

			waitTicks += squad.TicksSinceLastUpdate;

			var arrivalDist = WDist.FromCells(ArrivalRadiusCells);
			var units = squad.Units.Where(u => !u.IsDead).ToList();
			var allArrived = units.All(u => (u.CenterPosition - returnPos).Length <= arrivalDist.Length);

			if (allArrived || waitTicks >= MaxWaitTicks)
				squad.SquadManager.UnregisterSquad(squad);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
