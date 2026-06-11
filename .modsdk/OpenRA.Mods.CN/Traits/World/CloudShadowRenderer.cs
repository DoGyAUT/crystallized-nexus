#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Screenspace procedural cloud shadow post-process pass.
 * Replaces sprite-based CloudSpawner@Clouds with a shader-driven darkening overlay.
 */
#endregion

using System;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Renders procedural cloud shadows as a post-process pass over the terrain.",
		"Replaces sprite-based cloud actors. Blends between clear and ion-storm states",
		"based on WeatherController.Intensity.")]
	public sealed class CloudShadowRendererInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Spatial frequency of the cloud pattern. Smaller = larger clouds.")]
		public readonly float CloudScale = 0.006f;

		[Desc("Wind direction in degrees (0 = east, 90 = south).")]
		public readonly float WindAngle = 45f;

		[Desc("Cloud scroll speed during clear weather (world units per tick).")]
		public readonly float WindSpeed = 0.012f;

		[Desc("Maximum shadow darkness during clear weather (0=none, 1=fully black).")]
		public readonly float ShadowAlpha = 0.28f;

		[Desc("Cloud coverage threshold during clear weather. Lower = more clouds.")]
		public readonly float CloudCoverage = 0.48f;

		[Desc("Softness of cloud edges.")]
		public readonly float CloudEdge = 0.15f;

		[Desc("Maximum shadow darkness during full ion storm.")]
		public readonly float StormShadowAlpha = 0.52f;

		[Desc("Cloud coverage threshold during full ion storm.")]
		public readonly float StormCloudCoverage = 0.30f;

		[Desc("Additional wind speed multiplier during full ion storm.")]
		public readonly float StormWindSpeed = 0.035f;

		public override object Create(ActorInitializer init) { return new CloudShadowRenderer(init.Self, this); }
	}

	public sealed class CloudShadowRenderer : IRenderPostProcessPass, ITick, IWorldLoaded, INotifyActorDisposing
	{
		readonly CloudShadowRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly RenderPostProcessPassTexturedShaderBindings bindings;

		readonly IVertexBuffer<RenderPostProcessPassTexturedVertex> buffer;
		readonly RenderPostProcessPassTexturedVertex[] vertices = new RenderPostProcessPassTexturedVertex[6];
		int ticks;
		float intensity;
		WeatherController weatherController;

		readonly float windVx;
		readonly float windVy;

		public CloudShadowRenderer(Actor self, CloudShadowRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			bindings = new RenderPostProcessPassTexturedShaderBindings("cloudshadow");
			shader = renderer.CreateShader(bindings);

			var rad = info.WindAngle * Math.PI / 180.0;
			windVx = (float)(Math.Cos(rad) * info.WindSpeed);
			windVy = (float)(Math.Sin(rad) * info.WindSpeed);

			buffer = renderer.CreateVertexBuffer(bindings, vertices, true);
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			weatherController = w.WorldActor.TraitOrDefault<WeatherController>();
		}

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterTerrain;
		bool IRenderPostProcessPass.Enabled => Game.Settings.Graphics.CloudShadows;

		void ITick.Tick(Actor self)
		{
			ticks++;
			intensity = weatherController?.Intensity ?? 0f;
		}

		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			var topLeft = wr.Viewport.TopLeft;
			var bottomRight = wr.Viewport.BottomRight;

			vertices[0] = new RenderPostProcessPassTexturedVertex(topLeft.X, topLeft.Y, 0, 0);
			vertices[1] = new RenderPostProcessPassTexturedVertex(bottomRight.X, topLeft.Y, 1, 0);
			vertices[2] = new RenderPostProcessPassTexturedVertex(bottomRight.X, bottomRight.Y, 1, 1);
			vertices[3] = new RenderPostProcessPassTexturedVertex(bottomRight.X, bottomRight.Y, 1, 1);
			vertices[4] = new RenderPostProcessPassTexturedVertex(topLeft.X, bottomRight.Y, 0, 1);
			vertices[5] = new RenderPostProcessPassTexturedVertex(topLeft.X, topLeft.Y, 0, 0);

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
			shader.SetVec("Time", ticks);
			shader.SetVec("Intensity", intensity);
			shader.SetVec("CloudScale", info.CloudScale);
			shader.SetVec("WindVelocity", windVx, windVy);
			shader.SetVec("ShadowAlpha", info.ShadowAlpha);
			shader.SetVec("CloudCoverage", info.CloudCoverage);
			shader.SetVec("CloudEdge", info.CloudEdge);
			shader.SetVec("StormShadowAlpha", info.StormShadowAlpha);
			shader.SetVec("StormCloudCoverage", info.StormCloudCoverage);
			shader.SetVec("StormWindSpeed", info.StormWindSpeed);
			shader.PrepareRender();
			renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer?.Dispose();
			shader.Dispose();
		}
	}
}
