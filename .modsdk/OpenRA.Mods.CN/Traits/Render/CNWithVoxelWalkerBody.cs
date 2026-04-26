#region Copyright & License Information
/*
 * Crystallized Nexus - CNWithVoxelWalkerBody
 * Drop-in replacement for WithVoxelWalkerBody with optional VoxelDynamics tilt and Offset.
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
	[Desc("Renders a voxel walker body with optional VoxelDynamics tilt and visual offset. " +
		"Drop-in replacement for WithVoxelWalkerBody.")]
	public class CNWithVoxelWalkerBodyInfo : PausableConditionalTraitInfo,
		IRenderActorPreviewVoxelsInfo, Requires<RenderVoxelsInfo>, Requires<IMoveInfo>, Requires<IFacingInfo>
	{
		public readonly string Sequence = "idle";

		[Desc("The speed of the walker's legs.")]
		public readonly int TickRate = 5;

		[Desc("Defines if the Voxel should have a shadow.")]
		public readonly bool ShowShadow = true;

		[Desc("Visual offset in WDist applied to the voxel. " +
			"Use to center large-footprint units over their full occupied area. " +
			"Example for 2x2 footprint: 512, 512, 0")]
		public readonly WVec Offset = WVec.Zero;

		public override object Create(ActorInitializer init) { return new CNWithVoxelWalkerBody(init.Self, this); }

		public IEnumerable<ModelAnimation> RenderPreviewVoxels(IModelCache cache,
			ActorPreviewInitializer init, RenderVoxelsInfo rv, string image, Func<WRot> orientation, int facings, PaletteReference p)
		{
			var model = cache.GetModelSequence(image, Sequence);
			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var frame = init.GetValue<BodyAnimationFrameInit, uint>(this, 0);

			yield return new ModelAnimation(model, () => Offset,
				() => body.QuantizeOrientation(orientation(), facings),
				() => false, () => frame, ShowShadow);
		}
	}

	public class CNWithVoxelWalkerBody : PausableConditionalTrait<CNWithVoxelWalkerBodyInfo>, ITick, IActorPreviewInitModifier, IAutoMouseBounds
	{
		readonly IMove movement;
		readonly ModelAnimation modelAnimation;
		readonly RenderVoxels rv;

		uint tick, frame;
		readonly uint frames;

		public CNWithVoxelWalkerBody(Actor self, CNWithVoxelWalkerBodyInfo info)
			: base(info)
		{
			movement = self.Trait<IMove>();

			var body = self.Trait<BodyOrientation>();
			rv = self.Trait<RenderVoxels>();
			var dynamics = self.TraitOrDefault<VoxelDynamics>();

			var model = rv.Renderer.ModelCache.GetModelSequence(rv.Image, info.Sequence);
			frames = model.Frames;

			modelAnimation = new ModelAnimation(model,
				() => info.Offset,
				() =>
				{
					if (dynamics == null)
						return body.QuantizeOrientation(self.Orientation);

					var extra = dynamics.GetExtraRotation();
					var o = self.Orientation;
					return body.QuantizeOrientation(new WRot(o.Roll + extra.Roll, o.Pitch + extra.Pitch, o.Yaw));
				},
				() => IsTraitDisabled, () => frame, info.ShowShadow);

			rv.Add(modelAnimation);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || IsTraitPaused)
				return;

			if (movement.CurrentMovementTypes.HasMovementType(MovementType.Horizontal)
				|| movement.CurrentMovementTypes.HasMovementType(MovementType.Turn))
				tick++;

			if (tick < Info.TickRate)
				return;

			tick = 0;
			if (++frame == frames)
				frame = 0;
		}

		void IActorPreviewInitModifier.ModifyActorPreviewInit(Actor self, TypeDictionary inits)
		{
			inits.Add(new BodyAnimationFrameInit(frame));
		}

		Rectangle IAutoMouseBounds.AutoMouseoverBounds(Actor self, WorldRenderer wr)
		{
			return modelAnimation.ScreenBounds(self.CenterPosition, wr, rv.Info.Scale);
		}
	}
}
