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
	// Helpers
	// ---------------------------------------------------------------------------
	static class SubterraneanHelpers
	{
		static readonly BitSet<TargetableType> UndergroundTypes = new("Underground");
		static readonly string[] DefaultStrikeCapabilities =
		[
			"Superweapon", "Tech", "Production", "Economy", "Power",
		];

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
		/// How strongly a building pulls the deep-insertion point toward itself. High-value soft
		/// targets (economy/production/tech) dominate so subtanks surface in the vulnerable core.
		/// </summary>
		static int InsertionValueWeight(Actor b)
		{
			var weight = 1;
			if (HasCapability(b, "Superweapon")) weight += 8;
			if (HasCapability(b, "Tech")) weight += 6;
			if (HasCapability(b, "Economy")) weight += 6;
			if (HasCapability(b, "Production")) weight += 5;
			if (HasCapability(b, "Power")) weight += 3;
			return weight;
		}

		/// <summary>
		/// Chooses the soft, valuable building behind the active front. Capability value dominates;
		/// among equivalent buildings, depth from our primary base favours the rear and known surface
		/// fire breaks ties against a suicidal emergence point.
		/// </summary>
		public static Actor FindStrikeTarget(CNSquad squad, Actor source, Player enemy)
		{
			if (source == null || enemy == null)
				return null;

			var preferred = squad.PreferredTargetCapabilities != null &&
				squad.PreferredTargetCapabilities.Length > 0
					? squad.PreferredTargetCapabilities
					: DefaultStrikeCapabilities;
			var ownBase = squad.World.Map.CenterOfCell(squad.SquadManager.GetPrimaryBaseCenter());
			Actor best = null;
			var bestScore = long.MinValue;
			foreach (var candidate in squad.SquadManager.GetCachedEnemyBuildings())
			{
				if (candidate.Owner != enemy || HasCapability(candidate, "Defense"))
					continue;

				var preference = 0;
				for (var i = 0; i < preferred.Length; i++)
					if (HasCapability(candidate, preferred[i]))
					{
						preference = preferred.Length - i;
						break;
					}

				var depthCells = (candidate.CenterPosition - ownBase).Length / 1024;
				var surfaceThreat = squad.SquadManager.GetDefenseThreatAt(candidate.CenterPosition, source.Info);
				var score = (long)preference * 100000
					+ (long)InsertionValueWeight(candidate) * 10000
					+ depthCells * 100L
					- surfaceThreat;

				if (score > bestScore || (score == bestScore && (best == null || candidate.ActorID < best.ActorID)))
				{
					best = candidate;
					bestScore = score;
				}
			}

			return best;
		}

		/// <summary>
		/// Returns the cell to burrow toward for a deep-insertion attack.
		/// Computes a value-weighted centroid of the selected target's local base — economy,
		/// production and tech pull hard — and pushes slightly past it, away from our base.
		/// This surfaces units in the soft economic heart rather than the defended perimeter
		/// (a plain centroid would be dragged forward by front-line refineries/power).
		/// </summary>
		public static CPos FindDeepInsertionCell(CNSquad squad, Actor source)
		{
			long sumX = 0, sumY = 0, totalWeight = 0;
			var missionAnchor = squad.IsTargetValid ? squad.TargetActor : null;
			var clusterRadius = WDist.FromCells(24);
			var clusterRadiusSq = (long)clusterRadius.Length * clusterRadius.Length;

			foreach (var b in squad.SquadManager.GetCachedEnemyBuildings())
			{
				if (HasCapability(b, "Defense") ||
					(squad.MissionTargetPlayer != null && b.Owner != squad.MissionTargetPlayer) ||
					(missionAnchor != null &&
						(b.CenterPosition - missionAnchor.CenterPosition).LengthSquared > clusterRadiusSq))
					continue;

				var weight = InsertionValueWeight(b);
				sumX += (long)b.CenterPosition.X * weight;
				sumY += (long)b.CenterPosition.Y * weight;
				totalWeight += weight;
			}

			WPos targetPos;
			if (totalWeight > 0)
			{
				targetPos = new WPos((int)(sumX / totalWeight), (int)(sumY / totalWeight), 0);
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
				targetPos += offset;
			}

			var idealCell = squad.World.Map.CellContaining(targetPos);

			// The raw insertion point is a value-weighted centroid of building positions, so in a
			// densely-packed base it often lands on or beside an actual building footprint.
			// SubterraneanTransitionTerrainTypes only validates terrain type, not occupancy, so
			// without this the Move order silently reroutes to whatever open cell the pathfinder
			// can reach - usually near the base's outer edge, not the soft interior it's aiming for.
			return FindNearestOpenSurfaceCell(squad, source, idealCell) ?? idealCell;
		}

		/// <summary>
		/// Expands outward in rings from <paramref name="idealCell"/> to find the closest cell the
		/// source actor can actually enter, so deep-insertion/ambush points don't get silently
		/// deflected to the base perimeter by unchecked building occupancy.
		/// </summary>
		public static CPos? FindNearestOpenSurfaceCell(CNSquad squad, Actor source, CPos idealCell)
		{
			var mobile = source.TraitOrDefault<Mobile>();
			if (mobile == null)
				return null;

			var map = squad.World.Map;
			const int MaxSearchRadius = 6;

			for (var radius = 0; radius <= MaxSearchRadius; radius++)
			{
				CPos? best = null;
				var bestDistSq = long.MaxValue;

				for (var dy = -radius; dy <= radius; dy++)
				{
					for (var dx = -radius; dx <= radius; dx++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
							continue;

						var candidate = idealCell + new CVec(dx, dy);
						if (!map.Contains(candidate) || !mobile.CanEnterCell(candidate))
							continue;

						var distSq = (long)dx * dx + (long)dy * dy;
						if (distSq < bestDistSq)
						{
							bestDistSq = distSq;
							best = candidate;
						}
					}
				}

				if (best.HasValue)
					return best;
			}

			return null;
		}
	}

	// ===========================================================================
	// SUBTANK — SubterraneanAssault
	// ===========================================================================

	/// <summary>
	/// Idle: wait until squad is ready and the front has made contact, then strike the same enemy's rear.
	/// </summary>
	sealed class SubAssaultIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad)
		{
			squad.MissionTargetPlayer = null;
			squad.SetActorToTarget(null);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;
			if (!squad.IsOperational)
				return;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			Actor target;
			if (squad.SquadManager.Info.AttackWaveEnabled)
			{
				// Subterranean units are the second blow, not another body in the rally blob. Hold at
				// home until a front participant has actually exchanged damage with the wave's victim.
				if (!squad.SquadManager.TryGetSubterraneanStrikeEnemy(out var enemy))
					return;

				target = SubterraneanHelpers.FindStrikeTarget(squad, center, enemy);
				if (target != null)
					squad.MissionTargetPlayer = enemy;
			}
			else
			{
				// Profiles may disable coordinated waves. Preserve an independent subterranean raid
				// there rather than leaving an otherwise valid template permanently at home.
				target = null;
				if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
					target = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

				target ??= CNSquadHelper.FindUnprotectedTarget(squad);
				if (target != null)
					squad.MissionTargetPlayer = target.Owner;
			}

			if (target == null)
			{
				return;
			}

			CNBotLog.Debug("{0} subterranean strike released with the front: {1} targets {2} at {3}",
				squad.SquadManager.Player, squad.TemplateName ?? "SubterraneanAssault", target.Info.Name, target.Location);
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
		// Give up and retreat once no progress has been made for this many ticks
		const int MaxApproachTicks = 500;

		// Movement below this (sub-cell) threshold doesn't count as progress.
		const int MinProgressDistance = 128;

		// Surface when within this distance of the deep insertion point
		static readonly WDist ArrivalRadius = WDist.FromCells(2);

		bool orderIssued;
		CPos destinationCell;
		int lastProgressTick;
		WPos lastCenterPos;

		public void Activate(CNSquad squad)
		{
			orderIssued = false;
			destinationCell = CPos.Zero;
			lastProgressTick = squad.World.WorldTick;
			lastCenterPos = squad.CenterUnit()?.CenterPosition ?? WPos.Zero;
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

			// Stuck detection: only give up once genuinely stalled, not just slow on a big map.
			var moved = (center.CenterPosition - lastCenterPos).LengthSquared >
				MinProgressDistance * MinProgressDistance;
			if (moved)
			{
				lastCenterPos = center.CenterPosition;
				lastProgressTick = squad.World.WorldTick;
			}

			if (squad.World.WorldTick - lastProgressTick > MaxApproachTicks)
			{
				// Can't reach insertion point — stalled — retreat
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
		// Game ticks, not update cycles: how long the squad searches without a target before going
		// back to idle, and how long it waits for its units to surface before reburrowing.
		const int MaxStuckTicks = 1875;
		const int MaxSurfaceWaitTicks = 3750;

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
					var newTarget = squad.MissionTargetPlayer != null
						? SubterraneanHelpers.FindStrikeTarget(squad, center, squad.MissionTargetPlayer)
						: null;
					if (newTarget == null && squad.MissionTargetPlayer == null)
					{
						if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
							newTarget = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

						newTarget ??= CNSquadHelper.FindUnprotectedTarget(squad);
					}

					if (newTarget != null)
					{
						squad.SetActorToTarget(newTarget);
						stuckTicks = 0;
						return;
					}
				}

				stuckTicks += squad.TicksSinceLastUpdate;
				if (stuckTicks > MaxStuckTicks)
					squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultIdleState());
				return;
			}

			stuckTicks = 0;

			// Wait for units to surface, with a guard against being stuck burrowed
			if (SubterraneanHelpers.AnySubmerged(squad))
			{
				surfaceWaitTicks += squad.TicksSinceLastUpdate;
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
	/// Reborrow: dive to a short safe distance instead of trekking all the way home. The squad is
	/// already submerged and unseen while moving, so once it's clear of the immediate danger it
	/// heads straight back to Idle to pick a fresh target - letting it keep raiding elsewhere in
	/// or around the same base rather than a long round trip. Falls back to base if no safe nearby
	/// cell is found.
	/// </summary>
	sealed class SubAssaultReburrowState : CNStateBase, ICNState
	{
		const int MinRetreatCells = 6;
		const int MaxRetreatCells = 14;
		const int MaxRetreatTicks = 250;

		CPos retreatCell;
		int retreatStartTick;

		public void Activate(CNSquad squad)
		{
			retreatStartTick = squad.World.WorldTick;
			retreatCell = FindRetreatCell(squad, MinRetreatCells, MaxRetreatCells) ??
				squad.SquadManager.GetRandomBaseCenter();

			foreach (var unit in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Move", unit,
					Target.FromCell(squad.World, retreatCell), false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var retreatPos = squad.World.Map.CenterOfCell(retreatCell);
			var arrived = squad.OrderableUnits.All(u =>
				(u.CenterPosition - retreatPos).Length <= WDist.FromCells(2).Length);

			if (arrived || squad.World.WorldTick - retreatStartTick > MaxRetreatTicks)
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
		public void Activate(CNSquad squad) { squad.AcceptingPassengers = true; }

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
		// Abort if no new passenger has boarded within this many game ticks (~50 s at 15 ticks/s),
		// matching the ground TransportLoadState budget. The previous 10-update-cycle cap was far
		// too short for SAPC passengers to walk in and board, bouncing the squad back to idle so
		// it never deployed.
		const int MaxLoadTicks = 750;

		int lastProgressTick;
		int lastBoardedCount;

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = true;
			lastProgressTick = squad.World.WorldTick;
			lastBoardedCount = 0;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var carriers = squad.CarrierUnits.Where(u => !u.IsDead).ToList();
			if (carriers.Count == 0)
				return;

			var passengers = squad.PassengerUnits.Where(u => !u.IsDead).ToList();

			// Cargo aboard is the authoritative "loaded" signal — passengers leave
			// the world while boarded, so a passenger count cannot tell "all
			// aboard" apart from "none assigned".
			var anyCargo = carriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false);

			if (passengers.Count == 0 && !anyCargo)
			{
				// No passengers assigned and nothing aboard — stay at base and keep requesting top-ups.
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportIdleState());
				return;
			}

			// Track boarding progress: a slow-but-steady trickle of new production shouldn't be
			// treated the same as a genuinely stalled load. MaxLoadTicks only fires once no new
			// passenger has boarded for that whole duration, not from a fixed load-start clock.
			var boardedCount = carriers.Sum(c => c.TraitOrDefault<Cargo>()?.PassengerCount ?? 0);
			if (boardedCount > lastBoardedCount)
			{
				lastBoardedCount = boardedCount;
				lastProgressTick = squad.World.WorldTick;
			}

			var waiting = passengers.Count(u => u.IsInWorld);
			var carriersFull = carriers.All(c =>
			{
				var cargo = c.TraitOrDefault<Cargo>();
				return cargo == null || !cargo.HasSpace(1);
			});
			var fullyRecruitedAndAboard =
				waiting == 0 && squad.RecruitedPassengerCount >= squad.DesiredPassengerCount;
			var allLoaded = anyCargo && (carriersFull || fullyRecruitedAndAboard);

			if (allLoaded)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportWaveSyncState());
				return;
			}

			if (squad.World.WorldTick - lastProgressTick >= MaxLoadTicks)
			{
				// Timeout may send partial cargo, but never an empty SAPC.
				squad.FuzzyStateMachine.ChangeState(squad,
					anyCargo ? new SubTransportWaveSyncState() : new SubTransportIdleState());
				return;
			}

			// Reserve the still-walking passengers against carrier capacity before issuing orders.
			// Choosing the nearest carrier independently sent every infantryman to the same SAPC;
			// with two carriers the first filled while the second stayed empty and the rest retried it.
			carriers.Sort((a, b) => a.ActorID.CompareTo(b.ActorID));
			passengers.Sort((a, b) => a.ActorID.CompareTo(b.ActorID));
			var freeCapacity = new int[carriers.Count];
			for (var i = 0; i < carriers.Count; i++)
			{
				var cargo = carriers[i].TraitOrDefault<Cargo>();
				if (cargo == null)
					continue;

				var usedWeight = cargo.Passengers.Sum(p =>
					p.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1);
				freeCapacity[i] = cargo.Info.MaxWeight - usedWeight;
			}

			var carrierIndex = 0;
			foreach (var passenger in passengers)
			{
				if (!passenger.IsInWorld)
					continue;

				var passengerWeight = passenger.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1;
				while (carrierIndex < carriers.Count && freeCapacity[carrierIndex] < passengerWeight)
					carrierIndex++;

				if (carrierIndex >= carriers.Count)
					break;

				if (passenger.IsIdle)
				{
					squad.Bot.QueueOrder(new Order("EnterTransport", passenger,
						Target.FromActor(carriers[carrierIndex]), false));
				}

				// Non-idle passengers are already walking to the same deterministic carrier,
				// so their seats must remain reserved while newly produced infantry is assigned.
				freeCapacity[carrierIndex] -= passengerWeight;
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// A loaded SAPC force stays out of the front column. Once that column exchanges damage with its
	/// objective, the transports take a high-value rear target owned by the same player and depart.
	/// </summary>
	sealed class SubTransportWaveSyncState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = false;
			squad.MissionTargetPlayer = null;
			squad.SetActorToTarget(null);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var carrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead);
			if (carrier == null)
				return;

			Player enemy;
			if (squad.SquadManager.Info.AttackWaveEnabled)
			{
				if (!squad.SquadManager.TryGetSubterraneanStrikeEnemy(out enemy))
					return;
			}
			else
			{
				enemy = squad.SquadManager.GetCachedEnemyBuildings()
					.FirstOrDefault(a => !SubterraneanHelpers.HasCapability(a, "Defense"))?.Owner;
			}

			var target = SubterraneanHelpers.FindStrikeTarget(squad, carrier, enemy);
			if (target == null)
				return;

			squad.MissionTargetPlayer = enemy;
			squad.SetActorToTarget(target);
			CNBotLog.Debug("{0} subterranean transport released with the front: {1} targets {2} at {3}",
				squad.SquadManager.Player, squad.TemplateName ?? "SubterraneanTransport", target.Info.Name, target.Location);
			squad.FuzzyStateMachine.ChangeState(squad, new SubTransportBurrowState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Burrow: move to ambush position behind enemy lines while submerged.
	/// Target position: near a critical enemy building, on the side away from our base.
	/// </summary>
	sealed class SubTransportBurrowState : CNStateBase, ICNState
	{
		const int MaxBurrowTicks = 900;

		// Movement below this (sub-cell) threshold doesn't count as progress.
		const int MinProgressDistance = 128;

		bool moveIssued;
		int lastProgressTick;
		WPos lastCarrierPos;
		readonly Dictionary<uint, CPos> carrierAmbushCells = [];

		static readonly CVec[] CarrierOffsets =
		[
			new(0, 0),
			new(2, 0), new(-2, 0), new(0, 2), new(0, -2),
			new(2, 2), new(-2, -2), new(2, -2), new(-2, 2),
		];

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = false;
			moveIssued = false;
			lastProgressTick = squad.World.WorldTick;
			lastCarrierPos = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead)?.CenterPosition ?? WPos.Zero;
			carrierAmbushCells.Clear();
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Stuck detection: only give up once genuinely stalled, not just slow on a big map.
			var leadCarrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead);
			if (leadCarrier != null)
			{
				var moved = (leadCarrier.CenterPosition - lastCarrierPos).LengthSquared >
					MinProgressDistance * MinProgressDistance;
				if (moved)
				{
					lastCarrierPos = leadCarrier.CenterPosition;
					lastProgressTick = squad.World.WorldTick;
				}
			}

			if (squad.World.WorldTick - lastProgressTick > MaxBurrowTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportReturnState());
				return;
			}

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
				AssignCarrierAmbushCells(squad, targetCell);

				foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
					squad.Bot.QueueOrder(new Order("Move", carrier,
						Target.FromCell(squad.World, ResolveCarrierAmbushCell(carrier, targetCell)), false));

				// Escort subtanks tunnel alongside — their subterranean locomotor re-burrows automatically.
				foreach (var escort in squad.EscortUnits.Where(u => !u.IsDead && u.IsInWorld))
					squad.Bot.QueueOrder(new Order("Move", escort,
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

			var fallbackCell = squad.World.Map.CellContaining(targetPos);
			var arrived = squad.CarrierUnits.Where(u => !u.IsDead).All(u =>
			{
				var arrivalPos = squad.World.Map.CenterOfCell(ResolveCarrierAmbushCell(u, fallbackCell));
				return !SubterraneanHelpers.IsSubmerged(u) &&
					(u.CenterPosition - arrivalPos).Length < WDist.FromCells(3).Length;
			});

			if (arrived)
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportSurfaceState());
		}

		public void Deactivate(CNSquad squad) { }

		void AssignCarrierAmbushCells(CNSquad squad, CPos anchor)
		{
			var reserved = new HashSet<CPos>();
			var slot = 0;
			foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead).OrderBy(u => u.ActorID))
			{
				for (var i = 0; i < CarrierOffsets.Length; i++)
				{
					var ideal = anchor + CarrierOffsets[(slot + i) % CarrierOffsets.Length];
					var candidate = SubterraneanHelpers.FindNearestOpenSurfaceCell(squad, carrier, ideal);
					if (!candidate.HasValue || reserved.Contains(candidate.Value))
						continue;

					carrierAmbushCells[carrier.ActorID] = candidate.Value;
					reserved.Add(candidate.Value);
					break;
				}

				slot++;
			}
		}

		CPos ResolveCarrierAmbushCell(Actor carrier, CPos fallback)
		{
			return carrierAmbushCells.TryGetValue(carrier.ActorID, out var assigned) ? assigned : fallback;
		}

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

			var idealCell = world.Map.CellContaining(enemyPos + ambushOffset);

			// As with the SUBTANK deep-insertion point, the raw offset often lands on/beside a
			// building footprint - without this the SAPC would silently surface at the nearest
			// open cell the pathfinder can reach, typically the base's edge rather than its core.
			var openCell = SubterraneanHelpers.FindNearestOpenSurfaceCell(squad, source, idealCell);
			return openCell.HasValue ? world.Map.CenterOfCell(openCell.Value) : enemyPos + ambushOffset;
		}

		static Actor FindAmbushTarget(CNSquad squad, Actor source)
		{
			if (squad.IsTargetValid && (squad.MissionTargetPlayer == null ||
				squad.TargetActor.Owner == squad.MissionTargetPlayer))
				return squad.TargetActor;

			return SubterraneanHelpers.FindStrikeTarget(squad, source, squad.MissionTargetPlayer);
		}
	}

	/// <summary>
	/// Surface: issue Stop to carriers so they surface, then unload passengers.
	/// </summary>
	sealed class SubTransportSurfaceState : CNStateBase, ICNState
	{
		// Game ticks, not update cycles: long enough for the surface animation to play out.
		const int SurfaceWaitTicks = 150;

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

			waitTicks += squad.TicksSinceLastUpdate;

			// Wait for surface animation
			if (waitTicks < SurfaceWaitTicks)
				return;

			// Unload all carriers
			foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
				squad.Bot.QueueOrder(new Order("Unload", carrier, false));

			// Escort subtanks surface and attack the nearest enemy building.
			var escortTarget = CNSquadHelper.FindUnprotectedTarget(squad);
			if (escortTarget != null)
			{
				foreach (var escort in squad.EscortUnits.Where(u => !u.IsDead && u.IsInWorld))
					squad.Bot.QueueOrder(new Order("Attack", escort, Target.FromActor(escortTarget), false));
			}

			squad.FuzzyStateMachine.ChangeState(squad, new TransportDropAssaultState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Return: carriers burrow and return to base. Squad dissolves on arrival.
	/// </summary>
	sealed class SubTransportReturnState : CNStateBase, ICNState
	{
		const int MaxReturnTicks = 1200;
		bool returnIssued;
		CPos returnCell;
		int returnStartTick;

		public void Activate(CNSquad squad)
		{
			returnIssued = false;
			returnCell = CPos.Zero;
			returnStartTick = squad.World.WorldTick;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (squad.World.WorldTick - returnStartTick > MaxReturnTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SubTransportDoneState());
				return;
			}

			if (!returnIssued)
			{
				returnCell = squad.SquadManager.GetRandomBaseCenter();
				var returnTarget = Target.FromCell(squad.World, returnCell);
				foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead))
					squad.Bot.QueueOrder(new Order("Move", carrier, returnTarget, false));
				foreach (var escort in squad.EscortUnits.Where(u => !u.IsDead && u.IsInWorld))
					squad.Bot.QueueOrder(new Order("Move", escort, returnTarget, false));

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
