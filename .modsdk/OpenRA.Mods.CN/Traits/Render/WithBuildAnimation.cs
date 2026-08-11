#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.Render
{
	[Desc("Plays a sprite-body sequence (default 'make') stretched across the full duration of an ",
		"UnderConstruction build-up, instead of at the sequence's own speed like WithMakeAnimation. ",
		"Reverts to the normal idle sequence once construction completes.")]
	public class WithBuildAnimationInfo : TraitInfo, Requires<WithSpriteBodyInfo>, Requires<UnderConstructionInfo>
	{
		[SequenceReference]
		[Desc("Sequence name to play during construction.")]
		public readonly string Sequence = "make";

		[Desc("Apply to sprite bodies with these names.")]
		public readonly ImmutableArray<string> BodyNames = ["body"];

		public override object Create(ActorInitializer init) => new WithBuildAnimation(this);
	}

	public class WithBuildAnimation : INotifyCreated, ITick
	{
		readonly WithBuildAnimationInfo info;

		UnderConstruction underConstruction;
		WithSpriteBody[] wsbs;
		bool started;
		bool finished;

		public WithBuildAnimation(WithBuildAnimationInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			underConstruction = self.Trait<UnderConstruction>();
			wsbs = self.TraitsImplementing<WithSpriteBody>()
				.Where(w => info.BodyNames.Contains(w.Info.Name))
				.ToArray();
		}

		void ITick.Tick(Actor self)
		{
			if (finished)
				return;

			if (underConstruction.IsConstructing)
			{
				if (!started)
				{
					started = true;
					foreach (var wsb in wsbs)
					{
						var sequence = wsb.NormalizeSequence(self, info.Sequence);
						if (!wsb.DefaultAnimation.HasSequence(sequence))
							continue;

						// Map construction progress (0..1) onto the sequence frames.
						wsb.DefaultAnimation.PlayFetchIndex(sequence, () =>
						{
							var length = wsb.DefaultAnimation.GetSequence(sequence).Length;
							return Math.Min(length - 1, (int)(underConstruction.Progress * length));
						});
					}
				}
			}
			else if (started)
			{
				// Construction completed (or actor destroyed): restore the idle animation.
				finished = true;
				foreach (var wsb in wsbs)
					wsb.CancelCustomAnimation(self);
			}
		}
	}
}
