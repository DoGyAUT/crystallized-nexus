#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Projectiles;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Projectiles
{
	[Desc("LaserZap variant that renders the beam in front of world geometry.")]
	public class CNLaserZapInfo : IProjectileInfo
	{
		[Desc("The width of the zap.")]
		public readonly WDist Width = new(86);

		[Desc("The shape of the beam. Accepts values Cylindrical or Flat.")]
		public readonly BeamRenderableShape Shape = BeamRenderableShape.Cylindrical;

		[Desc("Equivalent to sequence ZOffset. Controls Z sorting.")]
		public readonly int ZOffset = 0;

		[Desc("The maximum duration (in ticks) of the beam's existence.")]
		public readonly int Duration = 10;

		[Desc("Total time-frame in ticks that the beam deals damage every DamageInterval.")]
		public readonly int DamageDuration = 1;

		[Desc("The number of ticks between the beam causing warhead impacts in its area of effect.")]
		public readonly int DamageInterval = 1;

		public readonly bool UsePlayerColor = false;

		[Desc("Color of the beam.")]
		public readonly Color Color = Color.Red;

		[Desc("Beam follows the target.")]
		public readonly bool TrackTarget = true;

		[Desc("The maximum/constant/incremental inaccuracy used in conjunction with the InaccuracyType property.")]
		public readonly WDist Inaccuracy = WDist.Zero;

		[Desc("Controls the way inaccuracy is calculated.")]
		public readonly InaccuracyType InaccuracyType = InaccuracyType.Maximum;

		[Desc("Beam can be blocked.")]
		public readonly bool Blockable = false;

		[Desc("Draw a second beam (for 'glow' effect).")]
		public readonly bool SecondaryBeam = false;

		[Desc("The width of the zap.")]
		public readonly WDist SecondaryBeamWidth = new(86);

		[Desc("The shape of the beam. Accepts values Cylindrical or Flat.")]
		public readonly BeamRenderableShape SecondaryBeamShape = BeamRenderableShape.Cylindrical;

		[Desc("Equivalent to sequence ZOffset. Controls Z sorting.")]
		public readonly int SecondaryBeamZOffset = 0;

		public readonly bool SecondaryBeamUsePlayerColor = false;

		[Desc("Color of the secondary beam.")]
		public readonly Color SecondaryBeamColor = Color.Red;

		[Desc("Draw a subtle heat shimmer around the beam.")]
		public readonly bool HeatEffect = true;

		[Desc("Number of animated heat shimmer lines.")]
		public readonly int HeatLines = 3;

		[Desc("Maximum heat shimmer offset in screen pixels.")]
		public readonly int HeatAmplitude = 3;

		[Desc("Heat shimmer wave length in screen pixels.")]
		public readonly int HeatWavelength = 44;

		[Desc("Heat shimmer animation speed.")]
		public readonly int HeatSpeed = 14;

		[Desc("Number of segments used for each heat shimmer line.")]
		public readonly int HeatSegments = 12;

		[Desc("Heat shimmer line width in screen pixels.")]
		public readonly int HeatWidth = 1;

		[Desc("Color of the heat shimmer lines.")]
		public readonly Color HeatColor = Color.FromArgb(34, 255, 232, 180);

		[Desc("Impact animation.")]
		public readonly string HitAnim = null;

		[SequenceReference(nameof(HitAnim), allowNullImage: true)]
		[Desc("Sequence of impact animation to use.")]
		public readonly string HitAnimSequence = "idle";

		[PaletteReference]
		public readonly string HitAnimPalette = "effect";

		[Desc("Image containing launch effect sequence.")]
		public readonly string LaunchEffectImage = null;

		[SequenceReference(nameof(LaunchEffectImage), allowNullImage: true)]
		[Desc("Launch effect sequence to play.")]
		public readonly string LaunchEffectSequence = null;

		[PaletteReference]
		[Desc("Palette to use for launch effect.")]
		public readonly string LaunchEffectPalette = "effect";

		public IProjectile Create(ProjectileArgs args)
		{
			var c = UsePlayerColor ? args.SourceActor.OwnerColor() : Color;
			return new CNLaserZap(this, args, c);
		}
	}

	public class CNLaserZap : IProjectile, IEffect, IEffectAboveShroud, ISync
	{
		sealed class ForegroundBeamRenderable : IRenderable, IFinalizedRenderable
		{
			readonly WVec length;
			readonly BeamRenderableShape shape;
			readonly WDist width;
			readonly Color color;

			public ForegroundBeamRenderable(WPos pos, int zOffset, in WVec length, BeamRenderableShape shape, WDist width, Color color)
			{
				Pos = pos;
				ZOffset = zOffset;
				this.length = length;
				this.shape = shape;
				this.width = width;
				this.color = color;
			}

			public WPos Pos { get; }
			public int ZOffset { get; }
			public bool IsDecoration => true;

			public IRenderable WithZOffset(int newOffset) { return new ForegroundBeamRenderable(Pos, newOffset, length, shape, width, color); }
			public IRenderable OffsetBy(in WVec vec) { return new ForegroundBeamRenderable(Pos + vec, ZOffset, length, shape, width, color); }
			public IRenderable AsDecoration() { return this; }
			public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }

			public void Render(WorldRenderer wr)
			{
				var vecLength = length.Length;
				if (vecLength == 0)
					return;

				if (shape == BeamRenderableShape.Flat)
				{
					var delta = length * width.Length / (2 * vecLength);
					var corner = new WVec(-delta.Y, delta.X, delta.Z);
					var a = wr.Screen3DPosition(Pos - corner);
					var b = wr.Screen3DPosition(Pos + corner);
					var c = wr.Screen3DPosition(Pos + corner + length);
					var d = wr.Screen3DPosition(Pos - corner + length);
					Game.Renderer.WorldRgbaColorRenderer.FillRect(a, b, c, d, color, ignoreWorldTint: true);
				}
				else
				{
					var start = wr.Screen3DPosition(Pos);
					var end = wr.Screen3DPosition(Pos + length);
					var screenWidth = wr.ScreenVector(new WVec(width, WDist.Zero, WDist.Zero))[0];
					Game.Renderer.WorldRgbaColorRenderer.DrawLine(start, end, screenWidth, color, ignoreWorldTint: true);
				}
			}

			public void RenderDebugGeometry(WorldRenderer wr) { }
			public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
		}

		sealed class HeatRenderable : IRenderable, IFinalizedRenderable
		{
			readonly WVec length;
			readonly int tick;
			readonly int lines;
			readonly int amplitude;
			readonly int wavelength;
			readonly int speed;
			readonly int segments;
			readonly int width;
			readonly Color color;

			public HeatRenderable(WPos pos, int zOffset, in WVec length, int tick, CNLaserZapInfo info)
				: this(pos, zOffset, length, tick, info.HeatLines, info.HeatAmplitude, info.HeatWavelength,
					info.HeatSpeed, info.HeatSegments, info.HeatWidth, info.HeatColor)
			{
			}

			HeatRenderable(WPos pos, int zOffset, in WVec length, int tick, int lines, int amplitude,
				int wavelength, int speed, int segments, int width, Color color)
			{
				Pos = pos;
				ZOffset = zOffset;
				this.length = length;
				this.tick = tick;
				this.lines = lines;
				this.amplitude = amplitude;
				this.wavelength = wavelength;
				this.speed = speed;
				this.segments = segments;
				this.width = width;
				this.color = color;
			}

			public WPos Pos { get; }
			public int ZOffset { get; }
			public bool IsDecoration => true;

			public IRenderable WithZOffset(int newOffset)
			{
				return new HeatRenderable(Pos, newOffset, length, tick, lines, amplitude, wavelength, speed, segments, width, color);
			}

			public IRenderable OffsetBy(in WVec vec)
			{
				return new HeatRenderable(Pos + vec, ZOffset, length, tick, lines, amplitude, wavelength, speed, segments, width, color);
			}

			public IRenderable AsDecoration() { return this; }
			public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }

			public void Render(WorldRenderer wr)
			{
				if (lines <= 0 || amplitude <= 0 || width <= 0 || segments <= 0)
					return;

				var startF = wr.Screen3DPosition(Pos);
				var endF = wr.Screen3DPosition(Pos + length);
				var dx = endF.X - startF.X;
				var dy = endF.Y - startF.Y;
				var distance = Math.Sqrt(dx * dx + dy * dy);

				if (distance <= 1)
					return;

				var nx = (float)(-dy / distance);
				var ny = (float)(dx / distance);
				var stepCount = Math.Max(2, segments);
				var waveLength = Math.Max(8, wavelength);
				var alpha = color.A;

				for (var line = 0; line < lines; line++)
				{
					var points = new float3[stepCount + 1];
					var phase = tick * speed * 0.025f + line * 2.11f;
					var side = line % 2 == 0 ? 1f : -1f;
					var lineScale = Math.Max(0.25f, 1f - 0.12f * line);
					var lineAlpha = (int)(alpha * Math.Max(0.35f, lineScale));
					var lineColor = Color.FromArgb(lineAlpha, color);

					for (var i = 0; i <= stepCount; i++)
					{
						var t = i / (float)stepCount;
						var envelope = (float)Math.Sin(t * Math.PI);
						var wave = (float)Math.Sin(t * distance / waveLength * Math.PI * 2 + phase);
						var offset = side * wave * amplitude * envelope * lineScale;
						var x = startF.X + dx * t + nx * offset;
						var y = startF.Y + dy * t + ny * offset;
						points[i] = new float3(x, y, startF.Z);
					}

					Game.Renderer.WorldRgbaColorRenderer.DrawLine(points, width, lineColor, false, BlendMode.Alpha, ignoreWorldTint: true);
				}
			}

			public void RenderDebugGeometry(WorldRenderer wr) { }
			public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
		}

		readonly ProjectileArgs args;
		readonly CNLaserZapInfo info;
		readonly Animation hitanim;
		readonly Color color;
		readonly Color secondaryColor;
		readonly bool hasLaunchEffect;
		int ticks;
		int interval;
		bool showHitAnim;

		[VerifySync]
		WPos target;

		[VerifySync]
		WPos source;

		public CNLaserZap(CNLaserZapInfo info, ProjectileArgs args, Color color)
		{
			this.args = args;
			this.info = info;
			this.color = color;
			secondaryColor = info.SecondaryBeamUsePlayerColor ? args.SourceActor.OwnerColor() : info.SecondaryBeamColor;
			target = args.PassiveTarget;
			source = args.Source;

			if (info.Inaccuracy.Length > 0)
			{
				var maxInaccuracyOffset = OpenRA.Mods.Common.Util.GetProjectileInaccuracy(info.Inaccuracy.Length, info.InaccuracyType, args);
				target += WVec.FromPDF(args.SourceActor.World.SharedRandom, 2) * maxInaccuracyOffset / 1024;
			}

			if (!string.IsNullOrEmpty(info.HitAnim))
			{
				hitanim = new Animation(args.SourceActor.World, info.HitAnim);
				showHitAnim = true;
			}

			hasLaunchEffect = !string.IsNullOrEmpty(info.LaunchEffectImage) && !string.IsNullOrEmpty(info.LaunchEffectSequence);
		}

		public void Tick(World world)
		{
			source = args.CurrentSource();

			if (hasLaunchEffect && ticks == 0)
				world.AddFrameEndTask(w => w.Add(new SpriteEffect(args.CurrentSource, args.CurrentMuzzleFacing, world,
					info.LaunchEffectImage, info.LaunchEffectSequence, info.LaunchEffectPalette)));

			if (info.TrackTarget && args.GuidedTarget.IsValidFor(args.SourceActor))
				target = args.Weapon.TargetActorCenter ? args.GuidedTarget.CenterPosition : args.GuidedTarget.Positions.ClosestToIgnoringPath(source);

			if (info.Blockable && BlocksProjectiles.AnyBlockingActorsBetween(world, args.SourceActor.Owner, source, target, info.Width, out var blockedPos))
				target = blockedPos;

			if (ticks < info.DamageDuration && --interval <= 0)
			{
				var warheadArgs = new WarheadArgs(args)
				{
					ImpactOrientation = new WRot(WAngle.Zero, OpenRA.Mods.Common.Util.GetVerticalAngle(source, target), args.CurrentMuzzleFacing()),
					ImpactPosition = target,
				};

				args.Weapon.Impact(Target.FromPos(target), warheadArgs);
				interval = info.DamageInterval;
			}

			if (showHitAnim)
			{
				if (ticks == 0)
					hitanim.PlayThen(info.HitAnimSequence, () => showHitAnim = false);

				hitanim.Tick();
			}

			if (++ticks >= info.Duration && !showHitAnim)
				world.AddFrameEndTask(w => w.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			yield break;
		}

		public IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr)
		{
			if (wr.World.FogObscures(target) && wr.World.FogObscures(source))
				yield break;

			if (ticks < info.Duration)
			{
				if (info.HeatEffect)
					yield return new HeatRenderable(source, info.ZOffset - 1, target - source, ticks, info);

				var rc = Color.FromArgb((info.Duration - ticks) * color.A / info.Duration, color);
				yield return new ForegroundBeamRenderable(source, info.ZOffset, target - source, info.Shape, info.Width, rc);

				if (info.SecondaryBeam)
				{
					var src = Color.FromArgb((info.Duration - ticks) * secondaryColor.A / info.Duration, secondaryColor);
					yield return new ForegroundBeamRenderable(source, info.SecondaryBeamZOffset, target - source,
						info.SecondaryBeamShape, info.SecondaryBeamWidth, src);
				}
			}

			if (showHitAnim)
				foreach (var r in hitanim.Render(target, wr.Palette(info.HitAnimPalette)))
					yield return r;
		}
	}
}
