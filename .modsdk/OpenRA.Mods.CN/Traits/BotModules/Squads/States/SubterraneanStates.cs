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
	// ---------------------------------------------------------------------------
	// Helpers
	// ---------------------------------------------------------------------------

	static class SubterraneanHelpers
	{
		static readonly BitSet<TargetableType> UndergroundTypes = new("Underground");

		/// <summary>Returns true if the actor is currently in the subterranean layer.</summary>
		public static bool IsSubmerged(Actor a)
		{
			return a.GetEnabledTargetTypes().Overlaps(UndergroundTypes);
		}

		/// <summary>Returns true if ALL orderable units in the squad are submerged.</summary>
		public static bool AllSubmerged(CNSquad squad)
		{
			return squad.OrderableUnits.All(IsSubmerged);
		}

		/// <summary>Returns true if ANY orderable unit is still submerged.</summary>
		public static bool AnySubmerged(CNSquad squad)
		{
			return squad.OrderableUnits.Any(IsSubmerged);
		}
	}

	// ===========================================================================
	// SUBTANK — SubterraneanAssault
	// ===========================================================================

	/// <summary>
	/// Idle: wait until squad is ready, then find a target and begin burrow approach.
	/// </summary>
	sealed class SubAssaultIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			// Find a priority target or closest enemy
			Actor target = null;
			if (squad.PreferredTargetTypes != null && squad.PreferredTargetTypes.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetTypes, center);

			target ??= FindClosestEnemyUnit(squad);

			if (target == null)
				return;

			squad.SetActorToTarget(target);
			squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultApproachState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Approach: issue Move order to near target. Units burrow automatically via subterranean locomotor.
	/// Wait until target is within AttackScanRadius, then surface and attack.
	/// </summary>
	sealed class SubAssaultApproachState : CNStateBase, ICNState
	{
		bool orderIssued;

		public void Activate(CNSquad squad) { orderIssued = false; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultIdleState());
				return;
			}

			var center = squad.CenterUnit();
			if (center == null)
				return;

			// Check if we're close enough to surface and attack
			var distToTarget = (center.CenterPosition - squad.TargetActor.CenterPosition).Length;
			var attackRange = WDist.FromCells(squad.SquadManager.Info.AttackScanRadius);

			if (distToTarget <= attackRange.Length)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultAttackState());
				return;
			}

			// Issue Move order once (locomotor handles burrowing)
			if (!orderIssued)
			{
				var targetCell = squad.World.Map.CellContaining(squad.TargetActor.CenterPosition);
				foreach (var unit in squad.OrderableUnits)
					squad.Bot.QueueOrder(new Order("Move", unit,
						Target.FromCell(squad.World, targetCell), false));
				orderIssued = true;
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Attack: units surface (stop burrowing) and attack the target.
	/// Reborrow and flee if HP critical.
	/// </summary>
	sealed class SubAssaultAttackState : CNStateBase, ICNState
	{
		int stuckTicks;
		const int MaxStuckTicks = 8;

		public void Activate(CNSquad squad)
		{
			stuckTicks = 0;
			// Surface all units by issuing a stop
			foreach (var unit in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Stop", unit, false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Flee if critically damaged
			if (ShouldFlee(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultReburrowState());
				return;
			}

			if (!squad.IsTargetValid)
			{
				stuckTicks++;
				if (stuckTicks > MaxStuckTicks)
					squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultIdleState());
				return;
			}

			stuckTicks = 0;

			// Units still surfacing — wait
			if (SubterraneanHelpers.AnySubmerged(squad))
				return;

			// Issue attack orders to idle units
			foreach (var unit in squad.OrderableUnits)
				if (!BusyAttack(unit))
					squad.Bot.QueueOrder(new Order("AttackMove", unit, squad.Target, false));
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Reborrow: retreat underground, move back to base to recover.
	/// After reaching base, return to Idle (squad stays alive for future use).
	/// </summary>
	sealed class SubAssaultReburrowState : CNStateBase, ICNState
	{
		bool retreatIssued;

		public void Activate(CNSquad squad) { retreatIssued = false; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!retreatIssued)
			{
				// Move to base — subterranean locomotor re-burrows automatically
				GoToRandomOwnBuilding(squad);
				retreatIssued = true;
			}

			// Wait until all units have burrowed (are traveling underground), then go Idle.
			// They will surface at the base on their own.
			if (SubterraneanHelpers.AllSubmerged(squad))
				squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultIdleState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	// ===========================================================================
	// SAPC — SubterraneanTransport (Ambush)
	// ===========================================================================

	/// <summary>
	/// Idle: wait for passengers to be assigned, then begin loading.
	/// </summary>
	sealed class SubTransportIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Check if we have passengers assigned
			var hasPassengers = squad.SlotAssignments
				.Any(a => a.SlotInfo.IsPassenger && a.Passengers.Count > 0);

			if (hasPassengers)
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportLoadState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Load: carriers move to passengers and load them.
	/// </summary>
	sealed class SubTransportLoadState : CNStateBase, ICNState
	{
		int loadWaitTicks;
		const int MaxLoadWaitTicks = 10;

		public void Activate(CNSquad squad) { loadWaitTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			loadWaitTicks++;
			if (loadWaitTicks > MaxLoadWaitTicks)
			{
				// Timed out — give up and go back to idle
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportIdleState());
				return;
			}

			var carriers = squad.CarrierUnits.Where(u => !u.IsDead).ToList();
			if (carriers.Count == 0)
				return;

			var passengers = squad.PassengerUnits.Where(u => !u.IsDead).ToList();
			if (passengers.Count == 0)
			{
				// No passengers — skip straight to approach
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportBurrowState());
				return;
			}

			// Check how many passengers are already loaded (they leave the world when boarding)
			var loadedCount = passengers.Count(p => !p.IsDead && !p.IsInWorld);

			if (loadedCount >= passengers.Count)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportBurrowState());
				return;
			}

			// Issue EnterTransport orders to passengers not yet loaded
			foreach (var passenger in passengers)
			{
				if (passenger.IsIdle)
				{
					var nearestCarrier = carriers
						.MinByOrDefault(c =>
							(c.CenterPosition - passenger.CenterPosition).LengthSquared);
					if (nearestCarrier != null)
						squad.Bot.QueueOrder(new Order("EnterTransport", passenger,
							Target.FromActor(nearestCarrier), false));
				}
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Burrow: move to ambush position behind enemy lines while submerged.
	/// Target position: near enemy Construction Yard, on the side away from our base.
	/// </summary>
	sealed class SubTransportBurrowState : CNStateBase, ICNState
	{
		bool moveIssued;

		public void Activate(CNSquad squad) { moveIssued = false; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!moveIssued)
			{
				var ambushPos = FindAmbushPosition(squad);
				if (ambushPos == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new SubTransportIdleState());
					return;
				}

				squad.SetPositionToTarget(ambushPos.Value);
				var targetCell = squad.World.Map.CellContaining(ambushPos.Value);

				foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
					squad.Bot.QueueOrder(new Order("Move", carrier,
						Target.FromCell(squad.World, targetCell), false));

				moveIssued = true;
			}

			// Check if we've arrived (carriers surfaced at destination)
			WPos targetPos;
			try
			{
				if (squad.Target.Type == TargetType.Invalid)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new SubTransportIdleState());
					return;
				}

				targetPos = squad.Target.CenterPosition;
			}
			catch (InvalidOperationException)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportIdleState());
				return;
			}

			var arrived = squad.CarrierUnits
				.Where(u => !u.IsDead)
				.All(u => !SubterraneanHelpers.IsSubmerged(u) &&
						  (u.CenterPosition - targetPos).Length <
						  WDist.FromCells(3).Length);

			if (arrived)
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportSurfaceState());
		}

		public void Deactivate(CNSquad squad) { }

		static WPos? FindAmbushPosition(CNSquad squad)
		{
			var world = squad.World;
			var player = squad.Bot.Player;

			// Find enemy Construction Yard
			var enemyCY = world.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead &&
							a.IsInWorld &&
							a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy &&
							a.CanBeViewedByPlayer(player) &&
							a.Info.HasTraitInfo<BuildingInfo>())
				.MinByOrDefault(a => (a.CenterPosition - (squad.CenterUnit()?.CenterPosition ?? WPos.Zero)).LengthSquared);

			if (enemyCY == null)
				return null;

			// Ambush position: enemy CY + offset away from our base center
			var ourBase = world.Map.CenterOfCell(squad.SquadManager.GetRandomBaseCenter());
			var enemyPos = enemyCY.CenterPosition;

			// Direction from our base to enemy base
			var dirToEnemy = enemyPos - ourBase;
			if (dirToEnemy == WVec.Zero)
				return enemyPos;

			// Offset: 5 cells past the enemy CY, away from our base
			var normalized = dirToEnemy * 1024 / dirToEnemy.Length;
			var ambushOffset = new WVec(normalized.X, normalized.Y, 0) *
				WDist.FromCells(5).Length / 1024;

			return enemyPos + ambushOffset;
		}
	}

	/// <summary>
	/// Surface: issue Stop to carriers so they surface, then unload passengers.
	/// </summary>
	sealed class SubTransportSurfaceState : CNStateBase, ICNState
	{
		int waitTicks;
		const int SurfaceWaitTicks = 2;

		public void Activate(CNSquad squad)
		{
			waitTicks = 0;
			// Stop carriers to trigger surfacing
			foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
				squad.Bot.QueueOrder(new Order("Stop", carrier, false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			waitTicks++;

			// Wait for surface animation
			if (waitTicks < SurfaceWaitTicks)
				return;

			// Unload all carriers
			foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
				squad.Bot.QueueOrder(new Order("Unload", carrier, false));

			squad.FuzzyStateMachine.ChangeState(squad, new SubTransportReturnState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Return: carriers burrow and return to base. Squad dissolves on arrival.
	/// </summary>
	sealed class SubTransportReturnState : CNStateBase, ICNState
	{
		bool returnIssued;

		public void Activate(CNSquad squad) { returnIssued = false; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!returnIssued)
			{
				GoToRandomOwnBuilding(squad);
				returnIssued = true;
			}

			// Dissolve once all carriers are back near base and surfaced
			var allHome = squad.CarrierUnits
				.Where(u => !u.IsDead)
				.All(u => !SubterraneanHelpers.IsSubmerged(u));

			if (allHome && returnIssued)
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportDoneState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Done: unregister the squad so its units return to the idle pool.
	/// </summary>
	sealed class SubTransportDoneState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			squad.SquadManager.UnregisterSquad(squad);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
