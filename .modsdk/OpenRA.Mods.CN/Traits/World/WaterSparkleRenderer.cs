#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Subtle screen-space sparkle pass for lit water terrain.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Renders small dissolving sparkles on lit water terrain.")]
	public sealed class WaterSparkleRendererInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Terrain types to apply the sparkle to.")]
		public readonly ImmutableArray<string> TerrainTypes = ["Water"];

		[Desc("Extra screen pixels around each water cell quad.")]
		public readonly int TilePadding = 2;

		[Desc("Additional tile template IDs to include in the shader regardless of terrain type.")]
		public readonly int[] AdditionalTemplates = [];

		[Desc("Palette-index ranges (min/max pairs) identifying water pixels for the Temperate tileset.")]
		public readonly int[] TemperateWaterPaletteIndexRanges = [];

		[Desc("Palette-index ranges (min/max pairs) identifying water pixels for the Snow tileset.")]
		public readonly int[] SnowWaterPaletteIndexRanges = [];

		[Desc("Tile template IDs that should use WaterPaletteIndexRanges instead of whole-cell water quads.")]
		public readonly int[] WaterPaletteMaskedTemplates = [];

		[Desc("Tile template IDs whose sprites occlude adjacent water cells.")]
		public readonly int[] CliffOcclusionTemplates = [];

		[Desc("Tile template IDs whose non-water pixels block the water shader.")]
		public readonly int[] CliffBlockTemplates = [];

		[Desc("Cell offsets (relative to each occluding tile) to exclude from the shader.")]
		public readonly CVec[] CliffOcclusionOffsets =
		[
			new CVec(1, 0), new CVec(0, 1),
			new CVec(0, -1), new CVec(-1, 0),
			new CVec(-1, -1),
			new CVec(-1, 1), new CVec(1, -1),
			new CVec(0, -2), new CVec(-1, -2), new CVec(-2, -1), new CVec(-2, 0),
			new CVec(-2, -2),
			new CVec(-2, 2), new CVec(2, -2),
		];

		[Desc("Overall sparkle strength. 0 disables.")]
		public readonly float Strength = 0.45f;

		[Desc("Approximate sparkle density from 0-1.")]
		public readonly float Density = 0.16f;

		[Desc("Sparkle life-cycle speed.")]
		public readonly float Speed = 0.010f;

		[Desc("Maximum tiny local movement in screen pixels.")]
		public readonly float MovementPixels = 0.75f;

		[Desc("Sparkle core radius in screen pixels.")]
		public readonly float CorePixels = 1.05f;

		[Desc("Sparkle cell size in screen pixels.")]
		public readonly float CellPixels = 13f;

		[Desc("Extra sparkle response where a godray lights the water (local brightness boost). 0 disables.")]
		public readonly float GodrayResponse = 1.8f;

		[Desc("Sparkle tint RGB.")]
		public readonly float TintRed = 0.75f;
		public readonly float TintGreen = 0.92f;
		public readonly float TintBlue = 1.0f;

		public override object Create(ActorInitializer init) { return new WaterSparkleRenderer(init.Self, this); }
	}

	public sealed class WaterSparkleRenderer : IRenderPostProcessPass, ITick, INotifyActorDisposing
	{
		readonly WaterSparkleRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly WaterSparkleShaderBindings bindings;
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

		public WaterSparkleRenderer(Actor self, WaterSparkleRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			bindings = new WaterSparkleShaderBindings();
			shader = renderer.CreateShader(bindings);

			var map = self.World.Map;
			var paletteRanges = string.Equals(map.Tileset, "SNOW", StringComparison.OrdinalIgnoreCase)
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
						return new[] { new CVec(1, 0), new CVec(-1, 0), new CVec(0, 1), new CVec(0, -1) }
							.Any(d => { var n = c + d; return map.Contains(n) && info.TerrainTypes.Contains(map.GetTerrainInfo(n).Type); });

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

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterWorld;
		bool IRenderPostProcessPass.Enabled => cells.Length > 0 && info.Strength > 0f && Game.Settings.Graphics.WaterEffects;

		void ITick.Tick(Actor self) { ticks++; }

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
			var cullPadding = (int)(info.MovementPixels + info.CorePixels + info.TilePadding + 2);

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
			shader.SetVec("Strength", info.Strength);
			shader.SetVec("Density", info.Density);
			shader.SetVec("Speed", info.Speed);
			shader.SetVec("MovementPixels", info.MovementPixels);
			shader.SetVec("CorePixels", info.CorePixels);
			shader.SetVec("CellPixels", info.CellPixels);
			shader.SetVec("GodrayResponse", info.GodrayResponse);
			shader.SetVec("Tint", info.TintRed, info.TintGreen, info.TintBlue);
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

	sealed class WaterSparkleShaderBindings : IShaderBindings
	{
		const string FragmentShader = @"#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D SourceTexture;
uniform vec2 WorldScroll;
uniform float Time;
uniform float Strength;
uniform float Density;
uniform float Speed;
uniform float MovementPixels;
uniform float CorePixels;
uniform float CellPixels;
uniform float GodrayResponse;
uniform vec3 Tint;

in vec2 vTexCoord;
out vec4 fragColor;

float hash(vec2 p)
{
	p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
	return fract(sin(p.x + p.y) * 43758.5453);
}

float lumaAt(vec2 pix)
{
	ivec2 sz = textureSize(SourceTexture, 0);
	ivec2 q = clamp(ivec2(pix), ivec2(0), sz - ivec2(1));
	vec3 c = texelFetch(SourceTexture, q, 0).rgb;
	return dot(c, vec3(0.299, 0.587, 0.114));
}

float sparkleLayer(vec2 world, float cell, float speedMul)
{
	vec2 g = world / cell;
	vec2 id = floor(g);
	vec2 f = fract(g);

	float seed = hash(id);
	float spawn = step(1.0 - Density, seed);
	float life = fract(seed * 7.13 + Time * Speed * speedMul);

	vec2 anchor = vec2(0.18 + 0.64 * hash(id + vec2(3.1, 7.7)), 0.18 + 0.64 * hash(id + vec2(9.2, 1.4)));
	vec2 wobble = vec2(
		sin(Time * 0.017 + seed * 17.0),
		cos(Time * 0.013 + seed * 23.0)) * (MovementPixels / cell);
	vec2 center = anchor + wobble;

	float distPx = length(f - center) * cell;
	float core = 1.0 - smoothstep(CorePixels * 0.45, CorePixels, distPx);
	float halo = (1.0 - smoothstep(CorePixels, CorePixels + 1.7, distPx)) * 0.16;
	float shape = clamp(core + halo, 0.0, 1.0);

	float fade = smoothstep(0.0, 0.10, life) * (1.0 - smoothstep(0.28, 0.95, life));
	float dissolveNoise = hash(id + floor((f - center) * 9.0));
	float dissolve = mix(1.0, smoothstep(life - 0.18, life + 0.22, dissolveNoise), smoothstep(0.25, 0.85, life));
	return shape * fade * dissolve * spawn;
}

void main()
{
	vec4 base = texelFetch(SourceTexture, ivec2(gl_FragCoord.xy), 0);
	fragColor = base;

	if (vTexCoord.x > 1.5)
		return;

	float diamond = 1.0 - smoothstep(0.96, 1.02, abs(vTexCoord.x) + abs(vTexCoord.y));
	if (diamond <= 0.001)
		return;

	float luma = dot(base.rgb, vec3(0.299, 0.587, 0.114));
	float bright = max(max(base.r, base.g), base.b);
	float sat = bright - min(min(base.r, base.g), base.b);

	float lit = smoothstep(0.07, 0.20, luma);
	float foam = smoothstep(0.50, 0.82, bright) * (1.0 - smoothstep(0.10, 0.28, sat));

	// Only sparkle on genuinely bluish water pixels. Bridges/roads drawn over
	// water are grey/brown (blue not dominant), so they get no sparkle.
	float blueDom = base.b - max(base.r, base.g);
	float isWater = smoothstep(0.02, 0.06, blueDom);
	// Permissive variant: a godray lightens (whitens) the water so blue
	// dominance drops - still treat it as water, but grey bridge (~0) is out.
	float isWaterSoft = smoothstep(0.00, 0.03, blueDom);

	// Godray reaction: godrays are additive sprites, so where one lights the
	// water this pixel is locally brighter than its surroundings. Sparkle
	// responds strongly there.
	vec2 fp = gl_FragCoord.xy;
	float ll = (lumaAt(fp + vec2( 7.0,  0.0)) + lumaAt(fp + vec2(-7.0,  0.0))
		+ lumaAt(fp + vec2( 0.0,  7.0)) + lumaAt(fp + vec2( 0.0, -7.0))
		+ lumaAt(fp + vec2( 5.0,  5.0)) + lumaAt(fp + vec2(-5.0,  5.0))
		+ lumaAt(fp + vec2( 5.0, -5.0)) + lumaAt(fp + vec2(-5.0, -5.0))) * 0.125;
	float godray = smoothstep(0.015, 0.075, luma - ll);

	float waterLight = diamond * (1.0 - foam)
		* (lit * isWater + godray * GodrayResponse * isWaterSoft);
	if (waterLight <= 0.001)
		return;

	vec2 world = gl_FragCoord.xy + WorldScroll;
	float sparkle = sparkleLayer(world + vec2(91.0, 5.0), CellPixels, 1.0);
	sparkle += sparkleLayer(world + vec2(37.0, 73.0), CellPixels * 1.47, 0.73) * 0.65;

	vec3 color = Tint * sparkle * waterLight * Strength;
	fragColor = vec4(clamp(base.rgb + color, vec3(0.0), vec3(1.0)), base.a);
}";

		public string VertexShaderName { get; } = "postprocess_textured";
		public string VertexShaderCode { get; } = ShaderBindings.GetShaderCode("postprocess_textured.vert");
		public string FragmentShaderName { get; } = "cn_postprocess_textured_watersparkle";
		public string FragmentShaderCode { get; } = FragmentShader;
		public int Stride { get; } = 16;
		public ShaderVertexAttribute[] Attributes { get; } =
		[
			new ShaderVertexAttribute("aVertexPosition", ShaderVertexAttributeType.Float, 2, 0),
			new ShaderVertexAttribute("aVertexTexCoord", ShaderVertexAttributeType.Float, 2, 8),
		];
	}
}
