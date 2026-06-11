#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Full-screen atmospheric color-grading post-process pass.
 * Selectable in Graphics settings: Off / Cinematic / Tox Haze.
 */
#endregion

using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Full-screen atmospheric color grading. Mode is chosen by the Graphics.PostProcessGrading setting.")]
	public sealed class AtmosphericGradingRendererInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Cinematic: contrast multiplier around mid-grey.")]
		public readonly float CinematicContrast = 1.08f;

		[Desc("Cinematic: saturation (1 = unchanged, <1 desaturates).")]
		public readonly float CinematicSaturation = 0.92f;

		[Desc("Cinematic: vignette strength (0-1).")]
		public readonly float CinematicVignette = 0.28f;

		[Desc("Cinematic: RGB tint multiply.")]
		public readonly float CinematicTintRed = 1.02f;
		public readonly float CinematicTintGreen = 1.0f;
		public readonly float CinematicTintBlue = 0.96f;

		[Desc("Cinematic: film grain strength (0 = off).")]
		public readonly float CinematicGrain = 0.024f;

		[Desc("Cinematic: procedural haze overlay strength.")]
		public readonly float CinematicHazeStrength = 0.035f;

		[Desc("Cinematic: RGB haze overlay color.")]
		public readonly float CinematicHazeRed = 0.82f;
		public readonly float CinematicHazeGreen = 0.78f;
		public readonly float CinematicHazeBlue = 0.66f;

		[Desc("Tox Haze: contrast multiplier around mid-grey.")]
		public readonly float ToxHazeContrast = 1.06f;

		[Desc("Tox Haze: saturation (1 = unchanged, <1 desaturates).")]
		public readonly float ToxHazeSaturation = 0.74f;

		[Desc("Tox Haze: vignette strength (0-1).")]
		public readonly float ToxHazeVignette = 0.42f;

		[Desc("Tox Haze: RGB tint multiply (greenish toxic haze).")]
		public readonly float ToxHazeTintRed = 0.94f;
		public readonly float ToxHazeTintGreen = 1.00f;
		public readonly float ToxHazeTintBlue = 0.84f;

		[Desc("Tox Haze: film grain strength (0 = off).")]
		public readonly float ToxHazeGrain = 0.026f;

		[Desc("Tox Haze: procedural haze overlay strength.")]
		public readonly float ToxHazeOverlayStrength = 0.16f;

		[Desc("Tox Haze: RGB haze overlay color.")]
		public readonly float ToxHazeOverlayRed = 0.68f;
		public readonly float ToxHazeOverlayGreen = 0.72f;
		public readonly float ToxHazeOverlayBlue = 0.50f;

		[Desc("Tox Haze: split-tone strength for cool shadows and dusty highlights.")]
		public readonly float ToxHazeSplitTone = 0.30f;

		[Desc("Tox Haze: RGB multiplier for shadow split-tone.")]
		public readonly float ToxHazeShadowRed = 0.62f;
		public readonly float ToxHazeShadowGreen = 0.78f;
		public readonly float ToxHazeShadowBlue = 0.82f;

		[Desc("Tox Haze: RGB multiplier for highlight split-tone.")]
		public readonly float ToxHazeHighlightRed = 1.10f;
		public readonly float ToxHazeHighlightGreen = 1.02f;
		public readonly float ToxHazeHighlightBlue = 0.74f;

		[Desc("Tox Haze: slight foggy lift for dark areas.")]
		public readonly float ToxHazeBlackLift = 0.045f;

		[Desc("Spatial frequency of the broad haze clouds. Smaller = larger haze banks.")]
		public readonly float HazeScale = 0.0028f;

		[Desc("Spatial frequency of the fine haze detail.")]
		public readonly float HazeDetailScale = 0.010f;

		[Desc("Haze drift speed in screen pixels per tick.")]
		public readonly float HazeDriftSpeed = 0.20f;

		[Desc("Haze coverage threshold. Lower = more haze.")]
		public readonly float HazeCoverage = 0.36f;

		[Desc("Softness of haze cloud edges.")]
		public readonly float HazeEdge = 0.42f;

		public override object Create(ActorInitializer init) { return new AtmosphericGradingRenderer(this); }
	}

	public sealed class AtmosphericGradingRenderer : IRenderPostProcessPass, ITick, INotifyActorDisposing
	{
		readonly AtmosphericGradingRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly AtmosphericGradingShaderBindings bindings;
		readonly IVertexBuffer<RenderPostProcessPassVertex> buffer;
		readonly RenderPostProcessPassVertex[] vertices =
		[
			new(-1, -1),
			new(1, -1),
			new(1, 1),
			new(1, 1),
			new(-1, 1),
			new(-1, -1),
		];

		int ticks;

		public AtmosphericGradingRenderer(AtmosphericGradingRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			bindings = new AtmosphericGradingShaderBindings();
			shader = renderer.CreateShader(bindings);
			buffer = renderer.CreateVertexBuffer(bindings, vertices, false);
		}

		void ITick.Tick(Actor self) { ticks++; }

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterWorld;
		bool IRenderPostProcessPass.Enabled => Game.Settings.Graphics.PostProcessGrading != GradingMode.Off;

		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			var mode = Game.Settings.Graphics.PostProcessGrading;

			float contrast, saturation, vignette, grain, tintR, tintG, tintB, hazeStrength, hazeR, hazeG, hazeB;
			float splitTone, shadowR, shadowG, shadowB, highlightR, highlightG, highlightB, blackLift;
			if (mode == GradingMode.ToxHaze)
			{
				contrast = info.ToxHazeContrast;
				saturation = info.ToxHazeSaturation;
				vignette = info.ToxHazeVignette;
				grain = info.ToxHazeGrain;
				tintR = info.ToxHazeTintRed;
				tintG = info.ToxHazeTintGreen;
				tintB = info.ToxHazeTintBlue;
				hazeStrength = info.ToxHazeOverlayStrength;
				hazeR = info.ToxHazeOverlayRed;
				hazeG = info.ToxHazeOverlayGreen;
				hazeB = info.ToxHazeOverlayBlue;
				splitTone = info.ToxHazeSplitTone;
				shadowR = info.ToxHazeShadowRed;
				shadowG = info.ToxHazeShadowGreen;
				shadowB = info.ToxHazeShadowBlue;
				highlightR = info.ToxHazeHighlightRed;
				highlightG = info.ToxHazeHighlightGreen;
				highlightB = info.ToxHazeHighlightBlue;
				blackLift = info.ToxHazeBlackLift;
				shader.SetVec("Mode", 2);
			}
			else
			{
				contrast = info.CinematicContrast;
				saturation = info.CinematicSaturation;
				vignette = info.CinematicVignette;
				grain = info.CinematicGrain;
				tintR = info.CinematicTintRed;
				tintG = info.CinematicTintGreen;
				tintB = info.CinematicTintBlue;
				hazeStrength = info.CinematicHazeStrength;
				hazeR = info.CinematicHazeRed;
				hazeG = info.CinematicHazeGreen;
				hazeB = info.CinematicHazeBlue;
				splitTone = 0f;
				shadowR = 1f;
				shadowG = 1f;
				shadowB = 1f;
				highlightR = 1f;
				highlightG = 1f;
				highlightB = 1f;
				blackLift = 0f;
				shader.SetVec("Mode", 1);
			}

			shader.SetTexture("SourceTexture", Game.Renderer.GetRenderBufferSnapshot());
			shader.SetVec("Contrast", contrast);
			shader.SetVec("Saturation", saturation);
			shader.SetVec("Vignette", vignette);
			shader.SetVec("Tint", tintR, tintG, tintB);
			shader.SetVec("Grain", grain);
			shader.SetVec("HazeStrength", hazeStrength);
			shader.SetVec("HazeColor", hazeR, hazeG, hazeB);
			shader.SetVec("HazeScale", info.HazeScale);
			shader.SetVec("HazeDetailScale", info.HazeDetailScale);
			shader.SetVec("HazeDriftSpeed", info.HazeDriftSpeed);
			shader.SetVec("HazeCoverage", info.HazeCoverage);
			shader.SetVec("HazeEdge", info.HazeEdge);
			shader.SetVec("WorldScroll", wr.Viewport.TopLeft.X, wr.Viewport.TopLeft.Y);
			shader.SetVec("SplitTone", splitTone);
			shader.SetVec("ShadowTint", shadowR, shadowG, shadowB);
			shader.SetVec("HighlightTint", highlightR, highlightG, highlightB);
			shader.SetVec("BlackLift", blackLift);
			shader.SetVec("Time", ticks);
			shader.PrepareRender();
			renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer.Dispose();
			shader.Dispose();
		}
	}

	sealed class AtmosphericGradingShaderBindings : IShaderBindings
	{
		const string FragmentShader = @"#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D SourceTexture;
uniform float Mode;
uniform float Contrast;
uniform float Saturation;
uniform float Vignette;
uniform vec3 Tint;
uniform float Grain;
uniform float Time;
uniform float HazeStrength;
uniform vec3 HazeColor;
uniform float HazeScale;
uniform float HazeDetailScale;
uniform float HazeDriftSpeed;
uniform float HazeCoverage;
uniform float HazeEdge;
uniform vec2 WorldScroll;
uniform float SplitTone;
uniform vec3 ShadowTint;
uniform vec3 HighlightTint;
uniform float BlackLift;

out vec4 fragColor;

float hash(vec2 p)
{
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
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

void main()
{
	ivec2 px = ivec2(gl_FragCoord.xy);
	vec4 src = texelFetch(SourceTexture, px, 0);
	fragColor = src;

	if (Mode < 0.5)
		return;

	vec3 c = src.rgb;
	c = (c - 0.5) * Contrast + 0.5;

	float luma = dot(c, vec3(0.299, 0.587, 0.114));
	c = mix(vec3(luma), c, Saturation);
	c *= Tint;

	if (SplitTone > 0.0)
	{
		float shadow = 1.0 - smoothstep(0.18, 0.62, luma);
		float highlight = smoothstep(0.42, 0.92, luma);
		vec3 graded = c * mix(vec3(1.0), ShadowTint, shadow * SplitTone);
		graded *= mix(vec3(1.0), HighlightTint, highlight * SplitTone);
		c = mix(c, graded, SplitTone);
		c += ShadowTint * BlackLift * shadow;
	}

	vec2 res = vec2(textureSize(SourceTexture, 0));
	vec2 uv = gl_FragCoord.xy / res;
	float d = distance(uv, vec2(0.5));
	float vig = 1.0 - Vignette * smoothstep(0.25, 0.85, d);
	c *= vig;

	if (HazeStrength > 0.0)
	{
		vec2 drift = vec2(0.65, 0.28) * (Time * HazeDriftSpeed);
		vec2 world = gl_FragCoord.xy + WorldScroll;
		float broad = fbm((world + drift) * HazeScale + vec2(13.0, 37.0));
		float detail = fbm((world - drift * 1.7 + vec2(41.0, 19.0)) * HazeDetailScale);
		float cloud = smoothstep(HazeCoverage, HazeCoverage + HazeEdge, broad * 0.82 + detail * 0.18);
		float edgeHaze = smoothstep(0.28, 0.95, d);
		float haze = clamp((0.32 + cloud * 0.68 + edgeHaze * 0.35) * HazeStrength, 0.0, 0.65);

		float darkLift = (1.0 - smoothstep(0.15, 0.85, dot(c, vec3(0.299, 0.587, 0.114)))) * haze * 0.28;
		c = mix(c + HazeColor * darkLift, HazeColor, haze);
	}

	if (Grain > 0.0)
	{
		vec2 grainPx = gl_FragCoord.xy;
		float grainFrame = floor(Time * 0.5);
		float fine = hash(grainPx + grainFrame * vec2(37.0, 17.0));
		float soft = hash(floor(grainPx * 0.5) + grainFrame * vec2(11.0, 29.0));
		float grainMask = mix(0.55, 1.10, 1.0 - smoothstep(0.18, 0.92, luma));
		float filmGrain = ((fine - 0.5) * 0.82 + (soft - 0.5) * 0.18) * Grain * grainMask;
		float chroma = (hash(grainPx.yx + grainFrame * vec2(23.0, 41.0)) - 0.5) * Grain * 0.14 * grainMask;
		c += vec3(filmGrain + chroma, filmGrain, filmGrain - chroma);
	}

	fragColor = vec4(clamp(c, 0.0, 1.0), src.a);
}";

		public string VertexShaderName { get; } = "postprocess";
		public string VertexShaderCode { get; } = ShaderBindings.GetShaderCode("postprocess.vert");
		public string FragmentShaderName { get; } = "cn_postprocess_grading";
		public string FragmentShaderCode { get; } = FragmentShader;
		public int Stride { get; } = 8;
		public ShaderVertexAttribute[] Attributes { get; } =
		[
			new ShaderVertexAttribute("aVertexPosition", ShaderVertexAttributeType.Float, 2, 0),
		];
	}
}
