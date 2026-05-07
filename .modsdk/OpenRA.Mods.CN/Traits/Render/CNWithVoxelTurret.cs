#region Copyright & License Information
/*
 * Crystallized Nexus - CNWithVoxelTurret
 * Drop-in replacement for WithVoxelTurret that applies VoxelDynamics tilt.
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
	[Desc("Renders a voxel turret with optional VoxelDynamics pitch/roll tilt. " +
		"Drop-in replacement for WithVoxelTurret.")]
	public class CNWithVoxelTurretInfo : ConditionalTraitInfo,
		IRenderActorPreviewVoxelsInfo, Requires<RenderVoxelsInfo>, Requires<TurretedInfo>
	{
		[Desc("Voxel sequence name to use.")]
		public readonly string Sequence = "turret";

		[Desc("Turreted 'Turret' key to display.")]
		public readonly string Turret = "primary";

		[Desc("Defines if the Voxel should have a shadow.")]
		public readonly bool ShowShadow = true;

		public override object Create(ActorInitializer init) { return new CNWithVoxelTurret(init.Self, this); }

		public IEnumerable<ModelAnimation> RenderPreviewVoxels(IModelCache cache,
			ActorPreviewInitializer init, RenderVoxelsInfo rv, string image, Func<WRot> orientation, int facings, PaletteReference p)
		{
			if (!EnabledByDefault)
				yield break;

			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var t = init.Actor.TraitInfos<TurretedInfo>()
				.First(tt => tt.Turret == Turret);

			var model = cache.GetModelSequence(image, Sequence);
			var turretOffset = t.PreviewPosition(init, orientation);
			var turretOrientation = t.PreviewOrientation(init, orientation, facings);
			var dynamics = init.GetOrDefault<VoxelDynamicsPreviewInit>();
			yield return new ModelAnimation(model,
				() => turretOffset() + (dynamics != null ? dynamics.Offset.GetExtraOffset() : WVec.Zero),
				() =>
				{
					var rot = turretOrientation();
					if (dynamics == null)
						return rot;

					var extra = dynamics.Rotation.GetExtraRotation();
					var bodyOri = body.QuantizeOrientation(orientation(), facings);
					var bodyTilted = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOri);
					var turretRelYaw = rot.Yaw - bodyOri.Yaw;
					return WRot.FromYaw(turretRelYaw).Rotate(bodyTilted);
				},
				() => false, () => 0, ShowShadow);
		}
	}

	public class CNWithVoxelTurret : ConditionalTrait<CNWithVoxelTurretInfo>
	{
		readonly Turreted turreted;
		readonly BodyOrientation body;

		public CNWithVoxelTurret(Actor self, CNWithVoxelTurretInfo info)
			: base(info)
		{
			turreted = self.TraitsImplementing<Turreted>()
				.First(tt => tt.Name == Info.Turret);
			body = self.Trait<BodyOrientation>();

			var rv = self.Trait<RenderVoxels>();
			var dynamics = self.TraitOrDefault<VoxelDynamics>();

			rv.Add(new ModelAnimation(rv.Renderer.ModelCache.GetModelSequence(rv.Image, Info.Sequence),
				() => turreted.Position(self),
				() =>
				{
					var rot = turreted.WorldOrientation;
					if (dynamics == null) return rot;

					// Apply extra tilt in body-local space, then add turret's relative yaw on top.
					// This keeps pitch/roll consistent with the body regardless of turret facing.
					var extra = dynamics.GetExtraRotation();
					var bodyOri = body.QuantizeOrientation(self.Orientation);
					var bodyTilted = new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(bodyOri);
					var turretRelYaw = rot.Yaw - bodyOri.Yaw;
					return WRot.FromYaw(turretRelYaw).Rotate(bodyTilted);
				},
				() => IsTraitDisabled, () => 0, info.ShowShadow));
		}
	}
}
