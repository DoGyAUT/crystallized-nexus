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
	[Desc("Repeatedly spawns a one-shot sprite animation at random intervals.",
		"Useful for fire flicker/light simulation, periodic sparks, etc.")]
	public class PeriodicSpriteEffectInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Image containing the sequence.")]
		public readonly string Image = null;

		[FieldLoader.Require]
		[SequenceReference(nameof(Image))]
		[Desc("Sequences to play. One is chosen at random each time.")]
		public readonly ImmutableArray<string> Sequences;

		[PaletteReference]
		[Desc("Palette to render with.")]
		public readonly string Palette = "effect";

		[Desc("Minimum ticks between spawns.")]
		public readonly int MinInterval = 20;

		[Desc("Maximum ticks between spawns.")]
		public readonly int MaxInterval = 80;

		[Desc("Offset from actor center for the effect spawn position.")]
		public readonly WVec Offset = WVec.Zero;

		[Desc("Render even when the actor is under fog.")]
		public readonly bool VisibleThroughFog = false;

		public override object Create(ActorInitializer init) { return new PeriodicSpriteEffect(init.Self, this); }
	}

	public class PeriodicSpriteEffect : ConditionalTrait<PeriodicSpriteEffectInfo>, ITick
	{
		int ticks;

		public PeriodicSpriteEffect(Actor self, PeriodicSpriteEffectInfo info)
			: base(info)
		{
			ticks = self.World.LocalRandom.Next(info.MinInterval, info.MaxInterval);
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || IsTraitDisabled)
				return;

			if (--ticks > 0)
				return;

			ticks = self.World.LocalRandom.Next(Info.MinInterval, Info.MaxInterval);

			var sequence = Info.Sequences[self.World.LocalRandom.Next(Info.Sequences.Length)];
			var pos = self.CenterPosition + Info.Offset;

			self.World.AddFrameEndTask(w => w.Add(
				new SpriteEffect(pos, self.World, Info.Image, sequence, Info.Palette, Info.VisibleThroughFog)));
		}
	}
}
