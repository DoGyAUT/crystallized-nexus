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

			var fallbackBuilding = FindClosestEnemyBuilding(squad);
			if (fallbackBuilding != null)
				return fallbackBuilding;

			Actor bestTarget = null;
			var bestScore = int.MaxValue;

			foreach (var actor in squad.World.Actors)
			{
				if (actor.IsDead || !actor.IsInWorld || !actor.CanBeViewedByPlayer(squad.Bot.Player))
					continue;

				if (actor.Owner.RelationshipWith(squad.Bot.Player) != PlayerRelationship.Enemy)
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

		protected static CNSquad FindAircraftSupportAttachTarget(CNSquad squad)
		{
			return squad.SquadManager.Squads
				.Where(s => s != squad &&
				            s.IsOperational &&
				            s.Type != CNSquadType.Transport &&
				            s.Type != CNSquadType.SubterraneanTransport &&
				            s.Type != CNSquadType.Support &&
				            s.Type != CNSquadType.AircraftSupport)
				.OrderByDescending(s => s.TemplateInfo?.Priority ?? 0)
				.ThenByDescending(s => s.Units.Count)
				.FirstOrDefault();
		}

		protected static Actor FindSupportAircraftTarget(CNSquad squad, CNSquad attachedTo)
		{
			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);
			var anchor = attachedTo?.CenterUnit();
			if (leadAircraft == null || anchor == null)
				return null;

			Actor bestTarget = null;
			var bestScore = int.MaxValue;
			foreach (var actor in squad.World.FindActorsInCircle(anchor.CenterPosition, WDist.FromCells(14)))
			{
				if (actor.IsDead || !actor.IsInWorld || !actor.CanBeViewedByPlayer(squad.Bot.Player))
					continue;

				if (actor.Owner.RelationshipWith(squad.Bot.Player) != PlayerRelationship.Enemy)
					continue;

				if (!CanAttackTarget(leadAircraft, actor))
					continue;

				var score = ScoreAircraftTarget(squad, leadAircraft, actor);
				if (attachedTo.IsTargetValid && actor == attachedTo.TargetActor)
					score -= 250;

				if (score >= bestScore)
					continue;

				bestScore = score;
				bestTarget = actor;
			}

			return bestScore <= MaxAcceptableAircraftThreatScore ? bestTarget : null;
		}

		protected static CPos? FindSupportStagingCell(CNSquad squad, CNSquad attachedTo)
		{
			var anchor = attachedTo?.CenterUnit();
			if (anchor == null)
				return null;

			var map = squad.World.Map;
			var anchorCell = anchor.Location;
			var referenceCell = anchor.Location;
			if (attachedTo.IsTargetValid && attachedTo.TargetActor != null)
			{
				try
				{
					referenceCell = map.CellContaining(attachedTo.TargetActor.CenterPosition);
				}
				catch (InvalidOperationException)
				{
					referenceCell = anchor.Location;
				}
			}

			if (!attachedTo.IsTargetValid)
			{
				var nearbyEnemy = squad.SquadManager.FindClosestEnemy(anchor);
				if (nearbyEnemy != null)
					referenceCell = map.CellContaining(nearbyEnemy.CenterPosition);
			}

			var dx = referenceCell.X - anchorCell.X;
			var dy = referenceCell.Y - anchorCell.Y;
			var distance = System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy));
			if (distance == 0)
				return anchorCell;

			var forward = new CVec(
				dx * SupportLeadCells / distance,
				dy * SupportLeadCells / distance);

			var desiredCell = anchorCell + forward;
			if (map.Contains(desiredCell))
				return desiredCell;

			return anchorCell;
		}

		protected static int ScoreAircraftTarget(CNSquad squad, Actor aircraft, Actor target)
		{
			var score = 0;
			score += (int)((aircraft.CenterPosition - target.CenterPosition).LengthSquared / 65536);

			if (target.Info.HasTraitInfo<BuildingInfo>())
				score -= 120;

			score += ScoreAircraftThreatAtTarget(squad, aircraft, target);
			return score;
		}

		protected static int ScoreAircraftThreatAtTarget(CNSquad squad, Actor aircraft, Actor target)
		{
			var score = 0;
			foreach (var threat in squad.World.FindActorsInCircle(target.CenterPosition, WDist.FromCells(AircraftThreatScanCells)))
			{
				if (threat.IsDead || !threat.IsInWorld)
					continue;

				if (threat.Owner.RelationshipWith(squad.Bot.Player) != PlayerRelationship.Enemy)
					continue;

				if (!threat.Info.HasTraitInfo<AttackBaseInfo>() || !CanAttackTarget(threat, aircraft))
					continue;

				var isBuilding = threat.Info.HasTraitInfo<BuildingInfo>();
				score += isBuilding ? 350 : 110;
			}

			return score;
		}

		protected static void QueueReturnToBase(CNSquad squad)
		{
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.Info.HasTraitInfo<AircraftInfo>())
					continue;

				if (IsRearming(unit) || HasFullCombatAmmo(unit))
					continue;

				squad.Bot.QueueOrder(new Order("ReturnToBase", unit, false));
			}
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
	}

	sealed class AircraftAttackIdleState : AircraftStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational || !HasCombatAircraft(squad))
				return;

			var leader = squad.CenterUnit();
			if (leader == null)
				return;

			var target = FindAircraftTarget(squad);
			if (target == null)
				return;

			squad.SetActorToTarget(target);
			squad.FuzzyStateMachine.ChangeState(squad, new AircraftAttackRunState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftAttackRunState : AircraftStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!squad.IsTargetValid)
			{
				var target = FindAircraftTarget(squad);
				if (target == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftAttackIdleState()));
					return;
				}

				squad.SetActorToTarget(target);
			}
			else
			{
				var leadAircraft = squad.OrderableUnits
					.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);
				if (leadAircraft != null &&
					ScoreAircraftThreatAtTarget(squad, leadAircraft, squad.TargetActor) > MaxAcceptableAircraftThreatScore)
				{
					var saferTarget = FindAircraftTarget(squad);
					if (saferTarget == null || saferTarget == squad.TargetActor)
					{
						squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftAttackIdleState()));
						return;
					}

					squad.SetActorToTarget(saferTarget);
				}
			}

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
			if (!squad.IsOperational || !HasCombatAircraft(squad))
				return;

			if (squad.AttachedTo == null || !squad.AttachedTo.IsOperational)
				squad.AttachedTo = FindAircraftSupportAttachTarget(squad);

			if (squad.AttachedTo == null)
			{
				var target = FindAircraftTarget(squad);
				if (target == null)
					return;

				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftAttackRunState());
				return;
			}

			var stagingCell = FindSupportStagingCell(squad, squad.AttachedTo);
			if (stagingCell.HasValue)
			{
				var stagingPos = squad.World.Map.CenterOfCell(stagingCell.Value);
				foreach (var unit in squad.OrderableUnits)
				{
					if (!unit.Info.HasTraitInfo<AircraftInfo>() || NeedsRearm(unit) || !unit.IsIdle)
						continue;

					if ((unit.CenterPosition - stagingPos).Length > WDist.FromCells(SupportStagingRadiusCells).Length)
						squad.Bot.QueueOrder(new Order("Move", unit, Target.FromCell(squad.World, stagingCell.Value), false));
				}
			}

			squad.FuzzyStateMachine.ChangeState(squad, new AircraftSupportAttackState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftSupportAttackState : AircraftStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (squad.AttachedTo == null || !squad.AttachedTo.IsOperational)
			{
				squad.AttachedTo = null;
				squad.SetActorToTarget(null);
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftSupportIdleState());
				return;
			}

			Actor target = null;
			if (squad.AttachedTo.IsTargetValid)
				target = squad.AttachedTo.TargetActor;

			target ??= FindSupportAircraftTarget(squad, squad.AttachedTo);
			target ??= squad.SquadManager.FindClosestEnemy(squad.AttachedTo.CenterUnit());
			if (target == null)
			{
				var stagingCell = FindSupportStagingCell(squad, squad.AttachedTo);
				if (!stagingCell.HasValue)
					return;

				var stagingPos = squad.World.Map.CenterOfCell(stagingCell.Value);

				foreach (var unit in squad.OrderableUnits)
				{
					if (!unit.Info.HasTraitInfo<AircraftInfo>())
						continue;
					if (NeedsRearm(unit))
						continue;
					if (!unit.IsIdle)
						continue;

					if ((unit.CenterPosition - stagingPos).Length <= WDist.FromCells(SupportStagingRadiusCells).Length)
						continue;

					squad.Bot.QueueOrder(new Order("Move", unit, Target.FromCell(squad.World, stagingCell.Value), false));
				}

				return;
			}

			squad.SetActorToTarget(target);

			var leadAircraft = squad.OrderableUnits
				.FirstOrDefault(u => u.Info.HasTraitInfo<AircraftInfo>() && !u.IsDead && u.IsInWorld);
			if (leadAircraft != null &&
				ScoreAircraftThreatAtTarget(squad, leadAircraft, target) > MaxAcceptableAircraftThreatScore)
			{
				foreach (var unit in squad.OrderableUnits)
				{
					if (!unit.Info.HasTraitInfo<AircraftInfo>() || NeedsRearm(unit) || !unit.IsIdle)
						continue;

					var followCenter = squad.AttachedTo.CenterUnit();
					if (followCenter != null)
						squad.Bot.QueueOrder(new Order("Move", unit, Target.FromActor(followCenter), false));
				}

				return;
			}

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
				if (!CanAttackTarget(unit, target))
					continue;

				squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
				issuedAttack = true;
			}

			if (!issuedAttack)
				squad.FuzzyStateMachine.ChangeState(squad, new AircraftReturnState(new AircraftSupportIdleState()));
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class AircraftReturnState : AircraftStateBase, ICNState
	{
		readonly ICNState nextState;

		public AircraftReturnState(ICNState nextState)
		{
			this.nextState = nextState;
		}

		public void Activate(CNSquad squad)
		{
			QueueReturnToBase(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			QueueReturnToBase(squad);

			if (!AllAircraftReady(squad))
				return;

			squad.FuzzyStateMachine.ChangeState(squad, nextState);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
