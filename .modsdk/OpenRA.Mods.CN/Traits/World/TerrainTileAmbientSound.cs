#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Plays a looping ambient sound at every cell matching the specified tile types and index.",
		"One sound instance is started per matching cell. Use Index: 0 to anchor to one cell per template.")]
	public class TerrainTileAmbientSoundInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[FieldLoader.Require]
		[Desc("Only active on this tileset (e.g. TEMPERATE).")]
		public readonly string Tileset;

		[FieldLoader.Require]
		[Desc("Tile template type IDs to match.")]
		public readonly ImmutableArray<ushort> Tiles;

		[Desc("Tile index within the template to anchor the sound on.")]
		public readonly byte Index = 0;

		[FieldLoader.Require]
		[Desc("Sound file to play (searched in registered audio packages).")]
		public readonly string Sound;

		[Desc("Volume multiplier.")]
		public readonly float VolumeModifier = 1f;

		public override object Create(ActorInitializer init) { return new TerrainTileAmbientSound(init.Self, this); }
	}

	public class TerrainTileAmbientSound : IWorldLoaded, INotifyRemovedFromWorld
	{
		readonly TerrainTileAmbientSoundInfo info;
		readonly List<ISound> sounds = [];
		bool active;

		public TerrainTileAmbientSound(Actor self, TerrainTileAmbientSoundInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			if (info.Tileset != w.Map.Tileset)
				return;

			active = true;

			var cells = w.Map.AllCells
				.Where(cell => info.Tiles.Contains(w.Map.Tiles[cell].Type) && w.Map.Tiles[cell].Index == info.Index);

			foreach (var cell in cells)
			{
				var pos = w.Map.CenterOfCell(cell);
				var s = Game.Sound.PlayLooped(SoundType.World, info.Sound, pos);
				if (s != null)
					sounds.Add(s);
			}
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			if (!active)
				return;

			foreach (var s in sounds)
				Game.Sound.StopSound(s);

			sounds.Clear();
		}
	}
}
