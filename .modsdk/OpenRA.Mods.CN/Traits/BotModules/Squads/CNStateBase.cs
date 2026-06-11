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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads
{
	abstract class CNStateBase
	{
		/// <summary>
		/// Validates that the squad has at least one operational carrier unit that is alive and in-world.
		/// Returns true if carriers are valid, false otherwise.
		/// </summary>
		public static bool ValidateCarriers(CNSquad squad)
		{
			if (squad == null || squad.CarrierUnits == null || !squad.CarrierUnits.Any())
				return false;

			return squad.CarrierUnits.Any(u => !u.IsDead && u.IsInWorld);
		}

		/// <summary>
		/// Horizontal (X/Y only) squared distance. Aircraft carry their cruise
		/// altitude in Z, so a 3-D distance check would never report a flying
		/// transport as "arrived"; callers comparing an airborne actor against a
		/// ground cell must ignore Z.
		/// </summary>
		protected static long HorizontalLengthSquared(WVec v)
			=> (long)v.X * v.X + (long)v.Y * v.Y;

		// --- Movement helpers (delegate to CNSquadHelper) ---
		protected static void GoToRandomOwnBuilding(CNSquad squad)
			=> CNSquadHelper.GoToRandomOwnBuilding(squad);

		protected static CPos RandomBuildingLocation(CNSquad squad)
			=> CNSquadHelper.RandomBuildingLocation(squad);

		// --- Attack helpers ---

		/// <summary>Returns true if the actor has an attack activity anywhere in its
		/// current activity chain (not just the head / first queued entry), so a
		/// deeply queued or derived Attack/FlyAttack still counts as busy.</summary>
		protected static bool BusyAttack(Actor a)
		{
			if (a.IsIdle)
				return false;

			var current = a.CurrentActivity;
			return current.ActivitiesImplementing<Attack>().Any()
				|| current.ActivitiesImplementing<FlyAttack>().Any();
		}

		/// <summary>Returns true if the actor has a weapon that can target the given actor.</summary>
		protected static bool CanAttackTarget(Actor a, Actor target)
		{
			if (!a.Info.HasTraitInfo<AttackBaseInfo>())
				return false;

			var targetTypes = target.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty)
				return false;

			foreach (var arm in a.TraitsImplementing<Armament>())
			{
				if (arm.IsTraitDisabled)
					continue;
				if (arm.IsTraitPaused)
					continue;
				if (arm.Weapon.IsValidTarget(targetTypes))
					return true;
			}

			return false;
		}

		protected static bool IsDefenseStructure(Actor actor)
		{
			if (actor == null || actor.IsDead || !actor.IsInWorld || actor.OccupiesSpace == null)
				return false;

			if (!actor.Info.HasTraitInfo<BuildingInfo>())
				return false;

			var caps = actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			return (caps?.Contains("Defense") ?? false) || actor.Info.HasTraitInfo<AttackBaseInfo>();
		}

		protected static Actor FindDefenseNearTarget(CNSquad squad, Actor target, int radiusCells)
		{
			if (target == null || target.IsDead || !target.IsInWorld || target.OccupiesSpace == null)
				return null;

			return FindDefenseNearPosition(squad, target.CenterPosition, radiusCells);
		}

		protected static Actor FindDefenseNearPosition(CNSquad squad, WPos center, int radiusCells)
		{
			return squad.World.FindActorsInCircle(center, WDist.FromCells(radiusCells))
				.Where(a => squad.SquadManager.IsLiveEnemyActor(a) && IsDefenseStructure(a))
				.MinByOrDefault(a => (a.CenterPosition - center).LengthSquared);
		}

		// --- Flee decision ---
		protected virtual bool ShouldFlee(CNSquad squad)
		{
			return ShouldFlee(squad, enemies =>
				!CNAttackOrFleeFuzzy.Default.CanAttack(squad.Units, enemies, squad.SquadManager.GetAttackFuzzyBoost()));
		}

		protected static bool ShouldFlee(CNSquad squad, Func<IReadOnlyCollection<Actor>, bool> flee)
		{
			if (!squad.IsValid)
				return false;

			var dangerRadius = squad.SquadManager.Info.DangerScanRadius;
			var units = squad.World.FindActorsInCircle(
				squad.CenterPosition(), WDist.FromCells(dangerRadius)).ToList();

			// Don't flee if own buildings are nearby
			foreach (var u in units)
				if (u.Owner == squad.Bot.Player && u.Info.HasTraitInfo<BuildingInfo>())
					return false;

			var enemies = units
				.Where(u => squad.SquadManager.IsPreferredEnemyUnit(u) &&
							u.Info.HasTraitInfo<AttackBaseInfo>())
				.ToList();

			if (enemies.Count == 0)
				return false;

			return flee(enemies);
		}

		// --- Enemy finding (delegate to CNSquadHelper) ---
		protected static Actor FindClosestEnemyUnit(CNSquad squad)
			=> CNSquadHelper.FindClosestEnemyUnit(squad);

		protected static Actor FindClosestEnemyBuilding(CNSquad squad)
			=> CNSquadHelper.FindClosestEnemyBuilding(squad);

		protected static Actor FindPriorityTarget(CNSquad squad, string[] priorityTypes, Actor sourceUnit)
			=> CNSquadHelper.FindPriorityTarget(squad, priorityTypes, sourceUnit);

		// --- Ammo helpers (for air squads) ---
		protected static bool IsRearming(Actor a)
		{
			return !a.IsIdle &&
				(a.CurrentActivity.ActivitiesImplementing<Resupply>().Any() ||
				 a.CurrentActivity.ActivitiesImplementing<ReturnToBase>().Any());
		}

		protected static bool FullAmmo(IEnumerable<AmmoPool> ammoPools)
		{
			foreach (var ap in ammoPools)
				if (!ap.HasFullAmmo)
					return false;
			return true;
		}

		protected static bool HasAmmo(IEnumerable<AmmoPool> ammoPools)
		{
			foreach (var ap in ammoPools)
				if (!ap.HasAmmo)
					return false;
			return true;
		}

		protected static bool ReloadsAutomatically(IEnumerable<AmmoPool> ammoPools, Rearmable rearmable)
		{
			if (rearmable == null)
				return true;
			foreach (var ap in ammoPools)
				if (!rearmable.Info.AmmoPools.Contains(ap.Info.Name))
					return false;
			return true;
		}

		protected static void Retreat(CNSquad squad, bool flee, bool rearm, bool repair)
		{
			var fleeingUnits = new List<Actor>();
			var repairOrderedForAircraft = false;

			foreach (var unit in squad.OrderableUnits)
			{
				if (IsRearming(unit))
					continue;

				var orderQueued = false;

				if (rearm && NeedsRearm(unit))
				{
					squad.Bot.QueueOrder(new Order("ReturnToBase", unit, false));
					orderQueued = true;
				}

				if (repair && NeedsRepair(unit) && TryFindRepairOrder(unit, out var orderId, out var repairBuilding))
				{
					var isAircraft = unit.Info.HasTraitInfo<AircraftInfo>();
					if (!isAircraft || !repairOrderedForAircraft)
					{
						squad.Bot.QueueOrder(new Order(orderId, unit, Target.FromActor(repairBuilding), orderQueued));
						orderQueued = true;
						repairOrderedForAircraft |= isAircraft;
					}
				}

				if (flee && !orderQueued)
					fleeingUnits.Add(unit);
			}

			if (fleeingUnits.Count > 0)
				squad.Bot.QueueOrder(new Order("Move", null, Target.FromCell(squad.World, RandomBuildingLocation(squad)), false,
					groupedActors: fleeingUnits.ToArray()));
		}

		static bool NeedsRearm(Actor unit)
		{
			var ammoPools = unit.TraitsImplementing<AmmoPool>().ToArray();
			return ammoPools.Length > 0 &&
				!ReloadsAutomatically(ammoPools, unit.TraitOrDefault<Rearmable>()) &&
				!FullAmmo(ammoPools);
		}

		static bool NeedsRepair(Actor unit)
		{
			var health = unit.TraitOrDefault<IHealth>();
			return health != null && health.DamageState > DamageState.Undamaged;
		}

		static bool TryFindRepairOrder(Actor unit, out string orderId, out Actor repairBuilding)
		{
			orderId = "Repair";
			repairBuilding = null;

			var repairable = unit.TraitOrDefault<Repairable>();
			if (repairable != null)
			{
				repairBuilding = repairable.FindRepairBuilding(unit);
				return repairBuilding != null;
			}

			var repairableNear = unit.TraitOrDefault<RepairableNear>();
			if (repairableNear == null)
				return false;

			orderId = "RepairNear";
			repairBuilding = repairableNear.FindRepairBuilding(unit);
			return repairBuilding != null;
		}
	}
}
