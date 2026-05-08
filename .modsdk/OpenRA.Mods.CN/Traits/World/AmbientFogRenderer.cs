#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Screenspace procedural ambient fog post-process pass.
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
	[Desc("Renders a soft procedural ambient fog overlay as a post-process pass.")]
	public sealed class AmbientFogRendererInfo : ConditionalTraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Fog tint. The alpha component is multiplied by Alpha.")]
		public readonly Color FogColor = Color.FromArgb(180, 255, 255, 255);

		[Desc("Overall fog strength.")]
		public readonly float Alpha = 0.30f;

		[Desc("Minimum ambient haze before the procedural fog variation is applied.")]
		public readonly float BaseAlpha = 0.16f;

		[Desc("Spatial frequency of the broad fog layer. Smaller = larger fog banks.")]
		public readonly float Scale = 0.0032f;

		[Desc("Spatial frequency of the fine fog detail.")]
		public readonly float DetailScale = 0.008f;

		[Desc("Wind direction in degrees (0 = east, 90 = south).")]
		public readonly float WindAngle = 25f;

		[Desc("Fog scroll speed in world units per tick.")]
		public readonly float WindSpeed = 0.018f;

		[Desc("Fog coverage threshold. Lower = more fog.")]
		public readonly float Coverage = 0.42f;

		[Desc("Softness of fog edges.")]
		public readonly float Edge = 0.50f;

		[Desc("Subtle animated density variation.")]
		public readonly float Pulse = 0.02f;

		public override object Create(ActorInitializer init) { return new AmbientFogRenderer(init.Self, this); }
	}

	public sealed class AmbientFogRenderer : ConditionalTrait<AmbientFogRendererInfo>, IRenderPostProcessPass, ITick, INotifyActorDisposing
	{
		readonly Renderer renderer;
		readonly IShader shader;
		readonly AmbientFogShaderBindings bindings;
		readonly float windVx;
		readonly float windVy;

		IVertexBuffer<RenderPostProcessPassTexturedVertex> buffer;
		RenderPostProcessPassTexturedVertex[] vertices = new RenderPostProcessPassTexturedVertex[6];
		int ticks;

		public AmbientFogRenderer(Actor self, AmbientFogRendererInfo info)
			: base(info)
		{
			renderer = Game.Renderer;
			bindings = new AmbientFogShaderBindings();
			shader = renderer.CreateShader(bindings);

			var rad = info.WindAngle * Math.PI / 180.0;
			windVx = (float)(Math.Cos(rad) * info.WindSpeed);
			windVy = (float)(Math.Sin(rad) * info.WindSpeed);

			buffer = renderer.CreateVertexBuffer(bindings, vertices, true);
		}

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterWorld;
		bool IRenderPostProcessPass.Enabled => !IsTraitDisabled && Info.Alpha > 0f && Info.FogColor.A > 0;

		void ITick.Tick(Actor self)
		{
			ticks++;
		}

		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			var topLeft = wr.Viewport.TopLeft;
			var bottomRight = wr.Viewport.BottomRight;

			vertices[0] = new RenderPostProcessPassTexturedVertex(topLeft.X,     topLeft.Y,     0, 0);
			vertices[1] = new RenderPostProcessPassTexturedVertex(bottomRight.X, topLeft.Y,     1, 0);
			vertices[2] = new RenderPostProcessPassTexturedVertex(bottomRight.X, bottomRight.Y, 1, 1);
			vertices[3] = new RenderPostProcessPassTexturedVertex(bottomRight.X, bottomRight.Y, 1, 1);
			vertices[4] = new RenderPostProcessPassTexturedVertex(topLeft.X,     bottomRight.Y, 0, 1);
			vertices[5] = new RenderPostProcessPassTexturedVertex(topLeft.X,     topLeft.Y,     0, 0);

			var scroll = wr.Viewport.TopLeft;
			var size = renderer.WorldFrameBufferSize;
			var width = 2f / (renderer.WorldDownscaleFactor * size.Width);
			var height = 2f / (renderer.WorldDownscaleFactor * size.Height);
			var fogAlpha = Info.Alpha * Info.FogColor.A / 255f;

			buffer.SetData(vertices, 6);
			shader.SetVec("Scroll", scroll.X, scroll.Y);
			shader.SetVec("WorldScroll", scroll.X, scroll.Y);
			shader.SetVec("p1", width, height);
			shader.SetVec("p2", -1, -1);
			shader.SetTexture("SourceTexture", Game.Renderer.GetRenderBufferSnapshot());
			shader.SetVec("Time", ticks);
			shader.SetVec("FogColor", Info.FogColor.R / 255f, Info.FogColor.G / 255f, Info.FogColor.B / 255f);
			shader.SetVec("Alpha", fogAlpha);
			shader.SetVec("BaseAlpha", Info.BaseAlpha);
			shader.SetVec("Scale", Info.Scale);
			shader.SetVec("DetailScale", Info.DetailScale);
			shader.SetVec("WindVelocity", windVx, windVy);
			shader.SetVec("Coverage", Info.Coverage);
			shader.SetVec("Edge", Info.Edge);
			shader.SetVec("Pulse", Info.Pulse);
			shader.PrepareRender();
			renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer?.Dispose();
			shader.Dispose();
		}
	}

	sealed class AmbientFogShaderBindings : IShaderBindings
	{
		const string FragmentShader = @"#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D SourceTexture;
uniform vec2 WorldScroll;
uniform float Time;
uniform vec3 FogColor;
uniform float Alpha;
uniform float BaseAlpha;
uniform float Scale;
uniform float DetailScale;
uniform vec2 WindVelocity;
uniform float Coverage;
uniform float Edge;
uniform float Pulse;

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

void main()
{
	vec4 base = texelFetch(SourceTexture, ivec2(gl_FragCoord.xy), 0);
	vec2 world = gl_FragCoord.xy + WorldScroll;
	vec2 drift = WindVelocity * Time;

	float broad = fbm((world + drift) * Scale);
	float detail = fbm((world - drift * 1.65 + vec2(41.0, 19.0)) * DetailScale);
	float fog = smoothstep(Coverage, Coverage + Edge, broad * 0.86 + detail * 0.14);
	float breathing = 1.0 + Pulse * sin(Time * 0.012 + broad * 6.2831853);
	float blend = clamp((BaseAlpha + fog * (1.0 - BaseAlpha)) * Alpha * breathing, 0.0, 1.0);

	fragColor = vec4(mix(base.rgb, FogColor, blend), base.a);
}";

		public string VertexShaderName { get; } = "postprocess_textured";
		public string VertexShaderCode { get; } = ShaderBindings.GetShaderCode("postprocess_textured.vert");
		public string FragmentShaderName { get; } = "cn_postprocess_textured_ambientfog";
		public string FragmentShaderCode { get; } = FragmentShader;
		public int Stride { get; } = 16;
		public ShaderVertexAttribute[] Attributes { get; } =
		[
			new ShaderVertexAttribute("aVertexPosition", ShaderVertexAttributeType.Float, 2, 0),
			new ShaderVertexAttribute("aVertexTexCoord", ShaderVertexAttributeType.Float, 2, 8),
		];
	}
}
