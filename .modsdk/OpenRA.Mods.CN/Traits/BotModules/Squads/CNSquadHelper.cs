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
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.Common.Traits;
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

		/// <summary>Closest enemy unit visible to the player (wide scan).</summary>
		public static Actor FindClosestEnemyUnit(CNSquad squad)
		{
			return squad.SquadManager.FindClosestEnemy(squad.CenterUnit());
		}

		/// <summary>Closest enemy building (no shroud check — bots know building locations).</summary>
		public static Actor FindClosestEnemyBuilding(CNSquad squad)
		{
			var center = squad.CenterUnit();
			if (center == null)
				return null;
			return squad.SquadManager.FindClosestEnemyBuilding(center);
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
				            !(a.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains("Defense") ?? false))
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
		/// 3. Closest enemy building (no shroud check)
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
