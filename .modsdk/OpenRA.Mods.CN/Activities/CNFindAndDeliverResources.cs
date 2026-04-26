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

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class CNFindAndDeliverResources : Activity
	{
		readonly Harvester harv;
		readonly HarvesterInfo harvInfo;
		readonly Mobile mobile;
		readonly ResourceClaimLayer claimLayer;
		readonly DockClientManager dockClient;
		readonly MoveCooldownHelper moveCooldownHelper;
		readonly CNHarvesterBotModule cnHarvesterBotModule;
		CPos? orderLocation;
		CPos? lastHarvestedCell;
		bool hasDeliveredLoad;
		bool hasHarvestedCell;
		bool hasWaited;

		public bool LastSearchFailed { get; private set; }

		public CNFindAndDeliverResources(Actor self, CPos? orderLocation = null)
		{
			harv = self.Trait<Harvester>();
			harvInfo = self.Info.TraitInfo<HarvesterInfo>();
			dockClient = self.Trait<DockClientManager>();
			mobile = self.Trait<Mobile>();
			claimLayer = self.World.WorldActor.Trait<ResourceClaimLayer>();
			moveCooldownHelper = new MoveCooldownHelper(self.World, mobile) { RetryIfDestinationBlocked = true };
			cnHarvesterBotModule = self.Owner.PlayerActor.TraitsImplementing<CNHarvesterBotModule>().FirstEnabledTraitOrDefault();

			if (orderLocation.HasValue)
				this.orderLocation = orderLocation.Value;
		}

		protected override void OnFirstRun(Actor self)
		{
			if (orderLocation != null)
			{
				lastHarvestedCell = orderLocation;
				if (harv.IsFull)
					QueueChild(CreateMoveToBestDock(self));
			}
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || harv.IsTraitDisabled)
				return true;

			if (NextActivity != null)
			{
				if (!harvInfo.QueueFullLoad && (hasHarvestedCell || LastSearchFailed))
					return true;

				if (hasDeliveredLoad || harv.IsFull)
					return true;
			}

			if (harv.IsFull || (!harv.IsEmpty && LastSearchFailed))
			{
				if (harv.DockClientManager.ReservedHost != null)
					return false;

				QueueChild(CreateMoveToBestDock(self));
				hasDeliveredLoad = true;
			}

			if (LastSearchFailed && !hasWaited)
			{
				QueueChild(new Wait(harv.Info.WaitDuration));
				hasWaited = true;
				return false;
			}

			hasWaited = false;

			var closestHarvestableCell = ClosestHarvestablePos(self);
			if (!closestHarvestableCell.HasValue)
			{
				if (lastHarvestedCell != null)
				{
					lastHarvestedCell = null;
					closestHarvestableCell = ClosestHarvestablePos(self);
					LastSearchFailed = !closestHarvestableCell.HasValue;
				}
				else
					LastSearchFailed = true;
			}
			else
				LastSearchFailed = false;

			var result = moveCooldownHelper.Tick(false);
			if (result != null)
				return result.Value;

			if (LastSearchFailed)
			{
				var lastproc = harv.DockClientManager?.LastReservedHost;
				if (lastproc != null)
				{
					var deliveryLoc = self.World.Map.CellContaining(lastproc.DockPosition);
					if (self.Location == deliveryLoc && harv.IsEmpty)
					{
						var unblockCell = deliveryLoc + harv.Info.UnblockCell;
						var moveTo = mobile.NearestMoveableCell(unblockCell, 1, 5);
						moveCooldownHelper.NotifyMoveQueued();
						QueueChild(mobile.MoveTo(moveTo, 1));
					}
				}

				return false;
			}

			moveCooldownHelper.NotifyMoveQueued();
			QueueChild(new HarvestResource(self, closestHarvestableCell.Value));
			lastHarvestedCell = closestHarvestableCell.Value;
			hasHarvestedCell = true;
			return false;
		}

		Activity CreateMoveToBestDock(Actor self)
		{
			var bestDockActor = cnHarvesterBotModule?.ChooseBestRefineryForDelivery(self, lastHarvestedCell ?? self.Location);
			if (bestDockActor != null)
				return new MoveToDock(self, bestDockActor, dockLineColor: dockClient.DockLineColor);

			return new MoveToDock(self, dockLineColor: dockClient.DockLineColor);
		}

		CPos? ClosestHarvestablePos(Actor self)
		{
			if (orderLocation == null)
			{
				if (harv.CanHarvestCell(self.Location) && claimLayer.CanClaimCell(self, self.Location))
					return self.Location;
			}
			else
			{
				if (harv.CanHarvestCell(orderLocation.Value) && claimLayer.CanClaimCell(self, orderLocation.Value))
					return orderLocation;

				orderLocation = null;
			}

			CPos searchFromLoc;
			int searchRadius;
			var dockPos = harv.DockClientManager?.LastReservedHost?.DockPosition;

			if (lastHarvestedCell.HasValue)
			{
				searchRadius = harvInfo.SearchFromHarvesterRadius;
				searchFromLoc = lastHarvestedCell.Value;
			}
			else
			{
				searchRadius = harvInfo.SearchFromProcRadius;
				if (dockPos != null)
					searchFromLoc = self.World.Map.CellContaining(dockPos.Value);
				else
					searchFromLoc = self.Location;
			}

			var searchRadiusSquared = searchRadius * searchRadius;
			var map = self.World.Map;
			var harvPos = self.CenterPosition;

			var path = mobile.PathFinder.FindPathToTargetCellByPredicate(
				self,
				[searchFromLoc, self.Location],
				loc =>
					harv.CanHarvestCell(loc) &&
					claimLayer.CanClaimCell(self, loc),
				BlockedByActor.Stationary,
				loc =>
				{
					if ((loc - searchFromLoc).LengthSquared > searchRadiusSquared)
						return PathGraph.PathCostForInvalidPath;

					if (dockPos.HasValue && harvInfo.ResourceRefineryDirectionPenalty > 0 && harv.CanHarvestCell(loc))
					{
						var pos = map.CenterOfCell(loc);
						var b = pos - dockPos.Value;

						if (b != WVec.Zero)
						{
							var c = pos - harvPos;
							if (c != WVec.Zero)
							{
								var a = harvPos - dockPos.Value;
								var cosA = (int)(512 * (b.LengthSquared + c.LengthSquared - a.LengthSquared) / b.Length / c.Length);
								return Math.Abs(harvInfo.ResourceRefineryDirectionPenalty / 2) + harvInfo.ResourceRefineryDirectionPenalty * cosA / 2048;
							}
						}
					}

					return 0;
				});

			if (path.Count > 0)
				return path[0];

			return null;
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			yield return Target.FromCell(self.World, self.Location);
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (ChildActivity != null)
				foreach (var n in ChildActivity.TargetLineNodes(self))
					yield return n;

			if (orderLocation != null)
				yield return new TargetLineNode(Target.FromCell(self.World, orderLocation.Value), harvInfo.HarvestLineColor);
			else
			{
				var manager = harv.DockClientManager;
				if (manager?.ReservedHostActor != null)
					yield return new TargetLineNode(Target.FromActor(manager.ReservedHostActor), manager.DockLineColor);
			}
		}
	}
}
