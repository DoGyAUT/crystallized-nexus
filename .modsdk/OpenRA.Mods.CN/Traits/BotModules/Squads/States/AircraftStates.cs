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
	abstract class AircraftStateBase : CNStateBase
	{
		protected const int AircraftThreatScanCells = 8;
		protected const int MaxAcceptableAircraftThreatScore = 800;
		protected const int AircraftStagingRadiusCells = 4;
		const int ApproachAnnulusMin = 4;
		const int ApproachAnnulusMax = 9;

		protected static bool HasCombatAircraft(CNSquad squad)
		{
			foreach (var unit in squad.OrderableUnits)
				if (unit.Info.HasTraitInfo<AircraftInfo>() && unit.Info.HasTraitInfo<AttackBaseInfo>())
					return true;

			return false;
		}

		protected static bool NeedsRearm(Actor actor)
		{
			var ammoPools = RelevantAmmoPools(actor);
			if (ammoPools.Length == 0)
				return false;

			return !HasAmmo(ammoPools);
		}

		protected static bool HasCombatAmmo(Actor actor)
		{
			var ammoPools = RelevantAmmoPools(actor);
			return ammoPools.Length == 0 || HasAmmo(ammoPools);
		}

		protected static bool AnyAircraftHasCombatAmmo(CNSquad squad)
		{
			return squad.OrderableUnits.Any(u =>
				u.Info.HasTraitInfo<AircraftInfo>() &&
				!u.IsDead &&
				u.IsInWorld &&
				HasCombatAmmo(u));
		}

		protected static int TotalCombatAmmo(CNSquad squad)
		{
			var total = 0;
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>() || unit.IsDead || !unit.IsInWorld)
					continue;

				var ammoPools = RelevantAmmoPools(unit);
				if (ammoPools.Length == 0)
					return int.MaxValue;

				foreach (var pool in ammoPools)
					total += pool.CurrentAmmoCount;
			}

			return total;
		}

		protected static bool HasFullCombatAmmo(Actor actor)
		{
			var ammoPools = RelevantAmmoPools(actor);
			return ammoPools.Length == 0 || FullAmmo(ammoPools);
		}

		static AmmoPool[] RelevantAmmoPools(Actor actor)
		{
			var rearmable = actor.TraitOrDefault<Rearmable>();
			if (rearmable?.RearmableAmmoPools != null && rearmable.RearmableAmmoPools.Length > 0)
				return rearmable.RearmableAmmoPools;

			return actor.TraitsImplementing<AmmoPool>().ToArray();
		}

		protected static Actor FindAircraftTarget(CNSquad squad)
		{
			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld && HasCombatAmmo(u));

			if (leadAircraft == null)
				return FindClosestEnemyBuilding(squad) ?? FindClosestEnemyUnit(squad);

			Actor bestTarget = null;
			var bestScore = int.MaxValue;

			// Cached per-tick (buildings + mobile/aircraft units) so several squads searching in the same
			// tick share one filtered enemy list instead of each re-scanning World.Actors independently.
			var candidates = squad.SquadManager.GetCachedEnemyBuildings().Concat(squad.SquadManager.GetCachedEnemyUnits());
			foreach (var actor in candidates)
			{
				if (!leadAircraft.Info.HasTraitInfo<AttackBaseInfo>() || !CanAttackTarget(leadAircraft, actor))
					continue;

				var score = ScoreAircraftTarget(squad, leadAircraft, actor);
				if (score >= bestScore)
					continue;

				bestScore = score;
				bestTarget = actor;
			}

			return bestTarget;
		}

		protected static Actor FindAircraftRaidTarget(CNSquad squad)
		{
			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld && HasCombatAmmo(u));
			if (leadAircraft == null)
				return null;

			Actor bestTarget = null;
			var bestPriority = int.MaxValue;
			var bestScore = int.MaxValue;
			var preferredCaps = squad.PreferredTargetCapabilities;

			if (preferredCaps == null || preferredCaps.Length == 0)
				return FindAircraftTarget(squad);

			var candidates = squad.SquadManager.GetCachedEnemyBuildings().Concat(squad.SquadManager.GetCachedEnemyUnits());
			foreach (var actor in candidates)
			{
				if (!CanAttackTarget(leadAircraft, actor))
					continue;

				var caps = actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
				if (caps == null)
					continue;

				var priority = int.MaxValue;
				for (var i = 0; i < preferredCaps.Length; i++)
				{
					if (!caps.Contains(preferredCaps[i]))
						continue;
					priority = i;
					break;
				}

				if (priority == int.MaxValue)
					continue;

				var score = ScoreAircraftTarget(squad, leadAircraft, actor);
				if (priority > bestPriority || (priority == bestPriority && score >= bestScore))
					continue;

				bestPriority = priority;
				bestScore = score;
				bestTarget = actor;
			}

			return bestTarget ?? FindAircraftTarget(squad);
		}

		protected static int ScoreAircraftTarget(CNSquad squad, Actor aircraft, Actor target)
		{
			var score = 0;
			score += (int)((aircraft.CenterPosition - target.CenterPosition).LengthSquared / 65536);

			if (target.Info.HasTraitInfo<BuildingInfo>())
				score -= 120;

			score += ScoreTemplateTargetPreference(squad, target);
			score += ScoreAircraftThreatAtTarget(squad, aircraft, target);
			return score;
		}

		static int ScoreTemplateTargetPreference(CNSquad squad, Actor target)
		{
			var caps = target.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (caps == null || squad.TemplateInfo == null)
				return 0;

			for (var i = 0; i < squad.TemplateInfo.PriorityTargetCapabilities.Length; i++)
				if (caps.Contains(squad.TemplateInfo.PriorityTargetCapabilities[i]))
					return -(squad.TemplateInfo.PriorityTargetCapabilities.Length - i) * 1000;

			return 0;
		}

		protected static int ScoreAircraftThreatAtTarget(CNSquad squad, Actor aircraft, Actor target)
		{
			var score = 0;
			foreach (var threat in squad.World.FindActorsInCircle(target.CenterPosition, WDist.FromCells(AircraftThreatScanCells)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(threat))
					continue;

				if (!threat.Info.HasTraitInfo<AttackBaseInfo>() || !CanAttackTarget(threat, aircraft))
					continue;

				var isBuilding = threat.Info.HasTraitInfo<BuildingInfo>();
				score += isBuilding ? 350 : 110;
			}

			return score;
		}

		static int ScanThreatAtPosition(CNSquad squad, Actor aircraft, WPos pos)
		{
			var score = 0;
			foreach (var threat in squad.World.FindActorsInCircle(pos, WDist.FromCells(AircraftThreatScanCells)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(threat))
					continue;
				if (!threat.Info.HasTraitInfo<AttackBaseInfo>() || !CanAttackTarget(threat, aircraft))
					continue;
				score += threat.Info.HasTraitInfo<BuildingInfo>() ? 350 : 110;
			}

			return score;
		}

		// Samples cells in a ring around the target and returns the cell with the
		// lowest AA threat — the "gap" in enemy air cover the aircraft should approach through.
		protected static CPos? FindLowThreatApproachCell(CNSquad squad, Actor leadAircraft, Actor target)
		{
			var map = squad.World.Map;
			var targetCell = map.CellContaining(target.CenterPosition);

			CPos? bestCell = null;
			var bestScore = int.MaxValue;

			foreach (var cell in map.FindTilesInAnnulus(targetCell, ApproachAnnulusMin, ApproachAnnulusMax))
			{
				if (!map.Contains(cell))
					continue;

				var score = ScanThreatAtPosition(squad, leadAircraft, map.CenterOfCell(cell));
				if (score >= bestScore)
					continue;

				bestScore = score;
				bestCell = cell;
			}

			return bestScore <= MaxAcceptableAircraftThreatScore ? bestCell : null;
		}

		protected static void QueueReturnToBase(CNSquad squad)
		{
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;

				if (IsRearming(unit))
					continue;

				if (!NeedsRearm(unit) && !HasFullCombatAmmo(unit))
					squad.Bot.QueueOrder(new Order("ReturnToBase", unit, false));
			}

			Retreat(squad, flee: false, rearm: true, repair: true);
		}

		protected static bool AllAircraftReady(CNSquad squad)
		{
			var foundAircraft = false;

			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;

				foundAircraft = true;
				if (IsRearming(unit) || !HasFullCombatAmmo(unit))
					return false;
			}

			return foundAircraft;
		}

		protected static bool QueueAircraftMoveOrRearm(CNSquad squad, CPos? stagingCell, Actor followCenter)
		{
			var issuedOrder = false;
			var stagingPos = stagingCell.HasValue ? squad.World.Map.CenterOfCell(stagingCell.Value) : WPos.Zero;
			var stagingRange = WDist.FromCells(AircraftStagingRadiusCells).Length;

			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;

				if (IsRearming(unit))
					continue;

				if (NeedsRearm(unit))
				{
					squad.Bot.QueueOrder(new Order("ReturnToBase", unit, false));
					issuedOrder = true;
					continue;
				}

				if (!unit.IsIdle)
					continue;

				if (followCenter != null)
				{
					squad.Bot.QueueOrder(new Order("Move", unit, Target.FromActor(followCenter), false));
					issuedOrder = true;
					continue;
				}

				if (!stagingCell.HasValue ||
					(unit.CenterPosition - stagingPos).Length <= stagingRange)
					continue;

				squad.Bot.QueueOrder(new Order("Move", unit, Target.FromCell(squad.World, stagingCell.Value), false));
				issuedOrder = true;
			}

			return issuedOrder;
		}
	}

	sealed class AircraftAttackIdleState : AircraftStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;
			if (!squad.IsOperational || !HasCombatAircraft(squad))
			{
				QueueAircraftMoveOrRearm(squad, squad.SquadManager.GetRandomBaseCenter(), null);
				return;
			}

			var target = FindAircraftTarget(squad);
			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftAttackRunState());
				return;
			}

			// No enemy visible — return idle aircraft to base rather than sitting
			// at wherever they last landed.
			var baseCell = squad.SquadManager.GetRandomBaseCenter();
			QueueAircraftMoveOrRearm(squad, baseCell, null);
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftAttackRunState : AircraftStateBase, ICNState
	{
		bool approachIssued;

		public void Activate(CNSquad squad) { approachIssued = false; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);

			if (!squad.IsTargetValid)
			{
				var target = FindAircraftTarget(squad);
				if (target == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftAttackIdleState()));
					return;
				}

				squad.SetActorToTarget(target);
				approachIssued = false;
			}
			else if (leadAircraft != null &&
				ScoreAircraftThreatAtTarget(squad, leadAircraft, squad.TargetActor) > MaxAcceptableAircraftThreatScore)
			{
				// While threat is high, hold off attack orders until approach move completes.
				if (!approachIssued)
				{
					var saferTarget = FindAircraftTarget(squad);
					if (saferTarget != null && saferTarget != squad.TargetActor)
					{
						// Found a less-defended target — switch and fall through to attack.
						squad.SetActorToTarget(saferTarget);
						approachIssued = false;
						goto issueAttack;
					}

					// Only available target is heavily defended — look for a gap in AA coverage
					// and route through it before committing the attack run.
					var approachCell = FindLowThreatApproachCell(squad, leadAircraft, squad.TargetActor);
					if (approachCell.HasValue)
					{
						foreach (var unit in squad.OrderableUnits)
						{
							if (!unit.Info.HasTraitInfo<AircraftInfo>() || NeedsRearm(unit) || !unit.IsIdle)
								continue;
							squad.Bot.QueueOrder(new Order("Move", unit,
								Target.FromCell(squad.World, approachCell.Value), false));
						}

						approachIssued = true;
					}
					else
					{
						squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftAttackIdleState()));
						return;
					}
				}

				return;
			}

			approachIssued = false;
		issueAttack:

			var issuedAttack = false;
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;
				if (NeedsRearm(unit))
					continue;
				if (BusyAttack(unit))
				{
					issuedAttack = true;
					continue;
				}

				if (!CanAttackTarget(unit, squad.TargetActor))
					continue;

				squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
				issuedAttack = true;
			}

			if (!issuedAttack)
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftAttackIdleState()));
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftRaiderIdleState : AircraftStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;
			if (!squad.IsOperational || !HasCombatAircraft(squad))
			{
				QueueAircraftMoveOrRearm(squad, squad.SquadManager.GetRandomBaseCenter(), null);
				return;
			}

			var target = FindAircraftRaidTarget(squad);
			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftRaiderRunState());
				return;
			}

			var baseCell = squad.SquadManager.GetRandomBaseCenter();
			QueueAircraftMoveOrRearm(squad, baseCell, null);
		}

		public void Deactivate(CNSquad squad) { }
	}

	// Hit-and-run: attack the chosen target, then immediately return to base to rearm.
	// Never lingers — each run ends as soon as the target dies or ammo drops.
	sealed class AircraftRaiderRunState : AircraftStateBase, ICNState
	{
		const int MaxStuckTicks = 150;
		const int MinPositionChangeForMovement = 128; // sub-pixels (~2 cells)
		const int MaxNoAmmoSpentTicks = 75;

		// Counts ticks where the lead aircraft hasn't moved, regardless of attack status.
		// This fires even when BusyAttack is true, catching the case where an attack
		// activity is active but the aircraft is truly hovering in place.
		int noMoveTicks;
		int noAmmoSpentTicks;
		int lastAmmoCount;
		bool approachIssued;
		WPos lastPosition;

		public void Activate(CNSquad squad)
		{
			noMoveTicks = 0;
			noAmmoSpentTicks = 0;
			approachIssued = false;
			lastAmmoCount = TotalCombatAmmo(squad);
			var lead = squad.OrderableUnits.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);
			lastPosition = lead != null ? lead.CenterPosition : WPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);
			if (leadAircraft == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftRaiderIdleState()));
				return;
			}

			// Position-based stuck check: always track movement regardless of attack status.
			// Resets on any meaningful movement; aborts the run if stuck too long.
			var moved = (leadAircraft.CenterPosition - lastPosition).LengthSquared >
				MinPositionChangeForMovement * MinPositionChangeForMovement;
			lastPosition = leadAircraft.CenterPosition;
			if (moved)
				noMoveTicks = 0;
			else if (++noMoveTicks >= MaxStuckTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftRaiderIdleState()));
				return;
			}

			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>() || unit.IsDead || !unit.IsInWorld)
					continue;

				if (NeedsRearm(unit) && !IsRearming(unit))
					squad.Bot.QueueOrder(new Order("ReturnToBase", unit, false));
			}

			if (!AnyAircraftHasCombatAmmo(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftRaiderIdleState()));
				return;
			}

			if (!squad.IsTargetValid)
			{
				var target = FindAircraftRaidTarget(squad);
				if (target == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftRaiderIdleState()));
					return;
				}

				squad.SetActorToTarget(target);
				approachIssued = false;
			}

			// Approach routing through the lowest-threat gap in AA coverage.
			// While threat is high, hold off attack orders: issue the approach move once and wait.
			var threatAtTarget = ScoreAircraftThreatAtTarget(squad, leadAircraft, squad.TargetActor);
			if (threatAtTarget > MaxAcceptableAircraftThreatScore)
			{
				if (!approachIssued)
				{
					var approachCell = FindLowThreatApproachCell(squad, leadAircraft, squad.TargetActor);
					if (approachCell.HasValue)
					{
						foreach (var unit in squad.OrderableUnits)
						{
							if (!unit.Info.HasTraitInfo<AircraftInfo>() || NeedsRearm(unit) || !unit.IsIdle)
								continue;
							squad.Bot.QueueOrder(new Order("Move", unit,
								Target.FromCell(squad.World, approachCell.Value), false));
						}

						approachIssued = true;
					}
					else
					{
						var saferTarget = FindAircraftRaidTarget(squad);
						if (saferTarget != null && saferTarget != squad.TargetActor)
						{
							squad.SetActorToTarget(saferTarget);
							approachIssued = false;
							return;
						}

						return;
					}
				}

				// noMoveTicks handles the overall timeout; nothing extra needed here.
				return;
			}

			approachIssued = false;

			var ammoCount = TotalCombatAmmo(squad);
			if (ammoCount < lastAmmoCount)
			{
				noAmmoSpentTicks = 0;
				lastAmmoCount = ammoCount;
			}
			else
				noAmmoSpentTicks++;

			var forceReissueAttack = noAmmoSpentTicks >= MaxNoAmmoSpentTicks;
			var issuedAttack = false;
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;
				if (NeedsRearm(unit))
					continue;
				if (BusyAttack(unit) && !forceReissueAttack)
				{
					issuedAttack = true;
					continue;
				}

				if (!CanAttackTarget(unit, squad.TargetActor))
					continue;

				squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
				issuedAttack = true;
				noMoveTicks = 0;
			}

			if (forceReissueAttack && issuedAttack)
				noAmmoSpentTicks = 0;

			// No unit could attack this target at all (e.g. ammo-paused weapons or wrong target type) —
			// try another raid target before giving up the sortie.
			if (!issuedAttack)
			{
				var nextTarget = FindAircraftRaidTarget(squad);
				if (nextTarget != null && nextTarget != squad.TargetActor)
				{
					squad.SetActorToTarget(nextTarget);
					noAmmoSpentTicks = 0;
					approachIssued = false;
					return;
				}

				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftRaiderIdleState()));
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftReturnState : AircraftStateBase, ICNState
	{
		const int MaxReturnWaitTicks = 300;

		// Re-issue the return/rearm orders periodically rather than every tick.
		// Spamming them each tick re-queued ReturnToBase/Retreat and could
		// interrupt the resupply cycle so AllAircraftReady never became true
		// until the timeout.
		const int ReissueInterval = 25;
		readonly ICNState nextState;
		int waitTicks;

		public AircraftReturnState(ICNState nextState)
		{
			this.nextState = nextState;
		}

		public void Activate(CNSquad squad)
		{
			waitTicks = 0;
			QueueReturnToBase(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			waitTicks++;

			if (waitTicks % ReissueInterval == 0)
				QueueReturnToBase(squad);

			if (AllAircraftReady(squad) || waitTicks > MaxReturnWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, nextState);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
