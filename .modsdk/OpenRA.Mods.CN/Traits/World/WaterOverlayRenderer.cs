#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Weather-reactive overlay pass for water terrain cells.
 * Blends between a clear-state shimmer and an ion-storm energy pulse.
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
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Renders a weather-reactive color overlay on water terrain cells.",
		"Blends between a clear-state shimmer and an ion-storm energy pulse",
		"based on WeatherController.Intensity. Requires WeatherController on the world actor",
		"for storm transitions; falls back to clear appearance if absent.")]
	public sealed class WaterOverlayRendererInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Terrain types to apply the overlay to.")]
		public readonly ImmutableArray<string> TerrainTypes = ["Water"];

		[Desc("Extra screen pixels around each water cell quad.")]
		public readonly int TilePadding = 2;

		[Desc("Additional tile template IDs to include in the shader regardless of terrain type.",
			"Use for bridge/structure tiles that visually sit over water.")]
		public readonly int[] AdditionalTemplates = [];

		[Desc("Palette-index ranges (min/max pairs) identifying water pixels for the Temperate tileset.")]
		public readonly int[] TemperateWaterPaletteIndexRanges = [];

		[Desc("Palette-index ranges (min/max pairs) identifying water pixels for the Snow tileset.")]
		public readonly int[] SnowWaterPaletteIndexRanges = [];

		[Desc("Tile template IDs that should use WaterPaletteIndexRanges instead of whole-cell water quads.")]
		public readonly int[] WaterPaletteMaskedTemplates = [];

		[Desc("Tile template IDs whose sprites occlude adjacent water cells.",
			"Water cells at the configured offsets relative to each matching tile are excluded from the shader.")]
		public readonly int[] CliffOcclusionTemplates = [];

		[Desc("Tile template IDs whose non-water pixels block the water shader.",
			"For each listed tile, pixels NOT matching WaterPaletteIndexRanges are rendered as a passthrough,",
			"restoring the original terrain pixels so the water effect cannot bleed onto cliff faces.")]
		public readonly int[] CliffBlockTemplates = [];

		[Desc("Cell offsets (relative to each occluding tile) to exclude from the shader.")]
		public readonly CVec[] CliffOcclusionOffsets =
		[
			new CVec(1, 0), new CVec(0, 1),                                   // SW, SE (1 step)
			new CVec(0, -1), new CVec(-1, 0),                                 // NW, NE (1 step)
			new CVec(-1, -1),                                                 // N straight (1 step)
			new CVec(-1, 1), new CVec(1, -1),                                 // W, E screen (1 step)
			new CVec(0, -2), new CVec(-1, -2), new CVec(-2, -1), new CVec(-2, 0), // NW/NE (2 steps)
			new CVec(-2, -2),                                                 // N straight (2 steps)
			new CVec(-2, 2), new CVec(2, -2),                                 // W, E screen (2 steps)
		];

		// ── Clear state ───────────────────────────────────────────────────
		[Desc("RGB tint multiplier on the water surface during clear weather.")]
		public readonly float ClearTintRed = 0.9f;
		public readonly float ClearTintGreen = 0.95f;
		public readonly float ClearTintBlue = 1.08f;

		[Desc("Maximum overlay blend strength during clear weather.")]
		public readonly float ClearAlpha = 0.06f;

		[Desc("Brightness shimmer intensity during clear weather.")]
		public readonly float ClearShimmer = 0.018f;

		[Desc("Screen-space distortion amplitude (pixels) during clear weather.")]
		public readonly float ClearDistortion = 1.2f;

		[Desc("Wave frequency for the clear-state distortion and shimmer.")]
		public readonly float ClearWaveScale = 0.03f;

		[Desc("Wave animation speed during clear weather.")]
		public readonly float ClearWaveSpeed = 0.04f;

		// ── Storm state ───────────────────────────────────────────────────
		[Desc("RGB tint multiplier on the water surface during full ion storm.")]
		public readonly float StormTintRed = 0.55f;
		public readonly float StormTintGreen = 1.05f;
		public readonly float StormTintBlue = 0.72f;

		[Desc("Maximum overlay blend strength during full ion storm.")]
		public readonly float StormAlpha = 0.28f;

		[Desc("Brightness shimmer intensity during ion storm.")]
		public readonly float StormShimmer = 0.04f;

		[Desc("Screen-space distortion amplitude (pixels) during full ion storm.")]
		public readonly float StormDistortion = 3.5f;

		[Desc("Pulsing energy brightness added on top of the storm tint.")]
		public readonly float StormPulse = 0.10f;

		[Desc("Low-frequency ion storm pulse speed.")]
		public readonly float StormPulseSpeed = 0.045f;

		[Desc("Wave frequency for the storm-state distortion.")]
		public readonly float StormWaveScale = 0.055f;

		[Desc("Wave animation speed during ion storm.")]
		public readonly float StormWaveSpeed = 0.14f;

		public override object Create(ActorInitializer init) { return new WaterOverlayRenderer(init.Self, this); }
	}

	public sealed class WaterOverlayRenderer : IRenderPostProcessPass, ITick, IWorldLoaded, INotifyActorDisposing
	{
		readonly WaterOverlayRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly RenderPostProcessPassTexturedShaderBindings bindings;
		readonly ImmutableArray<CPos> cells;
		readonly WaterSurfaceMask mask;
		readonly WaterSurfaceMask cliffMask;

		IVertexBuffer<RenderPostProcessPassTexturedVertex> buffer;
		RenderPostProcessPassTexturedVertex[] vertices = [];
		int vertexCapacity;
		int ticks;
		float intensity;
		WeatherController weatherController;

		int2 cachedTopLeft;
		int2 cachedBottomRight;
		int cachedVertexCount = -1;

		public WaterOverlayRenderer(Actor self, WaterOverlayRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			bindings = new RenderPostProcessPassTexturedShaderBindings("wateroverlay");
			shader = renderer.CreateShader(bindings);

			var map = self.World.Map;
			var paletteRanges = map.Tileset.ToUpperInvariant() == "SNOW"
				? info.SnowWaterPaletteIndexRanges
				: info.TemperateWaterPaletteIndexRanges;
			mask = new WaterSurfaceMask(map, paletteRanges, info.WaterPaletteMaskedTemplates);
			cliffMask = new WaterSurfaceMask(map, paletteRanges, info.CliffBlockTemplates, inverted: true, uvSentinel: 2f);

			var occluded = BuildOcclusionSet(map, info.CliffOcclusionTemplates, info.CliffOcclusionOffsets);

			var additionalIds = new HashSet<int>(info.AdditionalTemplates);
			cells = map.AllCells
				.Where(c =>
				{
					if (occluded.Contains(c))
						return false;
					if (mask.Enabled && mask.UsesTemplate(map.Tiles[c]))
						return false;
					if (info.TerrainTypes.Contains(map.GetTerrainInfo(c).Type))
						return true;
					if (additionalIds.Contains(map.Tiles[c].Type) && map.Height[c] == 0)
					{
						// Only include edge cells that border actual water terrain.
						return new[] { new CVec(1, 0), new CVec(-1, 0), new CVec(0, 1), new CVec(0, -1) }
							.Any(d => { var n = c + d; return map.Contains(n) && info.TerrainTypes.Contains(map.GetTerrainInfo(n).Type); });
					}

					return false;
				})
				.ToImmutableArray();
		}

		static HashSet<CPos> BuildOcclusionSet(Map map, int[] templateIds, CVec[] offsets)
		{
			var result = new HashSet<CPos>();
			if (templateIds.Length == 0 || offsets.Length == 0)
				return result;

			var ids = new HashSet<int>(templateIds);
			foreach (var cell in map.AllCells)
				if (ids.Contains(map.Tiles[cell].Type))
					foreach (var offset in offsets)
						result.Add(cell + offset);

			return result;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			weatherController = w.WorldActor.TraitOrDefault<WeatherController>();
		}

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterTerrain;
		bool IRenderPostProcessPass.Enabled => cells.Length > 0 && Game.Settings.Graphics.WaterEffects;

		void ITick.Tick(Actor self)
		{
			ticks++;
			intensity = weatherController?.Intensity ?? 0f;
		}

		bool EnsureCapacity(int required)
		{
			if (required <= vertexCapacity)
				return false;

			buffer?.Dispose();
			vertexCapacity = Exts.NextPowerOf2(required);
			vertices = new RenderPostProcessPassTexturedVertex[vertexCapacity];
			buffer = renderer.CreateVertexBuffer(bindings, vertices, true);
			return true;
		}

		static bool IntersectsViewport(int x, int y, int halfWidth, int halfHeight, int2 topLeft, int2 bottomRight, int padding)
		{
			return x + halfWidth + padding >= topLeft.X
				&& x - halfWidth - padding <= bottomRight.X
				&& y + halfHeight + padding >= topLeft.Y
				&& y - halfHeight - padding <= bottomRight.Y;
		}

		int BuildVertices(WorldRenderer wr)
		{
			var topLeft = wr.Viewport.TopLeft;
			var bottomRight = wr.Viewport.BottomRight;

			var required = cells.Length * 6
				+ (mask.Enabled ? mask.MaxVertexCount : 0)
				+ (cliffMask.Enabled ? cliffMask.MaxVertexCount : 0);
			var bufferRecreated = EnsureCapacity(required);

			if (!bufferRecreated && cachedVertexCount >= 0
				&& topLeft == cachedTopLeft && bottomRight == cachedBottomRight)
				return cachedVertexCount;

			var halfWidth = wr.TileSize.Width / 2 + info.TilePadding;
			var halfHeight = wr.TileSize.Height / 2 + info.TilePadding;
			var cullPadding = (int)System.Math.Max(info.ClearDistortion, info.StormDistortion) + info.TilePadding;

			var i = 0;
			var map = wr.World.Map;
			foreach (var cell in cells)
			{
				var center = wr.ScreenPxPosition(map.CenterOfCell(cell));
				if (!IntersectsViewport(center.X, center.Y, halfWidth, halfHeight, topLeft, bottomRight, cullPadding))
					continue;

				var left = center.X - halfWidth;
				var right = center.X + halfWidth;
				var top = center.Y - halfHeight;
				var bottom = center.Y + halfHeight;

				vertices[i++] = new RenderPostProcessPassTexturedVertex(left, top, -1, -1);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(right, top, 1, -1);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(right, bottom, 1, 1);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(right, bottom, 1, 1);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(left, bottom, -1, 1);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(left, top, -1, -1);
			}

			if (mask.Enabled)
				foreach (var cell in mask.Cells)
					i = mask.AddVertices(wr, cell, vertices, i, topLeft, bottomRight, cullPadding);

			if (cliffMask.Enabled)
				foreach (var cell in cliffMask.Cells)
					i = cliffMask.AddVertices(wr, cell, vertices, i, topLeft, bottomRight, cullPadding);

			buffer.SetData(vertices, i);
			cachedTopLeft = topLeft;
			cachedBottomRight = bottomRight;
			cachedVertexCount = i;
			return i;
		}

		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			var vertexCount = BuildVertices(wr);
			if (vertexCount == 0)
				return;

			var scroll = wr.Viewport.TopLeft;
			var size = renderer.WorldFrameBufferSize;
			var width = 2f / (renderer.WorldDownscaleFactor * size.Width);
			var height = 2f / (renderer.WorldDownscaleFactor * size.Height);

			shader.SetVec("Scroll", scroll.X, scroll.Y);
			shader.SetVec("WorldScroll", scroll.X, scroll.Y);
			shader.SetVec("p1", width, height);
			shader.SetVec("p2", -1, -1);
			shader.SetTexture("SourceTexture", Game.Renderer.GetRenderBufferSnapshot());
			shader.SetVec("Time", ticks);
			shader.SetVec("Intensity", intensity);
			shader.SetVec("ClearTint", info.ClearTintRed, info.ClearTintGreen, info.ClearTintBlue);
			shader.SetVec("ClearAlpha", info.ClearAlpha);
			shader.SetVec("ClearShimmer", info.ClearShimmer);
			shader.SetVec("ClearDistortion", info.ClearDistortion);
			shader.SetVec("ClearWaveScale", info.ClearWaveScale);
			shader.SetVec("ClearWaveSpeed", info.ClearWaveSpeed);
			shader.SetVec("StormTint", info.StormTintRed, info.StormTintGreen, info.StormTintBlue);
			shader.SetVec("StormAlpha", info.StormAlpha);
			shader.SetVec("StormShimmer", info.StormShimmer);
			shader.SetVec("StormDistortion", info.StormDistortion);
			shader.SetVec("StormPulse", info.StormPulse);
			shader.SetVec("StormPulseSpeed", info.StormPulseSpeed);
			shader.SetVec("StormWaveScale", info.StormWaveScale);
			shader.SetVec("StormWaveSpeed", info.StormWaveSpeed);
			shader.PrepareRender();
			renderer.DrawBatch(buffer, shader, 0, vertexCount, PrimitiveType.TriangleList);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer?.Dispose();
			mask?.Dispose();
			cliffMask?.Dispose();
			shader.Dispose();
		}
	}
}
