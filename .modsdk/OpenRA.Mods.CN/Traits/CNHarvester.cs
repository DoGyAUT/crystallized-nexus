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

using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class CNHarvesterInfo : HarvesterInfo
	{
		public override object Create(ActorInitializer init) { return new CNHarvester(init.Self, this); }
	}

	public class CNHarvester : Harvester, IResolveOrder
	{
		readonly ResourceClaimLayer claimLayer;
		readonly Mobile mobile;

		public CNHarvester(Actor self, HarvesterInfo info)
			: base(self, info)
		{
			claimLayer = self.World.WorldActor.Trait<ResourceClaimLayer>();
			mobile = self.TraitOrDefault<Mobile>();
		}

		protected override void Created(Actor self)
		{
			base.Created(self);

			if (Info.SearchOnCreation && mobile != null)
				self.QueueActivity(true, new CNFindAndDeliverResources(self));
		}

		public override void OnDockCompleted(Actor self, Actor hostActor, IDockHost dock)
		{
			base.OnDockCompleted(self, hostActor, dock);

			if (GetDockType.Overlaps(dock.GetDockType))
			{
				var currentActivity = self.CurrentActivity;
				if (currentActivity == null || (currentActivity is not CNFindAndDeliverResources && currentActivity.NextActivity == null))
					self.QueueActivity(true, new CNFindAndDeliverResources(self));
			}
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Harvest" && mobile != null)
			{
				CPos loc;
				if (order.Target.Type != TargetType.Invalid)
				{
					var cell = self.World.Map.CellContaining(order.Target.CenterPosition);
					loc = mobile.NearestCell(cell, p => mobile.CanEnterCell(p) && claimLayer.TryClaimCell(self, p), 1, 6);
				}
				else
					loc = self.Location;

				self.QueueActivity(order.Queued, new CNFindAndDeliverResources(self, loc));
				self.ShowTargetLines();
			}
		}
	}
}
