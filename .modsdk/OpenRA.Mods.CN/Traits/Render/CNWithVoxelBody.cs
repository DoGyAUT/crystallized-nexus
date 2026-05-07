#region Copyright & License Information
/*
 * Crystallized Nexus - CNWithVoxelBody
 * Drop-in replacement for WithVoxelBody that applies VoxelDynamics tilt.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.Traits.Render;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Renders a voxel body with optional VoxelDynamics pitch/roll tilt. " +
		"Drop-in replacement for WithVoxelBody.")]
	public class CNWithVoxelBodyInfo : ConditionalTraitInfo,
		IRenderActorPreviewVoxelsInfo, Requires<RenderVoxelsInfo>
	{
		public readonly string Sequence = "idle";
		public readonly WVec Offset;

		[Desc("Defines if the Voxel should have a shadow.")]
		public readonly bool ShowShadow = true;

		public override object Create(ActorInitializer init) { return new CNWithVoxelBody(init.Self, this); }

		public IEnumerable<ModelAnimation> RenderPreviewVoxels(IModelCache cache,
			ActorPreviewInitializer init, RenderVoxelsInfo rv, string image, Func<WRot> orientation, int facings, PaletteReference p)
		{
			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var dynamics = init.GetOrDefault<VoxelDynamicsPreviewInit>();
			var model = cache.GetModelSequence(image, Sequence);
			yield return new ModelAnimation(model,
				() => Offset + (dynamics != null ? dynamics.Offset.GetExtraOffset() : WVec.Zero),
				() =>
				{
					var baseOrientation = body.QuantizeOrientation(orientation(), facings);
					if (dynamics == null)
						return baseOrientation;

					var extra = dynamics.Rotation.GetExtraRotation();
					return new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(baseOrientation);
				},
				() => false, () => 0, ShowShadow);
		}
	}

	public class CNWithVoxelBody : ConditionalTrait<CNWithVoxelBodyInfo>, IAutoMouseBounds
	{
		readonly ModelAnimation modelAnimation;
		readonly RenderVoxels rv;

		public CNWithVoxelBody(Actor self, CNWithVoxelBodyInfo info)
			: base(info)
		{
			var body = self.Trait<BodyOrientation>();
			rv = self.Trait<RenderVoxels>();
			var dynamics = self.TraitOrDefault<VoxelDynamics>();

			var model = rv.Renderer.ModelCache.GetModelSequence(rv.Image, info.Sequence);
			modelAnimation = new ModelAnimation(model,
				() => info.Offset + (dynamics != null ? dynamics.GetExtraOffset() : WVec.Zero),
				() =>
				{
					var baseOrientation = body.QuantizeOrientation(self.Orientation);
					if (dynamics == null)
						return baseOrientation;

					// Apply pitch/roll in body-local space via quaternion composition:
					// extraRot.Rotate(base) = base * extraRot → tilt along body's own axes
					var extra = dynamics.GetExtraRotation();
					return new WRot(extra.Roll, extra.Pitch, WAngle.Zero).Rotate(baseOrientation);
				},
				() => IsTraitDisabled, () => 0, info.ShowShadow);

			rv.Add(modelAnimation);
		}

		Rectangle IAutoMouseBounds.AutoMouseoverBounds(Actor self, WorldRenderer wr)
		{
			return modelAnimation.ScreenBounds(self.CenterPosition, wr, rv.Info.Scale);
		}
	}
}
