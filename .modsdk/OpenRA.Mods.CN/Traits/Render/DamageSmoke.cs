#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Immutable;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Emits rising smoke puffs from damaged units and buildings, mimicking Tiberian Sun behavior.")]
	public class DamageSmokeInfo : ConditionalTraitInfo
	{
		[Desc("Damage state at which smoke begins.")]
		public readonly DamageState MinimumDamageState = DamageState.Heavy;

		[Desc("Damage state at which smoke rate switches to CriticalInterval.")]
		public readonly DamageState CriticalDamageState = DamageState.Critical;

		[Desc("Ticks between smoke puffs at MinimumDamageState.")]
		public readonly int Interval = 60;

		[Desc("Ticks between smoke puffs at CriticalDamageState.")]
		public readonly int CriticalInterval = 20;

		[Desc("Spawn positions relative to actor center. Add multiple for large buildings. Include Z to offset height.")]
		public readonly ImmutableArray<WVec> Offsets = [WVec.Zero];

		[Desc("Emit from all Offsets simultaneously instead of choosing one at random.")]
		public readonly bool AllOffsets = false;

		[Desc("Image used for smoke animation.")]
		public readonly string Image = "smoke";

		[SequenceReference(nameof(Image))]
		[Desc("Sequences to play — one is chosen at random per puff.")]
		public readonly ImmutableArray<string> Sequences = ["1"];

		[PaletteReference]
		[Desc("Palette to render smoke with.")]
		public readonly string Palette = "effect";

		[Desc("Lifetime of each smoke puff in ticks. Two values for a random range.")]
		public readonly ImmutableArray<int> Lifetime = [50, 70];

		[Desc("Upward rise speed per tick-cluster. Two values for a random range.")]
		public readonly ImmutableArray<WDist> Rise = [new WDist(384), new WDist(640)];

		[Desc("Random horizontal drift speed. Two values for a random range.")]
		public readonly ImmutableArray<WDist> Drift = [WDist.Zero, new WDist(96)];

		[Desc("Ticks between random drift direction changes.")]
		public readonly int RandomRate = 4;

		[Desc("Rate at which horizontal drift direction rotates each RandomRate interval.")]
		public readonly int TurnRate = 3;

		public override object Create(ActorInitializer init) { return new DamageSmoke(init.Self, this); }
	}

	public class DamageSmoke : ConditionalTrait<DamageSmokeInfo>, INotifyCreated, ITick
	{
		IHealth health;
		int ticks;

		public DamageSmoke(Actor self, DamageSmokeInfo info)
			: base(info) { }

		void INotifyCreated.Created(Actor self)
		{
			health = self.TraitOrDefault<IHealth>();
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || IsTraitDisabled || health == null || health.IsDead)
				return;

			var state = health.DamageState;
			if (state < Info.MinimumDamageState)
			{
				ticks = 0;
				return;
			}

			var interval = state >= Info.CriticalDamageState
				? Info.CriticalInterval
				: Info.Interval;

			if (--ticks > 0)
				return;

			ticks = interval;

			if (Info.AllOffsets)
			{
				foreach (var offset in Info.Offsets)
					SpawnSmoke(self, offset);
			}
			else
			{
				var offset = Info.Offsets.Length == 1
					? Info.Offsets[0]
					: Info.Offsets[self.World.LocalRandom.Next(Info.Offsets.Length)];

				SpawnSmoke(self, offset);
			}
		}

		void SpawnSmoke(Actor self, WVec offset)
		{
			var pos = self.CenterPosition + offset;
			var facing = WAngle.FromFacing(self.World.LocalRandom.Next(256));

			self.World.AddFrameEndTask(w => w.Add(new FloatingSprite(
				self,
				Info.Image,
				Info.Sequences,
				Info.Palette,
				false,
				Info.Lifetime,
				Info.Drift,
				Info.Rise,
				Info.TurnRate,
				Info.RandomRate,
				pos,
				facing)));
		}
	}
}
