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
using OpenRA.Mods.CN.Traits.BotModules.Squads;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc(
		"CN squad-aware unit builder. Consumes live squad demand from CNSquadManagerBotModule, " +
		"reinforces damaged squads first, then builds toward missing template instances. " +
		"Falls back to FallbackUnitsToBuild ratios when all squad demand is satisfied.")]
	public class CNUnitBuilderBotModuleInfo : ConditionalTraitInfo
	{
		[Desc(
			"If > 0, only produce units when fewer than this many are idle at base. " +
			"Must be >= the largest MinSlotsToActivate across all templates to avoid deadlock.")]
		public readonly int IdleBaseUnitsMaximum = -1;

		[Desc("Production queue categories the bot uses (must match queue Type fields in rules).")]
		public readonly string[] UnitQueues = ["Vehicle", "Infantry", "Plane", "Ship", "Aircraft"];

		[Desc(
			"Hard cap per unit type. The bot never builds more than this many of a type regardless of demand. " +
			"Format: actor-name: max-count")]
		public readonly Dictionary<string, int> UnitLimits = null;

		[Desc(
			"Earliest world tick at which the bot may start building a unit type. " +
			"Format: actor-name: first-allowed-tick")]
		public readonly Dictionary<string, int> UnitDelays = null;

		[Desc(
			"Fallback build ratios used when all squad template demands are already met. " +
			"Same format as vanilla UnitBuilderBotModule UnitsToBuild: actor-name: share. " +
			"Leave empty to idle when all squad slots are satisfied.")]
		public readonly Dictionary<string, int> FallbackUnitsToBuild = null;

		[Desc(
			"Additional unit counts to always want on top of squad demand. " +
			"Useful for defensive padding or unit types not in any template. " +
			"Format: actor-name: extra-count")]
		public readonly Dictionary<string, int> ExtraUnitsToBuild = null;

		[Desc("Minimum credits before the bot queues any new non-emergency unit production.")]
		public readonly int ProductionMinCashRequirement = 500;

		[Desc("Credits to keep in reserve before queueing regular unit production.")]
		public readonly int DesiredCashReserve = 1200;

		[Desc("Additional reserve per available production queue to avoid spending to zero across many factories.")]
		public readonly int AdditionalCashReservePerQueue = 200;

		public readonly int EcoProductionMinCashRequirement = -1;
		public readonly int RushProductionMinCashRequirement = -1;
		public readonly int TurtleProductionMinCashRequirement = -1;
		public readonly int TechProductionMinCashRequirement = -1;
		public readonly int ExpansionProductionMinCashRequirement = -1;
		public readonly int SteamrollerProductionMinCashRequirement = -1;

		public readonly int EcoDesiredCashReserve = -1;
		public readonly int RushDesiredCashReserve = -1;
		public readonly int TurtleDesiredCashReserve = -1;
		public readonly int TechDesiredCashReserve = -1;
		public readonly int ExpansionDesiredCashReserve = -1;
		public readonly int SteamrollerDesiredCashReserve = -1;

		public readonly int EcoAdditionalCashReservePerQueue = -1;
		public readonly int RushAdditionalCashReservePerQueue = -1;
		public readonly int TurtleAdditionalCashReservePerQueue = -1;
		public readonly int TechAdditionalCashReservePerQueue = -1;
		public readonly int ExpansionAdditionalCashReservePerQueue = -1;
		public readonly int SteamrollerAdditionalCashReservePerQueue = -1;

		[Desc("Only produce fallback units when at least one matching capturable target exists.")]
		public readonly bool RequireCapturableTargets = false;

		[Desc("Actor types that satisfy RequireCapturableTargets. Empty means any capturable target.")]
		public readonly HashSet<string> CapturableActorTypes = [];

		[Desc("Should visibility be considered when searching for capturable targets?")]
		public readonly bool CheckCaptureTargetsForVisibility = true;

		public override object Create(ActorInitializer init) => new CNUnitBuilderBotModule(init.Self, this);
	}

	public class CNUnitBuilderBotModule : ConditionalTrait<CNUnitBuilderBotModuleInfo>,
		IBotTick, IBotNotifyIdleBaseUnits, IBotRequestUnitProduction, INotifyActorDisposing
	{
		sealed class QueueReservation
		{
			public string TemplateName;
			public int LastProgressTick;
		}

		const int FeedbackTime = 30;
		const int MaxQueuedItemsPerQueue = 1;
		const int ReservationStallTicks = 5 * FeedbackTime;
		const int TemplateRecentBuildWindow = 6 * FeedbackTime;
		const int TemplateRecentBuildPenaltyStep = 175;
		const int TemplateReservationPenalty = 900;
		const int TemplateActiveSquadPenalty = 250;

		readonly World world;
		readonly Player player;
		readonly List<string> externalBuildRequests = [];
		readonly Dictionary<uint, QueueReservation> queueReservations = [];
		readonly Dictionary<string, int> templateRecentBuildTicks = new(StringComparer.OrdinalIgnoreCase);

		CNSquadManagerBotModule squadManager;
		CNBotProfileBotModule profileModule;
		PlayerResources playerResources;
		IBotRequestPauseUnitProduction[] requestPause;
		int idleUnitCount;
		int ticks;
		int currentQueueIndex;

		public CNUnitBuilderBotModule(Actor self, CNUnitBuilderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			ticks = world.LocalRandom.Next(FeedbackTime);
		}

		protected override void Created(Actor self)
		{
			squadManager = self.Owner.PlayerActor.TraitsImplementing<CNSquadManagerBotModule>()
				.FirstOrDefault();
			profileModule = self.Owner.PlayerActor.TraitsImplementing<CNBotProfileBotModule>()
				.FirstOrDefault();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			requestPause = self.Owner.PlayerActor
				.TraitsImplementing<IBotRequestPauseUnitProduction>()
				.ToArray();
		}

		void IBotNotifyIdleBaseUnits.UpdatedIdleBaseUnits(List<Actor> idleUnits)
		{
			idleUnitCount = idleUnits.Count;
		}

		void IBotRequestUnitProduction.RequestUnitProduction(IBot bot, string requestedActor)
		{
			externalBuildRequests.Add(requestedActor);
		}

		int IBotRequestUnitProduction.RequestedProductionCount(IBot bot, string requestedActor)
		{
			return externalBuildRequests.Count(r => r == requestedActor);
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (requestPause.Any(rp => rp.PauseUnitProduction))
				return;

			ticks++;
			if (ticks % FeedbackTime != 0)
				return;

			var queuesByCategory = AIUtils.FindQueuesByCategory(player);
			var existingByType = BuildExistingCounts(queuesByCategory);

			var request = externalBuildRequests.FirstOrDefault();
			if (request != null)
			{
				BuildSpecific(bot, request, queuesByCategory);
				externalBuildRequests.Remove(request);
				return;
			}

			if (Info.RequireCapturableTargets && !HasCapturableTarget())
				return;

			if (playerResources.GetCashAndResources() < GetActiveProductionMinCashRequirement())
				return;

			if (Info.IdleBaseUnitsMaximum > 0 && idleUnitCount >= Info.IdleBaseUnitsMaximum)
				return;

			var demand = CalculateDemand(existingByType);
			if (demand.Count > 0 && BuildDemand(bot, demand, existingByType, queuesByCategory))
				return;

			if (Info.FallbackUnitsToBuild != null && Info.FallbackUnitsToBuild.Count > 0)
				BuildFallback(bot, queuesByCategory, existingByType);
		}

		Dictionary<string, int> CalculateDemand(Dictionary<string, int> existingByType)
		{
			var demand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			if (squadManager != null && !squadManager.IsTraitDisabled)
			{
				foreach (var (typeName, count) in squadManager.GetCurrentDemand(existingByType))
					demand[typeName] = demand.GetValueOrDefault(typeName) + count;
			}

			if (Info.ExtraUnitsToBuild != null)
			{
				foreach (var (typeName, wanted) in Info.ExtraUnitsToBuild)
				{
					var existing = existingByType.GetValueOrDefault(typeName);
					var deficit = Math.Max(0, wanted - existing);
					if (deficit > 0)
						demand[typeName] = demand.GetValueOrDefault(typeName) + deficit;
				}
			}

			return demand;
		}

		bool BuildDemand(
			IBot bot,
			Dictionary<string, int> demand,
			Dictionary<string, int> existingByType,
			ILookup<string, ProductionQueue> queuesByCategory)
		{
			var builtAny = false;
			var committedCost = 0;

			for (var i = 0; i < Info.UnitQueues.Length; i++)
			{
				if (++currentQueueIndex >= Info.UnitQueues.Length)
					currentQueueIndex = 0;

				var queueCategory = Info.UnitQueues[currentQueueIndex];
				var queueSlots = GetAvailableQueues(queuesByCategory, queueCategory);
				if (queueSlots.Count == 0)
					continue;

				foreach (var queue in queueSlots)
				{
					if (string.Equals(queueCategory, "Vehicle", StringComparison.OrdinalIgnoreCase) &&
						TryBuildReservedVehicle(bot, queue, existingByType, demand, queuesByCategory, ref committedCost))
					{
						builtAny = true;
						continue;
					}

					var best = FindBestDemandUnit(demand, existingByType, queuesByCategory, queueCategory, committedCost);
					if (best == null)
						break;

					if (!HasBudgetFor(best, committedCost, queuesByCategory))
						continue;

					if (!QueueSpecific(bot, queue, best.Name))
						continue;

					existingByType[best.Name] = existingByType.GetValueOrDefault(best.Name) + 1;
					committedCost += best.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;

					var missingCount = demand.GetValueOrDefault(best.Name) - 1;
					if (missingCount > 0)
						demand[best.Name] = missingCount;
					else
						demand.Remove(best.Name);

					builtAny = true;
				}
			}

			return builtAny;
		}

		ActorInfo FindBestDemandUnit(
			Dictionary<string, int> demand,
			Dictionary<string, int> existingByType,
			ILookup<string, ProductionQueue> queuesByCategory,
			string preferredQueueCategory,
			int committedCost)
		{
			ActorInfo best = null;
			float bestScore = float.MinValue;

			foreach (var (typeName, missingCount) in demand)
			{
				if (missingCount <= 0)
					continue;

				if (Info.UnitDelays != null &&
					Info.UnitDelays.TryGetValue(typeName, out var delay) &&
					world.WorldTick < delay)
					continue;

				if (Info.UnitLimits != null &&
					Info.UnitLimits.TryGetValue(typeName, out var limit) &&
					existingByType.GetValueOrDefault(typeName) >= limit)
					continue;

				if (ReachedTemplateCap(typeName, existingByType))
					continue;

				if (!IsBuildable(typeName, queuesByCategory, preferredQueueCategory))
					continue;

				var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(typeName);
				if (actorInfo == null)
					continue;

				if (!HasBudgetFor(actorInfo, committedCost, queuesByCategory))
					continue;

				var existing = existingByType.GetValueOrDefault(typeName);
				var score = (float)missingCount / (existing + 1);

				if (existing == 0)
					score += 25f;

				if (score > bestScore)
				{
					bestScore = score;
					best = actorInfo;
				}
			}

			return best;
		}

		void BuildFallback(
			IBot bot,
			ILookup<string, ProductionQueue> queuesByCategory,
			Dictionary<string, int> existingByType)
		{
			var builtAny = false;
			var committedCost = 0;

			for (var i = 0; i < Info.UnitQueues.Length; i++)
			{
				if (++currentQueueIndex >= Info.UnitQueues.Length)
					currentQueueIndex = 0;

				var category = Info.UnitQueues[currentQueueIndex];
				var queues = GetAvailableQueues(queuesByCategory, category);
				if (queues.Count == 0)
					continue;

				foreach (var queue in queues)
				{
					var unit = ChooseFallbackUnit(queue, existingByType);
					if (unit == null || !HasAdequateAirRearmBuildings(unit, existingByType))
						continue;

					if (!HasBudgetFor(unit, committedCost, queuesByCategory))
						continue;

					if (!QueueSpecific(bot, queue, unit.Name))
						continue;

					existingByType[unit.Name] = existingByType.GetValueOrDefault(unit.Name) + 1;
					committedCost += unit.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
					builtAny = true;
				}
			}

			if (builtAny)
				return;
		}

		ActorInfo ChooseFallbackUnit(ProductionQueue queue, Dictionary<string, int> existingByType)
		{
			if (Info.FallbackUnitsToBuild == null || Info.FallbackUnitsToBuild.Count == 0)
				return null;

			var buildable = queue.BuildableItems().ToArray();
			if (buildable.Length == 0)
				return null;

			var totalOwned = Info.FallbackUnitsToBuild.Keys.Sum(t => existingByType.GetValueOrDefault(t));
			ActorInfo best = null;
			float bestError = float.MaxValue;

			foreach (var unit in buildable)
			{
				if (!Info.FallbackUnitsToBuild.TryGetValue(unit.Name, out var share))
					continue;
				if (Info.UnitDelays != null &&
					Info.UnitDelays.TryGetValue(unit.Name, out var delay) &&
					world.WorldTick < delay)
					continue;
				if (Info.UnitLimits != null &&
					Info.UnitLimits.TryGetValue(unit.Name, out var limit) &&
					existingByType.GetValueOrDefault(unit.Name) >= limit)
					continue;

				if (ReachedTemplateCap(unit.Name, existingByType))
					continue;

				var owned = existingByType.GetValueOrDefault(unit.Name);
				var ratio = totalOwned > 0 ? (float)owned / totalOwned : 0f;
				var target = share / 100f;
				var error = ratio - target;

				if (error < bestError)
				{
					bestError = error;
					best = unit;
				}
			}

			return best;
		}

		Dictionary<string, int> BuildExistingCounts(ILookup<string, ProductionQueue> queuesByCategory)
		{
			var existing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			foreach (var actor in world.ActorsHavingTrait<Mobile>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;
				if (actor.Info.HasTraitInfo<MobSpawnerSlaveInfo>())
					continue;
				existing[actor.Info.Name] = existing.GetValueOrDefault(actor.Info.Name) + 1;
			}

			foreach (var actor in world.ActorsHavingTrait<Aircraft>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;
				if (actor.Info.HasTraitInfo<MobSpawnerSlaveInfo>())
					continue;
				existing[actor.Info.Name] = existing.GetValueOrDefault(actor.Info.Name) + 1;
			}

			foreach (var queue in queuesByCategory.SelectMany(g => g))
				foreach (var item in queue.AllQueued())
					existing[item.Item] = existing.GetValueOrDefault(item.Item) + 1;

			return existing;
		}

		bool IsBuildable(string typeName, ILookup<string, ProductionQueue> queuesByCategory, string preferredQueueCategory = null)
		{
			var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(typeName);
			if (actorInfo == null)
				return false;

			var buildable = actorInfo.TraitInfoOrDefault<BuildableInfo>();
			if (buildable == null)
				return false;

			return buildable.Queue.Any(queueType =>
				(preferredQueueCategory == null || queueType == preferredQueueCategory) &&
				queuesByCategory[queueType].Any(q =>
					q.BuildableItems().Any(a => a.Name == typeName)));
		}

		void BuildSpecific(IBot bot, string typeName, ILookup<string, ProductionQueue> queuesByCategory)
		{
			var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(typeName);
			if (actorInfo == null)
				return;

			var buildable = actorInfo.TraitInfoOrDefault<BuildableInfo>();
			if (buildable == null)
				return;

			if (!HasAdequateAirRearmBuildings(actorInfo, null))
				return;

			var existingByType = BuildExistingCounts(queuesByCategory);
			if (ReachedTemplateCap(typeName, existingByType))
				return;

			foreach (var queueType in buildable.Queue)
			{
				var queue = GetAvailableQueues(queuesByCategory, queueType).FirstOrDefault(q =>
					q.BuildableItems().Any(a => a.Name == typeName));
				if (queue != null)
				{
					QueueSpecific(bot, queue, typeName);
					return;
				}
			}
		}

		List<ProductionQueue> GetAvailableQueues(ILookup<string, ProductionQueue> queuesByCategory, string queueType)
		{
			var result = new List<ProductionQueue>();
			foreach (var queue in queuesByCategory[queueType])
			{
				if (queue.AllQueued().Count() > MaxQueuedItemsPerQueue)
					continue;

				result.Add(queue);
			}

			return result;
		}

		bool TryBuildReservedVehicle(
			IBot bot,
			ProductionQueue queue,
			Dictionary<string, int> existingByType,
			Dictionary<string, int> demand,
			ILookup<string, ProductionQueue> queuesByCategory,
			ref int committedCost)
		{
			var reservation = GetOrCreateReservation(queue);
			var next = GetReservedVehicleBuild(queue, reservation.TemplateName, existingByType);
			if (next == null)
			{
				reservation.TemplateName = FindBestVehicleTemplate(queue, existingByType);
				next = GetReservedVehicleBuild(queue, reservation.TemplateName, existingByType);
				if (next == null)
				{
					ReleaseReservation(queue, reservation);
					return false;
				}
			}

			if (!HasReservationBudget(next, committedCost, queuesByCategory))
			{
				if (world.WorldTick - reservation.LastProgressTick >= ReservationStallTicks)
					ReleaseReservation(queue, reservation);

				return false;
			}

			if (!QueueSpecific(bot, queue, next.Name))
				return false;

			reservation.LastProgressTick = world.WorldTick;
			if (!string.IsNullOrEmpty(reservation.TemplateName))
				templateRecentBuildTicks[reservation.TemplateName] = world.WorldTick;
			existingByType[next.Name] = existingByType.GetValueOrDefault(next.Name) + 1;
			committedCost += next.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;

			var missingCount = demand.GetValueOrDefault(next.Name) - 1;
			if (missingCount > 0)
				demand[next.Name] = missingCount;
			else
				demand.Remove(next.Name);

			if (GetReservedVehicleBuild(queue, reservation.TemplateName, existingByType) == null)
				ReleaseReservation(queue, reservation);

			return true;
		}

		QueueReservation GetOrCreateReservation(ProductionQueue queue)
		{
			if (!queueReservations.TryGetValue(queue.Actor.ActorID, out var reservation))
			{
				reservation = new QueueReservation { LastProgressTick = world.WorldTick };
				queueReservations.Add(queue.Actor.ActorID, reservation);
			}

			return reservation;
		}

		void ReleaseReservation(ProductionQueue queue, QueueReservation reservation)
		{
			reservation.TemplateName = null;
			reservation.LastProgressTick = world.WorldTick;
			queueReservations.Remove(queue.Actor.ActorID);
		}

		string FindBestVehicleTemplate(ProductionQueue queue, Dictionary<string, int> existingByType)
		{
			if (squadManager == null || squadManager.IsTraitDisabled)
				return null;

			string bestTemplate = null;
			var bestScore = int.MinValue;

			foreach (var kv in squadManager.Info.Teams.OrderByDescending(t => squadManager.GetEffectivePriority(t.Value)))
			{
				var templateName = kv.Key;
				var template = kv.Value;
				if (!TemplateAppliesToFaction(template))
					continue;

				var next = GetReservedVehicleBuild(queue, templateName, existingByType);
				if (next == null)
					continue;

				var score = squadManager.GetEffectivePriority(template) * 100;
				if (HasExistingTemplateVehicleDeficit(templateName, queue, existingByType))
					score += 10000;

				var currentCount = CountReservedTemplateSquads(templateName);
				score += Math.Max(0, (template.MaxInstances - currentCount) * 10);
				score -= currentCount * TemplateActiveSquadPenalty;
				score -= CountReservedTemplateQueues(templateName, queue.Actor.ActorID) * TemplateReservationPenalty;
				score -= GetRecentTemplatePenalty(templateName);

				if (score > bestScore)
				{
					bestScore = score;
					bestTemplate = templateName;
				}
			}

			return bestTemplate;
		}

		int CountReservedTemplateQueues(string templateName, uint excludingQueueActorId)
		{
			var count = 0;
			foreach (var kv in queueReservations)
			{
				if (kv.Key == excludingQueueActorId)
					continue;

				if (!string.Equals(kv.Value.TemplateName, templateName, StringComparison.OrdinalIgnoreCase))
					continue;

				count++;
			}

			return count;
		}

		int GetRecentTemplatePenalty(string templateName)
		{
			if (!templateRecentBuildTicks.TryGetValue(templateName, out var lastBuildTick))
				return 0;

			var ticksSinceLastBuild = world.WorldTick - lastBuildTick;
			if (ticksSinceLastBuild >= TemplateRecentBuildWindow)
				return 0;

			var remainingTicks = TemplateRecentBuildWindow - ticksSinceLastBuild;
			var penaltySteps = Math.Max(1, (remainingTicks + FeedbackTime - 1) / FeedbackTime);
			return penaltySteps * TemplateRecentBuildPenaltyStep;
		}

		bool HasExistingTemplateVehicleDeficit(string templateName, ProductionQueue queue, Dictionary<string, int> existingByType)
		{
			foreach (var squad in squadManager.Squads)
			{
				if (!squad.IsValid || !string.Equals(squad.TemplateName, templateName, StringComparison.OrdinalIgnoreCase))
					continue;

				foreach (var assignment in OrderedAssignments(squad.SlotAssignments))
				{
					if (assignment.MissingCount <= 0 || assignment.SlotInfo.IsPassenger)
						continue;

					if (SelectBuildableType(queue, assignment.SlotInfo, existingByType) != null)
						return true;
				}
			}

			return false;
		}

		ActorInfo GetReservedVehicleBuild(ProductionQueue queue, string templateName, Dictionary<string, int> existingByType)
		{
			if (string.IsNullOrEmpty(templateName) || squadManager == null || squadManager.IsTraitDisabled)
				return null;

			if (!squadManager.Info.Teams.TryGetValue(templateName, out var template) || !TemplateAppliesToFaction(template))
				return null;

			foreach (var squad in squadManager.Squads
				.Where(s => s.IsValid && string.Equals(s.TemplateName, templateName, StringComparison.OrdinalIgnoreCase))
				.OrderBy(s => s.IsOperational ? 1 : 0)
				.ThenByDescending(s => s.TemplateInfo != null ? squadManager.GetEffectivePriority(s.TemplateInfo) : 0))
			{
				foreach (var assignment in OrderedAssignments(squad.SlotAssignments))
				{
					if (assignment.MissingCount <= 0 || assignment.SlotInfo.IsPassenger)
						continue;

					var reservedType = SelectBuildableType(queue, assignment.SlotInfo, existingByType);
					if (reservedType != null)
						return reservedType;
				}
			}

			if (CountReservedTemplateSquads(templateName) >= template.MaxInstances)
				return null;

			foreach (var slot in OrderedSlots(template.Slots.Values))
			{
				if (slot.IsPassenger)
					continue;

				var reservedType = SelectBuildableType(queue, slot, existingByType);
				if (reservedType != null)
					return reservedType;
			}

			return null;
		}

		IEnumerable<CNSlotAssignment> OrderedAssignments(IEnumerable<CNSlotAssignment> assignments)
		{
			foreach (var assignment in assignments)
				if (assignment.SlotInfo.IsCarrier || assignment.SlotInfo.IsAircraftCarrier)
					yield return assignment;

			foreach (var assignment in assignments)
				if (!assignment.SlotInfo.Optional && !assignment.SlotInfo.IsCarrier && !assignment.SlotInfo.IsAircraftCarrier && !assignment.SlotInfo.IsPassenger)
					yield return assignment;

			foreach (var assignment in assignments)
				if (assignment.SlotInfo.Optional && !assignment.SlotInfo.IsPassenger)
					yield return assignment;
		}

		IEnumerable<CNSlotInfo> OrderedSlots(IEnumerable<CNSlotInfo> slots)
		{
			foreach (var slot in slots)
				if (slot.IsCarrier || slot.IsAircraftCarrier)
					yield return slot;

			foreach (var slot in slots)
				if (!slot.Optional && !slot.IsCarrier && !slot.IsAircraftCarrier && !slot.IsPassenger)
					yield return slot;

			foreach (var slot in slots)
				if (slot.Optional && !slot.IsPassenger)
					yield return slot;
		}

		ActorInfo SelectBuildableType(ProductionQueue queue, CNSlotInfo slotInfo, Dictionary<string, int> existingByType)
		{
			ActorInfo best = null;
			var bestExisting = int.MaxValue;

			foreach (var typeName in slotInfo.AllowedTypes)
			{
				if (ReachedTemplateCap(typeName, existingByType))
					continue;

				var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(typeName);
				if (actorInfo == null || !queue.BuildableItems().Any(a => a.Name == typeName))
					continue;

				var existing = existingByType.GetValueOrDefault(typeName);
				if (existing >= bestExisting)
					continue;

				best = actorInfo;
				bestExisting = existing;
			}

			return best;
		}

		int GetDesiredReserve(ILookup<string, ProductionQueue> queuesByCategory)
		{
			var queueCount = queuesByCategory.SelectMany(g => g).Count();
			return GetActiveDesiredCashReserve() + queueCount * GetActiveAdditionalCashReservePerQueue();
		}

		bool HasBudgetFor(ActorInfo actorInfo, int committedCost, ILookup<string, ProductionQueue> queuesByCategory)
		{
			var cost = actorInfo.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			return playerResources.GetCashAndResources() >=
				GetActiveProductionMinCashRequirement() + GetDesiredReserve(queuesByCategory) + committedCost + cost;
		}

		bool HasReservationBudget(ActorInfo actorInfo, int committedCost, ILookup<string, ProductionQueue> queuesByCategory)
		{
			return HasBudgetFor(actorInfo, committedCost, queuesByCategory);
		}

		BotProfile ActiveProfile => profileModule != null && !profileModule.IsTraitDisabled
			? profileModule.ActiveProfile
			: BotProfile.Adaptive;

		int GetActiveProductionMinCashRequirement()
		{
			var v = ActiveProfile switch
			{
				BotProfile.Eco => Info.EcoProductionMinCashRequirement,
				BotProfile.Rush => Info.RushProductionMinCashRequirement,
				BotProfile.Turtle => Info.TurtleProductionMinCashRequirement,
				BotProfile.Tech => Info.TechProductionMinCashRequirement,
				BotProfile.Expansion => Info.ExpansionProductionMinCashRequirement >= 0 ? Info.ExpansionProductionMinCashRequirement : Info.EcoProductionMinCashRequirement,
				BotProfile.Steamroller => Info.SteamrollerProductionMinCashRequirement,
				_ => -1
			};

			return v >= 0 ? v : Info.ProductionMinCashRequirement;
		}

		int GetActiveDesiredCashReserve()
		{
			var v = ActiveProfile switch
			{
				BotProfile.Eco => Info.EcoDesiredCashReserve,
				BotProfile.Rush => Info.RushDesiredCashReserve,
				BotProfile.Turtle => Info.TurtleDesiredCashReserve,
				BotProfile.Tech => Info.TechDesiredCashReserve,
				BotProfile.Expansion => Info.ExpansionDesiredCashReserve >= 0 ? Info.ExpansionDesiredCashReserve : Info.EcoDesiredCashReserve,
				BotProfile.Steamroller => Info.SteamrollerDesiredCashReserve,
				_ => -1
			};

			return v >= 0 ? v : Info.DesiredCashReserve;
		}

		int GetActiveAdditionalCashReservePerQueue()
		{
			var v = ActiveProfile switch
			{
				BotProfile.Eco => Info.EcoAdditionalCashReservePerQueue,
				BotProfile.Rush => Info.RushAdditionalCashReservePerQueue,
				BotProfile.Turtle => Info.TurtleAdditionalCashReservePerQueue,
				BotProfile.Tech => Info.TechAdditionalCashReservePerQueue,
				BotProfile.Expansion => Info.ExpansionAdditionalCashReservePerQueue >= 0 ? Info.ExpansionAdditionalCashReservePerQueue : Info.EcoAdditionalCashReservePerQueue,
				BotProfile.Steamroller => Info.SteamrollerAdditionalCashReservePerQueue,
				_ => -1
			};

			return v >= 0 ? v : Info.AdditionalCashReservePerQueue;
		}

		bool HasCapturableTarget()
		{
			return world.Actors.Any(a =>
			{
				if (a.IsDead || !a.IsInWorld || a.Owner == player)
					return false;

				if (Info.CapturableActorTypes.Count > 0 && !Info.CapturableActorTypes.Contains(a.Info.Name))
					return false;

				if (Info.CheckCaptureTargetsForVisibility && !a.CanBeViewedByPlayer(player))
					return false;

				return a.Info.HasTraitInfo<CapturableInfo>();
			});
		}

		bool TemplateAppliesToFaction(CNTeamTemplateInfo template)
		{
			return template.Factions.Length == 0 || template.Factions.Contains(player.Faction.InternalName);
		}

		int CountReservedTemplateSquads(string templateName)
		{
			if (squadManager == null)
				return 0;

			return squadManager.Squads.Count(s =>
				s.IsValid &&
				s.IsTemplateBacked &&
				string.Equals(s.TemplateName, templateName, StringComparison.OrdinalIgnoreCase));
		}

		bool QueueSpecific(IBot bot, ProductionQueue queue, string typeName)
		{
			if (!queue.BuildableItems().Any(a => a.Name == typeName))
				return false;

			bot.QueueOrder(Order.StartProduction(queue.Actor, typeName, 1));
			return true;
		}

		bool ReachedTemplateCap(string typeName, Dictionary<string, int> existingByType)
		{
			if (squadManager == null || squadManager.IsTraitDisabled)
				return false;

			var templateCap = squadManager.GetTemplateUnitCap(typeName);
			return templateCap > 0 && existingByType.GetValueOrDefault(typeName) >= templateCap;
		}

		bool HasAdequateAirRearmBuildings(ActorInfo actorInfo, Dictionary<string, int> existingByType)
		{
			if (actorInfo.TraitInfoOrDefault<AircraftInfo>() == null)
				return true;

			var rearmable = actorInfo.TraitInfoOrDefault<RearmableInfo>();
			if (rearmable == null)
				return true;

			var countAir = existingByType != null
				? existingByType.GetValueOrDefault(actorInfo.Name)
				: AIUtils.CountActorsWithNameAndTrait<IPositionable>(actorInfo.Name, player);

			var countPads = rearmable.RearmActors
				.Sum(b => AIUtils.CountActorsWithNameAndTrait<Building>(b, player));

			return countAir < countPads;
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			queueReservations.Clear();
			templateRecentBuildTicks.Clear();
		}
	}
}
