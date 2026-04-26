using System;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	sealed class TransportIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			var hasCarriers = squad.CarrierUnits.Any(u => !u.IsDead);
			var hasPassengers = squad.PassengerUnits.Any(u => !u.IsDead);

			if (hasCarriers && hasPassengers)
				squad.FuzzyStateMachine.ChangeState(squad, new TransportLoadState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class TransportLoadState : CNStateBase, ICNState
	{
		int waitTicks;

		// 10 update-cycles × 75 game ticks = 750 ticks ≈ ~37 seconds at 20fps.
		const int MaxLoadTicks = 10;

		static void IssueLoadOrders(CNSquad squad)
		{
			var carrierList = squad.CarrierUnits.Where(u => !u.IsDead).ToList();
			if (carrierList.Count == 0)
				return;

			var passengerList = squad.PassengerUnits
				.Where(u => !u.IsDead && u.IsInWorld)
				.ToList();

			// Round-robin: distribute passengers evenly across all carriers so each
			// APC gets roughly the same number of boarders instead of all piling into
			// the nearest one and leaving the others empty.
			for (var i = 0; i < passengerList.Count; i++)
			{
				var carrier = carrierList[i % carrierList.Count];
				squad.Bot.QueueOrder(new Order("EnterTransport", passengerList[i],
					Target.FromActor(carrier), false));
			}
		}

		public void Activate(CNSquad squad)
		{
			waitTicks = 0;

			// Stop all carriers so they stay put while passengers walk to them.
			foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
				squad.Bot.QueueOrder(new Order("Stop", carrier, false));

			// Issue boarding orders immediately (don't wait up to 75 ticks for first Tick).
			IssueLoadOrders(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			waitTicks++;

			var passengers = squad.PassengerUnits.Where(u => !u.IsDead).ToList();

			if (passengers.Count == 0)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportIdleState());
				return;
			}

			var allLoaded = passengers.All(u => !u.IsInWorld);

			if (allLoaded || waitTicks >= MaxLoadTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportAttackMoveState());
				return;
			}

			// Re-issue only to idle passengers (those still walking are fine — don't
			// interrupt them with a new order that would restart the boarding walk).
			var carrierList = squad.CarrierUnits.Where(u => !u.IsDead).ToList();
			var idlePassengers = passengers.Where(u => u.IsInWorld && u.IsIdle).ToList();
			for (var i = 0; i < idlePassengers.Count; i++)
			{
				var carrier = carrierList[i % carrierList.Count];
				squad.Bot.QueueOrder(new Order("EnterTransport", idlePassengers[i],
					Target.FromActor(carrier), false));
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class TransportAttackMoveState : CNStateBase, ICNState
	{
		CPos? dropCell;
		Actor lastTarget;

		const int DropSearchMinCells = 4;
		const int DropSearchMaxCells = 10;
		const int DropArriveRangeCells = 3;

		public void Activate(CNSquad squad)
		{
			dropCell = null;
			lastTarget = null;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Find a drop-off target if we don't have one yet (squad starts without a target)
			if (!squad.IsTargetValid)
			{
				var carrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead);
				if (carrier == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
					return;
				}

				var dropTarget = squad.SquadManager.FindClosestEnemyBuilding(carrier);
				if (dropTarget == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
					return;
				}

				squad.SetActorToTarget(dropTarget);
			}

			var leadCarrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead);
			if (leadCarrier == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
				return;
			}

			if (squad.TargetActor != lastTarget || !dropCell.HasValue)
			{
				dropCell = FindSaferDropCell(squad, leadCarrier, squad.TargetActor);
				lastTarget = squad.TargetActor;
			}

			// Emergency unload: if any carrier is critically damaged, drop passengers now.
			var carriers = squad.CarrierUnits.Where(u => !u.IsDead).ToList();
			if (carriers.Any(c => c.TraitOrDefault<IHealth>()?.DamageState >= DamageState.Critical))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportUnloadState());
				return;
			}

			WPos targetPos;
			if (dropCell.HasValue)
				targetPos = squad.World.Map.CenterOfCell(dropCell.Value);
			else
			{
				try
				{
					if (squad.Target.Type == TargetType.Invalid)
					{
						squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
						return;
					}

					targetPos = squad.Target.CenterPosition;
				}
				catch (InvalidOperationException)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
					return;
				}
			}
			var unloadRange = WDist.FromCells(dropCell.HasValue ? DropArriveRangeCells : 12);

			foreach (var carrier in carriers)
			{
				if ((carrier.CenterPosition - targetPos).LengthSquared <= (long)unloadRange.Length * unloadRange.Length)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new TransportUnloadState());
					return;
				}

				if (carrier.IsIdle)
				{
					var moveTarget = dropCell.HasValue
						? Target.FromCell(squad.World, dropCell.Value)
						: squad.Target;

					// Use Move (not AttackMove) so the carrier ignores enemies and drives
					// directly to the drop-off point instead of stopping to fight.
					squad.Bot.QueueOrder(new Order("Move", carrier, moveTarget, false));
				}
			}
		}

		public void Deactivate(CNSquad squad) { }

		static CPos? FindSaferDropCell(CNSquad squad, Actor carrier, Actor target)
		{
			if (target == null)
				return null;

			var mobile = carrier.TraitOrDefault<Mobile>();
			if (mobile == null)
				return null;

			var world = squad.World;
			var map = world.Map;
			var targetCell = map.CellContaining(target.CenterPosition);
			var ourBaseCell = squad.SquadManager.GetRandomBaseCenter();

			CPos? bestCell = null;
			var bestScore = int.MaxValue;

			for (var dy = -DropSearchMaxCells; dy <= DropSearchMaxCells; dy++)
			{
				for (var dx = -DropSearchMaxCells; dx <= DropSearchMaxCells; dx++)
				{
					var distanceSquared = dx * dx + dy * dy;
					if (distanceSquared < DropSearchMinCells * DropSearchMinCells ||
						distanceSquared > DropSearchMaxCells * DropSearchMaxCells)
						continue;

					var cell = targetCell + new CVec(dx, dy);
					if (!map.Contains(cell) || !mobile.CanEnterCell(cell))
						continue;

					var score = ScoreDropCell(squad, targetCell, ourBaseCell, cell);
					if (score >= bestScore)
						continue;

					bestScore = score;
					bestCell = cell;
				}
			}

			return bestCell;
		}

		static int ScoreDropCell(CNSquad squad, CPos targetCell, CPos ourBaseCell, CPos candidate)
		{
			var world = squad.World;
			var candidatePos = world.Map.CenterOfCell(candidate);
			var targetPos = world.Map.CenterOfCell(targetCell);
			var score = 0;

			score += (candidate - targetCell).LengthSquared * 12;

			var baseToCandidate = (candidate - ourBaseCell).LengthSquared;
			var baseToTarget = (targetCell - ourBaseCell).LengthSquared;
			if (baseToCandidate < baseToTarget)
				score += (baseToTarget - baseToCandidate) * 6;

			foreach (var actor in world.FindActorsInCircle(candidatePos, WDist.FromCells(6)))
			{
				if (actor.IsDead || !actor.IsInWorld ||
					actor.Owner.RelationshipWith(squad.Bot.Player) != PlayerRelationship.Enemy)
					continue;

				var isBuilding = actor.Info.HasTraitInfo<BuildingInfo>();
				var canAttack = actor.Info.HasTraitInfo<AttackBaseInfo>();

				if (isBuilding && canAttack)
					score += 350;
				else if (canAttack)
					score += 80;
				else if (isBuilding)
					score -= 10;
			}

			foreach (var actor in world.FindActorsInCircle(targetPos, WDist.FromCells(4)))
			{
				if (actor.IsDead || !actor.IsInWorld ||
					actor.Owner.RelationshipWith(squad.Bot.Player) != PlayerRelationship.Enemy)
					continue;

				if (!actor.Info.HasTraitInfo<BuildingInfo>())
					continue;

				if ((actor.CenterPosition - candidatePos).LengthSquared <=
					(long)WDist.FromCells(6).Length * WDist.FromCells(6).Length)
					score += 80;
			}

			return score;
		}
	}

	sealed class TransportUnloadState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var carriers = squad.CarrierUnits.Where(u => !u.IsDead).ToList();
			var anyCargo = carriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false);

			if (!anyCargo)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
				return;
			}

			foreach (var carrier in carriers)
			{
				if (carrier.IsIdle && carrier.TraitOrDefault<Cargo>()?.IsEmpty() == false)
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class TransportReturnState : CNStateBase, ICNState
	{
		bool returnIssued;

		public void Activate(CNSquad squad) { returnIssued = false; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			if (!returnIssued)
			{
				GoToRandomOwnBuilding(squad);
				returnIssued = true;
			}

			var baseCell = squad.SquadManager.GetRandomBaseCenter();
			var basePos = squad.World.Map.CenterOfCell(baseCell);
			var homeRange = WDist.FromCells(10);

			var allHome = squad.CarrierUnits
				.Where(u => !u.IsDead)
				.All(u => (u.CenterPosition - basePos).Length <= homeRange.Length);

			if (allHome && returnIssued)
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class TransportDoneState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			squad.SquadManager.UnregisterSquad(squad);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
