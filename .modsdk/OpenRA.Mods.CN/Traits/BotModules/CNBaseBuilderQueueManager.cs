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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	sealed class CNBaseBuilderQueueManager
	{
		// How many bases one build order may try before giving up for this pass.
		const int MaxBasePlacementAttempts = 3;

		// Deficit used for a building that fills a base's capability floor: above any real fraction deficit,
		// so filling the floor wins over topping up a category that is merely below its share.
		const int CapabilityFloorDeficit = int.MaxValue / 2;

		public readonly string Category;
		public int WaitTicks;

		readonly CNBaseBuilderBotModule baseBuilder;
		readonly World world;
		readonly Player player;
		readonly PowerManager playerPower;
		readonly PlayerResources playerResources;
		readonly IResourceLayer resourceLayer;

		Actor[] playerBuildings;
		int failCount;
		int failRetryTicks;
		string lastFailedBuilding;
		int checkForBasesTicks;
		int cachedBases;
		int cachedBuildings;
		int minimumExcessPower;
		int defensePlacementAttempt;
		int refineryPlacementCooldownTicks;
		int defensePlacementCooldownTicks;
		CPos? baseCenterKeepsFailing = null;

		bool itemQueuedThisTick = false;

		WaterCheck waterState = WaterCheck.NotChecked;

		readonly struct RefineryCandidate
		{
			public readonly (CPos? Location, CPos Center, int Variant) Placement;
			public readonly int Score;

			public RefineryCandidate((CPos? Location, CPos Center, int Variant) placement, int score)
			{
				Placement = placement;
				Score = score;
			}
		}

		public CNBaseBuilderQueueManager(CNBaseBuilderBotModule baseBuilder, string category, Player p, PowerManager pm,
			PlayerResources pr, IResourceLayer rl)
		{
			this.baseBuilder = baseBuilder;
			world = p.World;
			player = p;
			playerPower = pm;
			playerResources = pr;
			resourceLayer = rl;
			Category = category;
			minimumExcessPower = baseBuilder.GetActiveMinimumExcessPower();
			if (baseBuilder.Info.NavalProductionTypes.Count == 0)
				waterState = WaterCheck.DontCheck;
		}

		public void Tick(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			if (refineryPlacementCooldownTicks > 0)
				refineryPlacementCooldownTicks--;
			if (defensePlacementCooldownTicks > 0)
				defensePlacementCooldownTicks--;

			// If we can't place any structures, give a nudge to BaseExpansionModules and hope it gets fixed.
			if (failCount >= baseBuilder.Info.MaximumFailedPlacementAttempts)
			{
				var hasBaseExpansionModules = baseBuilder.BaseExpansionModules != null && baseBuilder.BaseExpansionModules.Length > 0;
				if (hasBaseExpansionModules)
				{
					if (baseCenterKeepsFailing != null && !baseBuilder.Info.DefenseTypes.Contains(lastFailedBuilding))
					{
						var maxRadius = baseBuilder.GetEffectiveMaxBaseRadius();
						var stuckConyard = baseBuilder.ConstructionYardBuildings.Actors
							.Where(a => (a.Location - baseCenterKeepsFailing.Value).LengthSquared <= maxRadius * maxRadius)
							.MinByOrDefault(a => (a.Location - baseCenterKeepsFailing.Value).LengthSquared);

						if (stuckConyard != null)
						{
							foreach (var be in baseBuilder.BaseExpansionModules)
								be.UpdateExpansionParams(bot, false, true, stuckConyard);
						}
					}

					failCount = 0;
					baseCenterKeepsFailing = null;
				}

				// No BaseExpansionModules exist. Only bother resetting failCount when either
				// a) the number of buildings has decreased since last failure M ticks ago,
				// or b) number of BaseProviders (construction yard or similar) has increased since then.
				// Otherwise reset failRetryTicks instead to wait again.
				else if (--failRetryTicks <= 0)
				{
					var currentBuildings = baseBuilder.GetCachedPlayerBuildings().Count;
					var baseProviders = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);

					// The retry delay is armed in both cases. Only the failure branch used to reset it,
					// so after a successful recovery failRetryTicks stayed at or below zero and the next
					// time the queue got stuck the check fired on every single tick instead of waiting.
					failRetryTicks = baseBuilder.Info.StructureProductionResumeDelay;
					if (currentBuildings < cachedBuildings || baseProviders > cachedBases)
						failCount = 0;
				}

				if (failCount >= baseBuilder.Info.MaximumFailedPlacementAttempts)
					return;
			}

			if (waterState == WaterCheck.NotChecked)
			{
				if (AIUtils.IsAreaAvailable<BaseProvider>(world, player, world.Map, baseBuilder.GetEffectiveMaxBaseRadius(), baseBuilder.Info.WaterTerrainTypes))
					waterState = WaterCheck.EnoughWater;
				else
				{
					waterState = WaterCheck.NotEnoughWater;
					checkForBasesTicks = baseBuilder.Info.CheckForNewBasesDelay;
				}
			}

			if (waterState == WaterCheck.NotEnoughWater && --checkForBasesTicks <= 0)
			{
				var currentBases = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);

				if (currentBases > cachedBases)
				{
					cachedBases = currentBases;
					waterState = WaterCheck.NotChecked;
				}
			}

			// Only update once per second or so
			if (WaitTicks > 0)
				return;

			playerBuildings = baseBuilder.GetCachedPlayerBuildings().ToArray();
			var excessPowerBonus =
				baseBuilder.Info.ExcessPowerIncrement *
				(playerBuildings.Length / baseBuilder.Info.ExcessPowerIncreaseThreshold.Clamp(1, int.MaxValue));
			var profileMin = baseBuilder.GetActiveMinimumExcessPower();
			var profileMax = baseBuilder.GetActiveMaximumExcessPower();
			minimumExcessPower = (profileMin + excessPowerBonus).Clamp(profileMin, profileMax);

			// PERF: Queue only one actor at a time per category
			itemQueuedThisTick = false;
			var active = false;
			foreach (var queue in queuesByCategory[Category])
			{
				if (TickQueue(bot, queue))
					active = true;
			}

			// Add a random factor so not every AI produces at the same tick early in the game.
			// Minimum should not be negative as delays in HackyAI could be zero.
			var randomFactor = world.LocalRandom.Next(0, baseBuilder.Info.StructureProductionRandomBonusDelay);

			WaitTicks = active ? baseBuilder.Info.StructureProductionActiveDelay + randomFactor
				: baseBuilder.Info.StructureProductionInactiveDelay + randomFactor;
		}

		int CountPassableOrthogonalNeighbors(CPos cell)
		{
			if (baseBuilder.HarvesterLocomotorsList.Length <= 0)
				return 4;

			var count = 0;
			foreach (var dir in new[] { new CVec(0, -1), new CVec(0, 1), new CVec(-1, 0), new CVec(1, 0) })
			{
				var next = cell + dir;
				if (baseBuilder.HarvesterLocomotorsList.All(l =>
					l.MovementCostForCell(next) != PathGraph.MovementCostForUnreachableCell))
					count++;
			}

			return count;
		}

		int CountApproachNeighbors(CPos from, CPos to)
		{
			if (baseBuilder.HarvesterLocomotorsList.Length <= 0)
				return 1;

			var bestDist = (from - to).LengthSquared;
			var count = 0;
			foreach (var dir in new[] { new CVec(0, -1), new CVec(0, 1), new CVec(-1, 0), new CVec(1, 0) })
			{
				var next = from + dir;
				if ((next - to).LengthSquared >= bestDist)
					continue;

				if (baseBuilder.HarvesterLocomotorsList.All(l =>
					l.MovementCostForCell(next) != PathGraph.MovementCostForUnreachableCell))
					count++;
			}

			return count;
		}

		bool IsPassableForHarvesters(CPos cell)
		{
			if (baseBuilder.HarvesterLocomotorsList.Length <= 0)
				return true;

			return baseBuilder.HarvesterLocomotorsList.All(l =>
				l.MovementCostForCell(cell) != PathGraph.MovementCostForUnreachableCell);
		}

		static List<CPos> GetRefineryDockCells(ActorInfo actorInfo, CPos refineryLoc, CVec dimensions)
		{
			var result = new List<CPos>();
			var dockInfo = actorInfo.TraitInfoOrDefault<DockHostInfo>();
			if (dockInfo == null)
				return result;

			var x = dockInfo.DockOffset.X > 0 ? refineryLoc.X + dimensions.X :
				dockInfo.DockOffset.X < 0 ? refineryLoc.X - 1 :
				refineryLoc.X + dimensions.X / 2;
			var y = dockInfo.DockOffset.Y > 0 ? refineryLoc.Y + dimensions.Y :
				dockInfo.DockOffset.Y < 0 ? refineryLoc.Y - 1 :
				refineryLoc.Y + dimensions.Y / 2;

			result.Add(new CPos(x, y));

			if (dockInfo.DockOffset.X != 0)
			{
				for (var yy = refineryLoc.Y; yy < refineryLoc.Y + dimensions.Y; yy++)
					result.Add(new CPos(x, yy));
			}

			if (dockInfo.DockOffset.Y != 0)
			{
				for (var xx = refineryLoc.X; xx < refineryLoc.X + dimensions.X; xx++)
					result.Add(new CPos(xx, y));
			}

			return result.Distinct().ToList();
		}

		bool HasOpenRefineryApproach(ActorInfo actorInfo, CPos refineryLoc, CVec dimensions, CPos resourceLoc)
		{
			if (baseBuilder.HarvesterLocomotorsList.Length <= 0)
				return true;

			var dockCells = GetRefineryDockCells(actorInfo, refineryLoc, dimensions);
			if (dockCells.Count == 0)
				return true;

			var passableDockCells = 0;
			var goodApproachCells = 0;
			foreach (var edge in dockCells)
			{
				if (!IsPassableForHarvesters(edge))
					continue;

				passableDockCells++;
				if (CountApproachNeighbors(edge, resourceLoc) > 0)
					goodApproachCells++;
			}

			// Require a majority of dock cells to be passable (cliff blocking one full side fails this),
			// and at least two of them must have a clear step toward the resource field.
			return passableDockCells * 2 >= dockCells.Count && goodApproachCells >= 2;
		}

		readonly Dictionary<(CPos Base, CPos Resource), int> fieldPathLengthCache = [];
		int fieldPathCacheTick = -1;

		/// <summary>
		/// How far a harvester actually has to drive from the base to reach this field, in cells.
		/// <para>
		/// This is the term the placement scorer was written around and then had to give up on: the
		/// A* was run per candidate cell, which meant hundreds of full searches per decision. Measured
		/// once per field and cached it costs one, because the road length depends on where the field
		/// lies relative to the base — not on which cell inside the base the refinery lands on.
		/// </para>
		/// Without it, placement sees only straight-line distance, and a field on the far side of a
		/// cliff looks adjacent while the drive to it goes the long way round the map. The height
		/// penalty in ScoreRefineryTopologyFit is 350 per level squared, which the distance terms -
		/// thousands, at any real separation - comfortably outvote.
		/// </summary>
		int? HarvesterPathLengthToField(CPos baseCenter, CPos resource)
		{
			if (baseBuilder.Info.RefineryDetourPenalty <= 0)
				return null;

			if (world.WorldTick != fieldPathCacheTick)
			{
				fieldPathLengthCache.Clear();
				fieldPathCacheTick = world.WorldTick;
			}

			if (fieldPathLengthCache.TryGetValue((baseCenter, resource), out var cached))
				return cached < 0 ? null : cached;

			var length = -1;

			// A harvester is the right thing to ask, but the opening refinery is placed before the bot
			// owns one - and that placement matters most of all, since a first refinery on the wrong
			// side of a cliff handicaps the whole game. Any owned ground unit is a good enough stand-in:
			// cliffs stop them all alike, and it is the cliff that creates the detour being measured.
			Actor harvester = null;
			Actor anyGround = null;
			foreach (var actor in world.ActorsHavingTrait<Mobile>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				if (baseBuilder.Info.HarvesterTypes.Contains(actor.Info.Name))
				{
					harvester = actor;
					break;
				}

				anyGround ??= actor;
			}

			harvester ??= anyGround;

			if (harvester != null)
			{
				var path = baseBuilder.PathFinder.FindPathToTargetCells(
					harvester, baseCenter, [resource], BlockedByActor.None);

				if (path != null && path.Count > 0)
					length = path.Count;
			}
			else if (world.Map.Contains(resource) && world.Map.Contains(baseCenter))
			{
				// Nothing mobile at all - the moment right after the MCV deploys, when the very first
				// refinery is sited. Terrain stands in for the measurement that cannot be made yet: a
				// field on another terrace is only reachable by a ramp, and a ramp is a detour, so the
				// straight-line distance is inflated per level of height difference rather than the
				// check being skipped on the one placement that matters most.
				// CVec.Length is already in cells - the operands are cell coordinates, not world
				// positions - so there is no 1024 to divide out here.
				var straight = (resource - baseCenter).Length;
				var heightDelta = Math.Abs(world.Map.Height[resource] - world.Map.Height[baseCenter]);
				if (straight > 0 && heightDelta > 0)
					length = straight * (100 + heightDelta * Math.Max(0, baseBuilder.Info.RefineryUnmeasuredHeightDetourPercent)) / 100;
			}

			if (length > 0)
				CNBotLog.Debug("{0} field {1}: {2} cells to drive, {3} straight{4}",
					player, resource, length, (resource - baseCenter).Length,
					harvester == null ? " (estimated from height, no unit to path with)" : "");

			fieldPathLengthCache[(baseCenter, resource)] = length;
			return length < 0 ? null : length;
		}

		int ScoreRefineryTopologyFit(CPos refineryLoc, List<CPos> dockCells, IReadOnlyList<CPos> sampledResourceCells)
		{
			if (sampledResourceCells == null || sampledResourceCells.Count == 0 || !world.Map.Contains(refineryLoc))
				return 0;

			var resourceHeight = sampledResourceCells
				.Where(world.Map.Contains)
				.GroupBy(c => world.Map.Height[c])
				.OrderByDescending(g => g.Count())
				.Select(g => g.Key)
				.FirstOrDefault();

			var usableDocks = dockCells
				.Where(c => world.Map.Contains(c) && IsPassableForHarvesters(c))
				.ToList();
			if (usableDocks.Count == 0)
				usableDocks.Add(refineryLoc);

			var bestDockHeightDelta = usableDocks.Min(d => Math.Abs(world.Map.Height[d] - resourceHeight));
			var score = bestDockHeightDelta * bestDockHeightDelta * 350;

			foreach (var sample in sampledResourceCells.Take(4))
			{
				if (!world.Map.Contains(sample))
					continue;

				var bestContinuity = int.MaxValue;
				foreach (var dock in usableDocks)
					bestContinuity = Math.Min(bestContinuity, ScoreDirectDockApproach(dock, sample));

				if (bestContinuity != int.MaxValue)
					score += bestContinuity;
			}

			return score;
		}

		int ScoreDirectDockApproach(CPos dock, CPos resource)
		{
			var score = 0;
			var current = dock;
			var currentHeight = world.Map.Height[current];

			for (var i = 0; i < 6 && current != resource; i++)
			{
				var delta = resource - current;
				var step = Math.Abs(delta.X) >= Math.Abs(delta.Y)
					? new CVec(Math.Sign(delta.X), 0)
					: new CVec(0, Math.Sign(delta.Y));

				if (step == CVec.Zero)
					break;

				var next = current + step;
				if (!world.Map.Contains(next) || !IsPassableForHarvesters(next))
					return score + 300;

				var nextHeight = world.Map.Height[next];
				var heightDelta = Math.Abs(nextHeight - currentHeight);
				if (heightDelta > 0)
					score += heightDelta * heightDelta * 90;

				current = next;
				currentHeight = nextHeight;
			}

			return score;
		}

		static CPos[] GetFieldSampleCells(IEnumerable<CPos> nearbyResources, CPos resourceLoc, int maxSamples = 6)
		{
			var fieldCells = nearbyResources
				.OrderBy(c => (c - resourceLoc).LengthSquared)
				.Take(Math.Max(maxSamples, 4))
				.ToArray();

			if (fieldCells.Length == 0)
				return [resourceLoc];

			return fieldCells;
		}

		CPos[] SelectRefineryResourceCells(IReadOnlyList<CPos> resources, CPos baseLoc, CPos? existingRefineryLoc = null, CPos? requestedResourceLoc = null)
		{
			var maxChecks = Math.Max(baseBuilder.Info.MaxResourceCellsToCheck, 1);

			if (requestedResourceLoc != null)
				return resources
					.OrderBy(c => (c - requestedResourceLoc.Value).LengthSquared)
					.Take(maxChecks)
					.ToArray();

			var localCells = resources
				.OrderByDescending(c => CountPassableOrthogonalNeighbors(c) * 10 - (c - baseLoc).LengthSquared / 8)
				.Take(maxChecks);

			if (existingRefineryLoc == null)
				return localCells.ToArray();

			// Additional refineries should spread out, but not at the cost of ignoring
			// nearby open field cells that can support a second refinery in the same base.
			var spreadCells = resources
				.OrderByDescending(c => (c - existingRefineryLoc.Value).LengthSquared)
				.Take(maxChecks);

			return localCells
				.Concat(spreadCells)
				.Distinct()
				.Take(Math.Max(maxChecks, 16))
				.ToArray();
		}

		IEnumerable<CPos> GetRefineryCandidateCellsForField(
			ActorInfo actorInfo,
			BuildingInfo bi,
			IReadOnlyList<CPos> fieldCells)
		{
			if (fieldCells == null || fieldCells.Count == 0)
				yield break;

			var minX = fieldCells.Min(c => c.X);
			var maxX = fieldCells.Max(c => c.X);
			var minY = fieldCells.Min(c => c.Y);
			var maxY = fieldCells.Max(c => c.Y);
			var midX = (minX + maxX) / 2;
			var midY = (minY + maxY) / 2;

			// Sort anchors by field proximity only — NOT by base proximity.
			// The old base-proximity tie-breaker caused the cliff-side anchor (closer to base on
			// maps with surrounding cliffs) to always dominate, filling the Take() budget before
			// the open field-sides were ever tried.
			// IsCloseEnoughToBase already guarantees all yielded cells are reachable from the base.
			var sideAnchors = new[]
			{
				new CPos(minX - bi.Dimensions.X - 1, midY),
				new CPos(maxX + 1, midY),
				new CPos(midX, minY - bi.Dimensions.Y - 1),
				new CPos(midX, maxY + 1)
			}
			.OrderBy(a => fieldCells.Min(c => (a - c).LengthSquared))
			.ToArray();

			// Materialise each anchor's candidate list so we can round-robin across them.
			// Round-robin ensures all four field sides contribute candidates rather than one
			// side exhausting the Take() budget.
			var seen = new HashSet<CPos>();
			var perAnchor = sideAnchors
				.Select(anchor => world.Map.FindTilesInAnnulus(anchor, 0, 4)
					.OrderBy(c => (c - anchor).LengthSquared)
					.ThenBy(c => fieldCells.Min(f => (c - f).LengthSquared))
					.Take(8)
					.ToList())
				.ToArray();

			var indices = new int[perAnchor.Length];
			bool any;
			do
			{
				any = false;
				for (var i = 0; i < perAnchor.Length; i++)
				{
					while (indices[i] < perAnchor[i].Count)
					{
						var cell = perAnchor[i][indices[i]++];
						if (!seen.Add(cell))
							continue;
						if (!world.CanPlaceBuilding(cell, actorInfo, bi, null))
							continue;
						if (!bi.IsCloseEnoughToBase(world, player, actorInfo, cell))
							continue;
						any = true;
						yield return cell;
						break;
					}
				}
			}
			while (any);
		}

		int ScoreRefineryCandidate(
			ActorInfo actorInfo,
			CPos baseLoc,
			CPos resourceLoc,
			CPos refineryLoc,
			List<CPos> existingRefineries,
			IReadOnlyList<CPos> sampledResourceCells,
			int? harvesterPathLength)
		{
			var score = 0;
			var dockCells = GetRefineryDockCells(actorInfo, refineryLoc, actorInfo.TraitInfoOrDefault<BuildingInfo>()?.Dimensions ?? CVec.Zero);

			// How far the harvesters have to drive to reach this field at all. A property of the field,
			// so it is identical for every candidate cell and ranks fields against each other - it says
			// nothing about where within the base the refinery should stand. Squared, in the same
			// currency as the dock distance below, so a road that doubles back costs quadratically more
			// than its straight-line distance suggests.
			if (harvesterPathLength != null)
				score += harvesterPathLength.Value * harvesterPathLength.Value * baseBuilder.Info.RefineryDetourPenalty;

			// Added to that, never instead of it. Making the two exclusive left every candidate for a
			// given field scoring identically here, so the only thing still separating them was the pull
			// toward the base anchor below - which is how refineries ended up parked in open ground in
			// the middle of the base rather than beside the tiberium they serve.
			if (sampledResourceCells != null && sampledResourceCells.Count > 0)
			{
				var totalDistance = 0;
				foreach (var sample in sampledResourceCells)
				{
					var bestDockDistance = dockCells.Count > 0
						? dockCells.Min(d => (d - sample).LengthSquared)
						: (refineryLoc - sample).LengthSquared;
					totalDistance += bestDockDistance;
				}

				score += totalDistance * 10 / sampledResourceCells.Count;
			}
			else
				score += (refineryLoc - resourceLoc).LengthSquared * 10;

			score += ScoreRefineryTopologyFit(refineryLoc, dockCells, sampledResourceCells);

			// Secondary: prefer placements that don't drift too far from the local base anchor.
			score += (refineryLoc - baseLoc).LengthSquared * 2;

			// Prefer spreading away from existing refineries when options are otherwise similar.
			if (existingRefineries.Count > 0)
			{
				var nearestExisting = int.MaxValue;
				foreach (var existing in existingRefineries)
				{
					var dist = (refineryLoc - existing).LengthSquared;
					if (dist < nearestExisting)
						nearestExisting = dist;
				}

				score -= Math.Min(nearestExisting, 64);
			}

			// Prefer candidates that can approach the field directly instead of first driving away
			// and looping around cliffs or blocked choke points.
			var refineryOpenNeighbors = CountPassableOrthogonalNeighbors(refineryLoc);
			var resourceOpenNeighbors = CountPassableOrthogonalNeighbors(resourceLoc);
			var refineryApproachOptions = CountApproachNeighbors(refineryLoc, resourceLoc);
			var resourceApproachOptions = CountApproachNeighbors(resourceLoc, refineryLoc);

			score += (4 - refineryOpenNeighbors) * 20;
			score += (4 - resourceOpenNeighbors) * 10;

			if (refineryApproachOptions == 0)
				score += 400;
			else
				score += (2 - Math.Min(2, refineryApproachOptions)) * 60;

			if (resourceApproachOptions == 0)
				score += 250;
			else
				score += (2 - Math.Min(2, resourceApproachOptions)) * 40;

			if (sampledResourceCells != null)
			{
				foreach (var sample in sampledResourceCells)
				{
					var sampleApproach = CountApproachNeighbors(refineryLoc, sample);
					if (sampleApproach == 0)
						score += 120;
				}
			}

			return score;
		}

		bool TickQueue(IBot bot, ProductionQueue queue)
		{
			var currentBuilding = queue.AllQueued().FirstOrDefault();

			// Waiting to build something
			if (currentBuilding == null && failCount < baseBuilder.Info.MaximumFailedPlacementAttempts)
			{
				// PERF: We shouldn't be queueing new units when we're low on cash
				if (playerResources.GetCashAndResources() < baseBuilder.Info.ProductionMinCashRequirement || itemQueuedThisTick)
					return false;

				var item = ChooseBuildingToBuild(queue);
				if (item == null)
					return false;

				bot.QueueOrder(Order.StartProduction(queue.Actor, item.Name, 1));
				itemQueuedThisTick = true;
			}
			else if (currentBuilding != null && currentBuilding.Done)
			{
				baseCenterKeepsFailing = null;

				// Production is complete
				// Choose the placement logic
				// HACK: HACK HACK HACK
				// TODO: Derive this from BuildingCommonNames instead
				var type = BuildingType.Building;
				CPos? location = null;
				var actorVariant = 0;
				var orderString = "PlaceBuilding";

				// Check if Building is a plug for other Building
				var actorInfo = world.Map.Rules.Actors[currentBuilding.Item];
				var plugInfo = actorInfo.TraitInfoOrDefault<PlugInfo>();

				if (plugInfo != null)
				{
					var possibleBuilding = world.ActorsWithTrait<Pluggable>().FirstOrDefault(a =>
						a.Actor.Owner == player && a.Trait.AcceptsPlug(plugInfo.Type));

					if (possibleBuilding.Actor != null)
					{
						orderString = "PlacePlug";
						location = possibleBuilding.Actor.Location + possibleBuilding.Trait.Info.Offset;
					}
				}
				else if (actorInfo.HasTraitInfo<LineBuildInfo>() ||
					baseBuilder.Info.WallTypes.Contains(actorInfo.Name))
				{
					// Walls use LineBuild order and ProtectedByWalls/perimeter placement.
					orderString = "LineBuild";
					(location, baseCenterKeepsFailing, actorVariant) = ChooseWallLocation(actorInfo);
				}
				else if (baseBuilder.Info.GateTypes.Contains(actorInfo.Name))
				{
					// Gates replace a 3-cell wall segment and must use normal building placement.
					(location, baseCenterKeepsFailing, actorVariant) = ChooseGateLocation(actorInfo);
				}
				else
				{
					// Check if Building is a defense and if we should place it towards the enemy or not.
					if (baseBuilder.Info.DefenseTypes.Contains(actorInfo.Name) && world.LocalRandom.Next(100) < baseBuilder.Info.PlaceDefenseTowardsEnemyChance)
						type = BuildingType.Defense;
					else if (baseBuilder.Info.RefineryTypes.Contains(actorInfo.Name))
						type = BuildingType.Refinery;

					(location, baseCenterKeepsFailing, actorVariant) = ChooseBuildLocation(currentBuilding.Item, true, type);
				}

				if (location == null)
				{
					if (type == BuildingType.Defense)
					{
						// Defense placement uses a bounded sampled search. A miss can simply mean this
						// attempt's candidate set held no valid cell, not that the base is truly stuck,
						// so retry with a progressively wider search (see placementRelaxation above).
						defensePlacementAttempt++;
						if (defensePlacementAttempt < baseBuilder.Info.MaximumFailedPlacementAttempts)
							return true;

						// Out of attempts: cancel and cool down, exactly as an unplaceable refinery does,
						// instead of falling through to the shared failure path. That path raises failCount
						// by only one per exhausted run, so reaching its own cancel threshold took
						// MaximumFailedPlacementAttempts squared — 36 attempts at the configured 6, around
						// half a minute of a finished defense waiting on a placement that was never going
						// to succeed. It also let defenses trip the builder-wide production stall, which is
						// meant for a genuinely walled-in base, not for one crowded corner.
						defensePlacementAttempt = 0;
						bot.QueueOrder(Order.CancelProduction(queue.Actor, currentBuilding.Item, 1));
						defensePlacementCooldownTicks = baseBuilder.Info.DefensePlacementRetryDelay;
						return false;
					}
					else if (type == BuildingType.Refinery && AIUtils.CountActorByCommonName(baseBuilder.RefineryBuildings) > 0)
					{
						// A refinery with no accessible tiberium spot should not jam the entire build
						// queue — the bot should still build power, barracks, defenses, etc.
						// Cancel it and start a cooldown before the economy check tries again.
						CNBotLog.Debug("{0} refinery: placement found no cell, cancelling (have {1}, target {2})",
							player, AIUtils.CountActorByCommonName(baseBuilder.RefineryBuildings),
							baseBuilder.GetTargetRefineryCount());

						bot.QueueOrder(Order.CancelProduction(queue.Actor, currentBuilding.Item, 1));
						refineryPlacementCooldownTicks = baseBuilder.Info.RefineryPlacementRetryDelay;
						return false;
					}
				}

				if (location == null)
				{
					// If we just reached the maximum fail count, cache the number of current structures
					if (++failCount >= baseBuilder.Info.MaximumFailedPlacementAttempts)
					{
						CNBotLog.Debug($"{player} has nowhere to place {currentBuilding.Item}");
						bot.QueueOrder(Order.CancelProduction(queue.Actor, currentBuilding.Item, 1));
						lastFailedBuilding = currentBuilding.Item;
						if (type == BuildingType.Defense)
							defensePlacementCooldownTicks = 120;

						// Length == 0, not == null: BaseExpansionModules is assigned in Created() from
						// TraitsImplementing<IBotBaseExpansion>().ToArray() and is therefore never null —
						// at worst an empty array. The old null check made this branch dead code, so
						// cachedBuildings stayed at 0 forever while Tick's recovery path compares
						// `currentBuildings < cachedBuildings` against it. That condition can never hold
						// for a bot without an expansion module, leaving its build queue wedged until a
						// new BaseProvider happens to appear.
						if (baseBuilder.BaseExpansionModules == null || baseBuilder.BaseExpansionModules.Length == 0)
						{
							cachedBuildings = baseBuilder.GetCachedPlayerBuildings().Count;
							cachedBases = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);
						}
					}
				}
				else
				{
					failCount = 0;
					baseCenterKeepsFailing = null;
					if (type == BuildingType.Defense)
						defensePlacementAttempt = 0;

					bot.QueueOrder(new Order(orderString, player.PlayerActor, Target.FromCell(world, location.Value), false)
					{
						// Building to place
						TargetString = currentBuilding.Item,

						// Actor variant will always be small enough to safely pack in a CPos
						ExtraLocation = new CPos(actorVariant, 0),

						// Actor ID to associate the placement with
						ExtraData = queue.Actor.ActorID,
						SuppressVisualFeedback = true
					});

					// After succesfuly placing a building, nudge BaseExpansionModules to expand.
					// We want to avoid expanding too often, so we make a judgement by counting buildings.
					if (baseBuilder.Info.ProductionTypes.Contains(currentBuilding.Item)
						|| baseBuilder.Info.TechTypes.Contains(currentBuilding.Item) || baseBuilder.Info.RefineryTypes.Contains(currentBuilding.Item))
					{
						var numRef = baseBuilder.RefineryBuildings.Actors.Count(a => !a.IsDead) + (baseBuilder.Info.RefineryTypes.Contains(currentBuilding.Item) ? 1 : 0);

						var numProd = baseBuilder.ProductionBuildings.Actors.Count(a => !a.IsDead) + (baseBuilder.Info.ProductionTypes.Contains(currentBuilding.Item) ? 1 : 0);

						var numTech = playerBuildings.Count(a => baseBuilder.Info.TechTypes.Contains(a.Info.Name))
							+ (baseBuilder.Info.TechTypes.Contains(currentBuilding.Item) ? 1 : 0);

						var tolerateOnCash = baseBuilder.GetExpansionCashThrottle();

						if (numRef >= baseBuilder.Info.InititalMinimumRefineryCount + baseBuilder.Info.AdditionalMinimumRefineryCount
							&& numProd > 0 && numProd + numTech - baseBuilder.Info.ExpansionTolerate.Random(world.LocalRandom) - tolerateOnCash >= numRef)
						{
							var undeployEvenNoBase = numProd + numTech - baseBuilder.Info.ForceExpansionTolerate.Random(world.LocalRandom) - tolerateOnCash >= numRef;

							foreach (var be in baseBuilder.BaseExpansionModules)
								be.UpdateExpansionParams(bot, true, undeployEvenNoBase, null);
						}
					}

					return true;
				}
			}

			return true;
		}

		int CountExistingAndQueuedBuilding(string name)
		{
			var actorInfo = world.Map.Rules.Actors[name];
			var buildingVariantInfo = actorInfo.TraitInfoOrDefault<PlaceBuildingVariantsInfo>();
			var variants = buildingVariantInfo?.Actors ?? [];

			var count = playerBuildings.Count(a => a.Info.Name == name || variants.Contains(a.Info.Name));

			if (baseBuilder.BuildingsBeingProduced.TryGetValue(name, out var queued))
				count += queued;

			foreach (var variant in variants)
				if (baseBuilder.BuildingsBeingProduced.TryGetValue(variant, out var queuedVariant))
					count += queuedVariant;

			return count;
		}

		int CountExistingAndQueuedGates()
		{
			var count = playerBuildings.Count(a => baseBuilder.Info.GateTypes.Contains(a.Info.Name));
			foreach (var gate in baseBuilder.Info.GateTypes)
				if (baseBuilder.BuildingsBeingProduced.TryGetValue(gate, out var queued))
					count += queued;

			return count;
		}

		ActorInfo GetProducibleBuilding(FrozenSet<string> actors, IEnumerable<ActorInfo> buildables, Func<ActorInfo, int> orderBy = null)
		{
			var available = buildables.Where(actor =>
			{
				// Are we able to build this?
				if (!actors.Contains(actor.Name))
					return false;

				if (!baseBuilder.Info.BuildingLimits.TryGetValue(actor.Name, out var limit))
					return true;

				return CountExistingAndQueuedBuilding(actor.Name) < baseBuilder.GetScaledBuildingLimit(limit);
			});

			if (orderBy != null)
				return available.MaxByOrDefault(orderBy);

			return available.RandomOrDefault(world.LocalRandom);
		}

		// Returns the power surplus the bot would have if all currently powered-down buildings
		// were re-enabled. PowerDownBotModule suspends power draw by zeroing IPowerModifier,
		// so playerPower.ExcessPower is artificially inflated while buildings are offline.
		// Using this value for power-build decisions ensures the bot targets enough capacity
		// to run everything simultaneously, breaking the power-down / no-build cycle.
		int GetEffectiveExcessPower()
		{
			if (playerPower == null)
				return int.MaxValue;

			var pdm = player.PlayerActor.TraitsImplementing<PowerDownBotModule>()
				.FirstOrDefault(m => m.IsTraitEnabled());

			if (pdm == null)
				return playerPower.ExcessPower;

			var suppressedDraw = 0;
			foreach (var building in playerBuildings)
			{
				if (!pdm.Info.PowerDownTypes.Contains(building.Info.Name))
					continue;

				var modifier = building.TraitsImplementing<IPowerModifier>()
					.Aggregate(100, (acc, m) => acc * m.GetPowerModifier() / 100);

				if (modifier >= 100)
					continue;

				var fullDraw = building.Info.TraitInfos<PowerInfo>()
					.Where(p => p.EnabledByDefault)
					.Sum(p => p.Amount);

				if (fullDraw >= 0)
					continue;

				suppressedDraw += fullDraw * (100 - modifier) / 100;
			}

			return playerPower.ExcessPower + suppressedDraw;
		}

		bool HasSufficientPowerForActor(ActorInfo actorInfo)
		{
			return playerPower == null || actorInfo.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault)
				.Sum(p => p.Amount) + GetEffectiveExcessPower() >= baseBuilder.GetActiveMinimumExcessPower();
		}

		// Pre-flight check: returns true if at least one reachable resource field has a viable
		// building spot for the given refinery actor, and is not yet saturated by the cluster limit.
		// Uses terrain-only checks (no actor occupancy) to avoid false negatives from harvesters
		// or units temporarily blocking candidate cells. Actual placement handles the full check.
		bool HasViableRefineryField(ActorInfo refineryActorInfo)
		{
			if (resourceLayer == null)
				return true;

			var bi = refineryActorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return true;

			var baseCenter = baseBuilder.ResourceConyardCenter ?? baseBuilder.GetRandomBaseCenter();
			var effectiveMaxRadius = baseBuilder.GetEffectiveMaxBaseRadius(playerBuildings.Length);

			var resourceSearchRadius = Math.Max(effectiveMaxRadius, baseBuilder.Info.SellRefineryNoResourceDistance * 2);
			var nearbyResources = world.Map
				.FindTilesInAnnulus(baseCenter, baseBuilder.Info.MinBaseRadius, resourceSearchRadius)
				.Where(c => baseBuilder.ResourceMapModule != null
					? baseBuilder.ResourceMapModule.Info.ValuableResourceTypes.Contains(resourceLayer.GetResource(c).Type)
					: resourceLayer.GetResource(c).Type != null);

			if (baseBuilder.PathFinder != null && baseBuilder.HarvesterLocomotorsList.Length > 0)
				nearbyResources = nearbyResources.Where(c =>
					baseBuilder.HarvesterLocomotorsList.All(l =>
						baseBuilder.PathFinder.PathMightExistForLocomotorBlockedByImmovable(l, baseCenter, c)));

			if (baseBuilder.HarvesterLocomotorsList.Length > 0)
				nearbyResources = nearbyResources.Where(c => CountPassableOrthogonalNeighbors(c) >= 3);

			var nearbyResourceList = nearbyResources.ToList();
			if (nearbyResourceList.Count == 0)
				return false;

			var existingRefineryLocs = baseBuilder.RefineryBuildings.Actors
				.Where(a => !a.IsDead)
				.Select(a => a.Location)
				.ToList();

			// Apply the cluster saturation filter.
			if (baseBuilder.Info.MaxRefineriesPerCluster > 0 && existingRefineryLocs.Count > 0)
			{
				var clusterRadiusSq = baseBuilder.Info.RefineryClusterRadius * baseBuilder.Info.RefineryClusterRadius;
				nearbyResourceList = nearbyResourceList
					.Where(r => existingRefineryLocs.Count(loc => (loc - r).LengthSquared <= clusterRadiusSq) < baseBuilder.Info.MaxRefineriesPerCluster)
					.ToList();
			}

			if (nearbyResourceList.Count == 0)
				return false;

			// Terrain-only viability check — no actor occupancy, so harvesters can't cause false negatives.
			// Mirrors the anchor logic in GetRefineryCandidateCellsForField but skips CanPlaceBuilding.
			bool IsTerrainViable(CPos cell) =>
				world.Map.Contains(cell) &&
				world.Map.Ramp[cell] == 0 &&
				bi.TerrainTypes.Contains(world.Map.GetTerrainInfo(cell).Type);

			var closestRefineryLoc = existingRefineryLocs.Count > 0
				? existingRefineryLocs.OrderBy(loc => (loc - baseCenter).LengthSquared).First()
				: (CPos?)null;
			var sampleCells = SelectRefineryResourceCells(nearbyResourceList, baseCenter, closestRefineryLoc);

			foreach (var r in sampleCells)
			{
				var fieldSample = GetFieldSampleCells(nearbyResourceList, r);
				if (fieldSample == null || fieldSample.Length == 0)
					continue;

				var minX = fieldSample.Min(c => c.X);
				var maxX = fieldSample.Max(c => c.X);
				var minY = fieldSample.Min(c => c.Y);
				var maxY = fieldSample.Max(c => c.Y);
				var midX = (minX + maxX) / 2;
				var midY = (minY + maxY) / 2;

				// Check the four sides of this field cluster for terrain-viable placement spots.
				var sideAnchors = new[]
				{
					new CPos(minX - bi.Dimensions.X - 1, midY),
					new CPos(maxX + 1, midY),
					new CPos(midX, minY - bi.Dimensions.Y - 1),
					new CPos(midX, maxY + 1)
				};

				foreach (var anchor in sideAnchors)
				{
					foreach (var cell in world.Map.FindTilesInAnnulus(anchor, 0, 4))
					{
						if (!IsTerrainViable(cell)) continue;
						if (!bi.IsCloseEnoughToBase(world, player, refineryActorInfo, cell)) continue;
						return true;
					}
				}
			}

			// The scan above is a fast reject, not proof. It samples a few fields, probes four anchors
			// per field and demands flat, ramp-free ground within four cells of them — on broken
			// terrain, which is exactly where an expansion tends to land, it reports "nowhere" while
			// the full placement search would still find a spot. Taken as final it stopped bots from
			// ever building a refinery at an expansion that had tiberium right next to it.
			//
			// So when the resource map says a field genuinely still supports another refinery, let the
			// placement attempt be the judge. A real miss is already handled: the refinery is
			// cancelled and refineryPlacementCooldownTicks throttles the retry.
			if (baseBuilder.HasViableRefineryExpansionOpportunity())
				return true;

			// With no refinery at all, queue one regardless: placement then falls back to the base grid
			// pointing at a field, which is a poor spot but better than no income. Matched to the same
			// condition in the placement fallback — from the second refinery onward a spot beside the
			// tiberium is the whole point, and one parked in the middle of the base is not worth its
			// plot or its power.
			return existingRefineryLocs.Count < 1;
		}

		ActorInfo ChooseBuildingToBuild(ProductionQueue queue)
		{
			var buildableThings = queue.BuildableItems().ToList();

			// This gets used quite a bit, so let's cache it here
			var power = GetProducibleBuilding(baseBuilder.Info.PowerTypes, buildableThings,
				a => a.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount));

			// First priority is to get out of a low power situation.
			// Use effective excess power so powered-down buildings' suppressed draw is accounted for.
			var effectiveExcessPower = GetEffectiveExcessPower();
			if (playerPower != null && effectiveExcessPower < minimumExcessPower &&
				power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount) > 0)
			{
				CNBotLog.Debug("{0} decided to build {1}: Priority override (low power)", queue.Actor.Owner, power.Name);
				return power;
			}

			// Next is to build up a strong economy
			var wantsEconomy = baseBuilder.ShouldExpandEconomy();
			if (!wantsEconomy || refineryPlacementCooldownTicks > 0)
				CNBotLog.Debug("{0} refinery: skipped (wantsEconomy {1}, have {2}/{3}, cooldown {4})",
					player, wantsEconomy,
					AIUtils.CountActorByCommonName(baseBuilder.RefineryBuildings),
					baseBuilder.GetTargetRefineryCount(), refineryPlacementCooldownTicks);

			if (wantsEconomy && refineryPlacementCooldownTicks <= 0)
			{
				var refinery = GetProducibleBuilding(baseBuilder.Info.RefineryTypes, buildableThings);
				if (refinery == null)
					CNBotLog.Debug("{0} refinery: none buildable in this queue", player);
				else if (!HasSufficientPowerForActor(refinery))
					CNBotLog.Debug("{0} refinery: {1} blocked on power", player, refinery.Name);

				if (refinery != null && HasSufficientPowerForActor(refinery))
				{
					// Pre-flight: skip queuing if no buildable spot exists near a reachable resource field.
					// The very first refinery is exempt — it uses base-center fallback placement regardless.
					var hasExistingRefinery = AIUtils.CountActorByCommonName(baseBuilder.RefineryBuildings) > 0;
					var hasRequestedExpansionRefinery = baseBuilder.RequestedRefineries.Count > 0;
					if (!hasExistingRefinery || hasRequestedExpansionRefinery || HasViableRefineryField(refinery))
					{
						baseBuilder.RefineryExpansionBlocked = false;
						CNBotLog.Debug("{0} decided to build {1}: Priority override (refinery)", queue.Actor.Owner, refinery.Name);
						return refinery;
					}
					else
					{
						// No viable spot for a second refinery — signal that expansion is blocked so
						// PauseUnitProduction doesn't hold units hostage waiting for an impossible refinery.
						CNBotLog.Debug("{0} refinery: no viable field near {1} (have {2}, opportunity {3})",
							player, baseBuilder.ResourceConyardCenter ?? baseBuilder.GetRandomBaseCenter(),
							AIUtils.CountActorByCommonName(baseBuilder.RefineryBuildings),
							baseBuilder.HasViableRefineryExpansionOpportunity());

						baseBuilder.RefineryExpansionBlocked = true;
					}
				}

				if (power != null && refinery != null && !HasSufficientPowerForActor(refinery))
				{
					CNBotLog.Debug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}
			else if (!baseBuilder.ShouldExpandEconomy())
			{
				baseBuilder.RefineryExpansionBlocked = false;
			}

			// Build walls around protected structures, around the core base for defensive profiles, or across chokepoints.
			if (baseBuilder.Info.ProtectedByWalls.Count > 0 || baseBuilder.ShouldBuildBasePerimeterWalls() || baseBuilder.ShouldSealChokepoints())
			{
				if ((baseBuilder.ShouldBuildBasePerimeterWalls() || baseBuilder.ShouldSealChokepoints())
					&& CountExistingAndQueuedGates() < baseBuilder.Info.BasePerimeterMaxGateCount)
				{
					foreach (var gate in buildableThings
						.Where(a => baseBuilder.Info.GateTypes.Contains(a.Name))
						.OrderBy(a => CountExistingAndQueuedBuilding(a.Name)))
					{
						if (!HasSufficientPowerForActor(gate))
							continue;

						var (gateLoc, _, _) = ChooseGateLocation(gate);
						if (gateLoc != null)
						{
							CNBotLog.Debug("{0} decided to build {1}: Priority override (gate)", queue.Actor.Owner, gate.Name);
							return gate;
						}
					}
				}

				var wall = GetProducibleBuilding(baseBuilder.Info.WallTypes, buildableThings);
				if (wall != null && HasSufficientPowerForActor(wall))
				{
					var wallActorInfo = world.Map.Rules.Actors[wall.Name];
					var (wallLoc, _, _) = ChooseWallLocation(wallActorInfo);
					if (wallLoc != null)
					{
						CNBotLog.Debug("{0} decided to build {1}: Priority override (wall)", queue.Actor.Owner, wall.Name);
						return wall;
					}
				}
			}

			// Make sure that we can spend as fast as we are earning
			if (baseBuilder.GetActiveNewProductionCashThreshold() > 0 && baseBuilder.ShouldAddProduction()
				&& world.LocalRandom.Next(100) < baseBuilder.GetActiveNewProductionChance())
			{
				var production = GetProducibleBuilding(baseBuilder.Info.ProductionTypes, buildableThings);
				if (production != null && HasSufficientPowerForActor(production))
				{
					CNBotLog.Debug("{0} decided to build {1}: Priority override (production)", queue.Actor.Owner, production.Name);
					return production;
				}

				if (power != null && production != null && !HasSufficientPowerForActor(production))
				{
					CNBotLog.Debug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Only consider building this if there is enough water inside the base perimeter and there are close enough adjacent buildings
			if (waterState == WaterCheck.EnoughWater && baseBuilder.GetActiveNewProductionCashThreshold() > 0
				&& baseBuilder.ShouldAddProduction()
				&& AIUtils.IsAreaAvailable<GivesBuildableArea>(world, player, world.Map, baseBuilder.Info.CheckForWaterRadius, baseBuilder.Info.WaterTerrainTypes))
			{
				var navalproduction = GetProducibleBuilding(baseBuilder.Info.NavalProductionTypes, buildableThings);
				if (navalproduction != null && HasSufficientPowerForActor(navalproduction))
				{
					CNBotLog.Debug("{0} decided to build {1}: Priority override (navalproduction)", queue.Actor.Owner, navalproduction.Name);
					return navalproduction;
				}

				if (power != null && navalproduction != null && !HasSufficientPowerForActor(navalproduction))
				{
					CNBotLog.Debug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Create some head room for resource storage if we really need it
			if (baseBuilder.HasStoragePressure())
			{
				var silo = GetProducibleBuilding(baseBuilder.Info.SiloTypes, buildableThings);
				if (silo != null && HasSufficientPowerForActor(silo))
				{
					CNBotLog.Debug("{0} decided to build {1}: Priority override (silo)", queue.Actor.Owner, silo.Name);
					return silo;
				}

				if (power != null && silo != null && !HasSufficientPowerForActor(silo))
				{
					CNBotLog.Debug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Pre-compute defense counts once for DefenseRoleLimits checks below.
			var totalDefenseCount = 0;
			Dictionary<string, int> tagDefenseCounts = null;
			Dictionary<DefenseRole, int> roleDefenseCounts = null;
			var activeDefLimits = baseBuilder.GetActiveDefenseRoleLimits();
			if (activeDefLimits != null && activeDefLimits.Count > 0
				&& baseBuilder.Info.DefenseTypes.Count > 0)
			{
				tagDefenseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				roleDefenseCounts = [];
				foreach (var a in playerBuildings.Where(b => baseBuilder.Info.DefenseTypes.Contains(b.Info.Name)))
				{
					totalDefenseCount++;
					var caps = a.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
					if (caps != null)
						foreach (var tag in caps)
							tagDefenseCounts[tag] = (tagDefenseCounts.TryGetValue(tag, out var tc) ? tc : 0) + 1;

					// A defense counts towards every threat role it covers, not just its primary one.
					// The candidate lookups match on any capability tag, so counting only one of them
					// would let a multi-role building answer a role it never fills up (an Obelisk
					// tagged InfantryDefense/ArmorDefense used to raise one count alone and stayed
					// eligible for the other forever). SpecialDefense is not among them - see
					// GetDefenseRolesFromActor.
					foreach (var dr in GetDefenseRolesFromActor(a.Info))
						roleDefenseCounts[dr] = (roleDefenseCounts.TryGetValue(dr, out var rc) ? rc : 0) + 1;
				}
			}

			// Reactive defense: prefer the role that matches the current hotspot,
			// then fall back to the global combat analysis trend.
			var ca = player.PlayerActor.TraitsImplementing<CombatAnalysisBotModule>()
				.FirstOrDefault(m => !m.IsTraitDisabled);
			var defenseCenterForThreat = baseBuilder.GetDefenseReference(baseBuilder.GetRandomBaseCenter());
			var hotspotRole = baseBuilder.GetBestDefenseHotspotRole(defenseCenterForThreat);
			var reactiveRole = hotspotRole != DefenseRole.Default
				? hotspotRole
				: ca != null && ca.HasActiveThreat()
					? ca.GetHighestThreatRole()
					: DefenseRole.Default;
			if (defensePlacementCooldownTicks <= 0)
			{
				if (reactiveRole != DefenseRole.Default)
				{
					var reactiveDefense = ChooseReactiveDefense(buildableThings, reactiveRole, totalDefenseCount, roleDefenseCounts);
					if (reactiveDefense != null)
					{
						CNBotLog.Debug("{0} reactive defense: building {1} (threat role: {2})",
							player, reactiveDefense.Name, reactiveRole);
						return reactiveDefense;
					}
				}

				var plannedDefense = ChoosePlannedDefense(buildableThings, totalDefenseCount, roleDefenseCounts);
				if (plannedDefense != null)
				{
					CNBotLog.Debug("{0} planned defense: building {1}", player, plannedDefense.Name);
					return plannedDefense;
				}
			}

			// Build everything else. Prefer the structure with the largest deficit relative
			// to its desired base fraction instead of accepting the first random match.
			ActorInfo bestFractionActor = null;
			string bestFractionName = null;
			var bestFractionValue = 0;
			var bestFractionCount = 0;
			var bestFractionDeficit = int.MinValue;
			var baseBuildingCount = Math.Max(1, playerBuildings.Length);
			var buildableByName = buildableThings.ToDictionary(b => b.Name);
			var activeFractions = baseBuilder.GetActiveBuildingFractions();

			foreach (var frac in activeFractions)
			{
				var name = frac.Key;

				// Does this building have initial delay, if so have we passed it?
				if (baseBuilder.Info.BuildingDelays != null &&
					baseBuilder.Info.BuildingDelays.TryGetValue(name, out var delay) &&
					delay > world.WorldTick)
					continue;

				// Can we build this structure?
				if (!buildableByName.TryGetValue(name, out var actor))
					continue;

				if (baseBuilder.Info.DefenseTypes.Contains(name))
					continue;

				// Check the number of this structure and its variants
				var count = CountExistingAndQueuedBuilding(name);

				// A base still missing one of its guaranteed capabilities may build past the global fraction
				// cap. The cap is measured against ALL bases, so without this the redundancy floor fails
				// exactly when it matters: the main base holds the whole quota and the expansion, however
				// exposed, never gets its first barracks. Bounded per base, and BuildingLimits still apply.
				var capabilityFloorException = baseBuilder.AllowsCapabilityFloorException(name);

				// Do we want to build this structure?
				if (count * 100 > frac.Value * playerBuildings.Length && !capabilityFloorException)
					continue;

				if (baseBuilder.Info.BuildingLimits.TryGetValue(name, out var limit) && baseBuilder.GetScaledBuildingLimit(limit) <= count)
					continue;

				// DefenseRoleLimits: scale-based cap relative to base size (replaces hard limits for defenses)
				if (tagDefenseCounts != null && baseBuilder.Info.DefenseTypes.Contains(name))
				{
					if (activeDefLimits.TryGetValue("Total", out var totalLimit) &&
						totalDefenseCount * 100 >= totalLimit * playerBuildings.Length)
						continue;

					// Check every BotCapabilities tag of this actor against DefenseRoleLimits.
					var actorCaps = actor.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
					if (actorCaps != null)
					{
						var hitLimit = false;
						foreach (var tag in actorCaps)
						{
							if (activeDefLimits.TryGetValue(tag, out var tagLimit) &&
								tagDefenseCounts.TryGetValue(tag, out var tagCnt) &&
								tagCnt * 100 >= tagLimit * playerBuildings.Length)
							{ hitLimit = true; break; }
						}

						if (hitLimit) continue;
					}

					// A defense without BotCapabilities declares no role at all, so only the Total cap
					// (checked above) governs it.
				}

				// If we're considering to build a naval structure, check whether there is enough water inside the base perimeter
				// and any structure providing buildable area close enough to that water.
				// TODO: Extend this check to cover any naval structure, not just production.
				if (baseBuilder.Info.NavalProductionTypes.Contains(name)
					&& (waterState == WaterCheck.NotEnoughWater
						|| !AIUtils.IsAreaAvailable<GivesBuildableArea>(world, player, world.Map, baseBuilder.Info.CheckForWaterRadius, baseBuilder.Info.WaterTerrainTypes)))
					continue;

				// Skip buildings that require specific resource types (e.g. Nod Vein Harvester without veins on map)
				if (baseBuilder.Info.VeinsOnlyBuildingTypes.Contains(name) && !baseBuilder.HasVeinResources())
					continue;

				// A floor exception has to outrank the normal deficits as well, otherwise it is inert: the
				// type it applies to is by definition over its share and would always sort last.
				var deficit = capabilityFloorException
					? CapabilityFloorDeficit
					: frac.Value * baseBuildingCount - count * 100;
				if (deficit < bestFractionDeficit)
					continue;

				// Jitter exact ties so multiple AIs don't converge on the same build order.
				if (deficit == bestFractionDeficit && world.LocalRandom.Next(2) == 0)
					continue;

				bestFractionActor = actor;
				bestFractionName = name;
				bestFractionValue = frac.Value;
				bestFractionCount = count;
				bestFractionDeficit = deficit;
			}

			if (bestFractionActor != null)
			{
				// Will this put us into low power?
				if (playerPower != null && (effectiveExcessPower < minimumExcessPower || !HasSufficientPowerForActor(bestFractionActor)))
				{
					// Try building a power plant instead
					if (power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(pi => pi.Amount) > 0)
					{
						if (playerPower.PowerOutageRemainingTicks > 0)
							CNBotLog.Debug("{0} decided to build {1}: Priority override (is low power)", queue.Actor.Owner, power.Name);
						else
							CNBotLog.Debug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);

						return power;
					}
				}

				// Lets build this
				CNBotLog.Debug("{0} decided to build {1}: Desired is {2} ({3} / {4}); current is {5} / {4}",
					queue.Actor.Owner, bestFractionName, bestFractionValue, bestFractionValue * playerBuildings.Length,
					playerBuildings.Length, bestFractionCount);
				return bestFractionActor;
			}

			// Too spammy to keep enabled all the time, but very useful when debugging specific issues.
			// CNBotLog.Debug("{0} couldn't decide what to build for queue {1}.", queue.Actor.Owner, queue.Info.Group);
			return null;
		}

		/// <summary>
		/// Returns the best defense building to build reactively based on the highest-threat role.
		/// Respects DefenseRoleLimits; returns null if no eligible building exists.
		/// </summary>
		ActorInfo ChooseReactiveDefense(
			IEnumerable<ActorInfo> buildableThings,
			DefenseRole role,
			int totalDefenseCount,
			Dictionary<DefenseRole, int> roleDefenseCounts)
		{
			if (role == DefenseRole.Default)
				return null;

			// Find candidates by BotCapabilities tag matching the role enum name.
			var roleStr = role.ToString();
			var candidates = buildableThings
				.Where(b => b.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains(roleStr) ?? false)
				.ToList();

			if (candidates.Count == 0)
				return null;

			// The loop below returns the first candidate that passes, so the order IS the decision.
			// Unordered, that was whichever the production queue happened to list first, forever: for
			// any role several towers can fill, the first one answered every reactive build and the
			// rest never got a turn - gamg took every InfantryDefense call with gagat sitting behind
			// it, gacan every ArmorDefense call ahead of gasen and gamortar.
			//
			// Rarity is the same preference ChoosePlannedDefense already applies on a tie, so the two
			// paths no longer disagree about what to build. Worth leads it while under threat, which
			// is what the SpecialDefense tag buys now that it no longer blocks a budget.
			var preferHighValue = baseBuilder.IsUnderActiveThreat();
			candidates = candidates
				.OrderByDescending(a => preferHighValue && IsHighValueDefense(a))
				.ThenBy(a => CountExistingAndQueuedBuilding(a.Name))
				.ToList();

			var reactiveDefLimits = baseBuilder.GetActiveDefenseRoleLimits();

			// Check total defense limit first
			if (reactiveDefLimits != null &&
				reactiveDefLimits.TryGetValue("Total", out var totalLimit) &&
				totalDefenseCount * 100 >= totalLimit * playerBuildings.Length)
				return null;

			// Check role limit
			if (reactiveDefLimits != null &&
				reactiveDefLimits.TryGetValue(roleStr, out var roleLimit) &&
				roleDefenseCounts != null &&
				roleDefenseCounts.TryGetValue(role, out var roleCnt) &&
				roleCnt * 100 >= roleLimit * playerBuildings.Length)
				return null;

			foreach (var actorInfo in candidates)
			{
				if (!HasSufficientPowerForActor(actorInfo))
					continue;

				// The requested role still has room, but this candidate may cover other roles that are
				// already full. Checking only the requested one would let an expensive multi-role
				// building spend a cheap role's budget long after its own role ran out.
				if (reactiveDefLimits != null && IsAnyDefenseRoleFull(actorInfo, reactiveDefLimits, roleDefenseCounts))
					continue;

				return actorInfo;
			}

			return null;
		}

		/// <summary>True if any role this defense covers is at or above its share of the base.</summary>
		bool IsAnyDefenseRoleFull(ActorInfo actorInfo, IReadOnlyDictionary<string, int> defLimits,
			Dictionary<DefenseRole, int> roleDefenseCounts)
		{
			foreach (var r in GetDefenseRolesFromActor(actorInfo))
			{
				if (!defLimits.TryGetValue(r.ToString(), out var limit))
					continue;

				var count = 0;
				roleDefenseCounts?.TryGetValue(r, out count);
				if (count * 100 >= limit * playerBuildings.Length)
					return true;
			}

			return false;
		}

		ActorInfo ChoosePlannedDefense(
			IEnumerable<ActorInfo> buildableThings,
			int totalDefenseCount,
			Dictionary<DefenseRole, int> roleDefenseCounts)
		{
			var activeDefLimits = baseBuilder.GetActiveDefenseRoleLimits();
			if (activeDefLimits == null || activeDefLimits.Count == 0 || baseBuilder.Info.DefenseTypes.Count == 0)
				return null;

			var baseBuildingCount = Math.Max(1, playerBuildings.Length);
			var totalDeficit = int.MaxValue;
			if (activeDefLimits.TryGetValue(CNBaseBuilderBotModule.TotalDefenseLimitKey, out var totalLimit))
			{
				totalDeficit = totalLimit * baseBuildingCount - totalDefenseCount * 100;
				if (totalDeficit <= 0)
					return null;
			}

			ActorInfo bestActor = null;
			var bestDeficit = int.MinValue;
			var bestTypeCount = int.MaxValue;
			var bestHighValue = false;

			// Worth only breaks ties while something is actually attacking. Outside a threat the bot
			// keeps spreading its defenses by deficit and rarity, so it does not sink its whole
			// defense budget into the most expensive tower during a quiet build-up.
			var preferHighValue = baseBuilder.IsUnderActiveThreat();

			foreach (var actorInfo in buildableThings.Where(a => baseBuilder.Info.DefenseTypes.Contains(a.Name)))
			{
				// Rate a multi-role defense by its scarcest role: it is blocked as soon as any of the
				// roles it covers is at its limit, so its deficit has to be the smallest one as well.
				// Otherwise a building would keep winning on a role it is no longer allowed to fill.
				var deficit = int.MaxValue;
				var hasLimitedRole = false;
				foreach (var role in GetDefenseRolesFromActor(actorInfo))
				{
					if (!activeDefLimits.TryGetValue(role.ToString(), out var roleLimit))
						continue;

					var roleCount = 0;
					roleDefenseCounts?.TryGetValue(role, out roleCount);
					hasLimitedRole = true;
					deficit = Math.Min(deficit, roleLimit * baseBuildingCount - roleCount * 100);
				}

				// A defense that is budgeted against no role at all - a pure high-value tower, since
				// SpecialDefense is not a budget - is governed by the Total cap alone, so it inherits
				// that headroom. Skipping it instead made it silently unbuildable: no error, no lint
				// hit, the building simply never appeared.
				if (!hasLimitedRole)
					deficit = totalDeficit;

				if (deficit <= 0)
					continue;

				if (!HasSufficientPowerForActor(actorInfo))
					continue;

				var typeCount = CountExistingAndQueuedBuilding(actorInfo.Name);
				var highValue = preferHighValue && IsHighValueDefense(actorInfo);

				if (deficit < bestDeficit)
					continue;

				if (deficit == bestDeficit)
				{
					if (highValue != bestHighValue)
					{
						if (!highValue)
							continue;
					}
					else if (typeCount >= bestTypeCount)
						continue;
				}

				bestActor = actorInfo;
				bestDeficit = deficit;
				bestTypeCount = typeCount;
				bestHighValue = highValue;
			}

			return bestActor;
		}

		/// <summary>
		/// Every defense role this actor is BUDGETED against: the role limits are budgets, and a
		/// building occupies a slot in each threat it can answer. Placement needs a single role
		/// instead (see <see cref="GetDefenseRoleFromActor"/>).
		/// <para>
		/// SpecialDefense is deliberately absent. It marks a high-value tower rather than a threat
		/// answered, so as a budget it could only ever subtract: every high-value tower of both
		/// factions shared one narrow cap, and the more threats a tower covered the harder it was
		/// throttled. A tower is capped by what it defends against; its worth steers selection.
		/// </para>
		/// </summary>
		static List<DefenseRole> GetDefenseRolesFromActor(ActorInfo actorInfo)
		{
			var roles = new List<DefenseRole>();
			foreach (var role in ParseDeclaredDefenseRoles(actorInfo))
				if (role != DefenseRole.SpecialDefense && !roles.Contains(role))
					roles.Add(role);

			return roles;
		}

		/// <summary>
		/// The single role that decides how this defense is PLACED. SpecialDefense wins when present:
		/// it is the most specific statement a building makes about itself and carries its own
		/// placement style (outermost radius on the enemy approach vector). Otherwise the first
		/// declared role wins.
		/// </summary>
		static DefenseRole GetDefenseRoleFromActor(ActorInfo actorInfo)
		{
			var first = DefenseRole.Default;
			foreach (var role in ParseDeclaredDefenseRoles(actorInfo))
			{
				if (role == DefenseRole.SpecialDefense)
					return role;

				if (first == DefenseRole.Default)
					first = role;
			}

			return first;
		}

		/// <summary>
		/// Defense roles in the order the actor declares them. Reads the Capabilities array rather
		/// than CapabilitySet: the set is a HashSet whose enumeration order is not guaranteed, and
		/// the placement role used to be whichever parseable tag it happened to see last.
		/// </summary>
		static IEnumerable<DefenseRole> ParseDeclaredDefenseRoles(ActorInfo actorInfo)
		{
			var caps = actorInfo.TraitInfoOrDefault<BotCapabilitiesInfo>()?.Capabilities;
			if (caps == null)
				yield break;

			foreach (var tag in caps)
				if (Enum.TryParse<DefenseRole>(tag, true, out var role) && role != DefenseRole.Default)
					yield return role;
		}

		/// <summary>True if this defense declares itself a high-value tower.</summary>
		static bool IsHighValueDefense(ActorInfo actorInfo)
		{
			foreach (var role in ParseDeclaredDefenseRoles(actorInfo))
				if (role == DefenseRole.SpecialDefense)
					return true;

			return false;
		}

		// Pick the base this building belongs to, then place it there. If that base has no room the next
		// base in the preference order gets a try, so a full main base does not stall the whole queue.
		(CPos? Location, CPos? BaseCenter, int Variant) ChooseBuildLocation(string actorType, bool distanceToBaseIsImportant, BuildingType type)
		{
			baseBuilder.Info.BuildingLayouts.TryGetValue(actorType, out var entry);
			var orderedBases = baseBuilder.GetOrderedBasesForBuilding(actorType, type, entry?.NearBuilding);

			// Only ordinary buildings are retried in another base. Defense and refinery placement is
			// anchored on the threat / resource center rather than on the base, so a second pass would
			// repeat the same expensive scan for the same result.
			var attempts = type == BuildingType.Building
				? Math.Min(orderedBases.Count, MaxBasePlacementAttempts)
				: 1;
			var lastResult = ((CPos?)null, (CPos?)null, 0);
			for (var i = 0; i < attempts; i++)
			{
				var result = ChooseBuildLocationInBase(actorType, distanceToBaseIsImportant, type, orderedBases[i]);
				if (result.Location.HasValue)
					return result;

				lastResult = result;
			}

			return lastResult;
		}

		(CPos? Location, CPos? BaseCenter, int Variant) ChooseBuildLocationInBase(string actorType, bool distanceToBaseIsImportant,
			BuildingType type, CNBotBase targetBase)
		{
			var actorInfo = world.Map.Rules.Actors[actorType];
			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();

			if (bi == null)
				return (null, null, 0);

			// Determine layout for this building type
			var layout = baseBuilder.Info.DefaultLayout;
			var minSpacing = baseBuilder.Info.SameTypeMinSpacing;
			if (baseBuilder.Info.BuildingLayouts.TryGetValue(actorType, out var layoutEntry))
			{
				layout = layoutEntry.Layout;
				minSpacing = layoutEntry.MinSpacing;
			}

			var sameTypeBuildingsForSpacing = playerBuildings
				.Where(a => a.Info.Name == actorType)
				.Select(a => a.Location)
				.ToList();
			var refineryBuildingInfosForSpacing = type == BuildingType.Refinery
				? playerBuildings
					.Where(a => baseBuilder.Info.RefineryTypes.Contains(a.Info.Name))
					.Select(a => (Actor: a, BI: a.Info.TraitInfoOrDefault<BuildingInfo>()))
					.Where(t => t.BI != null)
					.ToList()
				: null;

			// Precompute once here so RespectsGeneralBuildingSpacing never calls TraitInfoOrDefault
			// inside its O(N_buildings) loop — that lookup was the main cost in FindPos.
			var globalBuildingInfos = baseBuilder.Info.GlobalMinSpacing > 0
				? playerBuildings
					.Select(b => (Actor: b, BI: b.Info.TraitInfoOrDefault<BuildingInfo>()))
					.Where(t => t.BI != null)
					.ToArray()
				: null;

			bool RespectsGeneralBuildingSpacing(CPos cell, BuildingInfo candidateBuildingInfo)
			{
				if (type == BuildingType.Refinery)
				{
					if (minSpacing > 0 && refineryBuildingInfosForSpacing != null && refineryBuildingInfosForSpacing.Count > 0)
					{
						var refineryRight = cell.X + candidateBuildingInfo.Dimensions.X;
						var refineryBottom = cell.Y + candidateBuildingInfo.Dimensions.Y;
						foreach (var (existing, existingBi) in refineryBuildingInfosForSpacing)
						{
							var existingRight = existing.Location.X + existingBi.Dimensions.X;
							var existingBottom = existing.Location.Y + existingBi.Dimensions.Y;
							var gapX = Math.Max(0, Math.Max(existing.Location.X - refineryRight, cell.X - existingRight));
							var gapY = Math.Max(0, Math.Max(existing.Location.Y - refineryBottom, cell.Y - existingBottom));
							if (Math.Max(gapX, gapY) < minSpacing)
								return false;
						}
					}

					return true;
				}

				var minSpacingSq = minSpacing * minSpacing;
				if (minSpacing > 0 && sameTypeBuildingsForSpacing.Count > 0
					&& sameTypeBuildingsForSpacing.Any(loc => (cell - loc).LengthSquared < minSpacingSq))
					return false;

				if (globalBuildingInfos == null)
					return true;

				var globalSpacing = baseBuilder.Info.GlobalMinSpacing;
				var hasLayoutOverride = baseBuilder.Info.BuildingLayouts.ContainsKey(actorType);
				var newRight = cell.X + candidateBuildingInfo.Dimensions.X;
				var newBottom = cell.Y + candidateBuildingInfo.Dimensions.Y;
				foreach (var (existing, existingBi) in globalBuildingInfos)
				{
					if (hasLayoutOverride && existing.Info.Name == actorType)
						continue;

					var existingRight = existing.Location.X + existingBi.Dimensions.X;
					var existingBottom = existing.Location.Y + existingBi.Dimensions.Y;
					var gapX = Math.Max(0, Math.Max(existing.Location.X - newRight, cell.X - existingRight));
					var gapY = Math.Max(0, Math.Max(existing.Location.Y - newBottom, cell.Y - existingBottom));
					if (Math.Max(gapX, gapY) < globalSpacing)
						return false;
				}

				return true;
			}

			bool IsValuableResourceCell(CPos cell)
			{
				if (resourceLayer == null || !world.Map.Contains(cell))
					return false;

				var resourceType = resourceLayer.GetResource(cell).Type;
				if (resourceType == null)
					return false;

				return baseBuilder.ResourceMapModule == null ||
					baseBuilder.ResourceMapModule.Info.ValuableResourceTypes.Contains(resourceType);
			}

			bool IsTooCloseToValuableResources(CPos cell, BuildingInfo candidateBuildingInfo, int padding)
			{
				if (padding <= 0 || candidateBuildingInfo == null)
					return false;

				var minX = cell.X - padding;
				var maxX = cell.X + candidateBuildingInfo.Dimensions.X - 1 + padding;
				var minY = cell.Y - padding;
				var maxY = cell.Y + candidateBuildingInfo.Dimensions.Y - 1 + padding;

				for (var y = minY; y <= maxY; y++)
				{
					for (var x = minX; x <= maxX; x++)
					{
						if (IsValuableResourceCell(new CPos(x, y)))
							return true;
					}
				}

				return false;
			}

			// One raster for the whole base. The step no longer depends on the building's own footprint:
			// it is the base raster, or - for types that declare a tighter MinSpacing than GlobalMinSpacing -
			// the finest exact subdivision of the raster their footprint still fits into. Anything bigger
			// than one raster cell simply consumes the neighbouring cells; the gap between footprints is
			// enforced by RespectsGeneralBuildingSpacing, not by the raster.
			//
			// The old formula (footprint + MinSpacing) gave every building type its own incommensurate
			// pitch (3, 4, 5, ...), so buildings of different types never lined up, and for same-type
			// buildings every second raster point produced a gap below GlobalMinSpacing and was rejected.
			var baseGridPitch = Math.Max(1, baseBuilder.Info.BaseGridCellSize);

			// Types with a BuildingLayouts entry are exempt from GlobalMinSpacing against their own kind
			// (that is what keeps the power plant rows and helipad blocks tight), so they keep their
			// declared padding here. Everything else must leave at least GlobalMinSpacing.
			var gridPadding = baseBuilder.Info.BuildingLayouts.ContainsKey(actorType)
				? Math.Max(0, minSpacing)
				: Math.Max(minSpacing, baseBuilder.Info.GlobalMinSpacing);

			int BaseGridStep(int dimension)
			{
				var span = Math.Max(1, dimension + gridPadding);
				if (span >= baseGridPitch)
					return baseGridPitch;

				for (var divisor = 1; divisor < baseGridPitch; divisor++)
					if (baseGridPitch % divisor == 0 && divisor >= span)
						return divisor;

				return baseGridPitch;
			}

			bool IsAlignedToBaseGrid(CPos cell, BuildingInfo candidateBuildingInfo, CPos gridAnchor)
			{
				if (layout != BaseBuildingLayout.BaseGrid || candidateBuildingInfo == null)
					return false;

				var gx = BaseGridStep(candidateBuildingInfo.Dimensions.X);
				var gy = BaseGridStep(candidateBuildingInfo.Dimensions.Y);
				return ((cell.X - gridAnchor.X) % gx + gx) % gx == 0
					&& ((cell.Y - gridAnchor.Y) % gy + gy) % gy == 0;
			}

			int ScoreBaseGridAlignment(CPos cell, BuildingInfo candidateBuildingInfo, CPos gridAnchor)
			{
				if (layout != BaseBuildingLayout.BaseGrid)
					return 0;

				return IsAlignedToBaseGrid(cell, candidateBuildingInfo, gridAnchor) ? -30 : 120;
			}

			CPos? FindNearestReachableResource(CPos origin, int maxRange)
			{
				if (resourceLayer == null)
					return null;

				foreach (var cell in world.Map.FindTilesInAnnulus(origin, baseBuilder.Info.MinBaseRadius, maxRange)
					.Where(IsValuableResourceCell)
					.Where(c =>
						(baseBuilder.HarvesterLocomotorsList.Length == 0 || CountPassableOrthogonalNeighbors(c) >= 2) &&
						(baseBuilder.PathFinder == null || baseBuilder.HarvesterLocomotorsList.Length == 0 ||
							baseBuilder.HarvesterLocomotorsList.All(l =>
								baseBuilder.PathFinder.PathMightExistForLocomotorBlockedByImmovable(l, origin, c))))
					.OrderBy(c => (c - origin).LengthSquared))
					return cell;

				return null;
			}

			// Find the buildable cell that is closest to pos and centered around center.
			// BaseGrid prefers a shared footprint-aware base raster, then falls back to compact placement.
			(CPos? Location, CPos Center, int Variant) FindPos(CPos center, CPos target, CPos gridAnchor, int minRange, int maxRange)
			{
				var isTech = baseBuilder.Info.TechTypes.Contains(actorType);
				var techDangerHotspots = isTech
					? baseBuilder.GetDefensePlacementThreats(center)
					: Array.Empty<CNBaseBuilderBotModule.DefensePlacementThreat>();

				var actorVariant = 0;
				var buildingVariantInfo = actorInfo.TraitInfoOrDefault<PlaceBuildingVariantsInfo>();
				var variantActorInfo = actorInfo;
				var vbi = bi;

				if (layout == BaseBuildingLayout.Random && center != target && buildingVariantInfo?.Actors != null)
				{
					if (buildingVariantInfo.Facings != null)
					{
						var vector = world.Map.CenterOfCell(target) - world.Map.CenterOfCell(center);
						if (vector.Length > 0)
						{
							var desiredFacing = new WAngle(WAngle.ArcSin((int)((long)Math.Abs(vector.X) * 1024 / vector.Length)).Angle);
							if (vector.X > 0 && vector.Y >= 0)
								desiredFacing = new WAngle(512) - desiredFacing;
							else if (vector.X < 0 && vector.Y >= 0)
								desiredFacing = new WAngle(512) + desiredFacing;
							else if (vector.X < 0 && vector.Y < 0)
								desiredFacing = -desiredFacing;

							for (var i = 0; i < buildingVariantInfo.Facings.Length; i++)
							{
								var minDelta = Math.Min(
									(desiredFacing - buildingVariantInfo.Facings[i]).Angle,
									(buildingVariantInfo.Facings[i] - desiredFacing).Angle);
								if (i == 0 || minDelta < Math.Min(
									(desiredFacing - buildingVariantInfo.Facings[actorVariant]).Angle,
									(buildingVariantInfo.Facings[actorVariant] - desiredFacing).Angle))
									actorVariant = i;
							}
						}
					}
					else
						actorVariant = world.LocalRandom.Next(buildingVariantInfo.Actors.Length + 1);
				}
				else if (buildingVariantInfo?.Actors != null)
				{
					actorVariant = world.LocalRandom.Next(buildingVariantInfo.Actors.Length + 1);
				}

				if (actorVariant != 0)
				{
					variantActorInfo = world.Map.Rules.Actors[buildingVariantInfo.Actors[actorVariant - 1]];
					vbi = variantActorInfo.TraitInfoOrDefault<BuildingInfo>();
				}

				(CPos? Location, CPos Center, int Variant) TryFindPos(BaseBuildingLayout activeLayout)
				{
					const int FindPosLimit = 256;
					var allCells = activeLayout == BaseBuildingLayout.Grid || activeLayout == BaseBuildingLayout.BaseGrid
						? world.Map.FindTilesInAnnulus(center, minRange, maxRange)
						: world.Map.FindTilesInAnnulus(center, minRange, maxRange).Take(FindPosLimit);

					var sameTypeBuildings = playerBuildings
						.Where(a => a.Info.Name == actorType)
						.Select(a => a.Location)
						.ToList();

					IEnumerable<CPos> cells;
					if (activeLayout == BaseBuildingLayout.Compact)
						cells = allCells.OrderBy(c => (c - center).LengthSquared);
					else if (activeLayout == BaseBuildingLayout.Clustered && sameTypeBuildings.Count > 0)
						cells = allCells.OrderBy(c => sameTypeBuildings.Min(loc => (c - loc).LengthSquared))
							.ThenBy(c => (c - target).LengthSquared);
					else if (activeLayout == BaseBuildingLayout.Grid || activeLayout == BaseBuildingLayout.BaseGrid)
					{
						var isBaseGrid = activeLayout == BaseBuildingLayout.BaseGrid;
						var anchor = !isBaseGrid && sameTypeBuildings.Count > 0
							? sameTypeBuildings[0]
							: gridAnchor;

						// BaseGrid: the shared base raster. Grid: the legacy per-type raster.
						var gx = isBaseGrid ? BaseGridStep(vbi.Dimensions.X) : Math.Max(1, vbi.Dimensions.X + Math.Max(0, minSpacing));
						var gy = isBaseGrid ? BaseGridStep(vbi.Dimensions.Y) : Math.Max(1, vbi.Dimensions.Y + Math.Max(0, minSpacing));
						cells = allCells
							.Where(c => ((c.X - anchor.X) % gx + gx) % gx == 0
								&& ((c.Y - anchor.Y) % gy + gy) % gy == 0)
							.OrderBy(c => (c - target).LengthSquared);
					}
					else if (activeLayout == BaseBuildingLayout.Random && center != target)
						cells = allCells.OrderBy(c => (c - target).LengthSquared);
					else if (activeLayout == BaseBuildingLayout.Random)
						cells = allCells.Shuffle(world.LocalRandom);
					else
						cells = allCells;

					if (isTech)
					{
						CPos? bestCell = null;
						var bestScore = long.MaxValue;
						foreach (var cell in cells)
						{
							if (!world.CanPlaceBuilding(cell, variantActorInfo, vbi, null))
								continue;

							if (distanceToBaseIsImportant && !vbi.IsCloseEnoughToBase(world, player, variantActorInfo, cell))
								continue;

							if (!RespectsGeneralBuildingSpacing(cell, vbi))
								continue;

							var score = baseBuilder.ScoreTechPlacementSafety(cell, center, techDangerHotspots)
								+ ScoreBaseGridAlignment(cell, vbi, gridAnchor);
							if (score >= bestScore)
								continue;

							bestScore = score;
							bestCell = cell;
						}

						return bestCell.HasValue ? (bestCell.Value, center, actorVariant) : (null, center, 0);
					}

					foreach (var cell in cells)
					{
						if (!world.CanPlaceBuilding(cell, variantActorInfo, vbi, null))
							continue;

						if (distanceToBaseIsImportant && !vbi.IsCloseEnoughToBase(world, player, variantActorInfo, cell))
							continue;

						if (!RespectsGeneralBuildingSpacing(cell, vbi))
							continue;

						return (cell, center, actorVariant);
					}

					return (null, center, 0);
				}

				var result = TryFindPos(layout);
				return result.Location.HasValue || layout != BaseBuildingLayout.BaseGrid
					? result
					: TryFindPos(BaseBuildingLayout.Compact);
			}

			var baseCenter = targetBase.Center;
			var planCenter = baseBuilder.GetBasePlanCenterForActor(
				actorInfo,
				targetBase,
				baseCenter,
				type == BuildingType.Defense,
				type == BuildingType.Refinery);

			// The radius grows with the size of THIS base, not with the bot's total building count —
			// otherwise a young expansion immediately claims the same radius as the fully built main base.
			var effectiveMaxRadius = baseBuilder.GetEffectiveMaxBaseRadius(targetBase.Buildings.Count);

			// If this building type has a NearBuilding override, shift the search center
			// toward the average position of all existing buildings of that type.
			// Falls back to baseCenter when no instances exist yet.
			var effectiveCenter = planCenter;
			if (layoutEntry?.NearBuilding != null)
			{
				CVec ClusterGroupOffset(int groupIndex, int spacing)
				{
					if (groupIndex <= 0)
						return CVec.Zero;

					var ring = (groupIndex + 7) / 8;
					var distance = Math.Max(1, spacing) * ring;
					var direction = (groupIndex - 1) % 8;
					return direction switch
					{
						0 => new CVec(distance, 0),
						1 => new CVec(-distance, 0),
						2 => new CVec(0, distance),
						3 => new CVec(0, -distance),
						4 => new CVec(distance, distance),
						5 => new CVec(-distance, distance),
						6 => new CVec(distance, -distance),
						_ => new CVec(-distance, -distance)
					};
				}

				// Only this base's instances count — otherwise a cluster in the main base drags
				// the search center of an expansion back across the map.
				var nearInstances = targetBase.Buildings
					.Where(b => b.Info.Name == layoutEntry.NearBuilding)
					.OrderBy(b => b.ActorID)
					.ToList();
				if (nearInstances.Count > 0)
				{
					var clusterGroupSize = Math.Max(0, layoutEntry.ClusterGroupSize);
					if (clusterGroupSize > 0 && layoutEntry.NearBuilding == actorType)
					{
						var groupIndex = nearInstances.Count / clusterGroupSize;
						var groupStart = groupIndex * clusterGroupSize;
						if (groupStart < nearInstances.Count)
						{
							nearInstances = nearInstances
								.Skip(groupStart)
								.Take(clusterGroupSize)
								.ToList();
						}
						else
						{
							var spacing = Math.Max(1, layoutEntry.ClusterGroupSpacing);
							effectiveCenter = planCenter + ClusterGroupOffset(groupIndex, spacing);
							nearInstances.Clear();
						}
					}

					if (nearInstances.Count > 0)
					{
						effectiveCenter = new CPos(
							(int)nearInstances.Average(b => b.Location.X),
							(int)nearInstances.Average(b => b.Location.Y));
					}
				}
			}

			// Bias the search toward the chosen ConYard (baseCenter).
			// effectiveCenter averages existing buildings, which are overwhelmingly
			// near spawn. If the chosen ConYard is an expansion base more than
			// effectiveMaxRadius cells away, the annulus never reaches it at all —
			// every building ends up at spawn. Switch to baseCenter in that case.
			if ((effectiveCenter - baseCenter).LengthSquared > effectiveMaxRadius * effectiveMaxRadius)
				effectiveCenter = baseCenter;

			switch (type)
			{
				case BuildingType.Defense:
				{
					// Resolve variant outside FindPos
					var defVariant = 0;
					var defBuildingVariantInfo = actorInfo.TraitInfoOrDefault<PlaceBuildingVariantsInfo>();
					var defVariantActorInfo = actorInfo;
					var defVbi = bi;
					if (defBuildingVariantInfo?.Actors != null)
					{
						defVariant = world.LocalRandom.Next(defBuildingVariantInfo.Actors.Length + 1);
						if (defVariant != 0)
						{
							defVariantActorInfo = world.Map.Rules.Actors[defBuildingVariantInfo.Actors[defVariant - 1]];
							defVbi = defVariantActorInfo.TraitInfoOrDefault<BuildingInfo>();
						}
					}

					// The ring of defense candidates is centred on where the bot believes it is threatened.
					// That used to be the raw position of whoever attacked last, so the whole ring jumped
					// with every individual attacker; it is now the weighted danger hotspot, which merges
					// nearby attacks and is scored by how often and how hard the bot was hit there.
					var defenseCenter = baseBuilder.GetDefenseReference(baseCenter);
					var rememberedHotspot = baseBuilder.GetBestDefenseHotspot(defenseCenter);
					var targetCell = rememberedHotspot ?? defenseCenter;

					// Reuse the outer-scope precomputed array (same data, avoids a second TraitInfoOrDefault pass).
					var playerBuildingInfos = globalBuildingInfos;

					var innerRadius = baseBuilder.Info.DefenseInnerRadius;
					var outerRadius = baseBuilder.Info.MaximumDefenseRadius;
					var midRadius = innerRadius > 0 ? (innerRadius + outerRadius) / 2 : outerRadius;

					// If defenseCenter (e.g. recorded attacker position) is farther than MaximumDefenseRadius
					// from the target base, candidates generated around it will all fail IsCloseEnoughToBase.
					// Fall back to baseCenter so placements stay within base adjacency range.
					//
					// This checks the TARGET base's construction yards, not every yard the bot owns. With the
					// latter, a threat recorded next to the main base kept every defense at the main base even
					// when the distribution had picked the exposed expansion - the base choice never bound.
					if (defenseCenter != baseCenter)
					{
						var outerRadiusSq = outerRadius * outerRadius;
						var anyNear = false;
						foreach (var cy in targetBase.ConstructionYards)
						{
							if (cy.IsDead || !cy.IsInWorld) continue;
							if ((cy.Location - defenseCenter).LengthSquared <= outerRadiusSq) { anyNear = true; break; }
						}

						if (!anyNear)
							defenseCenter = baseCenter;
					}

					var sameDefenseBuildings = playerBuildings
						.Where(a => a.Info.Name == actorType)
						.Select(a => a.Location).ToList();
					var allDefenseBuildings = playerBuildings
						.Where(a => baseBuilder.Info.DefenseTypes.Contains(a.Info.Name))
						.Select(a => a.Location).ToList();

					// Determine role-specific search area and sort order
					var role = GetDefenseRoleFromActor(actorInfo);
					if (role != DefenseRole.Default)
					{
						var roleHotspot = baseBuilder.GetBestDefenseHotspot(defenseCenter, role);
						if (roleHotspot.HasValue)
							targetCell = roleHotspot.Value;
					}

					IEnumerable<CPos> defenseCells;
					IEnumerable<CPos> sortedDefenseCells;
					var placementThreats = baseBuilder.GetDefensePlacementThreats(defenseCenter, role);
					long DefenseScore(CPos cell) => baseBuilder.ScoreDefensePlacement(cell, defenseCenter, targetCell, placementThreats);
					// Widen the search on every retry. The candidate list is truncated by score, so all
					// surviving cells face the same threat hotspot and sit close together; once one
					// defense stands there, the hard spacing filter rules out that whole cluster, and the
					// cells further out never made the list. Retrying with identical parameters therefore
					// reproduced the same miss — only the random building variant differed. Each attempt
					// now drops the spacing requirement by a cell and weighs proportionally more
					// candidates, so a retry actually explores somewhere new.
					var placementRelaxation = Math.Max(0, defensePlacementAttempt);
					var defMinSpacing = Math.Max(0,
						(role == DefenseRole.AADefense || role == DefenseRole.InfantryDefense
							? baseBuilder.Info.DefenseOuterMinSpacing
							: baseBuilder.Info.DefenseInnerMinSpacing) - placementRelaxation);
					var defMinSpacingSq = defMinSpacing * defMinSpacing;
					var defCandidateLimit = Math.Max(1,
						baseBuilder.Info.DefensePlacementCandidateLimit * (1 + placementRelaxation));
					long FormationScore(CPos cell, int preferredRadius)
					{
						var score = DefenseScore(cell);

						if (preferredRadius > 0)
							score -= Math.Abs((cell - defenseCenter).Length - preferredRadius) * 12L;

						if (allDefenseBuildings.Count == 0)
							return score;

						var nearestDefenseSq = allDefenseBuildings.Min(loc => (cell - loc).LengthSquared);
						var softSpacing = Math.Max(4, defMinSpacing + 2);
						var softSpacingSq = softSpacing * softSpacing;
						if (nearestDefenseSq < softSpacingSq)
							score -= (softSpacingSq - nearestDefenseSq) * 18L;
						else
							score += Math.Min(nearestDefenseSq, 100);

						return score;
					}

					IEnumerable<CPos> DefenseCandidateCells(CPos center, CPos target, int minRange, int maxRange, bool outerFirst = true)
					{
						minRange = Math.Max(0, minRange);
						maxRange = Math.Max(minRange, maxRange);
						var maxCandidates = Math.Max(64, defCandidateLimit * 8);
						var result = new List<CPos>(maxCandidates);
						var seen = new HashSet<CPos>();
						var dx = target.X - center.X;
						var dy = target.Y - center.Y;

						// Euclidean normalisation, not Chebyshev (max(|dx|,|dy|)). TryAdd rejects any cell
						// whose (Euclidean) distance from the centre exceeds maxRange, but the old divisor
						// placed the main candidate at Chebyshev distance `radius` — up to 1.41x that in
						// Euclidean terms for a diagonal target. The result was that for a diagonally
						// placed enemy every main-direction candidate in the outer ~30% of the radius band
						// was silently discarded, and since Radii() walks outer-to-inner and stops once
						// maxCandidates is full, the cells actually aimed at the threat often never made
						// the list at all — defenses fell back to the generic axis/diagonal ring.
						var denom = (int)Exts.ISqrt((long)dx * dx + (long)dy * dy);
						var px = dy == 0 ? 0 : -Math.Sign(dy);
						var py = dx == 0 ? 0 : Math.Sign(dx);

						void TryAdd(CPos cell)
						{
							if (result.Count >= maxCandidates || !world.Map.Contains(cell))
								return;

							var length = (cell - center).Length;
							if (length < minRange || length > maxRange)
								return;

							if (seen.Add(cell))
								result.Add(cell);
						}

						IEnumerable<int> Radii()
						{
							if (outerFirst)
								for (var r = maxRange; r >= minRange; r--)
									yield return r;
							else
								for (var r = minRange; r <= maxRange; r++)
									yield return r;
						}

						IEnumerable<int> LateralOffsets(int radius)
						{
							yield return 0;

							var maxOffset = Math.Max(4, radius / 2);
							for (var offset = 2; offset <= maxOffset; offset += 2)
							{
								yield return -offset;
								yield return offset;
							}
						}

						foreach (var radius in Radii())
						{
							if (denom > 0)
							{
								var main = new CPos(center.X + dx * radius / denom, center.Y + dy * radius / denom);
								foreach (var offset in LateralOffsets(radius))
									TryAdd(main + new CVec(px * offset, py * offset));
							}

							TryAdd(center + new CVec(radius, 0));
							TryAdd(center + new CVec(-radius, 0));
							TryAdd(center + new CVec(0, radius));
							TryAdd(center + new CVec(0, -radius));

							var diagonal = Math.Max(1, radius * 181 / 256);
							TryAdd(center + new CVec(diagonal, diagonal));
							TryAdd(center + new CVec(-diagonal, diagonal));
							TryAdd(center + new CVec(diagonal, -diagonal));
							TryAdd(center + new CVec(-diagonal, -diagonal));

							if (result.Count >= maxCandidates)
								break;
						}

						return result;
					}

					IEnumerable<CPos> LimitDefenseCandidates(IEnumerable<CPos> cells, Func<CPos, long> score, bool descending = true)
					{
						var limit = defCandidateLimit;
						var selected = new List<(CPos Cell, long Score)>(limit);

						foreach (var cell in cells)
						{
							var cellScore = score(cell);
							if (selected.Count < limit)
							{
								selected.Add((cell, cellScore));
								continue;
							}

							var replaceIndex = 0;
							var replaceScore = selected[0].Score;
							for (var i = 1; i < selected.Count; i++)
							{
								if (descending ? selected[i].Score < replaceScore : selected[i].Score > replaceScore)
								{
									replaceIndex = i;
									replaceScore = selected[i].Score;
								}
							}

							if (descending ? cellScore <= replaceScore : cellScore >= replaceScore)
								continue;

							selected[replaceIndex] = (cell, cellScore);
						}

						return descending
							? selected.OrderByDescending(c => c.Score).Select(c => c.Cell)
							: selected.OrderBy(c => c.Score).Select(c => c.Cell);
					}

					switch (role)
					{
						case DefenseRole.InfantryDefense:
						{
							// Mid-radius spread, sorted by spacing from existing same-type buildings
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								baseBuilder.Info.MinimumDefenseRadius, midRadius);
							sortedDefenseCells = sameDefenseBuildings.Count > 0
								? LimitDefenseCandidates(defenseCells, DefenseScore)
									.OrderByDescending(c => FormationScore(c, midRadius))
									.ThenByDescending(c => sameDefenseBuildings.Min(loc => (c - loc).LengthSquared))
								: LimitDefenseCandidates(defenseCells, DefenseScore)
									.OrderByDescending(c => FormationScore(c, midRadius));
							break;
						}

						case DefenseRole.ArmorDefense:
						{
							// Outer radius, sorted toward the most relevant attack direction.
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								innerRadius > 0 ? innerRadius : baseBuilder.Info.MinimumDefenseRadius, outerRadius);
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(c => FormationScore(c, outerRadius))
								.ThenBy(c => (c - targetCell).LengthSquared);
							break;
						}

						case DefenseRole.AADefense:
						{
							// Coverage of the base is one term in the score, not the sort key. It used to be
							// the primary key with FormationScore only breaking ties — and since two cells
							// almost never cover exactly the same number of buildings, the tie-break never
							// bound. AA therefore ignored the AADefense danger hotspot that was already
							// computed for it above and spread evenly around the base, including across the
							// side no aircraft had ever come from. Every other defense role sorts by
							// FormationScore, i.e. toward the threat; this one now does too, with coverage
							// able to decide between cells that face it equally well.
							var weaponRange = actorInfo.TraitInfos<ArmamentInfo>()
								.Select(a =>
								{
									if (!world.Map.Rules.Weapons.TryGetValue(a.Weapon.ToLowerInvariant(), out var w))
										return WDist.Zero;
									return w.Range;
								})
								.DefaultIfEmpty(WDist.Zero)
								.Max();
							var coverageRadiusCells = Math.Max(3, weaponRange.Length / 1024);
							var coverageRadiusCellsSq = coverageRadiusCells * coverageRadiusCells;

							// Only buildings worth an air strike count. Weighting every structure equally let
							// a cluster of walls and power plants outvote the refinery or the war factory.
							var protectedCaps = baseBuilder.Info.AAProtectedCapabilities;
							var protectable = playerBuildings
								.Where(b => protectedCaps.Count == 0 ||
									(b.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet
										.Overlaps(protectedCaps) ?? false))
								.Select(b => b.Location)
								.ToList();

							// Nothing tagged yet (very early game, or a mod that doesn't tag): fall back to
							// all buildings rather than scoring every candidate identically at zero.
							if (protectable.Count == 0)
								protectable = playerBuildings.Select(b => b.Location).ToList();

							// Track which of those are already covered by existing AA of this type.
							var coveredByExisting = new HashSet<CPos>();
							foreach (var existingAA in playerBuildings.Where(b => b.Info.Name == actorType))
								foreach (var bPos in protectable)
									if ((bPos - existingAA.Location).LengthSquared <= coverageRadiusCellsSq)
										coveredByExisting.Add(bPos);

							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								baseBuilder.Info.MinimumDefenseRadius, outerRadius);

							// Materialized once to avoid re-evaluating the LINQ per candidate below.
							var uncoveredPositions = protectable
								.Where(bPos => !coveredByExisting.Contains(bPos))
								.ToArray();

							var coverageWeight = (long)Math.Max(0, baseBuilder.Info.AACoverageWeight);
							long AACoverageScore(CPos cell)
							{
								if (coverageWeight == 0 || uncoveredPositions.Length == 0)
									return FormationScore(cell, outerRadius);

								var covered = 0;
								foreach (var bPos in uncoveredPositions)
									if ((bPos - cell).LengthSquared <= coverageRadiusCellsSq)
										covered++;

								return FormationScore(cell, outerRadius) + covered * coverageWeight;
							}

							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(AACoverageScore);
							break;
						}

						case DefenseRole.ArtilleryDefense:
						{
							// Inner-to-mid radius behind the front, Richtung Feind
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								baseBuilder.Info.MinimumDefenseRadius, midRadius);
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(c => FormationScore(c, midRadius))
								.ThenByDescending(c => (c - targetCell).LengthSquared);
							break;
						}

						case DefenseRole.GarrisonDefense:
						{
							// Same placement style as SpecialDefense (Obelisk) - outermost radius on the main approach
							// vector, since a bunker's job is to be the first thing the enemy walks into.
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								outerRadius - 2 > baseBuilder.Info.MinimumDefenseRadius ? outerRadius - 2 : baseBuilder.Info.MinimumDefenseRadius,
								outerRadius);
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(c => FormationScore(c, outerRadius))
								.ThenBy(c => (c - targetCell).LengthSquared);
							break;
						}

						case DefenseRole.SpecialDefense:
						{
							// Outermost radius directly on the main approach vector.
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								outerRadius - 2 > baseBuilder.Info.MinimumDefenseRadius ? outerRadius - 2 : baseBuilder.Info.MinimumDefenseRadius,
								outerRadius);
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(c => FormationScore(c, outerRadius))
								.ThenBy(c => (c - targetCell).LengthSquared);
							break;
						}

						default:
						{
							// Original inner/outer logic for unassigned defense types
							var useInnerLine = innerRadius > 0 && allDefenseBuildings.Count == 0;

							defenseCells = useInnerLine
								? DefenseCandidateCells(baseCenter, targetCell, baseBuilder.Info.MinimumDefenseRadius, innerRadius, false)
								: DefenseCandidateCells(defenseCenter, targetCell,
									innerRadius > 0 ? innerRadius : baseBuilder.Info.MinimumDefenseRadius, outerRadius);

							var preferredRadius = useInnerLine ? innerRadius : outerRadius;
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(c => FormationScore(c, preferredRadius))
								.ThenBy(c => useInnerLine ? (c - baseCenter).LengthSquared : (c - targetCell).LengthSquared);

							break;
						}
					}

					// A sealed flank is a veto, not a nudge. As a -80 score term it lost routinely against the
					// high-ground bonus (up to 90 per height level), the chokepoint anchor (160) and the
					// formation spacing terms, so defenses kept going up facing map edges and cliffs.
					// Run as a first pass so it can never starve placement: if every candidate faces a sealed
					// flank, the second pass drops the veto and takes the best-scoring cell anyway.
					var orderedDefenseCells = sortedDefenseCells.ToList();
					var vetoPasses = baseBuilder.Info.VetoSealedFlankDefenses ? 2 : 1;
					for (var pass = 0; pass < vetoPasses; pass++)
					{
						var vetoSealedFlanks = vetoPasses == 2 && pass == 0;
						foreach (var cell in orderedDefenseCells)
						{
							if (vetoSealedFlanks && baseBuilder.IsSealedFlankCell(cell, defenseCenter)) continue;
							if (!world.CanPlaceBuilding(cell, defVariantActorInfo, defVbi, null)) continue;
							if (!defVbi.IsCloseEnoughToBase(world, player, defVariantActorInfo, cell)) continue;
							if (IsTooCloseToValuableResources(cell, defVbi, baseBuilder.Info.DefenseResourceAvoidanceRadius)) continue;

							if (defMinSpacing > 0 && allDefenseBuildings.Count > 0
								&& allDefenseBuildings.Any(loc => (cell - loc).LengthSquared < defMinSpacingSq))
								continue;

							return (cell, defenseCenter, defVariant);
						}
					}

					// Do not fall back to a full vanilla annulus scan here: defense placement
					// can run for several bots at once, and the exhaustive CanPlaceBuilding
					// pass causes visible bot_tick spikes. Retry on the next build attempt
					// instead, where target/variant/candidate ordering may differ.
					return (null, defenseCenter, defVariant);
				}

				case BuildingType.Refinery:

					var requestRef = baseBuilder.RequestedRefineries.Count > 0 ? baseBuilder.RequestedRefineries.Keys.First() : null;
					var resourceBaseCenter = failCount > 0 ? baseCenter :
						(requestRef != null ? baseBuilder.RequestedRefineries[requestRef].ConyardLoc : (baseBuilder.ResourceConyardCenter ?? baseCenter));
					var requestedResourceLoc = requestRef != null
						? baseBuilder.RequestedRefineries[requestRef].ResourceLoc
						: (CPos?)null;

					// Try and place the refinery near a resource field
					if (resourceLayer != null)
					{
						// If we have a ResourceMapModule, only consider the resource types it considers valuable
						// Otherwise consider any resource type
						var nearbyResources = world.Map
							.FindTilesInAnnulus(resourceBaseCenter, baseBuilder.Info.MinBaseRadius, effectiveMaxRadius)
							.Where(c => baseBuilder.ResourceMapModule != null ?
							baseBuilder.ResourceMapModule.Info.ValuableResourceTypes.Contains(resourceLayer.GetResource(c).Type)
							: resourceLayer.GetResource(c).Type != null)
							.ToArray();

						// Filter out resource cells that harvesters can't actually reach (prevents building next to cliffs)
						if (baseBuilder.PathFinder != null && baseBuilder.HarvesterLocomotorsList.Length > 0)
						{
							nearbyResources = nearbyResources.Where(c =>
								baseBuilder.HarvesterLocomotorsList.All(l =>
									baseBuilder.PathFinder.PathMightExistForLocomotorBlockedByImmovable(l, resourceBaseCenter, c)))
								.ToArray();
						}

						// Remove resource cells that sit directly against cliffs (fewer than 3 passable
						// orthogonal neighbours). Cliff-locked fields — small patches surrounded by rocks —
						// score poorly here and are naturally deprioritised. Open fields in flat terrain
						// keep all four passable neighbours and remain in the pool.
						// This is O(n) per resource cell, zero path-finding cost.
						if (baseBuilder.HarvesterLocomotorsList.Length > 0)
							nearbyResources = nearbyResources.Where(c => CountPassableOrthogonalNeighbors(c) >= 3).ToArray();

						// Find the closest refinery we have if we have any when not failing to place for the first time
						var closestRefinery = failCount <= 0
							? baseBuilder.RefineryBuildings.Actors.Where(a => !a.IsDead)?.ClosestToIgnoringPath(world.Map.CenterOfCell(resourceBaseCenter))
							: null;

						var resourcesShouldCheck = SelectRefineryResourceCells(
							nearbyResources,
							resourceBaseCenter,
							closestRefinery?.Location,
							requestedResourceLoc);

						var existingRefineries = baseBuilder.RefineryBuildings.Actors
							.Where(a => !a.IsDead)
							.Select(a => a.Location)
							.ToList();
						RefineryCandidate? bestCandidate = null;

						foreach (var r in resourcesShouldCheck)
						{
							if (baseBuilder.ResourceMapModule != null)
							{
								var resourceIndice = baseBuilder.ResourceMapModule.FindClosestIndiceFromCPos(r);
								if (resourceIndice != null)
								{
									var pendingRefineries = baseBuilder.CountPendingRefineriesForIndice(resourceIndice);
									if (!baseBuilder.CanSupportAnotherRefinery(resourceIndice, pendingRefineries))
										continue;
								}
							}

							// Skip resource cells where too many refineries are already nearby
							if (baseBuilder.Info.MaxRefineriesPerCluster > 0 && existingRefineries.Count > 0)
							{
								var clusterRadiusSq = baseBuilder.Info.RefineryClusterRadius * baseBuilder.Info.RefineryClusterRadius;
								var nearbyRefCount = existingRefineries.Count(loc => (loc - r).LengthSquared <= clusterRadiusSq);
								if (nearbyRefCount >= baseBuilder.Info.MaxRefineriesPerCluster)
									continue;
							}

							var sampledResourceCells = GetFieldSampleCells(nearbyResources, r);
							var refineryCandidateLimit = layout == BaseBuildingLayout.BaseGrid ? 24 : 12;
							var candidateCells = GetRefineryCandidateCellsForField(actorInfo, bi, sampledResourceCells)
								.Take(refineryCandidateLimit)
								.ToArray();

							// The structural checks below (open neighbours, dock probe, HasOpenRefineryApproach,
							// PathMightExistForLocomotorBlockedByImmovable) reject cliff-LOCKED placements, but
							// they cannot see a field that IS reachable and only the long way round. On Forest
							// Fire the bot put refineries against the cliff below the blue tiberium, which sits
							// on the terrace above and is only reachable by driving out around the forest path,
							// so the harvesters gave up on it and drove down to the green field instead.
							// Straight-line distance said "adjacent"; the road said otherwise.
							//
							// Path-length A* was removed from here once before because it lagged out placement -
							// it was being run from every candidate cell, hundreds of searches per decision. The
							// road length is a property of the field, not of which base cell the refinery lands
							// on, so measuring it once per field (at most MaxResourceCellsToCheck of them) is the
							// same information for a fraction of the cost.
							var harvesterPathLength = HarvesterPathLengthToField(targetBase.Center, r);

							foreach (var loc in candidateCells)
							{
								if (!RespectsGeneralBuildingSpacing(loc, bi))
									continue;

								if (baseBuilder.PathFinder != null && baseBuilder.HarvesterLocomotorsList.Length > 0)
								{
									var canReachResource = baseBuilder.HarvesterLocomotorsList.All(l =>
										baseBuilder.PathFinder.PathMightExistForLocomotorBlockedByImmovable(l, loc, r));
									if (!canReachResource)
										continue;
								}

								// Reject placement cells wedged against cliffs.
								// Check the building origin AND one cell outside each dock edge so that a cliff
								// blocking the south or east side of a large refinery is also caught.
								if (baseBuilder.HarvesterLocomotorsList.Length > 0)
								{
									// Origin-cell neighbourhood (general openness).
									var openCount = 0;
									foreach (var dir in new[] { new CVec(0, -1), new CVec(0, 1), new CVec(-1, 0), new CVec(1, 0) })
									{
										var nb = loc + dir;
										if (baseBuilder.HarvesterLocomotorsList.All(l =>
											l.MovementCostForCell(nb) != PathGraph.MovementCostForUnreachableCell))
											openCount++;
									}

									if (openCount < 3)
										continue;

									// Dock-side openness: sample one cell beyond the east and south edges
									// (where the harvester actually enters) and require both to be passable.
									var eastProbe = new CPos(loc.X + bi.Dimensions.X, loc.Y + bi.Dimensions.Y / 2);
									var southProbe = new CPos(loc.X + bi.Dimensions.X / 2, loc.Y + bi.Dimensions.Y);
									var dockSideBlocked = false;
									foreach (var probe in new[] { eastProbe, southProbe })
									{
										if (!baseBuilder.HarvesterLocomotorsList.All(l =>
											l.MovementCostForCell(probe) != PathGraph.MovementCostForUnreachableCell))
										{
											dockSideBlocked = true;
											break;
										}
									}

									if (dockSideBlocked)
										continue;
								}

								// Also require a usable corridor on the field-facing side of the refinery.
								// This rejects spots that are technically reachable, but only via a long
								// loop around cliffs or very narrow side pinches.
								if (!HasOpenRefineryApproach(actorInfo, loc, bi.Dimensions, r))
									continue;

								var dockCells = GetRefineryDockCells(actorInfo, loc, bi.Dimensions);

								// Reject placements where a different resource cluster is clearly closer to the
								// dock than the intended field. At runtime the harvester picks the shortest path,
								// so a refinery placed next to a cliff with another field open on the dock side
								// will always send harvesters to that other field instead.
								if (baseBuilder.HarvesterLocomotorsList.Length > 0)
								{
									var passableDocks = dockCells.Where(IsPassableForHarvesters).ToList();
									if (passableDocks.Count > 0)
									{
										var sampledSet = new HashSet<CPos>(sampledResourceCells);
										var minIntendedDistSq = passableDocks.Min(d =>
											sampledResourceCells.Min(s => (d - s).LengthSquared));

										// If any other reachable resource cell is > 30% closer (dist² < intended * 0.49),
										// the harvester will consistently prefer it.
										var hasCloserField = false;
										foreach (var res in nearbyResources)
										{
											if (sampledSet.Contains(res))
												continue;
											if (!IsPassableForHarvesters(res))
												continue;
											var distSq = passableDocks.Min(d => (d - res).LengthSquared);
											if (distSq * 100 < minIntendedDistSq * 49)
											{
												hasCloserField = true;
												break;
											}
										}

										if (hasCloserField)
											continue;
									}
								}

								var score = ScoreRefineryCandidate(actorInfo, resourceBaseCenter, r, loc, existingRefineries, sampledResourceCells, harvesterPathLength)
									+ ScoreBaseGridAlignment(loc, bi, targetBase.GridAnchor);
								if (bestCandidate == null || score < bestCandidate.Value.Score)
									bestCandidate = new RefineryCandidate((loc, resourceBaseCenter, 0), score);
							}
						}

						if (bestCandidate != null)
						{
							if (baseBuilder.RequestedRefineries.Count > 0)
								baseBuilder.RequestedRefineries.Remove(requestRef);
							return bestCandidate.Value.Placement;
						}

						// Small relaxed second pass with a much tighter candidate budget.
						foreach (var r in resourcesShouldCheck)
						{
							var sampledRelaxed = GetFieldSampleCells(nearbyResources, r);
							var relaxedCandidateLimit = layout == BaseBuildingLayout.BaseGrid ? 12 : 6;
							var candidatesRelaxed = GetRefineryCandidateCellsForField(actorInfo, bi, sampledRelaxed)
								.Take(relaxedCandidateLimit)
								.ToArray();

							foreach (var loc in candidatesRelaxed)
							{
								if (!RespectsGeneralBuildingSpacing(loc, bi))
									continue;

								if (baseBuilder.PathFinder != null && baseBuilder.HarvesterLocomotorsList.Length > 0)
								{
									var canReach = baseBuilder.HarvesterLocomotorsList.All(l =>
										baseBuilder.PathFinder.PathMightExistForLocomotorBlockedByImmovable(l, loc, r));
									if (!canReach)
										continue;
								}

								if (baseBuilder.HarvesterLocomotorsList.Length > 0)
								{
									var openCount = 0;
									foreach (var dir in new[] { new CVec(0, -1), new CVec(0, 1), new CVec(-1, 0), new CVec(1, 0) })
									{
										var nb = loc + dir;
										if (baseBuilder.HarvesterLocomotorsList.All(l =>
											l.MovementCostForCell(nb) != PathGraph.MovementCostForUnreachableCell))
											openCount++;
									}

									if (openCount < 2)
										continue;
								}

								if (!HasOpenRefineryApproach(actorInfo, loc, bi.Dimensions, r))
									continue;

								var score = ScoreRefineryCandidate(actorInfo, resourceBaseCenter, r, loc, existingRefineries, sampledRelaxed, null)
									+ ScoreBaseGridAlignment(loc, bi, targetBase.GridAnchor);
								if (bestCandidate == null || score < bestCandidate.Value.Score)
									bestCandidate = new RefineryCandidate((loc, resourceBaseCenter, 0), score);
							}
						}

						if (bestCandidate != null)
						{
							if (baseBuilder.RequestedRefineries.Count > 0)
								baseBuilder.RequestedRefineries.Remove(requestRef);
							return bestCandidate.Value.Placement;
						}
					}

					if (baseBuilder.RequestedRefineries.Count > 0)
						baseBuilder.RequestedRefineries.Remove(requestRef);

					// Fallback placement puts the refinery on the base grid aimed at a field rather than
					// beside one. Aimed at a real field that is still worth doing at any refinery count:
					// the grid position closest to the tiberium shortens every haul, and a field being
					// far away is a reason to build toward it, not a reason to build nothing.
					//
					// What is not worth doing is the degenerate case below, where no field can be found
					// at all and the target collapses to the base centre. That is what produced
					// refineries parked in the middle of the base with tiberium nowhere near them. It
					// only pays off for the very first refinery, where the alternative is no income.
					var existingRefineryCount = AIUtils.CountActorByCommonName(baseBuilder.RefineryBuildings);
					var resourceFallbackRadius = Math.Max(effectiveMaxRadius, baseBuilder.Info.SellRefineryNoResourceDistance * 2);
					var fallbackTarget = requestedResourceLoc
						?? FindNearestReachableResource(resourceBaseCenter, resourceFallbackRadius);

					if (fallbackTarget.HasValue)
						return FindPos(baseCenter, fallbackTarget.Value, targetBase.GridAnchor, baseBuilder.Info.MinBaseRadius, effectiveMaxRadius);

					if (existingRefineryCount < 1)
						return FindPos(baseCenter, baseCenter, targetBase.GridAnchor, baseBuilder.Info.MinBaseRadius, effectiveMaxRadius);

					return (null, null, 0);

				case BuildingType.Building:
				{
					if (layout == BaseBuildingLayout.Coverage)
					{
						// Coverage radius: the aura range for aura buildings, otherwise the range that
						// actually makes the building worth spreading out — detection first, then plain
						// vision. Without the latter two a radar (DetectCloaked and RevealsShroud at 15
						// cells, no ProximityExternalCondition) would have been spaced as if it covered 8,
						// and the bot would have clustered them into overlapping circles.
						var proximityInfo = actorInfo.TraitInfoOrDefault<ProximityExternalConditionInfo>();
						var detectionRange = actorInfo.TraitInfos<DetectCloakedInfo>()
							.Select(d => d.Range)
							.DefaultIfEmpty(WDist.Zero)
							.Max();
						var visionRange = actorInfo.TraitInfos<RevealsShroudInfo>()
							.Select(r => r.Range)
							.DefaultIfEmpty(WDist.Zero)
							.Max();

						var coverageRadius = proximityInfo != null
							? Math.Max(1, proximityInfo.Range.Length / 1024)
							: detectionRange > WDist.Zero
								? Math.Max(1, detectionRange.Length / 1024)
								: visionRange > WDist.Zero
									? Math.Max(1, visionRange.Length / 1024)
									: 8;
						var coverageRadiusSq = coverageRadius * coverageRadius;

						var baseBuildingPositions = playerBuildings
							.Select(b => b.Location)
							.ToArray();

						// Track which positions are already covered by existing instances of this type
						var coveredByExisting = new HashSet<CPos>();
						foreach (var existing in playerBuildings.Where(b => b.Info.Name == actorType))
							foreach (var bPos in baseBuildingPositions)
								if ((bPos - existing.Location).LengthSquared <= coverageRadiusSq)
									coveredByExisting.Add(bPos);

						// Precompute uncovered positions once instead of re-filtering per candidate cell.
						var uncoveredPositions = baseBuildingPositions
							.Where(bPos => !coveredByExisting.Contains(bPos))
							.ToArray();

						if (uncoveredPositions.Length == 0)
							return (null, null, 0);

						var maxRadius = distanceToBaseIsImportant
							? effectiveMaxRadius
							: world.Map.Grid.MaximumTileSearchRange;

						// FindTilesInAnnulus for radius 25 yields ~2000 cells; checking all of them
						// with CanPlaceBuilding + IsCloseEnoughToBase + O(buildings) count is extremely
						// expensive when called across multiple bots on the same tick.
						// FindTilesInAnnulus is lazy — Take() stops the enumeration early.
						var covLimit = Math.Max(48, baseBuilder.Info.DefensePlacementCandidateLimit * 4);
						var coverageCandidates = world.Map.FindTilesInAnnulus(effectiveCenter,
							baseBuilder.Info.MinBaseRadius, maxRadius)
							.Take(covLimit);

						CPos? bestCell = null;
						var bestScore = -1;
						foreach (var cell in coverageCandidates)
						{
							if (!world.CanPlaceBuilding(cell, actorInfo, bi, null)) continue;
							if (!bi.IsCloseEnoughToBase(world, player, actorInfo, cell)) continue;
							if (!RespectsGeneralBuildingSpacing(cell, bi)) continue;
							var score = uncoveredPositions.Count(bPos => (bPos - cell).LengthSquared <= coverageRadiusSq);
							if (score > bestScore)
							{
								bestScore = score;
								bestCell = cell;
							}
						}

						if (bestCell.HasValue && bestScore > 0)
							return (bestCell, effectiveCenter, 0);

						return (null, null, 0);
					}

					return FindPos(effectiveCenter, effectiveCenter, targetBase.GridAnchor, baseBuilder.Info.MinBaseRadius,
						distanceToBaseIsImportant ? effectiveMaxRadius : world.Map.Grid.MaximumTileSearchRange);
				}
			}

			// Can't find a build location
			return (null, null, 0);
		}

		bool IsValuableResourceCell(CPos cell)
		{
			if (resourceLayer == null || !world.Map.Contains(cell))
				return false;

			var resourceType = resourceLayer.GetResource(cell).Type;
			return resourceType != null && (baseBuilder.ResourceMapModule == null
				|| baseBuilder.ResourceMapModule.Info.ValuableResourceTypes.Contains(resourceType));
		}

		bool HasNearbyValuableResource(CPos cell, int radius)
		{
			if (radius <= 0)
				return IsValuableResourceCell(cell);

			return world.Map.FindTilesInAnnulus(cell, 0, radius).Any(IsValuableResourceCell);
		}

		bool HasOwnActorAt(CPos cell, FrozenSet<string> actorTypes)
		{
			return world.ActorMap.GetActorsAt(cell)
				.Any(a => !a.IsDead && a.Owner == player && actorTypes.Contains(a.Info.Name));
		}

		bool HasAnyBuildingAt(CPos cell)
		{
			return world.ActorMap.GetActorsAt(cell)
				.Any(a => !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>());
		}

		bool HasBlockingBuildingForChokepointSeal(CPos cell)
		{
			return world.ActorMap.GetActorsAt(cell)
				.Any(a => !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>()
					&& (a.Owner != player
						|| (!baseBuilder.Info.WallTypes.Contains(a.Info.Name)
							&& !baseBuilder.Info.GateTypes.Contains(a.Info.Name))));
		}

		bool HasBlockingGateFootprintForChokepointSeal(CPos cell)
		{
			return world.ActorMap.GetActorsAt(cell)
				.Any(a => !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>()
					&& (a.Owner != player || !baseBuilder.Info.GateTypes.Contains(a.Info.Name)));
		}

		bool TryGetCorePerimeter(out CPos baseCenter, out int minX, out int maxX, out int minY, out int maxY)
		{
			var conyard = baseBuilder.ConstructionYardBuildings.Actors
				.Where(a => !a.IsDead && a.IsInWorld)
				.OrderBy(a => a.ActorID)
				.FirstOrDefault();

			baseCenter = conyard?.Location ?? baseBuilder.GetRandomBaseCenter();
			var center = baseCenter;
			var effectiveMaxRadius = baseBuilder.GetEffectiveMaxBaseRadius(playerBuildings.Length);
			var effectiveMaxRadiusSq = effectiveMaxRadius * effectiveMaxRadius;

			var coreBuildings = playerBuildings
				.Where(a => !a.IsDead && a.IsInWorld
					&& !baseBuilder.Info.WallTypes.Contains(a.Info.Name)
					&& !baseBuilder.Info.GateTypes.Contains(a.Info.Name)
					&& !baseBuilder.Info.RefineryTypes.Contains(a.Info.Name)
					&& (a.Location - center).LengthSquared <= effectiveMaxRadiusSq)
				.ToList();

			if (coreBuildings.Count < baseBuilder.GetActiveBasePerimeterWallMinimumStructures())
			{
				minX = maxX = minY = maxY = 0;
				return false;
			}

			var first = true;
			minX = maxX = minY = maxY = 0;
			foreach (var actor in coreBuildings)
			{
				var bi = actor.Info.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;

				var actorMinX = actor.Location.X;
				var actorMaxX = actor.Location.X + bi.Dimensions.X - 1;
				var actorMinY = actor.Location.Y;
				var actorMaxY = actor.Location.Y + bi.Dimensions.Y - 1;

				if (first)
				{
					minX = actorMinX;
					maxX = actorMaxX;
					minY = actorMinY;
					maxY = actorMaxY;
					first = false;
				}
				else
				{
					minX = Math.Min(minX, actorMinX);
					maxX = Math.Max(maxX, actorMaxX);
					minY = Math.Min(minY, actorMinY);
					maxY = Math.Max(maxY, actorMaxY);
				}
			}

			if (first)
				return false;

			var padding = Math.Max(2, baseBuilder.Info.BasePerimeterWallPadding);
			minX -= padding;
			maxX += padding;
			minY -= padding;
			maxY += padding;

			return true;
		}

		static IEnumerable<CPos> PerimeterCells(int minX, int maxX, int minY, int maxY)
		{
			for (var x = minX; x <= maxX; x++)
			{
				yield return new CPos(x, minY);
				yield return new CPos(x, maxY);
			}

			for (var y = minY + 1; y < maxY; y++)
			{
				yield return new CPos(minX, y);
				yield return new CPos(maxX, y);
			}
		}

		int CountExistingPerimeterWalls(int minX, int maxX, int minY, int maxY)
		{
			return PerimeterCells(minX, maxX, minY, maxY)
				.Count(c => HasOwnActorAt(c, baseBuilder.Info.WallTypes));
		}

		List<CPos> GetGateTargetCells(CPos baseCenter)
		{
			var targets = new List<CPos>();
			if (resourceLayer != null)
			{
				var resourceTargets = world.Map
					.FindTilesInAnnulus(baseCenter, baseBuilder.Info.MinBaseRadius, baseBuilder.GetEffectiveMaxBaseRadius(playerBuildings.Length) + 16)
					.Where(IsValuableResourceCell)
					.OrderBy(c => (c - baseCenter).LengthSquared)
					.ToList();

				if (resourceTargets.Count > 0)
					targets.Add(resourceTargets[0]);
			}

			var enemyBuilding = world.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy)
				.OrderBy(a => (a.Location - baseCenter).LengthSquared)
				.FirstOrDefault();

			if (enemyBuilding != null)
				targets.Add(enemyBuilding.Location);

			return targets;
		}

		// The center run of a corridor reserved for the gate (sized to the orientation-matched gate type). Null if none fits.
		List<CPos> ChokepointGateFootprint(CNSealableCorridor corridor)
		{
			foreach (var gateName in baseBuilder.Info.GateTypes)
			{
				var gateInfo = world.Map.Rules.Actors[gateName];
				var gbi = gateInfo.TraitInfoOrDefault<BuildingInfo>();
				if (gbi == null)
					continue;

				var horizontal = gbi.Dimensions.X > gbi.Dimensions.Y;
				if (horizontal != corridor.WallRunsHorizontal)
					continue;

				var span = corridor.WallRunsHorizontal ? gbi.Dimensions.X : gbi.Dimensions.Y;
				if (corridor.Cells.Length < span)
					return null;

				var axis = corridor.WallRunsHorizontal ? new CVec(1, 0) : new CVec(0, 1);
				var start = corridor.Center - axis * (span / 2);
				var foot = new List<CPos>(span);
				for (var i = 0; i < span; i++)
					foot.Add(start + axis * i);

				return foot.All(corridor.Cells.Contains) ? foot : null;
			}

			return null;
		}

		bool ChokepointCorridorIsWorthSealing(CNSealableCorridor corridor, ActorInfo wallActorInfo, BuildingInfo wallBuildingInfo)
		{
			var gateFootprint = ChokepointGateFootprint(corridor);
			if (gateFootprint == null)
				return false;

			var resourceAvoidanceRadius = Math.Max(2, baseBuilder.Info.BasePerimeterResourceAvoidanceRadius);
			if (corridor.Cells.Any(c => HasNearbyValuableResource(c, resourceAvoidanceRadius)))
				return false;

			if (gateFootprint.Any(HasBlockingGateFootprintForChokepointSeal))
				return false;

			var wallCells = corridor.Cells.Where(c => !gateFootprint.Contains(c)).ToArray();
			if (wallCells.Length == 0)
				return false;

			foreach (var cell in wallCells)
			{
				if (!world.Map.Contains(cell) || HasBlockingBuildingForChokepointSeal(cell))
					return false;

				// Existing own seal pieces are fine; they let the bot finish a partially built line.
				if (HasOwnActorAt(cell, baseBuilder.Info.WallTypes) || HasOwnActorAt(cell, baseBuilder.Info.GateTypes))
					continue;

				if (!world.CanPlaceBuilding(cell, wallActorInfo, wallBuildingInfo, null))
					return false;
			}

			return true;
		}

		// Lay a wall line across a narrow map chokepoint, reserving the center run for a gate.
		(CPos? Location, CPos? BaseCenter, int Variant) ChooseChokepointWallLocation(ActorInfo actorInfo)
		{
			if (!baseBuilder.ShouldSealChokepoints() || baseBuilder.TacticalMapModule == null)
				return (null, null, 0);

			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return (null, null, 0);

			var baseCenter = baseBuilder.PrimaryBase.Center;
			foreach (var corridor in baseBuilder.TacticalMapModule.GetSealableCorridors(baseCenter))
			{
				var gateFootprint = ChokepointGateFootprint(corridor);
				if (gateFootprint == null || !ChokepointCorridorIsWorthSealing(corridor, actorInfo, bi))
					continue;

				foreach (var cell in corridor.Cells
					.Where(c => !gateFootprint.Contains(c)
						&& world.Map.Contains(c)
						&& !HasOwnActorAt(c, baseBuilder.Info.WallTypes) && !HasOwnActorAt(c, baseBuilder.Info.GateTypes)
						&& !HasAnyBuildingAt(c)
						&& world.CanPlaceBuilding(c, actorInfo, bi, null)
						&& bi.IsCloseEnoughToBase(world, player, actorInfo, c))
					.OrderBy(c => (c - corridor.Center).LengthSquared))
					return (cell, baseCenter, 0);
			}

			return (null, null, 0);
		}

		// Place a single gate at the center of a sealed chokepoint, matching the wall line's orientation.
		(CPos? Location, CPos? BaseCenter, int Variant) ChooseChokepointGateLocation(ActorInfo actorInfo)
		{
			if (!baseBuilder.ShouldSealChokepoints() || baseBuilder.TacticalMapModule == null)
				return (null, null, 0);

			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return (null, null, 0);

			var wallActorInfo = world.Map.Rules.Actors[baseBuilder.Info.WallTypes.First()];
			var wallBuildingInfo = wallActorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (wallBuildingInfo == null)
				return (null, null, 0);

			var horizontal = bi.Dimensions.X > bi.Dimensions.Y;

			// A stable center: with a random construction yard the bot switched which chokepoint it
			// was sealing between calls and never finished a line.
			var baseCenter = baseBuilder.PrimaryBase.Center;
			foreach (var corridor in baseBuilder.TacticalMapModule.GetSealableCorridors(baseCenter))
			{
				// The gate orientation (3x1 vs 1x3) must match the wall line direction.
				if (corridor.WallRunsHorizontal != horizontal)
					continue;

				var footprint = ChokepointGateFootprint(corridor);
				if (footprint == null)
					continue;

				if (!ChokepointCorridorIsWorthSealing(corridor, wallActorInfo, wallBuildingInfo))
					continue;

				var topLeft = footprint[0];
				if (footprint.Any(c => HasAnyBuildingAt(c)))
					continue;

				if (!world.CanPlaceBuilding(topLeft, actorInfo, bi, null)
					|| !bi.IsCloseEnoughToBase(world, player, actorInfo, topLeft))
					continue;

				return (topLeft, baseCenter, 0);
			}

			return (null, null, 0);
		}

		// Find a free adjacent cell around a ProtectedByWalls building, then fall back to a core perimeter.
		(CPos? Location, CPos? BaseCenter, int Variant) ChooseWallLocation(ActorInfo actorInfo)
		{
			var (chokeLoc, chokeBase, chokeVar) = ChooseChokepointWallLocation(actorInfo);
			if (chokeLoc != null)
				return (chokeLoc, chokeBase, chokeVar);

			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();

			if (bi != null && baseBuilder.Info.ProtectedByWalls.Count > 0)
			{
				var protectedBuildings = world.ActorsWithTrait<Building>()
					.Where(a => !a.Actor.Disposed && a.Actor.Owner == player
						&& baseBuilder.Info.ProtectedByWalls.Contains(a.Actor.Info.Name))
					.ToList();

				foreach (var target in protectedBuildings)
				{
					var topLeft = target.Actor.Location;
					var dim = target.Trait.Info.Dimensions;

					var candidates = new List<CPos>();

					for (var x = -1; x <= dim.X; x++)
					{
						candidates.Add(topLeft + new CVec(x, -1));
						candidates.Add(topLeft + new CVec(x, dim.Y));
					}

					for (var y = 0; y < dim.Y; y++)
					{
						candidates.Add(topLeft + new CVec(-1, y));
						candidates.Add(topLeft + new CVec(dim.X, y));
					}

					var valid = candidates
						.Where(c => world.Map.Contains(c)
							&& !world.ActorMap.GetActorsAt(c).Any(a => a.Info.HasTraitInfo<BuildingInfo>())
							&& world.CanPlaceBuilding(c, actorInfo, bi, null))
						.ToList();

					if (valid.Count == 0) continue;
					return (valid.Random(world.LocalRandom), topLeft, 0);
				}
			}

			if (bi != null && baseBuilder.ShouldBuildBasePerimeterWalls() &&
				TryGetCorePerimeter(out var baseCenter, out var minX, out var maxX, out var minY, out var maxY))
			{
				var lineBuildRange = actorInfo.TraitInfoOrDefault<LineBuildInfo>()?.Range ?? 8;
				var existingWalls = new HashSet<CPos>(PerimeterCells(minX, maxX, minY, maxY)
					.Where(c => HasOwnActorAt(c, baseBuilder.Info.WallTypes) || HasOwnActorAt(c, baseBuilder.Info.GateTypes)));

				int WallScore(CPos cell)
				{
					var score = 0;
					if (existingWalls.Contains(cell + new CVec(1, 0))) score -= 80;
					if (existingWalls.Contains(cell + new CVec(-1, 0))) score -= 80;
					if (existingWalls.Contains(cell + new CVec(0, 1))) score -= 80;
					if (existingWalls.Contains(cell + new CVec(0, -1))) score -= 80;

					var nearestInline = existingWalls
						.Where(w => w.X == cell.X || w.Y == cell.Y)
						.Select(w => Math.Abs(w.X - cell.X) + Math.Abs(w.Y - cell.Y))
						.Where(d => d > 0)
						.DefaultIfEmpty(lineBuildRange + 1)
						.Min();

					if (nearestInline <= lineBuildRange)
						score -= 40 - nearestInline;

					var isCorner = (cell.X == minX || cell.X == maxX) && (cell.Y == minY || cell.Y == maxY);
					if (isCorner && existingWalls.Count == 0)
						score -= 30;

					score += Math.Abs(cell.X - baseCenter.X) + Math.Abs(cell.Y - baseCenter.Y);
					return score;
				}

				var resourceAvoidanceRadius = Math.Max(0, baseBuilder.Info.BasePerimeterResourceAvoidanceRadius);
				foreach (var cell in PerimeterCells(minX, maxX, minY, maxY)
					.Where(c =>
						world.Map.Contains(c) &&
						!existingWalls.Contains(c) &&
						!HasNearbyValuableResource(c, resourceAvoidanceRadius) &&
						!HasAnyBuildingAt(c) &&
						world.CanPlaceBuilding(c, actorInfo, bi, null) &&
						bi.IsCloseEnoughToBase(world, player, actorInfo, c))
					.OrderBy(WallScore))
					return (cell, baseCenter, 0);
			}

			return (null, null, 0);
		}

		(CPos? Location, CPos? BaseCenter, int Variant) ChooseGateLocation(ActorInfo actorInfo)
		{
			var (chokeLoc, chokeBase, chokeVar) = ChooseChokepointGateLocation(actorInfo);
			if (chokeLoc != null)
				return (chokeLoc, chokeBase, chokeVar);

			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null || !baseBuilder.ShouldBuildBasePerimeterWalls())
				return (null, null, 0);

			if (!TryGetCorePerimeter(out var baseCenter, out var minX, out var maxX, out var minY, out var maxY))
				return (null, null, 0);

			if (CountExistingPerimeterWalls(minX, maxX, minY, maxY) < baseBuilder.Info.BasePerimeterGateWallThreshold)
				return (null, null, 0);

			var targets = GetGateTargetCells(baseCenter);
			var horizontal = bi.Dimensions.X > bi.Dimensions.Y;
			var candidates = new List<CPos>();

			if (horizontal)
			{
				for (var x = minX + 1; x <= maxX - bi.Dimensions.X; x++)
				{
					candidates.Add(new CPos(x, minY));
					candidates.Add(new CPos(x, maxY));
				}
			}
			else
			{
				for (var y = minY + 1; y <= maxY - bi.Dimensions.Y; y++)
				{
					candidates.Add(new CPos(minX, y));
					candidates.Add(new CPos(maxX, y));
				}
			}

			bool CanReplaceWallRun(CPos topLeft)
			{
				foreach (var cell in bi.Tiles(topLeft))
					if (!HasOwnActorAt(cell, baseBuilder.Info.WallTypes))
						return false;

				if (!world.CanPlaceBuilding(topLeft, actorInfo, bi, null))
					return false;

				if (!bi.IsCloseEnoughToBase(world, player, actorInfo, topLeft))
					return false;

				var nearbyGate = world.Map.FindTilesInAnnulus(topLeft, 0, 6)
					.Any(c => HasOwnActorAt(c, baseBuilder.Info.GateTypes));

				return !nearbyGate;
			}

			int GateScore(CPos topLeft)
			{
				var center = new CPos(topLeft.X + bi.Dimensions.X / 2, topLeft.Y + bi.Dimensions.Y / 2);
				var sideCenterDistance = horizontal
					? Math.Abs(center.X - (minX + maxX) / 2)
					: Math.Abs(center.Y - (minY + maxY) / 2);

				var targetDistance = targets.Count == 0
					? 0
					: targets.Min(t => (t - center).LengthSquared);

				return targetDistance + sideCenterDistance * 8;
			}

			foreach (var cell in candidates
				.Where(CanReplaceWallRun)
				.OrderBy(GateScore))
				return (cell, baseCenter, 0);

			return (null, null, 0);
		}
	}
}
