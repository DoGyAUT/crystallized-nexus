#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// What a region of the map is for, once this bot holds it.
	/// <para>
	/// The same five names <see cref="CNBaseRole"/> already uses, moved onto the thing they were always
	/// describing. A base role had to guess an area from a radius around a construction yard; a region
	/// IS the area, bounded by the terrain and the doors leading out of it, so every one of these
	/// questions has an answer instead of an approximation: an outpost is a region that mostly holds a
	/// door, military is a region whose neighbour belongs to an enemy, and a region with no door at all
	/// needs no defence whatever is standing in it.
	/// </para>
	/// </summary>
	public enum CNRegionRole
	{
		/// <summary>Not held, or held and steering nothing.</summary>
		None,

		/// <summary>The starting region. Keeps the tech buildings; longest held and best defended.</summary>
		Core,

		/// <summary>Carries the tiberium. Collects the refineries and silos.</summary>
		Economy,

		/// <summary>Borders an enemy-held region. Collects unit production above the per-base minimum.</summary>
		Military,

		/// <summary>Small, or mostly a door onto something that matters. Defence and support only.</summary>
		Outpost,
	}

	/// <summary>
	/// This bot's standing on one region: whether it holds it, what the ground is worth, and what the
	/// region is for. The region's <em>shape</em> is a shared terrain fact (<see cref="CNRegion"/>);
	/// everything here is one bot's own reading of it and is deliberately not shared.
	/// </summary>
	public sealed class CNRegionState
	{
		public readonly int RegionId;

		/// <summary>Held: a construction yard of ours stands here, or enough of our buildings do.</summary>
		public bool Claimed;
		public int ClaimedSinceTick;

		public CNRegionRole Role;
		public int RoleSinceTick;

		// The three questions the region is scored on, each 0-100, plus the weighted total.
		public int ResourceScore;
		public int SpaceScore;
		public int SecurityScore;
		public int Value;

		// What the scores were read from. Kept so the overlay and the log can show the evidence rather
		// than only the verdict - every tuning argument about these numbers needs the inputs.

		/// <summary>Raw buildable-cell count of the region, unscaled - the capacity behind <see cref="SpaceScore"/>.</summary>
		public int BuildableCells;

		/// <summary>How many buildings this region's ground is taken to hold, or 0 when the notion is disabled.</summary>
		public int BuildingCapacity;

		/// <summary>Whether the region has as many of our buildings as its ground is taken to hold.</summary>
		public bool IsFull => BuildingCapacity > 0 && OwnBuildings >= BuildingCapacity;

		/// <summary>Most buildings we have ever had here, and when that last went up. A region that has
		/// stopped growing is finished with, whether or not it ever reached a target count.</summary>
		public int PeakBuildings;
		public int LastGrowthTick;

		public int Connections;
		public int SealableDoors;
		public int DoorWidthTotal;
		public int OwnBuildings;
		public int OwnConstructionYards;
		public bool BordersEnemy;

		public CNRegionState(int regionId)
		{
			RegionId = regionId;
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Tracks which regions of the map this bot holds, what each one's ground is worth, and what each",
		"held region is for (see CNRegionRole). Reads the shared region graph from CNTacticalMapBotModule;",
		"owns no terrain analysis of its own. Draw it with the \"cntopo\" chat command.")]
	public class CNRegionManagerBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Actor types that claim a region outright by standing in it.")]
		public readonly FrozenSet<string> ConstructionYardTypes = FrozenSet<string>.Empty;

		[Desc("Buildings of ours in a region that claim it even with no construction yard there - a base whose",
			"yard packed up still holds its ground. 0 means only a construction yard ever claims.")]
		public readonly int ClaimMinimumBuildings = 3;

		[Desc("Ticks between re-reading claims and re-scoring regions.")]
		public readonly int RegionRefreshInterval = 250;

		[Desc("A region keeps a role for at least this many ticks before it can be reassigned. A role decides",
			"what gets built there, so one that follows the evidence tick by tick leaves half-finished",
			"structure behind in both directions - same reasoning as BaseRoleMinimumHoldTicks.")]
		public readonly int RegionRoleMinimumHoldTicks = 1500;

		[Desc("Resource cells in a region that score a full 100 for resources. Above this the score saturates:",
			"past a point the answer is only \"plenty\".")]
		public readonly int ResourceCellsForFullScore = 400;

		[Desc("Buildable cells in a region that score a full 100 for space.")]
		public readonly int BuildableCellsForFullScore = 900;

		[Desc("Security lost per region this one connects to. Every neighbour is a way in, whether or not it",
			"pinches narrow enough to be walled.")]
		public readonly int ConnectionSecurityPenalty = 12;

		[Desc("Security lost per cell of total sealable-door width. A wide door is a worse door.")]
		public readonly int DoorWidthSecurityPenalty = 3;

		[Desc("Total penalty at which a region scores 50 for security. The score falls off by halves from",
			"there rather than subtracting the penalty outright: a straight subtraction floors every region",
			"with more than a couple of doors at zero, and a score reading 0 for both a moderately open",
			"region and a hopeless one cannot rank the two - which is the only job this number has.")]
		public readonly int SecurityHalfScorePenalty = 60;

		[Desc("Weight of the resource score in a region's overall value.")]
		public readonly int ResourceValueWeight = 100;

		[Desc("Weight of the buildable-space score in a region's overall value.")]
		public readonly int SpaceValueWeight = 60;

		[Desc("Weight of the security score in a region's overall value.")]
		public readonly int SecurityValueWeight = 80;

		[Desc("Resource cells a held region needs before it can be the Economy region.")]
		public readonly int EconomyMinimumResourceCells = 60;

		[Desc("A held region at most this many cells across is an Outpost regardless of what else it scores:",
			"there is not enough ground in it to be anything else.")]
		public readonly int OutpostMaximumRegionSize = 400;

		[Desc("Ticks without a new building after which a region counts as finished being built up, whatever",
			"its building count. Without this a region too small to reach the target count would block",
			"expansion for the rest of the match - an outpost with room for three never reaches eight.")]
		public readonly int RegionDevelopmentStallTicks = 3000;

		[Desc("Buildable cells a region needs per building it is considered able to hold. A region that has",
			"reached its capacity is full, and the base builder prefers to build anywhere else - which is",
			"also the signal that it is time to expand rather than keep cramming.",
			"0 disables the capacity notion entirely and nothing is ever considered full.",
			"Pick this from the ratio reported in the region log rather than by eye: a played match had a",
			"region of roughly 840 buildable cells holding 102 buildings, about eight cells each, so a",
			"guess of forty would have capped it at twenty and strangled the bot.")]
		public readonly int BuildableCellsPerBuilding = 0;

		public override object Create(ActorInitializer init) { return new CNRegionManagerBotModule(init.Self, this); }
	}

	public class CNRegionManagerBotModule : ConditionalTrait<CNRegionManagerBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		CNTacticalMapBotModule tacticalMap;
		bool firstTick = true;

		// Parallel to the shared region list, rebuilt whenever that list changes shape. Region ids are
		// positions in that list, so they mean nothing across a rebuild - see regionGeneration.
		CNRegionState[] states = [];
		int regionGeneration = -1;

		int nextRefreshTick;

		// Where this bot started, latched off the first construction yard it ever owns. The Core region is
		// pinned to this rather than to wherever the buildings currently average out, so losing ground does
		// not quietly relabel the main base as something else.
		CPos? homeOrigin;

		public CNRegionManagerBotModule(Actor self, CNRegionManagerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		/// <summary>True once regions have been read at least once and the states array means something.</summary>
		public bool Ready => states.Length > 0;

		void IBotTick.BotTick(IBot bot)
		{
			using var perfScope = CNBotPerf.Sample(bot, nameof(CNRegionManagerBotModule));

			if (firstTick)
			{
				tacticalMap = bot.Player.PlayerActor.TraitsImplementing<CNTacticalMapBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
				nextRefreshTick = world.LocalRandom.Next(0, Math.Max(1, Info.RegionRefreshInterval));
				firstTick = false;
			}

			if (tacticalMap == null || !tacticalMap.TopologyReady)
				return;

			if (world.WorldTick < nextRefreshTick)
				return;

			nextRefreshTick = world.WorldTick + Math.Max(1, Info.RegionRefreshInterval);
			Refresh();
		}

		void Refresh()
		{
			var regions = tacticalMap.GetRegions();
			if (regions.Count == 0)
				return;

			// A bridge falling can split or merge regions, and the ids are just positions in the list, so
			// everything remembered per id has to go with them. Held roles included: a role is a statement
			// about a piece of ground, and after a re-cut nothing guarantees id 4 is that ground any more.
			if (regionGeneration != tacticalMap.RegionGeneration || states.Length != regions.Count)
			{
				regionGeneration = tacticalMap.RegionGeneration;
				states = Exts.MakeArray(regions.Count, i => new CNRegionState(i));
			}

			ReadClaims(regions);
			ScoreRegions(regions);
			AssignRoles(regions);
		}

		/// <summary>
		/// Counts our own buildings per region in one pass and settles which regions we hold. One scan
		/// rather than a per-region query: the region a building is in is a single cell lookup, so walking
		/// our buildings once is cheaper than walking the map once per region.
		/// </summary>
		void ReadClaims(IReadOnlyList<CNRegion> regions)
		{
			foreach (var state in states)
			{
				state.OwnBuildings = 0;
				state.OwnConstructionYards = 0;
			}

			foreach (var actor in world.Actors)
			{
				if (actor.IsDead || !actor.IsInWorld || actor.Owner != player || !actor.Info.HasTraitInfo<BuildingInfo>())
					continue;

				var regionId = tacticalMap.GetRegionIdAt(actor.Location);
				if (regionId < 0 || regionId >= states.Length)
					continue;

				states[regionId].OwnBuildings++;

				if (Info.ConstructionYardTypes.Contains(actor.Info.Name))
				{
					states[regionId].OwnConstructionYards++;
					homeOrigin ??= actor.Location;
				}
			}

			foreach (var state in states)
			{
				if (state.OwnBuildings <= state.PeakBuildings)
					continue;

				state.PeakBuildings = state.OwnBuildings;
				state.LastGrowthTick = world.WorldTick;
			}

			var claimMinimum = Info.ClaimMinimumBuildings;
			for (var i = 0; i < states.Length; i++)
			{
				var state = states[i];
				var claimed = state.OwnConstructionYards > 0
					|| (claimMinimum > 0 && state.OwnBuildings >= claimMinimum);

				if (claimed != state.Claimed)
				{
					state.Claimed = claimed;
					state.ClaimedSinceTick = world.WorldTick;
				}

				// A region we no longer hold steers nothing, so it cannot keep a role either - and it must
				// release the exclusive ones, or losing the Core region would leave the bot unable to name
				// a new one.
				if (!claimed && state.Role != CNRegionRole.None)
				{
					state.Role = CNRegionRole.None;
					state.RoleSinceTick = world.WorldTick;
				}

				// Recorded here rather than in the scoring pass because it is about who owns the neighbours,
				// not about the ground: it moves with the match, while everything scored below is terrain.
				state.BordersEnemy = false;
				foreach (var adjacentId in regions[i].AdjacentRegionIds)
				{
					var owner = tacticalMap.GetRegionOwner(adjacentId);
					if (owner != null && player.RelationshipWith(owner) == PlayerRelationship.Enemy)
					{
						state.BordersEnemy = true;
						break;
					}
				}
			}
		}

		/// <summary>
		/// Scores every region on the three questions that decide whether it is worth holding: what can be
		/// harvested there, what can be built there, and how many ways in it has. Terrain only - none of it
		/// depends on who owns what, so an unheld region can be scored before anything is committed to it.
		/// </summary>
		void ScoreRegions(IReadOnlyList<CNRegion> regions)
		{
			var resourceFull = Math.Max(1, Info.ResourceCellsForFullScore);
			var spaceFull = Math.Max(1, Info.BuildableCellsForFullScore);
			var totalWeight = Info.ResourceValueWeight + Info.SpaceValueWeight + Info.SecurityValueWeight;

			for (var i = 0; i < states.Length; i++)
			{
				var region = regions[i];
				var state = states[i];

				state.ResourceScore = Math.Min(100, region.ResourceCellCount * 100 / resourceFull);
				state.SpaceScore = Math.Min(100, region.BuildableCellCount * 100 / spaceFull);
				state.BuildableCells = region.BuildableCellCount;
				state.BuildingCapacity = Info.BuildableCellsPerBuilding > 0
					? region.BuildableCellCount / Info.BuildableCellsPerBuilding
					: 0;

				// Every neighbouring region is a way in. Only some of those ways pinch narrow enough to have
				// resolved to a sealable corridor, which is why width is summed separately instead of being
				// the whole measure: a region reachable over a wide ramp scores no door width at all and is
				// still wide open, and the connection count is what catches it.
				var widthTotal = 0;
				foreach (var corridorIndex in region.DoorCorridorIndices)
				{
					var corridor = tacticalMap.GetRegionDoorCorridor(corridorIndex);
					if (corridor != null)
						widthTotal += corridor.Cells.Length;
				}

				state.Connections = region.AdjacentRegionIds.Length;
				state.SealableDoors = region.DoorCorridorIndices.Length;
				state.DoorWidthTotal = widthTotal;

				var penalty = state.Connections * Math.Max(0, Info.ConnectionSecurityPenalty)
					+ widthTotal * Math.Max(0, Info.DoorWidthSecurityPenalty);

				// Halving, not subtracting. Subtracting the penalty from 100 put every region with more than
				// a couple of doors on the floor: a played map scored a 3-connection region with 22 cells of
				// door and another with 49 both at exactly 0, which says they are equally defensible and they
				// are not. This keeps the whole range in play - a sealed pocket still reads 100, and the two
				// above separate to roughly 37 and 25 - while never quite reaching zero, because "no way in
				// at all" and "many wide ways in" should not meet at the same number from opposite ends.
				var half = Math.Max(1, Info.SecurityHalfScorePenalty);
				state.SecurityScore = 100 * half / (half + penalty);

				state.Value = totalWeight <= 0
					? 0
					: (state.ResourceScore * Info.ResourceValueWeight
						+ state.SpaceScore * Info.SpaceValueWeight
						+ state.SecurityScore * Info.SecurityValueWeight) / totalWeight;
			}
		}

		/// <summary>
		/// Hands out the roles among the regions we hold. Core and Military are exclusive; Economy and
		/// Outpost are not. A region that has held its role for less than the hold time keeps it, and an
		/// exclusive role still held by somebody is not handed out again - the same shape
		/// <see cref="CNBaseBuilderBotModule"/> uses for base roles, for the same reason.
		/// </summary>
		void AssignRoles(IReadOnlyList<CNRegion> regions)
		{
			var holdTicks = Math.Max(0, Info.RegionRoleMinimumHoldTicks);
			var locked = new bool[states.Length];
			var coreHeld = false;
			var militaryHeld = false;

			for (var i = 0; i < states.Length; i++)
			{
				var state = states[i];
				if (!state.Claimed || state.Role == CNRegionRole.None)
					continue;

				if (world.WorldTick - state.RoleSinceTick >= holdTicks)
					continue;

				locked[i] = true;
				coreHeld |= state.Role == CNRegionRole.Core;
				militaryHeld |= state.Role == CNRegionRole.Military;
			}

			var proposed = new CNRegionRole[states.Length];
			for (var i = 0; i < states.Length; i++)
				proposed[i] = locked[i] ? states[i].Role : CNRegionRole.None;

			// Core: where we started, if we still hold it. A bot driven off its starting ground falls back
			// to wherever most of its buildings now are, so there is always exactly one Core to put tech in.
			if (!coreHeld)
			{
				var coreId = -1;
				if (homeOrigin != null)
				{
					var homeRegion = tacticalMap.GetRegionIdAt(homeOrigin.Value);
					if (homeRegion >= 0 && homeRegion < states.Length && states[homeRegion].Claimed)
						coreId = homeRegion;
				}

				if (coreId < 0)
					coreId = BestUnassigned(proposed, locked, s => s.OwnBuildings > 0, s => s.OwnBuildings);

				if (coreId >= 0)
				{
					// An exclusive role outranks a hold on a non-exclusive one. The hold exists so a role
					// does not flicker; it was never meant to leave the bot without a Core at all, and it
					// did: lose the Core region shortly after the survivors were given Economy or Outpost,
					// and every candidate is locked until its timer runs out. Tech then has nowhere to go
					// for the rest of that hold. Never at the cost of the OTHER exclusive role, which is a
					// place of its own and not a spare slot.
					if (proposed[coreId] != CNRegionRole.Military)
					{
						proposed[coreId] = CNRegionRole.Core;
						locked[coreId] = false;
					}
				}
			}

			// Military: held ground that touches an enemy's. Preferring the one that already has buildings
			// keeps the role on a place that can actually take the production it steers there - an empty
			// pocket bordering the enemy is a front, not a factory site.
			if (!militaryHeld)
			{
				var militaryId = BestUnassigned(proposed, locked, s => s.BordersEnemy, s => s.OwnBuildings * 1000 + s.Value);

				// Same preemption as Core, and Core is filled first above so it wins any contest between
				// the two. A locked Economy or Outpost on the only region touching the enemy would
				// otherwise leave the bot with no Military at all.
				if (militaryId < 0)
					militaryId = BestUnassignedLocked(proposed, s => s.BordersEnemy && s.Role != CNRegionRole.Core,
						s => s.OwnBuildings * 1000 + s.Value);

				if (militaryId >= 0 && proposed[militaryId] != CNRegionRole.Core)
				{
					proposed[militaryId] = CNRegionRole.Military;
					locked[militaryId] = false;
				}
			}

			// Economy and Outpost are not exclusive: several fields and several doors can each be worth
			// their own. Outpost is decided first because it is about the ground being too small to be
			// anything else, which no amount of tiberium in it changes.
			for (var i = 0; i < states.Length; i++)
			{
				if (locked[i] || proposed[i] != CNRegionRole.None || !states[i].Claimed)
					continue;

				if (regions[i].Size <= Math.Max(1, Info.OutpostMaximumRegionSize))
					proposed[i] = CNRegionRole.Outpost;
				else if (regions[i].ResourceCellCount >= Info.EconomyMinimumResourceCells)
					proposed[i] = CNRegionRole.Economy;
			}

			var changes = 0;
			for (var i = 0; i < states.Length; i++)
			{
				if (states[i].Role == proposed[i])
					continue;

				states[i].Role = proposed[i];
				states[i].RoleSinceTick = world.WorldTick;
				changes++;
			}

			if (changes > 0)
				LogRoles(regions);
		}

		/// <summary>
		/// The best region that is claimed, unlocked and still roleless, among those passing
		/// <paramref name="eligible"/>, ranked by <paramref name="rank"/>. Returns -1 when none qualifies.
		/// </summary>
		int BestUnassigned(CNRegionRole[] proposed, bool[] locked, Func<CNRegionState, bool> eligible, Func<CNRegionState, int> rank)
		{
			var bestId = -1;
			var bestRank = int.MinValue;

			for (var i = 0; i < states.Length; i++)
			{
				if (locked[i] || proposed[i] != CNRegionRole.None || !states[i].Claimed || !eligible(states[i]))
					continue;

				var value = rank(states[i]);
				if (value <= bestRank)
					continue;

				bestRank = value;
				bestId = i;
			}

			return bestId;
		}

		/// <summary>
		/// The best claimed region for an exclusive role among those currently LOCKED under a non-exclusive
		/// one. Only reached when no unlocked candidate exists at all - a hold that would otherwise leave
		/// the bot without a Core or a Military entirely.
		/// </summary>
		int BestUnassignedLocked(CNRegionRole[] proposed, Func<CNRegionState, bool> eligible, Func<CNRegionState, int> rank)
		{
			var bestId = -1;
			var bestRank = int.MinValue;

			for (var i = 0; i < states.Length; i++)
			{
				if (!states[i].Claimed || !eligible(states[i]))
					continue;

				if (proposed[i] == CNRegionRole.Core || proposed[i] == CNRegionRole.Military)
					continue;

				var value = rank(states[i]);
				if (value <= bestRank)
					continue;

				bestRank = value;
				bestId = i;
			}

			return bestId;
		}

		void LogRoles(IReadOnlyList<CNRegion> regions)
		{
			var held = states.Where(s => s.Claimed).ToList();
			if (held.Count == 0)
			{
				CNBotLog.Debug("{0} regions: nothing held of {1}", player, regions.Count);
				return;
			}

			CNBotLog.Debug("{0} regions: {1} of {2} held | {3}",
				player, held.Count, regions.Count,
				string.Join("  ", held.Select(s =>
					$"R{s.RegionId} {s.Role} v{s.Value} (res{s.ResourceScore} spc{s.SpaceScore} sec{s.SecurityScore}; "
					+ $"{s.Connections} conn, {s.SealableDoors} doors w{s.DoorWidthTotal}, "

					// Buildings against the ground they stand on, because picking BuildableCellsPerBuilding
					// by eye is exactly the kind of guess that has cost this feature three attempts already.
					// This prints the ratio bots actually achieve, so the number can be read off a match.
					+ $"{s.OwnBuildings}b/{s.BuildableCells}c"
					+ (s.OwnBuildings > 0 ? $" ({s.BuildableCells / s.OwnBuildings}c per b)" : "")
					+ (s.BuildingCapacity > 0 ? $", cap {s.BuildingCapacity}{(s.IsFull ? " FULL" : "")}" : "")
					+ (s.BordersEnemy ? ", enemy adj" : "") + ")")));
		}

		/// <summary>
		/// Whether a region is still being built up, as opposed to finished with. Finished means one of
		/// three things, and the last two matter as much as the first: it has reached
		/// <paramref name="developedBuildings"/>, its ground is full, or it has simply stopped growing.
		/// <para>
		/// Without that last clause a small region deadlocks expansion outright - an outpost with room for
		/// three buildings never reaches a target of eight, so it counts as under development for the rest
		/// of the match and nothing new is ever founded.
		/// </para>
		/// </summary>
		public bool IsUnderDevelopment(CNRegionState state, int developedBuildings)
		{
			if (state == null || !state.Claimed)
				return false;

			if (state.OwnBuildings >= Math.Max(1, developedBuildings) || state.IsFull)
				return false;

			var stall = Math.Max(1, Info.RegionDevelopmentStallTicks);
			return world.WorldTick - state.LastGrowthTick < stall;
		}

		/// <summary>This bot's standing on one region, or null when the id names nothing.</summary>
		public CNRegionState GetRegionState(int regionId) =>
			regionId >= 0 && regionId < states.Length ? states[regionId] : null;

		public IReadOnlyList<CNRegionState> GetRegionStates() => states;

		/// <summary>
		/// This bot's standing on the region containing a cell, or null when the cell is in no region. The
		/// scores are terrain, so this answers for ground the bot has never been to - which is what siting
		/// an expansion needs, since the whole question there is what an unheld place would be worth.
		/// </summary>
		public CNRegionState GetRegionStateAt(CPos cell)
		{
			if (tacticalMap == null)
				return null;

			return GetRegionState(tacticalMap.GetRegionIdAt(cell));
		}

		/// <summary>What the region containing a cell is for. None when the cell is in no region, or in one we do not hold.</summary>
		public CNRegionRole GetRoleAt(CPos cell)
		{
			if (tacticalMap == null)
				return CNRegionRole.None;

			return GetRegionState(tacticalMap.GetRegionIdAt(cell))?.Role ?? CNRegionRole.None;
		}

		/// <summary>Whether this bot holds the region containing a cell. False for cells in no region at all.</summary>
		public bool IsClaimedAt(CPos cell)
		{
			if (tacticalMap == null)
				return false;

			return GetRegionState(tacticalMap.GetRegionIdAt(cell))?.Claimed ?? false;
		}

		/// <summary>The region this bot has assigned <paramref name="role"/> to, or -1. Only meaningful for the exclusive roles.</summary>
		public int GetRegionWithRole(CNRegionRole role)
		{
			for (var i = 0; i < states.Length; i++)
				if (states[i].Role == role)
					return i;

			return -1;
		}
	}
}
