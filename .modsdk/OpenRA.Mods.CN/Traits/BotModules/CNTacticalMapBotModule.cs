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
using System.Runtime.CompilerServices;
using OpenRA.Graphics;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// Topology marker types (access points that drive defense). First letter is the overlay label (P/R/B).
	public enum CNChokepointType { Passage, Ramp, Bridge }

	public sealed class CNChokepoint
	{
		public readonly CPos Cell;
		public readonly uint Domain;
		public readonly CNChokepointType Type;
		public readonly int BaseWeight;

		public CNChokepoint(CPos cell, uint domain, CNChokepointType type, int baseWeight)
		{
			Cell = cell;
			Domain = domain;
			Type = type;
			BaseWeight = baseWeight;
		}
	}

	// The terrain-only topology (chokepoints + connectivity), shared by all bots on the same map+locomotor so the
	// expensive scan runs once. Base-relative results (useful set, high-ground, corridors) stay per bot.
	public sealed class CNSharedTopology
	{
		public readonly List<CNChokepoint> Chokepoints = [];
		public readonly List<CPos> BridgeWatchCells = [];
		public CellLayer<bool> Passability;
		public IReadOnlyDictionary<CPos, uint> AbstractDomains;
		public CPos[] NodeCells = [];
		public bool LastBridgeSignature;
		public int Generation;

		// The map's shape, cut once by the same chokepoints that gate the territory walk - not anyone's
		// claim, a terrain fact every bot can read regardless of who (if anyone) owns it.
		public readonly List<CNRegion> Regions = [];
		public CellLayer<int> RegionIdByCell;

		// The corridors CNRegion.DoorCorridorIndices index into. Kept because the indices are otherwise
		// unreadable: the list was local to BuildRegions and thrown away, so a region could say it had
		// three doors and nothing could ask how wide any of them was.
		public readonly List<CNSealableCorridor> RegionDoorCorridors = [];

		// What the cut actually ran along: resolved chokepoint corridors plus cliff ramps. Kept because
		// "the regions are wrong here" always turns out to mean "the barrier has a hole here", and a hole
		// is not visible from the region outlines alone - they simply run past it.
		public HashSet<CPos> RegionBarrier = [];

		// Ownership is a separate, cheap, periodically-refreshed tally on top of the (rarely-changing)
		// region shape - not something a bridge-change rebuild needs to touch, so it lives outside
		// Generation. Parallel to Regions; null = unclaimed/contested. NextOwnershipRefreshTick
		// coordinates so only one bot's tick performs the scan per interval (single-sim-thread, same
		// assumption RebuildSharedOnBridgeChange already relies on).
		public Player[] RegionOwners = [];
		public int NextOwnershipRefreshTick;
	}

	/// <summary>
	/// One cell of the map's shape: a connected pocket of ground bounded by chokepoints, independent of
	/// who (if anyone) holds it. Computed once, shared - see <see cref="CNSharedTopology"/>.
	/// </summary>
	public sealed class CNRegion
	{
		public readonly int Id;
		public readonly CPos[] Cells;

		/// <summary>Indices into the shared chokepoint-corridor list for the doors bounding this region.</summary>
		public readonly int[] DoorCorridorIndices;

		public readonly int[] AdjacentRegionIds;

		/// <summary>Cells carrying a resource type - a rough size of what this region's ground is worth.</summary>
		public readonly int ResourceCellCount;

		/// <summary>Passable, non-ramp cells - a rough size of how much of this region can be built on.</summary>
		public readonly int BuildableCellCount;

		/// <summary>Cells with a non-region neighbour (a door, a wall, the map edge) - the outline, for drawing.</summary>
		public readonly CPos[] BoundaryCells;

		public int Size => Cells.Length;

		public CNRegion(int id, CPos[] cells, int[] doorCorridorIndices, int[] adjacentRegionIds,
			int resourceCellCount, int buildableCellCount, CPos[] boundaryCells)
		{
			Id = id;
			Cells = cells;
			DoorCorridorIndices = doorCorridorIndices;
			AdjacentRegionIds = adjacentRegionIds;
			ResourceCellCount = resourceCellCount;
			BuildableCellCount = buildableCellCount;
			BoundaryCells = boundaryCells;
		}
	}

	// A narrow chokepoint that can be plugged with a single axis-aligned wall line plus a matching gate.
	public sealed class CNSealableCorridor
	{
		public readonly CPos Center;
		public readonly bool WallRunsHorizontal;
		public readonly CPos[] Cells;

		public CNSealableCorridor(CPos center, bool wallRunsHorizontal, CPos[] cells)
		{
			Center = center;
			WallRunsHorizontal = wallRunsHorizontal;
			Cells = cells;
		}
	}

	/// <summary>
	/// A contiguous run of passable cells on the edge of a player's territory: a way in.
	/// <para>
	/// The rest of that edge is cliff, water or map edge - wall the terrain provides for free. Only the
	/// doors have to be held, which is what lets defence be planned as a line rather than as a scatter
	/// of independent points: a territory spanning seven bases still has one edge and, usually, two or
	/// three doors.
	/// </para>
	/// </summary>
	public sealed class CNTerritoryDoor
	{
		public readonly CPos Center;
		public readonly CPos[] Cells;

		/// <summary>Direction leading out of the territory, averaged over the run.</summary>
		public readonly CVec Outward;

		/// <summary>Reachable ground behind the door, capped. What passing through it actually opens up.</summary>
		public readonly int GroundBeyond;

		public int Width => Cells.Length;

		public CNTerritoryDoor(CPos center, CPos[] cells, CVec outward, int groundBeyond)
		{
			Center = center;
			Cells = cells;
			Outward = outward;
			GroundBeyond = groundBeyond;
		}
	}

	// A passable cliff-edge cell that overlooks reachable lower ground (height advantage, natural wall).
	public readonly struct CNHighGroundEdge
	{
		public readonly CPos Cell;
		public readonly CVec Outward;
		public readonly int HeightLevels;

		public CNHighGroundEdge(CPos cell, CVec outward, int heightLevels)
		{
			Cell = cell;
			Outward = outward;
			HeightLevels = heightLevels;
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Cartographs the map once at game start (and on bridge changes) to find chokepoints (bridges, cliff ramps,",
		"narrow land passages) and high-ground cliff edges, so the bot can place defense and lay out its base with intent.",
		"Reads the hierarchical pathfinder's abstract graph and flags its cut-edges / articulation points as chokepoints.")]
	public class CNTacticalMapBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Locomotor name(s) used to analyse ground access. The first one present is used.",
			"Should be the bot's main ground combat locomotor (e.g. tracked). Empty = first available ground locomotor.")]
		public readonly FrozenSet<string> TopologyLocomotors = FrozenSet<string>.Empty;

		[Desc("Maximum number of chokepoints returned per defensive placement query.")]
		public readonly int MaxTopologyHotspots = 6;

		[Desc("Only chokepoints within this many cells of the query reference are considered.")]
		public readonly int TopologyMaxDistance = 28;

		[Desc("Base weight of a chokepoint that is a bridge (the strongest natural funnel).")]
		public readonly int BridgeWeight = 220;

		[Desc("Base weight of a chokepoint that is a cliff ramp / height transition.")]
		public readonly int RampWeight = 170;

		[Desc("Base weight of a chokepoint that is a narrow land passage.")]
		public readonly int PassageWeight = 120;

		[Desc("Ticks between re-checks of bridge-chokepoint passability. A change triggers a rebuild.")]
		public readonly int TopologyRecheckInterval = 50;

		[Desc("Minimum ticks between recomputing the 'useful' chokepoint set (reachable + leads somewhere). Higher =",
			"cheaper; this is queried very often by the base builder so it is throttled rather than computed per query.")]
		public readonly int UsefulRefreshInterval = 125;

		[Desc("How many chokepoints to evaluate per tick when refreshing the 'useful' set or sealable corridors. The",
			"flood per chokepoint is expensive, so this is spread over several ticks. Lower = smoother but slower to",
			"update.")]
		public readonly int UsefulChunkSize = 3;

		[Desc("How many candidate cells to evaluate per tick when refreshing high-ground edges. A single cell check",
			"is cheap (a couple of height/passability lookups), so this can be much larger than UsefulChunkSize",
			"without a per-tick cost - keeps a full-radius scan from taking hundreds of ticks to complete.")]
		public readonly int HighGroundChunkSize = 80;

		[Desc("Optional long fallback recompute interval in ticks. 0 disables it (rely on bridge-change detection).")]
		public readonly int RecomputeInterval = 0;

		[Desc("Skip a base-relative refresh cycle (high-ground/useful-chokepoints/sealable-corridors) if the base",
			"reference has moved less than this many cells since the last completed scan - the base-building",
			"centroid drifts a little with every building added or lost, but a real relocation (new main base,",
			"lost conyard) moves it much further than that.")]
		public readonly int BaseMoveThreshold = 8;

		[Desc("Same idea as BaseMoveThreshold, but for the enemy building count: skip the refresh cycle unless it",
			"has changed by more than this many buildings since the last completed scan. During active combat the",
			"count churns constantly from individual losses/builds that don't meaningfully change reachability.")]
		public readonly int EnemyBuildingCountTolerance = 2;

		[Desc("Radius (cells) around the base reference scanned for high-ground cliff edges.")]
		public readonly int HighGroundScanRadius = 18;

		[Desc("Terrain height at which no height advantage is granted (matches HeightAdvantageBonus.BaseHeight).")]
		public readonly int HighGroundBaseHeight = 2;

		[Desc("A chokepoint is only considered sealable with a wall if its passable corridor is at most this wide (cells).")]
		public readonly int ChokepointSealMaxWidth = 5;

		[Desc("The coarse chokepoint cell is snapped to the genuinely narrowest crossing within this radius (cells).")]
		public readonly int ChokepointSnapRadius = 3;

		[Desc("Ignore chokepoints in regions (domains) with fewer abstract graph nodes than this — tiny isolated",
			"areas like an unreachable mesa top are not tactically meaningful.")]
		public readonly int MinDomainNodes = 4;

		[Desc("Maximum width (cells) of a passable corridor still considered a Passage chokepoint. Wider gaps are open",
			"terrain, not bottlenecks.")]
		public readonly int MaxPassageWidth = 8;

		[Desc("Both sides of a Passage must flood to at least this many cells, else it is a dead end / non-separating",
			"gap and is discarded.")]
		public readonly int MinPassageSideCells = 24;

		[Desc("Merge chokepoints that end up within this many cells of each other (cuts duplicate markers/clutter).")]
		public readonly int ChokepointMergeRadius = 5;

		[Desc("How far (cells) a ramp seal may grow away from the cliff it is anchored at. A ramp is a cut",
			"through a cliff and is at most about this wide; without a bound the run of slope tiles it grows",
			"along is connected across half a rolling map and the seal swallows the map with it. Both edges",
			"of a wide ramp seed the growth, so a ramp up to roughly twice this wide still closes.")]
		public readonly int RampSealMaxSpread = 8;

		[Desc("Regions smaller than this (cells) are merged into their larger neighbour by dropping the",
			"barrier between them, repeated until no undersized region has anywhere left to merge into.",
			"A chokepoint can be a perfectly genuine bottleneck and still be far too minor a wrinkle to",
			"deserve a region of its own - a coastline pinched every few cells came out as a chain of",
			"15/35/47-cell regions, which is not how anybody reads that ground. 0 disables merging.")]
		public readonly int MinRegionSize = 60;

		[Desc("Fallback minimum number of passable cells beyond a chokepoint before treating it as a real access.",
			"Used only when no enemy buildings exist; otherwise the far side must contain an enemy building.")]
		public readonly int MinimumChokepointBeyondCells = 96;

		[Desc("Hard cap on cells visited by the per-chokepoint 'leads somewhere' flood-fill, regardless of whether",
			"enemy buildings exist. Without this, a chokepoint whose reachable region contains no enemy building",
			"floods the ENTIRE region before giving up - unbounded on an open map. Treated as 'not confirmed' if hit.")]
		public readonly int FloodFillCellCap = 1500;

		[Desc("Hard cap on cells visited while working out which ground belongs to whom. A safety net, not",
			"a tuning knob: it counts every claim in the same walk, so at 5000 two territories of 2900 and",
			"2200 cells hit it between them and the walk stopped with most of the map - and nearly every",
			"chokepoint on it - never reached. Sized to cover a whole map instead.")]
		public readonly int TerritoryCellCap = 40000;

		[Desc("How far from its own buildings a player's territory reaches while no enemy building is known.",
			"Once one is, the split is decided by which side reaches a cell in fewer steps instead.")]
		public readonly int TerritoryUnopposedRadius = 40;

		[Desc("Ticks between territory rebuilds. Also gated on the base having moved or the enemy building",
			"count having changed, so a settled game recomputes almost never.")]
		public readonly int TerritoryRefreshInterval = 250;

		[Desc("Ticks between region-ownership rescans. The region shape itself is computed once and shared;",
			"only which player's buildings dominate each region is refreshed on a timer, coordinated across",
			"bots via CNSharedTopology so only one of them does the scan per interval.")]
		public readonly int RegionOwnershipRefreshInterval = 250;

		[Desc("Cells counted behind a door before the measurement stops. A door opening onto more ground than",
			"this is simply 'wide open' - the exact figure stops mattering well before the cap.")]
		public readonly int DoorBeyondCellCap = 1200;

		[Desc("Doors narrower than this are ignored: a one-cell gap in a cliff that units barely thread",
			"through is not what a defence line is planned around.")]
		public readonly int MinDoorWidth = 1;

		[Desc("Widest a gap may be and still count as a door. Past this the terrain is not pinching",
			"anything and no arrangement of turrets closes it - that is open front, held with an army",
			"or not at all.")]
		public readonly int MaxDoorWidth = 14;

		[Desc("Cells the flood behind a door must reach before it counts as a real way in. A pinch that",
			"opens onto almost nothing is a dead end, not something worth anchoring a defence at. Same",
			"figure as MinimumChokepointBeyondCells - both are the same question, 'is what's behind this",
			"real', just asked for a different feature.")]
		public readonly int MinDoorGroundBeyond = 96;

		[Desc("Doors within this many cells of a wider door are folded into it instead of standing on",
			"their own. A single gap scanned at a couple of slightly different points, or two real gaps",
			"a few cells apart, is one way in to a human - not several lined up in a row.")]
		public readonly int DoorMergeRadius = 8;

		[Desc("Weight given to a fully open territory door (GroundBeyond at DoorBeyondCellCap), scaled down",
			"for narrower ones but never below PassageWeight - MinDoorGroundBeyond already means 'this is a",
			"real way in', so the weakest qualifying door still has to compete like one. Same scale as",
			"BridgeWeight so a door-sourced and a chokepoint-sourced threat compose identically wherever",
			"both feed the same score.")]
		public readonly int DoorDefenseWeight = 200;

		[Desc("How far behind a door (cells, opposite Outward) the kill-zone wall band sits. Bigger than the",
			"3-cell GetDoorDefenseAnchors offset on purpose, so the already-anchored defence ends up INSIDE",
			"the walled zone, not sitting on its edge.")]
		public readonly int DoorKillZoneRadius = 7;

		public override object Create(ActorInitializer init) { return new CNTacticalMapBotModule(init.Self, this); }
	}

	public class CNTacticalMapBotModule : ConditionalTrait<CNTacticalMapBotModuleInfo>, IBotTick, IWorldLoaded
	{
		readonly World world;
		readonly Player player;

		PathFinder pathFinder;
		Locomotor locomotor;

		readonly List<CNChokepoint> chokepoints = [];
		IReadOnlyDictionary<CPos, uint> abstractDomains;
		CPos[] nodeCells = [];

		// Region shape (Cells/DoorCorridorIndices/AdjacentRegionIds/counts) is small and copied per-instance
		// like chokepoints; RegionIdByCell is heavy like Passability, so it is only ever referenced, never copied.
		readonly List<CNRegion> regions = [];
		readonly List<CNSealableCorridor> regionDoorCorridors = [];
		CellLayer<int> regionIdByCell;
		HashSet<CPos> regionBarrier = [];
		IResourceLayer resourceLayer;

		// The terrain-only topology is built once per (world, locomotor) and shared across all bots. The first bot to
		// build publishes here; the rest adopt it. Generation bumps on a (shared) bridge-change rebuild.
		static readonly ConditionalWeakTable<World, Dictionary<string, CNSharedTopology>> Registries = [];
		CNSharedTopology shared;
		int sharedGeneration = -1;

		// True once the topology has been built. Exposed so the render overlay can read without triggering a build.
		public bool TopologyReady { get; private set; }

		// Passability signature of bridge chokepoints, used to detect destroyed/repaired bridges cheaply.
		readonly List<CPos> bridgeWatchCells = [];
		bool lastBridgeSignature;
		int recheckTick;
		int recomputeTick;

		// High-ground edges and sealable corridors are base-relative; computed on the sim thread (throttled / spread)
		// and read as snapshots by the base builder, so the per-query path never pays the cost.
		// Refreshed INCREMENTALLY (a few cells per tick) to avoid a periodic burst - see TickHighGroundRefresh.
		readonly List<CNHighGroundEdge> highGroundEdges = [];
		readonly List<CNHighGroundEdge> highGroundBuilding = [];
		readonly List<CPos> highGroundSource = [];
		int highGroundCursor = -1;
		int highGroundRefreshTick;
		CPos highGroundBaseRef;
		int highGroundBaseHeight;
		CPos? highGroundLastBaseRef;

		// Sealable corridors are refreshed INCREMENTALLY (a few chokepoints per tick) to avoid a periodic burst.
		readonly List<CNSealableCorridor> sealableCorridors = [];
		readonly List<CNSealableCorridor> corridorBuilding = [];
		readonly List<CNChokepoint> corridorSource = [];
		int corridorCursor = -1;
		int corridorNextRefreshTick;
		CPos corridorBaseRef;
		HashSet<CPos> corridorEnemy;
		HashSet<CPos> corridorSeen;
		CPos? corridorLastBaseRef;
		int corridorLastEnemyCount = -1;

		// "Useful" chokepoints (reachable from own base, leading somewhere). The per-chokepoint flood is expensive, so
		// it is computed INCREMENTALLY on the sim thread (a few per tick) into usefulBuilding, then swapped into
		// usefulChokepoints. Queries (base builder, overlay) just read the last completed snapshot.
		readonly List<CNChokepoint> usefulChokepoints = [];
		readonly List<CNChokepoint> usefulBuilding = [];
		int usefulCursor = -1;
		int usefulNextRefreshTick;
		uint usefulDomain;
		CPos usefulReference;
		HashSet<CPos> usefulEnemyBuildings;
		CPos? usefulLastBaseRef;
		int usefulLastEnemyCount = -1;

		// Terrain passability cached once per rebuild (avoids repeated Locomotor.MovementCostForCell in scans/floods).
		CellLayer<bool> passability;

		public CNTacticalMapBotModule(Actor self, CNTacticalMapBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			pathFinder = world.WorldActor.TraitOrDefault<PathFinder>();
			resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();

			if (Info.TopologyLocomotors.Count > 0)
				locomotor = world.WorldActor.TraitsImplementing<Locomotor>()
					.FirstOrDefault(l => Info.TopologyLocomotors.Contains(l.Info.Name));

			// Fallback: first locomotor that actually allows ground movement.
			locomotor ??= world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.TerrainSpeeds.Values.Any(t => t.Cost < short.MaxValue));

			base.Created(self);
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// Build the (terrain-only) topology up front so bots never pay the one-time scan as a mid-game hitch.
			// Deferred to a frame-end task so it runs AFTER PathFinder.WorldLoaded has created its abstract graph
			// (trait WorldLoaded order is not guaranteed). Gated to bot players.
			if (player.IsBot)
				w.AddFrameEndTask(_ => EnsureBuilt());
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (!TopologyReady)
				return;

			// Advance the incremental 'useful' refresh on the sim thread (a few chokepoints per tick), so neither the
			// base builder nor the render overlay ever pays the full per-chokepoint flood in a single tick.
			TickUsefulRefresh();
			TickCorridorRefresh();
			TickHighGroundRefresh();
			TickTerritoryRefresh();
			TickRegionOwnershipRefresh();

			// Another bot rebuilt the shared topology (e.g. after a bridge change) -> adopt the newer version.
			if (shared != null && shared.Generation != sharedGeneration)
			{
				Adopt(shared);
				return;
			}

			if (Info.RecomputeInterval > 0 && --recomputeTick <= 0)
			{
				recomputeTick = Info.RecomputeInterval;
				RebuildSharedOnBridgeChange();
				return;
			}

			if (--recheckTick > 0)
				return;

			recheckTick = Math.Max(1, Info.TopologyRecheckInterval);

			// A bridge being destroyed/repaired flips the passability of its cells -> rebuild the shared topology once.
			if (shared != null && CurrentBridgeSignature() != lastBridgeSignature)
				RebuildSharedOnBridgeChange();
		}

		bool CurrentBridgeSignature()
		{
			// Cheap aggregate: are all watched bridge cells still passable? Checked LIVE (not via the frozen passability
			// snapshot) so an actual bridge destroyed/repaired is detected. Only a handful of cells, so this is cheap.
			foreach (var cell in bridgeWatchCells)
				if (locomotor == null || !world.Map.Contains(cell)
					|| locomotor.MovementCostForCell(cell) == short.MaxValue)
					return false;

			return true;
		}

		void EnsureBuilt()
		{
			if (!TopologyReady)
				Rebuild();
		}

		void Rebuild()
		{
			// TopologyReady is set only on success, at the end. It used to be set here, before the
			// guards below — so a build that bailed out (no abstract graph yet, no locomotor) left the
			// module permanently "ready" with zero chokepoints, and EnsureBuilt, which only rebuilds
			// while !TopologyReady, never tried again. The tactical map stayed silently dead for the
			// whole match. BuildOwnTopology returning false is exactly the "graph not created yet"
			// case the WorldLoaded frame-end task is meant to avoid, so it must stay retryable.
			ResetPerBaseCaches();
			recheckTick = Math.Max(1, Info.TopologyRecheckInterval);
			recomputeTick = Math.Max(1, Info.RecomputeInterval);
			chokepoints.Clear();
			bridgeWatchCells.Clear();
			abstractDomains = null;
			nodeCells = [];
			passability = null;
			regions.Clear();
			regionDoorCorridors.Clear();
			regionIdByCell = null;
			shared = null;
			sharedGeneration = -1;

			if (pathFinder == null || locomotor == null)
				return;

			// Reuse another bot's build for the same locomotor if present; otherwise build it once and publish.
			var registry = Registries.GetValue(world, _ => []);
			var key = locomotor.Info.Name;
			if (registry.TryGetValue(key, out var existing) && existing != null)
			{
				Adopt(existing);
				TopologyReady = true;
				return;
			}

			if (!BuildOwnTopology())
				return;

			shared = Publish();
			registry[key] = shared;
			sharedGeneration = shared.Generation;
			TopologyReady = true;
		}

		void ResetPerBaseCaches()
		{
			// This state is per-bot (unlike the shared topology graph), so with several CN bots in one game
			// their refresh cycles would otherwise all start at tick 0 and stay locked in step forever at the
			// same interval - bursting together instead of spreading out. A one-time random phase offset per
			// cycle (kept apart from the other two as well) avoids that without needing any coordination.
			var interval = Math.Max(1, Info.UsefulRefreshInterval);

			highGroundEdges.Clear();
			highGroundBuilding.Clear();
			highGroundSource.Clear();
			highGroundCursor = -1;
			highGroundRefreshTick = world.WorldTick + world.LocalRandom.Next(interval);
			highGroundLastBaseRef = null;
			usefulChokepoints.Clear();
			usefulBuilding.Clear();
			usefulCursor = -1;
			usefulNextRefreshTick = world.WorldTick + world.LocalRandom.Next(interval);
			usefulLastBaseRef = null;
			usefulLastEnemyCount = -1;
			sealableCorridors.Clear();
			corridorBuilding.Clear();
			corridorSource.Clear();
			corridorCursor = -1;
			corridorLastBaseRef = null;
			corridorLastEnemyCount = -1;
			corridorNextRefreshTick = world.WorldTick + world.LocalRandom.Next(interval);
		}

		// Adopt a shared topology built by another bot: reference the heavy data, copy the small lists.
		void Adopt(CNSharedTopology sh)
		{
			shared = sh;
			sharedGeneration = sh.Generation;
			passability = sh.Passability;
			abstractDomains = sh.AbstractDomains;
			nodeCells = sh.NodeCells;
			lastBridgeSignature = sh.LastBridgeSignature;
			chokepoints.Clear();
			chokepoints.AddRange(sh.Chokepoints);
			bridgeWatchCells.Clear();
			bridgeWatchCells.AddRange(sh.BridgeWatchCells);
			regions.Clear();
			regions.AddRange(sh.Regions);
			regionDoorCorridors.Clear();
			regionDoorCorridors.AddRange(sh.RegionDoorCorridors);
			regionIdByCell = sh.RegionIdByCell;
			regionBarrier = sh.RegionBarrier;
			ResetPerBaseCaches();
		}

		CNSharedTopology Publish()
		{
			var sh = new CNSharedTopology
			{
				Passability = passability,
				AbstractDomains = abstractDomains,
				NodeCells = nodeCells,
				LastBridgeSignature = lastBridgeSignature,
				RegionIdByCell = regionIdByCell,
				RegionBarrier = regionBarrier,
			};
			sh.Chokepoints.AddRange(chokepoints);
			sh.BridgeWatchCells.AddRange(bridgeWatchCells);
			sh.Regions.AddRange(regions);
			sh.RegionDoorCorridors.AddRange(regionDoorCorridors);
			sh.RegionOwners = new Player[regions.Count];
			return sh;
		}

		// Rebuild the shared topology after a bridge change and bump the generation so other bots re-adopt it.
		void RebuildSharedOnBridgeChange()
		{
			if (shared == null || !BuildOwnTopology())
				return;

			shared.Passability = passability;
			shared.AbstractDomains = abstractDomains;
			shared.NodeCells = nodeCells;
			shared.LastBridgeSignature = lastBridgeSignature;
			shared.Chokepoints.Clear();
			shared.Chokepoints.AddRange(chokepoints);
			shared.BridgeWatchCells.Clear();
			shared.BridgeWatchCells.AddRange(bridgeWatchCells);

			// A bridge changing can split or merge regions (it changes which corridors resolve), so the
			// shape has to be rebuilt alongside the chokepoints that gate it. Ownership is reset rather
			// than carried over - the old region ids no longer mean anything.
			shared.Regions.Clear();
			shared.Regions.AddRange(regions);
			shared.RegionDoorCorridors.Clear();
			shared.RegionDoorCorridors.AddRange(regionDoorCorridors);
			shared.RegionIdByCell = regionIdByCell;
			shared.RegionBarrier = regionBarrier;
			shared.RegionOwners = new Player[regions.Count];
			shared.NextOwnershipRefreshTick = 0;

			shared.Generation++;
			sharedGeneration = shared.Generation;
			ResetPerBaseCaches();
		}

		// The heavy terrain scan (shared by all bots via Publish): fills passability, abstractDomains, nodeCells,
		// chokepoints, bridgeWatchCells and lastBridgeSignature. Returns false if the locomotor has no abstract graph.
		bool BuildOwnTopology()
		{
			chokepoints.Clear();
			bridgeWatchCells.Clear();

			// Terrain-only connectivity (BlockedByActor.None): topology should reflect permanent terrain (cliffs,
			// water, bridges, ramps), NOT trees/buildings/units, which would create fake chokepoints around obstacles.
			// Queried BEFORE the passability fill so a not-yet-created abstract graph costs nothing: this method is
			// retryable (see Rebuild), and the fill is a full-map pass we don't want to repeat on every failed attempt.
			var (graph, domains) = pathFinder.GetOverlayDataForLocomotor(locomotor, BlockedByActor.None);
			if (graph == null || domains == null || graph.Count == 0)
				return false;

			// Cache terrain passability once so the scans/floods below don't call Locomotor.MovementCostForCell repeatedly.
			passability = new CellLayer<bool>(world.Map);
			foreach (var c in world.Map.AllCells)
				passability[c] = locomotor.MovementCostForCell(c) != short.MaxValue;

			abstractDomains = domains;
			nodeCells = graph.Keys.ToArray();

			// Count abstract nodes per domain so we can ignore tiny, isolated regions (mesa tops, unreachable
			// islands). Those are their own domain with only a handful of nodes and produce meaningless chokepoints.
			var domainNodeCount = new Dictionary<uint, int>();
			foreach (var node in nodeCells)
				if (abstractDomains.TryGetValue(node, out var dom))
					domainNodeCount[dom] = (domainNodeCount.TryGetValue(dom, out var dc) ? dc : 0) + 1;

			// De-duplicate chokepoints that map to the same representative cell.
			var byCell = new Dictionary<CPos, CNChokepoint>();

			void AddTyped(CPos cell, uint domain, CNChokepointType type)
			{
				if (byCell.ContainsKey(cell))
					return;

				// Merge near-duplicate passages to cut clutter. Discrete points (ramps, bridges) are kept as-is.
				var mergeSq = Math.Max(0, Info.ChokepointMergeRadius) * Math.Max(0, Info.ChokepointMergeRadius);
				if (type == CNChokepointType.Passage && mergeSq > 0)
					foreach (var existing in byCell.Keys)
						if ((existing - cell).LengthSquared <= mergeSq)
							return;

				var weight = type switch
				{
					CNChokepointType.Bridge => Info.BridgeWeight,
					CNChokepointType.Ramp => Info.RampWeight,
					_ => Info.PassageWeight,
				};

				byCell[cell] = new CNChokepoint(cell, domain, type, weight);
				if (type == CNChokepointType.Bridge)
					bridgeWatchCells.Add(cell);
			}

			void Add(CPos cell, CNChokepointType type)
			{
				// Ignore chokepoints inside tiny, isolated regions (e.g. an unreachable mesa top): not tactically real.
				var domain = DomainOf(cell);
				if (domainNodeCount.TryGetValue(domain, out var nodes) && nodes < Math.Max(1, Info.MinDomainNodes))
					return;

				AddTyped(cell, domain, type);
			}

			// Chokepoints are found directly from the terrain (not from the coarse abstract graph, which over-produces
			// noise): narrow separating passages, cliff ramps (marked below + above), and bridges.
			// Order matters: the near-duplicate merge in AddTyped only drops a Passage that lands close
			// to an ALREADY REGISTERED chokepoint. Scanning passages first meant bridges and ramps did
			// not exist yet, so nothing could be merged away — and bridges/ramps are never merge-checked
			// themselves. A single bridge therefore produced three markers (B in the middle, P at each
			// approach), which then crowded out other approaches in GetTopologyHotspots' top-N.
			// Discrete features first, passages last, so ChokepointMergeRadius actually does its job.
			foreach (var center in ScanBridgeCenters())
				Add(center, CNChokepointType.Bridge);

			foreach (var endpoint in ScanRampEndpoints())
				Add(endpoint, CNChokepointType.Ramp);

			foreach (var center in ScanPassageCenters())
				Add(center, CNChokepointType.Passage);

			chokepoints.AddRange(byCell.Values);
			lastBridgeSignature = CurrentBridgeSignature();

			BuildRegions(domainNodeCount);
			return true;
		}

		// The map's shape, cut by the same chokepoint corridors that gate the territory walk PLUS every
		// cliff ramp's full footprint - computed once here (unseeded by any claim) so every bot reads the
		// same regions regardless of who, if anyone, owns them. A ramp only sealable-corridor-resolves to
		// a narrow gate cell when it happens to be the tightest crossing nearby; a wide or gentle one
		// otherwise let the flood walk straight through it, so a visible cliff with a walkable slope ended
		// up inside one region instead of splitting high ground from low. The full ramp footprint (not
		// just its two chokepoint marker endpoints) is barrier here regardless of whether it resolved to a
		// sealable corridor, because height is a real separation even where nothing is narrow enough to
		// wall. One flood-fill component per gap between barrier cells is one region; adjacency between
		// regions falls out of which ones share a barrier cell, whether or not that cell also happens to
		// carry a resolved door corridor.
		//
		// Not every barrier deserves to stand, though: a coastline pinched every few cells came out as a
		// chain of 15/35/47-cell regions, which is not how anybody reads that ground. So the fill runs
		// more than once - undersized regions merge by dropping the barrier piece between them and
		// filling again (see MinRegionSize), which recomputes every outline, count and adjacency instead
		// of patching finished records up by hand.
		void BuildRegions(Dictionary<uint, int> domainNodeCount)
		{
			var gate = new HashSet<CPos>();
			var corridors = ResolveChokepointCorridors(gate);

			// Kept for the whole run, not just for cellToCorridorIndex below: DoorCorridorIndices on every
			// finished region points in here, and the merge only ever drops barrier pieces - it never
			// renumbers this list - so the indices stay valid however many rounds the fill takes.
			regionDoorCorridors.Clear();
			regionDoorCorridors.AddRange(corridors);

			var cellToCorridorIndex = new Dictionary<CPos, int>();
			for (var i = 0; i < corridors.Count; i++)
				foreach (var cell in corridors[i].Cells)
					cellToCorridorIndex[cell] = i;

			// IsCliffRamp only flags a cell with a cliff immediately beside it, so on a ramp wider than a
			// couple of cells only its outermost columns qualify: the middle of the slope touches nothing
			// impassable and was never barrier at all, which leaves a hole straight through the middle of
			// the very thing being sealed - and the one-ring dilation below only ever covered a hole two
			// cells across. A ramp is one connected run of walkable slope, bounded by the cliff it cuts
			// through, so the flagged cells are grown across that run: the seal ends up the ramp's true
			// width, however wide it is, while open bumpy ground stays untouched because no cliff seeds it
			// in the first place.
			// Map.Ramp was tried as part of this run and taken back out: on this tileset a great many
			// ordinary ground tiles carry a nonzero ramp index, so the run was connected across most of the
			// map and the seal carpeted open ground - see the terrain census logged below, which is what
			// any further attempt here should be decided on rather than on what a ramp "ought" to look
			// like.
			var slopeCells = new HashSet<CPos>();
			var rampCells = new HashSet<CPos>();
			foreach (var c in world.Map.AllCells)
			{
				if (!IsPassable(c) || !IsHeightTransition(c))
					continue;

				slopeCells.Add(c);

				// Seeded from the cliff, so a hillside nowhere near one is never barrier however much it
				// slopes.
				if (HasCliffAbove(c))
					rampCells.Add(c);
			}

			// What the map is actually made of, because three attempts at sealing ramps were each argued
			// from a different guess about that and each was wrong in a different direction. Once per map,
			// alongside the region census below.
			var passableCells = 0;
			var slopeTileCells = 0;
			var transitionCells = 0;
			var cliffAdjacentCells = 0;
			var heightCensus = new Dictionary<byte, int>();
			foreach (var c in world.Map.AllCells)
			{
				if (!IsPassable(c))
					continue;

				passableCells++;
				var h = world.Map.Height[c];
				heightCensus[h] = heightCensus.GetValueOrDefault(h) + 1;

				if (world.Map.Ramp[c] != 0)
					slopeTileCells++;

				if (IsHeightTransition(c))
					transitionCells++;

				if (HasCliffAbove(c))
					cliffAdjacentCells++;
			}

			CNBotLog.Debug("terrain: {0} passable, {1} slope tiles, {2} height transitions, {3} cliff-adjacent | heights {4}",
				passableCells, slopeTileCells, transitionCells, cliffAdjacentCells,
				string.Join(" ", heightCensus.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));

			// Bounded, because slope tiles are not rare on rolling terrain: their connected run reaches
			// across half a map, and an unbounded growth swallowed it whole - the barrier covered open
			// ground everywhere and left regions of one and two cells behind. A ramp is a cut through a
			// cliff and is only so wide, and both of its edges seed, so the bound closes a ramp up to
			// roughly twice RampSealMaxSpread across while leaving open hillside alone.
			var maxSpread = Math.Max(1, Info.RampSealMaxSpread);
			var rampQueue = new Queue<(CPos Cell, int Depth)>();
			foreach (var seed in rampCells)
				rampQueue.Enqueue((seed, 0));

			while (rampQueue.Count > 0)
			{
				var (cell, depth) = rampQueue.Dequeue();
				if (depth >= maxSpread)
					continue;

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (slopeCells.Contains(next) && rampCells.Add(next))
						rampQueue.Enqueue((next, depth + 1));
				}
			}

			// The barrier is kept as individually droppable pieces - one per resolved corridor, one per
			// chokepoint that never resolved to one, one per physical ramp - because merging undersized
			// regions below works by dropping the piece that separates them and running the whole fill
			// again, rather than by editing finished CNRegion records. Recomputing boundaries, adjacency
			// and the resource/buildable counts by hand per merge is exactly the bespoke bookkeeping that
			// re-deriving door geometry by hand already cost this feature five attempts.
			// Droppable says whether the merge below may give a piece up. Corridors and chokepoint cells
			// may: a bottleneck can be too minor a wrinkle to deserve a region. A ramp may not - dropping
			// it merges high ground into low ground, which is the one separation the whole ramp barrier
			// exists for, and a small plateau is still a place in its own right rather than part of the
			// ground below it.
			var pieces = new List<(HashSet<CPos> Cells, bool Droppable)>();
			foreach (var corridor in corridors)
				pieces.Add(([.. corridor.Cells], true));

			foreach (var cell in gate)
				if (!cellToCorridorIndex.ContainsKey(cell))
					pieces.Add(([cell], true));

			// A one-cell-thin barrier line has two known ways to leak with 8-directional movement: the
			// flood corner-cuts diagonally between two ramp cells that only touch corner-to-corner, and
			// the flat lead-in/lead-out cells at a ramp's very top and bottom are not height transitions
			// at all, so nothing above flags them - exactly where a region flowed straight through in the
			// first /cntopo pass. Dilating by one ring closes both: thick enough that no diagonal gap fits,
			// and wide enough to cover the ramp's open ends too. Per ramp rather than over one combined
			// blob - same cells, but each physical ramp can then be dropped on its own.
			var rampVisited = new HashSet<CPos>();
			var rampPieces = 0;
			foreach (var start in rampCells)
			{
				if (!rampVisited.Add(start))
					continue;

				var component = ConnectedComponent(start, rampCells, rampVisited);
				var piece = new HashSet<CPos>(component);
				foreach (var cell in component)
					foreach (var dir in CVec.Directions)
					{
						var n = cell + dir;
						if (world.Map.Contains(n) && IsPassable(n))
							piece.Add(n);
					}

				pieces.Add((piece, false));
				rampPieces++;
			}

			var active = new bool[pieces.Count];
			for (var i = 0; i < active.Length; i++)
				active[i] = true;

			var minRegionSize = Math.Max(0, Info.MinRegionSize);
			var piecesDropped = 0;
			var round = 0;

			// Cheap insurance, not a tuning knob: dropping is monotone, so this terminates on its own -
			// and it all happens once inside BuildOwnTopology, never per tick.
			const int MaxMergeRounds = 10;

			for (; ; round++)
			{
				var barrier = new HashSet<CPos>();
				for (var i = 0; i < pieces.Count; i++)
					if (active[i])
						barrier.UnionWith(pieces[i].Cells);

				FloodRegions(barrier, cellToCorridorIndex, domainNodeCount);
				regionBarrier = barrier;

				if (minRegionSize == 0 || round >= MaxMergeRounds - 1)
					break;

				// A piece goes as soon as EITHER side of it is undersized: a 15-cell sliver beside a
				// 5000-cell region should fold into it at once, not wait for the big one to look too
				// small as well. An undersized pocket with nothing to merge into keeps its pieces and
				// stays small, the same way MinDomainNodes leaves unreachable pockets alone.
				var droppedThisRound = 0;
				for (var i = 0; i < pieces.Count; i++)
				{
					if (!active[i] || !pieces[i].Droppable || !SeparatesUndersizedRegion(pieces[i].Cells, minRegionSize))
						continue;

					active[i] = false;
					droppedThisRound++;
				}

				if (droppedThisRound == 0)
					break;

				piecesDropped += droppedThisRound;
			}

			CNBotLog.Debug(
				"regions: {0} barrier pieces ({1} ramps, {2} ramp cells, kept whatever happens), {3} dropped "
				+ "over {4} rounds -> {5} regions (min size {6})",
				pieces.Count, rampPieces, rampCells.Count, piecesDropped, round + 1, regions.Count, minRegionSize);
		}

		/// <summary>
		/// Whether dropping this barrier piece would merge an undersized region into a neighbour. Read off
		/// the finished fill rather than tracked during it: a piece is small, and what it separates is
		/// simply whatever <see cref="regionIdByCell"/> says on either side of it.
		/// </summary>
		bool SeparatesUndersizedRegion(HashSet<CPos> piece, int minRegionSize)
		{
			var touching = new HashSet<int>();
			foreach (var cell in piece)
				foreach (var dir in CVec.Directions)
				{
					var id = GetRegionIdAt(cell + dir);
					if (id >= 0)
						touching.Add(id);
				}

			// Nothing on the far side to merge into - a piece against the map edge, or buried inside a
			// wider barrier. Dropping it would gain nothing.
			if (touching.Count < 2)
				return false;

			foreach (var id in touching)
				if (regions[id].Size < minRegionSize)
					return true;

			return false;
		}

		/// <summary>
		/// One flood-fill pass over the map with the given barrier: every gap between barrier cells
		/// becomes one region, with its outline, adjacency and counts derived fresh. Called repeatedly by
		/// <see cref="BuildRegions"/> with a shrinking barrier, so everything a merge would otherwise have
		/// to patch up by hand is simply recomputed instead.
		/// </summary>
		void FloodRegions(HashSet<CPos> barrier, Dictionary<CPos, int> cellToCorridorIndex, Dictionary<uint, int> domainNodeCount)
		{
			regions.Clear();
			regionIdByCell = new CellLayer<int>(world.Map);
			foreach (var c in world.Map.AllCells)
				regionIdByCell[c] = -1;

			var minDomainNodes = Math.Max(1, Info.MinDomainNodes);
			var visited = new HashSet<CPos>(barrier);
			var regionsTouchingBarrierCell = new Dictionary<CPos, List<int>>();

			foreach (var start in world.Map.AllCells)
			{
				if (!IsPassable(start) || !visited.Add(start))
					continue;

				// Tiny isolated pockets (a mesa top, an unreachable sliver) are not a tactically real
				// region, same reasoning MinDomainNodes already applies to chokepoints in one.
				if (domainNodeCount.TryGetValue(DomainOf(start), out var nodes) && nodes < minDomainNodes)
					continue;

				var id = regions.Count;
				var cells = new List<CPos>();
				var doorIndices = new HashSet<int>();
				var touchedBarrierCells = new List<CPos>();
				var resourceCells = 0;
				var buildableCells = 0;
				var queue = new Queue<CPos>();
				queue.Enqueue(start);
				cells.Add(start);

				while (queue.Count > 0)
				{
					var cell = queue.Dequeue();
					regionIdByCell[cell] = id;

					if (resourceLayer != null && resourceLayer.GetResource(cell).Type != null)
						resourceCells++;

					if (world.Map.Ramp[cell] == 0)
						buildableCells++;

					foreach (var dir in CVec.Directions)
					{
						var next = cell + dir;
						if (!world.Map.Contains(next))
							continue;

						if (barrier.Contains(next))
						{
							if (cellToCorridorIndex.TryGetValue(next, out var corridorIndex))
								doorIndices.Add(corridorIndex);

							touchedBarrierCells.Add(next);
							continue;
						}

						if (!IsPassable(next) || !visited.Add(next))
							continue;

						cells.Add(next);
						queue.Enqueue(next);
					}
				}

				foreach (var barrierCell in touchedBarrierCells)
				{
					if (!regionsTouchingBarrierCell.TryGetValue(barrierCell, out var touching))
						regionsTouchingBarrierCell[barrierCell] = touching = [];

					if (!touching.Contains(id))
						touching.Add(id);
				}

				// A cell with any neighbour outside this region's own cell set is on the outline - a
				// separate pass over the finished set rather than tracked during the flood, since a
				// neighbour failing visited.Add there could mean either "already ours" or "someone else's
				// region", and only this region's own finished cell set can tell the two apart.
				var cellSet = new HashSet<CPos>(cells);
				var boundary = new List<CPos>();
				foreach (var cell in cells)
				{
					var isBoundary = false;
					foreach (var dir in CVec.Directions)
					{
						if (world.Map.Contains(cell + dir) && cellSet.Contains(cell + dir))
							continue;

						isBoundary = true;
						break;
					}

					if (isBoundary)
						boundary.Add(cell);
				}

				regions.Add(new CNRegion(id, cells.ToArray(), doorIndices.ToArray(), [], resourceCells,
					buildableCells, boundary.ToArray()));
			}

			// Adjacency falls out of which regions share a barrier cell - a resolved door corridor or an
			// unresolved cliff ramp both count. One pass over the (small) barrier-cell map rather than one
			// per region: most barrier cells touch exactly two regions, so this stays close to linear.
			var adjacency = new Dictionary<int, HashSet<int>>();
			foreach (var touching in regionsTouchingBarrierCell.Values)
			{
				if (touching.Count < 2)
					continue;

				foreach (var a in touching)
				{
					if (!adjacency.TryGetValue(a, out var set))
						adjacency[a] = set = [];

					foreach (var b in touching)
						if (b != a)
							set.Add(b);
				}
			}

			for (var id = 0; id < regions.Count; id++)
			{
				var region = regions[id];
				var adjacent = adjacency.TryGetValue(id, out var set) ? set.ToArray() : [];
				regions[id] = new CNRegion(id, region.Cells, region.DoorCorridorIndices, adjacent,
					region.ResourceCellCount, region.BuildableCellCount, region.BoundaryCells);
			}
		}

		// Directly scans the terrain for narrow separating passages: the narrowest cell of each bounded, thick-
		// shouldered through-corridor whose two sides are both substantial (not a dead end). One cell per local
		// minimum; near-duplicates are merged by the caller.
		IEnumerable<CPos> ScanPassageCenters()
		{
			var maxWidth = Math.Max(1, Info.MaxPassageWidth);
			var widthH = new Dictionary<CPos, int>();
			var widthV = new Dictionary<CPos, int>();

			foreach (var c in world.Map.AllCells)
			{
				if (!IsPassable(c))
					continue;

				var wh = CorridorWidth(c, new CVec(1, 0), maxWidth);
				if (wh <= maxWidth)
					widthH[c] = wh;

				var wv = CorridorWidth(c, new CVec(0, 1), maxWidth);
				if (wv <= maxWidth)
					widthV[c] = wv;
			}

			var centers = new List<CPos>();

			// Horizontal corridors (narrow in X): keep the narrowest along the corridor length (Y).
			foreach (var (c, w) in widthH)
			{
				var up = widthH.GetValueOrDefault(c + new CVec(0, 1), int.MaxValue);
				var dn = widthH.GetValueOrDefault(c + new CVec(0, -1), int.MaxValue);
				if (w <= up && w <= dn && IsRealSeparator(c, new CVec(1, 0)))
					centers.Add(c);
			}

			// Vertical corridors (narrow in Y): keep the narrowest along the corridor length (X).
			foreach (var (c, w) in widthV)
			{
				var l = widthV.GetValueOrDefault(c + new CVec(-1, 0), int.MaxValue);
				var r = widthV.GetValueOrDefault(c + new CVec(1, 0), int.MaxValue);
				if (w <= l && w <= r && IsRealSeparator(c, new CVec(0, 1)))
					centers.Add(c);
			}

			return centers;
		}

		// Width of the passable run through c along step, if it is a through-corridor (open along the perpendicular
		// length axis) bounded by thick (>=2 cell) impassable shoulders within maxWidth. Otherwise int.MaxValue.
		int CorridorWidth(CPos c, CVec step, int maxWidth)
		{
			var perp = new CVec(step.Y, step.X);
			if (!IsPassable(c + perp) || !IsPassable(c - perp))
				return int.MaxValue;

			var pos = WalkSide(c, step, maxWidth);
			if (pos < 0)
				return int.MaxValue;

			var neg = WalkSide(c, new CVec(-step.X, -step.Y), maxWidth);
			if (neg < 0)
				return int.MaxValue;

			return pos + neg + 1;
		}

		// Passable cells from c along step until a thick shoulder; returns that count, or -1 if no thick shoulder.
		int WalkSide(CPos c, CVec step, int maxWidth)
		{
			for (var i = 1; i <= maxWidth; i++)
			{
				var cell = c + step * i;
				if (!IsPassable(cell))
				{
					var beyond = c + step * (i + 1);
					return !world.Map.Contains(beyond) || !IsPassable(beyond) ? i - 1 : -1;
				}
			}

			return -1;
		}

		// True if blocking the narrow corridor through c (along step) separates two substantial regions — both
		// perpendicular sides flood to at least MinPassageSideCells. Drops dead ends and non-separating gaps.
		bool IsRealSeparator(CPos c, CVec step)
		{
			var perp = new CVec(step.Y, step.X);
			var barrier = new HashSet<CPos> { c };
			for (var i = 1; i <= Info.MaxPassageWidth; i++)
			{
				var p = c + step * i;
				if (!IsPassable(p))
					break;
				barrier.Add(p);
			}

			for (var i = 1; i <= Info.MaxPassageWidth; i++)
			{
				var nn = c + new CVec(-step.X, -step.Y) * i;
				if (!IsPassable(nn))
					break;
				barrier.Add(nn);
			}

			var min = Math.Max(1, Info.MinPassageSideCells);
			return FloodAtLeast(c + perp, barrier, min) && FloodAtLeast(c - perp, barrier, min);
		}

		bool FloodAtLeast(CPos seed, HashSet<CPos> barrier, int min)
		{
			if (!world.Map.Contains(seed) || !IsPassable(seed) || barrier.Contains(seed))
				return false;

			var visited = new HashSet<CPos>(barrier) { seed };
			var queue = new Queue<CPos>();
			queue.Enqueue(seed);
			var count = 0;
			while (queue.Count > 0)
			{
				var cell = queue.Dequeue();
				if (++count >= min)
					return true;

				foreach (var d in CVec.Directions)
				{
					var nx = cell + d;
					if (!world.Map.Contains(nx) || !IsPassable(nx) || !visited.Add(nx))
						continue;

					queue.Enqueue(nx);
				}
			}

			return false;
		}

		// Finds cliff ramps directly and yields a point below and above each (clustered: one ramp = two points).
		IEnumerable<CPos> ScanRampEndpoints()
		{
			var rampCells = new HashSet<CPos>();
			foreach (var c in world.Map.AllCells)
				if (IsPassable(c) && IsCliffRamp(c))
					rampCells.Add(c);

			var endpoints = new List<CPos>();
			var visited = new HashSet<CPos>();
			foreach (var start in rampCells)
			{
				if (!visited.Add(start))
					continue;

				var (bottom, top) = RampEndpoints(Centroid(ConnectedComponent(start, rampCells, visited)));
				if (bottom != null)
					endpoints.Add(bottom.Value);
				if (top != null)
					endpoints.Add(top.Value);
			}

			return endpoints;
		}

		// Finds bridges directly (terrain type "Bridge", bridge actor footprints, and MapEditorData-tagged
		// bridge actors) and yields one cell per bridge.
		IEnumerable<CPos> ScanBridgeCenters()
		{
			var bridgeCells = new HashSet<CPos>();
			foreach (var c in world.Map.AllCells)
				if (world.Map.GetTerrainInfo(c).Type == "Bridge")
					bridgeCells.Add(c);

			foreach (var b in world.ActorsWithTrait<Bridge>())
			{
				var bi = b.Actor.Info.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;

				foreach (var cell in bi.Tiles(b.Actor.Location))
					bridgeCells.Add(cell);
			}

			// High/elevated bridges (this mod's BRIDGE1/BRIDGE2/RAILBRDG1/RAILBRDG2) are pure decoration -
			// Immobile, OccupiesSpace: false, no Bridge trait, and nothing patches the terrain layer to type
			// "Bridge" under them since they are not destructible - so neither check above ever sees them,
			// and they fell through to being scanned as a plain Passage instead (real, just under-weighted
			// and missing from bridgeWatchCells - harmless there since they can never be destroyed). Every
			// bridge actor in this mod's rules, low or high, is tagged MapEditorData Categories: Bridge,
			// which is the one thing both families actually share.
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				var med = a.Info.TraitInfoOrDefault<MapEditorDataInfo>();
				if (med == null || !med.Categories.Contains("Bridge"))
					continue;

				var bi = a.Info.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;

				foreach (var cell in bi.Tiles(a.Location))
					bridgeCells.Add(cell);
			}

			var centers = new List<CPos>();
			var visited = new HashSet<CPos>();
			foreach (var start in bridgeCells)
			{
				if (!visited.Add(start))
					continue;

				centers.Add(Centroid(ConnectedComponent(start, bridgeCells, visited)));
			}

			return centers;
		}

		static List<CPos> ConnectedComponent(CPos start, HashSet<CPos> cells, HashSet<CPos> visited)
		{
			var comp = new List<CPos> { start };
			var queue = new Queue<CPos>();
			queue.Enqueue(start);
			while (queue.Count > 0)
			{
				var c = queue.Dequeue();
				foreach (var d in CVec.Directions)
				{
					var n = c + d;
					if (cells.Contains(n) && visited.Add(n))
					{
						comp.Add(n);
						queue.Enqueue(n);
					}
				}
			}

			return comp;
		}

		static CPos Centroid(List<CPos> cells)
		{
			long sx = 0;
			long sy = 0;
			foreach (var c in cells)
			{
				sx += c.X;
				sy += c.Y;
			}

			var centre = new CPos((int)(sx / cells.Count), (int)(sy / cells.Count));
			var rep = cells[0];
			var best = (rep - centre).LengthSquared;
			foreach (var c in cells)
			{
				var s = (c - centre).LengthSquared;
				if (s < best)
				{
					best = s;
					rep = c;
				}
			}

			return rep;
		}

		// A ramp: a walkable height transition AND flanked by a cliff (a higher, impassable neighbour = the cliff wall
		// the ramp cuts through). This excludes gentle hills / bumpy open ground that merely vary in height.
		bool IsCliffRamp(CPos cell)
		{
			return IsHeightTransition(cell) && HasCliffAbove(cell);
		}

		// The cliff-wall half of IsCliffRamp on its own: a higher, impassable neighbour. Split out because
		// the region barrier seeds from slope tiles as well, not only from cells that read as a walkable
		// height transition.
		bool HasCliffAbove(CPos cell)
		{
			var h = world.Map.Height[cell];
			foreach (var d in CVec.Directions)
			{
				var n = cell + d;
				if (world.Map.Contains(n) && !IsPassable(n) && world.Map.Height[n] > h)
					return true;
			}

			return false;
		}

		// Representative cells just below and just above a cliff ramp ("unten und oben"), instead of on the slope.
		(CPos? Bottom, CPos? Top) RampEndpoints(CPos cell)
		{
			CPos? transition = IsHeightTransition(cell) ? cell : null;
			if (transition == null)
				foreach (var d in CVec.Directions)
					if (IsHeightTransition(cell + d))
					{
						transition = cell + d;
						break;
					}

			if (transition == null)
				return (null, null);

			var t = transition.Value;
			var h = world.Map.Height[t];
			CVec downhill = default;
			CVec uphill = default;
			var haveDown = false;
			var haveUp = false;
			foreach (var d in CVec.Directions)
			{
				var n = t + d;
				if (!world.Map.Contains(n) || !IsPassable(n))
					continue;

				var nh = world.Map.Height[n];
				if (Math.Abs(nh - h) > 1)
					continue;

				if (nh < h && !haveDown)
				{
					downhill = d;
					haveDown = true;
				}
				else if (nh > h && !haveUp)
				{
					uphill = d;
					haveUp = true;
				}
			}

			var bottom = haveDown ? (CPos?)ProjectPassable(t, downhill, 2) : null;
			var top = haveUp ? (CPos?)ProjectPassable(t, uphill, 2) : null;
			return (bottom, top);
		}

		// Steps up to 'steps' cells along dir while passable, returning the furthest reachable cell.
		CPos ProjectPassable(CPos start, CVec dir, int steps)
		{
			var c = start;
			for (var i = 0; i < steps; i++)
			{
				var n = c + dir;
				if (!world.Map.Contains(n) || !IsPassable(n))
					break;

				c = n;
			}

			return c;
		}

		bool IsHeightTransition(CPos cell)
		{
			if (!world.Map.Contains(cell))
				return false;

			var h = world.Map.Height[cell];
			var lower = false;
			var higher = false;
			foreach (var d in CVec.Directions)
			{
				var n = cell + d;
				if (!world.Map.Contains(n) || !IsPassable(n))
					continue;

				var nh = world.Map.Height[n];

				// Skip cliff jumps (height diff > 1 is not walkable); we only want actual walkable transitions.
				if (Math.Abs(nh - h) > 1)
					continue;

				if (nh < h)
					lower = true;
				else if (nh > h)
					higher = true;
			}

			return lower && higher;
		}

		bool IsPassable(CPos cell)
		{
			if (!world.Map.Contains(cell))
				return false;

			if (passability != null)
				return passability[cell];

			return locomotor != null && locomotor.MovementCostForCell(cell) != short.MaxValue;
		}

		uint DomainOf(CPos reference)
		{
			if (nodeCells.Length == 0)
				return 0;

			var best = nodeCells[0];
			var bestDist = (best - reference).LengthSquared;
			foreach (var node in nodeCells)
			{
				var dist = (node - reference).LengthSquared;
				if (dist < bestDist)
				{
					bestDist = dist;
					best = node;
				}
			}

			return abstractDomains.TryGetValue(best, out var d) ? d : 0;
		}

		// Throttled: recomputing this per query (a flood + enemy-building scan per chokepoint) caused frame spikes,
		// because the base builder queries topology very frequently. Usefulness is base-relative and changes slowly.
		// Pure read of the last completed snapshot. The refresh runs incrementally on the sim thread (see below).
		IReadOnlyList<CNChokepoint> GetUsefulChokepoints() => usefulChokepoints;

		// Advances the incremental 'useful' refresh by one chunk. Called from BotTick (sim thread) so the cost of the
		// per-chokepoint flood is spread over several ticks instead of bursting every UsefulRefreshInterval ticks.
		void TickUsefulRefresh()
		{
			if (chokepoints.Count == 0)
				return;

			if (usefulCursor < 0)
			{
				// Idle: start a new pass once the interval has elapsed.
				if (world.WorldTick < usefulNextRefreshTick)
					return;

				var reference = GetOwnBaseReference();
				if (reference == null)
				{
					usefulNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					return;
				}

				// One world.Actors pass, not two: the count pre-check and the position set are the same
				// filter, and world.Actors is the whole map. The set is discarded again if the pre-check
				// decides nothing changed, which is far cheaper than walking every actor twice.
				var enemyCells = EnemyBuildingCells();

				// Skip if neither our base nor the enemy building count changed since the last completed scan.
				if (!BaseMoved(reference.Value, usefulLastBaseRef) && !EnemyBuildingCountChanged(enemyCells.Count, usefulLastEnemyCount))
				{
					usefulNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					return;
				}

				usefulLastBaseRef = reference.Value;
				usefulLastEnemyCount = enemyCells.Count;
				usefulReference = reference.Value;
				usefulDomain = DomainOf(usefulReference);
				usefulEnemyBuildings = enemyCells;
				usefulBuilding.Clear();
				usefulCursor = 0;
			}

			var end = Math.Min(chokepoints.Count, usefulCursor + Math.Max(1, Info.UsefulChunkSize));
			for (; usefulCursor < end; usefulCursor++)
			{
				var cp = chokepoints[usefulCursor];
				if (cp.Domain != usefulDomain)
					continue;

				if (HasRealAccessBeyond(usefulReference, cp, usefulEnemyBuildings))
					usefulBuilding.Add(cp);
			}

			if (usefulCursor >= chokepoints.Count)
			{
				usefulChokepoints.Clear();
				usefulChokepoints.AddRange(usefulBuilding);
				usefulCursor = -1;
				usefulNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
			}
		}

		bool HasRealAccessBeyond(CPos reference, CNChokepoint cp, HashSet<CPos> enemyBuildings)
		{
			var hasEnemyBuildings = enemyBuildings.Count > 0;
			var beyondLimit = Math.Max(1, Info.MinimumChokepointBeyondCells);
			var forward = cp.Cell - reference;
			if (forward.LengthSquared == 0)
				return true;

			// Block the local pinch line (perpendicular to the approach, extended until impassable) so the flood cannot
			// wrap around the chokepoint back to the base side. Otherwise a coastal nook / dead end always looks "open".
			var perp = Math.Abs(forward.X) < Math.Abs(forward.Y) ? new CVec(1, 0) : new CVec(0, 1);
			const int MaxPinch = 16;
			var barrier = new HashSet<CPos> { cp.Cell };
			foreach (var sign in new[] { 1, -1 })
			{
				for (var k = 1; k <= MaxPinch; k++)
				{
					var c = cp.Cell + perp * (sign * k);
					if (!world.Map.Contains(c) || !IsPassable(c))
						break;

					barrier.Add(c);
				}
			}

			var seeds = new List<CPos>();
			if (forward.X != 0)
				seeds.Add(cp.Cell + new CVec(Math.Sign(forward.X), 0));
			if (forward.Y != 0)
				seeds.Add(cp.Cell + new CVec(0, Math.Sign(forward.Y)));
			if (forward.X != 0 && forward.Y != 0)
				seeds.Add(cp.Cell + new CVec(Math.Sign(forward.X), Math.Sign(forward.Y)));

			var queue = new Queue<CPos>();
			var visited = new HashSet<CPos>(barrier);
			foreach (var seed in seeds)
			{
				if (!world.Map.Contains(seed) || !IsPassable(seed) || !visited.Add(seed))
					continue;

				queue.Enqueue(seed);
			}

			var farCount = 0;
			var cellCap = Math.Max(beyondLimit, Info.FloodFillCellCap);
			while (queue.Count > 0)
			{
				var cell = queue.Dequeue();
				if (hasEnemyBuildings && enemyBuildings.Contains(cell))
					return true;

				farCount++;
				if (!hasEnemyBuildings && farCount >= beyondLimit)
					return true;

				// Hard safety cap: with enemy buildings present, there is otherwise no bound - a chokepoint whose
				// reachable region doesn't happen to contain one would flood the entire region before giving up.
				if (farCount >= cellCap)
					return false;

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (!world.Map.Contains(next) || !IsPassable(next) || !visited.Add(next))
						continue;

					queue.Enqueue(next);
				}
			}

			return false;
		}

		/// <summary>Chokepoints reachable from the reference, as defensive placement threats (proactive, static).</summary>
		public IEnumerable<CNBaseBuilderBotModule.DefensePlacementThreat> GetTopologyHotspots(CPos reference)
		{
			EnsureBuilt();
			if (chokepoints.Count == 0)
				return [];

			var maxDistSq = Math.Max(1, Info.TopologyMaxDistance) * Math.Max(1, Info.TopologyMaxDistance);

			return GetUsefulChokepoints()
				.Select(c => (Chokepoint: c, DistSq: (c.Cell - reference).LengthSquared))
				.Where(c => c.DistSq <= maxDistSq)
				.OrderByDescending(c => c.Chokepoint.BaseWeight - c.DistSq / 16)
				.Take(Math.Max(1, Info.MaxTopologyHotspots))
				.Select(c => new CNBaseBuilderBotModule.DefensePlacementThreat(c.Chokepoint.Cell, c.Chokepoint.BaseWeight))
				.ToArray();
		}

		/// <summary>
		/// Territory doors as defensive placement threats, weighted by ground behind rather than by type.
		/// Deliberately territory-wide, not per-base like <see cref="GetTopologyHotspots"/> - a door does not
		/// belong to whichever base happens to be nearest it, and re-ranking the same small door list by
		/// distance to each base in turn is exactly the per-base scatter this was built to stop.
		/// </summary>
		public IReadOnlyList<CNBaseBuilderBotModule.DefensePlacementThreat> GetDoorHotspots()
		{
			var territoryDoors = GetTerritoryDoors();
			if (territoryDoors.Count == 0)
				return [];

			var cap = Math.Max(1, Info.DoorBeyondCellCap);
			var minBeyond = Math.Min(cap, Math.Max(0, Info.MinDoorGroundBeyond));
			var floor = Info.PassageWeight;
			var ceiling = Math.Max(floor, Info.DoorDefenseWeight);

			return territoryDoors
				.Select(d =>
				{
					var beyond = Math.Min(d.GroundBeyond, cap);
					var weight = minBeyond >= cap
						? ceiling
						: floor + (ceiling - floor) * (beyond - minBeyond) / (cap - minBeyond);
					return new CNBaseBuilderBotModule.DefensePlacementThreat(d.Center, weight);
				})
				.ToArray();
		}

		/// <summary>
		/// Positions behind a territory door, facing the approach - same shape as
		/// <see cref="GetChokepointDefenseAnchors"/>, but the axis and side are already known from the door
		/// itself instead of having to be resigned against a reference each call.
		/// </summary>
		public IReadOnlyList<CPos> GetDoorDefenseAnchors()
		{
			var anchors = new List<CPos>();
			foreach (var door in GetTerritoryDoors())
			{
				if (door.Outward == CVec.Zero)
					continue;

				var approach = DoorApproachAxis(door);
				var lateral = new CVec(-approach.Y, approach.X);
				var behind = door.Center - approach * 3; // same offset GetChokepointDefenseAnchors uses

				foreach (var offset in new[] { 0, -2, 2, -4, 4 })
					anchors.Add(behind + lateral * offset);
			}

			return anchors;
		}

		// Doors are axis-aligned corridors (see ResolveChokepointCorridors), so Outward - summed over every
		// cell in the run in MakeDoor - stays dominated by one axis even though it is not unit length itself.
		public static CVec DoorApproachAxis(CNTerritoryDoor door) =>
			Math.Abs(door.Outward.X) < Math.Abs(door.Outward.Y)
				? new CVec(0, Math.Sign(door.Outward.Y))
				: new CVec(Math.Sign(door.Outward.X), 0);

		/// <summary>
		/// A band of cells behind a territory door - candidate wall cells for a set-back "kill zone", as
		/// opposed to sealing the door itself. Half of an annulus centered on the door, kept only on the
		/// territory side (opposite Outward) so the door stays passable and the zone's open side faces it.
		/// No radius parameter - always Info.DoorKillZoneRadius, so wall placement and the debug overlay can
		/// never disagree on which radius is in play.
		/// </summary>
		public IReadOnlyList<CPos> GetDoorKillZoneCells(CNTerritoryDoor door)
		{
			var radius = Math.Max(1, Info.DoorKillZoneRadius);
			if (door.Outward == CVec.Zero)
				return [];

			var approach = DoorApproachAxis(door);
			var band = Math.Min(radius - 1, 2);
			var cells = new List<CPos>();
			foreach (var cell in world.Map.FindTilesInAnnulus(door.Center, radius - band, radius))
			{
				var delta = cell - door.Center;
				var behind = approach.X != 0
					? Math.Sign(delta.X) != Math.Sign(approach.X)
					: Math.Sign(delta.Y) != Math.Sign(approach.Y);
				if (behind)
					cells.Add(cell);
			}

			return cells;
		}

		/// <summary>Bearings (from reference) toward each reachable access point, for the sealed-flank penalty.</summary>
		public IReadOnlyList<CVec> GetAccessBearings(CPos reference)
		{
			EnsureBuilt();
			if (chokepoints.Count == 0)
				return [];

			return GetUsefulChokepoints()
				.Select(c => c.Cell - reference)
				.Where(v => v.LengthSquared > 0)
				.ToList();
		}

		public IReadOnlyList<CNChokepoint> GetChokepoints()
		{
			EnsureBuilt();
			return chokepoints;
		}

		// Pure reads for the debug overlay (render thread): never build or recompute — just read what the sim produced.
		public IReadOnlyList<CNChokepoint> ChokepointsForOverlay() => chokepoints;
		public IReadOnlyList<CNChokepoint> UsefulForOverlay() => usefulChokepoints;

		// The chokepoints this bot actually acts on (reachable from its base and leading to a real region, not a dead end).
		// Used by the debug overlay to distinguish "detected" from "acted-on".
		public IReadOnlyList<CNChokepoint> GetUsefulChokepointsForOwnBase()
		{
			EnsureBuilt();
			return GetUsefulChokepoints();
		}

		CPos? GetOwnBaseReference()
		{
			var sumX = 0L;
			var sumY = 0L;
			var n = 0;
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld || a.Owner != player || !a.Info.HasTraitInfo<BuildingInfo>())
					continue;

				sumX += a.Location.X;
				sumY += a.Location.Y;
				n++;
			}

			return n == 0 ? null : new CPos((int)(sumX / n), (int)(sumY / n));
		}

		// True if the base centroid has drifted far enough from the last completed scan to be a genuine
		// relocation (new main base, lost conyard) rather than the normal jitter from adding/losing a building.
		bool BaseMoved(CPos current, CPos? last)
		{
			if (last == null)
				return true;

			var threshold = Math.Max(0, Info.BaseMoveThreshold);
			return (current - last.Value).LengthSquared > threshold * threshold;
		}

		// Same idea as BaseMoved: ignore the normal churn of individual buildings gained/lost during combat,
		// only treat it as a real change once it drifts far enough to plausibly affect reachability.
		bool EnemyBuildingCountChanged(int current, int last)
		{
			if (last < 0)
				return true;

			return Math.Abs(current - last) > Math.Max(0, Info.EnemyBuildingCountTolerance);
		}

		/// <summary>
		/// Passable cliff-edge cells within range of the reference that overlook reachable lower ground.
		/// Turrets here gain the HeightAdvantageBonus and use the cliff as a natural wall.
		/// </summary>
		public IReadOnlyList<CNHighGroundEdge> GetHighGroundEdges(CPos reference) => highGroundEdges;

		// Incremental refresh on the sim thread: scan a few candidate cells per tick instead of the whole
		// circle (~1000 cells at the default radius) in one go. With one CNTacticalMapBotModule per bot and
		// no sharing of this base-relative state (unlike the topology graph, which is shared per locomotor),
		// an unchunked scan could land on the same tick for several bots at once in a multi-bot game.
		void TickHighGroundRefresh()
		{
			if (highGroundCursor < 0)
			{
				if (world.WorldTick < highGroundRefreshTick)
					return;

				var reference = GetOwnBaseReference();
				if (reference == null || locomotor == null)
				{
					highGroundRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					highGroundEdges.Clear();
					return;
				}

				// Base hasn't meaningfully moved since the last completed scan - the result would be identical,
				// so skip re-scanning and just check back next interval.
				if (!BaseMoved(reference.Value, highGroundLastBaseRef))
				{
					highGroundRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					return;
				}

				highGroundBaseRef = reference.Value;
				highGroundLastBaseRef = reference.Value;
				highGroundBaseHeight = world.Map.Height[highGroundBaseRef];
				highGroundBuilding.Clear();
				highGroundSource.Clear();

				if (highGroundBaseHeight <= Info.HighGroundBaseHeight)
				{
					// Base is not elevated; no height advantage to exploit. Nothing to scan this cycle.
					highGroundEdges.Clear();
					highGroundRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					return;
				}

				var radius = Math.Max(1, Info.HighGroundScanRadius);
				highGroundSource.AddRange(world.Map.FindTilesInCircle(highGroundBaseRef, radius));
				highGroundCursor = 0;
			}

			var end = Math.Min(highGroundSource.Count, highGroundCursor + Math.Max(1, Info.HighGroundChunkSize));
			for (; highGroundCursor < end; highGroundCursor++)
			{
				var cell = highGroundSource[highGroundCursor];
				if (world.Map.Height[cell] != highGroundBaseHeight || !IsPassable(cell))
					continue;

				// An edge cell borders a strictly lower, enemy-reachable cell: it overlooks the approach.
				CVec outward = default;
				var isEdge = false;
				foreach (var d in CVec.Directions)
				{
					var lower = cell + d;
					if (!world.Map.Contains(lower))
						continue;

					if (world.Map.Height[lower] < highGroundBaseHeight && IsPassable(lower))
					{
						outward = d;
						isEdge = true;
						break;
					}
				}

				if (isEdge)
					highGroundBuilding.Add(new CNHighGroundEdge(cell, outward,
						Math.Clamp(highGroundBaseHeight - Info.HighGroundBaseHeight, 0, 4)));
			}

			if (highGroundCursor >= highGroundSource.Count)
			{
				highGroundEdges.Clear();
				highGroundEdges.AddRange(highGroundBuilding);
				highGroundCursor = -1;
				highGroundRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
			}
		}

		/// <summary>
		/// Reachable chokepoints whose passable corridor is narrow enough to plug with a single axis-aligned wall line.
		/// The wall runs perpendicular to the approach; a matching gate goes at the corridor center.
		/// </summary>
		public IReadOnlyList<CNSealableCorridor> GetSealableCorridors(CPos reference) => sealableCorridors;

		// Incremental refresh on the sim thread: process a few useful chokepoints per tick (the snap search + dead-end
		// flood per chokepoint is the cost), build into corridorBuilding, then swap. Spreads what was a periodic burst.
		void TickCorridorRefresh()
		{
			if (corridorCursor < 0)
			{
				if (world.WorldTick < corridorNextRefreshTick)
					return;

				var reference = GetOwnBaseReference();
				if (reference == null || usefulChokepoints.Count == 0)
				{
					corridorNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					if (usefulChokepoints.Count == 0)
						sealableCorridors.Clear();
					return;
				}

				// Single world.Actors pass, as in TickUsefulRefresh.
				var enemyCells = EnemyBuildingCells();

				if (!BaseMoved(reference.Value, corridorLastBaseRef) && !EnemyBuildingCountChanged(enemyCells.Count, corridorLastEnemyCount))
				{
					corridorNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					return;
				}

				corridorLastBaseRef = reference.Value;
				corridorLastEnemyCount = enemyCells.Count;
				corridorBaseRef = reference.Value;
				corridorEnemy = enemyCells;
				corridorSeen = [];
				corridorBuilding.Clear();
				corridorSource.Clear();
				corridorSource.AddRange(usefulChokepoints); // snapshot, in case the useful set is swapped mid-pass
				corridorCursor = 0;
			}

			var maxWidth = Math.Max(1, Info.ChokepointSealMaxWidth);
			var maxDistSq = Math.Max(1, Info.TopologyMaxDistance) * Math.Max(1, Info.TopologyMaxDistance);
			var snapRadius = Math.Max(0, Info.ChokepointSnapRadius);
			var end = Math.Min(corridorSource.Count, corridorCursor + Math.Max(1, Info.UsefulChunkSize));
			for (; corridorCursor < end; corridorCursor++)
			{
				var cp = corridorSource[corridorCursor];
				if ((cp.Cell - corridorBaseRef).LengthSquared > maxDistSq)
					continue;

				var corridor = FindNarrowestCrossing(cp.Cell, snapRadius, maxWidth);
				if (corridor != null && corridorSeen.Add(corridor.Center)
					&& SealingLeadsSomewhere(corridorBaseRef, corridor, corridorEnemy))
					corridorBuilding.Add(corridor);
			}

			if (corridorCursor >= corridorSource.Count)
			{
				corridorBuilding.Sort((a, b) =>
					(a.Center - corridorBaseRef).LengthSquared.CompareTo((b.Center - corridorBaseRef).LengthSquared));
				sealableCorridors.Clear();
				sealableCorridors.AddRange(corridorBuilding);
				corridorCursor = -1;
				corridorNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
			}
		}

		// Searches near the coarse chokepoint cell for the narrowest passable crossing that is a genuine bottleneck:
		// bounded by thick (>=2 cell) impassable shoulders on both sides AND open along the approach axis.
		// This stops a single rock in open ground (or a wide field) from being mistaken for a sealable chokepoint.
		// Public: also used to search for a narrower fallback pinch behind a door that is a poor line to hold.
		public CNSealableCorridor FindNarrowestCrossing(CPos near, int snapRadius, int maxWidth)
		{
			CNSealableCorridor best = null;
			var bestWidth = int.MaxValue;

			foreach (var center in world.Map.FindTilesInCircle(near, snapRadius))
			{
				if (!IsPassable(center))
					continue;

				for (var axis = 0; axis < 2; axis++)
				{
					var wallRunsHorizontal = axis == 0;
					var step = wallRunsHorizontal ? new CVec(1, 0) : new CVec(0, 1);

					// A real passage is open along the approach (perpendicular) axis on both sides of the line.
					var approach = wallRunsHorizontal ? new CVec(0, 1) : new CVec(1, 0);
					if (!IsPassable(center + approach) || !IsPassable(center - approach))
						continue;

					var cells = new List<CPos> { center };
					if (!WalkToShoulder(center, step, maxWidth, cells) || !WalkToShoulder(center, -step, maxWidth, cells))
						continue;

					if (cells.Count <= maxWidth && cells.Count < bestWidth)
					{
						bestWidth = cells.Count;
						best = new CNSealableCorridor(center, wallRunsHorizontal, cells.ToArray());
					}
				}
			}

			return best;
		}

		/// <summary>
		/// Candidate defense cells on the base side of sealable chokepoints.
		/// These sit just behind the wall/gate line so turrets cover the forced path instead of blocking it.
		/// </summary>
		public IReadOnlyList<CPos> GetChokepointDefenseAnchors(CPos reference)
		{
			var anchors = new List<CPos>();
			foreach (var corridor in GetSealableCorridors(reference))
			{
				var approachAxis = corridor.WallRunsHorizontal ? new CVec(0, 1) : new CVec(1, 0);
				var lateralAxis = corridor.WallRunsHorizontal ? new CVec(1, 0) : new CVec(0, 1);
				var baseSide = (reference - corridor.Center).LengthSquared > 0
					? Math.Sign(corridor.WallRunsHorizontal ? reference.Y - corridor.Center.Y : reference.X - corridor.Center.X)
					: 0;

				if (baseSide == 0)
					continue;

				var behind = corridor.Center + approachAxis * (baseSide * 3);
				foreach (var offset in new[] { 0, -2, 2, -4, 4 })
				{
					var cell = behind + lateralAxis * offset;
					if (world.Map.Contains(cell) && IsPassable(cell))
						anchors.Add(cell);
				}
			}

			return anchors;
		}

		// True if the far side of the wall line is a genuine region (reaches an enemy building, or is large), rather
		// than a dead-end pocket. The whole wall line is used as a barrier so the flood cannot wrap back to the base
		// side — this is what actually distinguishes "a chokepoint into enemy territory" from "a wall against nothing".
		// Current enemy building locations. Built ONCE per query and passed into the per-chokepoint floods below —
		// scanning world.Actors per chokepoint (N x actors) was a periodic multi-ms spike.
		#region Territory and doors

		readonly HashSet<CPos> territory = [];
		readonly List<CNTerritoryDoor> doors = [];
		readonly List<CPos> territoryWall = [];
		readonly List<CPos> territoryFront = [];
		readonly HashSet<CPos> horizon = [];
		int territoryNextRefreshTick;
		CPos? territoryLastBaseRef;
		int territoryLastEnemyCount = -1;

		// Which measurement answered "how much ground does this door open onto" - counted per rebuild and
		// reported in the funnel log, for the same reason every other stage of that funnel is: a door
		// inside our own ground cannot be measured the normal way, and which fallback answered for it is
		// not something to work out from a single number on the overlay.
		int doorsMeasuredByRegion;
		int doorsMeasuredByPinch;
		int doorsUnmeasured;

		/// <summary>Ground this player holds: closer to its own buildings than to any known enemy one.</summary>
		public IReadOnlyCollection<CPos> GetTerritory() { EnsureBuilt(); return territory; }

		/// <summary>The ways into that ground. Everything else along its edge is terrain doing the work.</summary>
		public IReadOnlyList<CNTerritoryDoor> GetTerritoryDoors() { EnsureBuilt(); return doors; }

		/// <summary>The map's regions - a terrain fact computed once and shared, unowned by default. See <see cref="GetRegionOwner"/>.</summary>
		public IReadOnlyList<CNRegion> GetRegions() { EnsureBuilt(); return regions; }

		/// <summary>
		/// Bumped whenever the map is re-cut (a bridge falling can split or merge regions). Region ids are
		/// positions in <see cref="GetRegions"/>, so anything remembering something per id has to drop it
		/// when this changes - after a re-cut nothing says id 4 is the same ground it was.
		/// </summary>
		public int RegionGeneration => shared?.Generation ?? 0;

		/// <summary>Region id at a cell, or -1 if it is a gate/chokepoint cell or outside any region.</summary>
		public int GetRegionIdAt(CPos cell) => regionIdByCell != null && world.Map.Contains(cell) ? regionIdByCell[cell] : -1;

		/// <summary>
		/// The corridor a region's <see cref="CNRegion.DoorCorridorIndices"/> entry names, or null when the
		/// index does not resolve. This is how a region's doors get a width: the region itself only carries
		/// the indices.
		/// </summary>
		public CNSealableCorridor GetRegionDoorCorridor(int corridorIndex)
		{
			EnsureBuilt();
			return corridorIndex >= 0 && corridorIndex < regionDoorCorridors.Count ? regionDoorCorridors[corridorIndex] : null;
		}

		/// <summary>Whoever's buildings currently dominate a region, or null if unclaimed/contested/unknown.</summary>
		public Player GetRegionOwner(int regionId) =>
			shared?.RegionOwners is { } owners && regionId >= 0 && regionId < owners.Length ? owners[regionId] : null;

		/// <summary>Edge cells backed by cliff, water or map edge - free wall, nothing to build there.</summary>
		public IReadOnlyList<CPos> GetTerritoryWall() { EnsureBuilt(); return territoryWall; }

		/// <summary>Edge cells open to the enemy, or too wide to plug. Held with an army, not with turrets.</summary>
		public IReadOnlyList<CPos> GetTerritoryFront() { EnsureBuilt(); return territoryFront; }

		// Read-only views for the render thread. Deliberately without EnsureBuilt: the overlay must never
		// trigger a topology build from render-prepare, which is what used to cause periodic spikes.
		public IReadOnlyCollection<CPos> RegionBarrierForOverlay() => regionBarrier;
		public IReadOnlyCollection<CPos> TerritoryForOverlay() => territory;
		public IReadOnlyList<CNTerritoryDoor> DoorsForOverlay() => doors;
		public IReadOnlyList<CPos> TerritoryWallForOverlay() => territoryWall;
		public IReadOnlyList<CPos> TerritoryFrontForOverlay() => territoryFront;

		void TickTerritoryRefresh()
		{
			if (world.WorldTick < territoryNextRefreshTick)
				return;

			territoryNextRefreshTick = world.WorldTick + Math.Max(1, Info.TerritoryRefreshInterval);

			var reference = GetOwnBaseReference();
			if (reference == null)
				return;

			var enemyCells = EnemyBuildingCells();

			// Same guard the chokepoint refresh uses: individual buildings coming and going during a fight
			// do not move an edge, and rebuilding on every one of them would be the expensive way to
			// compute the same answer.
			if (!BaseMoved(reference.Value, territoryLastBaseRef) && !EnemyBuildingCountChanged(enemyCells.Count, territoryLastEnemyCount))
				return;

			territoryLastBaseRef = reference.Value;
			territoryLastEnemyCount = enemyCells.Count;

			RebuildTerritory(enemyCells);
		}

		/// <summary>
		/// Region ownership is a separate, cheap tally on top of the (rarely-changing) shared region
		/// shape - unlike the territory walk, it does not re-derive anything per bot: whichever bot's tick
		/// hits the shared refresh window does one pass over every building on the map and settles every
		/// region's owner (whoever's buildings are the majority there) in one go, publishing it back onto
		/// the shared object for every other bot to read.
		/// </summary>
		void TickRegionOwnershipRefresh()
		{
			if (shared == null || shared.Regions.Count == 0 || world.WorldTick < shared.NextOwnershipRefreshTick)
				return;

			// Claim the window immediately so two bots ticking in the same frame do not both redo the scan.
			shared.NextOwnershipRefreshTick = world.WorldTick + Math.Max(1, Info.RegionOwnershipRefreshInterval);

			var tally = new Dictionary<int, Dictionary<Player, int>>();
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld || !a.Info.HasTraitInfo<BuildingInfo>())
					continue;

				var regionId = GetRegionIdAt(a.Location);
				if (regionId < 0)
					continue;

				if (!tally.TryGetValue(regionId, out var byPlayer))
					tally[regionId] = byPlayer = [];

				byPlayer[a.Owner] = byPlayer.GetValueOrDefault(a.Owner) + 1;
			}

			var owners = new Player[shared.Regions.Count];
			foreach (var (regionId, byPlayer) in tally)
				owners[regionId] = byPlayer.OrderByDescending(kv => kv.Value).First().Key;

			shared.RegionOwners = owners;
		}

		/// <summary>
		/// Claims ground for this player, then reads its edge.
		/// <para>
		/// Ownership is settled by a race rather than by distance: own buildings and known enemy buildings
		/// are all seeded into one breadth-first walk, and whichever side reaches a cell first keeps it.
		/// That follows the ground - a cell ten cells away across a cliff belongs to whoever can actually
		/// get there - which is the whole point of doing this on the map instead of on a circle.
		/// </para>
		/// </summary>
		void RebuildTerritory(HashSet<CPos> enemyCells)
		{
			territory.Clear();
			doors.Clear();
			territoryWall.Clear();
			territoryFront.Clear();

			var ownCells = OwnBuildingCells();
			if (ownCells.Count == 0)
				return;

			// Chokepoints hold the walk like walls. That is what makes a territory a place rather than a
			// blob: ground is enclosed by cliffs and by the handful of gaps in them, and a claim that runs
			// straight through those gaps describes nothing anybody would defend.
			//
			// It also puts the doors on the boundary by construction. Three earlier attempts tried to cut
			// them back out of a claim that had already flowed past them, and each failed differently.
			//
			// The gate has to be the chokepoint's full width, not just its coarse marker cell - blocking
			// only the one cell let the walk leak around anything wider than a single file, which is what
			// fragmented one real gap into a chain of several ragged "doors" a cell or two apart. The width
			// is exactly what CNSealableCorridor already resolves, so it is looked up once here and reused
			// for both the gate and, in BuildDoors, the door itself.
			var gate = new HashSet<CPos>();
			var chokepointCorridors = ResolveChokepointCorridors(gate);

			var mine = new HashSet<CPos>();
			var theirs = new HashSet<CPos>();
			var claimed = new HashSet<CPos>();
			var queue = new Queue<(CPos Cell, bool Mine, int Dist)>();

			foreach (var cell in ownCells)
				if (world.Map.Contains(cell) && claimed.Add(cell))
				{
					mine.Add(cell);
					queue.Enqueue((cell, true, 0));
				}

			foreach (var cell in enemyCells)
				if (world.Map.Contains(cell) && claimed.Add(cell))
				{
					theirs.Add(cell);
					queue.Enqueue((cell, false, 0));
				}

			// Without a known enemy there is no race to lose, so the claim has to be bounded by something
			// else or it swallows the map.
			var unopposedLimit = enemyCells.Count == 0 ? Math.Max(1, Info.TerritoryUnopposedRadius) : int.MaxValue;
			var cap = Math.Max(1, Info.TerritoryCellCap);

			// Where the walk ran out of budget rather than out of ground. This edge is an artefact of the
			// bound, not a feature of the map, and reading it as one produced a "door" sixty cells wide
			// with the rest of the map behind it - the outside of a circle is open in every direction.
			horizon.Clear();

			while (queue.Count > 0)
			{
				var (cell, isMine, dist) = queue.Dequeue();
				if (dist >= unopposedLimit || claimed.Count >= cap)
				{
					if (isMine)
						horizon.Add(cell);

					continue;
				}

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (!world.Map.Contains(next) || !IsPassable(next) || gate.Contains(next) || !claimed.Add(next))
						continue;

					if (isMine)
						mine.Add(next);
					else
						theirs.Add(next);

					queue.Enqueue((next, isMine, dist + 1));
				}
			}

			territory.UnionWith(mine);
			BuildDoors(theirs, chokepointCorridors);
		}

		/// <summary>
		/// Resolves every scanned chokepoint to its genuine wall-to-wall corridor via
		/// <see cref="FindNarrowestCrossing"/> and folds each into <paramref name="gate"/> at full width.
		/// A chokepoint that fails to resolve to a real corridor (no thick shoulder in reach) still blocks
		/// its own coarse cell, so the walk cannot pass straight through a marker that turned out to be
		/// unresolvable.
		/// </summary>
		List<CNSealableCorridor> ResolveChokepointCorridors(HashSet<CPos> gate)
		{
			var corridors = new List<CNSealableCorridor>();
			var seenCenters = new HashSet<CPos>();
			var snapRadius = Math.Max(0, Info.ChokepointSnapRadius);
			var maxWidth = Math.Max(1, Info.MaxDoorWidth) + 1;

			foreach (var cp in chokepoints)
			{
				var corridor = FindNarrowestCrossing(cp.Cell, snapRadius, maxWidth);
				if (corridor == null)
				{
					gate.Add(cp.Cell);
					continue;
				}

				gate.UnionWith(corridor.Cells);
				if (seenCenters.Add(corridor.Center))
					corridors.Add(corridor);
			}

			return corridors;
		}

		/// <summary>
		/// Walks the edge of the claimed ground and splits it into what has to be held and what does not.
		/// An edge cell whose outside neighbour is driveable is a way in; one backed by cliff, water or the
		/// map edge is wall we did not have to build.
		/// </summary>
		void BuildDoors(HashSet<CPos> theirs, List<CNSealableCorridor> chokepointCorridors)
		{
			var doorCells = new HashSet<CPos>();
			doorsMeasuredByRegion = 0;
			doorsMeasuredByPinch = 0;
			doorsUnmeasured = 0;

			foreach (var cell in territory)
			{
				// The bound stopped the walk here, so what lies past this cell was never examined. Reading
				// it as an edge describes the budget, not the map.
				if (horizon.Contains(cell))
					continue;

				var isEdge = false;
				var isDoor = false;
				var isFront = false;

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (territory.Contains(next))
						continue;

					isEdge = true;

					if (!world.Map.Contains(next) || !IsPassable(next))
						continue;

					// Ground the enemy got to first is a front, not a way in. Nothing about the terrain
					// funnels anything here; it is simply where the two claims met, and it is held with an
					// army rather than with turrets.
					if (theirs.Contains(next))
						isFront = true;
					else
						isDoor = true;
				}

				if (!isEdge)
					continue;

				if (isDoor)
					doorCells.Add(cell);
				else if (isFront)
					territoryFront.Add(cell);
				else
					territoryWall.Add(cell);
			}

			// Chokepoint corridors standing on our own ground are doors in the line we would draw, whether
			// or not they sit on the edge of the claim. A ramp through a cliff in the middle of held
			// territory is exactly what a defence is anchored at, and it never shows up as a boundary cell
			// because both of its sides belong to us. The corridor already covers the chokepoint at full
			// width - it is what gated the walk in RebuildTerritory - so touching it is adjacency against
			// the whole gap, not just its coarse marker cell.
			var chokepointGateCells = new HashSet<CPos>();
			var candidates = new List<CNSealableCorridor>();
			foreach (var corridor in chokepointCorridors)
			{
				chokepointGateCells.UnionWith(corridor.Cells);

				var touches = false;
				foreach (var cell in corridor.Cells)
				{
					foreach (var dir in CVec.Directions)
					{
						if (!territory.Contains(cell + dir))
							continue;

						touches = true;
						break;
					}

					if (touches)
						break;
				}

				if (touches)
					candidates.Add(corridor);
			}

			var adjacentOnly = candidates.Count;

			// Anything left in doorCells that is not part of a scanned chokepoint is an open edge nothing
			// was recorded for - rare, since the two claims otherwise tile the whole reachable map between
			// them, but resolved through the same lookup rather than left unclassified.
			var runsNoCorridor = 0;
			var snapRadius = Math.Max(0, Info.ChokepointSnapRadius);
			var maxWidth = Math.Max(1, Info.MaxDoorWidth) + 1;
			var seenCenters = new HashSet<CPos>(candidates.Select(c => c.Center));
			foreach (var start in doorCells)
			{
				if (chokepointGateCells.Contains(start))
					continue;

				var corridor = FindNarrowestCrossing(start, snapRadius, maxWidth);
				if (corridor == null)
				{
					// No thick shoulder within reach on either axis - nothing here actually funnels anything,
					// so it is front rather than a gate that simply failed to resolve.
					runsNoCorridor++;
					territoryFront.Add(start);
					continue;
				}

				if (seenCenters.Add(corridor.Center))
					candidates.Add(corridor);
			}

			// Doors that sit close together read as one way in to a human, not several lined up in a row -
			// a gap scanned at a couple of slightly different snap points, or two real gaps a few cells
			// apart. Widest first, so the one that actually leads somewhere is what survives the merge
			// rather than whichever happened to be found first.
			candidates.Sort((a, b) => b.Cells.Length.CompareTo(a.Cells.Length));
			var accepted = new List<CNSealableCorridor>();
			var mergeRadiusSq = Math.Max(0, Info.DoorMergeRadius) * Math.Max(0, Info.DoorMergeRadius);
			var runsMerged = 0;
			foreach (var corridor in candidates)
			{
				var mergedAway = false;
				foreach (var kept in accepted)
				{
					if ((corridor.Center - kept.Center).LengthSquared <= mergeRadiusSq)
					{
						mergedAway = true;
						break;
					}
				}

				if (mergedAway)
					runsMerged++;
				else
					accepted.Add(corridor);
			}

			var runsTooWide = 0;
			var runsTooNarrow = 0;
			var runsTooShallow = 0;
			foreach (var corridor in accepted)
			{
				if (corridor.Cells.Length < Math.Max(1, Info.MinDoorWidth))
				{
					runsTooNarrow++;
					continue;
				}

				// Too wide to be a door. Terrain is not funnelling anything through a gap this size, and
				// no arrangement of turrets closes it - treat it as front and let an army hold it.
				if (corridor.Cells.Length > Math.Max(1, Info.MaxDoorWidth))
				{
					runsTooWide++;
					territoryFront.AddRange(corridor.Cells);
					continue;
				}

				var door = MakeDoor(corridor.Cells.ToList());

				// A pinch that opens onto almost nothing is a dead end, not a way in - the ground behind it
				// has to be worth defending before it counts as one.
				if (door.GroundBeyond < Math.Max(0, Info.MinDoorGroundBeyond))
				{
					runsTooShallow++;
					continue;
				}

				doors.Add(door);
			}

			// Widest first: the door that lets the most through is the one a defence is built at.
			doors.Sort((a, b) => b.GroundBeyond.CompareTo(a.GroundBeyond));

			// Every step of the funnel, because three attempts at this produced no doors and each time the
			// screen could only report the total. Which stage empties the list is not something to keep
			// guessing at.
			CNBotLog.Debug(
				"{0} territory: {1} cells, {2} wall, {3} front, {4} horizon | {5} chokepoints, {6} corridors "
				+ "touching this territory | {7} extra edge cells ({8} no corridor) -> {9} candidates -> "
				+ "{10} merged away -> {11} doors ({12} too wide, {13} too narrow, {14} too shallow) | "
				+ "beyond: {15} by region, {16} by pinch, {17} nothing answered",
				player, territory.Count, territoryWall.Count, territoryFront.Count, horizon.Count,
				chokepoints.Count, adjacentOnly,
				doorCells.Count, runsNoCorridor, candidates.Count, runsMerged, doors.Count,
				runsTooWide, runsTooNarrow, runsTooShallow,
				doorsMeasuredByRegion, doorsMeasuredByPinch, doorsUnmeasured);
		}

		CNTerritoryDoor MakeDoor(List<CPos> run)
		{
			var sumX = 0;
			var sumY = 0;
			var outward = CVec.Zero;
			var outside = new List<CPos>();

			foreach (var cell in run)
			{
				sumX += cell.X;
				sumY += cell.Y;

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (territory.Contains(next) || !world.Map.Contains(next) || !IsPassable(next))
						continue;

					outward += dir;
					outside.Add(next);
				}
			}

			var mid = new CPos(sumX / run.Count, sumY / run.Count);

			// The centroid of a curved run can fall outside the run itself; the nearest actual cell is what
			// anything downstream wants to aim at.
			var center = run[0];
			var bestSq = int.MaxValue;
			foreach (var cell in run)
			{
				var d = (cell - mid).LengthSquared;
				if (d < bestSq)
				{
					bestSq = d;
					center = cell;
				}
			}

			// Measured from the ground just outside where there is any. That can come to nothing - every
			// candidate already inside the span or the claim, which is what produced "beyond 0" - and a
			// door standing inside our own ground is exactly that case. The regions know what such a door
			// separates without any flooding, so they are asked before falling back to sealing a guessed
			// axis by hand.
			var beyond = outside.Count > 0 ? GroundBeyondDoor(run, outside) : 0;
			if (beyond == 0)
			{
				beyond = GroundBeyondRegions(run);
				if (beyond > 0)
					doorsMeasuredByRegion++;
			}

			if (beyond == 0)
			{
				beyond = GroundBeyondPinch(center, run);
				if (beyond > 0)
					doorsMeasuredByPinch++;
				else
					doorsUnmeasured++;
			}

			return new CNTerritoryDoor(center, run.ToArray(), outward, beyond);
		}

		/// <summary>
		/// How much ground the door opens onto, measured with the door itself sealed so the flood cannot
		/// simply walk back in through it. Capped: past a point the answer is just "wide open".
		/// </summary>
		int GroundBeyondDoor(List<CPos> run, List<CPos> outside)
		{
			var cap = Math.Max(1, Info.DoorBeyondCellCap);
			var visited = new HashSet<CPos>(run);
			visited.UnionWith(territory);

			var queue = new Queue<CPos>();
			foreach (var cell in outside)
				if (visited.Add(cell))
					queue.Enqueue(cell);

			var count = 0;
			while (queue.Count > 0 && count < cap)
			{
				var cell = queue.Dequeue();
				count++;

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (!world.Map.Contains(next) || !IsPassable(next) || !visited.Add(next))
						continue;

					queue.Enqueue(next);
				}
			}

			return count;
		}

		/// <summary>
		/// Ground behind a door read off the shared region graph rather than flooded for. A door's cells
		/// are a region barrier by construction - <see cref="BuildRegions"/> cuts the map on exactly the
		/// resolved chokepoint corridors doors are made of - so the regions standing against its far side
		/// already know their own size, and nothing has to be walked or sealed by hand. Returns 0 when the
		/// graph cannot answer (the door separates nothing, or our own side of it cannot be identified),
		/// leaving the caller its old fallback.
		/// </summary>
		int GroundBeyondRegions(List<CPos> run)
		{
			if (regionIdByCell == null || regions.Count == 0)
				return 0;

			var nearId = NearestRegionId(territoryLastBaseRef ?? run[0]);
			if (nearId < 0)
				return 0;

			// Two cells out, not one: a ramp's barrier is dilated by a ring in BuildRegions, so the cells
			// immediately beside a ramp door belong to no region at all.
			var beyondIds = new HashSet<int>();
			foreach (var cell in run)
			{
				foreach (var neighbour in world.Map.FindTilesInCircle(cell, 2))
				{
					var id = GetRegionIdAt(neighbour);
					if (id >= 0 && id != nearId)
						beyondIds.Add(id);
				}
			}

			if (beyondIds.Count == 0)
				return 0;

			// Everything the far side leads on to in turn, with our own region held shut: a door onto a
			// small plateau that itself opens onto the rest of the map is a way in, not a pocket. Capped
			// like the floods are - past a point the answer is simply "wide open".
			var cap = Math.Max(1, Info.DoorBeyondCellCap);
			var visited = new HashSet<int>(beyondIds) { nearId };
			var queue = new Queue<int>(beyondIds);
			var count = 0;
			while (queue.Count > 0 && count < cap)
			{
				var region = regions[queue.Dequeue()];
				count += region.Size;

				foreach (var adjacent in region.AdjacentRegionIds)
					if (visited.Add(adjacent))
						queue.Enqueue(adjacent);
			}

			return Math.Min(count, cap);
		}

		/// <summary>
		/// The region at a cell, or the nearest one a couple of cells out. A base reference can sit on a
		/// gate or ramp cell, which belongs to no region at all.
		/// </summary>
		int NearestRegionId(CPos cell)
		{
			var id = GetRegionIdAt(cell);
			if (id >= 0)
				return id;

			foreach (var near in world.Map.FindTilesInCircle(cell, 3))
			{
				id = GetRegionIdAt(near);
				if (id >= 0)
					return id;
			}

			return -1;
		}

		/// <summary>
		/// Ground behind a gap that sits inside our own territory, where "behind" has to be worked out
		/// rather than read off the claim. The pinch is sealed across its narrow axis - the same barrier
		/// the chokepoint scan builds - and the flood starts on the side away from the base. Last resort,
		/// for a door the region graph cannot place: it guesses the axis from the base direction, so a
		/// wide feature can end up sealed the wrong way round.
		/// </summary>
		int GroundBeyondPinch(CPos center, List<CPos> run)
		{
			var reference = territoryLastBaseRef ?? center;
			var forward = center - reference;
			if (forward.LengthSquared == 0)
				return 0;

			// Same sideways reach the chokepoint scan uses, for the same reason: far enough to close a
			// narrow gap, short enough not to wall off open ground.
			const int PinchBarrierReach = 16;

			var perp = Math.Abs(forward.X) < Math.Abs(forward.Y) ? new CVec(1, 0) : new CVec(0, 1);
			var barrier = new HashSet<CPos>(run);
			foreach (var cell in run)
			{
				foreach (var sign in new[] { 1, -1 })
				{
					for (var k = 1; k <= PinchBarrierReach; k++)
					{
						var c = cell + perp * (sign * k);
						if (!world.Map.Contains(c) || !IsPassable(c))
							break;

						barrier.Add(c);
					}
				}
			}

			var seeds = new List<CPos>();
			if (forward.X != 0)
				seeds.Add(center + new CVec(Math.Sign(forward.X), 0));
			if (forward.Y != 0)
				seeds.Add(center + new CVec(0, Math.Sign(forward.Y)));

			var cap = Math.Max(1, Info.DoorBeyondCellCap);
			var visited = new HashSet<CPos>(barrier);
			var queue = new Queue<CPos>();
			foreach (var seed in seeds)
				if (world.Map.Contains(seed) && IsPassable(seed) && visited.Add(seed))
					queue.Enqueue(seed);

			var count = 0;
			while (queue.Count > 0 && count < cap)
			{
				var cell = queue.Dequeue();
				count++;

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (!world.Map.Contains(next) || !IsPassable(next) || !visited.Add(next))
						continue;

					queue.Enqueue(next);
				}
			}

			return count;
		}

		HashSet<CPos> OwnBuildingCells()
		{
			var cells = new HashSet<CPos>();
			foreach (var a in world.Actors)
				if (!a.IsDead && a.IsInWorld && a.Owner == player && a.Info.HasTraitInfo<BuildingInfo>())
					cells.Add(a.Location);

			return cells;
		}

		#endregion

		HashSet<CPos> EnemyBuildingCells()
		{
			return world.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy
					&& a.Info.HasTraitInfo<BuildingInfo>())
				.Select(a => a.Location)
				.ToHashSet();
		}

		bool SealingLeadsSomewhere(CPos reference, CNSealableCorridor corridor, HashSet<CPos> enemyBuildings)
		{
			var approach = corridor.WallRunsHorizontal ? new CVec(0, 1) : new CVec(1, 0);
			var baseSign = Math.Sign(corridor.WallRunsHorizontal
				? reference.Y - corridor.Center.Y
				: reference.X - corridor.Center.X);

			// Base exactly level with the corridor on the approach axis: there is no identifiable far
			// side, so which side the wall would seal is undefined. Reject instead of accepting — this
			// used to return true while GetChokepointDefenseAnchors skipped the very same corridor on
			// the same condition, producing corridors marked sealable that never got a turret behind them.
			if (baseSign == 0)
				return false;

			var farSeed = corridor.Center - approach * baseSign;
			var barrier = new HashSet<CPos>(corridor.Cells);
			if (!world.Map.Contains(farSeed) || !IsPassable(farSeed) || barrier.Contains(farSeed))
				return false;

			var hasEnemy = enemyBuildings.Count > 0;
			var limit = Math.Max(1, Info.MinimumChokepointBeyondCells);

			var visited = new HashSet<CPos>(barrier) { farSeed };
			var queue = new Queue<CPos>();
			queue.Enqueue(farSeed);
			var count = 0;
			while (queue.Count > 0)
			{
				var cell = queue.Dequeue();
				if (hasEnemy && enemyBuildings.Contains(cell))
					return true;

				if (++count >= limit)
					return true;

				foreach (var dir in CVec.Directions)
				{
					var next = cell + dir;
					if (!world.Map.Contains(next) || !IsPassable(next) || !visited.Add(next))
						continue;

					queue.Enqueue(next);
				}
			}

			return false;
		}

		// Walks passable cells from origin along step. Succeeds only at a *thick* shoulder (>=2 impassable cells or the
		// map edge), so a single isolated obstacle in open ground is not mistaken for a wall of a chokepoint.
		bool WalkToShoulder(CPos origin, CVec step, int maxWidth, List<CPos> into)
		{
			for (var i = 1; i <= maxWidth; i++)
			{
				var cell = origin + step * i;
				if (!IsPassable(cell))
				{
					var beyond = origin + step * (i + 1);
					return !world.Map.Contains(beyond) || !IsPassable(beyond);
				}

				into.Add(cell);
			}

			return false;
		}
	}
}
