#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("A chunk of cliff that can be shot away, opening a path through it. Tiberian Sun shipped the",
		"\"Destroyable Cliffs\" tile set (dcliff01/dcliff02) without a destroyed counterpart, so the",
		"collapsed state is assembled from templates the tile set already has - normally two ramp pieces",
		"covering the same footprint. Actors are placed by CNDestroyableCliffLayer, which finds the",
		"templates on the map; nothing has to be put down by hand in the editor.")]
	public class CNDestroyableCliffInfo : TraitInfo, IRulesetLoaded, Requires<IHealthInfo>
	{
		[Desc("Terrain template ids this actor stands in for. The ids differ per tile set (717/718 in",
			"temperate and desert, 987/988 in snow), the replacement ramps do not, so one actor covers all",
			"tile sets that share a cliff shape.")]
		public readonly ImmutableArray<ushort> Templates = [];

		[Desc("What the footprint is retiled with once the cliff falls, as template id keyed by the cell",
			"offset it is blitted at. Cells no template covers keep the tile they had, and anything a",
			"template would write outside the cliff's own cells is dropped - the collapse never edits ground",
			"the mapper did not paint as cliff.")]
		public readonly Dictionary<CVec, ushort> DestroyedTemplates = [];

		[Desc("Height levels added to every replacement cell on top of the automatic alignment.",
			"The blit is placed so its tallest tile matches the tallest cell of the cliff it replaces,",
			"which is right whenever the replacement reaches the plateau; use this when it does not.")]
		public readonly int HeightOffset = 0;

		[Desc("Recompute the CliffBackImpassability marking around the footprint after the collapse.",
			"That layer runs once at map load, so without this the cells the cliff used to hide stay",
			"flagged Impassable and the new path is blocked by terrain that is no longer there.")]
		public readonly bool RefreshCliffBackImpassability = true;

		[Desc("Terrain type CliffBackImpassabilityLayer marks hidden cells with. Must match the world trait.")]
		public readonly string CliffBackTerrainType = "Impassable";

		[Desc("Cells around the footprint that are re-examined for cliff-back impassability. The test looks",
			"four rows down the projection, so this needs to cover the tallest cliff plus its own footprint.")]
		public readonly int CliffBackRefreshMargin = 8;

		[Desc("Types of damage dealt to actors left standing in cells the collapse makes untenable.")]
		public readonly BitSet<DamageType> DamageTypes = default;

		public override object Create(ActorInitializer init) { return new CNDestroyableCliff(init.Self, this); }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (Templates.Length == 0)
				throw new YamlException($"{ai.Name}: CNDestroyableCliff needs at least one entry in Templates.");

			if (DestroyedTemplates.Count == 0)
				throw new YamlException($"{ai.Name}: CNDestroyableCliff needs at least one entry in DestroyedTemplates.");
		}
	}

	public class CNDestroyableCliff : INotifyKilled
	{
		readonly CNDestroyableCliffInfo info;
		readonly ITemplatedTerrainInfo terrainInfo;
		readonly Map map;

		CPos[] footprint = [];
		CPos templateOrigin;
		bool collapsed;

		/// <summary>The cells the intact cliff covers. Empty until the layer has placed this actor.</summary>
		public IReadOnlyList<CPos> Footprint => footprint;

		public CNDestroyableCliff(Actor self, CNDestroyableCliffInfo info)
		{
			this.info = info;
			map = self.World.Map;

			terrainInfo = map.Rules.TerrainInfo as ITemplatedTerrainInfo;
			if (terrainInfo == null)
				throw new InvalidDataException("CNDestroyableCliff requires a template-based tileset.");
		}

		/// <summary>
		/// Called by CNDestroyableCliffLayer once it knows which cells this cliff actually occupies. The
		/// template origin is passed separately because the actor stands in the middle of the cliff, not
		/// on the origin - the replacement blit is laid out from the origin, everything else from the actor.
		/// </summary>
		public void Create(CPos origin, CPos[] cells)
		{
			templateOrigin = origin;
			footprint = cells;
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (collapsed || footprint.Length == 0)
				return;

			collapsed = true;
			Collapse(self);
		}

		void Collapse(Actor self)
		{
			var maxHeight = map.Grid.MaximumTerrainHeight;

			// Align the blit to the cliff it replaces rather than to the ground under it: the ramp pieces
			// carry their own height profile, and their tallest tile is the one that has to meet the plateau.
			// Reading the base off the LOWEST footprint cell instead puts the whole ramp one or more levels
			// short, leaving a step at the top exactly where the new path is supposed to join.
			var highestCell = footprint.Max(c => map.Height[c]);
			var highestTile = 0;
			foreach (var (offset, templateId) in info.DestroyedTemplates)
			{
				if (!terrainInfo.Templates.TryGetValue(templateId, out var template))
					continue;

				for (var index = 0; index < template.TilesCount; index++)
					if (template[index] != null)
						highestTile = Math.Max(highestTile, template[index].Height);
			}

			var baseHeight = (highestCell - highestTile + info.HeightOffset).Clamp(0, maxHeight);
			var own = footprint.ToHashSet();

			foreach (var (offset, templateId) in info.DestroyedTemplates)
			{
				if (!terrainInfo.Templates.TryGetValue(templateId, out var template))
				{
					Log.Write("debug", $"CNDestroyableCliff on {self.Info.Name}: no template {templateId} in this tileset.");
					continue;
				}

				var origin = templateOrigin + offset;
				for (var index = 0; index < template.TilesCount; index++)
				{
					// A template only defines the frames its artwork actually uses - dcliff01 skips four of
					// its twenty-four, the ramps skip two - and the gaps come back as nulls. Those cells keep
					// whatever was there, which is what the artwork expects.
					var tile = template[index];
					if (tile == null)
						continue;

					// Only ever our own cells. A replacement template rarely lines up exactly with the cliff
					// it stands in for - the stock ramps hang a corner over the edge - and writing those
					// would retile ground next to the cliff that has nothing to do with the collapse.
					var cell = origin + new CVec(index % template.Size.X, index / template.Size.X);
					if (!own.Contains(cell))
						continue;

					// Tiles and heights are separate layers in OpenRA, unlike the tile set the artwork comes
					// from: swapping only the tile would leave the cliff's old height profile behind and the
					// ramp would render flat. Both writes notify the terrain and the locomotors themselves.
					map.Tiles[cell] = new TerrainTile(templateId, (byte)index);
					map.Height[cell] = (byte)(baseHeight + tile.Height).Clamp(0, maxHeight);
				}
			}

			if (info.RefreshCliffBackImpassability)
				RefreshCliffBackImpassability(self);

			KillTrappedActors(self);
		}

		/// <summary>
		/// Redoes CliffBackImpassabilityLayer's test over the collapsed area. That layer marks every cell a
		/// back-facing cliff visually covers as Impassable and runs exactly once, at map load - so after the
		/// cliff is gone its cells are still flagged, the locomotor still refuses them, and the path the
		/// collapse just opened does not exist as far as anything that moves is concerned.
		/// </summary>
		void RefreshCliffBackImpassability(Actor self)
		{
			var impassable = map.Rules.TerrainInfo.GetTerrainIndex(info.CliffBackTerrainType);
			var margin = Math.Max(0, info.CliffBackRefreshMargin);

			// Tunnel portals are the one place the original layer lets units stand behind a cliff, and
			// flagging one here would seal the tunnel for the rest of the match.
			var tunnelPortals = self.World.WorldActor.Info.TraitInfos<TerrainTunnelInfo>()
				.SelectMany(t => t.PortalCells())
				.ToHashSet();

			var minX = footprint.Min(c => c.X) - margin;
			var maxX = footprint.Max(c => c.X) + margin;
			var minY = footprint.Min(c => c.Y) - margin;
			var maxY = footprint.Max(c => c.Y) + margin;

			for (var y = minY; y <= maxY; y++)
			{
				for (var x = minX; x <= maxX; x++)
				{
					var cell = new CPos(x, y);
					if (!map.Contains(cell) || tunnelPortals.Contains(cell))
						continue;

					var uv = cell.ToMPos(map);
					var hidden = map.ProjectedCellsCovering(uv)
						.SelectMany(map.Unproject)
						.Any(p => p.V >= uv.V + 4);

					if (hidden)
					{
						map.CustomTerrain[cell] = impassable;
					}
					else if (map.CustomTerrain[cell] == impassable)
					{
						// Only ever clears the flag this layer sets. Anything else parked on CustomTerrain
						// here - a ground level bridge, most likely - belongs to whoever put it there.
						map.CustomTerrain[cell] = byte.MaxValue;
					}
				}
			}
		}

		/// <summary>
		/// Kills whatever the new terrain cannot hold. The collapse both opens and closes cells: a ramp tile
		/// where the map used to have flat plateau will not take a building that was standing on it.
		/// </summary>
		void KillTrappedActors(Actor self)
		{
			foreach (var cell in footprint)
			{
				foreach (var actor in self.World.ActorMap.GetActorsAt(cell).ToList())
				{
					if (actor == self || actor.IsDead || !actor.IsInWorld)
						continue;

					var positionable = actor.TraitOrDefault<IPositionable>();
					if (positionable != null && !positionable.CanExistInCell(cell))
						actor.Kill(self, info.DamageTypes);
				}
			}
		}
	}
}
