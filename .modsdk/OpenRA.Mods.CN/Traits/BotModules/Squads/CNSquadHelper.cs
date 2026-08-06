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
		// Target-scoring weights, in the same scale ScoreRushTarget uses: distance is
		// LengthSquared/65536, so one cell of separation is worth roughly 16 points at close
		// range. 150 therefore buys a few cells of detour for a target the squad fights better.
		const int EngageFractionBonus = 150;
		const int CounterFractionBonus = 150;

		// Penalty for a target other squads have already committed enough damage to kill. Worth roughly
		// twenty cells of detour, so a covered target loses to anything else within reach but still beats
		// having no target at all - a hard filter here would leave squads standing idle once the army
		// outnumbers the things worth shooting.
		const int OversubscribedPenalty = 6400;

		// Penalty for a target a markedly better-suited squad has already claimed. Half the
		// oversubscription penalty on purpose: leaving a target to a better answer is a preference,
		// while shooting something already dead on paper is waste.
		const int BetterServedPenalty = 3200;

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

		// --- Deterministic hashing ---

		/// <summary>
		/// Hash of a string that is identical on every machine and in every process.
		/// <para>
		/// string.GetHashCode is randomised per process in .NET Core, and the switch that used to
		/// disable that (UseRandomizedStringHashAlgorithm) was a .NET Framework option which no longer
		/// exists. Anything derived from it therefore differs between clients. That is fatal here:
		/// these seeds place squads, positions become movement orders, and every client simulates the
		/// bot itself - so two clients would issue different orders and the game desyncs.
		/// </para>
		/// FNV-1a, 32 bit. Chosen for being short and fully specified, not for hash quality; the seeds
		/// only need to spread squads apart, not resist collisions.
		/// </summary>
		public static int StableHash(string value)
		{
			if (string.IsNullOrEmpty(value))
				return 0;

			unchecked
			{
				var hash = 2166136261u;
				foreach (var c in value)
				{
					hash ^= c;
					hash *= 16777619u;
				}

				return (int)hash;
			}
		}

		// --- Attachment ---

		/// <summary>
		/// True if <paramref name="candidate"/> is a squad <paramref name="squad"/> may attach to.
		/// <para>
		/// Honours the template's AttachToRole when it is set. The idle states used to hardcode their
		/// candidate roles, which made the configured AttachToRole dead config: it was read only by the
		/// squad manager's initial seeding and then overwritten by whatever the state picked.
		/// </para>
		/// <paramref name="fallbackRoles"/> preserves each state's previous default for squads whose
		/// template leaves AttachToRole empty. Pass a cached array — this runs per candidate squad.
		/// </summary>
		public static bool IsAttachCandidate(CNSquad squad, CNSquad candidate, CNSquadType[] fallbackRoles)
		{
			if (candidate == null || candidate == squad)
				return false;

			var roles = squad.TemplateInfo?.AttachToRole;
			if (roles != null && roles.Length > 0)
				return roles.Contains(candidate.Type);

			return fallbackRoles.Contains(candidate.Type);
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

		/// <summary>True if this single unit has a weapon valid against the target's enabled target types.</summary>
		public static bool UnitCanHit(Actor unit, Actor target)
		{
			if (unit == null || unit.IsDead || !unit.IsInWorld || target == null || target.IsDead || !target.IsInWorld)
				return false;

			var targetTypes = target.GetEnabledTargetTypes();
			return !targetTypes.IsEmpty && UnitHasWeaponFor(unit, targetTypes);
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

		/// <summary>
		/// Fraction (0..1) of the squad's orderable units whose capabilities counter the target's.
		/// <para>
		/// SquadEngageFraction only answers "can we hit it at all" — a rocket soldier has a weapon
		/// valid against infantry and scores 1.0 there, however badly it actually performs. This
		/// answers "are we good against it" by reading the squad manager's NeedRules backwards:
		/// the rule <c>AntiArmor: EnemyCapabilities: Vehicle, Tank</c> declares that our AntiArmor
		/// units counter anything carrying Vehicle or Tank. Same table, opposite direction.
		/// </para>
		/// Returns 0 when either side declares no capabilities, so it can only ever add preference,
		/// never remove a target that the engage filter already accepted.
		/// </summary>
		public static double CounterFraction(CNSquad squad, Actor target)
		{
			if (target == null || target.IsDead || !target.IsInWorld)
				return 0;

			var targetCaps = target.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (targetCaps == null || targetCaps.Count == 0)
				return 0;

			var needRules = squad.SquadManager.Info.NeedRules;
			if (needRules == null || needRules.Count == 0)
				return 0;

			var total = 0;
			var counters = 0;
			foreach (var unit in squad.OrderableUnits)
			{
				if (unit == null || unit.IsDead || !unit.IsInWorld)
					continue;

				total++;

				var unitCaps = unit.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
				if (unitCaps == null)
					continue;

				if (UnitCountersTarget(unitCaps, targetCaps, needRules))
					counters++;
			}

			return total == 0 ? 0 : (double)counters / total;
		}

		static bool UnitCountersTarget(
			IReadOnlySet<string> unitCaps,
			IReadOnlySet<string> targetCaps,
			Dictionary<string, CNSquadNeedRuleInfo> needRules)
		{
			foreach (var cap in unitCaps)
			{
				if (!needRules.TryGetValue(cap, out var rule))
					continue;

				foreach (var counteredCap in rule.EnemyCapabilities)
					if (targetCaps.Contains(counteredCap))
						return true;
			}

			return false;
		}

		/// <summary>
		/// Score penalty for a target covered by enemy static defences, scaled by how badly they would
		/// hurt the unit doing the looking. Weighted in hundredths so a profile can tune how shy its
		/// squads are of fortified ground without touching the rest of the scoring.
		/// </summary>
		public static int DefenseThreatPenalty(CNSquad squad, Actor target, Actor reference)
		{
			var weight = squad.SquadManager.Info.DefenseThreatPenaltyPercent;
			if (weight <= 0 || reference == null)
				return 0;

			var threat = squad.SquadManager.GetDefenseThreatAt(target.CenterPosition, reference.Info);
			return threat <= 0 ? 0 : threat * weight / 100;
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
			var bestScoreByPriority = new int[priorityCaps.Length];

			for (var i = 0; i < bestScoreByPriority.Length; i++)
				bestScoreByPriority[i] = int.MaxValue;

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

					// Within a priority tier, distance used to be the only criterion. It now competes
					// with how much of the squad can engage the target at all, and how much of it
					// actually counters the target - so an anti-armor group walks past the infantry
					// standing slightly closer and takes the tank behind it.
					var counter = CounterFraction(squad, actor);
					var score = (int)((actor.CenterPosition - sourceUnit.CenterPosition).LengthSquared / 65536);
					score -= (int)(SquadEngageFraction(squad, actor) * EngageFractionBonus);
					score -= (int)(counter * CounterFractionBonus);
					if (squad.SquadManager.IsTargetOversubscribed(squad, actor))
						score += OversubscribedPenalty;
					if (squad.SquadManager.IsTargetBetterServed(squad, actor, counter))
						score += BetterServedPenalty;

					// What sits under a battery of guns is worth less than what does not. Without this a
					// squad that was driven off simply picked the same objective again on its next pass
					// and was ground down a few units at a time without ever landing a shot.
					score += DefenseThreatPenalty(squad, actor, sourceUnit);

					if (score < bestScoreByPriority[i])
					{
						bestScoreByPriority[i] = score;
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
