#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.CN.Traits;
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

		public override object Create(ActorInitializer init) { return new CNGarrisonBotModule(init.Self, this); }
	}

	public class CNGarrisonBotModule : ConditionalTrait<CNGarrisonBotModuleInfo>, IBotTick
	{
		readonly World world;

		int fillTicks;
		int swapTicks;
		CNSquadManagerBotModule squadManager;

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

		void TickFill(IBot bot, Player player, List<Actor> garrisons)
		{
			// Squad members are off limits. "Idle" is not the same as "unassigned": a squad parked in
			// CNWaveHoldState waiting for the next attack wave sits still and reports IsIdle, and the
			// garrison pass used to pull exactly those units out of the staging wave and into bunkers.
			// CNRepairManagerBotModule already gates on this; the same gate belongs here.
			var idleInfantry = world.ActorsHavingTrait<Mobile>()
				.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead && a.IsIdle
					&& HasCapability(a, Info.InfantryCapability)
					&& squadManager?.IsUnitAssignedToSquad(a) != true)
				.ToList();

			var spareCount = idleInfantry.Count - Info.MinimumSpareInfantry;
			if (spareCount <= 0)
				return;

			// Keep the buffer entirely out of consideration, not just out of the final picks.
			var pool = idleInfantry.Take(spareCount).ToList();

			foreach (var garrison in garrisons)
			{
				if (pool.Count == 0)
					break;

				var cargo = garrison.Trait<Cargo>();
				if (!cargo.HasSpace(1))
					continue;

				var need = AssessNeed(garrison, player);

				// HasSpace() reflects committed state only; QueueOrder doesn't apply until the order resolves
				// later, so track how much we've already earmarked this pass ourselves.
				for (var pendingWeight = 0; pool.Count > 0 && cargo.HasSpace(pendingWeight + 1); pendingWeight++)
				{
					var pick = PickBestForNeed(pool, need);
					pool.Remove(pick);
					bot.QueueOrder(new Order("EnterTransport", pick, Target.FromActor(garrison), false));
				}
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
				// bring the right specialist in on its own; if the garrison is full, make room for it now.
				if (!cargo.HasSpace(1))
					bot.QueueOrder(new Order("Unload", garrison, false));
			}
		}
	}
}
