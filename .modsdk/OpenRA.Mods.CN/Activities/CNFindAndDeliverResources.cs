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
using OpenRA.Activities;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Mods.Common.Traits;
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
				// Holding position for a reserved dock is only right while that dock still exists.
				// DockHost clears its reservations when the refinery dies, but this branch returns
				// unconditionally on a non-null host, so anything that leaves a stale reference here
				// parks a loaded harvester forever — it never goes idle, so the bot's idle handling
				// never sees it either.
				var reservedHost = harv.DockClientManager.ReservedHost;
				var reservedActor = harv.DockClientManager.ReservedHostActor;
				if (reservedHost != null && reservedActor != null && !reservedActor.IsDead && reservedActor.IsInWorld)
					return false;

				if (reservedHost != null)
					CNBotLog.Debug($"CN AI: Harvester {self} held a reservation on a refinery that is gone — choosing another dock.");

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
			var fromCell = lastHarvestedCell ?? self.Location;
			var bestDockActor = cnHarvesterBotModule?.ChooseBestRefineryForDelivery(self, fromCell)
				?? ChooseLeastBusyRefinery(self, fromCell);

			if (bestDockActor != null)
				return new MoveToDock(self, bestDockActor, dockLineColor: dockClient.DockLineColor);

			return new MoveToDock(self, dockLineColor: dockClient.DockLineColor);
		}

		// Player-controlled harvesters have no CNHarvesterBotModule to consult, so without this
		// they always fall back to the engine's plain "closest dock" search - which ignores
		// occupancy and piles every harvester onto the same nearest refinery even when a free one
		// is only a few cells further away. Doesn't need the bot module's full resource-map-aware
		// scoring, just enough occupancy-awareness to stop the pile-up.
		Actor ChooseLeastBusyRefinery(Actor self, CPos fromCell)
		{
			Actor best = null;
			var bestScore = int.MinValue;

			foreach (var pair in self.World.ActorsWithTrait<IDockHost>())
			{
				var refinery = pair.Actor;
				if (refinery.Owner != self.Owner || refinery.IsDead || !refinery.IsInWorld)
					continue;

				if (!dockClient.CanDockAt(refinery, pair.Trait, false, true))
					continue;

				var score = -pair.Trait.ReservationCount * 200 - (fromCell - refinery.Location).LengthSquared;
				if (score > bestScore)
				{
					bestScore = score;
					best = refinery;
				}
			}

			return best;
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
