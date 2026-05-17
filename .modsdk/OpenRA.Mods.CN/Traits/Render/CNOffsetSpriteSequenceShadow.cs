#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.Render
{
	[Desc("Offsets split sprite-sequence shadows independently from their main sprite.")]
	public sealed class CNOffsetSpriteSequenceShadowInfo : ConditionalTraitInfo
	{
		[Desc("World offset applied to decoration renderables emitted by sprite-sequence ShadowStart.")]
		public readonly WVec Offset = WVec.Zero;

		[Desc("Additional Z offset applied to the shadow renderables.")]
		public readonly int ZOffset = 0;

		[Desc("Optional alpha multiplier for the shadow renderables.")]
		public readonly float Alpha = 1f;

		public override object Create(ActorInitializer init) { return new CNOffsetSpriteSequenceShadow(this); }
	}

	public sealed class CNOffsetSpriteSequenceShadow : ConditionalTrait<CNOffsetSpriteSequenceShadowInfo>, IRenderModifier
	{
		readonly CNOffsetSpriteSequenceShadowInfo info;

		public CNOffsetSpriteSequenceShadow(CNOffsetSpriteSequenceShadowInfo info)
			: base(info)
		{
			this.info = info;
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (IsTraitDisabled)
				return r;

			return r.Select(OffsetShadow);
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			return bounds;
		}

		IRenderable OffsetShadow(IRenderable renderable)
		{
			if (!renderable.IsDecoration)
				return renderable;

			var offset = renderable
				.OffsetBy(info.Offset)
				.WithZOffset(renderable.ZOffset + info.ZOffset);

			if (info.Alpha >= 1f || offset is not IModifyableRenderable modifyable)
				return offset;

			return modifyable.WithAlpha(modifyable.Alpha * info.Alpha);
		}
	}
}
