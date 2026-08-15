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

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.CN.Traits.BotModules.Squads;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Sends damaged idle base units to allied repair facilities.")]
	public class CNRepairManagerBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Delay in ticks between repair scans.")]
		public readonly int RepairScanInterval = 75;

		[Desc("Minimum damage state required before a unit is sent to repair.")]
		public readonly DamageState MinimumDamageState = DamageState.Light;

		[Desc("Minimum damage state required before infantry is sent into a barracks for repair.")]
		public readonly DamageState BarracksMinimumDamageState = DamageState.Heavy;

		[Desc("Maximum number of repair orders to issue on each scan.")]
		public readonly int MaxAssignmentsPerScan = 3;

		[Desc("Mobile repair actor types that should repair damaged idle units inside the base area.")]
		public readonly FrozenSet<string> MobileRepairActorTypes = FrozenSet<string>.Empty;

		[Desc("Only use mobile repairers when the damaged unit is within this many cells of one.")]
		public readonly int MobileRepairSearchRadius = 10;

		public override object Create(ActorInitializer init) { return new CNRepairManagerBotModule(init.Self, this); }
	}

	public class CNRepairManagerBotModule : ConditionalTrait<CNRepairManagerBotModuleInfo>, IBotTick, IBotNotifyIdleBaseUnits
	{
		readonly List<Actor> idleBaseUnits = [];
		int repairScanTicks;
		readonly World world;
		CNSquadManagerBotModule squadManager;

		public CNRepairManagerBotModule(Actor self, CNRepairManagerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			repairScanTicks = world.LocalRandom.Next(RepairScanInterval);
		}

		void IBotNotifyIdleBaseUnits.UpdatedIdleBaseUnits(List<Actor> idleUnits)
		{
			idleBaseUnits.Clear();
			idleBaseUnits.AddRange(idleUnits);
		}

		void IBotTick.BotTick(IBot bot)
		{
			using var perfScope = CNBotPerf.Sample(bot, nameof(CNRepairManagerBotModule));

			if (--repairScanTicks > 0)
				return;

			repairScanTicks = RepairScanInterval;
			RefreshActiveSquadManager(bot);
			var assignments = 0;

			// Merge idle-base-units with any damaged idle infantry not yet in that list
			// (e.g. retreated units that the squad manager is holding back for repair).
			var repairCandidates = new HashSet<Actor>(idleBaseUnits);
			foreach (var actor in world.ActorsHavingTrait<RepairableInBarracks>())
				if (actor.Owner == bot.Player && !actor.IsDead && actor.IsInWorld && actor.IsIdle)
					repairCandidates.Add(actor);

			foreach (var actor in repairCandidates)
			{
				if (assignments >= Info.MaxAssignmentsPerScan)
					break;

				if (actor.Owner != bot.Player || actor.IsDead || !actor.IsInWorld || !actor.IsIdle)
					continue;

				if (squadManager?.IsUnitAssignedToSquad(actor) == true)
					continue;

				var health = actor.TraitOrDefault<IHealth>();
				if (health == null || health.DamageState < Info.MinimumDamageState || health.DamageState >= DamageState.Dead)
					continue;

				var repairableInBarracks = actor.TraitOrDefault<RepairableInBarracks>();
				if (repairableInBarracks != null && health.DamageState < Info.BarracksMinimumDamageState)
					continue;

				var mobileRepair = TryAssignMobileRepair(bot, actor);
				if (mobileRepair == MobileRepairResult.Ordered)
				{
					assignments++;
					continue;
				}

				// Already standing at its repairer and being healed: nothing to order, and nothing to send
				// it away for either. Falling through here is what had a unit drive off to a service depot
				// while the aura beside it was already repairing it - and it does not spend an assignment
				// slot, since no order was issued.
				if (mobileRepair == MobileRepairResult.AlreadyServiced)
					continue;

				var repairableNear = actor.TraitOrDefault<RepairableNear>();
				if (repairableNear != null)
				{
					var repairBuilding = repairableNear.FindRepairBuilding(actor);
					if (repairBuilding != null)
					{
						bot.QueueOrder(new Order("RepairNear", actor, Target.FromActor(repairBuilding), false));
						assignments++;
						continue;
					}
				}

				if (repairableInBarracks != null)
				{
					var repairBuilding = repairableInBarracks.FindRepairBuilding(actor);
					if (repairBuilding != null)
					{
						bot.QueueOrder(new Order("Repair", actor, Target.FromActor(repairBuilding), false));
						assignments++;
						continue;
					}
				}

				var repairable = actor.TraitOrDefault<Repairable>();
				if (repairable != null)
				{
					var repairBuilding = repairable.FindRepairBuilding(actor);
					if (repairBuilding != null)
					{
						bot.QueueOrder(new Order("Repair", actor, Target.FromActor(repairBuilding), false));
						assignments++;
					}
				}
			}
		}

		void RefreshActiveSquadManager(IBot bot)
		{
			if (squadManager != null && squadManager.IsTraitEnabled())
				return;

			squadManager = bot.Player.PlayerActor.TraitsImplementing<CNSquadManagerBotModule>()
				.FirstOrDefault(t => t.IsTraitEnabled());
		}

		/// <summary>
		/// What became of a mobile-repair attempt. Three outcomes, because two of them used to share the
		/// same "false" and the caller could not tell them apart: a unit already parked at its repairer,
		/// being healed by its aura, was read as "mobile repair not possible" and sent off to a distant
		/// service depot on the very next scan.
		/// </summary>
		enum MobileRepairResult { NotApplicable, AlreadyServiced, Ordered }

		MobileRepairResult TryAssignMobileRepair(IBot bot, Actor actor)
		{
			if (Info.MobileRepairActorTypes.Count == 0 || actor.Info.TraitInfoOrDefault<AircraftInfo>() != null)
				return MobileRepairResult.NotApplicable;

			var bestRepairer = idleBaseUnits
				.Where(a =>
					a != actor &&
					a.Owner == actor.Owner &&
					!a.IsDead &&
					a.IsInWorld &&
					a.IsIdle &&
					Info.MobileRepairActorTypes.Contains(a.Info.Name))

				// Ranked in world space, not in cells. CPos distance on a RectangularIsometric map is not
				// the distance on the ground: the same raw cell delta is a different real separation along
				// one axis than along the other, so the nearest repairer by this measure was not always the
				// nearest one, and the radius below let one damaged unit in and kept an equally close one
				// out depending on which way it happened to lie.
				.OrderBy(a => (a.CenterPosition - actor.CenterPosition).HorizontalLengthSquared)
				.FirstOrDefault();

			if (bestRepairer == null)
				return MobileRepairResult.NotApplicable;

			var maxRange = WDist.FromCells(Info.MobileRepairSearchRadius);
			var distanceSq = (bestRepairer.CenterPosition - actor.CenterPosition).HorizontalLengthSquared;
			if (distanceSq > (long)maxRange.Length * maxRange.Length)
				return MobileRepairResult.NotApplicable;

			// Already standing at the repairer: the unit is being serviced (or the repairer can't help
			// it), so there is nothing to order. Returning true here would burn one of the scan's
			// MaxAssignmentsPerScan slots on a no-op every single scan, starving units that do need
			// a repair order.
			// In cells, like the radius above. This read "<= 2" while distanceSq was a raw cell delta -
			// about one and a half cells - and measuring in world units now makes 2 a couple of pixels,
			// which no unit is ever inside. The check would have been dead.
			var atRepairer = WDist.FromCells(2);
			if (distanceSq <= (long)atRepairer.Length * atRepairer.Length)
				return MobileRepairResult.AlreadyServiced;

			bot.QueueOrder(new Order("Move", actor, Target.FromCell(actor.World, bestRepairer.Location), false));
			return MobileRepairResult.Ordered;
		}

		int RepairScanInterval => Info.RepairScanInterval > 0 ? Info.RepairScanInterval : 1;
	}
}
