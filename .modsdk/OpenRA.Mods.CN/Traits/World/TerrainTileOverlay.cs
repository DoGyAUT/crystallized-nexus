#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Displays a continuously playing sprite overlay at every cell matching the specified tile type and index.",
		"Renders as IRenderOverlay (terrain pass) so cloud shadows and post-processing appear on top.")]
	public class TerrainTileOverlayInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[FieldLoader.Require]
		[Desc("Only active on this tileset (e.g. TEMPERATE).")]
		public readonly string Tileset;

		[FieldLoader.Require]
		[Desc("Tile template type ID.")]
		public readonly ushort Tile;

		[FieldLoader.Require]
		[Desc("Tile index within the template.")]
		public readonly byte Index;

		[FieldLoader.Require]
		[Desc("Which image to use.")]
		public readonly string Image;

		[Desc("Which sequence to use.")]
		[SequenceReference(nameof(Image))]
		public readonly string Sequence = "idle";

		[PaletteReference]
		[Desc("Which palette to use.")]
		public readonly string Palette = TileSet.TerrainPaletteInternalName;

		public override object Create(ActorInitializer init) { return new TerrainTileOverlay(init.Self, this); }
	}

	public class TerrainTileOverlay : IWorldLoaded, IRenderOverlay, ITick
	{
		readonly TerrainTileOverlayInfo info;
		readonly Dictionary<CPos, Animation> animations = [];
		PaletteReference paletteRef;
		World world;
		bool active;

		public TerrainTileOverlay(Actor self, TerrainTileOverlayInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;
			if (info.Tileset != w.Map.Tileset)
				return;

			active = true;
			paletteRef = wr.Palette(info.Palette);

			var cells = w.Map.AllCells
				.Where(cell => w.Map.Tiles[cell].Type == info.Tile && w.Map.Tiles[cell].Index == info.Index);

			foreach (var cell in cells)
			{
				var anim = new Animation(w, info.Image);
				anim.PlayRepeating(info.Sequence);
				animations[cell] = anim;
			}
		}

		void ITick.Tick(Actor self)
		{
			if (!active)
				return;

			foreach (var anim in animations.Values)
				anim.Tick();
		}

		void IRenderOverlay.Render(WorldRenderer wr)
		{
			if (!active)
				return;

			foreach (var (cell, anim) in animations)
			{
				if (world.FogObscures(cell))
					continue;

				var pos = world.Map.CenterOfCell(cell);
				foreach (var r in anim.Render(pos, paletteRef))
					r.PrepareRender(wr).Render(wr);
			}
		}
	}
}
