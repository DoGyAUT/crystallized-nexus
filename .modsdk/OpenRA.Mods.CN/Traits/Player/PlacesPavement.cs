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

		[Desc("Terrain template IDs that should be treated as the solid source tile for terrain-to-clear LAT around pavement.",
			"Keyed by a local group name, e.g. Green: 626. This lets surrounding terrain fade into the clear side of pavement LAT.")]
		public readonly Dictionary<string, ImmutableArray<ushort>> TerrainLatBaseTemplates = [];

		[Desc("Terrain LAT template IDs for the same groups as TerrainLatBaseTemplates.",
			"Must contain exactly 16 IDs per group, using the same clear-side bitmask order as TransitionTemplates.")]
		public readonly Dictionary<string, ImmutableArray<ushort>> TerrainLatTransitionTemplates = [];

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
		readonly Dictionary<ushort, string> terrainLatGroups = [];
		bool placed;

		public PlacesPavement(ActorInitializer init, PlacesPavementInfo info)
		{
			this.info = info;

			// Build a lookup set of all pavement template IDs for neighbour checks
			foreach (var t in info.PavementTemplates)
				allPavementTypes.Add(t);
			foreach (var t in info.TransitionTemplates)
				allPavementTypes.Add(t);

			foreach (var kv in info.TerrainLatBaseTemplates)
			{
				if (!info.TerrainLatTransitionTemplates.TryGetValue(kv.Key, out var transitions) || transitions.Length != 16)
					continue;

				foreach (var t in kv.Value)
					terrainLatGroups[t] = kv.Key;
				foreach (var t in transitions)
					terrainLatGroups[t] = kv.Key;
			}
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
			var forceSolidCells = new HashSet<CPos>(innerCells);
			foreach (var cell in allCells)
			{
				if (map.Ramp[cell] == 0 && IsPavement(cell, map))
				{
					targetPavementCells.Add(cell);

					// Existing pavement inside the new placement area is shared between buildings.
					// Keep it solid instead of re-LATing it as an outer edge.
					forceSolidCells.Add(cell);
				}

				if (IsPavement(cell, map) || !CanPaveCell(cell, map, terrainInfo))
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
				var updatePavementCells = CollectPavementUpdateCells(map, allCells, targetPavementCells);
				foreach (var cell in updatePavementCells)
					SetTransitionTile(self, map, cell, updatePavementCells, forceSolidCells);

				SetAdjacentTerrainTransitions(map, updatePavementCells);
			}

			placed = true;
		}

		bool CanPaveCell(CPos cell, Map map, ITerrainInfo terrainInfo)
		{
			if (!map.Contains(cell))
				return false;

			if (map.Ramp[cell] != 0)
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

		void SetTransitionTile(Actor self, Map map, CPos cell, HashSet<CPos> targetPavementCells, HashSet<CPos> forceSolidCells)
		{
			var mask = CalculateClearSideMask(cell, map, targetPavementCells);
			if (mask == 0 || forceSolidCells.Contains(cell))
			{
				var templateId = info.PavementTemplates[self.World.SharedRandom.Next(info.PavementTemplates.Length)];
				map.Tiles[cell] = new TerrainTile(templateId, 0);
				return;
			}

			map.Tiles[cell] = new TerrainTile(info.TransitionTemplates[mask], 0);
		}

		HashSet<CPos> CollectPavementUpdateCells(Map map, HashSet<CPos> allCells, HashSet<CPos> targetPavementCells)
		{
			var updateCells = new HashSet<CPos>(targetPavementCells);
			var queue = new Queue<(CPos Cell, int Distance)>();

			foreach (var cell in allCells)
			{
				if (!map.Contains(cell))
					continue;

				queue.Enqueue((cell, 0));
			}

			var visited = new HashSet<CPos>(allCells);
			var maxDistance = info.Padding + 2;
			while (queue.Count > 0)
			{
				var (cell, distance) = queue.Dequeue();
				if (distance >= maxDistance)
					continue;

				foreach (var neighbor in NeighborCells(cell))
				{
					if (!map.Contains(neighbor) || !visited.Add(neighbor))
						continue;

					if (map.Ramp[neighbor] != 0)
						continue;

					if (IsPavement(neighbor, map))
					{
						updateCells.Add(neighbor);
						queue.Enqueue((neighbor, distance + 1));
					}
				}
			}

			return updateCells;
		}

		void SetAdjacentTerrainTransitions(Map map, HashSet<CPos> targetPavementCells)
		{
			if (terrainLatGroups.Count == 0)
				return;

			var adjacentTerrainCells = new HashSet<CPos>();
			foreach (var cell in targetPavementCells)
			{
				foreach (var neighbor in CardinalNeighborCells(cell))
				{
					if (!map.Contains(neighbor) || IsTargetPavement(neighbor, map, targetPavementCells))
						continue;

					if (map.Ramp[neighbor] != 0)
						continue;

					if (terrainLatGroups.ContainsKey(map.Tiles[neighbor].Type))
						adjacentTerrainCells.Add(neighbor);
				}
			}

			foreach (var cell in adjacentTerrainCells)
				SetTerrainTransitionTile(map, cell, targetPavementCells);
		}

		void SetTerrainTransitionTile(Map map, CPos cell, HashSet<CPos> targetPavementCells)
		{
			if (!terrainLatGroups.TryGetValue(map.Tiles[cell].Type, out var group))
				return;

			if (!info.TerrainLatTransitionTemplates.TryGetValue(group, out var transitions) || transitions.Length != 16)
				return;

			var mask = CalculateTerrainClearSideMask(cell, map, targetPavementCells, group);
			if (mask == 0)
				return;

			if (!originalTiles.ContainsKey(cell))
				originalTiles[cell] = map.Tiles[cell];

			map.Tiles[cell] = new TerrainTile(transitions[mask], 0);
		}

		int CalculateTerrainClearSideMask(CPos cell, Map map, HashSet<CPos> targetPavementCells, string group)
		{
			var mask = 0;

			if (IsClearSideForTerrainLat(cell + new CVec(0, -1), map, targetPavementCells, group))
				mask |= 1;
			if (IsClearSideForTerrainLat(cell + new CVec(1, 0), map, targetPavementCells, group))
				mask |= 2;
			if (IsClearSideForTerrainLat(cell + new CVec(0, 1), map, targetPavementCells, group))
				mask |= 4;
			if (IsClearSideForTerrainLat(cell + new CVec(-1, 0), map, targetPavementCells, group))
				mask |= 8;

			return mask;
		}

		bool IsClearSideForTerrainLat(CPos cell, Map map, HashSet<CPos> targetPavementCells, string group)
		{
			if (!map.Contains(cell))
				return true;

			if (IsTargetPavement(cell, map, targetPavementCells))
				return true;

			return !terrainLatGroups.TryGetValue(map.Tiles[cell].Type, out var otherGroup) || otherGroup != group;
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

		static IEnumerable<CPos> CardinalNeighborCells(CPos cell)
		{
			yield return new CPos(cell.X, cell.Y - 1);
			yield return new CPos(cell.X + 1, cell.Y);
			yield return new CPos(cell.X, cell.Y + 1);
			yield return new CPos(cell.X - 1, cell.Y);
		}
	}
}
