#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Activities;
using OpenRA.Mods.CN.Activities;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.CN.Traits.Movement
{
	[Desc("Makes vehicles perform arc maneuvers instead of rotating on the spot.",
		"When a required turn exceeds SteeringAngle, the unit drives ForwardCommitCells cells",
		"straight ahead, then sweeps SteeringCone arc cells toward the destination — all passed",
		"as a single Move so the engine's IsTurn arc-movement fires between consecutive cells.",
		"Requires TurnsWhileMoving: true on Mobile for fully gapless rotation.")]
	public class CNSteeredMobileInfo : ConditionalTraitInfo
	{
		[Desc("Cells to drive straight in the current heading before beginning the arc.")]
		public readonly int ForwardCommitCells = 1;

		[Desc("Turn angle threshold in WAngle units (0-1023, full circle = 1024).",
			"Turns up to this value are handled directly by the engine. 384 ≈ 135°.")]
		public readonly WAngle SteeringAngle = new(384);

		[Desc("Number of arc cells after the forward commit.",
			"The required turn is distributed evenly across these cells.",
			"More cells = wider, smoother arc. 3 cells suits bikes; 4-5 suits heavier vehicles.")]
		public readonly int SteeringCone = 3;

		public override object Create(ActorInitializer init) => new CNSteeredMobile(init.Self, this);
	}

	public class CNSteeredMobile : ConditionalTrait<CNSteeredMobileInfo>, IWrapMove
	{
		readonly Actor self;

		public CNSteeredMobile(Actor self, CNSteeredMobileInfo info)
			: base(info)
		{
			this.self = self;
		}

		public Activity WrapMove(Activity moveInner) =>
			new CNSteeredMoveActivity(self, moveInner, Info);
	}
}
