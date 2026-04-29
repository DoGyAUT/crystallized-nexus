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
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	sealed class CNBaseBuilderQueueManager
	{
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
			minimumExcessPower = baseBuilder.Info.MinimumExcessPower;
			if (baseBuilder.Info.NavalProductionTypes.Count == 0)
				waterState = WaterCheck.DontCheck;
		}

		public void Tick(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			// If we can't place any structures, give a nudge to BaseExpansionModules and hope it gets fixed.
			if (failCount >= baseBuilder.Info.MaximumFailedPlacementAttempts)
			{
				if (baseBuilder.BaseExpansionModules != null && baseCenterKeepsFailing != null)
				{
					if (!baseBuilder.Info.DefenseTypes.Contains(lastFailedBuilding))
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
				}

				// No BaseExpansionModules exist. Only bother resetting failCount when either
				// a) the number of buildings has decreased since last failure M ticks ago,
				// or b) number of BaseProviders (construction yard or similar) has increased since then.
				// Otherwise reset failRetryTicks instead to wait again.
				else if (baseBuilder.BaseExpansionModules == null && --failRetryTicks <= 0)
				{
					var currentBuildings = baseBuilder.GetCachedPlayerBuildings().Count;
					var baseProviders = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);

					if (currentBuildings < cachedBuildings || baseProviders > cachedBases)
						failCount = 0;
					else
						failRetryTicks = baseBuilder.Info.StructureProductionResumeDelay;
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
			minimumExcessPower =
				(baseBuilder.Info.MinimumExcessPower + excessPowerBonus)
					.Clamp(baseBuilder.Info.MinimumExcessPower, baseBuilder.Info.MaximumExcessPower);

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

		List<CPos> GetRefineryDockCells(ActorInfo actorInfo, CPos refineryLoc, CVec dimensions)
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

		Actor GetPathingHarvester()
		{
			return world.ActorsHavingTrait<Harvester>()
				.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.TraitOrDefault<Mobile>() != null)
				.FirstOrDefault();
		}

		int? FindShortestHarvesterPathLength(Actor harvester, IReadOnlyList<CPos> dockCells, IReadOnlyList<CPos> sampledResourceCells)
		{
			if (harvester == null || dockCells == null || sampledResourceCells == null || sampledResourceCells.Count == 0)
				return null;

			var mobile = harvester.TraitOrDefault<Mobile>();
			if (mobile == null)
				return null;

			int? best = null;
			foreach (var dock in dockCells)
			{
				if (!IsPassableForHarvesters(dock))
					continue;

				var path = mobile.PathFinder.FindPathToTargetCells(harvester, dock, sampledResourceCells, BlockedByActor.None, laneBias: false);
				if (path == null || path == PathFinder.NoPath || path.Count == 0)
					continue;

				if (best == null || path.Count < best.Value)
					best = path.Count;
			}

			return best;
		}



		bool HasAcceptableHarvesterPathQuality(CPos refineryLoc, IReadOnlyList<CPos> sampledResourceCells, int? harvesterPathLength)
		{
			if (harvesterPathLength == null || sampledResourceCells == null || sampledResourceCells.Count == 0)
				return true;

			var directDistance = sampledResourceCells.Min(sample => (refineryLoc - sample).Length);

			// Ratio-based cap: allow at most ~33% detour plus a small constant.
			// The old flat +6 was too lenient for mid-range fields — a 10-cell field allowed
			// a 16-cell path (60% detour), which is enough for a harvester to prefer another field.
			var maxReasonablePath = Math.Max(6, directDistance + Math.Max(3, directDistance / 3));
			return harvesterPathLength.Value <= maxReasonablePath;
		}

		CPos[] GetFieldSampleCells(IEnumerable<CPos> nearbyResources, CPos resourceLoc, int maxSamples = 6)
		{
			var fieldCells = nearbyResources
				.OrderBy(c => (c - resourceLoc).LengthSquared)
				.Take(Math.Max(maxSamples, 4))
				.ToArray();

			if (fieldCells.Length == 0)
				return [resourceLoc];

			return fieldCells;
		}

		IEnumerable<CPos> GetRefineryCandidateCellsForField(CPos baseLoc, ActorInfo actorInfo, BuildingInfo bi, IReadOnlyList<CPos> fieldCells)
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

		int ScoreRefineryCandidate(ActorInfo actorInfo, CPos baseLoc, CPos resourceLoc, CPos refineryLoc, List<CPos> existingRefineries, IReadOnlyList<CPos> sampledResourceCells, int? harvesterPathLength)
		{
			var score = 0;
			var dockCells = GetRefineryDockCells(actorInfo, refineryLoc, actorInfo.TraitInfoOrDefault<BuildingInfo>()?.Dimensions ?? CVec.Zero);

			// Primary goal: actual harvester route quality from the dock to the field.
			if (harvesterPathLength != null)
				score += harvesterPathLength.Value * harvesterPathLength.Value * 6;
			else if (sampledResourceCells != null && sampledResourceCells.Count > 0)
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
					// Walls use LineBuild order and ProtectedByWalls placement (also handles wall ring)
					orderString = "LineBuild";
					(location, baseCenterKeepsFailing, actorVariant) = ChooseWallLocation(actorInfo);
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
						// Defense placement uses a bounded sampled search. A miss can simply
						// mean this phase did not include a valid cell, not that the base is
						// truly stuck. Retry later with a different phase, then fall back to
						// normal base placement so completed defenses cannot block the queue.
						defensePlacementAttempt++;
						if (defensePlacementAttempt < baseBuilder.Info.MaximumFailedPlacementAttempts)
							return true;

						(location, baseCenterKeepsFailing, actorVariant) =
							ChooseBuildLocation(currentBuilding.Item, true, BuildingType.Building);

						if (location == null)
							defensePlacementAttempt = 0;
					}
				}

				if (location == null)
				{
					// If we just reached the maximum fail count, cache the number of current structures
					if (++failCount >= baseBuilder.Info.MaximumFailedPlacementAttempts)
					{
						AIUtils.BotDebug($"{player} has nowhere to place {currentBuilding.Item}");
						bot.QueueOrder(Order.CancelProduction(queue.Actor, currentBuilding.Item, 1));
						lastFailedBuilding = currentBuilding.Item;
						if (baseBuilder.BaseExpansionModules == null)
						{
							cachedBuildings = baseBuilder.GetCachedPlayerBuildings().Count;
							cachedBases = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);
						}
					}
				}
				else
				{
					failCount = 0;
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

		ActorInfo GetProducibleBuilding(FrozenSet<string> actors, IEnumerable<ActorInfo> buildables, Func<ActorInfo, int> orderBy = null)
		{
			var available = buildables.Where(actor =>
			{
				// Are we able to build this?
				if (!actors.Contains(actor.Name))
					return false;

				if (!baseBuilder.Info.BuildingLimits.TryGetValue(actor.Name, out var limit))
					return true;

				return CountExistingAndQueuedBuilding(actor.Name) < limit;
			});

			if (orderBy != null)
				return available.MaxByOrDefault(orderBy);

			return available.RandomOrDefault(world.LocalRandom);
		}

		bool HasSufficientPowerForActor(ActorInfo actorInfo)
		{
			return playerPower == null || actorInfo.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault)
				.Sum(p => p.Amount) + playerPower.ExcessPower >= baseBuilder.Info.MinimumExcessPower;
		}

		ActorInfo ChooseBuildingToBuild(ProductionQueue queue)
		{
			var buildableThings = queue.BuildableItems().ToList();

			// This gets used quite a bit, so let's cache it here
			var power = GetProducibleBuilding(baseBuilder.Info.PowerTypes, buildableThings,
				a => a.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount));

			// First priority is to get out of a low power situation
			if (playerPower != null && playerPower.ExcessPower < minimumExcessPower &&
				power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount) > 0)
			{
				AIUtils.BotDebug("{0} decided to build {1}: Priority override (low power)", queue.Actor.Owner, power.Name);
				return power;
			}

			// Next is to build up a strong economy
			if (baseBuilder.ShouldExpandEconomy())
			{
				var refinery = GetProducibleBuilding(baseBuilder.Info.RefineryTypes, buildableThings);
				if (refinery != null && HasSufficientPowerForActor(refinery))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (refinery)", queue.Actor.Owner, refinery.Name);
					return refinery;
				}

				if (power != null && refinery != null && !HasSufficientPowerForActor(refinery))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Build walls around protected structures
			if (baseBuilder.Info.ProtectedByWalls.Count > 0)
			{
				var wall = GetProducibleBuilding(baseBuilder.Info.WallTypes, buildableThings);
				if (wall != null && HasSufficientPowerForActor(wall))
				{
					var wallActorInfo = world.Map.Rules.Actors[wall.Name];
					var (wallLoc, _, _) = ChooseWallLocation(wallActorInfo);
					if (wallLoc != null)
					{
						AIUtils.BotDebug("{0} decided to build {1}: Priority override (wall)", queue.Actor.Owner, wall.Name);
						return wall;
					}
				}
			}

			// Make sure that we can spend as fast as we are earning
			if (baseBuilder.Info.NewProductionCashThreshold > 0 && baseBuilder.ShouldAddProduction()
				&& world.LocalRandom.Next(100) < baseBuilder.Info.NewProductionChance)
			{
				var production = GetProducibleBuilding(baseBuilder.Info.ProductionTypes, buildableThings);
				if (production != null && HasSufficientPowerForActor(production))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (production)", queue.Actor.Owner, production.Name);
					return production;
				}

				if (power != null && production != null && !HasSufficientPowerForActor(production))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Only consider building this if there is enough water inside the base perimeter and there are close enough adjacent buildings
			if (waterState == WaterCheck.EnoughWater && baseBuilder.Info.NewProductionCashThreshold > 0
				&& baseBuilder.ShouldAddProduction()
				&& AIUtils.IsAreaAvailable<GivesBuildableArea>(world, player, world.Map, baseBuilder.Info.CheckForWaterRadius, baseBuilder.Info.WaterTerrainTypes))
			{
				var navalproduction = GetProducibleBuilding(baseBuilder.Info.NavalProductionTypes, buildableThings);
				if (navalproduction != null && HasSufficientPowerForActor(navalproduction))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (navalproduction)", queue.Actor.Owner, navalproduction.Name);
					return navalproduction;
				}

				if (power != null && navalproduction != null && !HasSufficientPowerForActor(navalproduction))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Create some head room for resource storage if we really need it
			if (baseBuilder.HasStoragePressure())
			{
				var silo = GetProducibleBuilding(baseBuilder.Info.SiloTypes, buildableThings);
				if (silo != null && HasSufficientPowerForActor(silo))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (silo)", queue.Actor.Owner, silo.Name);
					return silo;
				}

				if (power != null && silo != null && !HasSufficientPowerForActor(silo))
				{
					AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);
					return power;
				}
			}

			// Pre-compute defense counts once for DefenseRoleLimits checks below.
			var totalDefenseCount = 0;
			Dictionary<string, int> tagDefenseCounts = null;
			Dictionary<DefenseRole, int> roleDefenseCounts = null;
			if (baseBuilder.Info.DefenseRoleLimits != null && baseBuilder.Info.DefenseRoleLimits.Count > 0
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

					var dr = GetDefenseRoleFromActor(a.Info);
					if (dr != DefenseRole.Default)
						roleDefenseCounts[dr] = (roleDefenseCounts.TryGetValue(dr, out var rc) ? rc : 0) + 1;
				}
			}

			// Reactive defense: prefer the role that matches the current hotspot,
			// then fall back to the global combat analysis trend.
			var ca = player.PlayerActor.TraitsImplementing<CombatAnalysisBotModule>()
				.FirstOrDefault(m => !m.IsTraitDisabled);
			var defenseCenterForThreat = baseBuilder.DefenseCenter == default ? baseBuilder.GetRandomBaseCenter() : baseBuilder.DefenseCenter;
			var hotspotRole = baseBuilder.GetBestDefenseHotspotRole(defenseCenterForThreat);
			var reactiveRole = hotspotRole != DefenseRole.Default
				? hotspotRole
				: ca != null && ca.HasActiveThreat()
					? ca.GetHighestThreatRole()
					: DefenseRole.Default;
			if (reactiveRole != DefenseRole.Default)
			{
				var reactiveDefense = ChooseReactiveDefense(buildableThings, reactiveRole, totalDefenseCount, roleDefenseCounts);
				if (reactiveDefense != null)
				{
					AIUtils.BotDebug("{0} reactive defense: building {1} (threat role: {2})",
						player, reactiveDefense.Name, reactiveRole);
					return reactiveDefense;
				}
			}

			// Build everything else. Prefer the structure with the largest deficit relative
			// to its desired base fraction instead of accepting the first random match.
			ActorInfo bestFractionActor = null;
			string bestFractionName = null;
			int bestFractionValue = 0;
			int bestFractionCount = 0;
			var bestFractionDeficit = int.MinValue;
			var baseBuildingCount = Math.Max(1, playerBuildings.Length);
			var buildableByName = buildableThings.ToDictionary(b => b.Name);

			foreach (var frac in baseBuilder.Info.BuildingFractions)
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

				// Check the number of this structure and its variants
				var count = CountExistingAndQueuedBuilding(name);

				// Do we want to build this structure?
				if (count * 100 > frac.Value * playerBuildings.Length)
					continue;

				if (baseBuilder.Info.BuildingLimits.TryGetValue(name, out var limit) && limit <= count)
					continue;

				// DefenseRoleLimits: scale-based cap relative to base size (replaces hard limits for defenses)
				if (tagDefenseCounts != null && baseBuilder.Info.DefenseTypes.Contains(name))
				{
					if (baseBuilder.Info.DefenseRoleLimits.TryGetValue("Total", out var totalLimit) &&
						totalDefenseCount * 100 >= totalLimit * playerBuildings.Length)
						continue;

					// Check every BotCapabilities tag of this actor against DefenseRoleLimits.
					var actorCaps = actor.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
					if (actorCaps != null)
					{
						var hitLimit = false;
						foreach (var tag in actorCaps)
						{
							if (baseBuilder.Info.DefenseRoleLimits.TryGetValue(tag, out var tagLimit) &&
								tagDefenseCounts.TryGetValue(tag, out var tagCnt) &&
								tagCnt * 100 >= tagLimit * playerBuildings.Length)
							{ hitLimit = true; break; }
						}
						if (hitLimit) continue;
					}
					else
					{
						// Fallback: DefenseRoles-based check for actors without BotCapabilities.
						var role = GetDefenseRoleFromActor(actor);
						if (role != DefenseRole.Default &&
							baseBuilder.Info.DefenseRoleLimits.TryGetValue(role.ToString(), out var roleLimit) &&
							roleDefenseCounts != null && roleDefenseCounts.TryGetValue(role, out var roleCnt) &&
							roleCnt * 100 >= roleLimit * playerBuildings.Length)
							continue;
					}
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

				var deficit = frac.Value * baseBuildingCount - count * 100;
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
				if (playerPower != null && (playerPower.ExcessPower < minimumExcessPower || !HasSufficientPowerForActor(bestFractionActor)))
				{
					// Try building a power plant instead
					if (power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(pi => pi.Amount) > 0)
					{
						if (playerPower.PowerOutageRemainingTicks > 0)
							AIUtils.BotDebug("{0} decided to build {1}: Priority override (is low power)", queue.Actor.Owner, power.Name);
						else
							AIUtils.BotDebug("{0} decided to build {1}: Priority override (would be low power)", queue.Actor.Owner, power.Name);

						return power;
					}
				}

				// Lets build this
				AIUtils.BotDebug("{0} decided to build {1}: Desired is {2} ({3} / {4}); current is {5} / {4}",
					queue.Actor.Owner, bestFractionName, bestFractionValue, bestFractionValue * playerBuildings.Length,
					playerBuildings.Length, bestFractionCount);
				return bestFractionActor;
			}

			// Too spammy to keep enabled all the time, but very useful when debugging specific issues.
			// AIUtils.BotDebug("{0} couldn't decide what to build for queue {1}.", queue.Actor.Owner, queue.Info.Group);
			return null;
		}

		/// <summary>
		/// Returns the best defense building to build reactively based on the highest-threat role.
		/// Respects DefenseRoleLimits and BuildingFractions; returns null if no eligible building exists.
		/// </summary>
		ActorInfo ChooseReactiveDefense(
			IEnumerable<ActorInfo> buildableThings,
			DefenseRole role,
			int totalDefenseCount,
			Dictionary<DefenseRole, int> roleDefenseCounts)
		{
			if (role == DefenseRole.Default)
				return null;

			// Find candidates by BotCapabilities tag matching the role enum name,
			// with fallback to the DefenseRoles dictionary.
			var roleStr = role.ToString();
			var candidates = buildableThings
				.Where(b =>
				{
					var caps = b.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
					if (caps != null)
						return caps.Contains(roleStr);
					return baseBuilder.Info.DefenseRoles.TryGetValue(role, out var types) && types.Contains(b.Name);
				})
				.ToList();

			if (candidates.Count == 0)
				return null;

			// Check total defense limit first
			if (baseBuilder.Info.DefenseRoleLimits != null &&
				baseBuilder.Info.DefenseRoleLimits.TryGetValue("Total", out var totalLimit) &&
				totalDefenseCount * 100 >= totalLimit * playerBuildings.Length)
				return null;

			// Check role limit
			if (baseBuilder.Info.DefenseRoleLimits != null &&
				baseBuilder.Info.DefenseRoleLimits.TryGetValue(roleStr, out var roleLimit) &&
				roleDefenseCounts != null &&
				roleDefenseCounts.TryGetValue(role, out var roleCnt) &&
				roleCnt * 100 >= roleLimit * playerBuildings.Length)
				return null;

			foreach (var actorInfo in candidates)
			{
				var count = CountExistingAndQueuedBuilding(actorInfo.Name);

				// Don't exceed the building's desired fraction even when reacting
				if (baseBuilder.Info.BuildingFractions != null &&
					baseBuilder.Info.BuildingFractions.TryGetValue(actorInfo.Name, out var frac) &&
					count * 100 > frac * playerBuildings.Length)
					continue;

				if (!HasSufficientPowerForActor(actorInfo))
					continue;

				return actorInfo;
			}

			return null;
		}

		DefenseRole GetDefenseRoleFromActor(ActorInfo actorInfo)
		{
			var caps = actorInfo.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (caps != null)
				foreach (var tag in caps)
					if (Enum.TryParse<DefenseRole>(tag, true, out var r) && r != DefenseRole.Default)
						return r;

			return baseBuilder.Info.DefenseRoles
				.FirstOrDefault(kv => kv.Value.Contains(actorInfo.Name)).Key;
		}

		(CPos? Location, CPos? BaseCenter, int Variant) ChooseBuildLocation(string actorType, bool distanceToBaseIsImportant, BuildingType type)
		{
			var actorInfo = world.Map.Rules.Actors[actorType];
			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();

			if (bi == null)
				return (null, null, 0);

			// Determine layout for this building type
			var layout = baseBuilder.Info.DefaultLayout;
			var minSpacing = baseBuilder.Info.DefaultMinSpacing;
			if (baseBuilder.Info.BuildingLayouts.TryGetValue(actorType, out var layoutEntry))
			{
				layout = layoutEntry.Layout;
				minSpacing = layoutEntry.MinSpacing;
			}

			var sameTypeBuildingsForSpacing = playerBuildings
				.Where(a => a.Info.Name == actorType)
				.Select(a => a.Location)
				.ToList();

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

			// Find the buildable cell that is closest to pos and centered around center
			(CPos? Location, CPos Center, int Variant) FindPos(CPos center, CPos target, int minRange, int maxRange)
			{
				var actorVariant = 0;
				var buildingVariantInfo = actorInfo.TraitInfoOrDefault<PlaceBuildingVariantsInfo>();
				var variantActorInfo = actorInfo;
				var vbi = bi;

				// PERF: With 8 bots placing buildings simultaneously, materializing and sorting
				// the full ~2000-cell annulus (radius 25) per call dominates bot_tick.
				// FindTilesInAnnulus yields cells ring-by-ring (inner→outer), so capping at
				// 256 covers the base interior. Grid layout keeps the full range because its
				// Where filter already limits what gets materialized for sorting.
				const int FindPosLimit = 256;
				var allCells = layout == BaseBuildingLayout.Grid
					? world.Map.FindTilesInAnnulus(center, minRange, maxRange)
					: world.Map.FindTilesInAnnulus(center, minRange, maxRange).Take(FindPosLimit);

				// Apply layout-specific cell ordering
				var sameTypeBuildings = playerBuildings
					.Where(a => a.Info.Name == actorType)
					.Select(a => a.Location)
					.ToList();

				IEnumerable<CPos> cells;
				if (layout == BaseBuildingLayout.Compact)
					cells = allCells.OrderBy(c => (c - center).LengthSquared);
				else if (layout == BaseBuildingLayout.Clustered && sameTypeBuildings.Count > 0)
					cells = allCells.OrderBy(c => sameTypeBuildings.Min(loc => (c - loc).LengthSquared));
				else if (layout == BaseBuildingLayout.Grid)
				{
					// Anchor the grid to the first placed building of this type so that
					// subsequent placements align to the same raster. Falls back to center.
					var gridAnchor = sameTypeBuildings.Count > 0 ? sameTypeBuildings[0] : center;
					var gx = Math.Max(1, baseBuilder.Info.GridSpacingX);
					var gy = Math.Max(1, baseBuilder.Info.GridSpacingY);
					cells = allCells
						.Where(c => ((c.X - gridAnchor.X) % gx + gx) % gx == 0
							&& ((c.Y - gridAnchor.Y) % gy + gy) % gy == 0)
						.OrderBy(c => (c - target).LengthSquared);
				}
				else
					cells = allCells;

				// Sort by distance to target if we have one
				if (layout == BaseBuildingLayout.Random && center != target)
				{
					cells = cells.OrderBy(c => (c - target).LengthSquared);

					// Rotate building if we have a Facings in buildingVariantInfo.
					// If we don't have Facings in buildingVariantInfo, use a random variant
					if (buildingVariantInfo?.Actors != null)
					{
						if (buildingVariantInfo.Facings != null)
						{
							var vector = world.Map.CenterOfCell(target) - world.Map.CenterOfCell(center);

							// The rotation Y point to upside vertically, so -Y = Y(rotation)
							var desireFacing = new WAngle(WAngle.ArcSin((int)((long)Math.Abs(vector.X) * 1024 / vector.Length)).Angle);
							if (vector.X > 0 && vector.Y >= 0)
								desireFacing = new WAngle(512) - desireFacing;
							else if (vector.X < 0 && vector.Y >= 0)
								desireFacing = new WAngle(512) + desireFacing;
							else if (vector.X < 0 && vector.Y < 0)
								desireFacing = -desireFacing;

							for (int i = 0, e = 1024; i < buildingVariantInfo.Facings.Length; i++)
							{
								var minDelta = Math.Min((desireFacing - buildingVariantInfo.Facings[i]).Angle, (buildingVariantInfo.Facings[i] - desireFacing).Angle);
								if (e > minDelta)
								{
									e = minDelta;
									actorVariant = i;
								}
							}
						}
						else
							actorVariant = world.LocalRandom.Next(buildingVariantInfo.Actors.Length + 1);
					}
				}
				else
				{
					cells = cells.Shuffle(world.LocalRandom);

					if (buildingVariantInfo?.Actors != null)
						actorVariant = world.LocalRandom.Next(buildingVariantInfo.Actors.Length + 1);
				}

				if (actorVariant != 0)
				{
					variantActorInfo = world.Map.Rules.Actors[buildingVariantInfo.Actors[actorVariant - 1]];
					vbi = variantActorInfo.TraitInfoOrDefault<BuildingInfo>();
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

			var baseCenter = baseBuilder.GetRandomBaseCenter();

			// Cache once — GetEffectiveMaxBaseRadius() scans all world buildings otherwise.
			var effectiveMaxRadius = baseBuilder.GetEffectiveMaxBaseRadius(playerBuildings.Length);

			// If this building type has a NearBuilding override, shift the search center
			// toward the average position of all existing buildings of that type.
			// Falls back to baseCenter when no instances exist yet.
			var effectiveCenter = baseCenter;
			if (layoutEntry?.NearBuilding != null)
			{
				var nearInstances = playerBuildings
					.Where(b => b.Info.Name == layoutEntry.NearBuilding)
					.ToList();
				if (nearInstances.Count > 0)
					effectiveCenter = new CPos(
						(int)nearInstances.Average(b => b.Location.X),
						(int)nearInstances.Average(b => b.Location.Y));
			}

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

					var defenseCenter = baseBuilder.DefenseCenter == default ? baseCenter : baseBuilder.DefenseCenter;
					var rememberedHotspot = baseBuilder.GetBestDefenseHotspot(defenseCenter);
					var targetCell = rememberedHotspot ?? (baseBuilder.DefenseCenter == default ? baseCenter : defenseCenter);

					// Reuse the outer-scope precomputed array (same data, avoids a second TraitInfoOrDefault pass).
					var playerBuildingInfos = globalBuildingInfos;

					var innerRadius = baseBuilder.Info.DefenseInnerRadius;
					var outerRadius = baseBuilder.Info.MaximumDefenseRadius;
					var midRadius = innerRadius > 0 ? (innerRadius + outerRadius) / 2 : outerRadius;
					var sameDefenseBuildings = playerBuildings
						.Where(a => a.Info.Name == actorType)
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
					IEnumerable<CPos> DefenseCandidateCells(CPos center, CPos target, int minRange, int maxRange, bool outerFirst = true)
					{
						minRange = Math.Max(0, minRange);
						maxRange = Math.Max(minRange, maxRange);
						var limit = Math.Max(1, baseBuilder.Info.DefensePlacementCandidateLimit);
						var maxCandidates = Math.Max(24, limit * 4);
						var result = new List<CPos>(maxCandidates);
						var seen = new HashSet<CPos>();
						var dx = target.X - center.X;
						var dy = target.Y - center.Y;
						var denom = Math.Max(Math.Abs(dx), Math.Abs(dy));
						var px = dy == 0 ? 0 : -Math.Sign(dy);
						var py = dx == 0 ? 0 : Math.Sign(dx);
						var offsets = new[] { 0, -2, 2, -4, 4 };
						var phase = Math.Abs(defensePlacementAttempt % 3);

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

						foreach (var radius in Radii())
						{
							if ((radius + phase) % 3 == 1)
								continue;

							if (denom > 0)
							{
								var main = new CPos(center.X + dx * radius / denom, center.Y + dy * radius / denom);
								foreach (var offset in offsets)
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
						var limit = Math.Max(1, baseBuilder.Info.DefensePlacementCandidateLimit);
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
									.OrderByDescending(DefenseScore)
									.ThenByDescending(c => sameDefenseBuildings.Min(loc => (c - loc).LengthSquared))
								: LimitDefenseCandidates(defenseCells, DefenseScore)
									.OrderByDescending(DefenseScore)
									.ThenBy(c => world.LocalRandom.Next());
							break;
						}

						case DefenseRole.ArmorDefense:
						{
							// Outer radius, sorted toward the most relevant attack direction.
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								innerRadius > 0 ? innerRadius : baseBuilder.Info.MinimumDefenseRadius, outerRadius);
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(DefenseScore)
								.ThenBy(c => (c - targetCell).LengthSquared);
							break;
						}

						case DefenseRole.AADefense:
						{
							// Coverage-based: pick cell that covers the most uncovered base buildings.
							// Use the actor's weapon range to determine coverage radius.
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

							// Buildings that need AA coverage
							var baseBuildingPositions = playerBuildings
								.Select(b => b.Location)
								.ToList();

							// Track which base buildings are already covered by existing AA
							var coveredByExisting = new HashSet<CPos>();
							foreach (var existingAA in playerBuildings.Where(b => b.Info.Name == actorType))
								foreach (var bPos in baseBuildingPositions)
									if ((bPos - existingAA.Location).LengthSquared <= coverageRadiusCellsSq)
										coveredByExisting.Add(bPos);

							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								baseBuilder.Info.MinimumDefenseRadius, outerRadius);

							// First limit by tactical direction, then run the heavier coverage check
							// only on the small candidate set.
							// Materialize uncoveredPositions once to avoid re-evaluating LINQ per candidate in OrderByDescending.
							var uncoveredPositions = baseBuildingPositions
								.Where(bPos => !coveredByExisting.Contains(bPos))
								.ToArray();
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(c =>
									uncoveredPositions.Count(bPos => (bPos - c).LengthSquared <= coverageRadiusCellsSq))
								.ThenByDescending(DefenseScore);
							break;
						}

						case DefenseRole.ArtilleryDefense:
						{
							// Inner-to-mid radius behind the front, Richtung Feind
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								baseBuilder.Info.MinimumDefenseRadius, midRadius);
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(DefenseScore)
								.ThenByDescending(c => (c - targetCell).LengthSquared);
							break;
						}

						case DefenseRole.Special:
						{
							// Outermost radius directly on the main approach vector.
							defenseCells = DefenseCandidateCells(defenseCenter, targetCell,
								outerRadius - 2 > baseBuilder.Info.MinimumDefenseRadius ? outerRadius - 2 : baseBuilder.Info.MinimumDefenseRadius,
								outerRadius);
							sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
								.OrderByDescending(DefenseScore)
								.ThenBy(c => (c - targetCell).LengthSquared);
							break;
						}

						default:
						{
							// Original inner/outer logic for unassigned defense types
							var useInnerLine = false;
							if (innerRadius > 0)
							{
								var allDefense = playerBuildings
									.Where(a => baseBuilder.Info.DefenseTypes.Contains(a.Info.Name)).ToList();
								var innerCount = allDefense.Count(a => (a.Location - baseCenter).Length <= innerRadius);
								var totalCount = allDefense.Count + 1;
								var targetInnerCount = totalCount * baseBuilder.Info.DefenseInnerRatio / 100;
								useInnerLine = innerCount < targetInnerCount;
							}

							defenseCells = useInnerLine
								? DefenseCandidateCells(baseCenter, targetCell, baseBuilder.Info.MinimumDefenseRadius, innerRadius, false)
								: DefenseCandidateCells(defenseCenter, targetCell,
									innerRadius > 0 ? innerRadius : baseBuilder.Info.MinimumDefenseRadius, outerRadius);

							var defLayout = useInnerLine ? baseBuilder.Info.DefenseInnerLayout : baseBuilder.Info.DefenseOuterLayout;
							if (defLayout == BaseBuildingLayout.Clustered && sameDefenseBuildings.Count > 0)
								sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
									.OrderBy(c => sameDefenseBuildings.Min(loc => (c - loc).LengthSquared));
							else if (defLayout == BaseBuildingLayout.Compact)
								sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
									.OrderBy(c => (c - baseCenter).LengthSquared);
							else
								sortedDefenseCells = LimitDefenseCandidates(defenseCells, DefenseScore)
									.OrderByDescending(DefenseScore)
									.ThenBy(c => (c - targetCell).LengthSquared);

							break;
						}
					}

					// Role-specific spacing: AA and AntiInf use outer spacing, others use their own
					var defMinSpacing = role == DefenseRole.AADefense || role == DefenseRole.InfantryDefense
						? baseBuilder.Info.DefenseOuterMinSpacing
						: baseBuilder.Info.DefenseInnerMinSpacing;
					var defMinSpacingSq = defMinSpacing * defMinSpacing;

					foreach (var cell in sortedDefenseCells)
					{
						if (!world.CanPlaceBuilding(cell, defVariantActorInfo, defVbi, null)) continue;
						if (!defVbi.IsCloseEnoughToBase(world, player, defVariantActorInfo, cell)) continue;

						if (defMinSpacing > 0 && sameDefenseBuildings.Count > 0
							&& sameDefenseBuildings.Any(loc => (cell - loc).LengthSquared < defMinSpacingSq))
							continue;

						if (playerBuildingInfos != null)
						{
							var globalSpacing = baseBuilder.Info.GlobalMinSpacing;
							var hasLayoutOverride = baseBuilder.Info.BuildingLayouts.ContainsKey(actorType);
							var tooClose = false;
							var nRight = cell.X + defVbi.Dimensions.X;
							var nBottom = cell.Y + defVbi.Dimensions.Y;
							foreach (var (existing, existingBi) in playerBuildingInfos)
							{
								if (hasLayoutOverride && existing.Info.Name == actorType) continue;
								var eRight = existing.Location.X + existingBi.Dimensions.X;
								var eBottom = existing.Location.Y + existingBi.Dimensions.Y;
								var gapX = Math.Max(0, Math.Max(existing.Location.X - nRight, cell.X - eRight));
								var gapY = Math.Max(0, Math.Max(existing.Location.Y - nBottom, cell.Y - eBottom));
								if (Math.Max(gapX, gapY) < globalSpacing) { tooClose = true; break; }
							}

							if (tooClose) continue;
						}

						return (cell, defenseCenter, defVariant);
					}

					// Do not fall back to a full vanilla annulus scan here: defense placement
					// can run for several bots at once, and the exhaustive CanPlaceBuilding
					// pass causes visible bot_tick spikes. Retry on the next build attempt
					// instead, where target/variant/candidate ordering may differ.
					return (null, defenseCenter, defVariant);
				}

				case BuildingType.Refinery:

					var requestRef = baseBuilder.RequestedRefineries.Count > 0 ? baseBuilder.RequestedRefineries.Keys.First() : null;

					// Try and place the refinery near a resource field
					if (resourceLayer != null)
					{
						// If we have failed to place to the requested refinery point, try and place it near the base center
						var resourceBaseCenter = failCount > 0 ? baseCenter :
							(requestRef != null ? baseBuilder.RequestedRefineries[requestRef].ConyardLoc : (baseBuilder.ResourceConyardCenter ?? baseCenter));

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

						CPos[] resourcesShouldCheck = null;

						if (closestRefinery == null)
						{
							// For the first refinery: score each resource cell purely by LOCAL accessibility —
							// open orthogonal neighbours (cliff-proximity) and how many resource cells are
							// in the immediate area (field density). No BFS from the conyard: that starting
							// point is biased toward whichever field happens to be closest to the base,
							// which is exactly the wrong heuristic when a nearby field is cliff-locked.
							resourcesShouldCheck = nearbyResources
								.OrderByDescending(c => CountPassableOrthogonalNeighbors(c) * 10 - (c - resourceBaseCenter).LengthSquared / 8)
								.Take(baseBuilder.Info.MaxResourceCellsToCheck)
								.ToArray();
						}
						else if (requestRef != null)
						{
							resourcesShouldCheck = nearbyResources.OrderBy(c => (c - baseBuilder.RequestedRefineries[requestRef].ResourceLoc).LengthSquared)
								.Take(baseBuilder.Info.MaxResourceCellsToCheck)
								.ToArray();
						}
						else
							resourcesShouldCheck = nearbyResources.OrderByDescending(c => (c - closestRefinery.Location).LengthSquared)
								.Take(baseBuilder.Info.MaxResourceCellsToCheck)
								.ToArray();

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
							var candidateCells = GetRefineryCandidateCellsForField(resourceBaseCenter, actorInfo, bi, sampledResourceCells)
								.Take(12)
								.ToArray();

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

								// Path-length A* removed: too expensive (hundreds of full A* calls per placement decision).
								// The structural checks above (open neighbours, dock probe, HasOpenRefineryApproach,
								// PathMightExistForLocomotorBlockedByImmovable, nearbyResources >= 3 open neighbours)
								// are sufficient to reject cliff-locked placements without pathfinder cost.
								int? harvesterPathLength = null;

								var score = ScoreRefineryCandidate(actorInfo, resourceBaseCenter, r, loc, existingRefineries, sampledResourceCells, harvesterPathLength);
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
							var candidatesRelaxed = GetRefineryCandidateCellsForField(resourceBaseCenter, actorInfo, bi, sampledRelaxed)
								.Take(6)
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

								var score = ScoreRefineryCandidate(actorInfo, resourceBaseCenter, r, loc, existingRefineries, sampledRelaxed, null);
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

					// Try and find a free spot somewhere else in the base
					return FindPos(baseCenter, baseCenter, baseBuilder.Info.MinBaseRadius, effectiveMaxRadius);

				case BuildingType.Building:
				{
					if (layout == BaseBuildingLayout.Coverage)
					{
						// Read coverage radius from ProximityExternalCondition on the actor, fall back to 8.
						var proximityInfo = actorInfo.TraitInfoOrDefault<ProximityExternalConditionInfo>();
						var coverageRadius = proximityInfo != null
							? Math.Max(1, proximityInfo.Range.Length / 1024)
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

					return FindPos(effectiveCenter, effectiveCenter, baseBuilder.Info.MinBaseRadius,
						distanceToBaseIsImportant ? effectiveMaxRadius : world.Map.Grid.MaximumTileSearchRange);
				}
			}

			// Can't find a build location
			return (null, null, 0);
		}

		// Find a free adjacent cell around a ProtectedByWalls building, then fall back to wall ring.
		(CPos? Location, CPos? BaseCenter, int Variant) ChooseWallLocation(ActorInfo actorInfo)
		{
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

			return (null, null, 0);
		}
	}
}
