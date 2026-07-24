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

		[Desc("Fallback minimum number of passable cells beyond a chokepoint before treating it as a real access.",
			"Used only when no enemy buildings exist; otherwise the far side must contain an enemy building.")]
		public readonly int MinimumChokepointBeyondCells = 96;

		[Desc("Hard cap on cells visited by the per-chokepoint 'leads somewhere' flood-fill, regardless of whether",
			"enemy buildings exist. Without this, a chokepoint whose reachable region contains no enemy building",
			"floods the ENTIRE region before giving up - unbounded on an open map. Treated as 'not confirmed' if hit.")]
		public readonly int FloodFillCellCap = 1500;

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
			TopologyReady = true;
			ResetPerBaseCaches();
			recheckTick = Math.Max(1, Info.TopologyRecheckInterval);
			recomputeTick = Math.Max(1, Info.RecomputeInterval);
			chokepoints.Clear();
			bridgeWatchCells.Clear();
			abstractDomains = null;
			nodeCells = [];
			passability = null;
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
				return;
			}

			if (!BuildOwnTopology())
				return;

			shared = Publish();
			registry[key] = shared;
			sharedGeneration = shared.Generation;
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
			};
			sh.Chokepoints.AddRange(chokepoints);
			sh.BridgeWatchCells.AddRange(bridgeWatchCells);
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

			// Cache terrain passability once so the scans/floods below don't call Locomotor.MovementCostForCell repeatedly.
			passability = new CellLayer<bool>(world.Map);
			foreach (var c in world.Map.AllCells)
				passability[c] = locomotor.MovementCostForCell(c) != short.MaxValue;

			// Terrain-only connectivity (BlockedByActor.None): topology should reflect permanent terrain (cliffs,
			// water, bridges, ramps), NOT trees/buildings/units, which would create fake chokepoints around obstacles.
			var (graph, domains) = pathFinder.GetOverlayDataForLocomotor(locomotor, BlockedByActor.None);
			if (graph == null || domains == null || graph.Count == 0)
				return false;

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
			foreach (var center in ScanPassageCenters())
				Add(center, CNChokepointType.Passage);

			foreach (var endpoint in ScanRampEndpoints())
				Add(endpoint, CNChokepointType.Ramp);

			foreach (var center in ScanBridgeCenters())
				Add(center, CNChokepointType.Bridge);

			chokepoints.AddRange(byCell.Values);
			lastBridgeSignature = CurrentBridgeSignature();
			return true;
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

		// Finds bridges directly (terrain type "Bridge" plus bridge actor footprints) and yields one cell per bridge.
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
			if (!IsHeightTransition(cell))
				return false;

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

				var enemyCount = world.Actors.Count(a => !a.IsDead && a.IsInWorld
					&& a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy
					&& a.Info.HasTraitInfo<BuildingInfo>());

				// Skip if neither our base nor the enemy building count changed since the last completed scan -
				// a cheap enough pre-check that it's worth doing before the full enemy-position set below.
				if (!BaseMoved(reference.Value, usefulLastBaseRef) && !EnemyBuildingCountChanged(enemyCount, usefulLastEnemyCount))
				{
					usefulNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					return;
				}

				usefulLastBaseRef = reference.Value;
				usefulLastEnemyCount = enemyCount;
				usefulReference = reference.Value;
				usefulDomain = DomainOf(usefulReference);
				usefulEnemyBuildings = world.Actors
					.Where(a => !a.IsDead && a.IsInWorld
						&& a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy
						&& a.Info.HasTraitInfo<BuildingInfo>())
					.Select(a => a.Location)
					.ToHashSet();
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

				var enemyCount = world.Actors.Count(a => !a.IsDead && a.IsInWorld
					&& a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy
					&& a.Info.HasTraitInfo<BuildingInfo>());

				if (!BaseMoved(reference.Value, corridorLastBaseRef) && !EnemyBuildingCountChanged(enemyCount, corridorLastEnemyCount))
				{
					corridorNextRefreshTick = world.WorldTick + Math.Max(1, Info.UsefulRefreshInterval);
					return;
				}

				corridorLastBaseRef = reference.Value;
				corridorLastEnemyCount = enemyCount;
				corridorBaseRef = reference.Value;
				corridorEnemy = EnemyBuildingCells();
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
		CNSealableCorridor FindNarrowestCrossing(CPos near, int snapRadius, int maxWidth)
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
			if (baseSign == 0)
				return true;

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
