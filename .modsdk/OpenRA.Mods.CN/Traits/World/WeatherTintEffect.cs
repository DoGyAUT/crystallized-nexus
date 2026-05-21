#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Sole writer of WorldTintState: takes the DayNightCycle base tint",
		"and lerps it toward the ion-storm tint by WeatherController intensity.",
		"This path is the per-sprite combined-shader tint, so it respects",
		"IgnoreWorldTint (both day/night and the storm skip those sprites).",
		"Declare AFTER DayNightCycle so it reads a fresh base each tick.")]
	public class WeatherTintEffectInfo : TraitInfo
	{
		[Desc("Ambient multiplier at full storm intensity.")]
		public readonly float StormAmbient = 0.55f;

		[Desc("Red channel tint at full storm intensity.")]
		public readonly float StormRed = 0.7f;

		[Desc("Green channel tint at full storm intensity.")]
		public readonly float StormGreen = 0.75f;

		[Desc("Blue channel tint at full storm intensity.")]
		public readonly float StormBlue = 1.1f;

		public override object Create(ActorInitializer init) { return new WeatherTintEffect(this); }
	}

	public class WeatherTintEffect : IWorldLoaded, ITick, INotifyActorDisposing
	{
		readonly WeatherTintEffectInfo info;

		WeatherController weatherController;
		DayNightCycle dayNight;

		public WeatherTintEffect(WeatherTintEffectInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			weatherController = w.WorldActor.TraitOrDefault<WeatherController>();
			dayNight = w.WorldActor.TraitOrDefault<DayNightCycle>();
			Apply();
		}

		void ITick.Tick(Actor self)
		{
			Apply();
		}

		void Apply()
		{
			// Day/night base (effective = Ambient * RGB, matching the old
			// TintPostProcessEffect convention). Identity when absent.
			float br = 1f, bg = 1f, bb = 1f;
			if (dayNight != null)
			{
				br = dayNight.BaseAmbient * dayNight.BaseRed;
				bg = dayNight.BaseAmbient * dayNight.BaseGreen;
				bb = dayNight.BaseAmbient * dayNight.BaseBlue;
			}

			// Storm target, same effective convention.
			var sr = info.StormAmbient * info.StormRed;
			var sg = info.StormAmbient * info.StormGreen;
			var sb = info.StormAmbient * info.StormBlue;

			var t = weatherController != null ? weatherController.Intensity : 0f;

			// Sole writer of the per-sprite world tint: storm layered on the
			// day/night base. Skips IgnoreWorldTint geometry in the shader.
			WorldTintState.Red = br + (sr - br) * t;
			WorldTintState.Green = bg + (sg - bg) * t;
			WorldTintState.Blue = bb + (sb - bb) * t;
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			// Reset shared state so a later match without these traits is not
			// left with a stale tint.
			WorldTintState.Red = 1f;
			WorldTintState.Green = 1f;
			WorldTintState.Blue = 1f;
		}
	}
}
