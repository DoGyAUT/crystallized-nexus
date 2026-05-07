#region Copyright & License Information
/*
 * Crystallized Nexus - CNWithVoxelBarrel
 * Drop-in replacement for WithVoxelBarrel that applies VoxelDynamics tilt.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.Traits.Render;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Renders a voxel barrel with optional VoxelDynamics pitch/roll tilt. " +
		"Drop-in replacement for WithVoxelBarrel.")]
	public class CNWithVoxelBarrelInfo : ConditionalTraitInfo,
		IRenderActorPreviewVoxelsInfo, Requires<RenderVoxelsInfo>, Requires<ArmamentInfo>, Requires<TurretedInfo>
	{
		[Desc("Voxel sequence name to use.")]
		public readonly string Sequence = "barrel";

		[Desc("Armament to use for recoil.")]
		public readonly string Armament = "primary";

		[Desc("Visual offset.")]
		public readonly WVec LocalOffset = WVec.Zero;

		[Desc("Rotate the barrel relative to the body.")]
		public readonly WRot LocalOrientation = WRot.None;

		[Desc("Defines if the Voxel should have a shadow.")]
		public readonly bool ShowShadow = true;

		[Desc("Maximum upward barrel elevation angle in WAngle units (1024 = 360°). Default ~30°. Set both to 0 to disable elevation.")]
		public readonly WAngle MaxElevation = new WAngle(85);

		[Desc("Maximum downward barrel depression angle in WAngle units (negative value). Default ~-15°.")]
		public readonly WAngle MinElevation = new WAngle(-42);

		[Desc("Barrel elevation change speed toward target angle, in WAngle units per tick.")]
		public readonly int ElevationSpeed = 4;

		[Desc("Barrel elevation return speed toward neutral when no target, in WAngle units per tick.")]
		public readonly int ElevationReturnSpeed = 2;

		[Desc("Ticks to hold last elevation after the last attack event before returning to neutral.")]
		public readonly int ElevationHoldTicks = 15;

		public override object Create(ActorInitializer init) { return new CNWithVoxelBarrel(init.Self, this); }

		public IEnumerable<ModelAnimation> RenderPreviewVoxels(IModelCache cache,
			ActorPreviewInitializer init, RenderVoxelsInfo rv, string image, Func<WRot> orientation, int facings, PaletteReference p)
		{
			if (!EnabledByDefault)
				yield break;

			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var armament = init.Actor.TraitInfos<ArmamentInfo>()
				.First(a => a.Name == Armament);
			var t = init.Actor.TraitInfos<TurretedInfo>()
				.First(tt => tt.Turret == armament.Turret);

			var model = cache.GetModelSequence(image, Sequence);
			var rawTurretOrientation = t.PreviewOrientation(init, orientation, facings);
			var dynamics = init.GetOrDefault<VoxelDynamicsPreviewInit>();

			WRot BodyOrientation() => body.QuantizeOrientation(orientation(), facings);

			WRot TurretOrientation()
			{
				var rot = rawTurretOrientation();
				if (dynamics == null)
					return rot;

				var bodyOri = BodyOrientation();
				var extra = dynamics.Rotation.GetExtraRotation();
				var bodyTilted = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOri);
				var turretRelYaw = rot.Yaw - bodyOri.Yaw;
				return WRot.FromYaw(turretRelYaw).Rotate(bodyTilted);
			}

			WVec BarrelOffset()
			{
				if (dynamics == null)
					return body.LocalToWorld(t.Offset + LocalOffset.Rotate(rawTurretOrientation()));

				var bodyOri = BodyOrientation();
				var extra = dynamics.Rotation.GetExtraRotation();
				var bodyTilted = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOri);
				return body.LocalToWorld(LocalOffset.Rotate(TurretOrientation()) + t.Offset.Rotate(bodyTilted)) +
					dynamics.Offset.GetExtraOffset();
			}

			WRot BarrelOrientation() => LocalOrientation.Rotate(TurretOrientation());

			yield return new ModelAnimation(model, BarrelOffset, BarrelOrientation, () => false, () => 0, ShowShadow);
		}
	}

	public class CNWithVoxelBarrel : ConditionalTrait<CNWithVoxelBarrelInfo>, ITick, INotifyAttack
	{
		readonly Actor self;
		readonly Armament armament;
		readonly Turreted turreted;
		readonly BodyOrientation body;
		readonly VoxelDynamics dynamics;

		int currentElevation;  // WAngle units, signed: positive = up
		int targetElevation;
		int ticksSinceLastAttack;
		Target cachedTarget = Target.Invalid;

		public CNWithVoxelBarrel(Actor self, CNWithVoxelBarrelInfo info)
			: base(info)
		{
			this.self = self;
			body = self.Trait<BodyOrientation>();
			dynamics = self.TraitOrDefault<VoxelDynamics>();
			armament = self.TraitsImplementing<Armament>()
				.First(a => a.Info.Name == Info.Armament);
			turreted = self.TraitsImplementing<Turreted>()
				.First(tt => tt.Name == armament.Info.Turret);

			var rv = self.Trait<RenderVoxels>();
			rv.Add(new ModelAnimation(rv.Renderer.ModelCache.GetModelSequence(rv.Image, Info.Sequence),
				BarrelOffset, BarrelRotation,
				() => IsTraitDisabled, () => 0, info.ShowShadow));
		}

		void ITick.Tick(Actor self)
		{
			ticksSinceLastAttack++;

			var hasTarget = cachedTarget.IsValidFor(self);
			if (hasTarget)
			{
				// Target still alive: track elevation every tick, regardless of hold timer.
				ComputeElevation(cachedTarget.CenterPosition);
			}
			else if (ticksSinceLastAttack > Info.ElevationHoldTicks)
			{
				// Target gone and hold expired: return to neutral.
				cachedTarget = Target.Invalid;
				targetElevation = 0;
			}
			// else: target gone but still in hold window — keep last elevation.

			var diff = targetElevation - currentElevation;
			if (diff == 0)
				return;

			var speed = hasTarget ? Info.ElevationSpeed : Info.ElevationReturnSpeed;
			var step = Math.Sign(diff) * speed;
			if (Math.Abs(step) > Math.Abs(diff))
				step = diff;
			currentElevation += step;
		}

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (a == armament && target.IsValidFor(self))
			{
				cachedTarget = target;
				ticksSinceLastAttack = 0;
			}
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (a == armament && target.IsValidFor(self))
			{
				cachedTarget = target;
				ticksSinceLastAttack = 0;
			}
		}

		void ComputeElevation(WPos targetPos)
		{
			var delta = targetPos - self.CenterPosition;
			var horizontalDist = delta.HorizontalLength;
			if (horizontalDist == 0)
				return;

			// ArcTan returns [0, 1024). Convert to signed [-512, 512] before clamping,
			// otherwise targets below self produce large positive values (e.g. 982 instead of -42).
			var rawAngle = WAngle.ArcTan(delta.Z, horizontalDist).Angle;
			var signedPitch = rawAngle > 512 ? rawAngle - 1024 : rawAngle;
			targetElevation = Math.Max(Info.MinElevation.Angle, Math.Min(Info.MaxElevation.Angle, signedPitch));
		}

		WVec BarrelOffset()
		{
			var localOffset = Info.LocalOffset + new WVec(-armament.Recoil, WDist.Zero, WDist.Zero);
			var bodyOrientation = body.QuantizeOrientation(self.Orientation);

			// Apply extra dynamics rotation to body orientation for correct barrel positioning
			if (dynamics != null)
			{
				var extra = dynamics.GetExtraRotation();
				var dynRot = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOrientation);
				localOffset = localOffset.Rotate(turreted.WorldOrientation) + turreted.Offset.Rotate(dynRot);
			}
			else
				localOffset = localOffset.Rotate(turreted.WorldOrientation) + turreted.Offset.Rotate(bodyOrientation);

			return body.LocalToWorld(localOffset);
		}

		WRot BarrelRotation()
		{
			var bodyOri = body.QuantizeOrientation(self.Orientation);
			WRot baseRot;
			if (dynamics == null)
				baseRot = Info.LocalOrientation.Rotate(turreted.WorldOrientation);
			else
			{
				// Keep the static barrel orientation in the same composed space as the tilted turret,
				// then apply elevation as a separate axis-angle rotation around the barrel's local left axis.
				var extra = dynamics.GetExtraRotation();
				var bodyTilted = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOri);
				var turretRelYaw = turreted.WorldOrientation.Yaw - bodyOri.Yaw;
				var tiltedTurret = WRot.FromYaw(turretRelYaw).Rotate(bodyTilted);
				baseRot = Info.LocalOrientation.Rotate(tiltedTurret);
			}

			// Temporarily disable dynamic barrel elevation and render the barrel
			// in its neutral orientation until the pitch logic is revisited.
			return baseRot;
		}
	}
}
