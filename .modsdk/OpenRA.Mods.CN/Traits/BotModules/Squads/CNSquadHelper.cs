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

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads
{
	/// <summary>
	/// Static utility methods shared across squad states and the squad manager.
	/// CNStateBase delegates to these, but non-state code can call them directly.
	/// </summary>
	static class CNSquadHelper
	{
		// --- Movement ---

		/// <summary>
		/// Movement order name appropriate for the squad's primary movement mode.
		/// Aircraft and subterranean units use plain "Move" (AttackMove fights all
		/// the way and disrupts flight paths / burrow runs); everything else uses
		/// "AttackMove" so the wave can engage opportunistic targets en route.
		/// </summary>
		public static string GetMovementOrderName(CNSquad squad)
		{
			switch (squad.Type)
			{
				case CNSquadType.Air:
				case CNSquadType.AirTransport:
				case CNSquadType.AircraftAttack:
				case CNSquadType.AircraftRaider:
				case CNSquadType.SubterraneanAssault:
				case CNSquadType.SubterraneanTransport:
					return "Move";
				default:
					return "AttackMove";
			}
		}

		public static void GoToRandomOwnBuilding(CNSquad squad)
		{
			var loc = RandomBuildingLocation(squad);
			foreach (var a in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Move", a, Target.FromCell(squad.World, loc), false));
		}

		public static CPos RandomBuildingLocation(CNSquad squad)
		{
			// GetRandomBaseCenter uses the per-tick building cache — no redundant world scan here.
			return squad.SquadManager.GetRandomBaseCenter();
		}

		// --- Enemy finding ---

		/// <summary>
		/// True if at least one orderable unit in the squad has an active armament whose weapon
		/// can target the given actor's enabled target types. Used to skip targets the squad
		/// physically cannot damage (e.g. ground-only squad picking an aircraft as closest enemy).
		/// </summary>
		public static bool CanSquadEngage(CNSquad squad, Actor target)
		{
			if (squad == null || target == null || target.IsDead || !target.IsInWorld)
				return false;

			var targetTypes = target.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty)
				return false;

			foreach (var unit in squad.OrderableUnits)
			{
				if (unit == null || unit.IsDead || !unit.IsInWorld)
					continue;
				if (UnitHasWeaponFor(unit, targetTypes))
					return true;
			}

			return false;
		}

		static bool UnitHasWeaponFor(Actor unit, BitSet<TargetableType> targetTypes)
		{
			if (!unit.Info.HasTraitInfo<AttackBaseInfo>())
				return false;

			foreach (var arm in unit.TraitsImplementing<Armament>())
			{
				if (arm.IsTraitDisabled || arm.IsTraitPaused)
					continue;
				if (arm.Weapon.IsValidTarget(targetTypes))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Fraction (0..1) of the squad's orderable units that have a weapon valid against the
		/// given target's enabled target types. Used to bias target selection toward what the
		/// squad's actual composition is equipped to fight — e.g. a squad that is mostly
		/// anti-armor shouldn't keep picking infantry it can barely scratch when armor is nearby.
		/// </summary>
		public static double SquadEngageFraction(CNSquad squad, Actor target)
		{
			if (target == null || target.IsDead || !target.IsInWorld)
				return 0;

			var targetTypes = target.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty)
				return 0;

			var total = 0;
			var canHit = 0;
			foreach (var unit in squad.OrderableUnits)
			{
				if (unit == null || unit.IsDead || !unit.IsInWorld)
					continue;

				total++;
				if (UnitHasWeaponFor(unit, targetTypes))
					canHit++;
			}

			return total == 0 ? 0 : (double)canHit / total;
		}

		/// <summary>Closest enemy unit visible to the player and engageable by the squad (wide scan).</summary>
		public static Actor FindClosestEnemyUnit(CNSquad squad)
		{
			return squad.SquadManager.FindClosestEnemy(squad.CenterUnit(), a => CanSquadEngage(squad, a));
		}

		/// <summary>Closest enemy building engageable by the squad (no shroud check).</summary>
		public static Actor FindClosestEnemyBuilding(CNSquad squad)
		{
			var center = squad.CenterUnit();
			if (center == null)
				return null;
			return squad.SquadManager.FindClosestEnemyBuilding(center, a => CanSquadEngage(squad, a));
		}

		/// <summary>
		/// Closest enemy building not tagged Defense. Falls back to any enemy building.
		/// No shroud check — for infiltrators that navigate to known positions past defenses.
		/// </summary>
		public static Actor FindUnprotectedTarget(CNSquad squad)
		{
			var center = squad.CenterUnit();
			if (center == null)
				return null;

			var nonDefense = squad.World.ActorsHavingTrait<Building>()
				.Where(a => squad.SquadManager.IsLiveEnemyActor(a) &&
							!(a.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains("Defense") ?? false) &&
							CanSquadEngage(squad, a))
				.MinByOrDefault(a => (a.CenterPosition - center.CenterPosition).LengthSquared);

			return nonDefense ?? FindClosestEnemyBuilding(squad);
		}

		/// <summary>
		/// Scans for preferred targets by BotCapabilities tag in priority order (first match wins).
		/// Falls back to null if none found. Used by Raider, Stealth, Assault with PriorityTargetCapabilities set.
		/// </summary>
		public static Actor FindPriorityTarget(CNSquad squad, string[] priorityCaps, Actor sourceUnit)
		{
			if (sourceUnit == null || priorityCaps == null || priorityCaps.Length == 0)
				return null;

			var world = squad.World;
			var bestByPriority = new Actor[priorityCaps.Length];
			var bestDistanceByPriority = new long[priorityCaps.Length];

			for (var i = 0; i < bestDistanceByPriority.Length; i++)
				bestDistanceByPriority[i] = long.MaxValue;

			void CheckActor(Actor actor)
			{
				if (!squad.SquadManager.IsPreferredEnemyUnit(actor))
					return;
				if (!actor.CanBeViewedByPlayer(squad.Bot.Player))
					return;
				if (!CanSquadEngage(squad, actor))
					return;

				var caps = actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
				if (caps == null)
					return;

				for (var i = 0; i < priorityCaps.Length; i++)
				{
					if (!caps.Contains(priorityCaps[i]))
						continue;

					var distance = (actor.CenterPosition - sourceUnit.CenterPosition).LengthSquared;
					if (distance < bestDistanceByPriority[i])
					{
						bestDistanceByPriority[i] = distance;
						bestByPriority[i] = actor;
					}

					return;
				}
			}

			foreach (var actor in world.ActorsHavingTrait<Mobile>())
				CheckActor(actor);
			foreach (var actor in world.ActorsHavingTrait<Aircraft>())
				CheckActor(actor);
			foreach (var actor in world.ActorsHavingTrait<Building>())
				CheckActor(actor);

			for (var i = 0; i < bestByPriority.Length; i++)
				if (bestByPriority[i] != null)
					return bestByPriority[i];

			return null;
		}

		/// <summary>
		/// Find the best attack target for a squad:
		/// 1. PriorityTargetCapabilities from template (if any)
		/// 2. Closest visible enemy unit
		/// 3. Closest enemy building (no shroud check).
		/// </summary>
		public static Actor FindTarget(CNSquad squad)
		{
			var center = squad.CenterUnit();
			if (center == null)
				return null;

			if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
			{
				var prio = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);
				if (prio != null)
					return prio;
			}

			return FindClosestEnemyUnit(squad) ?? FindClosestEnemyBuilding(squad);
		}
	}
}
