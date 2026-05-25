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
	[Desc("Sole writer of WorldTintState's bloom state: when the trait condition",
		"is active (typically dusk/dawn/night, granted by DayNightCycle), this",
		"publishes the per-frame BloomStrength = Intensity * NightFactor01 to",
		"the engine renderer, which then runs RenderGlowBloom each frame.",
		"Intensity is constant; NightFactor01 scales smoothly from 0 (noon) to",
		"1 (midnight), so dusk/dawn produce a softer halo than full night.")]
	public class BloomGlowEffectInfo : ConditionalTraitInfo
	{
		[Desc("Maximum bloom intensity multiplier (applied at full night).")]
		public readonly float Intensity = 2.5f;

		public override object Create(ActorInitializer init) { return new BloomGlowEffect(this); }
	}

	public class BloomGlowEffect : ConditionalTrait<BloomGlowEffectInfo>, ITick, INotifyActorDisposing
	{
		DayNightCycle dayNight;

		public BloomGlowEffect(BloomGlowEffectInfo info)
			: base(info) { }

		protected override void Created(Actor self)
		{
			base.Created(self);
			dayNight = self.TraitOrDefault<DayNightCycle>();
			Publish();
		}

		void ITick.Tick(Actor self)
		{
			Publish();
		}

		void Publish()
		{
			if (IsTraitDisabled)
			{
				WorldTintState.BloomEnabled = false;
				WorldTintState.BloomStrength = 0f;
				return;
			}

			// NightFactor01 is 0 at noon, 1 at midnight. The trait is only
			// active during dawn/dusk/night phases, so the factor is always
			// well above zero - the lerp gives a smooth ramp instead of a
			// hard on/off across phase boundaries.
			var night = dayNight != null ? dayNight.NightFactor01 : 1f;
			WorldTintState.BloomEnabled = true;
			WorldTintState.BloomStrength = Info.Intensity * night;
		}

		protected override void TraitDisabled(Actor self)
		{
			WorldTintState.BloomEnabled = false;
			WorldTintState.BloomStrength = 0f;
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			WorldTintState.BloomEnabled = false;
			WorldTintState.BloomStrength = 0f;
		}
	}
}
