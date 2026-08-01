#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Orders engineers to enter damaged bridge huts and requests replacements when needed.")]
	public class CNBridgeRepairBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Actor types that can repair bridges via RepairsBridges.")]
		public readonly FrozenSet<string> RepairerActorTypes = FrozenSet<string>.Empty;

		[Desc("Bridge hut actor types to consider. Leave empty to include all BridgeHut actors.")]
		public readonly FrozenSet<string> BridgeHutActorTypes = FrozenSet<string>.Empty;

		[Desc("Delay in ticks between bridge repair scans.")]
		public readonly int RepairScanInterval = 125;

		[Desc("Minimum bridge damage state required before a repairer is assigned.")]
		public readonly DamageState MinimumDamageState = DamageState.Light;

		[Desc("Maximum number of bridge hut options to consider on each scan.")]
		public readonly int MaximumRepairTargetOptions = 25;

		[Desc("Maximum owned or requested repairers for bridge repair support.")]
		public readonly int MaximumRepairers = 2;

		[Desc("Should visibility be considered when searching for repair targets?")]
		public readonly bool CheckRepairTargetsForVisibility = true;

		public override object Create(ActorInitializer init) { return new CNBridgeRepairBotModule(init.Self, this); }
	}

	public class CNBridgeRepairBotModule : ConditionalTrait<CNBridgeRepairBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		readonly Dictionary<Actor, Actor> activeAssignments = [];
		IBotRequestUnitProduction[] requestUnitProduction = [];
		int repairScanTicks;

		public CNBridgeRepairBotModule(Actor self, CNBridgeRepairBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			repairScanTicks = world.LocalRandom.Next(RepairScanInterval);
		}

		protected override void Created(Actor self)
		{
			requestUnitProduction = self.Owner.PlayerActor.TraitsImplementing<IBotRequestUnitProduction>().ToArray();
			base.Created(self);
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (--repairScanTicks > 0)
				return;

			repairScanTicks = RepairScanInterval;
			QueueRepairOrders(bot);
		}

		void QueueRepairOrders(IBot bot)
		{
			if (player.WinState != WinState.Undefined)
				return;

			PruneAssignments();

			var targets = world.ActorsHavingTrait<BridgeHut>()
				.Where(IsRepairTarget)
				.OrderByDescending(a => a.Trait<BridgeHut>().BridgeDamageState)
				.Take(Math.Max(1, Info.MaximumRepairTargetOptions))
				.ToList();

			if (targets.Count == 0)
				return;

			var repairers = world.ActorsHavingTrait<RepairsBridges>()
				.Where(IsAvailableRepairer)
				.ToList();

			if (repairers.Count == 0)
			{
				RequestRepairerProduction(bot);
				return;
			}

			var assignedTargets = activeAssignments.Values.ToHashSet();
			foreach (var repairer in repairers)
			{
				var target = targets
					.Where(t => !assignedTargets.Contains(t))
					.ClosestToWithPathFrom(repairer);

				if (target == null)
					continue;

				bot.QueueOrder(new Order("RepairBridge", repairer, Target.FromActor(target), false));
				activeAssignments[repairer] = target;
				assignedTargets.Add(target);
				AIUtils.BotDebug("AI ({0}): Ordered {1} to repair bridge hut {2}", player.ClientIndex, repairer, target);
			}
		}

		void PruneAssignments()
		{
			var stale = activeAssignments
				.Where(kv => !IsActiveAssignment(kv.Key, kv.Value))
				.Select(kv => kv.Key)
				.ToArray();

			foreach (var repairer in stale)
				activeAssignments.Remove(repairer);
		}

		bool IsActiveAssignment(Actor repairer, Actor target)
		{
			return IsLiveOwnedRepairer(repairer) && !repairer.IsIdle && IsRepairTarget(target);
		}

		bool IsAvailableRepairer(Actor repairer)
		{
			return IsLiveOwnedRepairer(repairer) &&
				repairer.IsIdle &&
				!activeAssignments.ContainsKey(repairer) &&
				repairer.Info.HasTraitInfo<IPositionableInfo>();
		}

		bool IsLiveOwnedRepairer(Actor repairer)
		{
			if (repairer.Owner != player || repairer.IsDead || !repairer.IsInWorld)
				return false;

			if (Info.RepairerActorTypes.Count > 0 && !Info.RepairerActorTypes.Contains(repairer.Info.Name.ToLowerInvariant()))
				return false;

			return repairer.TraitOrDefault<RepairsBridges>() != null;
		}

		bool IsRepairTarget(Actor target)
		{
			if (target == null || target.IsDead || !target.IsInWorld)
				return false;

			if (Info.BridgeHutActorTypes.Count > 0 && !Info.BridgeHutActorTypes.Contains(target.Info.Name.ToLowerInvariant()))
				return false;

			if (Info.CheckRepairTargetsForVisibility && !target.CanBeViewedByPlayer(player))
				return false;

			var hut = target.TraitOrDefault<BridgeHut>();
			return hut != null && !hut.Repairing && hut.BridgeDamageState >= Info.MinimumDamageState;
		}

		void RequestRepairerProduction(IBot bot)
		{
			if (Info.MaximumRepairers <= 0 || Info.RepairerActorTypes.Count == 0)
				return;

			var unitBuilder = requestUnitProduction.FirstEnabledTraitOrDefault();
			if (unitBuilder == null)
				return;

			var repairerCount = CountOwnedRepairers() + Info.RepairerActorTypes.Sum(type => unitBuilder.RequestedProductionCount(bot, type));
			foreach (var repairerType in Info.RepairerActorTypes)
			{
				if (repairerCount >= Info.MaximumRepairers)
					break;

				if (unitBuilder.RequestedProductionCount(bot, repairerType) > 0)
					continue;

				unitBuilder.RequestUnitProduction(bot, repairerType);
				repairerCount++;
			}
		}

		int CountOwnedRepairers()
		{
			return world.ActorsHavingTrait<RepairsBridges>().Count(IsLiveOwnedRepairer);
		}

		int RepairScanInterval => Info.RepairScanInterval > 0 ? Info.RepairScanInterval : 1;

		// NOTE: there is deliberately no INotifyActorDisposing here. This trait lives on the player
		// actor, so Disposing would only ever fire for the player actor itself — never for a repairer
		// or a bridge hut. The old handler matched `self` against the assignment dictionary and could
		// therefore never remove anything. PruneAssignments already drops dead repairers and repaired
		// or destroyed huts at the start of every scan, which is where that cleanup belongs.
	}
}
