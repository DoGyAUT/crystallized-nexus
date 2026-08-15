#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Tilts a static sprite back and forth to fake wind movement, without needing animated frames.",
		"The sprite is rotated around a pivot at its base, so the crown travels while the trunk stays planted.",
		"Purely cosmetic: driven by wall-clock time, never by the simulation.")]
	public class CNWindSwayInfo : TraitInfo, Requires<RenderSpritesInfo>
	{
		[Desc("Peak tilt at full gust strength. 1024 units is a full circle, so 10 is roughly 3.5 degrees.",
			"This is the main knob. Note that rotation is quantised to whole units, so small values give",
			"few distinct positions across a sway and start to look stepped: below about 6 the motion",
			"visibly ticks rather than flows.")]
		public readonly WAngle Angle = new(10);

		[Desc("Distance in screen pixels from the sprite centre down to the trunk base, i.e. roughly half",
			"the sprite height. The point the sprite pivots around; the crown travels twice this far.")]
		public readonly int PivotHeight = 24;

		[Desc("Milliseconds for one full sway cycle.")]
		public readonly int Period = 2200;

		[Desc("Milliseconds for one full gust cycle. Modulates the sway amplitude.")]
		public readonly int GustPeriod = 9000;

		[Desc("How strongly a gust swells and calms the sway, in percent of the base amplitude.")]
		public readonly int GustStrength = 45;

		[Desc("Wind direction as a facing in 32 steps, matching CloudSpawner's WindDirection.",
			"Gusts travel across the map along this axis.")]
		public readonly int Direction = 8;

		[Desc("World distance over which a gust's phase wraps once. Larger values make the gust front",
			"sweep across more of the map at a time.")]
		public readonly WDist WaveLength = new(12288);

		[Desc("Skip the sway entirely below this viewport zoom, where the movement is sub-pixel anyway.",
			"0 never skips.")]
		public readonly float MinZoom = 0f;

		public override object Create(ActorInitializer init) { return new CNWindSway(this); }
	}

	public class CNWindSway : IRenderModifier, INotifyCreated
	{
		readonly CNWindSwayInfo info;
		int phase;
		int wavePhase;

		// PERF: refilled and handed back each frame rather than allocating an iterator per tree per frame.
		// Callers materialise the result immediately (see WorldRenderer.GenerateRenderables).
		readonly List<IRenderable> buffer = new(2);

		public CNWindSway(CNWindSwayInfo info)
		{
			this.info = info;
		}

		// Deferred to Created: the actor's position is only reliable once every trait, including the
		// one providing IOccupySpace, has been constructed. Trees never move, so this runs once.
		void INotifyCreated.Created(Actor self)
		{
			// Per-actor phase so neighbouring trees never sway in lockstep.
			var cell = self.Location;
			phase = (cell.X * 0x27D4EB2D ^ cell.Y * 0x165667B1) >> 11 & 1023;

			// Phase offset along the wind axis so a gust visibly travels through a forest
			// instead of the whole map swelling at once.
			var dir = new WVec(0, -1024, 0).Rotate(WRot.FromYaw(WAngle.FromFacing(256 * info.Direction / 32)));
			var pos = self.CenterPosition;
			var along = (pos.X * dir.X + pos.Y * dir.Y) / 1024;
			wavePhase = (int)((long)along * 1024 / info.WaveLength.Length) & 1023;
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (info.Angle.Angle == 0 || wr.Viewport.Zoom < info.MinZoom)
				return r;

			var t = Game.RunTime;

			// Two sines at incommensurate rates, so the loop never reads as a loop.
			var wave = (7 * new WAngle((int)(1024 * t / info.Period) + phase + wavePhase).Sin()
				+ 3 * new WAngle((int)(2359 * t / info.Period) + 2 * phase).Sin()) / 10;

			var gust = 100 + info.GustStrength * new WAngle((int)(1024 * t / info.GustPeriod) + wavePhase).Sin() / 1024;

			var tilt = (int)((long)info.Angle.Angle * wave * gust / (1024 * 100));
			if (tilt == 0)
				return r;

			// The pivot correction itself is applied in screen space inside SpriteRenderable, where it
			// stays sub-pixel accurate. Anything routed through a world-space offset would be rounded to
			// whole pixels and the sprite would jump rather than tilt.
			var rotation = new WAngle(tilt);

			buffer.Clear();
			foreach (var renderable in r)
			{
				if (renderable is SpriteRenderable sr)
					buffer.Add(sr.WithRotation(rotation, info.PivotHeight));
				else
					buffer.Add(renderable);
			}

			return buffer;
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			return bounds;
		}
	}
}
