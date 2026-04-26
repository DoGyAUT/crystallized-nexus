#region Copyright & License Information
/*
 * Crystallized Nexus - FadeOut
 * Gradually fades an actor to transparent, then removes it.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Fades the actor out over time, then removes it.")]
	public class FadeOutInfo : ConditionalTraitInfo
	{
		[Desc("Ticks to stay fully visible before fading starts.")]
		public readonly int Delay = 100;

		[Desc("Ticks for the fade-out transition.")]
		public readonly int FadeDuration = 100;

		public override object Create(ActorInitializer init) { return new FadeOut(this); }
	}

	public class FadeOut : ConditionalTrait<FadeOutInfo>, ITick, IRenderModifier
	{
		int ticks;
		float alpha = 1f;
		bool done;

		public FadeOut(FadeOutInfo info)
			: base(info) { }

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || done)
				return;

			ticks++;

			if (ticks <= Info.Delay)
				return;

			var fadeTicks = ticks - Info.Delay;
			if (Info.FadeDuration > 0)
				alpha = 1f - (float)fadeTicks / Info.FadeDuration;
			else
				alpha = 0f;

			if (alpha <= 0f)
			{
				alpha = 0f;
				done = true;
				self.World.AddFrameEndTask(w => self.Dispose());
			}
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (IsTraitDisabled || alpha >= 1f)
				return r;

			return ModifyAlpha(r);
		}

		IEnumerable<IRenderable> ModifyAlpha(IEnumerable<IRenderable> r)
		{
			foreach (var renderable in r)
			{
				if (renderable is IModifyableRenderable mr)
					yield return mr.WithAlpha(alpha);
				else
					yield return renderable;
			}
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			return bounds;
		}
	}
}
