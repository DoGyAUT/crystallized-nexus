#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;
using Util = OpenRA.Mods.Common.Util;

namespace OpenRA.Mods.CN.Projectiles
{
	[Desc("Projectile with customisable acceleration vector, recieve dead actor speed by using range modifier, used as aircraft husk.")]
	public class ProjectileHuskInfo : IProjectileInfo
	{
		public readonly string Image = null;

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Loop a randomly chosen sequence of Image from this list while falling.")]
		public readonly ImmutableArray<string> Sequences = ["idle"];

		[PaletteReference(nameof(IsPlayerPalette))]
		[Desc("The palette used to draw this projectile.")]
		public readonly string Palette = "effect";

		[Desc("Palette is a player palette BaseName")]
		public readonly bool IsPlayerPalette = false;

		[Desc("Does this projectile have a shadow?")]
		public readonly bool Shadow = false;

		[Desc("Color to draw shadow if Shadow is true.")]
		public readonly Color ShadowColor = Color.FromArgb(140, 0, 0, 0);

		[Desc("Projectile movement vector per tick (forward, right, up), use negative values for opposite directions.")]
		public readonly WVec Velocity = WVec.Zero;

		[Desc("The X of the speed becomes dead actor speed by using range modifier, coop with trait SpawnHuskEffectOnDeath.")]
		public readonly bool UseRangeModifierAsVelocityX = true;

		[Desc("Movement random factor on Velocity. Don't use negative value!!!")]
		public readonly WVec? VelocityRandomFactor = null;

		[Desc("Value added to Velocity every tick when spin is activated.")]
		public readonly WVec AccelerationWhenSpin = new(0, 0, -10);

		[Desc("Value added to Velocity every tickwhen spin is NOT activated.")]
		public readonly WVec Acceleration = new(0, 0, -10);

		[Desc("Chance of Spin. Activate Spin.")]
		public readonly int SpinChance = 100;

		[Desc("When X speed is lower than this, Spin.")]
		public readonly int SpinWhenVelocityXIsLowerThan = 0;

		[Desc("Limit the maximum spin (in angle units per tick) that can be achieved.",
			"0 Disables spinning.")]
		public readonly int MaximumSpinSpeed = 0;

		[Desc("Spin acceleration.")]
		public readonly int SpinAcc = 0;

		[Desc("begin spin speed.")]
		public readonly int Spin = 0;

		[Desc("Revert the Y of the speed, and X, Y of acceleration at 50% randomness.")]
		public readonly bool HorizontalRevert = false;

		[Desc("Trail animation.")]
		public readonly string TrailImage = null;

		[SequenceReference(nameof(TrailImage), allowNullImage: true)]
		[Desc("Loop a randomly chosen sequence of TrailImage from this list while this projectile is moving.")]
		public readonly ImmutableArray<string> TrailSequences = ["idle"];

		[Desc("Interval in ticks between each spawned Trail animation.")]
		public readonly int TrailInterval = 2;

		[Desc("Delay in ticks until trail animation is spawned.")]
		public readonly int TrailDelay = 0;

		[PaletteReference(nameof(TrailUsePlayerPalette))]
		[Desc("Palette used to render the trail sequence.")]
		public readonly string TrailPalette = "effect";

		[Desc("Use the Player Palette to render the trail sequence.")]
		public readonly bool TrailUsePlayerPalette = false;

		[Desc("When set, display a line behind the actor. Length is measured in ticks after appearing.")]
		public readonly int ContrailLength = 0;

		[Desc("Time (in ticks) after which the line should appear. Controls the distance to the actor.")]
		public readonly int ContrailDelay = 1;

		[Desc("Equivalent to sequence ZOffset. Controls Z sorting.")]
		public readonly int ContrailZOffset = 2047;

		[Desc("Thickness of the emitted line at the start of the contrail.")]
		public readonly WDist ContrailStartWidth = new(64);

		[Desc("Thickness of the emitted line at the end of the contrail. Will default to " + nameof(ContrailStartWidth) + " if left undefined")]
		public readonly WDist? ContrailEndWidth = null;

		[Desc("RGB color at the contrail start.")]
		public readonly Color ContrailStartColor = Color.White;

		[Desc("Use player remap color instead of a custom color at the contrail the start.")]
		public readonly bool ContrailStartColorUsePlayerColor = false;

		[Desc("The alpha value [from 0 to 255] of color at the contrail the start.")]
		public readonly int ContrailStartColorAlpha = 255;

		[Desc("RGB color at the contrail end. Will default to " + nameof(ContrailStartColor) + " if left undefined")]
		public readonly Color? ContrailEndColor;

		[Desc("Use player remap color instead of a custom color at the contrail end.")]
		public readonly bool ContrailEndColorUsePlayerColor = false;

		[Desc("The alpha value [from 0 to 255] of color at the contrail end.")]
		public readonly int ContrailEndColorAlpha = 0;

		[Desc("Up to how many times does this projectile bounce when touching ground without hitting a target.",
			"0 implies exploding on contact with the originally targeted position.")]
		public readonly ImmutableArray<int> BounceCounts = ImmutableArray<int>.Empty;

		[Desc("Modify speed of each bounce by this percentage of previous speed on X, Y, Z. It must be non-negative number.")]
		public readonly WVec BounceVelocityPercentageModifier = new(80, 80, 50);

		[Desc("Terrain where the projectile explodes instead of bouncing.")]
		public readonly FrozenSet<string> InvalidBounceTerrain = [];

		[Desc("The projectile can only remain this long (in ticks), leave empty or set to -1 to disable.")]
		public readonly ImmutableArray<int> ExistTicks = ImmutableArray<int>.Empty;

		public IProjectile Create(ProjectileArgs args) { return new ProjectileHusk(this, args); }
	}

	public class ProjectileHusk : IProjectile, ISync
	{
		readonly ProjectileHuskInfo info;
		readonly Animation anim;
		readonly ProjectileArgs args;
		readonly string trailPalette;

		readonly float3 shadowColor;
		readonly float shadowAlpha;
		readonly int spinAcc;
		readonly int maxSpin;

		WVec velocity;
		WVec acceleration;
		WAngle facing;
		int spin;
		WDist dat;
		int remainingTicks;
		int remainingBounces;

		readonly ContrailRenderable contrail;

		[VerifySync]
		WPos pos, lastPos;
		int smokeTicks;

		public ProjectileHusk(ProjectileHuskInfo info, ProjectileArgs args)
		{
			this.info = info;
			this.args = args;
			pos = args.Source;
			facing = args.Facing;
			var world = args.SourceActor.World;
			dat = world.Map.DistanceAboveTerrain(pos);

			var vx = info.UseRangeModifierAsVelocityX && args.RangeModifiers.Length > 0 ? args.RangeModifiers[0] : info.Velocity.X;
			var vec = info.VelocityRandomFactor != null ? new WVec(vx + world.SharedRandom.Next(info.VelocityRandomFactor.Value.X), info.Velocity.Y + world.SharedRandom.Next(info.VelocityRandomFactor.Value.Y), info.Velocity.Z + world.SharedRandom.Next(info.VelocityRandomFactor.Value.Z)) : new WVec(vx, info.Velocity.Y, info.Velocity.Z);

			if (info.HorizontalRevert && world.SharedRandom.Next(2) == 0)
			{
				velocity = new WVec(-vec.Y, -vec.X, vec.Z);
				if (info.MaximumSpinSpeed > 0 && (Math.Abs(velocity.Y) < info.SpinWhenVelocityXIsLowerThan || world.SharedRandom.Next(1, 101) <= info.SpinChance))
				{
					acceleration = new WVec(-info.AccelerationWhenSpin.Y, info.AccelerationWhenSpin.X, info.AccelerationWhenSpin.Z);
					spin = -info.Spin;
					spinAcc = -info.SpinAcc;
					maxSpin = -info.MaximumSpinSpeed;
				}
				else
					acceleration = new WVec(-info.Acceleration.Y, info.Acceleration.X, info.Acceleration.Z);
			}
			else
			{
				velocity = new WVec(vec.Y, -vec.X, vec.Z);
				if (info.MaximumSpinSpeed > 0 && (Math.Abs(velocity.Y) < info.SpinWhenVelocityXIsLowerThan || world.SharedRandom.Next(1, 101) <= info.SpinChance))
				{
					acceleration = new WVec(info.AccelerationWhenSpin.Y, -info.AccelerationWhenSpin.X, info.AccelerationWhenSpin.Z);
					spin = info.Spin;
					spinAcc = info.SpinAcc;
					maxSpin = info.MaximumSpinSpeed;
				}
				else
					acceleration = new WVec(info.Acceleration.Y, -info.Acceleration.X, info.Acceleration.Z);
			}

			velocity = velocity.Rotate(WRot.FromYaw(facing));
			acceleration = acceleration.Rotate(WRot.FromYaw(facing));

			if (!string.IsNullOrEmpty(info.Image))
			{
				anim = new Animation(world, info.Image, GetEffectiveFacing);
				anim.PlayRepeating(info.Sequences.Random(world.SharedRandom));
			}

			shadowColor = new float3(info.ShadowColor.R, info.ShadowColor.G, info.ShadowColor.B) / 255f;
			shadowAlpha = info.ShadowColor.A / 255f;

			trailPalette = info.TrailPalette;
			if (info.TrailUsePlayerPalette)
				trailPalette += args.SourceActor.Owner.InternalName;
			smokeTicks = info.TrailDelay;

			if (info.ContrailLength > 0)
			{
				var startcolor = Color.FromArgb(info.ContrailStartColorAlpha, info.ContrailStartColor);
				var endcolor = Color.FromArgb(info.ContrailEndColorAlpha, info.ContrailEndColor ?? startcolor);
				contrail = new ContrailRenderable(world, args.SourceActor,
					startcolor, info.ContrailStartColorUsePlayerColor,
					endcolor, info.ContrailEndColor == null ? info.ContrailStartColorUsePlayerColor : info.ContrailEndColorUsePlayerColor,
					info.ContrailStartWidth,
					info.ContrailEndWidth ?? info.ContrailStartWidth,
					info.ContrailLength, info.ContrailDelay, info.ContrailZOffset);
			}

			remainingBounces = info.BounceCounts.Length > 0 ? info.BounceCounts[world.SharedRandom.Next(info.BounceCounts.Length)] : 0;
			remainingTicks = info.ExistTicks.Length > 0 ? info.ExistTicks[world.SharedRandom.Next(info.ExistTicks.Length)] : -1;
		}

		public void Tick(World world)
		{
			lastPos = pos;
			pos += velocity;
			dat = world.Map.DistanceAboveTerrain(pos);

			if (maxSpin != 0)
			{
				var spinAngle = new WAngle(spin);
				facing += spinAngle;
				acceleration = acceleration.Rotate(WRot.FromYaw(spinAngle));
				spin = Math.Abs(spin) < Math.Abs(maxSpin) ? spin + spinAcc : maxSpin;
			}

			velocity += acceleration;

			if (info.ContrailLength > 0)
				contrail.Update(pos);

			// Explodes
			if (remainingTicks == 0)
			{
				Explode(world);
			}
			else if (dat.Length <= 0 && remainingBounces <= 0)
			{
				// if the projectile hits the horizontal ground (not cliff), we will fix it to the surface
				// we use "dat.Length > velocity.Z" for a simple test on hits the horizontal ground or cliff
				if (dat.Length > velocity.Z)
					pos -= new WVec(0, 0, dat.Length);

				Explode(world);
			}
			else if (dat.Length <= 0 && remainingBounces > 0)
			{
				var cell = world.Map.CellContaining(pos);
				if ((!world.Map.Contains(cell)) || info.InvalidBounceTerrain.Contains(world.Map.GetTerrainInfo(cell).Type))
					Explode(world);

				// if the projectile bounces on the cliff, we will revert the X and Y of the speed, while Z does not revert.
				// if the projectile bounces on the horizontal ground, we will only revert the Z of the speed, while X and Y does not revert.
				var xyDeflectpara = 1;
				if (dat.Length <= velocity.Z)
					xyDeflectpara = -1;

				velocity = new WVec(xyDeflectpara * velocity.X * info.BounceVelocityPercentageModifier.X / 100, xyDeflectpara * velocity.Y * info.BounceVelocityPercentageModifier.Y / 100, -velocity.Z * xyDeflectpara * info.BounceVelocityPercentageModifier.Z / 100);
				remainingBounces--;
			}

			if (!string.IsNullOrEmpty(info.TrailImage) && --smokeTicks < 0)
			{
				world.AddFrameEndTask(w => w.Add(new SpriteEffect(pos, GetEffectiveFacing(), w,
					info.TrailImage, info.TrailSequences.Random(world.SharedRandom), trailPalette)));

				smokeTicks = info.TrailInterval;
			}

			anim?.Tick();

			if (remainingTicks > 0)
				remainingTicks--;
		}

		WAngle GetEffectiveFacing()
		{
			return facing;
		}

		protected virtual void Explode(World world)
		{
			if (info.ContrailLength > 0)
				world.AddFrameEndTask(w => w.Add(new ContrailFader(pos, contrail)));

			world.AddFrameEndTask(w => w.Remove(this));

			var warheadArgs = new WarheadArgs(args)
			{
				ImpactOrientation = new WRot(WAngle.Zero, Util.GetVerticalAngle(lastPos, pos), args.Facing),
				ImpactPosition = pos,
			};

			args.Weapon.Impact(Target.FromPos(pos), warheadArgs);
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (anim == null)
				yield break;

			if (info.ContrailLength > 0)
				yield return contrail;

			var world = args.SourceActor.World;
			if (!world.FogObscures(pos))
			{
				var paletteName = info.Palette;
				if (paletteName != null && info.IsPlayerPalette)
					paletteName += args.SourceActor.Owner.InternalName;

				var palette = wr.Palette(paletteName);

				if (info.Shadow)
				{
					var shadowPos = pos - new WVec(0, 0, dat.Length);
					foreach (var r in anim.Render(shadowPos, palette))
						yield return ((IModifyableRenderable)r)
							.WithTint(shadowColor, ((IModifyableRenderable)r).TintModifiers | TintModifiers.ReplaceColor)
							.WithAlpha(shadowAlpha);
				}

				foreach (var r in anim.Render(pos, palette))
					yield return r;
			}
		}
	}
}
