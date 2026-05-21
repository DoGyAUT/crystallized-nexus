#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Screenspace Tiberium glow as a SINGLE fullscreen post-process pass.
 *
 * Terrain accuracy without a grid: the CPU builds a per-cell coverage texture
 * from the resource layer (R = green, G = blue, B = red Tiberium, A = Veins). It is uploaded with LINEAR filtering and sampled
 * (bilinear + a small blur) in a fullscreen shader, which projects each screen
 * pixel back to a map cell via a CPU-supplied affine transform. Fog / glow /
 * particles / distortion follow the real Tiberium cells smoothly - no tile
 * grid, and actors off the field are never falsely affected.
 */
#endregion

using System;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Fullscreen screenspace glow / fog / particles over Tiberium, gated by a resource-layer coverage texture.")]
	public sealed class TiberiumGlowRendererInfo : TraitInfo, ILobbyCustomRulesIgnore, Requires<IResourceLayerInfo>
	{
		[Desc("Resource type for the green tint group.")]
		public readonly string GreenType = "Tiberium";

		[Desc("Resource type for the blue tint group.")]
		public readonly string BlueType = "BlueTiberium";

		[Desc("Resource type for the red tint group.")]
		public readonly string RedType = "RedTiberium";

		[Desc("Resource type for the organic vein effect group.")]
		public readonly string VeinsType = "Veins";

		[Desc("Green Tiberium glow tint RGB.")]
		public readonly float GreenTintRed = 0.30f;
		public readonly float GreenTintGreen = 1.10f;
		public readonly float GreenTintBlue = 0.35f;

		[Desc("Blue Tiberium glow tint RGB.")]
		public readonly float BlueTintRed = 0.35f;
		public readonly float BlueTintGreen = 0.70f;
		public readonly float BlueTintBlue = 1.20f;

		[Desc("Red Tiberium glow tint RGB.")]
		public readonly float RedTintRed = 1.20f;
		public readonly float RedTintGreen = 0.30f;
		public readonly float RedTintBlue = 0.25f;

		[Desc("Veins effect tint RGB.")]
		public readonly float VeinsTintRed = 1.00f;
		public readonly float VeinsTintGreen = 0.42f;
		public readonly float VeinsTintBlue = 0.10f;

		[Desc("Core self-illumination strength.")]
		public readonly float GlowStrength = 0.28f;

		[Desc("Soft ground light-bleed strength.")]
		public readonly float BleedStrength = 0.07f;

		[Desc("Hot filament self-illumination of living veins.")]
		public readonly float VeinsGlowStrength = 0.14f;

		[Desc("Soft orange ambient light bleeding around the vein patch.")]
		public readonly float VeinsAmbientStrength = 0.09f;

		[Desc("Darkening applied to the vein bed before the glow is added.")]
		public readonly float VeinsDarkenStrength = 0.16f;

		[Desc("Organic vein pulse speed.")]
		public readonly float VeinsPulseSpeed = 0.085f;

		[Desc("Organic vein pulse amplitude / depth.")]
		public readonly float VeinsPulseStrength = 0.85f;

		[Desc("Strength of the rings pulsing outward from the vein hole.")]
		public readonly float VeinsRingStrength = 0.65f;

		[Desc("Number of concentric rings across the patch (higher = tighter).")]
		public readonly float VeinsRingScale = 5.0f;

		[Desc("Outward travel speed of the vein-hole rings.")]
		public readonly float VeinsRingSpeed = 0.045f;

		[Desc("Sap corpuscles flowing inward along the tendrils to the hole.")]
		public readonly float VeinsCorpStrength = 0.10f;

		[Desc("Number of corpuscle bands across the patch (higher = more/finer).")]
		public readonly float VeinsCorpScale = 26.0f;

		[Desc("Inward flow speed of the sap corpuscles.")]
		public readonly float VeinsCorpSpeed = 0.060f;

		[Desc("Wet glistening twinkle on the tendril pixels.")]
		public readonly float VeinsGlintStrength = 0.18f;

		[Desc("Fraction of tendril pixels that can glint (0-1).")]
		public readonly float VeinsGlintDensity = 0.10f;

		[Desc("Glint flicker rate.")]
		public readonly float VeinsGlintSpeed = 0.20f;

		[Desc("Slow organic spores drifting up off the vein field.")]
		public readonly float VeinsSporeStrength = 0.10f;

		[Desc("Approximate spore density from 0-1.")]
		public readonly float VeinsSporeDensity = 0.14f;

		[Desc("Spore rise speed.")]
		public readonly float VeinsSporeSpeed = 0.008f;

		[Desc("How much the tendril thickness breathes (mask dilate/erode, 0-1).")]
		public readonly float VeinsBreathStrength = 0.50f;

		[Desc("Vein thickness breathing speed.")]
		public readonly float VeinsBreathSpeed = 0.020f;

		[Desc("Spatial frequency of the crawling vein pattern.")]
		public readonly float VeinsCrawlScale = 0.040f;

		[Desc("Animation speed of the crawling vein pattern.")]
		public readonly float VeinsCrawlSpeed = 0.085f;

		[Desc("Legacy, unused.")]
		public readonly float ShimmerStrength = 0.3f;

		[Desc("Maximum screen-space distortion in pixels. Subtle fbm-gradient refraction inside the field. 0 disables.")]
		public readonly float Distortion = 0.8f;

		[Desc("Spatial frequency of the distortion warp noise.")]
		public readonly float WaveScale = 0.05f;

		[Desc("Distortion warp evolution speed.")]
		public readonly float WaveSpeed = 0.03f;

		[Desc("Self-illumination pulse speed.")]
		public readonly float PulseSpeed = 0.05f;

		[Desc("Self-illumination pulse amplitude / depth (1.0 = off -> full).")]
		public readonly float PulseStrength = 1.0f;

		[Desc("Soft tiberium fog strength.")]
		public readonly float FogStrength = 0.45f;

		[Desc("Additional fog bloom around bright crystal pixels.")]
		public readonly float FogBloom = 0.06f;

		[Desc("Spatial frequency of the broad fog cloud. Smaller = larger fog banks.")]
		public readonly float FogScale = 0.0035f;

		[Desc("Spatial frequency of the fine fog detail.")]
		public readonly float FogDetailScale = 0.014f;

		[Desc("Fog drift speed in world pixels per tick (slow roll).")]
		public readonly float FogSpeed = 0.5f;

		[Desc("Fog coverage threshold. Lower = more fog.")]
		public readonly float FogCoverage = 0.25f;

		[Desc("Softness of fog edges.")]
		public readonly float FogEdge = 0.60f;

		[Desc("Strength of small glowing particles rising from Tiberium fields.")]
		public readonly float ParticleStrength = 0.8f;

		[Desc("Approximate particle density from 0-1.")]
		public readonly float ParticleDensity = 0.18f;

		[Desc("Particle rise animation speed.")]
		public readonly float ParticleSpeed = 0.010f;

		[Desc("Legacy, unused (fullscreen pass has no per-cell padding).")]
		public readonly int GlowPadding = 8;
		public readonly int GlowOffsetX = 0;
		public readonly int GlowOffsetY = -12;

		public override object Create(ActorInitializer init) { return new TiberiumGlowRenderer(init.Self, this); }
	}

	public sealed class TiberiumGlowRenderer : IRenderPostProcessPass, ITick, INotifyActorDisposing
	{
		readonly TiberiumGlowRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly TiberiumGlowShaderBindings bindings;
		readonly IVertexBuffer<RenderPostProcessPassTexturedVertex> buffer;
		readonly RenderPostProcessPassTexturedVertex[] vertices = new RenderPostProcessPassTexturedVertex[6];

		readonly World world;
		readonly Map map;
		readonly IResourceLayer resourceLayer;
		readonly Action<CPos, string> cellChanged;
		readonly Sheet coverageSheet;
		readonly Sheet heightSheet;
		readonly int mapW;
		readonly int mapH;
		readonly int texW;
		readonly int texH;

		bool coverageDirty = true;
		int ticks;

		public TiberiumGlowRenderer(Actor self, TiberiumGlowRendererInfo info)
		{
			this.info = info;
			world = self.World;
			map = world.Map;
			renderer = Game.Renderer;
			bindings = new TiberiumGlowShaderBindings();
			shader = renderer.CreateShader(bindings);
			buffer = renderer.CreateVertexBuffer(bindings, vertices, true);

			resourceLayer = self.Trait<IResourceLayer>();
			cellChanged = (_, _) => coverageDirty = true;
			resourceLayer.CellChanged += cellChanged;

			mapW = map.MapSize.Width;
			mapH = map.MapSize.Height;

			// Textures must be power-of-two; pad the sheet up. Padding cells stay
			// zero so they have no visual effect.
			texW = Exts.NextPowerOf2(mapW);
			texH = Exts.NextPowerOf2(mapH);

			// Buffered sheet. The texture is created lazily on the first
			// GetTexture() AFTER data has been committed (see Draw); creating it
			// earlier would make GetData() read back an empty GPU texture.
			coverageSheet = new Sheet(SheetType.BGRA, new Size(texW, texH));
			heightSheet = new Sheet(SheetType.BGRA, new Size(texW, texH));
		}

		bool filterSet;

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterWorld;

		bool IRenderPostProcessPass.Enabled => Game.Settings.Graphics.TiberiumEffects;

		void ITick.Tick(Actor self) { ticks++; }

		void RebuildCoverage()
		{
			var data = coverageSheet.GetData();
			var hdata = heightSheet.GetData();
			Array.Clear(data, 0, data.Length);
			Array.Clear(hdata, 0, hdata.Length);

			foreach (var cell in map.AllCells)
			{
				var mpos = cell.ToMPos(map);
				if (mpos.U < 0 || mpos.U >= mapW || mpos.V < 0 || mpos.V >= mapH)
					continue;

				byte g = 0, b = 0, r = 0, v = 0;

				var resource = resourceLayer.GetResource(cell);
				if (resource.Type != null && resource.Density > 0)
				{
					var maxDensity = resourceLayer.GetMaxDensity(resource.Type);
					var coverage = maxDensity > 0
						? (byte)Math.Min(255, (resource.Density * 255 + maxDensity / 2) / maxDensity)
						: (byte)255;

					if (resource.Type == info.GreenType)
						g = coverage;
					else if (resource.Type == info.BlueType)
						b = coverage;
					else if (resource.Type == info.RedType)
						r = coverage;
					else if (resource.Type == info.VeinsType)
						v = coverage;
				}

				// BGRA byte order. Shader sees: .r=green, .g=blue, .b=red, .a=veins.
				var idx = 4 * (mpos.V * texW + mpos.U);
				data[idx + 0] = r; // Blue byte   -> shader .b (red tiberium)
				data[idx + 1] = b; // Green byte  -> shader .g (blue tiberium)
				data[idx + 2] = g; // Red byte    -> shader .r (green tiberium)
				data[idx + 3] = v;

				// Per-cell terrain height level (for screen->cell height correction).
				var lvl = map.Height.TryGetValue(cell, out var hl) ? hl : (byte)0;
				hdata[idx + 0] = lvl;
				hdata[idx + 1] = lvl;
				hdata[idx + 2] = lvl;
				hdata[idx + 3] = 255;
			}

			coverageSheet.CommitBufferedData();
			heightSheet.CommitBufferedData();
			coverageDirty = false;
		}

		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			if (coverageDirty)
				RebuildCoverage();

			var topLeft = wr.Viewport.TopLeft;
			var bottomRight = wr.Viewport.BottomRight;

			// Screen-pixel -> map-cell affine transform. The projection is linear
			// (height ignored), so three reference cells fully define it. Use far
			// corners and divide for numerical stability.
			var su = Math.Max(texW - 1, 1);
			var sv = Math.Max(texH - 1, 1);
			var sA = ScreenOf(wr, new MPos(0, 0));
			var sB = ScreenOf(wr, new MPos(su, 0));
			var sC = ScreenOf(wr, new MPos(0, sv));
			var bUx = (sB.X - sA.X) / (float)su;
			var bUy = (sB.Y - sA.Y) / (float)su;
			var bVx = (sC.X - sA.X) / (float)sv;
			var bVy = (sC.Y - sA.Y) / (float)sv;
			var det = bUx * bVy - bVx * bUy;
			if (Math.Abs(det) < 1e-6f)
				det = det < 0 ? -1e-6f : 1e-6f;
			var invDet = 1f / det;
			var i00 = bVy * invDet;
			var i01 = -bVx * invDet;
			var i10 = -bUy * invDet;
			var i11 = bUx * invDet;

			// Carry the viewport screen-px through the texcoord. These corners ARE
			// the vertex positions, so the interpolated value is exactly aligned
			// with the rendered scene; the shader projects px -> cell per pixel and
			// corrects for terrain height (the lookup is sampled from the height
			// texture iteratively).
			vertices[0] = new RenderPostProcessPassTexturedVertex(topLeft.X,     topLeft.Y,     topLeft.X,     topLeft.Y);
			vertices[1] = new RenderPostProcessPassTexturedVertex(bottomRight.X, topLeft.Y,     bottomRight.X, topLeft.Y);
			vertices[2] = new RenderPostProcessPassTexturedVertex(bottomRight.X, bottomRight.Y, bottomRight.X, bottomRight.Y);
			vertices[3] = new RenderPostProcessPassTexturedVertex(bottomRight.X, bottomRight.Y, bottomRight.X, bottomRight.Y);
			vertices[4] = new RenderPostProcessPassTexturedVertex(topLeft.X,     bottomRight.Y, topLeft.X,     bottomRight.Y);
			vertices[5] = new RenderPostProcessPassTexturedVertex(topLeft.X,     topLeft.Y,     topLeft.X,     topLeft.Y);

			// Vertical screen px per terrain height level (zoom-dependent).
			var refp = map.CenterOfCell(new MPos(0, 0).ToCPos(map));
			var hpx0 = wr.ScreenPxPosition(new WPos(refp.X, refp.Y, 0)).Y;
			var hpx1 = wr.ScreenPxPosition(new WPos(refp.X, refp.Y, 724)).Y;
			var heightPxPerLevel = (float)(hpx0 - hpx1);

			var scroll = wr.Viewport.TopLeft;
			var size = renderer.WorldFrameBufferSize;
			var width = 2f / (renderer.WorldDownscaleFactor * size.Width);
			var height = 2f / (renderer.WorldDownscaleFactor * size.Height);

			buffer.SetData(vertices, 6);
			shader.SetVec("Scroll", scroll.X, scroll.Y);
			shader.SetVec("WorldScroll", scroll.X, scroll.Y);
			shader.SetVec("p1", width, height);
			shader.SetVec("p2", -1, -1);
			shader.SetTexture("SourceTexture", Game.Renderer.GetRenderBufferSnapshot());
			var coverageTex = coverageSheet.GetTexture();
			var heightTex = heightSheet.GetTexture();
			if (!filterSet)
			{
				// Linear filtering -> the per-cell mask is interpolated smoothly
				// between cells (no hard tile edges / grid).
				coverageTex.ScaleFilter = TextureScaleFilter.Linear;
				heightTex.ScaleFilter = TextureScaleFilter.Linear;
				filterSet = true;
			}

			shader.SetTexture("CoverageTexture", coverageTex);
			shader.SetTexture("HeightTexture", heightTex);
			shader.SetVec("TexSize", texW, texH);
			shader.SetVec("ScreenOrigin", sA.X, sA.Y);
			shader.SetVec("InvR0", i00, i01);
			shader.SetVec("InvR1", i10, i11);
			shader.SetVec("HeightPxPerLevel", heightPxPerLevel);
			shader.SetVec("Time", ticks);
			shader.SetVec("GreenTint", info.GreenTintRed, info.GreenTintGreen, info.GreenTintBlue);
			shader.SetVec("BlueTint", info.BlueTintRed, info.BlueTintGreen, info.BlueTintBlue);
			shader.SetVec("RedTint", info.RedTintRed, info.RedTintGreen, info.RedTintBlue);
			shader.SetVec("VeinsTint", info.VeinsTintRed, info.VeinsTintGreen, info.VeinsTintBlue);
			shader.SetVec("GlowStrength", info.GlowStrength);
			shader.SetVec("BleedStrength", info.BleedStrength);
			shader.SetVec("VeinsGlowStrength", info.VeinsGlowStrength);
			shader.SetVec("VeinsAmbientStrength", info.VeinsAmbientStrength);
			shader.SetVec("VeinsDarkenStrength", info.VeinsDarkenStrength);
			shader.SetVec("PulseSpeed", info.PulseSpeed);
			shader.SetVec("PulseStrength", info.PulseStrength);
			shader.SetVec("VeinsPulseSpeed", info.VeinsPulseSpeed);
			shader.SetVec("VeinsPulseStrength", info.VeinsPulseStrength);
			shader.SetVec("VeinsRingStrength", info.VeinsRingStrength);
			shader.SetVec("VeinsRingScale", info.VeinsRingScale);
			shader.SetVec("VeinsRingSpeed", info.VeinsRingSpeed);
			shader.SetVec("VeinsCorpStrength", info.VeinsCorpStrength);
			shader.SetVec("VeinsCorpScale", info.VeinsCorpScale);
			shader.SetVec("VeinsCorpSpeed", info.VeinsCorpSpeed);
			shader.SetVec("VeinsGlintStrength", info.VeinsGlintStrength);
			shader.SetVec("VeinsGlintDensity", info.VeinsGlintDensity);
			shader.SetVec("VeinsGlintSpeed", info.VeinsGlintSpeed);
			shader.SetVec("VeinsSporeStrength", info.VeinsSporeStrength);
			shader.SetVec("VeinsSporeDensity", info.VeinsSporeDensity);
			shader.SetVec("VeinsSporeSpeed", info.VeinsSporeSpeed);
			shader.SetVec("VeinsBreathStrength", info.VeinsBreathStrength);
			shader.SetVec("VeinsBreathSpeed", info.VeinsBreathSpeed);
			shader.SetVec("VeinsCrawlScale", info.VeinsCrawlScale);
			shader.SetVec("VeinsCrawlSpeed", info.VeinsCrawlSpeed);
			shader.SetVec("FogStrength", info.FogStrength);
			shader.SetVec("FogBloom", info.FogBloom);
			shader.SetVec("FogScale", info.FogScale);
			shader.SetVec("FogDetailScale", info.FogDetailScale);
			shader.SetVec("FogSpeed", info.FogSpeed);
			shader.SetVec("FogCoverage", info.FogCoverage);
			shader.SetVec("FogEdge", info.FogEdge);
			shader.SetVec("ParticleStrength", info.ParticleStrength);
			shader.SetVec("ParticleDensity", info.ParticleDensity);
			shader.SetVec("ParticleSpeed", info.ParticleSpeed);
			shader.PrepareRender();
			renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
		}

		int2 ScreenOf(WorldRenderer wr, MPos mpos)
		{
			// Height ignored so the projection stays linear (3 cells define it).
			var p = map.CenterOfCell(mpos.ToCPos(map));
			return wr.ScreenPxPosition(new WPos(p.X, p.Y, 0));
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			resourceLayer.CellChanged -= cellChanged;
			coverageSheet.Dispose();
			buffer?.Dispose();
			shader.Dispose();
		}
	}

	sealed class TiberiumGlowShaderBindings : IShaderBindings
	{
		const string FragmentShader = @"#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D SourceTexture;
uniform sampler2D CoverageTexture;
uniform sampler2D HeightTexture;
uniform vec2 WorldScroll;
uniform vec2 TexSize;
uniform vec2 ScreenOrigin;
uniform vec2 InvR0;
uniform vec2 InvR1;
uniform float HeightPxPerLevel;
uniform float Time;
uniform vec3 GreenTint;
uniform vec3 BlueTint;
uniform vec3 RedTint;
uniform vec3 VeinsTint;
uniform float GlowStrength;
uniform float BleedStrength;
uniform float VeinsGlowStrength;
uniform float VeinsAmbientStrength;
uniform float VeinsDarkenStrength;
uniform float PulseSpeed;
uniform float PulseStrength;
uniform float VeinsPulseSpeed;
uniform float VeinsPulseStrength;
uniform float VeinsRingStrength;
uniform float VeinsRingScale;
uniform float VeinsRingSpeed;
uniform float VeinsCorpStrength;
uniform float VeinsCorpScale;
uniform float VeinsCorpSpeed;
uniform float VeinsGlintStrength;
uniform float VeinsGlintDensity;
uniform float VeinsGlintSpeed;
uniform float VeinsSporeStrength;
uniform float VeinsSporeDensity;
uniform float VeinsSporeSpeed;
uniform float VeinsBreathStrength;
uniform float VeinsBreathSpeed;
uniform float VeinsCrawlScale;
uniform float VeinsCrawlSpeed;
uniform float FogStrength;
uniform float FogBloom;
uniform float FogScale;
uniform float FogDetailScale;
uniform float FogSpeed;
uniform float FogCoverage;
uniform float FogEdge;
uniform float ParticleStrength;
uniform float ParticleDensity;
uniform float ParticleSpeed;
in vec2 vTexCoord;
out vec4 fragColor;

float hash(vec2 p)
{
	p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
	return fract(sin(p.x + p.y) * 43758.5453);
}

float noise(vec2 p)
{
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);

	return mix(
		mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), u.x),
		mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x),
		u.y);
}

float fbm(vec2 p)
{
	float v = 0.0;
	float a = 0.5;

	for (int i = 0; i < 4; i++)
	{
		v += a * noise(p);
		p = p * 2.03 + vec2(17.3, 29.1);
		a *= 0.5;
	}

	return v;
}

vec3 sourceAt(vec2 pixel)
{
	ivec2 sz = textureSize(SourceTexture, 0);
	ivec2 pos = clamp(ivec2(pixel), ivec2(0), sz - ivec2(1));
	return texelFetch(SourceTexture, pos, 0).rgb;
}

float lumaAt(vec2 pixel)
{
	return dot(sourceAt(pixel), vec3(0.299, 0.587, 0.114));
}

// Wide soft blur of the coverage mask in cell space (gaussian-ish, ~2 cell
// radius) so the field edge feathers gradually instead of snapping per cell.
vec4 coverage(vec2 cellUV)
{
	vec2 px = 1.0 / TexSize;
	vec4 c = texture(CoverageTexture, cellUV) * 0.16;

	c += texture(CoverageTexture, cellUV + vec2( px.x,  0.0)) * 0.08;
	c += texture(CoverageTexture, cellUV + vec2(-px.x,  0.0)) * 0.08;
	c += texture(CoverageTexture, cellUV + vec2( 0.0,  px.y)) * 0.08;
	c += texture(CoverageTexture, cellUV + vec2( 0.0, -px.y)) * 0.08;

	c += texture(CoverageTexture, cellUV + vec2( px.x,  px.y)) * 0.055;
	c += texture(CoverageTexture, cellUV + vec2(-px.x,  px.y)) * 0.055;
	c += texture(CoverageTexture, cellUV + vec2( px.x, -px.y)) * 0.055;
	c += texture(CoverageTexture, cellUV + vec2(-px.x, -px.y)) * 0.055;

	c += texture(CoverageTexture, cellUV + vec2( 2.0 * px.x,  0.0)) * 0.04;
	c += texture(CoverageTexture, cellUV + vec2(-2.0 * px.x,  0.0)) * 0.04;
	c += texture(CoverageTexture, cellUV + vec2( 0.0,  2.0 * px.y)) * 0.04;
	c += texture(CoverageTexture, cellUV + vec2( 0.0, -2.0 * px.y)) * 0.04;

	c += texture(CoverageTexture, cellUV + vec2( 2.0 * px.x,  2.0 * px.y)) * 0.0225;
	c += texture(CoverageTexture, cellUV + vec2(-2.0 * px.x,  2.0 * px.y)) * 0.0225;
	c += texture(CoverageTexture, cellUV + vec2( 2.0 * px.x, -2.0 * px.y)) * 0.0225;
	c += texture(CoverageTexture, cellUV + vec2(-2.0 * px.x, -2.0 * px.y)) * 0.0225;

	c += texture(CoverageTexture, cellUV + vec2( 3.0 * px.x,  0.0)) * 0.02;
	c += texture(CoverageTexture, cellUV + vec2(-3.0 * px.x,  0.0)) * 0.02;
	c += texture(CoverageTexture, cellUV + vec2( 0.0,  3.0 * px.y)) * 0.02;
	c += texture(CoverageTexture, cellUV + vec2( 0.0, -3.0 * px.y)) * 0.02;
	return c;
}

// Very wide, cheap blur of just the veins channel. The radius spans many
// cells, so the per-cell density blocks average out completely (no
// checkerboard) and what is left is a smooth bell that peaks deep in the
// patch (the dense core / vein hole) and fades to 0 at the rim. Used as a
// pseudo radial 'depth' to drive rings travelling outward from the hole.
float veinsField(vec2 uv)
{
	vec2 px = 1.0 / TexSize;
	float s = texture(CoverageTexture, uv).a;
	for (int i = 1; i <= 4; i++)
	{
		float r = float(i) * 5.0;
		s += texture(CoverageTexture, uv + vec2( r,  0.0) * px).a;
		s += texture(CoverageTexture, uv + vec2(-r,  0.0) * px).a;
		s += texture(CoverageTexture, uv + vec2( 0.0,  r) * px).a;
		s += texture(CoverageTexture, uv + vec2( 0.0, -r) * px).a;
		s += texture(CoverageTexture, uv + vec2( r,  r) * px * 0.7).a;
		s += texture(CoverageTexture, uv + vec2(-r,  r) * px * 0.7).a;
		s += texture(CoverageTexture, uv + vec2( r, -r) * px * 0.7).a;
		s += texture(CoverageTexture, uv + vec2(-r, -r) * px * 0.7).a;
	}
	return s / 33.0;
}

// A tiny 1-2px spark born near the ground that rises and dissolves. The grid
// is hidden by (a) a low-frequency domain warp that bends the lattice so there
// are no straight repeating rows, and (b) per-spark jitter of position, size
// and speed. `jx` decorrelates stacked layers.
float spark(vec2 world, float cell, float lifePx, float speed, float corePx, float jx)
{
	// Low-frequency domain warp -> the cell lattice is no longer a visible
	// regular pattern (this is what removes the grid look on the water).
	vec2 w = world + (vec2(
		noise(world * 0.013 + jx),
		noise(world * 0.011 + jx + 7.0)) - 0.5) * cell * 2.2;

	vec2 g = w / cell;
	vec2 id = floor(g);
	vec2 f = fract(g);

	float seed = hash(id + vec2(jx, jx * 1.7));
	float spawn = step(1.0 - ParticleDensity, seed);

	// Per-spark speed + phase so they neither sync nor pulse in lockstep.
	float spd = speed * (0.55 + 1.1 * hash(id + vec2(jx + 2.3, jx)));
	float life = fract(seed * 7.13 + Time * ParticleSpeed * spd);

	// Rise from near the ground (0.92) upward by life * lifePx.
	float riseFrac = life * (lifePx / cell);
	float bx = 0.15 + 0.70 * hash(id + vec2(jx + 5.7, jx)) + sin(life * 6.2831853 + seed * 9.0) * 0.035;
	vec2 center = vec2(bx, 0.92 - riseFrac);

	float cr = corePx * (0.7 + 0.7 * hash(id + vec2(jx + 1.1, jx)));
	float distPx = length(f - center) * cell;
	float core = 1.0 - smoothstep(cr * 0.5, cr, distPx);
	float halo = (1.0 - smoothstep(cr, cr + 1.5, distPx)) * 0.14;
	float shape = clamp(core + halo, 0.0, 1.0);

	// Quick fade-in off the ground, then dissolve toward the top of the rise.
	float fade = smoothstep(0.0, 0.10, life) * (1.0 - smoothstep(0.55, 1.0, life));

	return shape * fade * spawn;
}

float particles(vec2 world, float fieldMask)
{
	// Same motion as before - only the core size is smaller (~1-2px dots
	// instead of fat spheres): corePx 1.2/1.7 -> 0.55/0.70.
	float p = spark(world, 16.0, 15.0, 1.00, 0.55, 0.0);
	p += spark(world + vec2(37.0, 11.0), 23.0, 22.0, 0.62, 0.70, 9.0) * 0.8;
	return p * ParticleStrength * smoothstep(0.10, 0.40, fieldMask);
}

// Organic counterpart to spark(): a slow, soft mote that drifts up off
// the vein bed and sways sideways as it dissolves (warm spore / vapour,
// not a crisp crystal spark). Independent density / speed so it animates
// on its own rhythm. Same domain-warp trick removes any cell lattice.
float spore(vec2 world, float cell, float lifePx, float dens, float spd)
{
	vec2 w = world + (vec2(
		noise(world * 0.012 + 3.0),
		noise(world * 0.010 + 11.0)) - 0.5) * cell * 2.4;

	vec2 g = w / cell;
	vec2 id = floor(g);
	vec2 f = fract(g);

	float seed = hash(id + vec2(3.1, 7.7));
	float spawn = step(1.0 - dens, seed);

	float life = fract(seed * 5.31 + Time * spd * (0.6 + 0.8 * hash(id + vec2(2.0, 9.0))));
	float riseFrac = life * (lifePx / cell);

	// Lateral sway -> the mote is wafted, not on rails.
	float bx = 0.12 + 0.76 * hash(id + vec2(5.0, 1.0))
		+ sin(life * 6.2831853 + seed * 9.0) * 0.07;
	vec2 center = vec2(bx, 0.94 - riseFrac);

	float cr = 1.4 * (0.7 + 0.7 * hash(id + vec2(1.3, 4.4)));
	float distPx = length(f - center) * cell;
	float core = 1.0 - smoothstep(cr * 0.5, cr, distPx);
	float halo = (1.0 - smoothstep(cr, cr + 2.2, distPx)) * 0.12;
	float shape = clamp(core + halo, 0.0, 1.0);

	float fade = smoothstep(0.0, 0.12, life) * (1.0 - smoothstep(0.50, 1.0, life));
	return shape * fade * spawn;
}

void main()
{
	vec4 base = texelFetch(SourceTexture, ivec2(gl_FragCoord.xy), 0);
	fragColor = base;
	vec3 result = base.rgb;

	// vTexCoord carries the viewport screen px (aligned with the scene).
	// Project px -> cell (flat), then correct for terrain height by looking up
	// the height texture and re-projecting. A cell at level L is drawn L*hppl
	// px higher, so the flat projection of (px shifted down by L*hppl) yields
	// the cell actually visible here. Iterate as height is locally ~constant.
	vec2 sp = vTexCoord;
	vec2 cellXY;
	vec2 cellUV;
	for (int it = 0; it < 3; it++)
	{
		vec2 rel = sp - ScreenOrigin;
		cellXY = vec2(dot(InvR0, rel), dot(InvR1, rel));
		cellUV = (cellXY + 0.5) / TexSize;
		float lvl = texture(HeightTexture, clamp(cellUV, 0.0, 1.0)).r * 255.0;
		sp = vTexCoord + vec2(0.0, lvl * HeightPxPerLevel);
	}

	vec2 inb = step(vec2(0.0), cellUV) * step(cellUV, vec2(1.0));
	float inBounds = inb.x * inb.y;
	if (inBounds < 0.5)
		return;

	vec4 cov = coverage(cellUV);
	float gC = cov.r;            // green tiberium
	float bC = cov.g;            // blue tiberium
	float rC = cov.b;            // red tiberium

	// The coverage texture stores one texel per map cell. Drawing the veins
	// proportional to that density makes the cell lattice show up as a
	// diamond checkerboard. Domain-warp the lookup, then collapse it to a
	// FLAT presence gate (saturates almost immediately): the interior is a
	// uniform 1.0 with no per-cell magnitude left, so the grid is gone -
	// every visible structure below comes from continuous procedural noise.
	vec2 vWorld = gl_FragCoord.xy + WorldScroll;
	vec2 vWarp = (vec2(fbm(vWorld * 0.021 + 4.0),
		fbm(vWorld * 0.021 + 23.0)) - 0.5) * (3.0 / TexSize);
	float vC = coverage(cellUV + vWarp).a;            // veins

	// Soft, terrain-accurate field mask. Wide smoothstep over the blurred
	// coverage -> the edge feathers over ~2 cells (gentle cloud edge).
	float field = smoothstep(0.04, 0.55, max(gC, max(bC, rC)));
	float veins = smoothstep(0.05, 0.22, vC);

	if (max(field, veins) <= 0.002)
		return;

	vec2 world = gl_FragCoord.xy + WorldScroll;

	if (field > 0.002)
	{
		// Density-weighted tint so mixed fields blend naturally per pixel.
		float wsum = gC + bC + rC + 1e-4;
		vec3 tintCol = (GreenTint * gC + BlueTint * bC + RedTint * rC) / wsum;
		vec3 glowCol = tintCol / max(max(tintCol.r, max(tintCol.g, tintCol.b)), 0.001);

		// Sharp per-pixel crystal highlight (gated to the field).
		vec3 srcDir = normalize(base.rgb + vec3(0.001));
		vec3 tintDir = normalize(tintCol + vec3(0.001));
		float brightness = max(max(base.r, base.g), base.b);
		float saturation = brightness - min(min(base.r, base.g), base.b);
		float luma = dot(base.rgb, vec3(0.299, 0.587, 0.114));
		vec2 px = gl_FragCoord.xy;
		float localLuma =
			(lumaAt(px + vec2( 3.0,  0.0)) +
			lumaAt(px + vec2(-3.0,  0.0)) +
			lumaAt(px + vec2( 0.0,  3.0)) +
			lumaAt(px + vec2( 0.0, -3.0)) +
			lumaAt(px + vec2( 2.0,  2.0)) +
			lumaAt(px + vec2(-2.0,  2.0)) +
			lumaAt(px + vec2( 2.0, -2.0)) +
			lumaAt(px + vec2(-2.0, -2.0))) * 0.125;
		float localHighlight = smoothstep(0.025, 0.12, luma - localLuma);
		float crystal = field
			* smoothstep(0.20, 0.62, brightness)
			* smoothstep(0.10, 0.38, saturation)
			* smoothstep(0.72, 0.92, dot(srcDir, tintDir))
			* localHighlight;

		// One slow, large rolling cloud.
		vec2 drift = vec2(0.60, 0.28) * (Time * FogSpeed);
		float broad = fbm((world + drift) * FogScale + vec2(13.0, 37.0));
		float detail = fbm((world - drift * 1.6 + vec2(41.0, 19.0)) * FogDetailScale);
		float cloud = broad * 0.90 + detail * 0.10;
		float cloudWisps = smoothstep(FogCoverage, FogCoverage + FogEdge, cloud);
		float fogNoise = 0.46 + 0.54 * cloudWisps;

		// Per-crystal pulse, fully off -> current value, phase varies by position.
		float pulseWave = 0.5 + 0.5 * sin(Time * PulseSpeed + hash(floor(world / 6.0)) * 6.2831853);
		float pulse = mix(1.0, pulseWave, PulseStrength);

		vec3 shimmered = base.rgb;
		shimmered *= mix(1.0, 0.85 + 0.30 * pulseWave, crystal);

		vec3 emissive = tintCol * GlowStrength * crystal * pulse;

		float bleedMask = field * 0.34 * (0.55 + 0.45 * fogNoise);
		vec3 bleed = tintCol * BleedStrength * bleedMask;

		// Translucent, cloud-driven haze (low flat base, strong wisp variation)
		// so it reads as drifting fog, not a flat saturated paint slab.
		float fogEnabled = step(0.001, FogStrength + FogBloom);
		float fogMask = field * fogEnabled * (0.22 + 0.78 * cloudWisps);
		float fogAmount = clamp(fogMask * FogStrength + crystal * FogBloom * 0.18, 0.0, 0.40);
		vec3 fogColor = mix(tintCol * 0.28, glowCol, 0.22 + 0.30 * fogNoise);
		vec3 fogged = mix(shimmered, fogColor, fogAmount);

		float particleMask = particles(world, field) * step(0.001, ParticleStrength);
		vec3 particleGlow = mix(vec3(1.0), glowCol, 0.55) * particleMask;

		result = clamp(fogged + emissive + bleed + particleGlow, vec3(0.0), vec3(1.0));
	}

	if (veins > 0.002)
	{
		// Two fbm layers drift in opposite directions so the tendrils slowly
		// writhe instead of sitting still.
		vec2 crawlDrift = vec2(0.74, -0.28) * (Time * VeinsCrawlSpeed);
		float crawlA = fbm((world + crawlDrift) * VeinsCrawlScale + vec2(71.0, 11.0));
		float crawlB = fbm((world - crawlDrift * 1.35) * (VeinsCrawlScale * 1.65) + vec2(17.0, 89.0));

		// Ridged value -> thin bright filaments that read as raised ranks.
		float ridge = crawlA * 0.6 + crawlB * 0.4;
		float fibers = smoothstep(0.40, 0.80, ridge);

		// A bright band of 'sap' travels ALONG the filaments over time, so the
		// veins look like they actively pump / crawl, not a frozen texture.
		float surge = 0.5 + 0.5 * sin(ridge * 9.0 - Time * VeinsCrawlSpeed * 6.0);
		surge *= surge;

		// Rings pulsing OUTWARD from the vein hole. veinsField() is a very
		// wide blur, so it is a smooth bell peaking at the dense core (the
		// hole) and fading at the rim - and grid-free. Iso-depth contours
		// are concentric around the core; +Time makes each crest drift to
		// lower depth, i.e. travel outward from the hole. A slow breathing
		// envelope (VeinsPulse*) makes the rings swell and ebb - this is the
		// ONLY pulsation in the field now.
		float depth = veinsField(cellUV + vWarp);
		float ringWave = 0.5 + 0.5 * sin((depth * VeinsRingScale + Time * VeinsRingSpeed) * 6.2831853);
		float ringBreath = mix(1.0, 0.55 + 0.45 * sin(Time * VeinsPulseSpeed), VeinsPulseStrength);
		float ring = pow(ringWave, 1.6) * smoothstep(0.02, 0.16, depth) * ringBreath;

		// Living motion: warp the underlying scene along the flow direction.
		vec2 flow = vec2(crawlA - 0.5, crawlB - 0.5);
		vec2 wave = vec2(sin(Time * VeinsCrawlSpeed * 1.7 + crawlB * 6.2831853),
			cos(Time * VeinsCrawlSpeed * 1.3 + crawlA * 6.2831853)) * 0.90;
		vec2 offset = (flow * 3.4 + wave) * veins;
		vec3 shifted = sourceAt(gl_FragCoord.xy + offset);
		float movement = veins * (0.26 + 0.22 * fibers);
		vec3 moved = mix(result, shifted, movement);

		// PIXEL-ACCURATE vein mask. The coverage texture is one texel per
		// cell and can never trace the sprite, so - exactly like the
		// Tiberium crystal highlight - key off the actual rendered scene
		// colour: bright, saturated pixels whose hue points at VeinsTint are
		// the real tendril art. This follows the sprite per-pixel and has no
		// cell grid (it is sub-cell image detail, not per-cell density).
		vec3 vDir = normalize(VeinsTint + vec3(0.001));
		vec3 sDir = normalize(base.rgb + vec3(0.001));
		float vBrightness = max(max(base.r, base.g), base.b);
		float vSaturation = vBrightness - min(min(base.r, base.g), base.b);

		// Breathing tendril thickness: a slow wave (radiating from the hole
		// via -depth) shifts the hue-match threshold, so the mask dilates
		// and erodes as if the veins fill with fluid and drain - a subtle
		// vital sign, no extra geometry.
		float vBreath = 0.5 + 0.5 * sin(Time * VeinsBreathSpeed - depth * 2.0);
		float thick = VeinsBreathStrength * (vBreath - 0.5) * 0.14;
		float veinPix = veins
			* smoothstep(0.84 - thick, 0.96 - thick, dot(sDir, vDir))
			* smoothstep(0.10, 0.32, vBrightness)
			* smoothstep(0.05, 0.20, vSaturation);

		// Gentle bed darkening, kept OFF the actual tendril pixels so the
		// glow can pop instead of the patch reading as a dead muddy slab.
		float bedDarken = VeinsDarkenStrength * veins * (0.35 + 0.45 * (1.0 - veinPix));
		vec3 bed = moved * (1.0 - bedDarken);

		// Glow rides the pixel-accurate tendril mask only (no field-wide
		// flat term), plus the ring crest sweeping THROUGH those same
		// pixels outward from the hole - so the sprite itself lights up.
		float fiberBright = veinPix * (0.55 + 0.45 * surge);
		float ringHit = ring * veinPix;
		vec3 lit = bed * (1.0 + VeinsRingStrength * ringHit);

		vec3 lineGlow = VeinsTint * VeinsGlowStrength
			* (2.40 * fiberBright + 3.20 * ringHit);

		// Sap corpuscles: thin bright knots travelling INWARD along the
		// tendrils to the hole (-Time -> toward higher depth = the core).
		// Counter-motion to the outward ring (fluid pumping in vs pulse
		// radiating out). Rides the pixel-accurate mask + fbm break-up.
		float corpSaw = fract(depth * VeinsCorpScale - Time * VeinsCorpSpeed);
		float corp = smoothstep(0.80, 0.94, corpSaw) * (1.0 - smoothstep(0.94, 1.0, corpSaw));
		corp *= veinPix * (0.30 + 0.70 * fibers) * smoothstep(0.03, 0.18, depth);
		vec3 corpGlow = mix(VeinsTint, vec3(1.0), 0.35) * VeinsCorpStrength * corp;

		// Wet glistening: short per-spot twinkles on the tendril pixels.
		vec2 gcell = floor(world * 1.3);
		float gseed = hash(gcell);
		float gslot = Time * VeinsGlintSpeed + gseed * 7.0;
		float gon = step(1.0 - VeinsGlintDensity, hash(gcell + floor(gslot) * 1.7));
		float gtw = sin(fract(gslot) * 3.14159265);
		float glint = gon * gtw * gtw * veinPix;
		vec3 glintGlow = mix(vec3(1.0), VeinsTint, 0.40) * VeinsGlintStrength * glint;

		// Slow spores drifting up off the bed, denser toward the hole.
		float sp = spore(world, 20.0, 26.0, VeinsSporeDensity, VeinsSporeSpeed);
		sp += spore(world + vec2(29.0, 13.0), 30.0, 34.0, VeinsSporeDensity * 0.8, VeinsSporeSpeed * 0.7) * 0.75;
		float spores = sp * veins * (0.30 + 0.70 * depth);
		vec3 sporeGlow = mix(vec3(1.0), VeinsTint, 0.50) * VeinsSporeStrength * spores;

		// Very light, flat ambient across the whole field - just enough to
		// tint the immediate surroundings. No pulse, no core gradient.
		vec3 ambientGlow = VeinsTint * VeinsAmbientStrength * veins;

		result = clamp(lit + lineGlow + corpGlow + glintGlow + sporeGlow + ambientGlow, vec3(0.0), vec3(1.0));
	}

	fragColor = vec4(result, base.a);
}";

		public string VertexShaderName { get; } = "postprocess_textured";
		public string VertexShaderCode { get; } = ShaderBindings.GetShaderCode("postprocess_textured.vert");
		public string FragmentShaderName { get; } = "cn_postprocess_textured_tiberiumglow";
		public string FragmentShaderCode { get; } = FragmentShader;
		public int Stride { get; } = 16;
		public ShaderVertexAttribute[] Attributes { get; } =
		[
			new ShaderVertexAttribute("aVertexPosition", ShaderVertexAttributeType.Float, 2, 0),
			new ShaderVertexAttribute("aVertexTexCoord", ShaderVertexAttributeType.Float, 2, 8),
		];
	}
}
