#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Screenspace water reflection pass for subtle animated water reflections.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Draws a lightweight screenspace reflection over water terrain cells.")]
	public sealed class WaterReflectionRendererInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Terrain types to treat as reflective water.")]
		public readonly ImmutableArray<string> TerrainTypes = ["Water"];

		[Desc("Screen-space sample offset for the reflected source pixels.")]
		public readonly int ReflectionOffsetX = 0;

		[Desc("Screen-space sample offset for the reflected source pixels.")]
		public readonly int ReflectionOffsetY = 42;

		[Desc("Maximum reflection blend strength.")]
		public readonly float Alpha = 0.22f;

		[Desc("Maximum screen-space distortion in pixels.")]
		public readonly float Distortion = 2.5f;

		[Desc("Wave frequency used by the distortion shader.")]
		public readonly float WaveScale = 0.045f;

		[Desc("Wave animation speed.")]
		public readonly float WaveSpeed = 0.06f;

		[Desc("Subtle brightness shimmer added to the water.")]
		public readonly float Shimmer = 0.035f;

		[Desc("Reflection tint.")]
		public readonly float TintRed = 0.62f;

		[Desc("Reflection tint.")]
		public readonly float TintGreen = 0.82f;

		[Desc("Reflection tint.")]
		public readonly float TintBlue = 1.15f;

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

		public override object Create(ActorInitializer init) { return new WaterReflectionRenderer(init.Self, this); }
	}

	public sealed class WaterReflectionRenderer : IRenderPostProcessPass, ITick, INotifyActorDisposing
	{
		readonly WaterReflectionRendererInfo info;
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

		int2 cachedTopLeft;
		int2 cachedBottomRight;
		int cachedVertexCount = -1;

		public WaterReflectionRenderer(Actor self, WaterReflectionRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			bindings = new RenderPostProcessPassTexturedShaderBindings("waterreflection");
			shader = renderer.CreateShader(bindings);

			var map = self.World.Map;
			var paletteRanges = map.Tileset.Equals("SNOW",
System.StringComparison.InvariantCultureIgnoreCase)
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

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterTerrain;
		bool IRenderPostProcessPass.Enabled => cells.Length > 0 && info.Alpha > 0 && Game.Settings.Graphics.WaterEffects;

		void ITick.Tick(Actor self)
		{
			ticks++;
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
			var cullPadding = System.Math.Abs(info.ReflectionOffsetY) + (int)info.Distortion + info.TilePadding;

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
			shader.SetVec("p1", width, height);
			shader.SetVec("p2", -1, -1);
			shader.SetTexture("SourceTexture", Game.Renderer.GetRenderBufferSnapshot());
			shader.SetVec("ReflectionOffset", info.ReflectionOffsetX, info.ReflectionOffsetY);
			shader.SetVec("Tint", info.TintRed, info.TintGreen, info.TintBlue);
			shader.SetVec("Alpha", info.Alpha);
			shader.SetVec("Distortion", info.Distortion);
			shader.SetVec("WaveScale", info.WaveScale);
			shader.SetVec("WaveSpeed", info.WaveSpeed);
			shader.SetVec("Shimmer", info.Shimmer);
			shader.SetVec("Time", ticks);
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
