#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.MapGenerator;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Warheads
{
	[TraitLocation(SystemActors.World)]
	[Desc("Exposes terrain deformation as a lobby option.")]
	public class TerrainDeformationOptionsInfo : TraitInfo, ILobbyOptions
	{
		public const string OptionId = "terraindeformation";

		[Desc("Descriptive label for the terrain deformation checkbox in the lobby.")]
		public readonly string CheckboxLabel = "Terrain Deformation";

		[Desc("Tooltip description for the terrain deformation checkbox in the lobby.")]
		public readonly string CheckboxDescription = "Allow explosions to deform terrain and retile nearby ramps.";

		[Desc("Default value of the terrain deformation checkbox in the lobby.")]
		public readonly bool CheckboxEnabled = true;

		[Desc("Prevent the terrain deformation state from being changed in the lobby.")]
		public readonly bool CheckboxLocked = false;

		[Desc("Whether to display the terrain deformation checkbox in the lobby.")]
		public readonly bool CheckboxVisible = true;

		[Desc("Display order for the terrain deformation checkbox in the lobby.")]
		public readonly int CheckboxDisplayOrder = 10;

		[Desc("Options category in which to display the terrain deformation checkbox in the lobby.")]
		public readonly string CheckboxCategory = null;

		public static bool Enabled(World world)
		{
			return world.LobbyInfo.GlobalSettings.OptionOrDefault(OptionId, true);
		}

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, OptionId,
				CheckboxLabel, CheckboxDescription, CheckboxVisible, CheckboxDisplayOrder, CheckboxEnabled, CheckboxLocked, CheckboxCategory);
		}

		public override object Create(ActorInitializer init) { return new object(); }
	}

	[Desc("Changes terrain height around an impact, simulating Tiberian Sun-style terrain deformation.")]
	public class TerrainDeformationWarhead : Warhead
	{
		[Desc("Size of the area. Terrain will be deformed in each tile.",
			"Provide 2 values for a ring effect (outer/inner).")]
		public readonly ImmutableArray<int> Size = [0, 0];

		[Desc("Percentage chance the terrain is deformed.")]
		public readonly int Chance = 100;

		[Desc("Height delta applied to affected cells.",
			"Negative values dig craters, positive values raise terrain.")]
		public readonly int HeightDelta = -1;

		[Desc("Minimum terrain height after applying HeightDelta.")]
		public readonly int MinHeight = 0;

		[Desc("Maximum terrain height after applying HeightDelta.",
			"-1 uses the map grid's MaximumTerrainHeight.")]
		public readonly int MaxHeight = -1;

		[Desc("Terrain types that may be deformed.",
			"Leave empty to allow all terrain types not in ForbiddenTerrainTypes.")]
		public readonly HashSet<string> AllowedTerrainTypes = [];

		[Desc("Terrain types that must never be deformed.")]
		public readonly HashSet<string> ForbiddenTerrainTypes = ["Water", "Bridge", "Cliff", "Rock", "Impassable", "Road", "Rail"];

		[Desc("Allow deformation on ramp tiles.")]
		public readonly bool AllowRamps = false;

		[Desc("Skip cells occupied by actors.")]
		public readonly bool BlockedByActors = true;

		[Desc("Terrain template ids used for ramp retile. Empty disables ramp retile.")]
		public readonly ImmutableArray<ushort> RampTemplates =
			[0, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57];

		[Desc("Extra cells around the deformation area that may be retiled into ramps.")]
		public readonly int RampRetileRadius = 2;

		[Desc("When digging at MinHeight, raise a rim around the impact area to simulate a deeper crater.")]
		public readonly bool RaiseRimAtMinHeight = true;

		[Desc("Rim height above MinHeight when RaiseRimAtMinHeight is enabled.")]
		public readonly int MinHeightRimHeight = 1;

		[Desc("Rim thickness in cells around the impact area when RaiseRimAtMinHeight is enabled.")]
		public readonly int MinHeightRimThickness = 2;

		[Desc("Terrain template IDs that should keep their LAT group after deformation.",
			"Keyed by a local group name, e.g. Rough: 150.")]
		public readonly Dictionary<string, ImmutableArray<ushort>> TerrainLatBaseTemplates = new()
		{
			["Rough"] = [150],
			["Sand"] = [535],
			["Green"] = [626],
		};

		[Desc("Terrain LAT template IDs for the same groups as TerrainLatBaseTemplates.",
			"Must contain exactly 16 IDs per group, using bit 0 = north, bit 1 = east, bit 2 = south, bit 3 = west.")]
		public readonly Dictionary<string, ImmutableArray<ushort>> TerrainLatTransitionTemplates = new()
		{
			["Rough"] = [151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164, 165, 166],
			["Sand"] = [536, 537, 538, 539, 540, 541, 542, 543, 544, 545, 546, 547, 548, 549, 550, 551],
			["Green"] = [627, 628, 629, 630, 631, 632, 633, 634, 635, 636, 637, 638, 639, 640, 641, 642],
		};

		[Desc("Extra cells around ramp retile cells that should be repainted with terrain LAT.")]
		public readonly int TerrainLatRepaintRadius = 1;

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			if (target.Type == TargetType.Invalid || HeightDelta == 0)
				return;

			var firedBy = args.SourceActor;
			var world = firedBy.World;

			if (!TerrainDeformationOptionsInfo.Enabled(world))
				return;

			if (world.Map.Grid.MaximumTerrainHeight == 0)
				return;

			if (Chance < world.SharedRandom.Next(100))
				return;

			var pos = target.CenterPosition;
			if (world.Map.DistanceAboveTerrain(pos) > AirThreshold)
				return;

			var targetTile = world.Map.CellContaining(pos);
			var minRange = Size.Length > 1 && Size[1] > 0 ? Size[1] : 0;
			var maxRange = Size.Length > 0 ? Size[0] : 0;
			var allCells = world.Map.FindTilesInAnnulus(targetTile, minRange, maxRange);
			var maxHeight = MaxHeight < 0 ? world.Map.Grid.MaximumTerrainHeight : MaxHeight;
			var heightChanges = new Dictionary<CPos, byte>();

			foreach (var cell in allCells)
			{
				if (!CanDeformCell(cell, firedBy))
					continue;

				var currentHeight = world.Map.Height[cell];
				var nextHeight = (currentHeight + HeightDelta).Clamp(MinHeight, maxHeight);
				if (nextHeight == currentHeight)
					continue;

				heightChanges[cell] = (byte)nextHeight;
			}

			if (heightChanges.Count == 0 && RaiseRimAtMinHeight && HeightDelta < 0)
				AddMinHeightRim(targetTile, maxRange, maxHeight, firedBy, heightChanges);

			if (heightChanges.Count == 0)
				return;

			if (RampTemplates.Length == 0 || !TryRetileRamps(world, firedBy, heightChanges))
				ApplyHeightChangesAndTerrainLat(world, firedBy, heightChanges);
		}

		void ApplyHeightChanges(World world, Dictionary<CPos, byte> heightChanges)
		{
			foreach (var (cell, height) in heightChanges)
				world.Map.Height[cell] = height;
		}

		void ApplyHeightChangesAndTerrainLat(World world, Actor firedBy, Dictionary<CPos, byte> heightChanges)
		{
			ApplyHeightChanges(world, heightChanges);

			var terrainLatGroups = BuildTerrainLatGroups(world.Map);
			if (terrainLatGroups.Count == 0)
				return;

			ApplyTerrainLat(world, CollectRetileCells(world.Map, firedBy, heightChanges.Keys), terrainLatGroups);
		}

		void AddMinHeightRim(
			CPos targetTile,
			int craterRadius,
			int maxHeight,
			Actor firedBy,
			Dictionary<CPos, byte> heightChanges)
		{
			var rimHeight = (MinHeight + MinHeightRimHeight).Clamp(MinHeight, maxHeight);
			if (rimHeight <= MinHeight)
				return;

			var rimInnerRadius = Math.Max(1, craterRadius + 1);
			var rimOuterRadius = rimInnerRadius + Math.Max(0, MinHeightRimThickness - 1);
			foreach (var cell in firedBy.World.Map.FindTilesInAnnulus(targetTile, rimInnerRadius, rimOuterRadius))
			{
				if (!CanDeformCell(cell, firedBy))
					continue;

				if (firedBy.World.Map.Height[cell] >= rimHeight)
					continue;

				heightChanges[cell] = (byte)rimHeight;
			}
		}

		bool TryRetileRamps(World world, Actor firedBy, Dictionary<CPos, byte> heightChanges)
		{
			var map = world.Map;
			var tileable = CollectRetileCells(map, firedBy, heightChanges.Keys);

			if (tileable.Count == 0)
				return false;

			var terrainLatGroups = BuildTerrainLatGroups(map);
			var rampTiler = new RampTiler(map, RampTemplates);
			var heightMap = new RampTiler.HeightMap(map);

			heightMap.MarkUntileable(map.AllCells.Where(c => !tileable.Contains(c)));
			SeedCurrentCornerHeights(heightMap, map, tileable, heightChanges);
			rampTiler.PullHeightMap(heightMap);

			var adjustmentMode = HeightDelta < 0
				? RampTiler.AdjustmentMode.Minimal
				: RampTiler.AdjustmentMode.Maximal;
			if (!heightMap.Constrain(adjustmentMode))
				return false;

			var brush = rampTiler.TileHeightMap(heightMap, world.SharedRandom);
			if (brush == null)
				return false;

			try
			{
				brush.Paint(map, [], CPos.Zero, 0, MultiBrush.Replaceability.Tile, world.SharedRandom);
				ApplyTerrainLat(world, tileable, terrainLatGroups);
				return true;
			}
			catch (ArgumentException)
			{
				return false;
			}
		}

		HashSet<CPos> CollectRetileCells(Map map, Actor firedBy, IEnumerable<CPos> origins)
		{
			var tileable = new HashSet<CPos>();
			var retileRadius = Math.Max(0, RampRetileRadius);

			foreach (var origin in origins)
				foreach (var cell in map.FindTilesInAnnulus(origin, 0, retileRadius))
					if (CanRetileCell(cell, firedBy))
						tileable.Add(cell);

			return tileable;
		}

		void SeedCurrentCornerHeights(
			RampTiler.HeightMap heightMap,
			Map map,
			IEnumerable<CPos> cells,
			IReadOnlyDictionary<CPos, byte> heightChanges)
		{
			var heightStep = map.Grid.TileScale / 2;
			var cornerHeights = new Dictionary<int2, byte>();

			foreach (var cell in cells)
			{
				var baseHeight = heightChanges.TryGetValue(cell, out var changedHeight)
					? changedHeight
					: map.Height[cell];
				var ramp = map.Grid.Ramps[map.Ramp[cell]];
				var xy = heightMap.CPosToXy(cell);

				AddCorner(xy, baseHeight, ramp.Corners[0].Z / heightStep, cornerHeights);
				AddCorner(xy + new int2(1, 0), baseHeight, ramp.Corners[1].Z / heightStep, cornerHeights);
				AddCorner(xy + new int2(1, 1), baseHeight, ramp.Corners[2].Z / heightStep, cornerHeights);
				AddCorner(xy + new int2(0, 1), baseHeight, ramp.Corners[3].Z / heightStep, cornerHeights);
			}

			foreach (var (xy, height) in cornerHeights)
				if (heightMap.Target.ContainsXY(xy))
					heightMap.Target[xy] = height;
		}

		void AddCorner(int2 xy, byte baseHeight, int rampHeight, Dictionary<int2, byte> cornerHeights)
		{
			var height = (byte)Math.Clamp(baseHeight + rampHeight, byte.MinValue, byte.MaxValue);
			if (!cornerHeights.TryGetValue(xy, out var existing))
			{
				cornerHeights.Add(xy, height);
				return;
			}

			cornerHeights[xy] = HeightDelta < 0
				? Math.Min(existing, height)
				: Math.Max(existing, height);
		}

		bool CanRetileCell(CPos cell, Actor firedBy)
		{
			var map = firedBy.World.Map;
			if (!map.Contains(cell))
				return false;

			var terrainType = map.GetTerrainInfo(cell).Type;
			if (ForbiddenTerrainTypes.Count > 0 && ForbiddenTerrainTypes.Contains(terrainType))
				return false;

			if (AllowedTerrainTypes.Count > 0 && !AllowedTerrainTypes.Contains(terrainType))
				return false;

			if (BlockedByActors && firedBy.World.ActorMap.GetActorsAt(cell).Any())
				return false;

			return true;
		}

		Dictionary<ushort, string> BuildTerrainLatGroups(Map map)
		{
			var groups = new Dictionary<ushort, string>();
			foreach (var kv in TerrainLatBaseTemplates)
			{
				if (!TerrainLatTransitionTemplates.TryGetValue(kv.Key, out var transitions) || transitions.Length != 16)
					continue;

				foreach (var template in kv.Value)
					if (IsValidTemplate(map, template))
						groups[template] = kv.Key;

				foreach (var template in transitions)
					if (IsValidTemplate(map, template))
						groups[template] = kv.Key;
			}

			return groups;
		}

		void ApplyTerrainLat(
			World world,
			HashSet<CPos> cells,
			Dictionary<ushort, string> terrainLatGroups)
		{
			if (terrainLatGroups.Count == 0)
				return;

			var map = world.Map;
			var repaintCells = new HashSet<CPos>();
			var radius = Math.Max(0, TerrainLatRepaintRadius);
			foreach (var origin in cells)
				foreach (var cell in map.FindTilesInAnnulus(origin, 0, radius))
					if (map.Contains(cell))
						repaintCells.Add(cell);

			foreach (var cell in repaintCells)
			{
				if (map.Ramp[cell] != 0)
					continue;

				var group = GetTerrainLatGroup(map, cell, terrainLatGroups);
				if (group == null)
					continue;

				if (!TerrainLatBaseTemplates.TryGetValue(group, out var baseTemplates) || baseTemplates.Length == 0)
					continue;
				if (!TerrainLatTransitionTemplates.TryGetValue(group, out var transitions) || transitions.Length != 16)
					continue;

				var mask = CalculateTerrainLatMask(map, cell, group, terrainLatGroups);
				var template = mask == 0
					? baseTemplates[world.SharedRandom.Next(baseTemplates.Length)]
					: transitions[mask];

				if (IsValidTemplate(map, template))
					map.Tiles[cell] = new TerrainTile(template, 0);
			}
		}

		string GetTerrainLatGroup(
			Map map,
			CPos cell,
			Dictionary<ushort, string> terrainLatGroups)
		{
			return terrainLatGroups.TryGetValue(map.Tiles[cell].Type, out var group) ? group : null;
		}

		int CalculateTerrainLatMask(
			Map map,
			CPos cell,
			string group,
			Dictionary<ushort, string> terrainLatGroups)
		{
			var mask = 0;
			if (!IsSameTerrainLatGroup(map, cell + new CVec(0, -1), group, terrainLatGroups))
				mask |= 1;
			if (!IsSameTerrainLatGroup(map, cell + new CVec(1, 0), group, terrainLatGroups))
				mask |= 2;
			if (!IsSameTerrainLatGroup(map, cell + new CVec(0, 1), group, terrainLatGroups))
				mask |= 4;
			if (!IsSameTerrainLatGroup(map, cell + new CVec(-1, 0), group, terrainLatGroups))
				mask |= 8;

			return mask;
		}

		bool IsSameTerrainLatGroup(
			Map map,
			CPos cell,
			string group,
			Dictionary<ushort, string> terrainLatGroups)
		{
			if (!map.Contains(cell))
				return false;

			return GetTerrainLatGroup(map, cell, terrainLatGroups) == group;
		}

		bool IsValidTemplate(Map map, ushort template)
		{
			return map.Rules.TerrainInfo.TryGetTerrainInfo(new TerrainTile(template, 0), out _);
		}

		bool CanDeformCell(CPos cell, Actor firedBy)
		{
			var map = firedBy.World.Map;
			if (!map.Contains(cell))
				return false;

			if (!AllowRamps && map.Ramp[cell] != 0)
				return false;

			var terrainType = map.GetTerrainInfo(cell).Type;
			if (ForbiddenTerrainTypes.Count > 0 && ForbiddenTerrainTypes.Contains(terrainType))
				return false;

			if (AllowedTerrainTypes.Count > 0 && !AllowedTerrainTypes.Contains(terrainType))
				return false;

			if (BlockedByActors && firedBy.World.ActorMap.GetActorsAt(cell).Any())
				return false;

			return true;
		}
	}
}
