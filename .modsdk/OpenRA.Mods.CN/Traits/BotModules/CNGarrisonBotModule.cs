#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.CN.Traits.BotModules.Squads;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	enum CNGarrisonNeed { None, AntiArmor, AntiAir }

	[TraitLocation(SystemActors.Player)]
	[Desc("Sends spare idle infantry to garrison own buildings tagged with GarrisonCapability (e.g. GAFORT)",
		"whenever they have open Cargo capacity, preferring whichever infantry specialization the local threat",
		"around that building calls for. Periodically swaps a mismatched passenger out if the local threat",
		"changes and none of the current garrison covers it.")]
	public class CNGarrisonBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Ticks between scans for garrison buildings with open capacity and available infantry.")]
		public readonly int ScanInterval = 150;

		[Desc("Ticks between re-evaluating whether the current garrison mix still covers the local threat.")]
		public readonly int SwapInterval = 450;

		[Desc("Radius (in cells) around a garrison building scanned for enemy presence to determine which",
			"infantry specialization it needs.")]
		public readonly WDist ThreatScanRadius = WDist.FromCells(12);

		[Desc("BotCapabilities tag that marks a building as wanting garrison management.")]
		public readonly string GarrisonCapability = "GarrisonDefense";

		[Desc("BotCapabilities tag required on infantry eligible to be sent to garrison.")]
		public readonly string InfantryCapability = "Infantry";

		[Desc("BotCapabilities tag on infantry that counters vehicle/armor threats.")]
		public readonly string AntiArmorCapability = "AntiArmor";

		[Desc("BotCapabilities tag on infantry that counters air threats.")]
		public readonly string AntiAirCapability = "AntiAir";

		[Desc("BotCapabilities tag on enemy units that count as an armor threat.")]
		public readonly string EnemyArmorCapability = "Vehicle";

		[Desc("BotCapabilities tag on enemy units that count as an air threat.")]
		public readonly string EnemyAirCapability = "Aircraft";

		[Desc("Idle infantry are only sent to garrison once this many remain unclaimed, so squad formation",
			"is never starved just to fill a bunker.")]
		public readonly int MinimumSpareInfantry = 2;

		[Desc("Desired occupied cargo weight per garrison. Kept below full capacity so several positions",
			"can be manned without consuming the bot's whole infantry production.")]
		public readonly int DesiredOccupancyWeight = 4;

		[Desc("Ticks an infantry unit reserved from squad assignment may wait to receive its garrison order.")]
		public readonly int ReservationTimeout = 450;

		public override object Create(ActorInitializer init) { return new CNGarrisonBotModule(init.Self, this); }
	}

	public class CNGarrisonBotModule : ConditionalTrait<CNGarrisonBotModuleInfo>, IBotTick
	{
		readonly World world;

		int fillTicks;
		int swapTicks;
		int reservationCapacityTick = -1;
		int availableReservationWeight;
		CNSquadManagerBotModule squadManager;
		readonly Dictionary<Actor, int> reservedInfantry = [];

		public CNGarrisonBotModule(Actor self, CNGarrisonBotModuleInfo info)
			: base(info)
		{
			world = self.World;
		}

		void RefreshActiveSquadManager(IBot bot)
		{
			if (squadManager != null && squadManager.IsTraitEnabled())
				return;

			squadManager = bot.Player.PlayerActor.TraitsImplementing<CNSquadManagerBotModule>()
				.FirstOrDefault(t => t.IsTraitEnabled());
		}

		void IBotTick.BotTick(IBot bot)
		{
			using var perfScope = CNBotPerf.Sample(bot, nameof(CNGarrisonBotModule));

			RefreshActiveSquadManager(bot);

			var doFill = --fillTicks <= 0;
			if (doFill)
				fillTicks = Info.ScanInterval;

			var doSwap = --swapTicks <= 0;
			if (doSwap)
				swapTicks = Info.SwapInterval;

			if (!doFill && !doSwap)
				return;

			var player = bot.Player;

			var garrisons = world.ActorsHavingTrait<Cargo>()
				.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead
					&& (a.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains(Info.GarrisonCapability) ?? false))
				.ToList();

			if (garrisons.Count == 0)
				return;

			if (doSwap)
				TickSwap(bot, player, garrisons);

			if (doFill)
				TickFill(bot, player, garrisons);
		}

		// Determines what the garrison building's immediate surroundings call for, based on visible enemy
		// BotCapabilities near it. Reuses the same capability tags combat units are already tagged with
		// elsewhere in the ruleset, so no separate threat-classification system is needed.
		CNGarrisonNeed AssessNeed(Actor garrison, Player player)
		{
			var armorCount = 0;
			var airCount = 0;

			foreach (var enemy in world.FindActorsInCircle(garrison.CenterPosition, Info.ThreatScanRadius))
			{
				if (enemy.Owner.RelationshipWith(player) != PlayerRelationship.Enemy)
					continue;

				// The comment above says visible, and this is what makes it true. Without it the garrison
				// answered threats the bot has no business knowing about yet - cloaked or shrouded aircraft
				// approaching out of sight had it swapping pre-emptively to anti-air.
				if (!enemy.CanBeViewedByPlayer(player))
					continue;

				var caps = enemy.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
				if (caps == null)
					continue;

				if (caps.Contains(Info.EnemyAirCapability))
					airCount++;
				else if (caps.Contains(Info.EnemyArmorCapability))
					armorCount++;
			}

			if (airCount == 0 && armorCount == 0)
				return CNGarrisonNeed.None;

			return airCount > armorCount ? CNGarrisonNeed.AntiAir : CNGarrisonNeed.AntiArmor;
		}

		string WantedCapability(CNGarrisonNeed need)
		{
			return need == CNGarrisonNeed.AntiAir ? Info.AntiAirCapability : Info.AntiArmorCapability;
		}

		static bool HasCapability(Actor a, string capability)
		{
			return a.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains(capability) ?? false;
		}

		bool CanFightFromGarrison(Actor actor)
		{
			return HasCapability(actor, Info.InfantryCapability) &&
				actor.Info.TraitInfos<ArmamentInfo>()
					.Any(a => string.Equals(a.Name, "garrisoned", StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Claims just enough newly produced infantry to man existing positions before the squad pass
		/// consumes every unassigned unit. The old fill-only approach almost never saw a candidate: squad
		/// templates and garrison filling were racing for the same idle unit, and the squad manager won.
		/// </summary>
		public bool TryReserveInfantry(Actor actor)
		{
			if (IsTraitDisabled || actor == null || actor.Owner == null || actor.IsDead || !actor.IsInWorld
				|| !CanFightFromGarrison(actor))
				return false;

			if (reservedInfantry.ContainsKey(actor))
				return true;

			if (reservationCapacityTick != world.WorldTick)
			{
				reservationCapacityTick = world.WorldTick;
				foreach (var (reserved, tick) in reservedInfantry.ToArray())
					if (reserved.IsDead || !reserved.IsInWorld || world.WorldTick - tick >= Info.ReservationTimeout)
						reservedInfantry.Remove(reserved);

				availableReservationWeight = 0;
				foreach (var garrison in world.ActorsHavingTrait<Cargo>())
				{
					if (garrison.Owner != actor.Owner || garrison.IsDead || !garrison.IsInWorld ||
						!(garrison.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains(
							Info.GarrisonCapability) ?? false))
						continue;

					var cargo = garrison.Trait<Cargo>();
					var desired = Math.Min(cargo.Info.MaxWeight, Math.Max(1, Info.DesiredOccupancyWeight));
					availableReservationWeight += Math.Max(0, desired - UsedCargoWeight(cargo));
				}

				availableReservationWeight = Math.Max(0, availableReservationWeight - reservedInfantry.Count);
			}

			if (availableReservationWeight <= 0)
				return false;

			reservedInfantry[actor] = world.WorldTick;
			availableReservationWeight--;
			return true;
		}

		static int UsedCargoWeight(Cargo cargo)
		{
			var weight = 0;
			foreach (var passenger in cargo.Passengers)
				weight += passenger.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1;

			return weight;
		}

		/// <summary>
		/// A spare unit carrying <paramref name="capability"/> that is free to be garrisoned, or null. Same
		/// eligibility the fill pass uses - idle, not in a squad, and outside the spare-infantry buffer -
		/// because a swap that empties a position on the strength of a replacement the fill pass would
		/// then refuse to hand over is worse than no swap at all.
		/// </summary>
		Actor FindFreeSpecialist(Player player, string capability)
		{
			var spare = -Info.MinimumSpareInfantry;
			foreach (var a in world.ActorsHavingTrait<Mobile>())
			{
				if (a.Owner != player || !a.IsInWorld || a.IsDead || !a.IsIdle
					|| !CanFightFromGarrison(a)
					|| squadManager?.IsUnitAssignedToSquad(a) == true)
					continue;

				// Counted the same way the fill pass counts: the buffer comes off the top, and only what is
				// left over may be committed.
				if (++spare > 0 && HasCapability(a, capability))
					return a;
			}

			return null;
		}

		void TickFill(IBot bot, Player player, List<Actor> garrisons)
		{
			// Squad members are off limits. "Idle" is not the same as "unassigned": a squad parked in
			// CNWaveHoldState waiting for the next attack wave sits still and reports IsIdle, and the
			// garrison pass used to pull exactly those units out of the staging wave and into bunkers.
			// CNRepairManagerBotModule already gates on this; the same gate belongs here.
			var idleInfantry = world.ActorsHavingTrait<Mobile>()
				.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead && a.IsIdle
					&& CanFightFromGarrison(a)
					&& squadManager?.IsUnitAssignedToSquad(a) != true)
				.ToList();

			var reserved = idleInfantry.Where(reservedInfantry.ContainsKey).ToList();
			var general = idleInfantry.Where(a => !reservedInfantry.ContainsKey(a)).ToList();
			var spareCount = Math.Max(0, general.Count - Info.MinimumSpareInfantry);
			var pool = reserved.Concat(general.Take(spareCount)).ToList();
			if (pool.Count == 0)
				return;

			foreach (var garrison in garrisons)
			{
				if (pool.Count == 0)
					break;

				var cargo = garrison.Trait<Cargo>();
				var desired = Math.Min(cargo.Info.MaxWeight, Math.Max(1, Info.DesiredOccupancyWeight));
				var occupied = UsedCargoWeight(cargo);
				if (occupied >= desired || !cargo.HasSpace(1))
					continue;

				var need = AssessNeed(garrison, player);

				// HasSpace() reflects committed state only; QueueOrder doesn't apply until the order resolves
				// later, so track how much we've already earmarked this pass ourselves.
				var queued = 0;
				for (var pendingWeight = 0; pool.Count > 0 && occupied + pendingWeight < desired
					&& cargo.HasSpace(pendingWeight + 1); pendingWeight++)
				{
					var pick = PickBestForNeed(pool, need);
					pool.Remove(pick);
					bot.QueueOrder(new Order("EnterTransport", pick, Target.FromActor(garrison), false));
					queued++;
				}

				if (queued > 0)
					CNBotLog.Debug("{0} garrison {1} at {2}: filling {3} slot(s), occupancy {4}/{5}",
						player, garrison.Info.Name, garrison.Location, queued, occupied, desired);
			}
		}

		Actor PickBestForNeed(List<Actor> pool, CNGarrisonNeed need)
		{
			if (need != CNGarrisonNeed.None)
			{
				var match = pool.FirstOrDefault(a => HasCapability(a, WantedCapability(need)));
				if (match != null)
					return match;
			}

			return pool[0];
		}

		void TickSwap(IBot bot, Player player, List<Actor> garrisons)
		{
			foreach (var garrison in garrisons)
			{
				var cargo = garrison.Trait<Cargo>();
				if (cargo.IsEmpty())
					continue;

				var need = AssessNeed(garrison, player);
				if (need == CNGarrisonNeed.None)
					continue;

				var wantedCapability = WantedCapability(need);
				if (cargo.Passengers.Any(p => HasCapability(p, wantedCapability)))
					continue;

				// Nobody currently inside covers the local threat. If there's a free slot the fill pass will
				// bring the right specialist in on its own; if the garrison is full, make room for it now -
				// but only once the replacement actually exists.
				//
				// Unload empties the whole position, and this used to fire whether or not anything could
				// take the vacated seats. A full anti-armour bunker that spotted aircraft with no free
				// anti-air infantry anywhere would empty itself and stand there vacant, then refill with
				// the same unsuitable squad on the next pass and empty again on the one after. Worse, the
				// fill pass running in the same bot tick still sees the old cargo, because orders resolve
				// later - so it cannot be relied on to catch the position on the way down.
				if (!cargo.HasSpace(1) && FindFreeSpecialist(player, wantedCapability) != null)
					bot.QueueOrder(new Order("Unload", garrison, false));
			}
		}
	}
}
