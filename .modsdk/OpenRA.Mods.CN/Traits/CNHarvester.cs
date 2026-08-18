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
				// No logging here. This ran on every completed dock cycle of every harvester, through
				// Log.Write directly rather than CNBotLog.Debug, so unlike every other bot diagnostic it
				// was not gated on the BotDebug setting and wrote for players who never asked for it. One
				// match produced 799 lines from this one call site, out of nearly 8000 dock-debug lines
				// that between them drowned the log the bot work is actually read from.
				var currentActivity = self.CurrentActivity;
				var willRequeue = currentActivity == null || (currentActivity is not CNFindAndDeliverResources && currentActivity.NextActivity == null);

				if (willRequeue)
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
