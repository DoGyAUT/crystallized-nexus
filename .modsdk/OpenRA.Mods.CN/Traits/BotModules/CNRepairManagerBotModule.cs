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

using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using OpenRA.Mods.CN.Traits;
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

		public CNRepairManagerBotModule(Actor self, CNRepairManagerBotModuleInfo info)
			: base(info)
		{
			repairScanTicks = self.World.LocalRandom.Next(RepairScanInterval);
		}

		void IBotNotifyIdleBaseUnits.UpdatedIdleBaseUnits(List<Actor> idleUnits)
		{
			idleBaseUnits.Clear();
			idleBaseUnits.AddRange(idleUnits);
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (idleBaseUnits.Count == 0 || --repairScanTicks > 0)
				return;

			repairScanTicks = RepairScanInterval;
			var assignments = 0;

			foreach (var actor in idleBaseUnits)
			{
				if (assignments >= Info.MaxAssignmentsPerScan)
					break;

				if (actor.Owner != bot.Player || actor.IsDead || !actor.IsInWorld || !actor.IsIdle)
					continue;

				var health = actor.TraitOrDefault<IHealth>();
				if (health == null || health.DamageState < Info.MinimumDamageState || health.DamageState >= DamageState.Dead)
					continue;

				if (TryAssignMobileRepair(bot, actor))
				{
					assignments++;
					continue;
				}

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

				var repairableInBarracks = actor.TraitOrDefault<RepairableInBarracks>();
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

		bool TryAssignMobileRepair(IBot bot, Actor actor)
		{
			if (Info.MobileRepairActorTypes.Count == 0 || actor.Info.TraitInfoOrDefault<AircraftInfo>() != null)
				return false;

			var bestRepairer = idleBaseUnits
				.Where(a =>
					a != actor &&
					a.Owner == actor.Owner &&
					!a.IsDead &&
					a.IsInWorld &&
					a.IsIdle &&
					Info.MobileRepairActorTypes.Contains(a.Info.Name))
				.OrderBy(a => (a.Location - actor.Location).LengthSquared)
				.FirstOrDefault();

			if (bestRepairer == null)
				return false;

			var maxRangeSq = Info.MobileRepairSearchRadius * Info.MobileRepairSearchRadius;
			if ((bestRepairer.Location - actor.Location).LengthSquared > maxRangeSq)
				return false;

			bot.QueueOrder(new Order("Move", actor, Target.FromCell(actor.World, bestRepairer.Location), false));
			return true;
		}

		int RepairScanInterval => Info.RepairScanInterval > 0 ? Info.RepairScanInterval : 1;
	}
}
