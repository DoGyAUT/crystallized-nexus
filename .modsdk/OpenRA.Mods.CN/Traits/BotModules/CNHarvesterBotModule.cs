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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("CN custom harvester bot module with refinery-aware field distribution.")]
	public class CNHarvesterBotModuleInfo : ConditionalTraitInfo, NotBefore<IResourceLayerInfo>, NotBefore<CNResourceMapBotModuleInfo>
	{
		[Desc("Actor types that are considered harvesters. If harvester count drops below RefineryTypes count, a new harvester is built.")]
		public readonly FrozenSet<string> HarvesterTypes = FrozenSet<string>.Empty;

		[Desc("Actor types that are counted as refineries.")]
		public readonly FrozenSet<string> RefineryTypes = FrozenSet<string>.Empty;

		[Desc("Interval (in ticks) between giving out orders to idle harvesters.")]
		public readonly int ScanForIdleHarvestersInterval = 50;

		[Desc("Interval (in ticks) between rebalancing harvesters across resource indices.")]
		public readonly int ScanForLowEffectHarvestersInterval = 433;

		[Desc("Interval (in ticks) between checking whether full harvesters should switch to a less congested refinery.")]
		public readonly int ScanForDockReassignmentInterval = 20;

		[Desc("When an idle harvester cannot find resources, increase the wait to this many scan intervals.")]
		public readonly int ScanIntervalMultiplerWhenNoResources = 5;

		[Desc("Avoid enemy actors nearby when searching for a new resource patch.")]
		public readonly WDist HarvesterEnemyAvoidanceRadius = WDist.FromCells(10);

		[Desc("For each enemy within the threat radius, apply the following cost multiplier for every cell that needs to be moved through.")]
		public readonly int HarvesterEnemyAvoidanceCostMultipler = 20;

		[Desc("How many resource cells should a harvester respond for.")]
		public readonly int ResourceCellsPerHarvester = 4;

		[Desc("How many harvesters should player own at least.")]
		public readonly int InitialHarvesters = 4;

		[Desc("Soft cap for total harvesters based on current refinery count.")]
		public readonly int MaxHarvestersPerRefinery = 2;

		[Desc("Maximum desired harvesters assigned to a finite resource indice.")]
		public readonly int MaxHarvestersPerFiniteIndice = 2;

		[Desc("Maximum desired harvesters assigned to a respawning resource indice.")]
		public readonly int MaxHarvestersPerRespawningIndice = 3;

		[Desc("Score penalty per reserved client at a refinery when searching for the best dock.")]
		public readonly int RefineryOccupancyPenalty = 200;

		[Desc("Score bonus for a respawning field near a refinery.")]
		public readonly int RespawningFieldBonus = 80;

		[Desc("How far around a refinery to look for already docking / unloading harvesters.")]
		public readonly int BusyRefineryNearbyHarvesterRadius = 5;

		[Desc("Score penalty per nearby busy harvester at a refinery.")]
		public readonly int BusyRefineryNearbyHarvesterPenalty = 350;

		[Desc("Apply an extra strong malus once this many busy harvesters are already tied to a refinery.")]
		public readonly int BusyRefineryHardAvoidThreshold = 2;

		[Desc("Extra score penalty when a refinery is already visibly backed up.")]
		public readonly int BusyRefineryHardAvoidPenalty = 1200;

		[Desc("Minimum score improvement required before rerouting a full harvester to another refinery.")]
		public readonly int DockReassignmentScoreThreshold = 250;

		[Desc("Distance slack (in cells squared) where a free refinery should beat a backed-up closer refinery.")]
		public readonly int FreeRefineryDistanceSlack = 64;

		public override object Create(ActorInitializer init) { return new CNHarvesterBotModule(init.Self, this); }
	}

	public class CNHarvesterBotModule : ConditionalTrait<CNHarvesterBotModuleInfo>, IBotTick, IBotRespondToAttack, INotifyActorDisposing, IWorldLoaded
	{
		sealed class HarvesterTraitWrapper
		{
			public readonly Actor Actor;
			public readonly Harvester Harvester;
			public readonly DockClientManager DockClientManager;
			public readonly Parachutable Parachutable;
			public readonly Mobile Mobile;
			public int NoResourcesCooldown { get; set; }
			public CPos LastKnownPosition { get; set; }
			public int StationaryTicks { get; set; }

			public HarvesterTraitWrapper(Actor actor)
			{
				Actor = actor;
				Harvester = actor.Trait<Harvester>();
				DockClientManager = actor.Trait<DockClientManager>();
				Parachutable = actor.TraitOrDefault<Parachutable>();
				Mobile = actor.TraitOrDefault<Mobile>();
				LastKnownPosition = actor.Location;
			}
		}

		const int StuckHarvesterThreshold = 200;
		const int StuckNearRefineryRadius = 3;

		readonly World world;
		readonly Player player;
		readonly Func<Actor, bool> unitCannotBeOrdered;
		readonly Dictionary<Actor, HarvesterTraitWrapper> harvesters = [];
		readonly Stack<HarvesterTraitWrapper> harvestersNeedingOrders = [];
		readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> refineries;
		readonly ActorIndex.OwnerAndNamesAndTrait<HarvesterInfo> harvestersIndex;
		readonly Dictionary<CPos, string> resourceTypesByCell = [];

		// Tracks which refinery each harvester was last directed to harvest for.
		// Persists until the harvester becomes idle again and receives a new assignment.
		// Used to penalise over-assigned refineries so all docks are utilised.
		readonly Dictionary<Actor, Actor> harvesterRefineryAssignment = [];

		IResourceLayer resourceLayer;
		ResourceClaimLayer claimLayer;
		IBotRequestUnitProduction[] requestUnitProduction;
		CNResourceMapBotModule resourceMapModule;
		CNBotProfileBotModule profileModule;

		int scanForLowEffectHarvestersTicks;
		int scanForIdleHarvestersTicks;
		int scanForDockReassignmentTicks;
		int respondToAttackCooldown = 40;
		bool firstTick = true;

		public CNHarvesterBotModule(Actor self, CNHarvesterBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			unitCannotBeOrdered = a => a.Owner != self.Owner || a.IsDead || !a.IsInWorld;
			refineries = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.RefineryTypes, player);
			harvestersIndex = new ActorIndex.OwnerAndNamesAndTrait<HarvesterInfo>(world, info.HarvesterTypes, player);
		}

		protected override void Created(Actor self)
		{
			requestUnitProduction = self.Owner.PlayerActor.TraitsImplementing<IBotRequestUnitProduction>().ToArray();
			resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			claimLayer = world.WorldActor.TraitOrDefault<ResourceClaimLayer>();
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			if (resourceLayer == null)
				return;

			foreach (var cell in w.Map.AllCells)
			{
				var resource = resourceLayer.GetResource(cell);
				if (resource.Type != null)
					resourceTypesByCell[cell] = resource.Type;
			}

			resourceLayer.CellChanged += ResourceCellChanged;
		}

		void ResourceCellChanged(CPos cell, string resourceType)
		{
			if (resourceType == null)
				resourceTypesByCell.Remove(cell);
			else
				resourceTypesByCell[cell] = resourceType;
		}

		protected override void TraitEnabled(Actor self)
		{
			scanForIdleHarvestersTicks = world.LocalRandom.Next(Info.ScanForIdleHarvestersInterval);
			scanForLowEffectHarvestersTicks = world.LocalRandom.Next(Info.ScanForLowEffectHarvestersInterval);
			scanForDockReassignmentTicks = world.LocalRandom.Next(Info.ScanForDockReassignmentInterval);
		}

		void IBotTick.BotTick(IBot bot)
		{
			respondToAttackCooldown--;

			if (resourceLayer == null || resourceLayer.IsEmpty)
				return;

			if (firstTick)
			{
				resourceMapModule = bot.Player.PlayerActor.TraitsImplementing<CNResourceMapBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				profileModule = bot.Player.PlayerActor.TraitsImplementing<CNBotProfileBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				firstTick = false;
			}

			var searchedForResources = false;
			while (harvestersNeedingOrders.TryPop(out var hno) && !searchedForResources)
				searchedForResources = HarvestIfAble(bot, hno);

			if (--scanForIdleHarvestersTicks <= 0)
			{
				scanForIdleHarvestersTicks = Info.ScanForIdleHarvestersInterval;
				FindIdleHarvester();

				var unitBuilder = requestUnitProduction.FirstEnabledTraitOrDefault();
				if (unitBuilder != null && Info.HarvesterTypes.Count > 0)
				{
					var harvsNum = AIUtils.CountActorByCommonName(harvestersIndex);
					var desiredHarvesterCount = GetDesiredHarvesterCount();
					if (harvsNum < desiredHarvesterCount)
					{
						var harvesterType = Info.HarvesterTypes.Random(world.LocalRandom);
						if (unitBuilder.RequestedProductionCount(bot, harvesterType) == 0)
							unitBuilder.RequestUnitProduction(bot, harvesterType);
					}
				}
			}

			if (--scanForLowEffectHarvestersTicks <= 0)
			{
				scanForLowEffectHarvestersTicks = Info.ScanForLowEffectHarvestersInterval;
				if (resourceMapModule != null)
					FindAndOrderLowEffectHarvesterOnResourceMap(bot);
			}

			if (--scanForDockReassignmentTicks <= 0)
			{
				scanForDockReassignmentTicks = Info.ScanForDockReassignmentInterval;
				ReassignCongestedDockingHarvesters(bot);
			}
		}

		int GetDesiredHarvesterCount()
		{
			var refineryCount = Math.Max(0, AIUtils.CountActorByCommonName(refineries));
			var target = Info.InitialHarvesters;

			if (resourceMapModule == null)
				return ApplyStrategyHarvesterTarget(Math.Max(target, refineryCount), refineryCount);

			var fieldDemand = 0;
			for (var i = 0; i < resourceMapModule.GetIndicesLength(); i++)
			{
				var indice = resourceMapModule.GetIndice(i);
				if (indice == null)
					continue;

				if (indice.ResourceCellsCount <= 0 && !indice.HasRespawningResourceSource)
					continue;

				// Only count fields we are actually working or staging around.
				// The previous logic summed every valuable field on the whole map,
				// which scaled to huge harvester targets once the bot had many refineries.
				if (indice.PlayerRefineryCount <= 0)
					continue;

				var demandForIndice = Exts.IntegerDivisionRoundingAwayFromZero(
					Math.Max(1, indice.ResourceCellsCount),
					Math.Max(1, Info.ResourceCellsPerHarvester));

				var maxPerIndice = indice.HasRespawningResourceSource
					? Info.MaxHarvestersPerRespawningIndice
					: Info.MaxHarvestersPerFiniteIndice;

				if (indice.HasRespawningResourceSource)
					demandForIndice = Math.Max(1, demandForIndice);

				fieldDemand += Math.Min(Math.Max(1, demandForIndice), Math.Max(1, maxPerIndice));
			}

			var refineryCap = refineryCount > 0
				? Math.Max(Info.InitialHarvesters, refineryCount * Math.Max(1, Info.MaxHarvestersPerRefinery))
				: Info.InitialHarvesters;

			// Keep one small spare above the current worked-field demand, but do not let
			// refinery count alone explode the harvester population.
			target = Math.Max(target, fieldDemand + (refineryCount > 0 ? 1 : 0));
			target = ApplyStrategyHarvesterTarget(target, refineryCount);
			return Math.Min(target, refineryCap);
		}

		int ApplyStrategyHarvesterTarget(int target, int refineryCount)
		{
			var percent = profileModule?.CurrentStrategy.HarvesterTargetPercent ?? 100;
			if (percent != 100)
				target = Exts.IntegerDivisionRoundingAwayFromZero(target * Math.Max(1, percent), 100);

			return Math.Max(refineryCount, target);
		}

		void FindAndOrderLowEffectHarvesterOnResourceMap(IBot bot)
		{
			CPos? worstEffectIndice = null;
			var worstEffectHarvesterCount = int.MaxValue;
			var lackHarvesterIndices = new List<(int Attraction, int LackHarvs, CPos ResoueceCenter)>();
			var indiceSideLengthSquare = resourceMapModule.GetIndiceSideLength() * resourceMapModule.GetIndiceSideLength();

			for (var i = 0; i < resourceMapModule.GetIndicesLength(); i++)
			{
				var baseIndice = resourceMapModule.GetIndice(i);
				if (baseIndice == null)
					continue;

				var attraction = indiceSideLengthSquare >> 5;
				attraction += baseIndice.ResourceCellsCount - baseIndice.PlayerHarvetserCount * Info.ResourceCellsPerHarvester;
				if (baseIndice.HasRespawningResourceSource)
					attraction += Info.RespawningFieldBonus;

				var lackHarvs = attraction > 0
					? attraction / Info.ResourceCellsPerHarvester
					: (attraction == 0 && baseIndice.ResourceCellsCount > 0 ? 1 : -1);

				attraction >>= 1;

				if (baseIndice.PlayerRefineryCount <= 0 && lackHarvs > 0)
					lackHarvs = 2;

				if (baseIndice.EnemyBaseCount > 0 || baseIndice.EnemyUnitCount > 0)
					attraction -= indiceSideLengthSquare << 4;
				else
				{
					var (_, nearbyEnemy, nearbyEnemyBase) = resourceMapModule.GetNearbyIndicesThreat(i);
					if (nearbyEnemyBase + nearbyEnemy > 0)
						attraction -= indiceSideLengthSquare >> 5;
				}

				if (baseIndice.PlayerRefineryCount > 0)
					attraction += indiceSideLengthSquare;

				if (baseIndice.ResourceCellsCount > 0 && attraction > 0 && lackHarvs > 0)
					lackHarvesterIndices.Add((attraction, lackHarvs, baseIndice.ResourceCellsCenter));

				if (lackHarvs < worstEffectHarvesterCount && lackHarvs < 0)
				{
					worstEffectHarvesterCount = lackHarvs;
					worstEffectIndice = baseIndice.IndiceCenter;
				}
			}

			if (worstEffectIndice == null)
				return;

			var harvestersCanAssign = -worstEffectHarvesterCount;
			var searchRadius = resourceMapModule.GetIndiceScanRadius();
			var nearbyHarvesters = world.FindActorsInCircle(world.Map.CenterOfCell(worstEffectIndice.Value), WDist.FromCells(searchRadius))
				.Where(a => a.Owner == player && resourceMapModule.Info.HarvesterTypes.Contains(a.Info.Name))
				.ToList();

			var pathDistanceSquareFactor = resourceMapModule.GetIndiceRowCount() * resourceMapModule.GetIndiceRowCount()
				+ resourceMapModule.GetIndiceColumnCount() * resourceMapModule.GetIndiceColumnCount();

			harvestersCanAssign = Math.Min(harvestersCanAssign, nearbyHarvesters.Count - 1);
			if (harvestersCanAssign <= 0)
				return;

			foreach (var (_, lackHarvs, resourceCenter) in
				lackHarvesterIndices.OrderByDescending(d => d.Attraction - (nearbyHarvesters[0].Location - d.ResoueceCenter).LengthSquared / pathDistanceSquareFactor))
			{
				if (harvestersCanAssign <= 0)
					break;

				var needHarvs = lackHarvs;
				var nearbyResources = world.Map.FindTilesInAnnulus(resourceCenter, 0, resourceMapModule.GetIndiceScanRadius())
					.Where(c => resourceMapModule.Info.ValuableResourceTypes.Contains(resourceLayer.GetResource(c).Type)
						&& (nearbyHarvesters[0].Location - resourceCenter).LengthSquared >= (c - nearbyHarvesters[0].Location).LengthSquared)
					.ToArray();

				if (nearbyResources.Length <= 0 || needHarvs <= 0)
					continue;

				var usedHarvs = new HashSet<Actor>();
				foreach (var harv in nearbyHarvesters)
				{
					if (needHarvs <= 0 || harvestersCanAssign <= 0)
						break;

					var parach = harv.TraitOrDefault<Parachutable>();
					if (parach != null && parach.IsInAir)
					{
						harvestersCanAssign--;
						usedHarvs.Add(harv);
						continue;
					}

					var mobile = harv.TraitOrDefault<Mobile>();
					var targetCell = nearbyResources.Random(world.LocalRandom);
					if (mobile == null || mobile.PathFinder.PathMightExistForLocomotorBlockedByImmovable(mobile.Locomotor, harv.Location, targetCell))
					{
						bot.QueueOrder(new Order("Harvest", harv, Target.FromCell(world, targetCell), false));
						needHarvs--;
						harvestersCanAssign--;
						usedHarvs.Add(harv);
					}
				}

				nearbyHarvesters.RemoveAll(usedHarvs.Contains);
			}
		}

		void FindIdleHarvester()
		{
			var toRemove = harvesters.Keys.Where(unitCannotBeOrdered).ToList();
			foreach (var a in toRemove)
			{
				harvesters.Remove(a);
				harvesterRefineryAssignment.Remove(a);
			}

			var newHarvesters = world.ActorsHavingTrait<Harvester>().Where(a => !unitCannotBeOrdered(a) && !harvesters.ContainsKey(a));
			foreach (var a in newHarvesters)
				harvesters[a] = new HarvesterTraitWrapper(a);

			harvestersNeedingOrders.Clear();
			foreach (var h in harvesters)
			{
				var wrapper = h.Value;
				if (wrapper.Actor.IsIdle)
				{
					wrapper.StationaryTicks = 0;
					wrapper.LastKnownPosition = wrapper.Actor.Location;
				}
				else if (wrapper.Actor.Location == wrapper.LastKnownPosition)
					wrapper.StationaryTicks += Info.ScanForIdleHarvestersInterval;
				else
				{
					wrapper.StationaryTicks = 0;
					wrapper.LastKnownPosition = wrapper.Actor.Location;
				}

				harvestersNeedingOrders.Push(wrapper);
			}
		}

		bool HarvestIfAble(IBot bot, HarvesterTraitWrapper h)
		{
			if (h.Actor.IsDead || !h.Actor.IsInWorld || h.Mobile == null)
				return false;

			var isStuck = false;
			if (!h.Actor.IsIdle && h.StationaryTicks >= StuckHarvesterThreshold)
			{
				isStuck = true;
				foreach (var refinery in refineries.Actors)
				{
					if (refinery.IsDead || !refinery.IsInWorld)
						continue;
					if ((h.Actor.Location - refinery.Location).LengthSquared <= StuckNearRefineryRadius * StuckNearRefineryRadius)
					{
						isStuck = false;
						break;
					}
				}
			}

			if (!h.Actor.IsIdle && !isStuck)
			{
				if (h.Actor.CurrentActivity is not CNFindAndDeliverResources act || !act.LastSearchFailed)
					return false;
			}

			if (h.NoResourcesCooldown > 1)
			{
				h.NoResourcesCooldown--;
				return false;
			}

			if (h.Parachutable != null && h.Parachutable.IsInAir)
				return false;

			if (isStuck)
			{
				AIUtils.BotDebug($"CN AI: Harvester {h.Actor} appears deadlocked at {h.Actor.Location}. Re-issuing harvest order.");
				h.StationaryTicks = 0;
			}

			// Clear stale assignment before scoring so this harvester doesn't penalise its own old refinery.
			harvesterRefineryAssignment.Remove(h.Actor);
			var refineryAnchor = FindBestRefineryAnchor(h.Actor, h);
			var newSafeResourcePatch = FindNextResource(h.Actor, h, refineryAnchor);
			AIUtils.BotDebug($"CN AI: Harvester {h.Actor} is idle. Ordering to {newSafeResourcePatch} in search for new resources.");
			if (newSafeResourcePatch.Type != TargetType.Invalid)
			{
				bot.QueueOrder(new Order("Harvest", h.Actor, newSafeResourcePatch, false));
				var assignedRefinery = refineryAnchor;
				if (h.DockClientManager != null && newSafeResourcePatch.Type == TargetType.Terrain)
					assignedRefinery = FindBestRefineryForResource(h.Actor, h.DockClientManager, world.Map.CellContaining(newSafeResourcePatch.CenterPosition), out _);

				if (assignedRefinery != null)
					harvesterRefineryAssignment[h.Actor] = assignedRefinery;
			}
			else
				h.NoResourcesCooldown = Info.ScanIntervalMultiplerWhenNoResources;

			return true;
		}

		Target FindNextResource(Actor actor, HarvesterTraitWrapper harv, Actor refineryAnchor)
		{
			var scanFromActor = refineryAnchor ?? actor;

			var targets = resourceTypesByCell
				.Where(kvp =>
					harv.Harvester.Info.Resources.Contains(kvp.Value) &&
					claimLayer.CanClaimCell(actor, kvp.Key))
				.Select(kvp => kvp.Key);

			var avoidanceCostForBin = new Dictionary<int2, int>();
			var cellRadius = Info.HarvesterEnemyAvoidanceRadius.Length / 1024;
			var minCellCost = harv.Mobile.Locomotor.Info.TerrainSpeeds.Values.Min(ti => ti.Cost);
			var cellCostMultiplier = Info.HarvesterEnemyAvoidanceCostMultipler;

			static int2 CellToBin(CPos cell, int radius) => new(cell.X / radius, cell.Y / radius);

			static int CalculateAvoidanceCostForBin(World world, int2 bin, int radius, Actor actor, int minCellCost, int multiplier)
			{
				var r = WDist.FromCells(radius);
				var vec = new WVec(r, r, WDist.Zero);
				var originCell = new CPos(bin.X * radius + radius / 2, bin.Y * radius + radius / 2);
				var origin = world.Map.CenterOfCell(originCell);
				var threatActors = world.ActorMap.ActorsInBox(origin - vec, origin + vec)
					.Where(u => !u.IsDead && actor.Owner.RelationshipWith(u.Owner) == PlayerRelationship.Enemy);

				return threatActors.Count() * minCellCost * multiplier;
			}

			var path = harv.Mobile.PathFinder.FindPathToTargetCells(
				actor, scanFromActor.Location, targets, BlockedByActor.Stationary,
				loc =>
				{
					var bin = CellToBin(loc, cellRadius);
					if (avoidanceCostForBin.TryGetValue(bin, out var avoidanceCost))
						return avoidanceCost;

					avoidanceCost = CalculateAvoidanceCostForBin(world, bin, cellRadius, actor, minCellCost, cellCostMultiplier);
					avoidanceCostForBin.Add(bin, avoidanceCost);
					return avoidanceCost;
				});

			if (path.Count == 0)
				return Target.Invalid;

			return Target.FromCell(world, path[0]);
		}

		Actor FindBestRefineryAnchor(Actor actor, HarvesterTraitWrapper harv)
		{
			var dockClientManager = harv?.DockClientManager ?? actor.TraitOrDefault<DockClientManager>();
			return FindBestRefineryAnchor(actor, dockClientManager, out _);
		}

		Actor FindBestRefineryAnchor(Actor actor, DockClientManager dockClientManager, out int bestScore)
		{
			Actor best = null;
			bestScore = int.MinValue;

			foreach (var refinery in refineries.Actors)
			{
				if (refinery.IsDead || !refinery.IsInWorld)
					continue;

				var dock = refinery.TraitsImplementing<IDockHost>()
					.FirstOrDefault(host => dockClientManager != null && dockClientManager.CanDockAt(refinery, host, false, true));
				if (dock == null)
					continue;

				var score = 0;
				var indice = resourceMapModule?.FindClosestIndiceFromCPos(refinery.Location);
				if (indice != null)
				{
					score += indice.ResourceCellsCount * 16;
					score -= indice.PlayerHarvetserCount * Info.ResourceCellsPerHarvester * 8;
					score -= indice.PlayerRefineryCount * 40;

					if (indice.HasRespawningResourceSource)
						score += Info.RespawningFieldBonus;
				}

				score -= dock.ReservationCount * Info.RefineryOccupancyPenalty;
				var persistentAssigned = harvesterRefineryAssignment.Values.Count(r => r == refinery);
				score -= persistentAssigned * Info.RefineryOccupancyPenalty;
				var busyHarvesterCount = CountBusyHarvestersNearRefinery(refinery, actor);
				score -= busyHarvesterCount * Info.BusyRefineryNearbyHarvesterPenalty;
				if (busyHarvesterCount >= Math.Max(1, Info.BusyRefineryHardAvoidThreshold))
					score -= Info.BusyRefineryHardAvoidPenalty;
				score -= (actor.Location - refinery.Location).LengthSquared * 2;

				if (score > bestScore)
				{
					bestScore = score;
					best = refinery;
				}
			}

			return best ?? dockClientManager?.ClosestDock(null, ignoreOccupancy: true)?.Actor;
		}

		Actor FindBestRefineryForResource(Actor actor, DockClientManager dockClientManager, CPos resourceCell, out int bestScore)
		{
			Actor best = null;
			bestScore = int.MinValue;
			Actor bestFree = null;
			var bestFreeDistance = int.MaxValue;

			foreach (var refinery in refineries.Actors)
			{
				if (refinery.IsDead || !refinery.IsInWorld)
					continue;

				var dock = refinery.TraitsImplementing<IDockHost>()
					.FirstOrDefault(host => dockClientManager != null && dockClientManager.CanDockAt(refinery, host, false, true));
				if (dock == null)
					continue;

				var score = 0;
				var indice = resourceMapModule?.FindClosestIndiceFromCPos(refinery.Location);
				if (indice != null && indice.HasRespawningResourceSource)
					score += Info.RespawningFieldBonus / 2;

				var dockPressure = dock.ReservationCount;
				var persistentAssigned = harvesterRefineryAssignment.Values.Count(r => r == refinery);
				var busyHarvesterCount = CountBusyHarvestersNearRefinery(refinery, actor);
				dockPressure += persistentAssigned;

				score -= dockPressure * Info.RefineryOccupancyPenalty;
				if (busyHarvesterCount >= Math.Max(1, Info.BusyRefineryHardAvoidThreshold))
					score -= Info.BusyRefineryHardAvoidPenalty;
				score -= busyHarvesterCount * Info.BusyRefineryNearbyHarvesterPenalty;
				score -= (resourceCell - refinery.Location).LengthSquared * 8;

				var fieldDistance = (resourceCell - refinery.Location).LengthSquared;
				if (dockPressure == 0 && busyHarvesterCount == 0 && fieldDistance < bestFreeDistance)
				{
					bestFreeDistance = fieldDistance;
					bestFree = refinery;
				}

				if (score > bestScore)
				{
					bestScore = score;
					best = refinery;
				}
			}

			if (bestFree != null && best != null)
			{
				var bestDistance = (resourceCell - best.Location).LengthSquared;
				var bestBusy = CountBusyHarvestersNearRefinery(best, actor);
				var bestDock = best.TraitsImplementing<IDockHost>()
					.FirstOrDefault(host => dockClientManager != null && dockClientManager.CanDockAt(best, host, false, true));

				if (bestDock != null &&
					(bestBusy > 0 || bestDock.ReservationCount > 0 || harvesterRefineryAssignment.Values.Any(r => r == best)) &&
					bestFreeDistance <= bestDistance + Info.FreeRefineryDistanceSlack)
				{
					return bestFree;
				}
			}

			return best ?? dockClientManager?.ClosestDock(null, ignoreOccupancy: true)?.Actor;
		}

		public Actor ChooseBestRefineryForDelivery(Actor harvester, CPos resourceCell)
		{
			if (harvester == null || harvester.IsDead || !harvester.IsInWorld)
				return null;

			var dockClientManager = harvester.TraitOrDefault<DockClientManager>();
			return FindBestRefineryForResource(harvester, dockClientManager, resourceCell, out _);
		}

		int ScoreRefineryForHarvester(Actor refinery, Actor actor, DockClientManager dockClientManager)
		{
			if (refinery == null || refinery.IsDead || !refinery.IsInWorld || dockClientManager == null)
				return int.MinValue;

			var dock = refinery.TraitsImplementing<IDockHost>()
				.FirstOrDefault(host => dockClientManager.CanDockAt(refinery, host, false, true));
			if (dock == null)
				return int.MinValue;

			var score = 0;
			var indice = resourceMapModule?.FindClosestIndiceFromCPos(refinery.Location);
			if (indice != null)
			{
				score += indice.ResourceCellsCount * 16;
				score -= indice.PlayerHarvetserCount * Info.ResourceCellsPerHarvester * 8;
				score -= indice.PlayerRefineryCount * 40;

				if (indice.HasRespawningResourceSource)
					score += Info.RespawningFieldBonus;
			}

			score -= dock.ReservationCount * Info.RefineryOccupancyPenalty;
			var persistentAssigned = harvesterRefineryAssignment.Values.Count(r => r == refinery);
			score -= persistentAssigned * Info.RefineryOccupancyPenalty;
			var busyHarvesterCount = CountBusyHarvestersNearRefinery(refinery, actor);
			score -= busyHarvesterCount * Info.BusyRefineryNearbyHarvesterPenalty;
			if (busyHarvesterCount >= Math.Max(1, Info.BusyRefineryHardAvoidThreshold))
				score -= Info.BusyRefineryHardAvoidPenalty;
			score -= (actor.Location - refinery.Location).LengthSquared * 2;
			return score;
		}

		void ReassignCongestedDockingHarvesters(IBot bot)
		{
			foreach (var h in harvesters.Values)
			{
				if (h.Actor.IsDead || !h.Actor.IsInWorld || h.DockClientManager == null || h.Mobile == null)
					continue;

				if (h.Parachutable != null && h.Parachutable.IsInAir)
					continue;

				// Only touch harvesters that are currently trying to deliver a load.
				if (h.Harvester.IsEmpty || h.Actor.CurrentActivity is not CNFindAndDeliverResources)
					continue;

				var currentDock = h.DockClientManager.ReservedHostActor;
				if (currentDock == null)
					continue;

				// Do not reroute harvesters that have already reached the dock area or are unloading.
				var currentHost = h.DockClientManager.ReservedHost;
				if (currentHost != null)
				{
					var dockCell = world.Map.CellContaining(currentHost.DockPosition);
					if ((h.Actor.Location - dockCell).LengthSquared <= 4)
						continue;
				}

				var currentScore = ScoreRefineryForHarvester(currentDock, h.Actor, h.DockClientManager);
				var bestDock = FindBestRefineryForResource(h.Actor, h.DockClientManager, h.Actor.Location, out var bestScore);
				if (bestDock == null || bestDock == currentDock)
					continue;

				if (bestScore < currentScore + Info.DockReassignmentScoreThreshold)
					continue;

				AIUtils.BotDebug($"CN AI: Rerouting full harvester {h.Actor} from congested refinery {currentDock} to {bestDock}.");
				bot.QueueOrder(new Order("Dock", h.Actor, Target.FromActor(bestDock), false));
			}
		}

		int CountBusyHarvestersNearRefinery(Actor refinery, Actor requester)
		{
			var count = 0;
			var radius = WDist.FromCells(Math.Max(1, Info.BusyRefineryNearbyHarvesterRadius));
			foreach (var pair in world.ActorsWithTrait<Harvester>())
			{
				var harvActor = pair.Actor;
				if (harvActor == requester || harvActor.Owner != player || harvActor.IsDead || !harvActor.IsInWorld)
					continue;

				var harvester = pair.Trait;
				var dockClientManager = harvActor.TraitOrDefault<DockClientManager>();
				var reservedHere = dockClientManager?.ReservedHostActor == refinery;
				var nearby = (harvActor.CenterPosition - refinery.CenterPosition).Length <= radius.Length;
				if (!reservedHere && !nearby)
					continue;

				// Treat both unloading and already-waiting harvesters as busy even after they start
				// shedding load, because they still occupy the same dock queue.
				if (harvester.IsEmpty && !reservedHere)
					continue;

				count++;
			}

			return count;
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			refineries.Dispose();
			harvestersIndex.Dispose();

			if (resourceLayer != null)
				resourceLayer.CellChanged -= ResourceCellChanged;
		}

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (respondToAttackCooldown > 0 || !Info.HarvesterTypes.Contains(self.Info.Name)
				|| e.Attacker == null || e.Attacker.IsDead || !e.Attacker.AppearsHostileTo(self))
				return;

			var parach = self.TraitOrDefault<Parachutable>();
			if (parach != null && parach.IsInAir)
				return;

			var dockClientManager = self.Trait<DockClientManager>();
			if (dockClientManager.ReservedHostActor != null)
				return;

			respondToAttackCooldown = 30;
			var scanFromActor = FindBestRefineryAnchor(self, harvesters.GetValueOrDefault(self)) ??
				dockClientManager.ClosestDock(null, forceEnter: true, ignoreOccupancy: true)?.Actor;
			if (scanFromActor != null)
				bot.QueueOrder(new Order("Dock", self, Target.FromActor(scanFromActor), false));
		}
	}
}
