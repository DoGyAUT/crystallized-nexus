#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Lerps TintPostProcessEffect tint values based on WeatherController intensity.",
		"Requires TintPostProcessEffect and WeatherController on the world actor.")]
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

	public class WeatherTintEffect : IWorldLoaded, ITick
	{
		readonly WeatherTintEffectInfo info;

		TintPostProcessEffect tintEffect;
		WeatherController weatherController;

		float baseRed, baseGreen, baseBlue, baseAmbient;

		public WeatherTintEffect(WeatherTintEffectInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			tintEffect = w.WorldActor.TraitOrDefault<TintPostProcessEffect>();
			weatherController = w.WorldActor.TraitOrDefault<WeatherController>();

			if (tintEffect == null || weatherController == null)
				return;

			baseRed = tintEffect.Red;
			baseGreen = tintEffect.Green;
			baseBlue = tintEffect.Blue;
			baseAmbient = tintEffect.Ambient;
		}

		void ITick.Tick(Actor self)
		{
			if (tintEffect == null || weatherController == null)
				return;

			var t = weatherController.Intensity;
			tintEffect.Red = baseRed + (info.StormRed - baseRed) * t;
			tintEffect.Green = baseGreen + (info.StormGreen - baseGreen) * t;
			tintEffect.Blue = baseBlue + (info.StormBlue - baseBlue) * t;
			tintEffect.Ambient = baseAmbient + (info.StormAmbient - baseAmbient) * t;
		}
	}
}
