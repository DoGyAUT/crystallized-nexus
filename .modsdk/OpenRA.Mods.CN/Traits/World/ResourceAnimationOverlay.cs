#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * ResourceAnimationOverlay - plays sparkle animations randomly over Tiberium cells
 * Based on TerrainTileAnimation by The OpenHV Developers
 */
#endregion

using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Plays sparkle animations randomly over Tiberium resource cells. Attach to the world actor.")]
	public class ResourceAnimationOverlayInfo : TraitInfo, ILobbyCustomRulesIgnore, Requires<IResourceLayerInfo>
	{
		[Desc("Resource types to apply the sparkle effect to.")]
		public readonly ImmutableArray<string> ResourceTypes = ["Tiberium", "BlueTiberium", "RedTiberium"];

		[Desc("Average time (ticks) between sparkles. Min and max.")]
		public readonly ImmutableArray<int> Interval = [3 * 25, 8 * 25];

		[Desc("Delay (in ticks) before the first sparkle appears.")]
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

		public override object Create(ActorInitializer init) { return new ResourceAnimationOverlay(init.Self, this); }
	}

	public class ResourceAnimationOverlay : ITick
	{
		readonly ResourceAnimationOverlayInfo info;
		readonly IResourceLayer resourceLayer;
		readonly bool hasAdditional;
		ImmutableArray<CPos> cells;
		int ticks;
		bool cellsCached;

		public ResourceAnimationOverlay(Actor self, ResourceAnimationOverlayInfo info)
		{
			this.info = info;
			ticks = info.InitialDelay;
			resourceLayer = self.Trait<IResourceLayer>();
			resourceLayer.CellChanged += (_, _) => cellsCached = false;
			hasAdditional = info.AdditionalImage != null && info.AdditionalSequences.Length > 0 && info.AdditionalPalette != null;
		}

		void CacheCells(World world)
		{
			cells = world.Map.AllCells
				.Where(cell =>
				{
					var resource = resourceLayer.GetResource(cell);
					return resource.Type != null && info.ResourceTypes.Contains(resource.Type);
				})
				.ToImmutableArray();

			cellsCached = true;
		}

		void ITick.Tick(Actor self)
		{
			var world = self.World;

			if (!cellsCached)
				CacheCells(world);

			if (cells.Length < 1)
				return;

			if (--ticks > 0)
				return;

			ticks = Common.Util.RandomInRange(world.LocalRandom, info.Interval);

			var cell = cells.Random(world.LocalRandom);

			var resource = resourceLayer.GetResource(cell);
			if (resource.Type == null || !info.ResourceTypes.Contains(resource.Type))
				return;

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

			world.AddFrameEndTask(w => w.Add(new SpriteEffect(position, w, image, sequence, palette, visibleThroughFog: true)));
		}
	}
}
