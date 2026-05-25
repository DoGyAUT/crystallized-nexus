#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Drifting cloud shadow folded into the world tint (combined shader).
 * Darkens terrain + world-tinted sprites uniformly and is automatically
 * skipped for IgnoreWorldTint geometry (godrays, explosions, glow FX), which
 * therefore render naturally on top. Replaces the sprite-based cloud shadows.
 */
#endregion

using System;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Applies a drifting cloud shadow as part of the world tint (combined shader).",
		"Darkens terrain and world-tinted sprites; IgnoreWorldTint geometry is skipped.",
		"Blends between clear and ion-storm states via WeatherController.Intensity.")]
	public sealed class WorldCloudShadowInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		[Desc("Spatial frequency of the cloud pattern. Smaller = larger clouds.")]
		public readonly float CloudScale = 0.006f;

		[Desc("Wind direction in degrees (0 = east, 90 = south).")]
		public readonly float WindAngle = 45f;

		[Desc("Cloud scroll speed during clear weather (world px per tick).")]
		public readonly float WindSpeed = 0.45f;

		[Desc("Maximum shadow darkness during clear weather (0 = none, 1 = black).")]
		public readonly float ShadowAlpha = 0.28f;

		[Desc("Cloud coverage threshold during clear weather. Lower = more clouds.")]
		public readonly float CloudCoverage = 0.48f;

		[Desc("Softness of cloud edges.")]
		public readonly float CloudEdge = 0.15f;

		[Desc("Maximum shadow darkness during full ion storm.")]
		public readonly float StormShadowAlpha = 0.52f;

		[Desc("Cloud coverage threshold during full ion storm.")]
		public readonly float StormCloudCoverage = 0.30f;

		[Desc("Cloud scroll speed during full ion storm (world px per tick).")]
		public readonly float StormWindSpeed = 1.2f;

		public override object Create(ActorInitializer init) { return new WorldCloudShadow(this); }
	}

	public sealed class WorldCloudShadow : ITick, IWorldLoaded, INotifyActorDisposing
	{
		readonly WorldCloudShadowInfo info;
		readonly float windDirX;
		readonly float windDirY;

		WeatherController weatherController;
		float windOffsetX;
		float windOffsetY;

		public WorldCloudShadow(WorldCloudShadowInfo info)
		{
			this.info = info;
			var rad = info.WindAngle * Math.PI / 180.0;
			windDirX = (float)Math.Cos(rad);
			windDirY = (float)Math.Sin(rad);
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			weatherController = w.WorldActor.TraitOrDefault<WeatherController>();
		}

		void ITick.Tick(Actor self)
		{
			if (!Game.Settings.Graphics.CloudShadows)
			{
				CloudShadowState.Alpha = 0f;
				return;
			}

			var intensity = weatherController?.Intensity ?? 0f;
			var windSpeed = Lerp(info.WindSpeed, info.StormWindSpeed, intensity);
			windOffsetX += windDirX * windSpeed;
			windOffsetY += windDirY * windSpeed;

			CloudShadowState.Alpha = Lerp(info.ShadowAlpha, info.StormShadowAlpha, intensity);
			CloudShadowState.Scale = info.CloudScale;
			CloudShadowState.Coverage = Lerp(info.CloudCoverage, info.StormCloudCoverage, intensity);
			CloudShadowState.Edge = info.CloudEdge;
			CloudShadowState.WindX = windOffsetX;
			CloudShadowState.WindY = windOffsetY;
			CloudShadowState.Time = 1f;
		}

		static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

		void INotifyActorDisposing.Disposing(Actor self)
		{
			// The state is process-global: make sure a following world (or the
			// menu) is not left darkened.
			CloudShadowState.Alpha = 0f;
			CloudShadowState.Time = 0f;
		}
	}
}
