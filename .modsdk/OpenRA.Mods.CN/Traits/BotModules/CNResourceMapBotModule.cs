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
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	public class CNResourceMapBotModuleInfo : ConditionalTraitInfo, NotBefore<IResourceLayerInfo>
	{
		[Desc("Harvestable and valuable resource types.")]
		public readonly FrozenSet<string> ValuableResourceTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what types are considered resource creator.")]
		public readonly FrozenSet<string> ResourceCreatorTypes = FrozenSet<string>.Empty;

		[Desc($"Actor types that are considered refineries for {nameof(HarvesterTypes)}.")]
		public readonly FrozenSet<string> RefineryTypes = FrozenSet<string>.Empty;

		[Desc($"Actor types that are considered harvesters for {nameof(ValuableResourceTypes)}.")]
		public readonly FrozenSet<string> HarvesterTypes = FrozenSet<string>.Empty;

		[Desc("Actor types that are considered to be the base building for expansion. Other enemy units will also be recorded.")]
		public readonly FrozenSet<string> EnemyBaseBuildingTypes = FrozenSet<string>.Empty;

		[Desc("Delay (in ticks) for updating the indices.")]
		public readonly int UpdateResourceMapInverval = 67;

		[Desc("The size (in cells) of half of the side length of the indice (in square).")]
		public readonly int ResourceMapStrideRadius = 12;

		public override object Create(ActorInitializer init) { return new CNResourceMapBotModule(init.Self, this); }
	}

	public class CNResourceIndice
	{
		public int2 IndiceIndex;
		public CPos IndiceCenter;
		public int ResourceCellsCount;
		public CPos ResourceCellsCenter;
		public CPos[] ResourceCreatorLocs;
		public int PlayerRefineryCount;
		public int PlayerHarvetserCount;
		public int EnemyUnitCount;
		public int EnemyBaseCount;
		public int FriendlyBaseCount;
		public int FriendlyUnitCount;
		public bool HasRespawningResourceSource;

		public CNResourceIndice(int2 indiceIndex, CPos indiceCenter)
		{
			IndiceIndex = indiceIndex;
			IndiceCenter = indiceCenter;
			ResourceCreatorLocs = [];
		}
	}

	public class CNResourceMapBotModule : ConditionalTrait<CNResourceMapBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		IResourceLayer resourceLayer;

		CNResourceIndice[] resourceMapIndices;
		readonly int indiceSideLength;
		readonly int indiceResourceScanRadius;
		int resourceMapIndicesColumnCount;
		int resourceMapIndicesRowCount;

		int updateResourceMapIndex;
		int updateResourceMapInterval;
		bool firstTick = true;

		public CNResourceMapBotModule(Actor self, CNResourceMapBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			indiceSideLength = info.ResourceMapStrideRadius << 1;
			indiceResourceScanRadius = info.ResourceMapStrideRadius * 12 / 10;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (firstTick)
			{
				resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
				updateResourceMapInterval = world.LocalRandom.Next(Info.UpdateResourceMapInverval, Info.UpdateResourceMapInverval << 1);

				if (resourceMapIndices == null && resourceLayer != null)
				{
					var map = world.Map;
					var actualMapWidth = map.Bounds.Width;
					var actualMapHeight = map.Bounds.Height;
					var xoffset = map.Bounds.X;
					var yoffset = map.Bounds.Y;

					resourceMapIndicesColumnCount = (actualMapWidth + indiceSideLength - 1) / indiceSideLength;
					resourceMapIndicesRowCount = (actualMapHeight + indiceSideLength - 1) / indiceSideLength;

					var overallIndiceWidth = resourceMapIndicesColumnCount * indiceSideLength;
					var overallIndiceHeight = resourceMapIndicesRowCount * indiceSideLength;

					xoffset -= (overallIndiceWidth - actualMapWidth) >> 1;
					yoffset -= (overallIndiceHeight - actualMapHeight) >> 1;

					resourceMapIndices = Exts.MakeArray(resourceMapIndicesColumnCount * resourceMapIndicesRowCount,
						i => new CNResourceIndice(
							new int2(i % resourceMapIndicesColumnCount, i / resourceMapIndicesColumnCount),
							new MPos(
								xoffset + i % resourceMapIndicesColumnCount * indiceSideLength + (indiceSideLength >> 1),
								yoffset + i / resourceMapIndicesColumnCount * indiceSideLength + (indiceSideLength >> 1)).ToCPos(map)));

					for (var i = 0; i < resourceMapIndices.Length; i++)
						UpdateResourceMap(i);
				}

				firstTick = false;
			}

			if (resourceMapIndices == null || resourceMapIndices.Length == 0)
				return;

			if (--updateResourceMapInterval <= 0)
			{
				updateResourceMapInterval = Info.UpdateResourceMapInverval;
				UpdateResourceMap(updateResourceMapIndex);
				updateResourceMapIndex = (updateResourceMapIndex + 1) % resourceMapIndices.Length;
			}
		}

		void UpdateResourceMap(int index)
		{
			if (resourceLayer == null || resourceMapIndices == null || resourceMapIndices.Length == 0)
				return;

			var indice = resourceMapIndices[index];
			indice.FriendlyBaseCount = 0;
			indice.FriendlyUnitCount = 0;

			var sumCellsX = 0;
			var sumCellsY = 0;

			var resTiles = world.Map.FindTilesInAnnulus(indice.IndiceCenter, 0, indiceResourceScanRadius).Where(c =>
			{
				if (!Info.ValuableResourceTypes.Contains(resourceLayer.GetResource(c).Type))
					return false;

				sumCellsX += c.X;
				sumCellsY += c.Y;
				return true;
			}).ToList();

			var resTilesCount = resTiles.Count;
			var bestCell = CPos.Zero;
			if (resTilesCount != 0)
			{
				var resAvgCell = new CPos(sumCellsX / resTilesCount, sumCellsY / resTilesCount);
				bestCell = resTiles[0];
				var bestDist = (bestCell - resAvgCell).LengthSquared;
				foreach (var c in resTiles)
				{
					var dist = (c - resAvgCell).LengthSquared;
					if (dist < bestDist)
					{
						bestDist = dist;
						bestCell = c;
					}
				}
			}

			var refineryCount = 0;
			var harvesterCount = 0;
			var normalEnemyCount = 0;
			var highThreatEnemyCount = 0;
			var hasRespawningSource = false;

			var resourceCreatorLocs = world.FindActorsInCircle(world.Map.CenterOfCell(indice.IndiceCenter), WDist.FromCells(indiceResourceScanRadius))
				.Where(a =>
				{
					if (a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy)
					{
						if (Info.EnemyBaseBuildingTypes.Contains(a.Info.Name))
							highThreatEnemyCount++;
						else
							normalEnemyCount++;
					}
					else if (a.Owner.RelationshipWith(player) == PlayerRelationship.Ally)
					{
						if (Info.EnemyBaseBuildingTypes.Contains(a.Info.Name))
							indice.FriendlyBaseCount++;
						else
							indice.FriendlyUnitCount++;

						if (a.Owner == player)
						{
							if (Info.RefineryTypes.Contains(a.Info.Name))
								refineryCount++;

							if (Info.HarvesterTypes.Contains(a.Info.Name))
								harvesterCount++;
						}
					}

					if (!Info.ResourceCreatorTypes.Contains(a.Info.Name))
						return false;

					if (a.TraitOrDefault<ISeedableResource>() != null)
						hasRespawningSource = true;

					return true;
				}).Select(a => a.Location).ToArray();

			indice.ResourceCellsCount = resTilesCount;
			indice.ResourceCellsCenter = bestCell;
			indice.ResourceCreatorLocs = resourceCreatorLocs;
			indice.PlayerRefineryCount = refineryCount;
			indice.PlayerHarvetserCount = harvesterCount;
			indice.EnemyUnitCount = normalEnemyCount;
			indice.EnemyBaseCount = highThreatEnemyCount;
			indice.HasRespawningResourceSource = hasRespawningSource;
		}

		public int GetIndicesLength() => resourceMapIndices?.Length ?? 0;

		public int GetIndiceSideLength() => indiceSideLength;

		public int GetIndiceColumnCount() => resourceMapIndicesColumnCount;

		public int GetIndiceRowCount() => resourceMapIndicesRowCount;

		public int GetIndiceScanRadius() => indiceResourceScanRadius;

		public (int IndiceCount, int EnemyUnitCount, int EnemyBaseCount) GetNearbyIndicesThreat(int index)
		{
			var baseIndice = resourceMapIndices[index];

			var indiceCount = 0;
			var nearbyEnemyBase = 0;
			var nearbyEnemyUnit = 0;

			var x = baseIndice.IndiceIndex.X;
			var y = baseIndice.IndiceIndex.Y;
			var offsets = new[] { -1, 0, 1 };

			for (var i = 0; i < offsets.Length; i++)
			{
				// Bound the column before folding into the flat index — otherwise a column that
				// walks off the left/right edge silently wraps into the previous/next row instead
				// of being excluded, corrupting the threat score for edge-of-map indices.
				var nx = x + offsets[i];
				if (nx < 0 || nx >= resourceMapIndicesColumnCount)
					continue;

				for (var j = 0; j < offsets.Length; j++)
				{
					var ny = y + offsets[j];
					if (ny < 0 || ny >= resourceMapIndicesRowCount)
						continue;

					var offsetIndex = nx + ny * resourceMapIndicesColumnCount;
					if (offsetIndex == index || offsetIndex < 0 || offsetIndex >= resourceMapIndices.Length)
						continue;

					var indice = resourceMapIndices[offsetIndex];
					nearbyEnemyBase += indice.EnemyBaseCount - indice.FriendlyBaseCount;
					nearbyEnemyUnit += indice.EnemyUnitCount - indice.FriendlyUnitCount;
					indiceCount++;
				}
			}

			return (indiceCount, Math.Max(nearbyEnemyUnit, 0), Math.Max(nearbyEnemyBase, 0));
		}

		public CNResourceIndice GetIndice(int i)
		{
			if (resourceMapIndices == null || i < 0 || i >= resourceMapIndices.Length)
				return null;

			return resourceMapIndices[i];
		}

		public CNResourceIndice FindClosestIndiceFromCPos(CPos cpos)
		{
			if (resourceMapIndices == null || resourceMapIndices.Length == 0)
				return null;

			var maxDist = int.MaxValue;
			var best = 0;

			for (var i = 0; i < resourceMapIndices.Length; i++)
			{
				var index = resourceMapIndices[i];
				var dist = (index.IndiceCenter - cpos).LengthSquared;
				if (dist < maxDist)
				{
					maxDist = dist;
					best = i;
				}
			}

			return GetIndice(best);
		}

		public bool HasRespawningResources(CPos cpos)
		{
			return FindClosestIndiceFromCPos(cpos)?.HasRespawningResourceSource ?? false;
		}
	}
}
