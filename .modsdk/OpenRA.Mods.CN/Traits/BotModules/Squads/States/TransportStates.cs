using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	sealed class TransportIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { squad.AcceptingPassengers = true; }

		public void Tick(CNSquad squad)
		{
			// Sanity check: verify squad and all carriers are valid
			if (!squad.IsValid || !ValidateCarriers(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			if (!squad.IsOperational)
				return;

			var hasCarriers = squad.CarrierUnits.Any(u => !u.IsDead && u.IsInWorld);
			if (!hasCarriers)
				return;

			var hasPassengers = squad.PassengerUnits.Any(u => !u.IsDead && u.IsInWorld);
			if (hasPassengers)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportLoadState());
				return;
			}

			// No live passengers walking around, but cargo is already aboard — this happens when
			// a reformed squad inherits a previously-loaded carrier. Don't sit idle on a loaded
			// transport: head straight to the attack-move state to deliver the cargo.
			if (HasCargoAboard(squad))
				squad.FuzzyStateMachine.ChangeState(squad, new TransportAttackMoveState());
		}

		public void Deactivate(CNSquad squad) { }

		static bool HasCargoAboard(CNSquad squad)
		{
			foreach (var carrier in squad.CarrierUnits)
			{
				if (carrier.IsDead || !carrier.IsInWorld)
					continue;
				if (carrier.TraitOrDefault<Cargo>()?.IsEmpty() == false)
					return true;
			}

			return false;
		}
	}

	sealed class AirTransportIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { squad.AcceptingPassengers = true; }

		public void Tick(CNSquad squad)
		{
			// Sanity check: verify squad and all carriers are valid
			if (!squad.IsValid || !ValidateCarriers(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			if (!squad.IsOperational)
				return;

			var hasCarriers = squad.CarrierUnits.Any(u => !u.IsDead && u.IsInWorld);
			if (!hasCarriers)
				return;

			var hasPassengers = squad.PassengerUnits.Any(u => !u.IsDead && u.IsInWorld);
			if (hasPassengers)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AirTransportLoadState());
				return;
			}

			// No live passengers walking around, but cargo is already aboard — this happens when
			// a reformed squad inherits a previously-loaded carrier. Don't sit idle on a loaded
			// air transport (was the main reason AirTransport squads "just stood around in base").
			if (HasCargoAboard(squad))
				squad.FuzzyStateMachine.ChangeState(squad, new AirTransportAttackMoveState());
		}

		public void Deactivate(CNSquad squad) { }

		static bool HasCargoAboard(CNSquad squad)
		{
			foreach (var carrier in squad.CarrierUnits)
			{
				if (carrier.IsDead || !carrier.IsInWorld)
					continue;
				if (carrier.TraitOrDefault<Cargo>()?.IsEmpty() == false)
					return true;
			}

			return false;
		}
	}

	sealed class AirTransportLoadState : CNStateBase, ICNState
	{
		const int MaxLoadTicks = 750;
		const int LandSearchMaxCells = 10;
		const int PassengerStagingMinCells = 2;
		const int PassengerStagingMaxCells = 4;

		int lastProgressTick;
		int lastBoardedCount;
		CPos? landingCell;
		readonly Dictionary<uint, CPos> passengerStagingCells = [];

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = true;
			lastProgressTick = squad.World.WorldTick;
			lastBoardedCount = 0;
			landingCell = null;
			passengerStagingCells.Clear();

			var carrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead && u.IsInWorld);
			if (carrier == null)
				return;

			landingCell = FindLandingCell(squad, carrier);
			if (!landingCell.HasValue)
				return;

			StagePassengers(squad, landingCell.Value);
			squad.Bot.QueueOrder(new Order("Land", carrier, Target.FromCell(squad.World, landingCell.Value), false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var carrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead && u.IsInWorld);
			if (carrier == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			var aircraft = carrier.TraitOrDefault<Aircraft>();
			var cargo = carrier.TraitOrDefault<Cargo>();
			var anyCargo = cargo?.IsEmpty() == false;
			var passengers = squad.PassengerUnits.Where(u => !u.IsDead).ToList();

			if (passengers.Count == 0 && !anyCargo)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			// Track boarding progress: a slow-but-steady trickle of new production shouldn't be
			// treated the same as a genuinely stalled load. MaxLoadTicks only fires once no new
			// passenger has boarded for that whole duration, not from a fixed load-start clock.
			var boardedCount = cargo?.PassengerCount ?? 0;
			if (boardedCount > lastBoardedCount)
			{
				lastBoardedCount = boardedCount;
				lastProgressTick = squad.World.WorldTick;
			}

			// Depart only when the carrier is full, or every recruited passenger is aboard AND we
			// recruited the full complement. Under capacity we wait while the squad manager tops up
			// passengers (squad.AcceptingPassengers); MaxLoadTicks is the fail-safe.
			var waiting = passengers.Count(u => u.IsInWorld);
			var carrierFull = IsCarrierFull(cargo);
			var fullyRecruitedAndAboard =
				waiting == 0 && squad.RecruitedPassengerCount >= squad.DesiredPassengerCount;

			if (anyCargo && (carrierFull || fullyRecruitedAndAboard))
			{
				// With the wave system active, hold loaded and depart together with the next wave;
				// otherwise make the drop-run immediately (legacy behaviour).
				squad.FuzzyStateMachine.ChangeState(squad,
					squad.SquadManager.Info.AttackWaveEnabled
						? new TransportWaveSyncState()
						: new AirTransportAttackMoveState());
				return;
			}

			if (squad.World.WorldTick - lastProgressTick >= MaxLoadTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad,
					anyCargo ? new AirTransportAttackMoveState() : new TransportDoneState());
				return;
			}

			if (!landingCell.HasValue)
				landingCell = FindLandingCell(squad, carrier);

			if (!landingCell.HasValue)
				return;

			StagePassengers(squad, landingCell.Value);

			if (aircraft == null || !aircraft.AtLandAltitude)
			{
				if (carrier.IsIdle)
					squad.Bot.QueueOrder(new Order("Land", carrier, Target.FromCell(squad.World, landingCell.Value), false));

				return;
			}

			var idlePassengers = passengers.Where(u => u.IsInWorld && u.IsIdle).ToList();
			foreach (var passenger in idlePassengers)
				squad.Bot.QueueOrder(new Order("EnterTransport", passenger, Target.FromActor(carrier), false));
		}

		public void Deactivate(CNSquad squad) { }

		static CPos? FindLandingCell(CNSquad squad, Actor carrier)
		{
			var aircraft = carrier.TraitOrDefault<Aircraft>();
			if (aircraft == null)
				return null;

			var map = squad.World.Map;
			var origin = map.CellContaining(carrier.CenterPosition);

			CPos? best = null;
			var bestScore = int.MaxValue;
			for (var dy = -LandSearchMaxCells; dy <= LandSearchMaxCells; dy++)
			{
				for (var dx = -LandSearchMaxCells; dx <= LandSearchMaxCells; dx++)
				{
					var cell = origin + new CVec(dx, dy);
					if (!map.Contains(cell) || !aircraft.CanLand(cell, blockedByMobile: false))
						continue;

					var score = dx * dx + dy * dy;
					if (score >= bestScore)
						continue;

					bestScore = score;
					best = cell;
				}
			}

			return best;
		}

		void StagePassengers(CNSquad squad, CPos landing)
		{
			foreach (var passenger in squad.PassengerUnits.Where(u => !u.IsDead && u.IsInWorld))
			{
				if (!passengerStagingCells.TryGetValue(passenger.ActorID, out var staging) ||
					!CanPassengerEnter(passenger, staging))
				{
					if (!TryFindPassengerStagingCell(passenger, landing, out staging))
						continue;

					passengerStagingCells[passenger.ActorID] = staging;
				}

				if (passenger.IsIdle &&
					(passenger.CenterPosition - squad.World.Map.CenterOfCell(staging)).Length >
					WDist.FromCells(1).Length)
					squad.Bot.QueueOrder(new Order("Move", passenger, Target.FromCell(squad.World, staging), false));
			}
		}

		static bool TryFindPassengerStagingCell(Actor passenger, CPos landing, out CPos staging)
		{
			staging = landing;
			for (var radius = PassengerStagingMinCells; radius <= PassengerStagingMaxCells; radius++)
			{
				for (var dy = -radius; dy <= radius; dy++)
				{
					for (var dx = -radius; dx <= radius; dx++)
					{
						if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
							continue;

						var cell = landing + new CVec(dx, dy);
						if (!CanPassengerEnter(passenger, cell))
							continue;

						staging = cell;
						return true;
					}
				}
			}

			return false;
		}

		static bool CanPassengerEnter(Actor passenger, CPos cell)
		{
			var mobile = passenger.TraitOrDefault<Mobile>();
			return mobile != null && passenger.World.Map.Contains(cell) && mobile.CanEnterCell(cell);
		}
	}

	sealed class TransportLoadState : CNStateBase, ICNState
	{
		// Abort threshold: if no new passenger has boarded within this time, transition to safe
		// state. 3000 game ticks ≈ 120 seconds - raised from 1500 since APC squads were frequently
		// timing out and departing half-empty while production caught up.
		const int MaxLoadTicks = 3000;
		const int RallySearchRadius = 6;
		const int CongestionCheckCells = 2;

		int lastProgressTick;
		int lastBoardedCount;

		// Stable passenger → carrier assignment. Keeping it stable (instead of re-deriving a
		// round-robin from the shrinking idle list every tick) is what prevents trickle-in
		// passengers from all being re-routed to the same — possibly already full — carrier,
		// which left the other carriers half-empty when more than one transport was present.
		readonly Dictionary<uint, uint> passengerToCarrier = [];

		// Recompute assignments capacity-aware: each passenger goes to the carrier with the most
		// free space, and assignments to a full/dead carrier are dropped so the passenger is
		// re-routed to one that still has room. Passenger weight is treated as 1 (all transport
		// passengers are infantry, MaxWeight == seat count), which matches the CN templates.
		void AssignPassengersToCarriers(CNSquad squad)
		{
			var carriers = squad.CarrierUnits.Where(c => !c.IsDead && c.IsInWorld).ToList();
			if (carriers.Count == 0)
				return;

			var cargoByCarrier = carriers.ToDictionary(c => c.ActorID, c => c.TraitOrDefault<Cargo>());
			var passengers = squad.PassengerUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
			var livePassengers = passengers.Select(p => p.ActorID).ToHashSet();

			// Drop stale assignments: passenger gone/boarded, carrier gone, or carrier now full.
			foreach (var id in passengerToCarrier.Keys.ToList())
			{
				var carrierId = passengerToCarrier[id];
				var carrierGone = !cargoByCarrier.TryGetValue(carrierId, out var cargo) || cargo == null;

				// Occupancy, not HasSpace(): the seats this carrier's own still-walking passengers
				// have reserved must not count as "full", or every assignment would be dropped and
				// re-made every tick while the infantry is en route (see IsCarrierFull).
				if (!livePassengers.Contains(id) || carrierGone || IsCarrierFull(cargo))
					passengerToCarrier.Remove(id);
			}

			// Pending seats already claimed by still-walking (in-world, unboarded) passengers.
			var pending = carriers.ToDictionary(c => c.ActorID, _ => 0);
			foreach (var carrierId in passengerToCarrier.Values)
				if (pending.ContainsKey(carrierId))
					pending[carrierId]++;

			// Free capacity in WEIGHT, not passenger count: a mob-squad master boards with several
			// zero-weight slaves, so counting bodies makes a carrier look full long before it is.
			int Free(uint carrierId)
			{
				var cargo = cargoByCarrier[carrierId];
				if (cargo == null)
					return 0;

				var usedWeight = 0;
				foreach (var passenger in cargo.Passengers)
					usedWeight += passenger.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1;

				return cargo.Info.MaxWeight - usedWeight - pending[carrierId];
			}

			foreach (var passenger in passengers)
			{
				if (passengerToCarrier.ContainsKey(passenger.ActorID))
					continue;

				uint best = 0;
				var bestFree = 0;
				foreach (var carrier in carriers)
				{
					var free = Free(carrier.ActorID);
					if (free > bestFree)
					{
						bestFree = free;
						best = carrier.ActorID;
					}
				}

				if (bestFree <= 0)
					continue; // no carrier has room right now — retry next tick

				passengerToCarrier[passenger.ActorID] = best;
				pending[best]++;
			}
		}

		void IssueBoardingOrders(CNSquad squad, bool idleOnly)
		{
			var carrierById = squad.CarrierUnits
				.Where(c => !c.IsDead && c.IsInWorld)
				.ToDictionary(c => c.ActorID);

			foreach (var passenger in squad.PassengerUnits.Where(u => !u.IsDead && u.IsInWorld))
			{
				if (idleOnly && !passenger.IsIdle)
					continue;

				if (!passengerToCarrier.TryGetValue(passenger.ActorID, out var carrierId) ||
					!carrierById.TryGetValue(carrierId, out var carrier))
					continue;

				squad.Bot.QueueOrder(new Order("EnterTransport", passenger, Target.FromActor(carrier), false));
			}
		}

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = true;
			lastProgressTick = squad.World.WorldTick;
			lastBoardedCount = 0;
			passengerToCarrier.Clear();

			// Relocate to a nearby open cell before loading instead of stopping wherever
			// production left the carrier - that's often right at the war factory apron, a spot
			// congested with own traffic and buildings that makes it hard for recruited infantry
			// to path in and board. Air transports don't have this problem since they already
			// pick a clear landing cell (FindLandingCell); this is the ground equivalent. Falls
			// back to stopping in place if nothing clear is found nearby.
			var rallyCarrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead && u.IsInWorld);
			var rallyCell = rallyCarrier != null ? FindLoadRallyCell(squad, rallyCarrier) : null;

			foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld))
				squad.Bot.QueueOrder(rallyCell.HasValue
					? new Order("Move", carrier, Target.FromCell(squad.World, rallyCell.Value), false)
					: new Order("Stop", carrier, false));

			foreach (var escort in squad.EscortUnits.Where(u => !u.IsDead && u.IsInWorld))
				squad.Bot.QueueOrder(new Order("Stop", escort, false));

			// Issue boarding orders immediately (don't wait up to 75 ticks for first Tick).
			AssignPassengersToCarriers(squad);
			IssueBoardingOrders(squad, idleOnly: false);
		}

		// Finds the nearest cell within RallySearchRadius that's genuinely clear (no foreign actors
		// within CongestionCheckCells) for the carrier to park at while loading. Returns null if
		// nothing clear is found, so the caller can fall back to stopping in place.
		//
		// The squad's own units are excluded from the congestion count. Without that exclusion the
		// carrier counted *itself* — it is always within CongestionCheckCells of any candidate near
		// its own position — so radius 0 could never win and the transport always drove several
		// cells away, dragging the loading point out from under the infantry already walking to it.
		static CPos? FindLoadRallyCell(CNSquad squad, Actor carrier)
		{
			var mobile = carrier.TraitOrDefault<Mobile>();
			if (mobile == null)
				return null;

			var map = squad.World.Map;
			var origin = carrier.Location;
			var own = squad.Units.Concat(squad.PassengerUnits).ToHashSet();

			for (var radius = 0; radius <= RallySearchRadius; radius++)
			{
				for (var dy = -radius; dy <= radius; dy++)
				{
					for (var dx = -radius; dx <= radius; dx++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
							continue;

						var candidate = origin + new CVec(dx, dy);
						if (!map.Contains(candidate) || !mobile.CanEnterCell(candidate))
							continue;

						var congested = squad.World.FindActorsInCircle(
								map.CenterOfCell(candidate), WDist.FromCells(CongestionCheckCells))
							.Any(a => !own.Contains(a));
						if (!congested)
							return candidate;
					}
				}
			}

			return null;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Sanity check: if all carriers are dead, transition to done state instead of attack states.
			var validCarriers = squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
			if (validCarriers.Count == 0)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			var passengers = squad.PassengerUnits.Where(u => !u.IsDead).ToList();

			// Cargo aboard the carriers is the authoritative "loaded" signal:
			// passenger actors leave the world while boarded, so a passenger count
			// alone cannot tell "all aboard" apart from "none assigned".
			var anyCargo = validCarriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false);

			if (passengers.Count == 0 && !anyCargo)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportIdleState());
				return;
			}

			// Track boarding progress across all carriers: a slow-but-steady trickle of new
			// production shouldn't be treated the same as a genuinely stalled load. MaxLoadTicks
			// only fires once no new passenger has boarded for that whole duration, not from a
			// fixed load-start clock.
			var boardedCount = validCarriers.Sum(c => c.TraitOrDefault<Cargo>()?.PassengerCount ?? 0);
			if (boardedCount > lastBoardedCount)
			{
				lastBoardedCount = boardedCount;
				lastProgressTick = squad.World.WorldTick;
			}

			// Depart only when carriers are actually full (capacity reached), or every recruited
			// passenger is aboard AND we recruited the full complement. While still under capacity
			// we wait — the squad manager keeps topping up passengers (squad.AcceptingPassengers),
			// so transports leave full instead of with whatever boarded first. MaxLoadTicks is the
			// fail-safe for the case where the economy simply can't produce enough infantry.
			var waiting = passengers.Count(u => u.IsInWorld);
			var carriersFull = validCarriers.All(c => IsCarrierFull(c.TraitOrDefault<Cargo>()));
			var fullyRecruitedAndAboard =
				waiting == 0 && squad.RecruitedPassengerCount >= squad.DesiredPassengerCount;
			var allLoaded = anyCargo && (carriersFull || fullyRecruitedAndAboard);

			// Fully loaded with the wave system active: wait and roll out together with the next
			// attack wave instead of making a lone drop-run. The timeout path below still departs
			// solo if loading dragged on, so a stalled transport is never stuck.
			if (allLoaded && squad.SquadManager.Info.AttackWaveEnabled)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportWaveSyncState());
				return;
			}

			if (allLoaded)
			{
				if (squad.Type == CNSquadType.AirTransport)
					squad.FuzzyStateMachine.ChangeState(squad, new AirTransportAttackMoveState());
				else
					squad.FuzzyStateMachine.ChangeState(squad, new TransportAttackMoveState());

				return;
			}

			if (squad.World.WorldTick - lastProgressTick >= MaxLoadTicks)
			{
				// Timeout may send partial cargo, but never an empty transport.
				squad.FuzzyStateMachine.ChangeState(squad,
					anyCargo ? new TransportAttackMoveState() : new TransportIdleState());
				return;
			}

			// Re-assign capacity-aware (re-routing passengers off full/dead carriers) and re-issue
			// only to idle passengers — those still walking keep their order so we don't restart
			// their boarding walk.
			AssignPassengersToCarriers(squad);
			IssueBoardingOrders(squad, idleOnly: true);
		}

		public void Deactivate(CNSquad squad) { }
	}

	// ---------------------------------------------------------------------------
	// Wave sync: a fully-loaded transport parks at base and waits for the squad
	// manager to launch the next attack wave, then rolls out together with it and
	// drops on the wave's target — so the cargo joins the assault instead of making
	// a lone, easily-picked-off drop-run. Falls back to a solo run after a timeout so
	// a loaded transport never idles indefinitely (e.g. when waves are infrequent).
	// ---------------------------------------------------------------------------
	sealed class TransportWaveSyncState : CNStateBase, ICNState
	{
		// Max ticks to wait for a wave before going solo. Most profiles launch waves more
		// often than this; slow profiles (turtle/tech) may occasionally fall back to a solo run.
		const int MaxWaveWaitTicks = 1800;

		int waitStartTick;

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = false;
			waitStartTick = squad.World.WorldTick;

			// Keep carriers parked at base while waiting for the wave.
			foreach (var carrier in squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld))
				squad.Bot.QueueOrder(new Order("Stop", carrier, false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			var carriers = squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
			if (carriers.Count == 0)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			// Lost the cargo while waiting (passengers killed/unloaded) — nothing to deliver.
			var anyCargo = carriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false);
			if (!anyCargo)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
				return;
			}

			var waveGo = squad.SquadManager.IsWaveLaunched;
			var timedOut = squad.World.WorldTick - waitStartTick >= MaxWaveWaitTicks;
			if (!waveGo && !timedOut)
				return;

			// Roll out timed with the wave, but pick our own drop target: the wave keeps the enemy's
			// front busy while the transport slips its cargo into the soft rear (see FindRearDropTarget).
			if (squad.Type == CNSquadType.AirTransport)
				squad.FuzzyStateMachine.ChangeState(squad, new AirTransportAttackMoveState());
			else
				squad.FuzzyStateMachine.ChangeState(squad, new TransportAttackMoveState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class TransportAttackMoveState : CNStateBase, ICNTimeCriticalState
	{
		const int DropSearchMinCells = 4;
		const int DropSearchMaxCells = 10;
		const int DropArriveRangeCells = 3;

		// Fail-safe: if carriers arrive at LZ but never transition to unload (e.g., stuck on idle),
		// force the unload state after this many ticks of being at/near the drop zone.
		const int MaxLZIdleTicks = 180; // ~12 seconds at 15 ticks/sec

		// Threat relaxation: when already at/near the LZ, reduce threat score so a nearly-finished
		// transport doesn't get stuck in "hesitation" where a tiny threat prevents unload.
		const int LzThreatRelaxFactor = 2; // divide threat by this factor near LZ
		CPos? dropCell;
		Actor lastTarget;

		int lzIdleStartTick;

		// Per-carrier offset cells around the anchor dropCell so multi-carrier squads don't all
		// converge on the same tile (which causes pathing collisions and "any-arrived" early
		// transitions that leave stragglers unloading mid-route).
		readonly Dictionary<uint, CPos> carrierDropCells = [];

		// Ring offsets around the anchor in stepping-stone order: lead carrier takes the anchor,
		// followers fan out two cells in cardinal then diagonal directions before tightening up.
		static readonly CVec[] CarrierOffsets =
		[
			new(0, 0),
			new(2, 0), new(-2, 0), new(0, 2), new(0, -2),
			new(2, 2), new(-2, -2), new(2, -2), new(-2, 2),
			new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
		];

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = false;
			dropCell = null;
			lastTarget = null;
			lzIdleStartTick = 0;
			carrierDropCells.Clear();
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Fail-safe idle check: if the transport has been at/near the LZ for too long
			// with passengers still aboard, force unload to prevent permanent idle.
			// IsInWorld matters here: a carrier that has left the world (a SAPC burrowed into a
			// tunnel, a carrier being transported) keeps reporting its last CenterPosition, which
			// would pin the "all arrived" check to a stale spot forever.
			var lzCarriers = squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
			if (dropCell.HasValue && lzCarriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false))
			{
				var lzTargetPos = squad.World.Map.CenterOfCell(dropCell.Value);
				var nearLZ = lzCarriers.All(c => (c.CenterPosition - lzTargetPos).Length <= WDist.FromCells(DropArriveRangeCells + 2).Length);
				if (nearLZ)
				{
					if (lzIdleStartTick == 0)
						lzIdleStartTick = squad.World.WorldTick;

					if (squad.World.WorldTick - lzIdleStartTick >= MaxLZIdleTicks)
					{
						squad.FuzzyStateMachine.ChangeState(squad, new TransportUnloadState());
						return;
					}
				}
				else
				{
					lzIdleStartTick = 0;
				}
			}

			// Find a drop-off target if we don't have one yet (squad starts without a target)
			if (!squad.IsTargetValid)
			{
				var carrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead && u.IsInWorld);
				if (carrier == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
					return;
				}

				var dropTarget = FindRearDropTarget(squad, carrier);
				if (dropTarget == null)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
					return;
				}

				squad.SetActorToTarget(dropTarget);
			}

			var leadCarrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead && u.IsInWorld);
			if (leadCarrier == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
				return;
			}

			if (squad.TargetActor != lastTarget || !dropCell.HasValue)
			{
				dropCell = FindSaferDropCell(squad, leadCarrier, squad.TargetActor);
				lastTarget = squad.TargetActor;
				carrierDropCells.Clear();
			}

			// Emergency unload: if any carrier is critically damaged, drop passengers now.
			var carriers = squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
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
			var unloadRangeSq = (long)unloadRange.Length * unloadRange.Length;

			if (dropCell.HasValue)
				AssignPerCarrierDropCells(squad, carriers, dropCell.Value, leadCarrier);

			// Arrival: ALL carriers within unloadRange of their assigned cell. Single-carrier
			// squads behave identically to the previous "any-arrived" check; multi-carrier
			// squads now wait for stragglers so unloads happen at the LZ, not en route.
			var allArrived = carriers.Count > 0;
			foreach (var carrier in carriers)
			{
				var arrivalPos = ResolveCarrierTargetPos(squad, carrier, targetPos);
				if ((carrier.CenterPosition - arrivalPos).LengthSquared > unloadRangeSq)
				{
					allArrived = false;
					break;
				}
			}

			if (allArrived)
			{
				lzIdleStartTick = 0;
				squad.FuzzyStateMachine.ChangeState(squad, new TransportUnloadState());
				return;
			}

			foreach (var carrier in carriers)
			{
				if (!carrier.IsIdle)
					continue;

				Target moveTarget;
				if (carrierDropCells.TryGetValue(carrier.ActorID, out var assigned))
					moveTarget = Target.FromCell(squad.World, assigned);
				else if (dropCell.HasValue)
					moveTarget = Target.FromCell(squad.World, dropCell.Value);
				else
					moveTarget = squad.Target;

				// Use Move (not AttackMove) so the carrier ignores enemies and drives
				// directly to the drop-off point instead of stopping to fight.
				squad.Bot.QueueOrder(new Order("Move", carrier, moveTarget, false));
			}

			// Escorts AttackMove to the drop zone — they fight enemies en route and arrive combat-ready.
			if (dropCell.HasValue)
			{
				var escortTarget = Target.FromCell(squad.World, dropCell.Value);
				foreach (var escort in squad.EscortUnits.Where(e => !e.IsDead && e.IsInWorld && e.IsIdle))
					squad.Bot.QueueOrder(new Order("AttackMove", escort, escortTarget, false));
			}
		}

		public void Deactivate(CNSquad squad) { }

		void AssignPerCarrierDropCells(CNSquad squad, List<Actor> carriers, CPos anchor, Actor leadCarrier)
		{
			if (carriers.Count == 0)
				return;

			// Stale entries (dead carriers) are pruned implicitly — only live carriers ever read back.
			var allAssigned = carriers.All(c => carrierDropCells.ContainsKey(c.ActorID));
			if (allAssigned)
				return;

			var mobile = leadCarrier?.TraitOrDefault<Mobile>();
			var map = squad.World.Map;

			var slot = 0;
			foreach (var carrier in carriers)
			{
				if (carrierDropCells.ContainsKey(carrier.ActorID))
				{
					slot++;
					continue;
				}

				var assigned = anchor;
				for (var o = 0; o < CarrierOffsets.Length; o++)
				{
					var candidate = anchor + CarrierOffsets[(slot + o) % CarrierOffsets.Length];
					if (!map.Contains(candidate))
						continue;
					if (mobile != null && !mobile.CanEnterCell(candidate))
						continue;

					assigned = candidate;
					break;
				}

				carrierDropCells[carrier.ActorID] = assigned;
				slot++;
			}
		}

		WPos ResolveCarrierTargetPos(CNSquad squad, Actor carrier, WPos fallbackPos)
		{
			if (carrierDropCells.TryGetValue(carrier.ActorID, out var cell))
				return squad.World.Map.CenterOfCell(cell);

			return fallbackPos;
		}

		// A transport's value is hitting the soft rear (economy/production), not the defended front
		// the wave is busy with. Prefer high-value, non-defense buildings; among equals, prefer the
		// one deepest in enemy territory (farthest from our base). The drop CELL is then placed on
		// the far side of that target (see ScoreDropCell), so units land behind the building.
		static Actor FindRearDropTarget(CNSquad squad, Actor carrier)
		{
			var mgr = squad.SquadManager;
			var ourBasePos = squad.World.Map.CenterOfCell(mgr.GetRandomBaseCenter());

			// Restrict to the player the attack wave is engaging, so "soft rear" means the rear of
			// THAT enemy. Without this the scan ran over every enemy on the map, and the depth
			// tiebreaker below (farther from our base scores higher) actively pulled the drop toward
			// a more distant, uninvolved opponent — the transport went off and attacked someone else
			// entirely while the rest of the army focused a different player.
			// No wave running (or its target already dead): fall back to considering everyone.
			var waveTarget = mgr.WaveTarget;
			var focusOwner = waveTarget != null && !waveTarget.IsDead && waveTarget.IsInWorld
				? waveTarget.Owner
				: null;

			var best = ScoreRearTargets(mgr, ourBasePos, focusOwner);
			if (best == null && focusOwner != null)
				best = ScoreRearTargets(mgr, ourBasePos, null);

			// Final fallbacks: any undefended building, then the closest building of any kind.
			return best
				?? CNSquadHelper.FindUnprotectedTarget(squad)
				?? mgr.FindClosestEnemyBuilding(carrier);
		}

		// Highest-value non-defense building, optionally limited to a single owner. Null if none match.
		static Actor ScoreRearTargets(CNSquadManagerBotModule mgr, WPos ourBasePos, Player owner)
		{
			Actor best = null;
			var bestScore = int.MinValue;

			foreach (var building in mgr.GetCachedEnemyBuildings())
			{
				if (!mgr.IsLiveEnemyActor(building))
					continue;

				if (owner != null && building.Owner != owner)
					continue;

				var caps = building.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;

				// Never drop onto defenses — that is the front, exactly what we want to bypass.
				if (caps?.Contains("Defense") ?? false)
					continue;

				var score = 0;
				if (caps != null)
				{
					if (caps.Contains("Superweapon")) score += 600;
					if (caps.Contains("Tech")) score += 500;
					if (caps.Contains("Economy")) score += 450;
					if (caps.Contains("Production")) score += 350;
					if (caps.Contains("Power")) score += 200;
				}

				// Depth tiebreaker: deeper = farther from our base = more "behind the lines".
				score += (building.CenterPosition - ourBasePos).Length / 1024;

				if (score > bestScore)
				{
					bestScore = score;
					best = building;
				}
			}

			return best;
		}

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

			// Apply threat relaxation near LZ: when already at the drop zone, a tiny threat
			// shouldn't prevent the transport from completing its unload.
			foreach (var actor in world.FindActorsInCircle(candidatePos, WDist.FromCells(6)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor))
					continue;

				var isBuilding = actor.Info.HasTraitInfo<BuildingInfo>();
				var canAttack = actor.Info.HasTraitInfo<AttackBaseInfo>();

				if (isBuilding && canAttack)
					score += 350 / LzThreatRelaxFactor;
				else if (canAttack)
					score += 80 / LzThreatRelaxFactor;
				else if (isBuilding)
					score -= 10;
			}

			foreach (var actor in world.FindActorsInCircle(targetPos, WDist.FromCells(4)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor))
					continue;

				if (!actor.Info.HasTraitInfo<BuildingInfo>())
					continue;

				if ((actor.CenterPosition - candidatePos).LengthSquared <=
					(long)WDist.FromCells(6).Length * WDist.FromCells(6).Length)
					score += 80 / LzThreatRelaxFactor;
			}

			return score;
		}
	}

	sealed class AirTransportAttackMoveState : CNStateBase, ICNTimeCriticalState
	{
		const int DropSearchMinCells = 5;
		const int DropSearchMaxCells = 13;
		const int DropArriveRangeCells = 3;
		const int ThreatScanCells = 7;

		// Loiter fail-safe: if the lead carrier hasn't made horizontal progress for
		// this many ticks while still carrying cargo, it is hovering over/near the
		// LZ — force the unload rather than circling forever.
		// Game ticks, not update cycles.
		const int MaxLoiterTicks = 4500;
		const int MinProgressDistance = 512; // sub-cell; movement below this counts as "not moving"

		// Threat relaxation: when already at/near the LZ, reduce threat score so a nearly-finished
		// transport doesn't get stuck in "hesitation" where a tiny threat prevents unload.
		const int LzThreatRelaxFactor = 2; // divide threat by this factor near LZ

		CPos? dropCell;
		Actor lastTarget;
		WPos lastLeadPos;
		int loiterTicks;

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = false;
			dropCell = null;
			lastTarget = null;
			lastLeadPos = WPos.Zero;
			loiterTicks = 0;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var leadCarrier = squad.CarrierUnits.FirstOrDefault(u => !u.IsDead && u.IsInWorld);
			if (leadCarrier == null)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
				return;
			}

			if (!squad.IsTargetValid || !dropCell.HasValue)
			{
				if (!TryFindAirDropPlan(squad, leadCarrier, out var dropTarget, out dropCell))
				{
					squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
					return;
				}

				squad.SetActorToTarget(dropTarget);
				lastTarget = dropTarget;
			}

			if (squad.TargetActor != lastTarget)
			{
				dropCell = FindSafeAirDropCell(squad, leadCarrier, squad.TargetActor);
				lastTarget = squad.TargetActor;

				if (!dropCell.HasValue)
				{
					squad.SetActorToTarget(null);
					squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
					return;
				}
			}

			var carriers = squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
			if (carriers.Any(c => c.TraitOrDefault<IHealth>()?.DamageState >= DamageState.Heavy))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
				return;
			}

			if (carriers.Count == 0)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportReturnState());
				return;
			}

			var targetPos = squad.World.Map.CenterOfCell(dropCell.Value);
			var unloadRange = WDist.FromCells(DropArriveRangeCells);
			var unloadRangeSq = (long)unloadRange.Length * unloadRange.Length;

			// Horizontal-only distance: an airborne carrier's Z is its cruise
			// altitude, so a 3-D check would never fall under the threshold and
			// the transport would circle the LZ forever without unloading.
			foreach (var carrier in carriers)
			{
				if (HorizontalLengthSquared(carrier.CenterPosition - targetPos) <= unloadRangeSq)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new AirTransportUnloadState(dropCell.Value));
					return;
				}
			}

			// Loiter fail-safe: if the lead carrier stops making horizontal
			// progress while still carrying cargo, it has effectively arrived
			// (hovering over the LZ) — unload instead of looping.
			var lead = carriers[0];
			var moved = HorizontalLengthSquared(lead.CenterPosition - lastLeadPos)
				> (long)MinProgressDistance * MinProgressDistance;
			lastLeadPos = lead.CenterPosition;

			if (moved)
				loiterTicks = 0;
			else if ((loiterTicks += squad.TicksSinceLastUpdate) >= MaxLoiterTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new AirTransportUnloadState(dropCell.Value));
				return;
			}

			foreach (var carrier in carriers)
				if (carrier.IsIdle)
					squad.Bot.QueueOrder(new Order("Move", carrier, Target.FromCell(squad.World, dropCell.Value), false));
		}

		public void Deactivate(CNSquad squad) { }

		static bool TryFindAirDropPlan(CNSquad squad, Actor carrier, out Actor target, out CPos? dropCell)
		{
			target = null;
			dropCell = null;

			var bestScore = int.MaxValue;
			foreach (var candidate in squad.SquadManager.GetCachedEnemyBuildings().Where(IsCriticalDropTarget))
			{
				var candidateDropCell = FindSafeAirDropCell(squad, carrier, candidate);
				if (!candidateDropCell.HasValue)
					continue;

				var score = ScoreAirDropTarget(squad, carrier, candidate) +
					ScoreAirDropCell(squad, carrier, squad.World.Map.CellContaining(candidate.CenterPosition),
						squad.SquadManager.GetRandomBaseCenter(), candidateDropCell.Value);

				if (score >= bestScore)
					continue;

				bestScore = score;
				target = candidate;
				dropCell = candidateDropCell;
			}

			// Fallback: no capability-tagged target had a landable approach. Use
			// the nearest enemy building with a safe drop cell so a loaded
			// transport still engages instead of idling at base forever.
			if (target == null)
			{
				foreach (var candidate in squad.SquadManager.GetCachedEnemyBuildings()
					.OrderBy(b => (b.CenterPosition - carrier.CenterPosition).LengthSquared))
				{
					var fallbackCell = FindSafeAirDropCell(squad, carrier, candidate);
					if (!fallbackCell.HasValue)
						continue;

					target = candidate;
					dropCell = fallbackCell;
					break;
				}
			}

			return target != null;
		}

		static bool IsCriticalDropTarget(Actor actor)
		{
			var caps = actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			return caps != null &&
				(caps.Contains("Economy") ||
				 caps.Contains("Production") ||
				 caps.Contains("Tech") ||
				 caps.Contains("Superweapon") ||
				 caps.Contains("Power"));
		}

		static int ScoreAirDropTarget(CNSquad squad, Actor carrier, Actor target)
		{
			var score = (int)((carrier.CenterPosition - target.CenterPosition).LengthSquared / 65536);

			var caps = target.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (caps != null)
			{
				if (caps.Contains("Superweapon"))
					score -= 900;
				if (caps.Contains("Tech"))
					score -= 700;
				if (caps.Contains("Production"))
					score -= 550;
				if (caps.Contains("Economy"))
					score -= 450;
				if (caps.Contains("Power"))
					score -= 250;
			}

			// Apply threat relaxation near LZ: when already at the drop zone, a tiny threat
			// shouldn't prevent the transport from completing its unload.
			var threatScore = ScoreAirThreatAt(squad, carrier, target.CenterPosition, ThreatScanCells + 3);
			score += threatScore / LzThreatRelaxFactor;
			return score;
		}

		static CPos? FindSafeAirDropCell(CNSquad squad, Actor carrier, Actor target)
		{
			if (target == null)
				return null;

			var aircraft = carrier.TraitOrDefault<Aircraft>();
			if (aircraft == null)
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
					if (!aircraft.CanLand(cell))
						continue;

					var score = ScoreAirDropCell(squad, carrier, targetCell, ourBaseCell, cell);
					if (score >= bestScore)
						continue;

					bestScore = score;
					bestCell = cell;
				}
			}

			return bestCell;
		}

		static int ScoreAirDropCell(CNSquad squad, Actor carrier, CPos targetCell, CPos ourBaseCell, CPos candidate)
		{
			var world = squad.World;
			var candidatePos = world.Map.CenterOfCell(candidate);
			var score = 0;

			score += (candidate - targetCell).LengthSquared * 12;

			var baseToCandidate = (candidate - ourBaseCell).LengthSquared;
			var baseToTarget = (targetCell - ourBaseCell).LengthSquared;
			if (baseToCandidate < baseToTarget)
				score += (baseToTarget - baseToCandidate) * 4;

			// Apply threat relaxation near LZ: when already at the drop zone, a tiny threat
			// shouldn't prevent the transport from completing its unload.
			var threatScore = ScoreAirThreatAt(squad, carrier, candidatePos, ThreatScanCells);
			score += threatScore / LzThreatRelaxFactor;
			return score;
		}

		static int ScoreAirThreatAt(CNSquad squad, Actor carrier, WPos position, int radiusCells)
		{
			var score = 0;
			foreach (var actor in squad.World.FindActorsInCircle(position, WDist.FromCells(radiusCells)))
			{
				if (!squad.SquadManager.IsLiveEnemyActor(actor))
					continue;

				var caps = actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
				var isDefense = caps?.Contains("Defense") ?? false;
				var isAntiAir = (caps?.Contains("AntiAir") ?? false) || (caps?.Contains("AADefense") ?? false);
				var isBuilding = actor.Info.HasTraitInfo<BuildingInfo>();
				var canAttackCarrier = actor.Info.HasTraitInfo<AttackBaseInfo>() && CanAttackTarget(actor, carrier);

				if (isAntiAir || canAttackCarrier)
					score += isBuilding ? 2200 : 900;
				else if (isDefense)
					score += isBuilding ? 800 : 250;
				else if (isBuilding && actor.Info.HasTraitInfo<AttackBaseInfo>())
					score += 450;
			}

			return score;
		}
	}

	// ---------------------------------------------------------------------------
	// Air-transport unload: an airborne carrier hovering at cruise altitude over the LZ
	// reports IsIdle == false (it is running FlyIdle), so the idle-gated Unload order of the
	// ground TransportUnloadState never fired and the transport sat fully loaded at the LZ
	// forever. Here we explicitly Land at the drop cell first; a landed aircraft becomes idle
	// and CanUnload, so the Unload order goes through and the cargo is actually dropped.
	// ---------------------------------------------------------------------------
	sealed class AirTransportUnloadState : CNStateBase, ICNTimeCriticalState
	{
		// Timeout: 600 game ticks ≈ 24 seconds — force-drop and bail if stuck.
		const int MaxUnloadWaitTicks = 600;

		readonly CPos dropCell;
		int unloadStartTick;

		public AirTransportUnloadState(CPos dropCell) { this.dropCell = dropCell; }

		public void Activate(CNSquad squad)
		{
			unloadStartTick = squad.World.WorldTick;

			// Land on the (landable) drop cell. Once down, the carrier is idle and can unload.
			foreach (var carrier in squad.CarrierUnits.Where(c => !c.IsDead && c.IsInWorld))
				squad.Bot.QueueOrder(new Order("Land", carrier, Target.FromCell(squad.World, dropCell), false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var carriers = squad.CarrierUnits.Where(c => !c.IsDead && c.IsInWorld).ToList();
			var anyCargo = carriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false);

			// Cargo dropped: capture-and-sell drops hand the engineers to the capture mission.
			// Regular drops commit the unloaded passengers to the raid instead of releasing them
			// into the normal idle/flee pool.
			if (!anyCargo)
			{
				squad.FuzzyStateMachine.ChangeState(squad,
					squad.TemplateInfo?.CaptureAndSell == true
						? new EngineerCaptureState()
						: new TransportDropAssaultState());
				return;
			}

			// Fail-safe: force the drop and hand over to the raid state if we have been stuck too long.
			if (squad.World.WorldTick - unloadStartTick >= MaxUnloadWaitTicks)
			{
				foreach (var carrier in carriers.Where(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false))
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));

				squad.FuzzyStateMachine.ChangeState(squad,
					squad.TemplateInfo?.CaptureAndSell == true
						? new EngineerCaptureState()
						: new TransportDropAssaultState());
				return;
			}

			foreach (var carrier in carriers)
			{
				if (carrier.TraitOrDefault<Cargo>()?.IsEmpty() != false || !carrier.IsIdle)
					continue;

				// Idle + airborne: the Land hasn't landed it yet (or it took off again) — re-issue Land.
				// Idle + landed: unload now (the engine lands-in-place, drops, then takes off).
				var aircraft = carrier.TraitOrDefault<Aircraft>();
				if (aircraft != null && !aircraft.AtLandAltitude)
					squad.Bot.QueueOrder(new Order("Land", carrier, Target.FromCell(squad.World, dropCell), false));
				else
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));
			}

			// Escorts attack the nearest enemy while passengers unload.
			var escortEnemy = FindClosestEnemyUnit(squad);
			if (escortEnemy != null)
			{
				foreach (var escort in squad.EscortUnits.Where(e => !e.IsDead && e.IsInWorld && e.IsIdle))
					squad.Bot.QueueOrder(new Order("Attack", escort, Target.FromActor(escortEnemy), false));
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class TransportUnloadState : CNStateBase, ICNTimeCriticalState
	{
		// Timeout: 600 game ticks ≈ 24 seconds — abort if stuck.
		const int MaxUnloadWaitTicks = 600;

		// Periodic nudge interval: re-issue Unload order every N ticks to "nudge" the engine's activity queue.
		const int NudgeIntervalTicks = 100;

		int unloadStartTick;
		int lastNudgeTick;

		public void Activate(CNSquad squad)
		{
			unloadStartTick = squad.World.WorldTick;
			lastNudgeTick = squad.World.WorldTick;

			// Order the unload here rather than waiting for the first Tick. Entering this state is
			// itself the decision that the carriers have arrived, so deferring the order cost a full
			// update interval — that, on top of the interval the arrival check itself ran on, is why
			// a loaded APC visibly sat at the drop point before anything happened.
			foreach (var carrier in squad.CarrierUnits.Where(c => !c.IsDead && c.IsInWorld))
				if (carrier.TraitOrDefault<Cargo>()?.IsEmpty() == false)
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var carriers = squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
			var anyCargo = carriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false);

			// If no cargo remains, transition out. Capture-and-sell drops hand the just-unloaded
			// engineers to the capture mission; regular drops keep the passengers attacking.
			if (!anyCargo)
			{
				squad.FuzzyStateMachine.ChangeState(squad,
					squad.TemplateInfo?.CaptureAndSell == true
						? new EngineerCaptureState()
						: new TransportDropAssaultState());
				return;
			}

			// Periodic nudge: if a carrier is idle but still has passengers, re-issue Unload order
			// every NudgeIntervalTicks to try and "nudge" the engine's activity queue.
			if (squad.World.WorldTick - lastNudgeTick >= NudgeIntervalTicks)
			{
				lastNudgeTick = squad.World.WorldTick;
				foreach (var carrier in carriers.Where(c => c.IsIdle && c.TraitOrDefault<Cargo>()?.IsEmpty() == false))
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));
			}

			// Timeout: if the transport has been at the LZ for too long with passengers still aboard,
			// force unload on all carriers and hand control to the drop mission.
			if (squad.World.WorldTick - unloadStartTick >= MaxUnloadWaitTicks)
			{
				foreach (var carrier in carriers.Where(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false))
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));

				squad.FuzzyStateMachine.ChangeState(squad,
					squad.TemplateInfo?.CaptureAndSell == true
						? new EngineerCaptureState()
						: new TransportDropAssaultState());
				return;
			}

			// Standard idle check: re-issue Unload to carriers that became idle.
			foreach (var carrier in carriers)
			{
				if (carrier.IsIdle && carrier.TraitOrDefault<Cargo>()?.IsEmpty() == false)
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));
			}

			// Escorts attack the nearest enemy while passengers unload.
			var escortEnemy = FindClosestEnemyUnit(squad);
			if (escortEnemy != null)
			{
				foreach (var escort in squad.EscortUnits.Where(e => !e.IsDead && e.IsInWorld && e.IsIdle))
					squad.Bot.QueueOrder(new Order("Attack", escort, Target.FromActor(escortEnemy), false));
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	// ---------------------------------------------------------------------------
	// Drop assault: regular APC/SAPC/Orca transport passengers commit to the raid
	// after unloading. They deliberately skip the normal ground flee state so the
	// drop does not immediately run home after the carriers leave.
	// ---------------------------------------------------------------------------
	sealed class TransportDropAssaultState : CNStateBase, ICNState
	{
		const int MaxMissionTicks = 1500; // game ticks, ~60 seconds
		const int CarrierReturnReissueTicks = 150;
		const int PassengerOrderReissueTicks = 75;
		const int CarrierHomeRangeCells = 10;

		int startTick;
		int lastCarrierReturnTick;
		int lastPassengerOrderTick;
		CPos returnCell;
		readonly HashSet<uint> adoptedPassengers = [];

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = false;
			startTick = squad.World.WorldTick;
			lastCarrierReturnTick = squad.World.WorldTick - CarrierReturnReissueTicks;
			lastPassengerOrderTick = squad.World.WorldTick - PassengerOrderReissueTicks;
			returnCell = squad.SquadManager.GetRandomBaseCenter();
			adoptedPassengers.Clear();

			AdoptPassengers(squad);
			IssueCarrierReturn(squad, force: true);
		}

		public void Tick(CNSquad squad)
		{
			AdoptPassengers(squad);

			var carriers = squad.CarrierUnits.Where(c => !c.IsDead && c.IsInWorld).ToList();
			var passengers = squad.PassengerUnits.Where(p => !p.IsDead && p.IsInWorld).ToList();
			var anyCargo = carriers.Any(c => c.TraitOrDefault<Cargo>()?.IsEmpty() == false);

			if (anyCargo)
				IssueRemainingUnload(squad, carriers);

			IssueCarrierReturn(squad, force: false);

			if (passengers.Count == 0 && !anyCargo)
			{
				if (carriers.Count == 0 || CarriersHome(squad, carriers))
					squad.SquadManager.UnregisterSquad(squad);

				return;
			}

			if (passengers.Count > 0)
				OrderPassengers(squad, passengers);

			if (squad.World.WorldTick - startTick >= MaxMissionTicks)
				squad.SquadManager.UnregisterSquad(squad);
		}

		public void Deactivate(CNSquad squad) { }

		void AdoptPassengers(CNSquad squad)
		{
			foreach (var passenger in squad.PassengerUnits.Where(p => !p.IsDead && p.IsInWorld))
			{
				if (!adoptedPassengers.Add(passenger.ActorID))
					continue;

				squad.Units.Add(passenger);
			}
		}

		void IssueCarrierReturn(CNSquad squad, bool force)
		{
			if (!force && squad.World.WorldTick - lastCarrierReturnTick < CarrierReturnReissueTicks)
				return;

			lastCarrierReturnTick = squad.World.WorldTick;
			var target = Target.FromCell(squad.World, returnCell);

			foreach (var carrier in squad.CarrierUnits.Where(c => !c.IsDead && c.IsInWorld))
			{
				var cargo = carrier.TraitOrDefault<Cargo>();
				if (cargo != null && !cargo.IsEmpty())
					continue;

				squad.Bot.QueueOrder(new Order("Move", carrier, target, false));
			}

			foreach (var escort in squad.EscortUnits.Where(e => !e.IsDead && e.IsInWorld))
				squad.Bot.QueueOrder(new Order("Move", escort, target, false));
		}

		static void IssueRemainingUnload(CNSquad squad, IEnumerable<Actor> carriers)
		{
			foreach (var carrier in carriers)
			{
				var cargo = carrier.TraitOrDefault<Cargo>();
				if (cargo == null || cargo.IsEmpty() || !carrier.IsIdle)
					continue;

				var aircraft = carrier.TraitOrDefault<Aircraft>();
				if (aircraft != null && !aircraft.AtLandAltitude)
					squad.Bot.QueueOrder(new Order("Land", carrier, Target.FromCell(squad.World, carrier.Location), false));
				else
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));
			}
		}

		void OrderPassengers(CNSquad squad, List<Actor> passengers)
		{
			if (squad.World.WorldTick - lastPassengerOrderTick < PassengerOrderReissueTicks)
				return;

			var target = FindPassengerTarget(squad, passengers);
			if (target == null)
				return;

			lastPassengerOrderTick = squad.World.WorldTick;
			squad.SetActorToTarget(target);
			foreach (var passenger in passengers)
			{
				if (BusyAttack(passenger) && !passenger.IsIdle)
					continue;

				squad.Bot.QueueOrder(new Order("AttackMove", passenger, Target.FromActor(target), false));
			}
		}

		static Actor FindPassengerTarget(CNSquad squad, List<Actor> passengers)
		{
			var center = AveragePosition(passengers);
			Actor best = null;
			var bestScore = long.MaxValue;

			foreach (var candidate in squad.World.FindActorsInCircle(center,
				WDist.FromCells(squad.SquadManager.Info.AttackScanRadius)))
				ConsiderCandidate(squad, passengers, candidate, center, ref best, ref bestScore);

			foreach (var candidate in squad.SquadManager.GetCachedEnemyBuildings())
				ConsiderCandidate(squad, passengers, candidate, center, ref best, ref bestScore);

			if (best == null)
			{
				foreach (var passenger in passengers)
				{
					best = squad.SquadManager.FindClosestEnemy(passenger,
						WDist.FromCells(squad.SquadManager.Info.AttackScanRadius * 2),
						a => CanAttackTarget(passenger, a));
					if (best != null)
						break;
				}
			}

			return best;
		}

		static void ConsiderCandidate(
			CNSquad squad,
			List<Actor> passengers,
			Actor candidate,
			WPos center,
			ref Actor best,
			ref long bestScore)
		{
			if (candidate == null || candidate.IsDead || !candidate.IsInWorld)
				return;

			if (!squad.SquadManager.IsLiveEnemyActor(candidate) || candidate.Info.HasTraitInfo<LineBuildInfo>())
				return;

			if (!candidate.Info.HasTraitInfo<BuildingInfo>() && !candidate.CanBeViewedByPlayer(squad.Bot.Player))
				return;

			if (!passengers.Any(p => CanAttackTarget(p, candidate)))
				return;

			var score = (candidate.CenterPosition - center).LengthSquared - TargetValueBonus(candidate);
			if (score >= bestScore)
				return;

			bestScore = score;
			best = candidate;
		}

		static long TargetValueBonus(Actor candidate)
		{
			var caps = candidate.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (caps == null)
				return 0;

			var bonusCells = 0;
			if (caps.Contains("Superweapon")) bonusCells += 10;
			if (caps.Contains("Tech")) bonusCells += 8;
			if (caps.Contains("Production")) bonusCells += 7;
			if (caps.Contains("Economy")) bonusCells += 6;
			if (caps.Contains("Power")) bonusCells += 3;
			if (caps.Contains("Defense")) bonusCells -= 4;

			var sign = bonusCells < 0 ? -1 : 1;
			var bonus = WDist.FromCells(Math.Abs(bonusCells)).Length;
			return sign * (long)bonus * bonus;
		}

		static WPos AveragePosition(List<Actor> actors)
		{
			long x = 0;
			long y = 0;
			long z = 0;

			foreach (var actor in actors)
			{
				x += actor.CenterPosition.X;
				y += actor.CenterPosition.Y;
				z += actor.CenterPosition.Z;
			}

			return new WPos((int)(x / actors.Count), (int)(y / actors.Count), (int)(z / actors.Count));
		}

		bool CarriersHome(CNSquad squad, List<Actor> carriers)
		{
			var returnPos = squad.World.Map.CenterOfCell(returnCell);
			var homeRange = WDist.FromCells(CarrierHomeRangeCells);
			var homeRangeSq = (long)homeRange.Length * homeRange.Length;

			return carriers.All(c => HorizontalLengthSquared(c.CenterPosition - returnPos) <= homeRangeSq);
		}
	}

	sealed class TransportReturnState : CNStateBase, ICNState
	{
		// Cleanup timeout: if carriers are stuck in a return loop but cannot reach base
		// (e.g., pathfinding blocked) for more than this many ticks, force release units.
		const int MaxReturnWaitTicks = 1200; // game ticks, ~48 seconds

		// Grace before a non-idle carrier is told to unload anyway, in game ticks.
		const int UnloadGraceTicks = 375;
		bool returnIssued;
		int unloadAttemptTicks;
		int returnStartTick;
		CPos returnCell;

		public void Activate(CNSquad squad)
		{
			returnIssued = false;
			unloadAttemptTicks = 0;
			returnStartTick = squad.World.WorldTick;
			returnCell = CPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			// Sanity check: if no valid carriers remain, release units immediately.
			var validCarriers = squad.CarrierUnits.Where(u => !u.IsDead && u.IsInWorld).ToList();
			if (validCarriers.Count == 0)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			// Cleanup timeout: if carriers are stuck in a return loop for too long,
			// force transition to done state to release units back to the manager pool.
			if (squad.World.WorldTick - returnStartTick >= MaxReturnWaitTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
				return;
			}

			// Consistently issue return-to-base order every tick until carriers are en route.
			if (!returnIssued)
			{
				returnCell = squad.SquadManager.GetRandomBaseCenter();
				var target = Target.FromCell(squad.World, returnCell);
				foreach (var carrier in validCarriers)
					squad.Bot.QueueOrder(new Order("Move", carrier, target, false));
				foreach (var escort in squad.EscortUnits.Where(e => !e.IsDead && e.IsInWorld))
					squad.Bot.QueueOrder(new Order("Move", escort, target, false));

				returnIssued = true;
			}

			var basePos = squad.World.Map.CenterOfCell(returnCell);
			var homeRange = WDist.FromCells(10);
			var homeRangeSq = (long)homeRange.Length * homeRange.Length;

			// Horizontal-only: air-transport carriers hover at cruise altitude
			// over the base; a 3-D distance would never satisfy the home check.
			var allHome = validCarriers
				.All(u => HorizontalLengthSquared(u.CenterPosition - basePos) <= homeRangeSq);

			if (!allHome || !returnIssued)
				return;

			// Unload any remaining cargo before dissolving so passengers aren't stranded inside.
			var carriersWithCargo = validCarriers
				.Where(u => u.TraitOrDefault<Cargo>()?.IsEmpty() == false)
				.ToList();

			if (carriersWithCargo.Count > 0)
			{
				unloadAttemptTicks += squad.TicksSinceLastUpdate;

				// Idle carriers get the Unload order immediately.
				// Non-idle carriers (landing animation, rearm-pad state) get it after a
				// short grace period so we don't interrupt an ongoing unload sequence.
				var graceElapsed = unloadAttemptTicks >= UnloadGraceTicks;
				foreach (var carrier in carriersWithCargo.Where(u => u.IsIdle || graceElapsed))
					squad.Bot.QueueOrder(new Order("Unload", carrier, false));

				if (graceElapsed)
					unloadAttemptTicks = 0;

				return;
			}

			squad.FuzzyStateMachine.ChangeState(squad, new TransportDoneState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	// ---------------------------------------------------------------------------
	// Engineer capture-and-sell: after a CaptureAndSell transport drops its engineer
	// passengers behind enemy lines, each engineer captures the nearest valuable enemy
	// building (distance dominates, high value nudges it a few cells "closer") and the
	// freshly captured building is immediately sold for cash + denial. Empty carriers head
	// home; the squad dissolves once the engineers are spent (consumed on capture) or on a
	// timeout. Shared by ground (APC) and subterranean (SAPC) drops.
	// ---------------------------------------------------------------------------
	sealed class EngineerCaptureState : CNStateBase, ICNState
	{
		const int MaxMissionTicks = 1500; // game ticks, ~60 s hard cap

		// Game ticks, not update cycles: lets the final capture+sell resolve before dissolving. As
		// 50 cycles this was 3750 ticks, well past MaxMissionTicks — the mission always timed out
		// first, so the grace period never applied at all.
		const int GraceAfterEngineersTicks = 375;

		int startTick;
		int engineerlessTicks;
		readonly HashSet<uint> sellOrdered = [];
		readonly List<Actor> captureTargets = [];

		public void Activate(CNSquad squad)
		{
			squad.AcceptingPassengers = false;
			startTick = squad.World.WorldTick;
			engineerlessTicks = 0;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
			{
				squad.SquadManager.UnregisterSquad(squad);
				return;
			}

			// Sell any building an engineer has just captured for us.
			foreach (var target in captureTargets)
			{
				if (target == null || target.IsDead || !target.IsInWorld)
					continue;
				if (target.Owner != squad.Bot.Player || sellOrdered.Contains(target.ActorID))
					continue;

				squad.Bot.QueueOrder(new Order("Sell", target, false));
				sellOrdered.Add(target.ActorID);
			}

			// Empty carriers head home; carriers still holding cargo keep unloading.
			foreach (var carrier in squad.CarrierUnits.Where(c => !c.IsDead && c.IsInWorld))
			{
				var cargo = carrier.TraitOrDefault<Cargo>();
				if (cargo != null && !cargo.IsEmpty())
				{
					if (carrier.IsIdle)
						squad.Bot.QueueOrder(new Order("Unload", carrier, false));
				}
				else if (carrier.IsIdle)
				{
					squad.Bot.QueueOrder(new Order("Move", carrier,
						Target.FromCell(squad.World, squad.SquadManager.GetRandomBaseCenter()), false));
				}
			}

			// Assign idle engineers to the best nearby capturable enemy building.
			var engineers = squad.PassengerUnits.Where(e => !e.IsDead && e.IsInWorld).ToList();
			foreach (var engineer in engineers)
			{
				if (!engineer.IsIdle)
					continue;

				var target = FindCaptureTarget(squad, engineer);
				if (target == null)
					continue;

				squad.Bot.QueueOrder(new Order("CaptureActor", engineer, Target.FromActor(target), false));
				if (!captureTargets.Contains(target))
					captureTargets.Add(target);
			}

			// Dissolve once engineers are spent and pending captures have resolved, or on timeout.
			if (engineers.Count == 0)
				engineerlessTicks += squad.TicksSinceLastUpdate;
			else
				engineerlessTicks = 0;

			if ((engineers.Count == 0 && engineerlessTicks >= GraceAfterEngineersTicks)
				|| squad.World.WorldTick - startTick >= MaxMissionTicks)
				squad.SquadManager.UnregisterSquad(squad);
		}

		public void Deactivate(CNSquad squad) { }

		static Actor FindCaptureTarget(CNSquad squad, Actor engineer)
		{
			var engineerCaptureManager = engineer.TraitOrDefault<CaptureManager>();
			if (engineerCaptureManager == null)
				return null;

			Actor best = null;
			var bestScore = int.MaxValue;
			foreach (var building in squad.SquadManager.GetCachedEnemyBuildings())
			{
				if (building.IsDead || !building.IsInWorld)
					continue;

				var targetCaptureManager = building.TraitOrDefault<CaptureManager>();
				if (targetCaptureManager == null || !engineerCaptureManager.CanTarget(targetCaptureManager))
					continue;

				// Distance dominates (in cells); value gives a small "worth a few cells closer" nudge.
				var distCells = (building.CenterPosition - engineer.CenterPosition).Length / 1024;
				var score = distCells - CaptureValueBonus(building);
				if (score < bestScore)
				{
					bestScore = score;
					best = building;
				}
			}

			return best;
		}

		static int CaptureValueBonus(Actor building)
		{
			var caps = building.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (caps == null)
				return 0;

			var bonus = 0;
			if (caps.Contains("Superweapon")) bonus += 6;
			if (caps.Contains("Tech")) bonus += 5;
			if (caps.Contains("Economy")) bonus += 5;
			if (caps.Contains("Production")) bonus += 4;
			if (caps.Contains("Power")) bonus += 2;
			return bonus;
		}
	}

	sealed class TransportDoneState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { squad.AcceptingPassengers = false; }

		public void Tick(CNSquad squad)
		{
			squad.SquadManager.UnregisterSquad(squad);
		}

		public void Deactivate(CNSquad squad) { }
	}
}
