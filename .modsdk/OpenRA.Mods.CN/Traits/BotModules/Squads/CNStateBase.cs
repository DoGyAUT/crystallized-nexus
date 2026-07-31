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
		/// True when a carrier's cargo hold is genuinely full: the weight actually aboard leaves no
		/// room for another unit.
		/// <para>
		/// Two things this must NOT do. It must not use Cargo.HasSpace(), because that also counts
		/// reservedWeight, which the engine reserves the moment a passenger *starts* its Enter
		/// activity — a ground transport orders its infantry to board from across the base, so every
		/// seat is reserved while they are still walking and the transport departs nearly empty.
		/// </para>
		/// <para>
		/// And it must not count passengers, because CN infantry squads are MobSpawnerMasters whose
		/// slaves carry Passenger.Weight 0 (see ^MobSquadMember). One gasol squad is a single unit of
		/// weight but four bodies in the hold, so PassengerCount against MaxWeight declared an APC
		/// full after two squads when it can carry five.
		/// </para>
		/// </summary>
		protected static bool IsCarrierFull(Cargo cargo)
		{
			if (cargo == null)
				return true;

			var usedWeight = 0;
			foreach (var passenger in cargo.Passengers)
				usedWeight += passenger.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1;

			return cargo.Info.MaxWeight - usedWeight < 1;
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

		protected const int KillSecureRadiusCells = 6;

		/// <summary>
		/// Nearest critically-damaged enemy the squad can engage within a short radius. A near-dead
		/// unit contributes nothing further to the fight it's already losing, so finishing it off
		/// before continuing the main push denies the enemy a free reprieve and secures the kill.
		/// </summary>
		protected static Actor FindKillSecureTarget(CNSquad squad, WPos center)
		{
			return squad.World.FindActorsInCircle(center, WDist.FromCells(KillSecureRadiusCells))
				.Where(a => squad.SquadManager.IsPreferredEnemyUnit(a) &&
					a.Info.HasTraitInfo<AttackBaseInfo>() &&
					(a.TraitOrDefault<IHealth>()?.DamageState ?? DamageState.Undamaged) >= DamageState.Critical &&
					CNSquadHelper.CanSquadEngage(squad, a))
				.MinByOrDefault(a => (a.CenterPosition - center).LengthSquared);
		}

		/// <summary>
		/// Nearest "useful" chokepoint (bridge/ramp/passage the bot's own topology scan considers
		/// reachable and non-dead-end) that the squad isn't already sitting on. Raid-style squads
		/// (Raider, Stealth) that found no target this pass reposition here instead of sitting still,
		/// increasing the chance of catching enemy traffic on a later scan. Returns null if the
		/// tactical map module isn't attached or has no useful chokepoints for this base.
		/// </summary>
		protected static CPos? FindAmbushChokepoint(CNSquad squad, Actor center)
		{
			if (center == null)
				return null;

			var tacticalMap = squad.SquadManager.GetTacticalMap();
			if (tacticalMap == null)
				return null;

			var chokepoints = tacticalMap.GetUsefulChokepointsForOwnBase();
			if (chokepoints.Count == 0)
				return null;

			CPos? best = null;
			var bestDistSq = long.MaxValue;
			foreach (var chokepoint in chokepoints)
			{
				var distSq = (chokepoint.Cell - center.Location).LengthSquared;
				if (distSq <= 16 || distSq >= bestDistSq)
					continue;

				bestDistSq = distSq;
				best = chokepoint.Cell;
			}

			return best;
		}

		// --- Retreat cell search (shared by Raider/Stealth flee states) ---

		/// <summary>
		/// Finds the best cell in a ring around the squad leader to retreat to: far from threats,
		/// close to the target/base. Scans a ring between minRetreatCells and maxRetreatCells.
		/// </summary>
		protected static CPos? FindRetreatCell(CNSquad squad, int minRetreatCells, int maxRetreatCells)
		{
			var leader = squad.CenterUnit();
			if (leader == null)
				return null;

			var mobile = leader.TraitOrDefault<Mobile>();
			if (mobile == null)
				return null;

			var map = squad.World.Map;
			var origin = leader.Location;
			var baseCell = squad.SquadManager.GetRandomBaseCenter();
			var dangerRadiusCells = Math.Max(0, squad.SquadManager.Info.DangerScanRadius) + 4;

			// One scan covering every candidate's detection radius, instead of a separate
			// FindActorsInCircle call per candidate cell (up to ~800+ candidates in the ring).
			var scanRadius = WDist.FromCells(maxRetreatCells + dangerRadiusCells);
			var threats = squad.World.FindActorsInCircle(leader.CenterPosition, scanRadius)
				.Where(a => squad.SquadManager.IsPreferredEnemyUnit(a) && a.Info.HasTraitInfo<AttackBaseInfo>())
				.Select(a => (Pos: a.CenterPosition, Cell: a.Location))
				.ToList();

			var perCandidateRangeSq = (long)WDist.FromCells(dangerRadiusCells).Length * WDist.FromCells(dangerRadiusCells).Length;

			CPos? bestCell = null;
			var bestScore = int.MinValue;

			for (var dy = -maxRetreatCells; dy <= maxRetreatCells; dy++)
			{
				for (var dx = -maxRetreatCells; dx <= maxRetreatCells; dx++)
				{
					var distanceSquared = dx * dx + dy * dy;
					if (distanceSquared < minRetreatCells * minRetreatCells ||
						distanceSquared > maxRetreatCells * maxRetreatCells)
						continue;

					var candidate = origin + new CVec(dx, dy);
					if (!map.Contains(candidate) || !mobile.CanEnterCell(candidate))
						continue;

					var score = ScoreRetreatCell(squad, candidate, baseCell, threats, perCandidateRangeSq);
					if (score <= bestScore)
						continue;

					bestScore = score;
					bestCell = candidate;
				}
			}

			return bestCell;
		}

		static int ScoreRetreatCell(
			CNSquad squad, CPos candidate, CPos baseCell,
			List<(WPos Pos, CPos Cell)> threats, long perCandidateRangeSq)
		{
			var candidatePos = squad.World.Map.CenterOfCell(candidate);
			var closestEnemyDistance = int.MaxValue;
			var closestThreatCell = CPos.Zero;
			var foundThreat = false;

			foreach (var (pos, cell) in threats)
			{
				var distance = (pos - candidatePos).LengthSquared;
				if (distance > perCandidateRangeSq)
					continue;

				if (distance < closestEnemyDistance)
				{
					closestEnemyDistance = (int)distance;
					closestThreatCell = cell;
					foundThreat = true;
				}
			}

			var score = closestEnemyDistance == int.MaxValue ? 1000000 : closestEnemyDistance;
			if (foundThreat)
				score += (candidate - closestThreatCell).LengthSquared * 20;
			if (squad.IsTargetValid)
				score -= (candidate - squad.TargetActor.Location).LengthSquared * 2;
			else
				score -= (candidate - baseCell).LengthSquared * 2;

			return score;
		}

		// --- Flee decision ---
		protected virtual bool ShouldFlee(CNSquad squad)
		{
			return ShouldFlee(squad, (friendlies, enemies) =>
				!CNAttackOrFleeFuzzy.Default.CanAttack(friendlies, enemies, squad.SquadManager.GetAttackFuzzyBoost()));
		}

		/// <summary>
		/// Evaluates the flee decision, handing the callback the friendly force present for this fight
		/// and the enemies threatening it. The callback used to receive only the enemies and weighed
		/// squad.Units against them, so a squad fighting shoulder to shoulder with two others judged
		/// the engagement as if it stood alone — all three concluded they were outnumbered and withdrew
		/// from a fight the combined force was winning.
		/// </summary>
		protected static bool ShouldFlee(CNSquad squad, Func<IReadOnlyCollection<Actor>, IReadOnlyCollection<Actor>, bool> flee)
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

			return flee(FriendlyForce(squad, units), enemies);
		}

		/// <summary>
		/// The friendly fighting force actually present: the squad's own units plus every other
		/// friendly combat unit already found inside the danger radius, so this costs no extra scan.
		/// <para>
		/// Own units only count when they belong to a squad — loose stragglers, harvesters and
		/// half-built forces are not a fighting force and must not talk the squad into standing its
		/// ground. Allied players are counted too, but their squad membership is unknowable from here,
		/// so mobile armed units stand in for it; that also keeps allied defensive structures out,
		/// which would inflate the firepower of a force that cannot follow the fight.
		/// </para>
		/// </summary>
		static IReadOnlyCollection<Actor> FriendlyForce(CNSquad squad, List<Actor> unitsInDangerRadius)
		{
			// Start from the whole squad so a strung-out squad is never rated weaker than before.
			var force = new HashSet<Actor>();
			foreach (var unit in squad.Units)
				if (unit != null && !unit.IsDead && unit.IsInWorld)
					force.Add(unit);

			var player = squad.Bot.Player;
			foreach (var unit in unitsInDangerRadius)
			{
				if (unit.IsDead || !unit.IsInWorld || !unit.Info.HasTraitInfo<AttackBaseInfo>())
					continue;

				if (unit.Owner == player)
				{
					if (squad.SquadManager.IsUnitAssignedToSquad(unit))
						force.Add(unit);
				}
				else if (unit.Owner.RelationshipWith(player) == PlayerRelationship.Ally &&
					unit.Info.HasTraitInfo<MobileInfo>())
					force.Add(unit);
			}

			return force;
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
