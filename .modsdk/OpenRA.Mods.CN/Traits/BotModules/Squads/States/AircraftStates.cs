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

		// Percent of the flight that must be armed and not rearming before a sortie starts.
		const int SortieMinArmedPercent = 50;

		// How close to a launched wave's target a candidate has to be to count as supporting it,
		// and how much that is worth. Deliberately below the 1000-per-rank the template's own
		// PriorityTargetCapabilities are worth: the wave steers the flight within its role, it
		// does not override what the template was built to hunt.
		const int WaveSupportRadiusCells = 12;
		const int WaveSupportBonus = 500;

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
			score += ScoreWaveSupport(squad, target);
			score += ScoreAircraftThreatAtTarget(squad, aircraft, target);

			// Prefer targets the squad's actual composition can hit well (e.g. a mixed squad with
			// only some anti-air-capable units shouldn't keep picking a target only half the squad
			// can actually engage).
			score -= (int)(CNSquadHelper.SquadEngageFraction(squad, target) * 150);

			var counter = CNSquadHelper.CounterFraction(squad, target);
			score -= (int)(counter * 150);

			// The claim penalties matter most here of anywhere: flights are the squads most prone to
			// piling onto one building, because they reach it fastest and all see the same thing.
			// Ten aircraft kept bombing what three of them had already killed.
			if (squad.SquadManager.IsTargetOversubscribed(squad, target))
				score += 6400;

			if (squad.SquadManager.IsTargetBetterServed(squad, target, counter))
				score += 3200;

			return score;
		}

		/// <summary>
		/// Pulls the flight toward whatever a launched ground wave is currently hitting, without ever
		/// making it wait for one. Aircraft used to be wave participants themselves, which parked them
		/// in the base between waves and then had them hover at the ground rally point inside enemy AA
		/// while the tanks caught up. They now fly their own sortie cycle and simply prefer targets near
		/// the wave when there is a wave; with none active this contributes nothing.
		/// </summary>
		static int ScoreWaveSupport(CNSquad squad, Actor target)
		{
			var manager = squad.SquadManager;
			if (!manager.IsWaveLaunched)
				return 0;

			var waveTarget = manager.WaveTarget;
			if (waveTarget == null || waveTarget.IsDead || !waveTarget.IsInWorld)
				return 0;

			var radius = WDist.FromCells(WaveSupportRadiusCells).Length;
			if ((target.CenterPosition - waveTarget.CenterPosition).LengthSquared > (long)radius * radius)
				return 0;

			return -WaveSupportBonus;
		}

		/// <summary>
		/// True once enough of the flight is armed and out of the rearm cycle to be worth sending.
		/// Target selection only needs one aircraft with ammo, so without this gate a squad launched
		/// again the moment its first aircraft finished rearming and trickled into intact AA one at a
		/// time instead of striking together.
		/// </summary>
		protected static bool EnoughAircraftArmedForSortie(CNSquad squad)
		{
			var total = 0;
			var armed = 0;

			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>() || unit.IsDead || !unit.IsInWorld)
					continue;

				total++;
				if (!IsRearming(unit) && HasCombatAmmo(unit))
					armed++;
			}

			return total > 0 && armed * 100 >= total * SortieMinArmedPercent;
		}

		/// <summary>
		/// A stable place for an idle flight to sit, preferring somewhere it can actually rearm.
		/// GetRandomBaseCenter picks a fresh building on every call, so using it directly had idle
		/// aircraft drifting from one corner of the base to the next instead of holding station.
		/// </summary>
		protected static CPos LoiterCell(CNSquad squad)
		{
			var buildings = squad.SquadManager.GetCachedOwnBuildings();
			if (buildings.Count == 0)
				return squad.SquadManager.GetRandomBaseCenter();

			var candidates = buildings;
			foreach (var unit in squad.OrderableUnits)
			{
				var rearmActors = unit.Info.TraitInfoOrDefault<RearmableInfo>()?.RearmActors;
				if (rearmActors == null || rearmActors.Count == 0)
					continue;

				var pads = buildings.Where(b => rearmActors.Contains(b.Info.Name)).ToList();
				if (pads.Count > 0)
					candidates = pads;

				break;
			}

			// Same stable per-squad seed the wave hold uses, so two flights of the same template
			// don't stack on one pad.
			var seed = (CNSquadHelper.StableHash(squad.TemplateName) ^ squad.CreatedTick) & int.MaxValue;
			return candidates[seed % candidates.Count].Location;
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
			if (!squad.IsOperational || !HasCombatAircraft(squad) || !EnoughAircraftArmedForSortie(squad))
			{
				QueueAircraftMoveOrRearm(squad, LoiterCell(squad), null);
				return;
			}

			var target = FindAircraftTarget(squad);
			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftAttackRunState());
				return;
			}

			// No enemy visible — hold station at the loiter point rather than sitting
			// at wherever they last landed.
			QueueAircraftMoveOrRearm(squad, LoiterCell(squad), null);
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftAttackRunState : AircraftStateBase, ICNState
	{
		// How long the run-in manoeuvre gets before the attack goes ahead anyway. The approach is flown
		// to pick the way in, not to make the target safer, so this is a flight time and not a condition.
		const int ApproachHoldTicks = 100;

		bool approachIssued;
		CPos approachCell;
		int approachIssuedTick;
		Actor orderedTarget;

		public void Activate(CNSquad squad)
		{
			approachIssued = false;
			approachIssuedTick = 0;
			orderedTarget = null;
		}

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
				// While threat is high, hold off attack orders until the approach move completes.
				if (approachIssued)
				{
					// And then attack regardless of the score. The score is measured around the TARGET, not
					// around us - flying the approach cannot lower it by so much as a point - so a state
					// that waits for it to fall waits forever. This branch used to do exactly that: order
					// the approach, set the flag, and from then on return every tick with the squadron
					// parked in mid-air until its target happened to die. The manoeuvre was only ever meant
					// to choose the way in, which is what the comment above says; once it is flown, or has
					// had long enough to be flown, the run goes ahead.
					var arrivedAt = squad.World.Map.CenterOfCell(approachCell);
					var arrived = (squad.CenterPosition() - arrivedAt).HorizontalLengthSquared
						<= (long)WDist.FromCells(AircraftStagingRadiusCells).Length * WDist.FromCells(AircraftStagingRadiusCells).Length;

					if (!arrived && squad.World.WorldTick - approachIssuedTick < ApproachHoldTicks)
						return;

					approachIssued = false;
					goto issueAttack;
				}

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
					var approachCell2 = FindLowThreatApproachCell(squad, leadAircraft, squad.TargetActor);
					if (approachCell2.HasValue)
					{
						foreach (var unit in squad.OrderableUnits)
						{
							if (!unit.Info.HasTraitInfo<AircraftInfo>() || NeedsRearm(unit) || !unit.IsIdle)
								continue;
							squad.Bot.QueueOrder(new Order("Move", unit,
								Target.FromCell(squad.World, approachCell2.Value), false));
						}

						approachCell = approachCell2.Value;
						approachIssuedTick = squad.World.WorldTick;
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

			var targetChanged = orderedTarget != squad.TargetActor;
			var issuedAttack = false;
			var issuedNewAttack = false;
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;
				if (NeedsRearm(unit))
					continue;

				// An Attack activity aimed at the previous actor is not useful work. Waiting for it to
				// unwind left an entire flight hovering over the first wreck before it engaged target two.
				if (BusyAttack(unit) && !targetChanged)
				{
					issuedAttack = true;
					continue;
				}

				if (!CanAttackTarget(unit, squad.TargetActor))
					continue;

				squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
				issuedAttack = true;
				issuedNewAttack = true;
			}

			if (issuedNewAttack)
				orderedTarget = squad.TargetActor;

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
			if (!squad.IsOperational || !HasCombatAircraft(squad) || !EnoughAircraftArmedForSortie(squad))
			{
				QueueAircraftMoveOrRearm(squad, LoiterCell(squad), null);
				return;
			}

			var target = FindAircraftRaidTarget(squad);
			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftRaiderRunState());
				return;
			}

			QueueAircraftMoveOrRearm(squad, LoiterCell(squad), null);
		}

		public void Deactivate(CNSquad squad) { }
	}

	// Hit-and-run: attack the chosen target, then immediately return to base to rearm.
	// Never lingers — each run ends as soon as the target dies or ammo drops.
	sealed class AircraftRaiderRunState : AircraftStateBase, ICNState
	{
		// Game ticks, not update cycles. Both of these used to be incremented once per update, which
		// made their real length depend on AttackForceInterval — and left the stuck detector so long
		// (150 cycles is over seven minutes) that it never fired at all.
		const int MaxStuckTicks = 375;
		const int MinPositionChangeForMovement = 128; // sub-pixels (~2 cells)
		const int MaxNoAmmoSpentTicks = 500;
		const int ApproachHoldTicks = 100;

		// Time since the lead aircraft last moved, regardless of attack status. This fires even when
		// BusyAttack is true, catching the case where an attack activity is active but the aircraft
		// is truly hovering in place. lastPosition only advances on real movement, so the threshold
		// measures distance covered over time rather than distance covered per update.
		int noMoveTicks;
		int noAmmoSpentTicks;
		int lastAmmoCount;
		bool approachIssued;
		CPos approachCell;
		int approachIssuedTick;
		Actor orderedTarget;
		WPos lastPosition;

		public void Activate(CNSquad squad)
		{
			noMoveTicks = 0;
			noAmmoSpentTicks = 0;
			approachIssued = false;
			approachIssuedTick = 0;
			orderedTarget = null;
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
			if (moved)
			{
				lastPosition = leadAircraft.CenterPosition;
				noMoveTicks = 0;
			}
			else if ((noMoveTicks += squad.TicksSinceLastUpdate) >= MaxStuckTicks)
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

			var targetChanged = orderedTarget != squad.TargetActor;

			// Approach routing through the lowest-threat gap in AA coverage.
			// While threat is high, hold off attack orders only until the approach has actually been
			// flown. The score is measured around the target and cannot fall merely because we moved.
			var threatAtTarget = ScoreAircraftThreatAtTarget(squad, leadAircraft, squad.TargetActor);
			if (threatAtTarget > MaxAcceptableAircraftThreatScore)
			{
				if (approachIssued)
				{
					var arrivedAt = squad.World.Map.CenterOfCell(approachCell);
					var stagingRange = WDist.FromCells(AircraftStagingRadiusCells).Length;
					var arrived = (squad.CenterPosition() - arrivedAt).HorizontalLengthSquared
						<= (long)stagingRange * stagingRange;

					if (!arrived && squad.World.WorldTick - approachIssuedTick < ApproachHoldTicks)
						return;

					approachIssued = false;
					goto issueAttack;
				}

				if (!approachIssued)
				{
					var saferApproachCell = FindLowThreatApproachCell(squad, leadAircraft, squad.TargetActor);
					if (saferApproachCell.HasValue)
					{
						var issuedApproach = false;
						foreach (var unit in squad.OrderableUnits)
						{
							if (!unit.Info.HasTraitInfo<AircraftInfo>() || NeedsRearm(unit) || IsRearming(unit) ||
								(!unit.IsIdle && !targetChanged))
								continue;
							squad.Bot.QueueOrder(new Order("Move", unit,
								Target.FromCell(squad.World, saferApproachCell.Value), false));
							issuedApproach = true;
						}

						if (issuedApproach)
						{
							approachCell = saferApproachCell.Value;
							approachIssuedTick = squad.World.WorldTick;
							approachIssued = true;
						}
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

				return;
			}

			approachIssued = false;

		issueAttack:
			var ammoCount = TotalCombatAmmo(squad);
			if (ammoCount < lastAmmoCount)
			{
				noAmmoSpentTicks = 0;
				lastAmmoCount = ammoCount;
			}
			else
				noAmmoSpentTicks += squad.TicksSinceLastUpdate;

			var forceReissueAttack = noAmmoSpentTicks >= MaxNoAmmoSpentTicks;
			var issuedAttack = false;
			var issuedNewAttack = false;
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;
				if (NeedsRearm(unit))
					continue;
				if (BusyAttack(unit) && !forceReissueAttack && !targetChanged)
				{
					issuedAttack = true;
					continue;
				}

				if (!CanAttackTarget(unit, squad.TargetActor))
					continue;

				squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
				issuedAttack = true;
				issuedNewAttack = true;
				noMoveTicks = 0;
			}

			if (issuedNewAttack)
				orderedTarget = squad.TargetActor;

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
		// Game ticks, not update cycles.
		const int MaxReturnWaitTicks = 1500;

		// Re-issue the return/rearm orders periodically rather than on every update.
		// Spamming them re-queued ReturnToBase/Retreat and could interrupt the
		// resupply cycle so AllAircraftReady never became true until the timeout.
		const int ReissueInterval = 250;
		readonly ICNState nextState;
		int waitTicks;
		int lastReissueTicks;

		public AircraftReturnState(ICNState nextState)
		{
			this.nextState = nextState;
		}

		public void Activate(CNSquad squad)
		{
			waitTicks = 0;
			lastReissueTicks = 0;
			QueueReturnToBase(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			waitTicks += squad.TicksSinceLastUpdate;

			// Elapsed-time comparison rather than a modulo on the counter: with a variable update
			// cadence the counter no longer lands on exact multiples of the interval.
			if (waitTicks - lastReissueTicks >= ReissueInterval)
			{
				lastReissueTicks = waitTicks;
				QueueReturnToBase(squad);
			}

			if (AllAircraftReady(squad) || waitTicks > MaxReturnWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, nextState);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
