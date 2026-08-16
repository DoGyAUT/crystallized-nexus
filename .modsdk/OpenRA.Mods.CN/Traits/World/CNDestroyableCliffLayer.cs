#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Places a CNDestroyableCliff actor over every destroyable-cliff template found on the map, the",
		"same way LegacyBridgeLayer places bridges. Mappers only paint the tiles; every map that already",
		"uses the Tiberian Sun \"Destroyable Cliffs\" set gets working cliffs without being touched.")]
	public class CNDestroyableCliffLayerInfo : TraitInfo
	{
		[ActorReference]
		[Desc("Cliff actor types to place. Each names the templates it stands in for in its own",
			"CNDestroyableCliff trait.")]
		public readonly ImmutableArray<string> CliffActorTypes = [];

		[Desc("Percentage of a template's cells that must still be intact on the map before it counts as a",
			"cliff at all. Mappers use single tiles of these templates as decoration - one snow cell on",
			"`tiers`, eight on `1ice6` - and a fragment collapsed with a full-size ramp blit would retile",
			"ground that was never cliff. Whole cliffs are painted whole, so anything far short of complete",
			"is scenery and left alone.")]
		public readonly int MinimumTemplateCoverage = 75;

		public override object Create(ActorInitializer init) { return new CNDestroyableCliffLayer(this); }
	}

	public class CNDestroyableCliffLayer : IWorldLoaded
	{
		readonly CNDestroyableCliffLayerInfo info;

		CellLayer<Actor> cliffs;

		public CNDestroyableCliffLayer(CNDestroyableCliffLayerInfo info)
		{
			this.info = info;
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			if (info.CliffActorTypes.Length == 0)
				return;

			if (w.Map.Rules.TerrainInfo is not ITemplatedTerrainInfo terrainInfo)
				throw new InvalidDataException("CNDestroyableCliffLayer requires a template-based tileset.");

			cliffs = new CellLayer<Actor>(w.Map);

			// Which actor stands in for which template. Built from the actors rather than configured here so
			// the two halves cannot drift apart: an actor that names a template is the actor placed on it.
			var cliffTypes = new Dictionary<ushort, string>();
			foreach (var actorType in info.CliffActorTypes)
			{
				if (!w.Map.Rules.Actors.TryGetValue(actorType.ToLowerInvariant(), out var actorInfo))
					throw new YamlException($"CNDestroyableCliffLayer references undefined actor type '{actorType}'.");

				var cliffInfo = actorInfo.TraitInfoOrDefault<CNDestroyableCliffInfo>()
					?? throw new YamlException($"CNDestroyableCliffLayer actor '{actorType}' has no CNDestroyableCliff trait.");

				foreach (var template in cliffInfo.Templates)
					cliffTypes[template] = actorType;
			}

			// Only the templates this tile set actually defines are worth scanning for - the snow ids mean
			// nothing on a temperate map, and vice versa.
			var present = cliffTypes.Keys.Where(terrainInfo.Templates.ContainsKey).ToHashSet();
			if (present.Count == 0)
				return;

			var placed = 0;
			foreach (var cell in w.Map.AllCells)
				if (present.Contains(w.Map.Tiles[cell].Type))
					placed += PlaceCliff(w, terrainInfo, cliffTypes, cell) ? 1 : 0;

			Log.Write("debug", $"CNDestroyableCliffLayer: placed {placed} destroyable cliffs.");
		}

		bool PlaceCliff(World w, ITemplatedTerrainInfo terrainInfo, Dictionary<ushort, string> cliffTypes, CPos cell)
		{
			// Already covered by an actor placed from one of the earlier cells of the same template.
			if (cliffs[cell] != null)
				return false;

			// Walk back from this subtile's index to where the template starts, so the actor sits on the
			// template origin no matter which of its cells the scan reached first.
			var mapTile = w.Map.Tiles[cell];
			var template = terrainInfo.Templates[mapTile.Type];
			var origin = new CPos(
				cell.X - mapTile.Index % template.Size.X,
				cell.Y - mapTile.Index / template.Size.X);

			var cells = new List<CPos>();
			var defined = 0;
			for (var index = 0; index < template.Size.X * template.Size.Y; index++)
			{
				// Templates leave gaps where their artwork has none - dcliff01 defines twenty of its
				// twenty-four frames - and a gap is not a cell anybody failed to paint.
				if (template[index] == null)
					continue;

				defined++;

				var subCell = origin + new CVec(index % template.Size.X, index / template.Size.X);
				if (!w.Map.Contains(subCell))
					continue;

				// A cell of the right template at the right index. Anything else here means the mapper has
				// painted over part of the cliff, and that cell is not ours to collapse.
				var subTile = w.Map.Tiles[subCell];
				if (subTile.Type != mapTile.Type || subTile.Index != index)
					continue;

				cells.Add(subCell);
			}

			if (defined == 0 || cells.Count * 100 < defined * info.MinimumTemplateCoverage)
				return false;

			// The actor sits in the middle of the cliff, not on the template origin. Everything measured
			// from an actor's centre gets twice as big when the centre is a corner: the hit shape needed to
			// cover the whole cliff would reach 5500 world units, and ActorMap.LargestActorRadius - the
			// slack added to EVERY area-damage query in the game, on every map, whether or not it has
			// cliffs - is the largest hit shape in the ruleset. Centring keeps it under what the mod's
			// biggest building already costs.
			var anchor = CentreCell(w, cells);

			var actor = w.CreateActor(cliffTypes[mapTile.Type],
			[
				new LocationInit(anchor),
				new OwnerInit(w.WorldActor.Owner),
			]);

			actor.Trait<CNDestroyableCliff>().Create(origin, [.. cells]);

			foreach (var subCell in cells)
				cliffs[subCell] = actor;

			return true;
		}

		/// <summary>
		/// The footprint cell closest to the middle of the cliff. Ties go to the first in the scan's own
		/// row-major order, so the same cliff always resolves to the same cell - the hit shape and the
		/// targetable offsets in the rules are written against it.
		/// </summary>
		static CPos CentreCell(World w, List<CPos> cells)
		{
			var sum = WVec.Zero;
			foreach (var cell in cells)
				sum += w.Map.CenterOfCell(cell) - WPos.Zero;

			var mean = WPos.Zero + sum / cells.Count;

			var best = cells[0];
			var bestDistance = long.MaxValue;
			foreach (var cell in cells)
			{
				var distance = (w.Map.CenterOfCell(cell) - mean).HorizontalLengthSquared;
				if (distance >= bestDistance)
					continue;

				bestDistance = distance;
				best = cell;
			}

			return best;
		}

		/// <summary>The cliff covering a cell, or null. Null everywhere before the world has loaded.</summary>
		public Actor GetCliffAt(CPos cell) =>
			cliffs != null && cliffs.Contains(cell) ? cliffs[cell] : null;
	}
}
