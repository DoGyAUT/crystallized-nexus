#region Copyright & License Information
/*
 * Crystallized Nexus - CNWithVoxelUnloadBody
 * Drop-in replacement for WithVoxelUnloadBody that applies VoxelDynamics tilt.
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
	[Desc("Renders a voxel body with idle/unload sequences and optional VoxelDynamics tilt. " +
		"Drop-in replacement for WithVoxelUnloadBody.")]
	public class CNWithVoxelUnloadBodyInfo : TraitInfo, IRenderActorPreviewVoxelsInfo, Requires<RenderVoxelsInfo>
	{
		[Desc("Voxel sequence name to use when docked to a refinery.")]
		public readonly string UnloadSequence = "unload";

		[Desc("Voxel sequence name to use when undocked from a refinery.")]
		public readonly string IdleSequence = "idle";

		[Desc("Defines if the Voxel should have a shadow.")]
		public readonly bool ShowShadow = true;

		public override object Create(ActorInitializer init) { return new CNWithVoxelUnloadBody(init.Self, this); }

		public IEnumerable<ModelAnimation> RenderPreviewVoxels(IModelCache cache,
			ActorPreviewInitializer init, RenderVoxelsInfo rv, string image, Func<WRot> orientation, int facings, PaletteReference p)
		{
			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var dynamics = init.GetOrDefault<VoxelDynamicsPreviewInit>();
			var model = cache.GetModelSequence(image, IdleSequence);
			yield return new ModelAnimation(model,
				() => dynamics != null ? dynamics.Offset.GetExtraOffset() : WVec.Zero,
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

	public class CNWithVoxelUnloadBody : IAutoMouseBounds, IDockClientBody
	{
		bool docked;

		readonly ModelAnimation modelAnimation;
		readonly RenderVoxels rv;

		public CNWithVoxelUnloadBody(Actor self, CNWithVoxelUnloadBodyInfo info)
		{
			var body = self.Trait<BodyOrientation>();
			rv = self.Trait<RenderVoxels>();
			var dynamics = self.TraitOrDefault<VoxelDynamics>();

			Func<WRot> orientationFunc;
			if (dynamics != null)
			{
				orientationFunc = () =>
				{
					var extra = dynamics.GetExtraRotation();
					var o = self.Orientation;
					var modOrientation = new WRot(o.Roll + extra.Roll, o.Pitch + extra.Pitch, o.Yaw);
					return body.QuantizeOrientation(modOrientation);
				};
			}
			else
			{
				orientationFunc = () => body.QuantizeOrientation(self.Orientation);
			}

			var idleModel = rv.Renderer.ModelCache.GetModelSequence(rv.Image, info.IdleSequence);
			modelAnimation = new ModelAnimation(idleModel, () => WVec.Zero,
				orientationFunc,
				() => docked,
				() => 0, info.ShowShadow);

			rv.Add(modelAnimation);

			var unloadModel = rv.Renderer.ModelCache.GetModelSequence(rv.Image, info.UnloadSequence);
			rv.Add(new ModelAnimation(unloadModel, () => WVec.Zero,
				orientationFunc,
				() => !docked,
				() => 0, info.ShowShadow));
		}

		void IDockClientBody.PlayDockAnimation(Actor self, Action after)
		{
			docked = true;
			after();
		}

		void IDockClientBody.PlayReverseDockAnimation(Actor self, Action after)
		{
			docked = false;
			after();
		}

		Rectangle IAutoMouseBounds.AutoMouseoverBounds(Actor self, WorldRenderer wr)
		{
			return modelAnimation.ScreenBounds(self.CenterPosition, wr, rv.Info.Scale);
		}
	}
}
