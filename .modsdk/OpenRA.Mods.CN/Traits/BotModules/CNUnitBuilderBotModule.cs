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

		[Desc("Panic-mode unit spam (see BotTick) only fires while the bot has no squads yet, or has fewer than",
			"this many owned armed units in total - i.e. a genuine 'nothing to defend with' emergency (an early",
			"rush, or having just been wiped out), not a general production boost whenever merely under attack.")]
		public readonly int PanicMaxCombatUnits = 3;

		[Desc("Credits to keep in reserve before queueing regular unit production.")]
		public readonly int DesiredCashReserve = 1200;

		[Desc("Additional reserve per available production queue to avoid spending to zero across many factories.")]
		public readonly int AdditionalCashReservePerQueue = 200;

		[Desc("Percent cut applied to the computed cash reserve at EconomyOverflow factor 1.0 (drains hoarded cash when surplus).")]
		public readonly int EconomyOverflowReserveCutPct = 80;

		[Desc("Max items queued per production queue when EconomyOverflow exceeds the unlock threshold.")]
		public readonly int EconomyOverflowMaxQueuedItemsPerQueue = 2;

		[Desc("EconomyOverflow factor (milli, 0..1000) at which the bot starts queueing more items per queue.")]
		public readonly int EconomyOverflowMaxQueuedThresholdMilli = 500;

		public readonly int RushProductionMinCashRequirement = -1;
		public readonly int TurtleProductionMinCashRequirement = -1;
		public readonly int TechProductionMinCashRequirement = -1;
		public readonly int ExpansionProductionMinCashRequirement = -1;
		public readonly int SteamrollerProductionMinCashRequirement = -1;

		public readonly int RushDesiredCashReserve = -1;
		public readonly int TurtleDesiredCashReserve = -1;
		public readonly int TechDesiredCashReserve = -1;
		public readonly int ExpansionDesiredCashReserve = -1;
		public readonly int SteamrollerDesiredCashReserve = -1;

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
			public Actor QueueActor;
			public string TemplateName;
			public int LastProgressTick;
		}

		const int FeedbackTime = 30;
		const int BaseMaxQueuedItemsPerQueue = 1;
		const int ReservationStallTicks = 15 * FeedbackTime;

		readonly World world;
		readonly Player player;
		readonly List<string> externalBuildRequests = [];
		readonly Dictionary<uint, QueueReservation> queueReservations = [];

		CNSquadManagerBotModule squadManager;
		CNBotProfileBotModule profileModule;
		CNBaseBuilderBotModule baseBuilder;
		CombatAnalysisBotModule combatAnalysis;
		PlayerResources playerResources;
		IBotRequestPauseUnitProduction[] requestPause;
		int idleUnitCount;
		int ticks;

		public CNUnitBuilderBotModule(Actor self, CNUnitBuilderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			ticks = world.LocalRandom.Next(FeedbackTime);
		}

		protected override void Created(Actor self)
		{
			RefreshActiveBotTraits();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			requestPause = self.Owner.PlayerActor
				.TraitsImplementing<IBotRequestPauseUnitProduction>()
				.ToArray();
		}

		void RefreshActiveBotTraits()
		{
			if (squadManager == null || !squadManager.IsTraitEnabled())
				squadManager = player.PlayerActor.TraitsImplementing<CNSquadManagerBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());

			if (profileModule == null || !profileModule.IsTraitEnabled())
				profileModule = player.PlayerActor.TraitsImplementing<CNBotProfileBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());

			if (baseBuilder == null || !baseBuilder.IsTraitEnabled())
				baseBuilder = player.PlayerActor.TraitsImplementing<CNBaseBuilderBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());

			if (combatAnalysis == null || !combatAnalysis.IsTraitEnabled())
				combatAnalysis = player.PlayerActor.TraitsImplementing<CombatAnalysisBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
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
			RefreshActiveBotTraits();

			if (requestPause.Any(rp => rp.PauseUnitProduction))
				return;

			ticks++;
			if (ticks % FeedbackTime != 0)
				return;

			var queuesByCategory = AIUtils.FindQueuesByCategory(player);
			PruneDeadQueueReservations();
			var existingByType = BuildExistingCounts(queuesByCategory);

			// Under active attack with essentially nothing to defend with (no squads yet, or very few units
			// owned in total - an early rush, or having just been wiped out): spam the cheapest armed unit
			// from whichever queue counters the dominant threat, instead of following normal squad demand.
			// Deliberately NOT a general production boost whenever merely under attack - a bot with a real
			// army already has squads/defense to respond with, and shouldn't keep piling on extra units on
			// top of those. Takes priority every tick while both conditions hold (CombatAnalysisBotModule.
			// HasActiveThreat decays back to false on its own, and normal production naturally regrows the
			// unit count, so this hands back control on its own once either condition clears).
			if (combatAnalysis != null && !combatAnalysis.IsTraitDisabled && combatAnalysis.HasActiveThreat()
				&& HasNoRealDefenseForce()
				&& playerResources.GetCashAndResources() >= GetActiveProductionMinCashRequirement()
				&& (Info.IdleBaseUnitsMaximum <= 0 || idleUnitCount < Info.IdleBaseUnitsMaximum)
				&& BuildPanicUnit(bot, queuesByCategory))
				return;

			var request = externalBuildRequests.FirstOrDefault();
			if (request != null)
			{
				BuildSpecific(bot, request, queuesByCategory, existingByType);
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

			var orderedCategories = OrderCategoriesByDemand(demand);

			foreach (var queueCategory in orderedCategories)
			{
				var queueSlots = GetAvailableQueues(queuesByCategory, queueCategory);
				if (queueSlots.Count == 0)
					continue;

				foreach (var queue in queueSlots)
				{
					if (TryBuildReservedTemplate(bot, queue, existingByType, demand, queuesByCategory, ref committedCost))
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

		IEnumerable<string> OrderCategoriesByDemand(Dictionary<string, int> demand)
		{
			var categoryDemands = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var (typeName, missingCount) in demand)
			{
				if (missingCount <= 0)
					continue;

				var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(typeName);
				var buildable = actorInfo?.TraitInfoOrDefault<BuildableInfo>();
				if (buildable == null)
					continue;

				foreach (var category in buildable.Queue)
					if (Info.UnitQueues.Contains(category))
						categoryDemands[category] = categoryDemands.GetValueOrDefault(category) + missingCount;
			}

			return Info.UnitQueues
				.OrderByDescending(c => categoryDemands.GetValueOrDefault(c));
		}

		ActorInfo FindBestDemandUnit(
			Dictionary<string, int> demand,
			Dictionary<string, int> existingByType,
			ILookup<string, ProductionQueue> queuesByCategory,
			string preferredQueueCategory,
			int committedCost)
		{
			var candidates = new List<(ActorInfo Actor, int Score)>();

			foreach (var (typeName, missingCount) in demand)
			{
				if (missingCount <= 0)
					continue;

				if (Info.UnitDelays != null &&
					Info.UnitDelays.TryGetValue(typeName, out var delay) &&
					world.WorldTick < delay)
					continue;

				if (ReachedUnitCap(typeName, existingByType))
					continue;

				if (!IsBuildable(typeName, queuesByCategory, preferredQueueCategory))
					continue;

				var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(typeName);
				if (actorInfo == null)
					continue;

				if (!HasBudgetFor(actorInfo, committedCost, queuesByCategory))
					continue;

				candidates.Add((actorInfo, missingCount));
			}

			if (candidates.Count == 0)
				return null;

			// Weighted-random rather than a hard argmax, matching how template selection already works
			// (WeightedTemplateOrder + TemplateSelectionSharpness) and what CLAUDE.md states the bot
			// should do: use everything continuously, with demand as a light bias only.
			//
			// Argmax was pathological here because competing units of the same category end up with
			// near-identical demand scores — observed live: gmisinf 16540, e2 16539, gasol 16140. A
			// single point of lead handed that queue to one type permanently, and the runners-up were
			// never built at all. That included the infantry types transport templates recruit as
			// passengers, which is why loading APCs sat at "wanted=4 pool=0" indefinitely.
			if (squadManager != null && !squadManager.IsTraitDisabled)
				return squadManager.WeightedTemplateOrder(candidates, c => c.Score)[0].Actor;

			// No squad manager available: keep the previous highest-demand-wins behaviour.
			var fallback = candidates[0];
			foreach (var candidate in candidates)
				if (candidate.Score > fallback.Score)
					fallback = candidate;

			return fallback.Actor;
		}

		// True if the bot has essentially nothing to defend itself with: no squads formed yet, or very few
		// owned armed units in total. Gates panic-mode unit spam so it only fires for a genuine emergency
		// (early rush, or having just been wiped out) rather than piling extra units on top of a real army
		// that already has squads/defense to respond with.
		bool HasNoRealDefenseForce()
		{
			if (squadManager == null || squadManager.IsTraitDisabled || squadManager.Squads.Count == 0)
				return true;

			var combatUnitCount = world.ActorsHavingTrait<Mobile>()
				.Count(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<AttackBaseInfo>());

			return combatUnitCount < Math.Max(0, Info.PanicMaxCombatUnits);
		}

		// Cheapest currently-buildable armed unit from whichever queue category counters the dominant
		// threat (Vehicle for an armor-heavy attack, Infantry otherwise), ignoring normal squad demand.
		// Deliberately restricted to ground categories only - falling through to Info.UnitQueues' full
		// list (which also includes Plane/Ship/Aircraft) would panic-build aircraft whenever the ground
		// queues were merely busy/full, which is never what an emergency ground defender should be.
		bool BuildPanicUnit(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			var preferredCategory = combatAnalysis.GetHighestThreatRole() == DefenseRole.ArmorDefense
				? "Vehicle"
				: "Infantry";
			var fallbackCategory = preferredCategory == "Vehicle" ? "Infantry" : "Vehicle";

			foreach (var category in new[] { preferredCategory, fallbackCategory })
			{
				var queues = GetAvailableQueues(queuesByCategory, category);
				if (queues.Count == 0)
					continue;

				var unit = queues
					.SelectMany(q => q.BuildableItems())
					.Where(a => a.HasTraitInfo<AttackBaseInfo>())
					.OrderBy(a => a.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? int.MaxValue)
					.FirstOrDefault();

				if (unit == null)
					continue;

				var queue = queues.First(q => q.BuildableItems().Any(a => a.Name == unit.Name));
				if (QueueSpecific(bot, queue, unit.Name))
					return true;
			}

			return false;
		}

		void BuildFallback(
			IBot bot,
			ILookup<string, ProductionQueue> queuesByCategory,
			Dictionary<string, int> existingByType)
		{
			var committedCost = 0;

			foreach (var category in Info.UnitQueues)
			{
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
				}
			}
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
			var bestError = float.MaxValue;

			foreach (var unit in buildable)
			{
				if (!Info.FallbackUnitsToBuild.TryGetValue(unit.Name, out var share))
					continue;
				if (Info.UnitDelays != null &&
					Info.UnitDelays.TryGetValue(unit.Name, out var delay) &&
					world.WorldTick < delay)
					continue;
				if (ReachedUnitCap(unit.Name, existingByType))
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

		void BuildSpecific(IBot bot, string typeName, ILookup<string, ProductionQueue> queuesByCategory, Dictionary<string, int> existingByType)
		{
			var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(typeName);
			if (actorInfo == null)
				return;

			var buildable = actorInfo.TraitInfoOrDefault<BuildableInfo>();
			if (buildable == null)
				return;

			if (!HasAdequateAirRearmBuildings(actorInfo, null))
				return;

			if (ReachedUnitCap(typeName, existingByType))
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
				// NOTE: `>`, not `>=` — deliberate, do not "fix" this again. The comparison is
				// off-by-one relative to the constant names: BaseMaxQueuedItemsPerQueue = 1 actually
				// permits two items in flight, EconomyOverflowMaxQueuedItemsPerQueue = 2 permits three.
				// That extra slot is what keeps a factory from idling between units — production only
				// tops queues up every FeedbackTime ticks, so at a true depth of one the factory sits
				// dead for up to that long after each unit completes. The mod's build rates are tuned
				// against this behaviour; tightening it to >= measurably cut unit output.
				if (queue.AllQueued().Count() > GetMaxQueuedItemsPerQueue())
					continue;

				result.Add(queue);
			}

			return result;
		}

		bool TryBuildReservedTemplate(
			IBot bot,
			ProductionQueue queue,
			Dictionary<string, int> existingByType,
			Dictionary<string, int> demand,
			ILookup<string, ProductionQueue> queuesByCategory,
			ref int committedCost)
		{
			var reservation = GetOrCreateReservation(queue);
			var next = GetReservedTemplateBuild(queue, reservation.TemplateName, existingByType);
			if (next == null)
			{
				reservation.TemplateName = FindBestQueueTemplate(queue, existingByType);
				next = GetReservedTemplateBuild(queue, reservation.TemplateName, existingByType);
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
			existingByType[next.Name] = existingByType.GetValueOrDefault(next.Name) + 1;
			committedCost += next.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;

			var missingCount = demand.GetValueOrDefault(next.Name) - 1;
			if (missingCount > 0)
				demand[next.Name] = missingCount;
			else
				demand.Remove(next.Name);

			if (GetReservedTemplateBuild(queue, reservation.TemplateName, existingByType) == null)
				ReleaseReservation(queue, reservation);

			return true;
		}

		// Drops reservations belonging to production buildings that no longer exist. A reservation is
		// only released when its template is finished or stalls, so a factory destroyed mid-reservation
		// left its entry behind forever — and CountReservedTemplateQueues kept counting that phantom
		// against GetEffectiveMaxInstances, which permanently stopped the bot from building that
		// template once enough factories had been lost over a long game.
		// Keyed on the owning actor rather than on presence in queuesByCategory: that lookup drops
		// queues whose trait is merely disabled (low power), and a temporary power-down should not
		// throw away a reservation the bot is still working toward.
		void PruneDeadQueueReservations()
		{
			if (queueReservations.Count == 0)
				return;

			foreach (var (actorId, reservation) in queueReservations.ToList())
			{
				var queueActor = reservation.QueueActor;
				if (queueActor == null || queueActor.IsDead || !queueActor.IsInWorld || queueActor.Disposed)
					queueReservations.Remove(actorId);
			}
		}

		QueueReservation GetOrCreateReservation(ProductionQueue queue)
		{
			if (!queueReservations.TryGetValue(queue.Actor.ActorID, out var reservation))
			{
				reservation = new QueueReservation { QueueActor = queue.Actor, LastProgressTick = world.WorldTick };
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

		string FindBestQueueTemplate(ProductionQueue queue, Dictionary<string, int> existingByType)
		{
			if (squadManager == null || squadManager.IsTraitDisabled)
				return null;

			// Templates that still need a unit built for this queue, split so partially-built
			// squads (reinforcement deficit) are finished before fresh templates are started.
			var deficit = new List<KeyValuePair<string, CNTeamTemplateInfo>>();
			var fresh = new List<KeyValuePair<string, CNTeamTemplateInfo>>();

			foreach (var kv in squadManager.Info.Teams)
			{
				if (!TemplateAppliesToFaction(kv.Value))
					continue;

				// Returns null when the template needs nothing right now or is at its instance cap.
				if (GetReservedTemplateBuild(queue, kv.Key, existingByType) == null)
					continue;

				if (HasExistingTemplateQueueDeficit(kv.Key, queue, existingByType))
					deficit.Add(kv);
				else
					fresh.Add(kv);
			}

			// Weighted-random rotation: every eligible template can win, biased only lightly
			// toward higher EffectiveScore (tag/need score) via TemplateSelectionSharpness, so
			// the bot keeps using all templates instead of locking onto the single best one.
			var pool = deficit.Count > 0 ? deficit : fresh;
			if (pool.Count == 0)
				return null;

			var ordered = squadManager.WeightedTemplateOrder(pool, kv => squadManager.GetEffectiveScore(kv.Value));
			return ordered[0].Key;
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

		bool HasExistingTemplateQueueDeficit(string templateName, ProductionQueue queue, Dictionary<string, int> existingByType)
		{
			foreach (var squad in squadManager.Squads)
			{
				// Boost priority for squads we will actually reinforce:
				// forming squads (not yet operational) and home-role squads.
				// Operational attack squads are skipped — we don't reinforce them.
				// Transports still loading at base count too, mirroring GetReservedTemplateBuild:
				// otherwise a half-loaded transport is classified as a "fresh" template rather than a
				// deficit, and loses the priority that finishing a started squad is supposed to get.
				if (!squad.IsValid ||
					(squad.IsOperational && !squad.AllowsOperationalReinforcement && !squad.AcceptingPassengers) ||
					!string.Equals(squad.TemplateName, templateName, StringComparison.OrdinalIgnoreCase))
					continue;

				foreach (var assignment in OrderedAssignments(squad.SlotAssignments))
				{
					if (assignment.MissingCount <= 0 || assignment.SlotInfo.IsPassenger)
						continue;

					if (SelectBuildableType(queue, assignment.SlotInfo, existingByType) != null)
						return true;
				}

				if (!squad.AcceptingPassengers)
					continue;

				foreach (var assignment in squad.SlotAssignments)
				{
					if (!assignment.SlotInfo.IsPassenger || assignment.MissingToRecruit <= 0)
						continue;

					if (SelectBuildableType(queue, assignment.SlotInfo, existingByType) != null)
						return true;
				}
			}

			return false;
		}

		ActorInfo GetReservedTemplateBuild(ProductionQueue queue, string templateName, Dictionary<string, int> existingByType)
		{
			if (string.IsNullOrEmpty(templateName) || squadManager == null || squadManager.IsTraitDisabled)
				return null;

			if (!squadManager.Info.Teams.TryGetValue(templateName, out var template) || !TemplateAppliesToFaction(template))
				return null;

			// AcceptingPassengers widens the filter on purpose. A transport squad whose carrier slot is
			// filled counts as operational (MinSlotsToActivate is 1 for the APC templates) and does not
			// allow operational reinforcement, so it used to be excluded here entirely — while every
			// passenger slot below is skipped as well. The reservation path was therefore completely
			// blind to a loading transport's missing infantry, and since BuildDemand hands each queue to
			// the reservation first and only falls through to the passenger-aware demand path when the
			// reservation yields nothing, transports simply never got topped up. They departed on the
			// MaxLoadTicks timeout with whatever they were formed with.
			foreach (var squad in squadManager.Squads
				.Where(s => s.IsValid &&
					string.Equals(s.TemplateName, templateName, StringComparison.OrdinalIgnoreCase) &&
					(!s.IsOperational || s.AllowsOperationalReinforcement || s.AcceptingPassengers))
				.OrderBy(s => s.IsOperational ? 1 : 0)
				.ThenByDescending(s => s.TemplateInfo != null ? squadManager.GetEffectiveScore(s.TemplateInfo) : 0))
			{
				foreach (var assignment in OrderedAssignments(squad.SlotAssignments))
				{
					if (assignment.MissingCount <= 0 || assignment.SlotInfo.IsPassenger)
						continue;

					var reservedType = SelectBuildableType(queue, assignment.SlotInfo, existingByType);
					if (reservedType != null)
						return reservedType;
				}

				// Passengers are handled separately: OrderedAssignments deliberately omits them (they are
				// not squad Units), and the deficit must be measured with MissingToRecruit, since a boarded
				// passenger has left the world and no longer shows up in MissingCount.
				if (!squad.AcceptingPassengers)
					continue;

				foreach (var assignment in squad.SlotAssignments)
				{
					if (!assignment.SlotInfo.IsPassenger || assignment.MissingToRecruit <= 0)
						continue;

					var passengerType = SelectBuildableType(queue, assignment.SlotInfo, existingByType);
					if (passengerType != null)
						return passengerType;
				}
			}

			// Use the squad manager's effective (ScaleWithBuilding-aware) cap so we don't queue
			// units for template instances the manager will refuse to form (they'd sit idle in the pool).
			var reservedTemplateInstances = CountReservedTemplateSquads(templateName)
				+ CountReservedTemplateQueues(templateName, queue.Actor.ActorID);
			if (reservedTemplateInstances >= squadManager.GetEffectiveMaxInstances(template))
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

		static IEnumerable<CNSlotAssignment> OrderedAssignments(IEnumerable<CNSlotAssignment> assignments)
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

		static IEnumerable<CNSlotInfo> OrderedSlots(IEnumerable<CNSlotInfo> slots)
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
				if (ReachedUnitCap(typeName, existingByType))
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
			if (baseBuilder != null && !baseBuilder.HasEconomyRecoveryPath())
				return 0;

			var queueCount = queuesByCategory.SelectMany(g => g).Count();
			var raw = GetActiveDesiredCashReserve() + queueCount * GetActiveAdditionalCashReservePerQueue();

			var milli = baseBuilder?.EconomyOverflowMilli ?? 0;
			if (milli <= 0 || Info.EconomyOverflowReserveCutPct <= 0)
				return raw;

			var cut = raw * Info.EconomyOverflowReserveCutPct * milli / (100 * 1000);
			return Math.Max(0, raw - cut);
		}

		int GetMaxQueuedItemsPerQueue()
		{
			var milli = baseBuilder?.EconomyOverflowMilli ?? 0;
			return milli >= Info.EconomyOverflowMaxQueuedThresholdMilli
				? Math.Max(BaseMaxQueuedItemsPerQueue, Info.EconomyOverflowMaxQueuedItemsPerQueue)
				: BaseMaxQueuedItemsPerQueue;
		}

		bool HasBudgetFor(ActorInfo actorInfo, int committedCost, ILookup<string, ProductionQueue> queuesByCategory)
		{
			var cost = actorInfo.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;

			// Rare/expensive high-priority units (IgnoresCashReserve templates) skip the reserve buffer -
			// they'd otherwise almost never clear DesiredCashReserve against cheaper, more frequent demand.
			var skipsReserve = squadManager != null && !squadManager.IsTraitDisabled &&
				squadManager.GetTypesIgnoringCashReserve().Contains(actorInfo.Name);
			var reserve = skipsReserve ? 0 : GetDesiredReserve(queuesByCategory);

			return playerResources.GetCashAndResources() >=
				GetActiveProductionMinCashRequirement() + reserve + committedCost + cost;
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
				BotProfile.Rush => Info.RushProductionMinCashRequirement,
				BotProfile.Turtle => Info.TurtleProductionMinCashRequirement,
				BotProfile.Tech => Info.TechProductionMinCashRequirement,
				BotProfile.Expansion => Info.ExpansionProductionMinCashRequirement,
				BotProfile.Steamroller => Info.SteamrollerProductionMinCashRequirement,
				_ => -1
			};

			return v >= 0 ? v : Info.ProductionMinCashRequirement;
		}

		int GetActiveDesiredCashReserve()
		{
			var v = ActiveProfile switch
			{
				BotProfile.Rush => Info.RushDesiredCashReserve,
				BotProfile.Turtle => Info.TurtleDesiredCashReserve,
				BotProfile.Tech => Info.TechDesiredCashReserve,
				BotProfile.Expansion => Info.ExpansionDesiredCashReserve,
				BotProfile.Steamroller => Info.SteamrollerDesiredCashReserve,
				_ => -1
			};

			return v >= 0 ? v : Info.DesiredCashReserve;
		}

		int GetActiveAdditionalCashReservePerQueue()
		{
			var v = ActiveProfile switch
			{
				BotProfile.Rush => Info.RushAdditionalCashReservePerQueue,
				BotProfile.Turtle => Info.TurtleAdditionalCashReservePerQueue,
				BotProfile.Tech => Info.TechAdditionalCashReservePerQueue,
				BotProfile.Expansion => Info.ExpansionAdditionalCashReservePerQueue,
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

		static bool QueueSpecific(IBot bot, ProductionQueue queue, string typeName)
		{
			if (!queue.BuildableItems().Any(a => a.Name == typeName))
				return false;

			bot.QueueOrder(Order.StartProduction(queue.Actor, typeName, 1));
			return true;
		}

		bool ReachedUnitCap(string typeName, Dictionary<string, int> existingByType)
		{
			if (string.IsNullOrEmpty(typeName))
				return false;

			var existing = existingByType.GetValueOrDefault(typeName);
			var configuredLimit = GetActiveUnitLimit(typeName);
			if (configuredLimit > 0 && existing >= configuredLimit)
				return true;

			if (squadManager == null || squadManager.IsTraitDisabled)
				return false;

			var templateCap = squadManager.GetTemplateUnitCap(typeName);
			return templateCap > 0 && existing >= templateCap;
		}

		int GetActiveUnitLimit(string typeName)
		{
			return TryGetUnitLimit(Info.UnitLimits, typeName, out var limit) ? limit : 0;
		}

		static bool TryGetUnitLimit(Dictionary<string, int> limits, string typeName, out int limit)
		{
			limit = 0;
			if (limits == null || limits.Count == 0)
				return false;

			if (limits.TryGetValue(typeName, out limit))
				return true;

			foreach (var kv in limits)
			{
				if (!string.Equals(kv.Key, typeName, StringComparison.OrdinalIgnoreCase))
					continue;

				limit = kv.Value;
				return true;
			}

			return false;
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
		}
	}
}
