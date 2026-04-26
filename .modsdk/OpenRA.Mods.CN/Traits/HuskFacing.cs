#region Copyright & License Information
/*
 * Copyright 2019-2025 The Crystallized Nexus Developers (see CREDITS)
 * This file is part of Crystallized Nexus, which is free software.
 * It is made available to you under the terms of the GNU General Public
 * License as published by the Free Software Foundation, either version 3
 * of the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Mods.Common;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Stores the facing an actor had when it died, for use by immobile husk actors.",
		"Reads FacingInit so SpawnActorOnDeath can pass the correct facing through.")]
	public class HuskFacingInfo : TraitInfo, IFacingInfo
	{
		[Desc("Fallback facing if no FacingInit is provided.")]
		public readonly WAngle InitialFacing = WAngle.Zero;

		public WAngle GetInitialFacing() => InitialFacing;

		public override object Create(ActorInitializer init) { return new HuskFacing(init, this); }
	}

	public class HuskFacing : IFacing
	{
		readonly Actor self;

		public HuskFacing(ActorInitializer init, HuskFacingInfo info)
		{
			self = init.Self;
			Facing = init.GetValue<FacingInit, WAngle>(info.InitialFacing);
		}

		public WAngle Facing { get; set; }

		public WAngle TurnSpeed => WAngle.Zero;

		public WRot Orientation
		{
			get
			{
				// Mirror Mobile.Orientation: combine facing yaw with the terrain ramp tilt
				// so husks on ramps visually conform to the slope.
				var terrainOrientation = self.World.Map.TerrainOrientation(self.Location);
				return WRot.FromYaw(Facing).Rotate(terrainOrientation);
			}
		}
	}
}
