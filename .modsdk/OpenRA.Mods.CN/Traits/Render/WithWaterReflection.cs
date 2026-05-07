#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Actor-level water reflection render modifier.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Clones the actor renderable as a subtle reflection when above water terrain.")]
	public sealed class WithWaterReflectionInfo : ConditionalTraitInfo
	{
		[Desc("Terrain types that should receive the reflection.")]
		public readonly ImmutableArray<string> TerrainTypes = ["Water"];

		[Desc("Tint color and alpha for the reflected actor.")]
		public readonly Color ReflectionColor = Color.FromArgb(82, 130, 170, 210);

		[Desc("Reflection position offset relative to the actor ground projection.")]
		public readonly WVec Offset = WVec.Zero;

		[Desc("Reflection Z offset relative to the actor renderable.")]
		public readonly int ZOffset = -6;

		[Desc("Also allow reflections when water is within this many cells of the renderable position.")]
		public readonly int WaterProbeRadius = 0;

		public override object Create(ActorInitializer init) { return new WithWaterReflection(this); }
	}

	static class WaterReflectionRenderableUtils
	{
		public static bool HasWaterNear(Map map, ImmutableArray<string> terrainTypes, WPos pos, int radius)
		{
			var center = map.CellContaining(pos);
			for (var y = -radius; y <= radius; y++)
			{
				for (var x = -radius; x <= radius; x++)
				{
					var cell = center + new CVec(x, y);
					if (map.Contains(cell) && terrainTypes.Contains(map.GetTerrainInfo(cell).Type))
						return true;
				}
			}

			return false;
		}

		public static IRenderable Reflect(
			IRenderable renderable, Map map, ImmutableArray<string> terrainTypes, WVec offset, int zOffset,
			int waterProbeRadius, in float3 reflectionColor, float reflectionAlpha, bool skipDecorations, bool reflectContrails)
		{
			if (skipDecorations && renderable.IsDecoration)
				return null;

			if (!HasWaterNear(map, terrainTypes, renderable.Pos, waterProbeRadius))
				return null;

			var height = map.DistanceAboveTerrain(renderable.Pos).Length;
			var reflectionOffset = offset - new WVec(0, 0, height);
			var reflectionZOffset = renderable.ZOffset + height + zOffset;

			if (renderable is IModifyableRenderable modifyable &&
				(modifyable.TintModifiers & TintModifiers.ReplaceColor) == 0)
			{
				var reflection = modifyable
					.WithTint(reflectionColor, TintModifiers.None)
					.WithAlpha(reflectionAlpha)
					.OffsetBy(reflectionOffset)
					.WithZOffset(reflectionZOffset)
					.AsDecoration();

				if (reflection is ModelRenderable modelReflection)
					return modelReflection.WithZReflection();

				if (reflection is SpriteRenderable spriteReflection)
					return spriteReflection.WithYFlip();

				return reflection;
			}

			if (reflectContrails && renderable is ContrailRenderable)
				return renderable
					.OffsetBy(reflectionOffset)
					.WithZOffset(reflectionZOffset);

			return null;
		}
	}

	public sealed class WithWaterReflection : ConditionalTrait<WithWaterReflectionInfo>, IRenderModifier
	{
		readonly WithWaterReflectionInfo info;
		readonly float3 reflectionColor;
		readonly float reflectionAlpha;

		public WithWaterReflection(WithWaterReflectionInfo info)
			: base(info)
		{
			this.info = info;
			reflectionColor = new float3(info.ReflectionColor.R, info.ReflectionColor.G, info.ReflectionColor.B) / 255f;
			reflectionAlpha = info.ReflectionColor.A / 255f;
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			var renderables = r.ToList();
			if (IsTraitDisabled || !Game.Settings.Graphics.WaterEffects)
				return renderables;

			var map = self.World.Map;
			var reflections = renderables
				.Select(renderable => WaterReflectionRenderableUtils.Reflect(renderable, map, info.TerrainTypes,
					info.Offset, info.ZOffset, info.WaterProbeRadius, reflectionColor, reflectionAlpha, true, false))
				.Where(reflection => reflection != null)
				.OrderBy(WorldRenderer.RenderableZPositionComparisonKey);

			return reflections.Concat(renderables);
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			var boundsList = bounds.ToList();
			if (IsTraitDisabled || !Game.Settings.Graphics.WaterEffects)
				return boundsList;

			var map = self.World.Map;
			var cell = map.CellContaining(self.CenterPosition);
			if (!map.Contains(cell) || !info.TerrainTypes.Contains(map.GetTerrainInfo(cell).Type))
				return boundsList;

			var height = self.World.Map.DistanceAboveTerrain(self.CenterPosition).Length;
			var offset = wr.ScreenPxOffset(info.Offset - new WVec(0, 0, height));
			return boundsList.Concat(boundsList.Select(r => new Rectangle(r.X + offset.X, r.Y + offset.Y, r.Width, r.Height)));
		}
	}
}
