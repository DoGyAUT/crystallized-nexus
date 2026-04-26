#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * TerrainAnimationOverlay - plays animations randomly over specified terrain type cells
 * Based on TiberiumSparkleOverlay
 */
#endregion

using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Effects;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Plays animations randomly over specified terrain type cells. Attach to the world actor.",
		"Can be used for water waves, lava glow, etc.")]
	public class TerrainAnimationOverlayInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[FieldLoader.Require]
		[Desc("Terrain types to apply the effect to (e.g. Water, Rough, Lava).")]
		public readonly ImmutableArray<string> TerrainTypes;

		[Desc("Average time (ticks) between effect spawns. Min and max.")]
		public readonly ImmutableArray<int> Interval = [3 * 25, 8 * 25];

		[Desc("Delay (in ticks) before the first effect appears.")]
		public readonly int InitialDelay = 50;

		[FieldLoader.Require]
		[Desc("Which image to use.")]
		public readonly string Image;

		[FieldLoader.Require]
		[SequenceReference(nameof(Image))]
		[Desc("Which sequences to randomly pick from.")]
		public readonly ImmutableArray<string> Sequences;

		[FieldLoader.Require]
		[PaletteReference]
		[Desc("Which palette to use.")]
		public readonly string Palette;

		[Desc("Additional image for extra effects. If set, AdditionalSequences and AdditionalPalette must also be set.")]
		public readonly string AdditionalImage = null;

		[SequenceReference(nameof(AdditionalImage), allowNullImage: true)]
		[Desc("Sequences to randomly pick from for the additional image.")]
		public readonly ImmutableArray<string> AdditionalSequences = [];

		[PaletteReference]
		[Desc("Palette to use for the additional image.")]
		public readonly string AdditionalPalette = null;

		[Desc("Chance (0-100) that the additional effect is picked instead of the primary one.")]
		public readonly int AdditionalChance = 30;

		[Desc("Whether the effect should be visible through fog of war.")]
		public readonly bool VisibleThroughFog = true;

		public override object Create(ActorInitializer init) { return new TerrainAnimationOverlay(init.Self, this); }
	}

	public class TerrainAnimationOverlay : ITick
	{
		readonly TerrainAnimationOverlayInfo info;
		readonly bool hasAdditional;

		readonly ImmutableArray<CPos> cells;
		int ticks;

		public TerrainAnimationOverlay(Actor self, TerrainAnimationOverlayInfo info)
		{
			this.info = info;
			ticks = info.InitialDelay;

			hasAdditional = info.AdditionalImage != null
				&& info.AdditionalSequences.Length > 0
				&& info.AdditionalPalette != null;

			// Cache terrain cells once — terrain doesn't change at runtime
			cells = self.World.Map.AllCells
				.Where(cell => info.TerrainTypes.Contains(self.World.Map.GetTerrainInfo(cell).Type))
				.ToImmutableArray();
		}

		void ITick.Tick(Actor self)
		{
			if (cells.Length < 1)
				return;

			if (--ticks > 0)
				return;

			var world = self.World;
			ticks = Common.Util.RandomInRange(world.LocalRandom, info.Interval);

			var cell = cells.Random(world.LocalRandom);
			var position = world.Map.CenterOfCell(cell);

			string image;
			string sequence;
			string palette;

			if (hasAdditional && world.LocalRandom.Next(100) < info.AdditionalChance)
			{
				image = info.AdditionalImage;
				sequence = info.AdditionalSequences.Random(world.LocalRandom);
				palette = info.AdditionalPalette;
			}
			else
			{
				image = info.Image;
				sequence = info.Sequences.Random(world.LocalRandom);
				palette = info.Palette;
			}

			world.AddFrameEndTask(w => w.Add(new SpriteEffect(position, w, image, sequence, palette, visibleThroughFog: info.VisibleThroughFog)));
		}
	}
}
