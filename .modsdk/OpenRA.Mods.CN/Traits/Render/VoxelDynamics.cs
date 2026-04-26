#region Copyright & License Information
/*
 * Crystallized Nexus - VoxelDynamics
 * Provides dynamic pitch/roll tilting for voxel units.
 */
#endregion

using System;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	public interface IVoxelExtraRotation
	{
		WRot GetExtraRotation();
	}

	[Desc("Provides impact-tilt and acceleration-tilt for voxel units. " +
		"Add CNWithVoxelBody/Turret/Barrel instead of their vanilla counterparts.")]
	public class VoxelDynamicsInfo : ConditionalTraitInfo
	{
		[Desc("Maximum pitch (forward/backward) in WAngle units (1024 = 360 degrees).")]
		public readonly int MaxPitch = 48;

		[Desc("Maximum roll (sideways) in WAngle units.")]
		public readonly int MaxRoll = 48;

		[Desc("Spring return speed (1-30). Higher = snappier.")]
		public readonly int SpringStrength = 12;

		[Desc("Damping factor (0-90). Higher = less oscillation.")]
		public readonly int Damping = 70;

		[Desc("If non-empty, only hits with these damage types trigger impact tilt. Empty = all damage.")]
		public readonly BitSet<DamageType> ImpactDamageTypes = default;

		[Desc("Pitch impulse on damage hit in WAngle units.")]
		public readonly int ImpactPitch = 72;

		[Desc("Pitch impulse when firing (recoil). 0 = disabled.")]
		public readonly int RecoilPitch = 40;

		[Desc("Roll impulse when firing (recoil). 0 = disabled.")]
		public readonly int RecoilRoll = 20;

		[Desc("Roll impulse on damage hit in WAngle units.")]
		public readonly int ImpactRoll = 56;

		[Desc("Ticks the impact impulse holds before spring takes over.")]
		public readonly int ImpactHoldTicks = 6;

		[Desc("Pitch from acceleration/deceleration (scale 0-500). 0 = disabled. " +
			"Tilts on speed change only, returns to neutral at constant speed.")]
		public readonly int AccelerationPitchScale = 0;

		[Desc("Invert the acceleration pitch direction. Use for hover/aircraft that lean forward when accelerating.")]
		public readonly bool InvertPitch = false;

		[Desc("Constant pitch offset in WAngle units. Positive = nose down, negative = nose up.")]
		public readonly int PitchOffset = 0;

		[Desc("Constant roll offset in WAngle units.")]
		public readonly int RollOffset = 0;

		[Desc("Roll from turning (scale 0-200). 0 = disabled. " +
			"Tilts on turn rate change only, returns to neutral at constant turn speed.")]
		public readonly int TurnRollScale = 0;

		[Desc("If true, roll is driven by current turn rate instead of turn acceleration. " +
			"Useful for bikes and hovercraft that should visibly lean during sustained curves.")]
		public readonly bool ContinuousTurnRoll = false;

		public override object Create(ActorInitializer init) { return new VoxelDynamics(init.Self, this); }
	}

	public class VoxelDynamics : ConditionalTrait<VoxelDynamicsInfo>, IVoxelExtraRotation, ITick, INotifyDamage, INotifyAttack
	{
		// Ring buffer: smooth velocity over 8 ticks to handle OpenRA's per-subcell movement
		const int VelSamples = 8;

		// All values in WAngle.Angle units (integers), scaled *100 for precision
		int pitch100;
		int roll100;
		int pitchVel;
		int rollVel;
		int targetPitch100;
		int targetRoll100;
		int impactHoldRemaining;
		readonly WPos[] posHistory = new WPos[VelSamples];
		int posHistoryIndex;
		int lastFwd100;    // previous smoothed forward velocity for acceleration calculation
		int lastYawDelta;  // previous yaw delta for turn acceleration calculation
		WAngle lastYaw;
		bool posInitialized;

		public VoxelDynamics(Actor self, VoxelDynamicsInfo info)
			: base(info) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (IsTraitDisabled) return;
			if (Info.RecoilPitch == 0 && Info.RecoilRoll == 0) return;

			var maxP = Info.MaxPitch * 100;
			var maxR = Info.MaxRoll * 100;

			// Project recoil into body pitch/roll axes based on turret facing
			// so a rear-facing turret kicks the body forward instead of backward
			var turretYaw = WAngle.Zero;
			if (a != null)
			{
				var turreted = self.TraitsImplementing<Turreted>()
					.FirstOrDefault(t => t.Name == a.Info.Turret);
				if (turreted != null)
					turretYaw = turreted.WorldOrientation.Yaw - self.Orientation.Yaw;
			}

			var yawRad = turretYaw.Angle * 2.0 * Math.PI / 1024.0;
			var pitchFactor = (int)(Math.Cos(yawRad) * 100);
			var rollFactor = (int)(Math.Sin(yawRad) * 100);

			if (Info.RecoilPitch > 0)
			{
				var pitchImpulse = Info.RecoilPitch * pitchFactor / 100;
				targetPitch100 = Math.Clamp(targetPitch100 + pitchImpulse * 100, -maxP, maxP);
			}

			if (Info.RecoilRoll > 0)
			{
				var barrelSign = barrel != null ? (barrel.Offset.Y >= 0 ? 1 : -1) : 1;
				var rollImpulse = Info.RecoilRoll * rollFactor / 100;
				targetRoll100 = Math.Clamp(targetRoll100 + barrelSign * rollImpulse * 100, -maxR, maxR);
			}

			impactHoldRemaining = Math.Max(impactHoldRemaining, Info.ImpactHoldTicks);
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || e.Damage.Value <= 0) return;
			if (!Info.ImpactDamageTypes.IsEmpty && !e.Damage.DamageTypes.Overlaps(Info.ImpactDamageTypes))
				return;

			int pitchSign, rollSign;
			if (e.Attacker != null && !e.Attacker.Disposed && e.Attacker.IsInWorld)
			{
				var delta = self.CenterPosition - e.Attacker.CenterPosition;

				// Pitch always tilts the same direction on impact (away from body front),
				// matching the recoil convention — positive pitch from firing, negative from hit.
				pitchSign = -1;

				// Roll is directional: transform world-space delta into body-local right axis.
				var bodyYaw = self.Orientation.Yaw.Angle * 2.0 * Math.PI / 1024.0;
				var localRight = delta.X * Math.Cos(bodyYaw) - delta.Y * Math.Sin(bodyYaw);
				rollSign = localRight >= 0 ? -1 : 1;
			}
			else
			{
				pitchSign = self.World.SharedRandom.Next(2) == 0 ? 1 : -1;
				rollSign = self.World.SharedRandom.Next(2) == 0 ? 1 : -1;
			}

			var maxP = Info.MaxPitch * 100;
			var maxR = Info.MaxRoll * 100;

			targetPitch100 = Math.Clamp(targetPitch100 + pitchSign * Info.ImpactPitch * 100, -maxP, maxP);
			targetRoll100 = Math.Clamp(targetRoll100 + rollSign * Info.ImpactRoll * 100, -maxR, maxR);
			impactHoldRemaining = Info.ImpactHoldTicks;
		}

		void ITick.Tick(Actor self)
		{
			var currentPos = self.CenterPosition;
			var currentYaw = self.Orientation.Yaw;

			if (!posInitialized)
			{
				for (var i = 0; i < VelSamples; i++)
					posHistory[i] = currentPos;
				lastYaw = currentYaw;
				lastFwd100 = 0;
				lastYawDelta = 0;
				posInitialized = true;
				return;
			}

			// Store current position in ring buffer
			posHistory[posHistoryIndex] = currentPos;
			posHistoryIndex = (posHistoryIndex + 1) % VelSamples;

			if (!IsTraitDisabled)
			{
				// Acceleration-driven pitch
				if (Info.AccelerationPitchScale > 0)
				{
					var oldest = posHistory[posHistoryIndex];
					var delta = currentPos - oldest;
					var yawRad = currentYaw.Angle * 2.0 * Math.PI / 1024.0;
					var fwd100 = (int)((delta.X * Math.Sin(yawRad) + delta.Y * Math.Cos(yawRad)) * 100);
					if (Info.InvertPitch) fwd100 = -fwd100;

					// Acceleration = change in velocity → tilts only on speed change
					var accel100 = lastFwd100 - fwd100;
					lastFwd100 = fwd100;

					var maxP = Info.MaxPitch * 100;
					if (impactHoldRemaining <= 0)
						targetPitch100 = Math.Clamp(targetPitch100 + accel100 * Info.AccelerationPitchScale / 10000, -maxP, maxP);
				}

				// Turn-driven roll
				if (Info.TurnRollScale > 0)
				{
					var yawDelta = (currentYaw - lastYaw).Angle;
					if (yawDelta > 512) yawDelta -= 1024;
					lastYaw = currentYaw;

					var maxR = Info.MaxRoll * 100;
					if (impactHoldRemaining <= 0)
					{
						if (Info.ContinuousTurnRoll)
							targetRoll100 = Math.Clamp(-yawDelta * Info.TurnRollScale * 100 / 512, -maxR, maxR);
						else
						{
							// yawAccel = change in turn rate → tilts only when turn rate changes
							var yawAccel = yawDelta - lastYawDelta;
							targetRoll100 = Math.Clamp(targetRoll100 + yawAccel * Info.TurnRollScale * 100 / 1000, -maxR, maxR);
						}
					}

					lastYawDelta = yawDelta;
				}
			}

			// Decay impact target toward zero
			if (impactHoldRemaining > 0)
				impactHoldRemaining--;
			else
			{
				targetPitch100 = targetPitch100 * (100 - Info.SpringStrength) / 100;
				targetRoll100 = targetRoll100 * (100 - Info.SpringStrength) / 100;
			}

			// Spring-damper
			var maxPitch = Info.MaxPitch * 100;
			var maxRoll = Info.MaxRoll * 100;

			var pitchForce = (targetPitch100 - pitch100) * Info.SpringStrength / 10;
			pitchVel = pitchVel * (100 - Info.Damping) / 100 + pitchForce;
			pitch100 = Math.Clamp(pitch100 + pitchVel / 10, -maxPitch, maxPitch);
			if (targetPitch100 == 0 && Math.Abs(pitch100) < 50 && Math.Abs(pitchVel) < 10) { pitch100 = 0; pitchVel = 0; }

			var rollForce = (targetRoll100 - roll100) * Info.SpringStrength / 10;
			rollVel = rollVel * (100 - Info.Damping) / 100 + rollForce;
			roll100 = Math.Clamp(roll100 + rollVel / 10, -maxRoll, maxRoll);
			if (targetRoll100 == 0 && Math.Abs(roll100) < 50 && Math.Abs(rollVel) < 10) { roll100 = 0; rollVel = 0; }
		}

		/// <summary>
		/// Returns extra rotation in WORLD space (not body space).
		/// The caller must combine this with body orientation correctly.
		/// </summary>
		public WRot GetExtraRotation()
		{
			return new WRot(
				new WAngle(roll100 / 100 + Info.RollOffset),
				new WAngle(pitch100 / 100 + Info.PitchOffset),
				WAngle.Zero);
		}
	}
}
