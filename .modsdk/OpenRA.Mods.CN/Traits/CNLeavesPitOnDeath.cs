#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Sinks the ground where this actor died into a pit: a flat floor a height level down, ringed by",
		"single-cell ramps so the hole is walkable rather than a cut-out. A killed veinhole leaves a real",
		"dent in the map, not a decal.",
		"The tiles are found in the tile set rather than named here - every Tiberian Sun tile set ships",
		"1x1 ramp pieces for all four slope directions ('Ramp edge fixup'), but under different template",
		"ids per tile set, and looking them up by their slope data covers all of them at once.")]
	public class CNLeavesPitOnDeathInfo : ConditionalTraitInfo
	{
		[Desc("Size of the whole dent, in cells: the floor plus the ramp ring around it.")]
		public readonly CVec Dimensions = new(4, 4);

		[Desc("Size of the flat floor inside the dent, in cells. Centred in Dimensions; the cells left over",
			"become the ramp ring.")]
		public readonly CVec FloorDimensions = new(2, 2);

		[Desc("Top-left corner of the dent, as a cell offset from this actor's location.")]
		public readonly CVec Offset = CVec.Zero;

		[Desc("Height levels the floor drops by. One ramp cell spans exactly one level, so anything deeper",
			"than 1 leaves a step the ring cannot bridge.")]
		public readonly int Depth = 1;

		[Desc("Terrain type the replacement tiles must have. Only 1x1 templates of this type are considered,",
			"which is what keeps the search off roads, shores and cliff pieces.")]
		public readonly string TerrainType = "Clear";

		[Desc("Clear any resources standing in the dent. Veins growing on the lip of a fresh hole read as a",
			"rendering fault more than as terrain.")]
		public readonly bool ClearResources = true;

		[Desc("Types of damage dealt to actors left standing in cells the new terrain cannot hold.")]
		public readonly BitSet<DamageType> DamageTypes = default;

		public override object Create(ActorInitializer init) { return new CNLeavesPitOnDeath(init.Self, this); }
	}

	public class CNLeavesPitOnDeath : ConditionalTrait<CNLeavesPitOnDeathInfo>, INotifyKilled
	{
		readonly Map map;
		readonly ITemplatedTerrainInfo terrainInfo;

		// Template id per slope direction, indexed by RampType 0-4 (0 being the flat floor piece).
		// Resolved from the tile set on first use; ushort.MaxValue means this tile set has no piece for it.
		ushort[] templates;
		bool sunk;

		public CNLeavesPitOnDeath(Actor self, CNLeavesPitOnDeathInfo info)
			: base(info)
		{
			map = self.World.Map;

			terrainInfo = map.Rules.TerrainInfo as ITemplatedTerrainInfo;
			if (terrainInfo == null)
				throw new InvalidDataException("CNLeavesPitOnDeath requires a template-based tileset.");
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || sunk)
				return;

			sunk = true;
			Sink(self);
		}

		void Sink(Actor self)
		{
			templates ??= ResolveTemplates();

			// Without a flat floor piece there is nothing to sink the middle to, and a ring of ramps around
			// untouched ground would be worse than leaving the map alone.
			if (templates[0] == ushort.MaxValue)
			{
				Log.Write("debug", $"CNLeavesPitOnDeath on {self.Info.Name}: tileset has no flat 1x1 {Info.TerrainType} template.");
				return;
			}

			var origin = self.Location + Info.Offset;
			var size = new CVec(Math.Max(1, Info.Dimensions.X), Math.Max(1, Info.Dimensions.Y));
			var floorSize = new CVec(
				Math.Clamp(Info.FloorDimensions.X, 1, size.X),
				Math.Clamp(Info.FloorDimensions.Y, 1, size.Y));

			// Centre the floor in the dent, biasing to the top left when the leftover ring is uneven.
			var floorOrigin = origin + new CVec((size.X - floorSize.X) / 2, (size.Y - floorSize.Y) / 2);

			var cells = new List<CPos>();
			for (var y = 0; y < size.Y; y++)
				for (var x = 0; x < size.X; x++)
					cells.Add(origin + new CVec(x, y));

			cells.RemoveAll(c => !map.Contains(c));
			if (cells.Count == 0)
				return;

			var floorCells = new HashSet<CPos>();
			for (var y = 0; y < floorSize.Y; y++)
				for (var x = 0; x < floorSize.X; x++)
					floorCells.Add(floorOrigin + new CVec(x, y));

			// Read the rim off the lowest cell in the dent so the hole is never dug out of ground that is
			// already below it - on sloped ground that would raise one side instead of lowering the other.
			var rimHeight = cells.Min(c => map.Height[c]);
			var floorHeight = (byte)Math.Clamp(rimHeight - Math.Max(0, Info.Depth), 0, map.Grid.MaximumTerrainHeight);

			var floorCentre = FloorCentre(floorCells);
			var resourceLayer = Info.ClearResources ? self.World.WorldActor.TraitOrDefault<IResourceLayer>() : null;

			foreach (var cell in cells)
			{
				var rampType = floorCells.Contains(cell) ? (byte)0 : OutwardRamp(cell, floorCentre);
				var templateId = templates[rampType];

				// A tile set missing one of the four ramp pieces would otherwise punch a flat cell into the
				// rim. Leaving the cell as it was keeps the surrounding ground intact instead.
				if (templateId == ushort.MaxValue)
					continue;

				// Tiles and heights are separate layers: writing only the tile would leave the old height
				// profile behind and the ramp would render flat. Both writes notify the terrain themselves.
				map.Tiles[cell] = new TerrainTile(templateId, 0);
				map.Height[cell] = floorHeight;

				resourceLayer?.ClearResources(cell);
			}

			KillTrappedActors(self, cells);
		}

		WPos FloorCentre(HashSet<CPos> floorCells)
		{
			long x = 0, y = 0;
			foreach (var cell in floorCells)
			{
				var pos = map.CenterOfCell(cell);
				x += pos.X;
				y += pos.Y;
			}

			return new WPos((int)(x / floorCells.Count), (int)(y / floorCells.Count), 0);
		}

		/// <summary>
		/// The ramp piece whose raised side points away from the floor. Derived from the engine's own ramp
		/// table rather than from a hand-written direction map: on the isometric grid a ramp's high side runs
		/// along a screen axis, which is a diagonal in cell coordinates, and writing that mapping out by hand
		/// is how the slope ends up pointing into the hole instead of out of it.
		/// </summary>
		byte OutwardRamp(CPos cell, WPos floorCentre)
		{
			var centre = map.CenterOfCell(cell);
			var outward = new int2(centre.X - floorCentre.X, centre.Y - floorCentre.Y);

			var best = (byte)0;
			var bestScore = long.MinValue;
			for (var rampType = (byte)1; rampType <= 4; rampType++)
			{
				if (templates[rampType] == ushort.MaxValue)
					continue;

				var high = HighSide(rampType);
				var score = (long)high.X * outward.X + (long)high.Y * outward.Y;
				if (score > bestScore)
				{
					bestScore = score;
					best = rampType;
				}
			}

			return best;
		}

		/// <summary>Direction the raised corners of a ramp sit in, in world space.</summary>
		int2 HighSide(byte rampType)
		{
			var high = int2.Zero;
			foreach (var corner in map.Grid.Ramps[rampType].Corners)
				if (corner.Z > 0)
					high += new int2(corner.X, corner.Y);

			return high;
		}

		/// <summary>
		/// The four 1x1 pieces the dent is built from, keyed by RampType. Picking the lowest matching
		/// template id keeps the choice the same for every player in a game.
		/// </summary>
		ushort[] ResolveTemplates()
		{
			var found = new ushort[5];
			for (var i = 0; i < found.Length; i++)
				found[i] = ushort.MaxValue;

			var terrainIndex = map.Rules.TerrainInfo.GetTerrainIndex(Info.TerrainType);

			foreach (var template in terrainInfo.Templates.Values.OrderBy(t => t.Id))
			{
				if (template.Size.X != 1 || template.Size.Y != 1 || template.TilesCount != 1)
					continue;

				var tile = template[0];
				if (tile == null || tile.TerrainType != terrainIndex || tile.Height != 0)
					continue;

				if (tile.RampType < found.Length && found[tile.RampType] == ushort.MaxValue)
					found[tile.RampType] = template.Id;
			}

			return found;
		}

		/// <summary>
		/// Kills whatever the new terrain cannot hold. A ramp tile where the map used to have flat ground
		/// will not take a building that was standing on it.
		/// </summary>
		void KillTrappedActors(Actor self, List<CPos> cells)
		{
			foreach (var cell in cells)
			{
				foreach (var actor in self.World.ActorMap.GetActorsAt(cell).ToList())
				{
					if (actor == self || actor.IsDead || !actor.IsInWorld)
						continue;

					var positionable = actor.TraitOrDefault<IPositionable>();
					if (positionable != null && !positionable.CanExistInCell(cell))
						actor.Kill(self, Info.DamageTypes);
				}
			}
		}
	}
}
