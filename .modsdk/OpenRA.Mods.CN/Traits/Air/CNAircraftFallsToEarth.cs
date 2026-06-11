#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Immutable;
using OpenRA.Activities;
using OpenRA.GameRules;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	public enum CNAircraftCrashProfile
	{
		Glide,
		Curve,
		Spiral,
		Tumble,
	}

	[Desc("CN aircraft crash behaviour with varied glide, curve, spiral and tumble profiles.")]
	public class CNAircraftFallsToEarthInfo : TraitInfo, IRulesetLoaded, Requires<AircraftInfo>
	{
		[WeaponReference]
		[Desc("Explosion weapon that triggers when hitting ground.")]
		public readonly string Explosion = "UnitExplode";

		[Desc("Weighted list of crash profiles. Duplicate entries increase chance.")]
		public readonly ImmutableArray<CNAircraftCrashProfile> Profiles =
			[CNAircraftCrashProfile.Glide, CNAircraftCrashProfile.Glide, CNAircraftCrashProfile.Curve, CNAircraftCrashProfile.Spiral];

		[Desc("Does the aircraft husk keep moving forward while crashing?")]
		public readonly bool Moves = true;

		[Desc("Forward speed range while crashing.")]
		public readonly ImmutableArray<int> ForwardSpeed = [80, 180];

		[Desc("Initial fall velocity range per tick.")]
		public readonly ImmutableArray<WDist> InitialFallVelocity = [new WDist(48), new WDist(96)];

		[Desc("Fall velocity added every tick.")]
		public readonly WDist FallAcceleration = new(2);

		[Desc("Small yaw change range for Glide crashes.")]
		public readonly ImmutableArray<int> GlideTurnRate = [-1, 2];

		[Desc("Pitch-down rate range for Glide crashes.")]
		public readonly ImmutableArray<int> GlidePitchRate = [1, 4];

		[Desc("Yaw change range for Curve crashes.")]
		public readonly ImmutableArray<int> CurveTurnRate = [-4, 5];

		[Desc("Roll target multiplier applied from curve turn rate.")]
		public readonly int CurveRollMultiplier = 24;

		[Desc("Roll adjustment speed for Curve crashes.")]
		public readonly int CurveRollSpeed = 5;

		[Desc("Initial spin rate range for Spiral crashes.")]
		public readonly ImmutableArray<int> SpiralInitialSpin = [4, 12];

		[Desc("Spin acceleration per tick for Spiral crashes.")]
		public readonly int SpiralAcceleration = 2;

		[Desc("Maximum spin rate for Spiral crashes.")]
		public readonly int SpiralMaxSpin = 34;

		[Desc("Yaw change range for Tumble crashes.")]
		public readonly ImmutableArray<int> TumbleYawRate = [-6, 7];

		[Desc("Pitch rate range for Tumble crashes.")]
		public readonly ImmutableArray<int> TumblePitchRate = [4, 12];

		[Desc("Roll rate range for Tumble crashes.")]
		public readonly ImmutableArray<int> TumbleRollRate = [-14, 15];

		[Desc("Smoke trail image. Empty disables smoke trail.")]
		public readonly string SmokeTrailImage = "durasmoke_medium";

		[SequenceReference(nameof(SmokeTrailImage), allowNullImage: true)]
		public readonly ImmutableArray<string> SmokeTrailSequences = ["idle"];

		[PaletteReference]
		public readonly string SmokeTrailPalette = "effect";

		[Desc("Ticks between smoke trail puffs.")]
		public readonly int SmokeTrailInterval = 3;

		public readonly WVec SmokeTrailOffset = WVec.Zero;
		public readonly ImmutableArray<int> SmokeTrailLifetime = [70, 110];
		public readonly ImmutableArray<WDist> SmokeTrailDrift = [WDist.Zero, new WDist(8)];
		public readonly ImmutableArray<WDist> SmokeTrailRise = [new WDist(4), new WDist(16)];
		public readonly int SmokeTrailTurnRate = 1;
		public readonly int SmokeTrailRandomRate = 16;

		[Desc("Fire trail images. Empty disables fire trail. One image is chosen randomly per puff.")]
		public readonly ImmutableArray<string> FireTrailImages = ["huskfire01a", "huskfire02a", "huskfire03a"];

		public readonly ImmutableArray<string> FireTrailSequences = ["idle-overlay"];

		[PaletteReference]
		public readonly string FireTrailPalette = "effect";

		[Desc("Ticks between fire trail puffs.")]
		public readonly int FireTrailInterval = 8;

		public readonly WVec FireTrailOffset = WVec.Zero;
		public readonly ImmutableArray<int> FireTrailLifetime = [18, 34];
		public readonly ImmutableArray<WDist> FireTrailDrift = [WDist.Zero, new WDist(4)];
		public readonly ImmutableArray<WDist> FireTrailRise = [WDist.Zero, new WDist(8)];
		public readonly int FireTrailTurnRate = 1;
		public readonly int FireTrailRandomRate = 10;

		public WeaponInfo ExplosionWeapon { get; private set; }

		public override object Create(ActorInitializer init) { return new CNAircraftFallsToEarth(init, this); }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (string.IsNullOrEmpty(Explosion))
				return;

			var weaponToLower = Explosion.ToLowerInvariant();
			if (!rules.Weapons.TryGetValue(weaponToLower, out var weapon))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{weaponToLower}'");

			ExplosionWeapon = weapon;
		}
	}

	public class CNAircraftFallsToEarth : IEffectiveOwner, INotifyCreated
	{
		readonly CNAircraftFallsToEarthInfo info;
		readonly Player effectiveOwner;

		public CNAircraftFallsToEarth(ActorInitializer init, CNAircraftFallsToEarthInfo info)
		{
			this.info = info;
			effectiveOwner = init.GetValue<EffectiveOwnerInit, Player>(info, init.Self.Owner);
		}

		bool IEffectiveOwner.Disguised => true;
		Player IEffectiveOwner.Owner => effectiveOwner;

		void INotifyCreated.Created(Actor self)
		{
			self.QueueActivity(false, new CNAircraftCrash(self, info));
		}
	}

	sealed class CNAircraftCrash : Activity
	{
		readonly Aircraft aircraft;
		readonly CNAircraftFallsToEarthInfo info;
		readonly CNAircraftCrashProfile profile;
		readonly int forwardSpeed;
		readonly int turnRate;
		readonly int pitchRate;
		readonly int rollRate;
		readonly int spinDirection;

		WDist fallVelocity;
		int spin;
		int smokeTicks;
		int fireTicks;

		public CNAircraftCrash(Actor self, CNAircraftFallsToEarthInfo info)
		{
			this.info = info;
			IsInterruptible = false;
			aircraft = self.Trait<Aircraft>();

			var random = self.World.SharedRandom;
			profile = info.Profiles.Length == 0 ? CNAircraftCrashProfile.Glide : info.Profiles.Random(random);
			forwardSpeed = Util.RandomInRange(random, info.ForwardSpeed);
			fallVelocity = Util.RandomDistance(random, info.InitialFallVelocity);
			spinDirection = random.Next(2) == 0 ? -1 : 1;

			switch (profile)
			{
				case CNAircraftCrashProfile.Curve:
					turnRate = RandomNonZero(random, info.CurveTurnRate, 3);
					break;
				case CNAircraftCrashProfile.Spiral:
					spin = Math.Max(1, Util.RandomInRange(random, info.SpiralInitialSpin)) * spinDirection;
					break;
				case CNAircraftCrashProfile.Tumble:
					turnRate = RandomNonZero(random, info.TumbleYawRate, 4);
					pitchRate = Math.Max(1, Util.RandomInRange(random, info.TumblePitchRate)) * spinDirection;
					rollRate = RandomNonZero(random, info.TumbleRollRate, 8);
					break;
				default:
					turnRate = Util.RandomInRange(random, info.GlideTurnRate);
					pitchRate = Math.Max(0, Util.RandomInRange(random, info.GlidePitchRate));
					break;
			}
		}

		public override bool Tick(Actor self)
		{
			if (self.World.Map.DistanceAboveTerrain(self.CenterPosition).Length <= 0)
			{
				info.ExplosionWeapon?.Impact(Target.FromPos(self.CenterPosition), self);
				self.Kill(self);
				Cancel(self);
				return true;
			}

			UpdateOrientation();
			SpawnTrails(self);

			var move = info.Moves ? aircraft.FlyStep(forwardSpeed, aircraft.Facing) : WVec.Zero;
			move -= new WVec(WDist.Zero, WDist.Zero, fallVelocity);
			fallVelocity += info.FallAcceleration;

			aircraft.SetPosition(self, aircraft.CenterPosition + move);
			return false;
		}

		void UpdateOrientation()
		{
			switch (profile)
			{
				case CNAircraftCrashProfile.Curve:
					aircraft.Facing = new WAngle(aircraft.Facing.Angle + turnRate);
					aircraft.Roll = Util.TickFacing(
						aircraft.Roll,
						new WAngle(-turnRate * info.CurveRollMultiplier),
						new WAngle(Math.Max(1, info.CurveRollSpeed)));
					aircraft.Pitch = new WAngle(aircraft.Pitch.Angle + 1);
					break;
				case CNAircraftCrashProfile.Spiral:
					if (Math.Abs(spin) < Math.Max(1, info.SpiralMaxSpin))
						spin += info.SpiralAcceleration * spinDirection;

					aircraft.Facing = new WAngle(aircraft.Facing.Angle + spin);
					aircraft.Roll = new WAngle(aircraft.Roll.Angle + spin * 2);
					aircraft.Pitch = new WAngle(aircraft.Pitch.Angle + Math.Abs(spin) / 4);
					break;
				case CNAircraftCrashProfile.Tumble:
					aircraft.Facing = new WAngle(aircraft.Facing.Angle + turnRate);
					aircraft.Roll = new WAngle(aircraft.Roll.Angle + rollRate);
					aircraft.Pitch = new WAngle(aircraft.Pitch.Angle + pitchRate);
					break;
				default:
					aircraft.Facing = new WAngle(aircraft.Facing.Angle + turnRate);
					aircraft.Pitch = new WAngle(aircraft.Pitch.Angle + pitchRate);
					break;
			}
		}

		void SpawnTrails(Actor self)
		{
			if (!string.IsNullOrEmpty(info.SmokeTrailImage) && --smokeTicks <= 0)
			{
				SpawnFloating(self, info.SmokeTrailImage, info.SmokeTrailSequences, info.SmokeTrailPalette,
					info.SmokeTrailLifetime, info.SmokeTrailDrift, info.SmokeTrailRise,
					info.SmokeTrailTurnRate, info.SmokeTrailRandomRate, info.SmokeTrailOffset);
				smokeTicks = Math.Max(1, info.SmokeTrailInterval);
			}

			if (info.FireTrailImages.Length > 0 && --fireTicks <= 0)
			{
				SpawnFloating(self, info.FireTrailImages.Random(self.World.LocalRandom), info.FireTrailSequences, info.FireTrailPalette,
					info.FireTrailLifetime, info.FireTrailDrift, info.FireTrailRise,
					info.FireTrailTurnRate, info.FireTrailRandomRate, info.FireTrailOffset);
				fireTicks = Math.Max(1, info.FireTrailInterval);
			}
		}

		static void SpawnFloating(
			Actor self,
			string image,
			ImmutableArray<string> sequences,
			string palette,
			ImmutableArray<int> lifetime,
			ImmutableArray<WDist> drift,
			ImmutableArray<WDist> rise,
			int turnRate,
			int randomRate,
			WVec offset)
		{
			var pos = self.CenterPosition + offset;
			var facing = WAngle.FromFacing(self.World.LocalRandom.Next(256));

			self.World.AddFrameEndTask(w => w.Add(new FloatingSprite(
				self,
				image,
				sequences,
				palette,
				false,
				lifetime,
				drift,
				rise,
				turnRate,
				Math.Max(1, randomRate),
				pos,
				facing)));
		}

		static int RandomNonZero(MersenneTwister random, ImmutableArray<int> range, int fallback)
		{
			var value = Util.RandomInRange(random, range);
			if (value != 0)
				return value;

			return random.Next(2) == 0 ? -Math.Abs(fallback) : Math.Abs(fallback);
		}
	}
}
