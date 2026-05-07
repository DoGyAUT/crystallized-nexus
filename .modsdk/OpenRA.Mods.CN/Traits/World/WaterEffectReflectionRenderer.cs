#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Water reflections for transient world effects such as explosions, projectiles, and contrails.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Clones transient world-effect renderables as subtle water reflections.")]
	public sealed class WaterEffectReflectionRendererInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Terrain types that should receive the reflection.")]
		public readonly ImmutableArray<string> TerrainTypes = ["Water"];

		[Desc("Tint color and alpha for reflected effects.")]
		public readonly Color ReflectionColor = Color.FromArgb(58, 120, 170, 220);

		[Desc("Reflection position offset relative to the effect projection.")]
		public readonly WVec Offset = WVec.Zero;

		[Desc("Reflection Z offset relative to the source renderable.")]
		public readonly int ZOffset = -8;

		[Desc("Also allow reflections when water is within this many cells of the effect position.")]
		public readonly int WaterProbeRadius = 0;

		[Desc("Reflect contrail line renderables in addition to sprite renderables.")]
		public readonly bool ReflectContrails = true;

		public override object Create(ActorInitializer init) { return new WaterEffectReflectionRenderer(this); }
	}

	public sealed class WaterEffectReflectionRenderer : IRender
	{
		readonly WaterEffectReflectionRendererInfo info;
		readonly float3 reflectionColor;
		readonly float reflectionAlpha;

		public WaterEffectReflectionRenderer(WaterEffectReflectionRendererInfo info)
		{
			this.info = info;
			reflectionColor = new float3(info.ReflectionColor.R, info.ReflectionColor.G, info.ReflectionColor.B) / 255f;
			reflectionAlpha = info.ReflectionColor.A / 255f;
		}

		IEnumerable<IRenderable> IRender.Render(Actor self, WorldRenderer wr)
		{
			var map = self.World.Map;
			var effects = self.World.UnpartitionedEffects
				.Concat(self.World.ScreenMap.RenderableEffectsInBox(wr.Viewport.TopLeft, wr.Viewport.BottomRight));

			foreach (var effect in effects)
			{
				foreach (var renderable in effect.Render(wr))
				{
					var reflection = WaterReflectionRenderableUtils.Reflect(renderable, map, info.TerrainTypes,
						info.Offset, info.ZOffset, info.WaterProbeRadius, reflectionColor, reflectionAlpha, false, info.ReflectContrails);

					if (reflection != null)
						yield return reflection;
				}
			}
		}

		IEnumerable<Rectangle> IRender.ScreenBounds(Actor self, WorldRenderer wr)
		{
			yield break;
		}
	}
}
