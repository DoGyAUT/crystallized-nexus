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

		public static bool HasCapability(Actor actor, string capability)
		{
			return actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains(capability) ?? false;
		}

		/// <summary>
		/// Returns the cell to burrow toward for a deep-insertion attack.
		/// Computes the centroid of all non-defense enemy buildings and pushes
		/// slightly past it, away from our base — so units surface in the heart
		/// of the enemy base rather than at the defended perimeter.
		/// </summary>
		public static CPos FindDeepInsertionCell(CNSquad squad, Actor source)
		{
			long sumX = 0, sumY = 0;
			var count = 0;

			foreach (var b in squad.SquadManager.GetCachedEnemyBuildings())
			{
				if (HasCapability(b, "Defense"))
					continue;

				sumX += b.CenterPosition.X;
				sumY += b.CenterPosition.Y;
				count++;
			}

			WPos targetPos;
			if (count > 0)
			{
				targetPos = new WPos((int)(sumX / count), (int)(sumY / count), 0);
			}
			else
			{
				// Only defenses known — surface at the original target if still valid
				if (squad.IsTargetValid)
					return squad.World.Map.CellContaining(squad.TargetActor.CenterPosition);

				return source.Location;
			}

			// Push a few cells further past the centroid, deeper into enemy territory
			var ourBase = squad.World.Map.CenterOfCell(squad.SquadManager.GetRandomBaseCenter());
			var dir = targetPos - ourBase;
			if (dir != WVec.Zero)
			{
				var normalized = dir * 1024 / dir.Length;
				var offset = new WVec(normalized.X, normalized.Y, 0) * WDist.FromCells(4).Length / 1024;
				targetPos = targetPos + offset;
			}

			return squad.World.Map.CellContaining(targetPos);
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

			// Priority target by BotCapabilities tag, then closest non-defense building.
			Actor target = null;
			if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

			target ??= CNSquadHelper.FindUnprotectedTarget(squad);

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
		// Give up and retreat if still burrowing after this many ticks
		const int MaxApproachTicks = 500;

		// Surface when within this distance of the deep insertion point
		static readonly WDist ArrivalRadius = WDist.FromCells(2);

		bool orderIssued;
		CPos destinationCell;
		int approachTicks;

		public void Activate(CNSquad squad)
		{
			orderIssued = false;
			destinationCell = CPos.Zero;
			approachTicks = 0;
		}

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

			if (!orderIssued)
			{
				// Burrow toward the centroid of non-defense enemy buildings so units
				// surface deep inside the base rather than at the defended perimeter.
				destinationCell = SubterraneanHelpers.FindDeepInsertionCell(squad, center);

				foreach (var unit in squad.OrderableUnits)
					squad.Bot.QueueOrder(new Order("Move", unit,
						Target.FromCell(squad.World, destinationCell), false));

				orderIssued = true;
			}

			approachTicks++;
			if (approachTicks > MaxApproachTicks)
			{
				// Can't reach insertion point in time — retreat
				squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultReburrowState());
				return;
			}

			// Surface once we're close to the deep insertion position
			var destPos = squad.World.Map.CenterOfCell(destinationCell);
			if ((center.CenterPosition - destPos).Length <= ArrivalRadius.Length)
			{
				// Clear the preset target so AttackState re-finds the nearest enemy
				// from the deep position rather than marching back to the perimeter.
				squad.SetActorToTarget(null);
				squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultAttackState());
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
		const int MaxStuckTicks = 25;
		const int MaxSurfaceWaitTicks = 50;

		int stuckTicks;
		int surfaceWaitTicks;

		public void Activate(CNSquad squad)
		{
			stuckTicks = 0;
			surfaceWaitTicks = 0;

			// Surface all units by issuing a stop
			foreach (var unit in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Stop", unit, false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (ShouldFlee(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultReburrowState());
				return;
			}

			if (!squad.IsTargetValid)
			{
				// Re-acquire the nearest valuable target from the current deep position.
				// This handles the post-insertion case where the preset target was cleared.
				var center = squad.CenterUnit();
				if (center != null)
				{
					Actor newTarget = null;
					if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
						newTarget = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);
					newTarget ??= CNSquadHelper.FindUnprotectedTarget(squad);

					if (newTarget != null)
					{
						squad.SetActorToTarget(newTarget);
						stuckTicks = 0;
						return;
					}
				}

				stuckTicks++;
				if (stuckTicks > MaxStuckTicks)
					squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultIdleState());
				return;
			}

			stuckTicks = 0;

			// Wait for units to surface, with a guard against being stuck burrowed
			if (SubterraneanHelpers.AnySubmerged(squad))
			{
				surfaceWaitTicks++;
				if (surfaceWaitTicks > MaxSurfaceWaitTicks)
					squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultReburrowState());
				return;
			}

			surfaceWaitTicks = 0;

			foreach (var unit in squad.OrderableUnits)
				if (!BusyAttack(unit))
					squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
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
		CPos retreatCell;

		public void Activate(CNSquad squad)
		{
			retreatIssued = false;
			retreatCell = CPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!retreatIssued)
			{
				// Move to base — subterranean locomotor re-burrows automatically.
				retreatCell = squad.SquadManager.GetRandomBaseCenter();
				foreach (var unit in squad.OrderableUnits)
					squad.Bot.QueueOrder(new Order("Move", unit,
						Target.FromCell(squad.World, retreatCell), false));

				retreatIssued = true;
			}

			var retreatPos = squad.World.Map.CenterOfCell(retreatCell);
			var homeRange = WDist.FromCells(8);

			var allHome = squad.OrderableUnits.All(u =>
				(u.CenterPosition - retreatPos).Length <= homeRange.Length);

			// Wait until the squad actually returns home instead of immediately
			// retargeting as soon as it burrows.
			if (allHome)
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
		const int MaxLoadWaitTicks = 10;

		int loadWaitTicks;

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
	/// Target position: near a critical enemy building, on the side away from our base.
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
					// No valid target — unload any passengers before returning to idle.
					foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead && u.TraitOrDefault<Cargo>()?.IsEmpty() == false))
						squad.Bot.QueueOrder(new Order("Unload", carrier, false));
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
			var source = squad.CenterUnit();
			if (source == null)
				return null;

			var target = FindAmbushTarget(squad, source);
			if (target == null)
				return null;

			// Ambush position: target + offset away from our base center.
			var ourBase = world.Map.CenterOfCell(squad.SquadManager.GetRandomBaseCenter());
			var enemyPos = target.CenterPosition;

			// Direction from our base to enemy base
			var dirToEnemy = enemyPos - ourBase;
			if (dirToEnemy == WVec.Zero)
				return enemyPos;

			// Offset: 5 cells past the target, away from our base
			var normalized = dirToEnemy * 1024 / dirToEnemy.Length;
			var ambushOffset = new WVec(normalized.X, normalized.Y, 0) *
				WDist.FromCells(5).Length / 1024;

			return enemyPos + ambushOffset;
		}

		static Actor FindAmbushTarget(CNSquad squad, Actor source)
		{
			var preferredCaps = squad.PreferredTargetCapabilities != null &&
				squad.PreferredTargetCapabilities.Length > 0
					? squad.PreferredTargetCapabilities
					: new[] { "Superweapon", "Tech", "Production", "Economy", "Power" };

			return squad.SquadManager.GetCachedEnemyBuildings()
				.Where(a => HasAnyCapability(a, preferredCaps) && !HasCapability(a, "Defense"))
				.OrderBy(a => ScoreAmbushTarget(squad, source, a, preferredCaps))
				.FirstOrDefault()
				?? squad.SquadManager.GetCachedEnemyBuildings()
					.Where(a => !HasCapability(a, "Defense"))
					.MinByOrDefault(a => (a.CenterPosition - source.CenterPosition).LengthSquared);
		}

		static int ScoreAmbushTarget(CNSquad squad, Actor source, Actor target, string[] preferredCaps)
		{
			var score = (int)((source.CenterPosition - target.CenterPosition).LengthSquared / 65536);

			for (var i = 0; i < preferredCaps.Length; i++)
				if (HasCapability(target, preferredCaps[i]))
				{
					score -= (preferredCaps.Length - i) * 300;
					break;
				}

			foreach (var actor in squad.World.FindActorsInCircle(target.CenterPosition, WDist.FromCells(7)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor))
					continue;

				if (HasCapability(actor, "Defense"))
					score += actor.Info.HasTraitInfo<BuildingInfo>() ? 600 : 180;
				else if (actor.Info.HasTraitInfo<AttackBaseInfo>())
					score += 120;
			}

			return score;
		}

		static bool HasAnyCapability(Actor actor, string[] capabilities)
		{
			foreach (var capability in capabilities)
				if (HasCapability(actor, capability))
					return true;

			return false;
		}

		static bool HasCapability(Actor actor, string capability)
		{
			return actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains(capability) ?? false;
		}
	}

	/// <summary>
	/// Surface: issue Stop to carriers so they surface, then unload passengers.
	/// </summary>
	sealed class SubTransportSurfaceState : CNStateBase, ICNState
	{
		const int SurfaceWaitTicks = 2;

		int waitTicks;

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
		CPos returnCell;

		public void Activate(CNSquad squad)
		{
			returnIssued = false;
			returnCell = CPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!returnIssued)
			{
				returnCell = squad.SquadManager.GetRandomBaseCenter();
				foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
					squad.Bot.QueueOrder(new Order("Move", carrier,
						Target.FromCell(squad.World, returnCell), false));

				returnIssued = true;
			}

			// Dissolve once all carriers are back near base and surfaced
			var returnPos = squad.World.Map.CenterOfCell(returnCell);
			var homeRange = WDist.FromCells(8);

			var carriers = squad.CarrierUnits.Where(u => !u.IsDead).ToList();
			var allHome = carriers.Count > 0 &&
				carriers.All(u => !SubterraneanHelpers.IsSubmerged(u) &&
					(u.CenterPosition - returnPos).Length <= homeRange.Length);

			if (!allHome || !returnIssued)
				return;

			var carriersWithCargo = carriers
				.Where(u => u.TraitOrDefault<Cargo>()?.IsEmpty() == false)
				.ToList();
			if (carriersWithCargo.Count > 0)
			{
				foreach (var carrier in carriersWithCargo.Where(u => u.IsIdle))
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));
				return;
			}

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
