#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.CN.Effects;
using OpenRA.Mods.Cnc.Traits.Render;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;
using CommonUtil = OpenRA.Mods.Common.Util;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Spawns rendered voxel turret/barrel pieces as ballistic debris when the actor dies.")]
	public class VoxelDebrisOnDeathInfo : ConditionalTraitInfo, Requires<RenderVoxelsInfo>, Requires<BodyOrientationInfo>
	{
		[Desc("Chance in percent to spawn each rendered voxel piece.")]
		public readonly int Chance = 100;

		[Desc("Horizontal launch speed range per tick.")]
		public readonly ImmutableArray<WDist> HorizontalVelocity = [new WDist(42), new WDist(112)];

		[Desc("Initial upward velocity range per tick.")]
		public readonly ImmutableArray<WDist> VerticalVelocity = [new WDist(110), new WDist(220)];

		[Desc("Downward velocity added each tick.")]
		public readonly WDist Gravity = new(12);

		[Desc("Lifetime range in ticks.")]
		public readonly ImmutableArray<int> Lifetime = [45, 90];

		[Desc("Yaw spin rate range in WAngle units per tick.")]
		public readonly ImmutableArray<int> YawRate = [-18, 19];

		[Desc("Pitch spin rate range in WAngle units per tick.")]
		public readonly ImmutableArray<int> PitchRate = [-14, 15];

		[Desc("Roll spin rate range in WAngle units per tick.")]
		public readonly ImmutableArray<int> RollRate = [-16, 17];

		[Desc("Screen map bounds used for culling the debris effect.")]
		public readonly Size ScreenMapSize = new(256, 256);

		public override object Create(ActorInitializer init) { return new VoxelDebrisOnDeath(this); }
	}

	public class VoxelDebrisOnDeath : ConditionalTrait<VoxelDebrisOnDeathInfo>, INotifyKilled
	{
		public VoxelDebrisOnDeath(VoxelDebrisOnDeathInfo info)
			: base(info) { }

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || Info.Chance <= 0)
				return;

			var rv = self.TraitOrDefault<RenderVoxels>();
			if (rv == null)
				return;

			var body = self.Trait<BodyOrientation>();
			var camera = new WRot(WAngle.Zero, body.CameraPitch - new WAngle(256), new WAngle(256));
			var lightSource = new WRot(WAngle.Zero, new WAngle(256) - rv.Info.LightPitch, rv.Info.LightYaw);
			var pieces = EnumerateRenderedPieces(self, body).ToArray();
			if (pieces.Length == 0)
				return;

			foreach (var piece in pieces)
			{
				if (Info.Chance < 100 && self.World.LocalRandom.Next(100) >= Info.Chance)
					continue;

				if (!rv.Renderer.ModelCache.HasModelSequence(rv.Image, piece.Sequence))
					continue;

				var model = rv.Renderer.ModelCache.GetModelSequence(rv.Image, piece.Sequence);
				var position = self.CenterPosition + piece.Offset;
				var velocity = RandomVelocity(self.World.LocalRandom);

				self.World.AddFrameEndTask(w => w.Add(new VoxelDebris(
					w,
					model,
					rv.Renderer,
					rv.Info,
					position,
					piece.Rotation,
					velocity,
					camera,
					lightSource,
					self.Owner.InternalName,
					Math.Max(1, CommonUtil.RandomInRange(self.World.LocalRandom, Info.Lifetime)),
					Math.Max(1, Info.Gravity.Length),
					RandomNonZero(self.World.LocalRandom, Info.YawRate, 10),
					RandomNonZero(self.World.LocalRandom, Info.PitchRate, 8),
					RandomNonZero(self.World.LocalRandom, Info.RollRate, 8),
					Info.ScreenMapSize,
					piece.ShowShadow)));
			}
		}

		WVec RandomVelocity(MersenneTwister random)
		{
			var speed = CommonUtil.RandomDistance(random, Info.HorizontalVelocity).Length;
			var vertical = CommonUtil.RandomDistance(random, Info.VerticalVelocity).Length;
			var yaw = new WAngle(random.Next(1024));
			return new WVec(0, -speed, vertical).Rotate(WRot.FromYaw(yaw));
		}

		static int RandomNonZero(MersenneTwister random, ImmutableArray<int> range, int fallback)
		{
			var value = CommonUtil.RandomInRange(random, range);
			if (value != 0)
				return value;

			return random.Next(2) == 0 ? -Math.Abs(fallback) : Math.Abs(fallback);
		}

		static IEnumerable<VoxelDebrisPiece> EnumerateRenderedPieces(Actor self, BodyOrientation body)
		{
			foreach (var turret in self.TraitsImplementing<CNWithVoxelTurret>())
			{
				if (turret.IsTraitDisabled)
					continue;

				var turreted = self.TraitsImplementing<Turreted>().First(t => t.Name == turret.Info.Turret);
				yield return new VoxelDebrisPiece(
					turret.Info.Sequence,
					turreted.Position(self),
					CNTurretRotation(self, body, turreted),
					turret.Info.ShowShadow);
			}

			foreach (var turret in self.TraitsImplementing<WithVoxelTurret>())
			{
				if (turret.IsTraitDisabled)
					continue;

				var turreted = self.TraitsImplementing<Turreted>().First(t => t.Name == turret.Info.Turret);
				yield return new VoxelDebrisPiece(
					turret.Info.Sequence,
					turreted.Position(self),
					turreted.WorldOrientation,
					turret.Info.ShowShadow);
			}

			foreach (var barrel in self.TraitsImplementing<CNWithVoxelBarrel>())
			{
				if (barrel.IsTraitDisabled)
					continue;

				var armament = self.TraitsImplementing<Armament>().First(a => a.Info.Name == barrel.Info.Armament);
				var turreted = self.TraitsImplementing<Turreted>().First(t => t.Name == armament.Info.Turret);
				yield return new VoxelDebrisPiece(
					barrel.Info.Sequence,
					CNBarrelOffset(self, body, barrel.Info, armament, turreted),
					CNBarrelRotation(self, body, barrel.Info, turreted),
					barrel.Info.ShowShadow);
			}

			foreach (var barrel in self.TraitsImplementing<WithVoxelBarrel>())
			{
				if (barrel.IsTraitDisabled)
					continue;

				var armament = self.TraitsImplementing<Armament>().First(a => a.Info.Name == barrel.Info.Armament);
				var turreted = self.TraitsImplementing<Turreted>().First(t => t.Name == armament.Info.Turret);
				var bodyOrientation = body.QuantizeOrientation(self.Orientation);
				var offset = (barrel.Info.LocalOffset + new WVec(-armament.Recoil, WDist.Zero, WDist.Zero))
					.Rotate(turreted.WorldOrientation) + turreted.Offset.Rotate(bodyOrientation);

				yield return new VoxelDebrisPiece(
					barrel.Info.Sequence,
					body.LocalToWorld(offset),
					barrel.Info.LocalOrientation.Rotate(turreted.WorldOrientation),
					barrel.Info.ShowShadow);
			}
		}

		static WRot CNTurretRotation(Actor self, BodyOrientation body, Turreted turreted)
		{
			var dynamics = self.TraitOrDefault<VoxelDynamics>();
			var rot = turreted.WorldOrientation;
			if (dynamics == null)
				return rot;

			var extra = dynamics.GetExtraRotation();
			var bodyOri = body.QuantizeOrientation(self.Orientation);
			var bodyTilted = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOri);
			var turretRelYaw = rot.Yaw - bodyOri.Yaw;
			return WRot.FromYaw(turretRelYaw).Rotate(bodyTilted);
		}

		static WVec CNBarrelOffset(Actor self, BodyOrientation body, CNWithVoxelBarrelInfo info, Armament armament, Turreted turreted)
		{
			var localOffset = info.LocalOffset + new WVec(-armament.Recoil, WDist.Zero, WDist.Zero);
			var bodyOrientation = body.QuantizeOrientation(self.Orientation);
			var dynamics = self.TraitOrDefault<VoxelDynamics>();

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

		static WRot CNBarrelRotation(Actor self, BodyOrientation body, CNWithVoxelBarrelInfo info, Turreted turreted)
		{
			var bodyOri = body.QuantizeOrientation(self.Orientation);
			var dynamics = self.TraitOrDefault<VoxelDynamics>();
			if (dynamics == null)
				return info.LocalOrientation.Rotate(turreted.WorldOrientation);

			var extra = dynamics.GetExtraRotation();
			var bodyTilted = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOri);
			var turretRelYaw = turreted.WorldOrientation.Yaw - bodyOri.Yaw;
			return info.LocalOrientation.Rotate(WRot.FromYaw(turretRelYaw).Rotate(bodyTilted));
		}

		readonly record struct VoxelDebrisPiece(string Sequence, WVec Offset, WRot Rotation, bool ShowShadow);
	}
}
