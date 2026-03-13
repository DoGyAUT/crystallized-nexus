#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * TiberiumSparkleOverlay - plays sparkle animations randomly over Tiberium cells
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
	public class TiberiumSparkleOverlayInfo : TraitInfo, ILobbyCustomRulesIgnore, Requires<IResourceLayerInfo>
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

		public override object Create(ActorInitializer init) { return new TiberiumSparkleOverlay(init.Self, this); }
	}

	public class TiberiumSparkleOverlay : ITick
	{
		readonly TiberiumSparkleOverlayInfo info;
		readonly IResourceLayer resourceLayer;
		ImmutableArray<CPos> cells;
		int ticks;
		bool cellsCached;

		public TiberiumSparkleOverlay(Actor self, TiberiumSparkleOverlayInfo info)
		{
			this.info = info;
			ticks = info.InitialDelay;
			resourceLayer = self.Trait<IResourceLayer>();
			resourceLayer.CellChanged += (_, _) => cellsCached = false;
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
			var sequence = info.Sequences.Random(world.LocalRandom);


			world.AddFrameEndTask(w => w.Add(new SpriteEffect(position, w, info.Image, sequence, info.Palette, visibleThroughFog: true)));
		}
	}
}
