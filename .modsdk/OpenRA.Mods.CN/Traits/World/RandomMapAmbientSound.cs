#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * RandomMapAmbientSound - plays one-shot ambient sounds on random map cells
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Plays random positional ambient sounds over specified terrain type cells. Attach to the world actor.")]
	public class RandomMapAmbientSoundInfo : ConditionalTraitInfo, ILobbyCustomRulesIgnore
	{
		[FieldLoader.Require]
		[Desc("Sounds to randomly pick from.")]
		public readonly ImmutableArray<string> SoundFiles = [];

		[Desc("Terrain types to use as possible sound positions. Empty means any map cell.")]
		public readonly ImmutableArray<string> TerrainTypes = [];

		[Desc("Initial delay (in ticks) before playing the first sound. Two values indicate a random delay range.")]
		public readonly ImmutableArray<int> Delay = [0];

		[Desc("Interval between playing sounds (in ticks). Two values indicate a random delay range.")]
		public readonly ImmutableArray<int> Interval = [10 * 25, 30 * 25];

		[Desc("Volume multiplier for the played sounds.")]
		public readonly float VolumeModifier = 1f;

		[Desc("Maximum number of sounds from this trait that may still be playing at the same time.")]
		public readonly int MaxActiveSounds = 2;

		public override object Create(ActorInitializer init) { return new RandomMapAmbientSound(init.Self, this); }
	}

	public class RandomMapAmbientSound : ConditionalTrait<RandomMapAmbientSoundInfo>, ITick, INotifyRemovedFromWorld
	{
		readonly ImmutableArray<CPos> cells;
		readonly HashSet<ISound> currentSounds = [];
		int ticks;

		public RandomMapAmbientSound(Actor self, RandomMapAmbientSoundInfo info)
			: base(info)
		{
			ticks = Common.Util.RandomInRange(self.World.LocalRandom, info.Delay);

			IEnumerable<CPos> allCells = self.World.Map.AllCells;
			if (info.TerrainTypes.Length > 0)
				allCells = allCells.Where(cell => info.TerrainTypes.Contains(self.World.Map.GetTerrainInfo(cell).Type));

			cells = allCells.ToImmutableArray();
		}

		void ITick.Tick(Actor self)
		{
			currentSounds.RemoveWhere(s => s == null || s.Complete);

			if (IsTraitDisabled || cells.Length < 1)
				return;

			if (--ticks > 0)
				return;

			ticks = Common.Util.RandomInRange(self.World.LocalRandom, Info.Interval);

			if (currentSounds.Count >= Info.MaxActiveSounds)
				return;

			var world = self.World;
			var cell = cells.Random(world.LocalRandom);
			var position = world.Map.CenterOfCell(cell);
			var sound = Info.SoundFiles.RandomOrDefault(world.LocalRandom);
			var played = Game.Sound.Play(SoundType.World, sound, position, Info.VolumeModifier);

			if (played != null)
				currentSounds.Add(played);
		}

		void StopSounds()
		{
			foreach (var sound in currentSounds)
				Game.Sound.StopSound(sound);

			currentSounds.Clear();
		}

		protected override void TraitEnabled(Actor self) { ticks = Common.Util.RandomInRange(self.World.LocalRandom, Info.Delay); }
		protected override void TraitDisabled(Actor self) { StopSounds(); }

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self) { StopSounds(); }
	}
}
