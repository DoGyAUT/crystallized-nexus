#region Copyright & License Information
/*
 * Copyright 2019-2025 The OpenHV Developers (see CREDITS)
 * Adapted for Crystallized Nexus.
 * This file is part of OpenHV, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.CN.Traits;

namespace OpenRA.Mods.CN.Effects
{
	sealed class Cloud : IEffect, ISpatiallyPartitionable
	{
		readonly Animation animation;
		readonly string palette;
		readonly WPos edge;
		readonly int facing;
		readonly int renderZOffset;
		readonly ImmutableArray<WDist> speed;
		readonly WDist closeEnough;
		WPos position;

		public Cloud(World world, Animation animation, WPos position, WPos edge, int facing, CloudSpawnerInfo info)
		{
			this.animation = animation;
			this.position = position;
			this.edge = edge;
			this.facing = facing;
			renderZOffset = info.RenderZOffset;

			palette = info.Palette;
			speed = info.Speed;
			closeEnough = info.CloseEnough;

			EnableSoftOverlayFiltering(animation);
			world.ScreenMap.Add(this, position, animation.Image);
		}

		static void EnableSoftOverlayFiltering(Animation animation)
		{
			var sequence = animation.CurrentSequence;
			if (sequence == null)
				return;

			for (var frame = 0; frame < sequence.Length; frame++)
			{
				sequence.GetSprite(frame).Sheet.GetTexture().ScaleFilter = TextureScaleFilter.Linear;
				var shadow = sequence.GetShadow(frame, WAngle.Zero);
				if (shadow != null)
					shadow.Sheet.GetTexture().ScaleFilter = TextureScaleFilter.Linear;
			}
		}

		IEnumerable<IRenderable> IEffect.Render(WorldRenderer r)
		{
			// Atmospheric overlays (cloud shadows / godrays) are sky-level: never
			// hide them by ground shroud/fog. The old single-point ShroudObscures
			// test popped the whole large sprite along cell-quantised shroud
			// edges as it drifted -> hard straight "cut" artifact.
			if (!Game.Settings.Graphics.CloudShadows)
				return SpriteRenderable.None;

			// NOT AsDecoration: godrays must be depth-tested so buildings /
			// cliffs / ramps occlude them (no bleed-through). They use an
			// Additive blend mode, for which the engine already disables depth
			// writes (Sdl2GraphicsContext.SetBlendMode), so they still do not
			// clip explosions / FX drawn afterwards.
			return animation.Render(position, WVec.Zero, renderZOffset, r.Palette(palette));
		}

		void IEffect.Tick(World world)
		{
			if ((edge - position).Length < closeEnough.Length)
			{
				world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); });
				return;
			}

			var forward = Common.Util.RandomDistance(world.SharedRandom, speed).Length;

			// Needs to be defined the same way delta is defined in CloudSpawner.SpawnCloud to ensure facing consistency.
			var offset = new WVec(0, -forward, 0);
			offset = offset.Rotate(WRot.FromFacing(facing));

			animation.Tick();
			position += offset;
			world.ScreenMap.Update(this, position, animation.Image);
		}
	}
}
