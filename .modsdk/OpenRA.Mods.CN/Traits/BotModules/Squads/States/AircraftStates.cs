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
	abstract class AircraftStateBase : CNStateBase
	{
		protected const int AircraftThreatScanCells = 8;
		protected const int MaxAcceptableAircraftThreatScore = 800;
		protected const int SupportLeadCells = 5;
		protected const int SupportStagingRadiusCells = 4;
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
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);

			if (leadAircraft == null)
				return FindClosestEnemyBuilding(squad) ?? FindClosestEnemyUnit(squad);

			Actor bestTarget = null;
			var bestScore = int.MaxValue;

			foreach (var actor in squad.World.Actors)
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor) || !actor.CanBeViewedByPlayer(squad.Bot.Player))
					continue;

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

		// Scans near all operational friendly ground squads for the best attack target.
		// Falls back to any visible enemy if no ground squad is engaged.
		protected static Actor FindSupportHitTarget(CNSquad squad)
		{
			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);
			if (leadAircraft == null)
				return null;

			var seen = new HashSet<Actor>();
			Actor bestTarget = null;
			var bestScore = int.MaxValue;

			foreach (var s in squad.SquadManager.Squads)
			{
				if (s == squad || !s.IsOperational)
					continue;

				var type = s.Type;
				if (type == CNSquadType.Transport || type == CNSquadType.AirTransport ||
					type == CNSquadType.SubterraneanTransport || type == CNSquadType.Support ||
					type == CNSquadType.Air || type == CNSquadType.AircraftAttack ||
					type == CNSquadType.AircraftSupport)
					continue;

				var anchor = s.CenterUnit();
				if (anchor == null)
					continue;

				foreach (var actor in squad.World.FindActorsInCircle(anchor.CenterPosition, WDist.FromCells(14)))
				{
					if (!seen.Add(actor))
						continue;

					if (!squad.SquadManager.IsLiveEnemyActor(actor) || !actor.CanBeViewedByPlayer(squad.Bot.Player))
						continue;

					if (!CanAttackTarget(leadAircraft, actor))
						continue;

					var score = ScoreAircraftTarget(squad, leadAircraft, actor);
					if (score >= bestScore)
						continue;

					bestScore = score;
					bestTarget = actor;
				}
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

			var score = 0;
			foreach (var (tag, value) in squad.TemplateInfo.ThreatBonuses)
				if (caps.Contains(tag))
					score -= value * 20;

			return score;
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

		protected static bool QueueSupportMoveOrRearm(CNSquad squad, CPos? stagingCell, Actor followCenter)
		{
			var issuedOrder = false;
			var stagingPos = stagingCell.HasValue ? squad.World.Map.CenterOfCell(stagingCell.Value) : WPos.Zero;
			var stagingRange = WDist.FromCells(SupportStagingRadiusCells).Length;

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
				QueueSupportMoveOrRearm(squad, squad.SquadManager.GetRandomBaseCenter(), null);
				return;
			}

			var leader = squad.CenterUnit();
			if (leader == null)
				return;

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
			QueueSupportMoveOrRearm(squad, baseCell, null);
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

	sealed class AircraftSupportIdleState : AircraftStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;
			if (!squad.IsOperational || !HasCombatAircraft(squad))
			{
				QueueSupportMoveOrRearm(squad, squad.SquadManager.GetRandomBaseCenter(), null);
				return;
			}

			var target = FindSupportHitTarget(squad);
			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftSupportRunState());
				return;
			}

			var baseCell = squad.SquadManager.GetRandomBaseCenter();
			QueueSupportMoveOrRearm(squad, baseCell, null);
		}

		public void Deactivate(CNSquad squad) { }
	}

	// Hit-and-run: attack the chosen target, then immediately return to base to rearm.
	// Never lingers — each run ends as soon as the target dies or ammo drops.
	sealed class AircraftSupportRunState : AircraftStateBase, ICNState
	{
		const int MaxStuckTicks = 150;

		int stuckTicks;
		bool approachIssued;

		public void Activate(CNSquad squad)
		{
			stuckTicks = 0;
			approachIssued = false;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);
			if (leadAircraft == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftSupportIdleState()));
				return;
			}

			// Any aircraft spent ammo → return all to rearm together.
			if (squad.OrderableUnits.Any(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld && NeedsRearm(u)))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftSupportIdleState()));
				return;
			}

			// Target gone → return immediately.
			if (!squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftSupportIdleState()));
				return;
			}

			// Approach routing through the lowest-threat gap in AA coverage.
			// While threat is high, hold off attack orders: issue the approach move once and wait
			// (don't clear approachIssued the very next tick or we oscillate Move↔Attack every tick).
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
						// No safe gap — abort this run.
						squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftSupportIdleState()));
						return;
					}
				}

				// Still in approach phase — count toward stuck timeout so a permanently
				// fortified target doesn't trap the squad here forever.
				if (++stuckTicks >= MaxStuckTicks)
					squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftSupportIdleState()));
				return;
			}

			approachIssued = false;

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
					stuckTicks = 0;
					continue;
				}

				if (!CanAttackTarget(unit, squad.TargetActor))
					continue;

				squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
				issuedAttack = true;
				stuckTicks = 0;
			}

			if (!issuedAttack && ++stuckTicks >= MaxStuckTicks)
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftSupportIdleState()));
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftReturnState : AircraftStateBase, ICNState
	{
		readonly ICNState nextState;
		int waitTicks;
		const int MaxReturnWaitTicks = 600;

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

			QueueReturnToBase(squad);
			waitTicks++;

			if (AllAircraftReady(squad) || waitTicks > MaxReturnWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, nextState);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
