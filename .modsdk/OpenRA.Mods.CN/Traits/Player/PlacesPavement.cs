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

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Automatically places pavement terrain tiles under a building when it is constructed.",
		"Uses LAT (Land-Art Transition) tiles for smooth edges between pavement and surrounding terrain.",
		"Supports multiple inner template IDs for visual variation.",
		"Optionally removes pavement when the building is sold or destroyed.")]
	public class PlacesPavementInfo : TraitInfo, Requires<BuildingInfo>
	{
		[FieldLoader.Require]
		[Desc("Terrain template IDs to use for inner (solid) pavement tiles.",
			"Multiple IDs are picked randomly for visual variation.",
			"These must match valid 1x1 Template IDs from the tileset.",
			"Temperate: 582, 583, 584, 585. Snow: 1032, 1033, 1034, 1035.")]
		public readonly ImmutableArray<ushort> PavementTemplates = default;

		[Desc("Terrain template IDs for LAT transition tiles (plat01-plat16).",
			"Must contain exactly 16 IDs, one for each clear-side bitmask combination.",
			"Order: bit 0 = north side is clear, bit 1 = east side is clear,",
			"bit 2 = south side is clear, bit 3 = west side is clear.",
			"Temperate: 596-611. Snow: 1046-1061.",
			"Leave empty to disable LAT transitions (hard edges).")]
		public readonly ImmutableArray<ushort> TransitionTemplates = default;

		[Desc("Number of extra cells around the building footprint to pave.",
			"0 = only footprint, 1 = footprint + 1 ring around it, etc.")]
		public readonly int Padding = 1;

		[Desc("Whether to restore original terrain when the building is sold.")]
		public readonly bool RemoveOnSelling = true;

		[Desc("Whether to restore original terrain when the building is destroyed.")]
		public readonly bool RemoveOnDeath = false;

		[Desc("Terrain types that are allowed to be paved over.",
			"Leave empty to allow paving over any terrain type not in ForbiddenTerrainTypes.")]
		public readonly HashSet<string> AllowedTerrainTypes = [];

		[Desc("Terrain types that should never be paved over (takes priority over AllowedTerrainTypes).")]
		public readonly HashSet<string> ForbiddenTerrainTypes = ["Water", "Bridge", "Cliff", "Rock"];

		public override object Create(ActorInitializer init) { return new PlacesPavement(init, this); }
	}

	public class PlacesPavement : INotifyCreated, INotifySold, INotifyKilled
	{
		readonly PlacesPavementInfo info;
		readonly Dictionary<CPos, TerrainTile> originalTiles = [];
		readonly HashSet<ushort> allPavementTypes = [];
		bool placed;

		public PlacesPavement(ActorInitializer init, PlacesPavementInfo info)
		{
			this.info = info;

			// Build a lookup set of all pavement template IDs for neighbour checks
			foreach (var t in info.PavementTemplates)
				allPavementTypes.Add(t);
			foreach (var t in info.TransitionTemplates)
				allPavementTypes.Add(t);
		}

		void INotifyCreated.Created(Actor self)
		{
			PlacePavementTiles(self);
		}

		void PlacePavementTiles(Actor self)
		{
			if (placed)
				return;

			if (info.PavementTemplates.Length == 0)
				return;

			var map = self.World.Map;
			var terrainInfo = map.Rules.TerrainInfo;
			var buildingInfo = self.Info.TraitInfo<BuildingInfo>();
			var footprintCells = buildingInfo.Tiles(self.Location).ToHashSet();

			// Calculate all cells to pave (footprint + padding)
			var innerCells = new HashSet<CPos>(footprintCells);
			var allCells = new HashSet<CPos>(footprintCells);

			if (info.Padding > 0)
			{
				for (var p = 0; p < info.Padding; p++)
				{
					var expanded = new HashSet<CPos>(allCells);
					foreach (var cell in allCells)
					{
						foreach (var neighbor in NeighborCells(cell))
						{
							if (map.Contains(neighbor))
								expanded.Add(neighbor);
						}
					}

					// Inner cells are everything except the outermost ring
					if (p < info.Padding - 1 || info.TransitionTemplates.Length == 0)
						innerCells = [.. expanded];

					allCells = expanded;
				}
			}

			// If no transition templates, all cells are inner
			if (info.TransitionTemplates.Length == 0)
				innerCells = allCells;

			// First pass: set inner pavement tiles and store originals
			var targetPavementCells = new HashSet<CPos>();
			foreach (var cell in allCells)
			{
				if (IsPavement(cell, map))
				{
					targetPavementCells.Add(cell);
					continue;
				}

				if (!CanPaveCell(cell, map, terrainInfo))
					continue;

				originalTiles[cell] = map.Tiles[cell];
				targetPavementCells.Add(cell);

				if (innerCells.Contains(cell))
				{
					// Inner cell: random solid pavement tile
					var templateId = info.PavementTemplates[self.World.SharedRandom.Next(info.PavementTemplates.Length)];
					map.Tiles[cell] = new TerrainTile(templateId, 0);
				}
			}

			// Second pass: set transition tiles on border cells
			if (info.TransitionTemplates.Length == 16)
			{
				// Find the border ring (cells that are paved but have non-paved neighbours)
				var borderCells = allCells.Except(innerCells).Where(c => originalTiles.ContainsKey(c));

				foreach (var cell in borderCells)
					SetTransitionTile(self, map, cell, targetPavementCells);

				// Also update inner edge cells that border non-paved terrain
				foreach (var cell in innerCells.Where(c => originalTiles.ContainsKey(c)))
				{
					var mask = CalculateClearSideMask(cell, map, targetPavementCells);

					// Not fully surrounded by pavement
					if (mask > 0)
						SetTransitionTile(self, map, cell, targetPavementCells);
				}
			}

			placed = true;
		}

		bool CanPaveCell(CPos cell, Map map, ITerrainInfo terrainInfo)
		{
			if (!map.Contains(cell))
				return false;

			var currentTile = map.Tiles[cell];
			var tileTerrainIndex = terrainInfo.GetTerrainIndex(currentTile);
			var terrainType = terrainInfo.TerrainTypes[tileTerrainIndex].Type;

			// Skip forbidden terrain
			if (info.ForbiddenTerrainTypes.Count > 0 && info.ForbiddenTerrainTypes.Contains(terrainType))
				return false;

			// Check allowed terrain (if specified)
			if (info.AllowedTerrainTypes.Count > 0 && !info.AllowedTerrainTypes.Contains(terrainType))
				return false;

			// Skip cells that already have pavement
			if (allPavementTypes.Contains(currentTile.Type))
				return false;

			return true;
		}

		/// <summary>
		/// Calculate a 4-bit bitmask based on which cardinal sides are clear terrain.
		/// Bit layout:
		///   bit 0 (1) = North side is clear
		///   bit 1 (2) = East side is clear
		///   bit 2 (4) = South side is clear
		///   bit 3 (8) = West side is clear
		/// Result: 0 = fully surrounded by pavement, 15 = standalone pavement.
		/// </summary>
		int CalculateClearSideMask(CPos cell, Map map, HashSet<CPos> targetPavementCells)
		{
			var mask = 0;

			if (!IsTargetPavement(cell + new CVec(0, -1), map, targetPavementCells))
				mask |= 1;
			if (!IsTargetPavement(cell + new CVec(1, 0), map, targetPavementCells))
				mask |= 2;
			if (!IsTargetPavement(cell + new CVec(0, 1), map, targetPavementCells))
				mask |= 4;
			if (!IsTargetPavement(cell + new CVec(-1, 0), map, targetPavementCells))
				mask |= 8;

			return mask;
		}

		void SetTransitionTile(Actor self, Map map, CPos cell, HashSet<CPos> targetPavementCells)
		{
			var mask = CalculateClearSideMask(cell, map, targetPavementCells);
			if (mask == 0)
			{
				var templateId = info.PavementTemplates[self.World.SharedRandom.Next(info.PavementTemplates.Length)];
				map.Tiles[cell] = new TerrainTile(templateId, 0);
				return;
			}

			map.Tiles[cell] = new TerrainTile(info.TransitionTemplates[mask], 0);
		}

		bool IsTargetPavement(CPos cell, Map map, HashSet<CPos> targetPavementCells)
		{
			if (!map.Contains(cell))
				return false;

			return targetPavementCells.Contains(cell) || IsPavement(cell, map);
		}

		bool IsPavement(CPos cell, Map map)
		{
			if (!map.Contains(cell))
				return false;

			return allPavementTypes.Contains(map.Tiles[cell].Type);
		}

		void RestoreTerrain(Actor self)
		{
			if (!placed || originalTiles.Count == 0)
				return;

			var map = self.World.Map;

			foreach (var kvp in originalTiles)
			{
				if (!map.Contains(kvp.Key))
					continue;

				// Only restore if it's still our pavement
				if (allPavementTypes.Contains(map.Tiles[kvp.Key].Type))
					map.Tiles[kvp.Key] = kvp.Value;
			}

			originalTiles.Clear();
		}

		void INotifySold.Selling(Actor self)
		{
			if (info.RemoveOnSelling)
				RestoreTerrain(self);
		}

		void INotifySold.Sold(Actor self) { }

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (info.RemoveOnDeath)
				RestoreTerrain(self);
		}

		static IEnumerable<CPos> NeighborCells(CPos cell)
		{
			yield return new CPos(cell.X - 1, cell.Y - 1);
			yield return new CPos(cell.X, cell.Y - 1);
			yield return new CPos(cell.X + 1, cell.Y - 1);
			yield return new CPos(cell.X - 1, cell.Y);
			yield return new CPos(cell.X + 1, cell.Y);
			yield return new CPos(cell.X - 1, cell.Y + 1);
			yield return new CPos(cell.X, cell.Y + 1);
			yield return new CPos(cell.X + 1, cell.Y + 1);
		}
	}
}
