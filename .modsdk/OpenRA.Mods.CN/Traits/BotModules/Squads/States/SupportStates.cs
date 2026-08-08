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

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	/// <summary>
	/// Idle: scan for an active squad to attach to — those named by the template's AttachToRole,
	/// or Assault/Rush/Protection when the template does not configure it.
	/// </summary>
	sealed class SupportIdleState : CNStateBase, ICNState
	{
		// Game ticks, not update cycles.
		const int ScanInterval = 225;

		// Used only when the template does not configure AttachToRole.
		static readonly CNSquadType[] DefaultAttachRoles =
			[CNSquadType.Assault, CNSquadType.Rush, CNSquadType.Protection];
		int scanTicks;
		int idleTicks;

		public void Activate(CNSquad squad)
		{
			scanTicks = 0;
			idleTicks = 0;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (squad.TemplateInfo?.StayInBase == true)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SupportGuardState());
				return;
			}

			idleTicks += squad.TicksSinceLastUpdate;
			if (idleTicks >= squad.SquadManager.Info.MaxIdleScanTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SupportGuardState());
				return;
			}

			scanTicks -= squad.TicksSinceLastUpdate;
			if (scanTicks > 0)
				return;

			scanTicks = ScanInterval;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			var attachTarget = squad.SquadManager.Squads
				.Where(s => s.IsOperational && CNSquadHelper.IsAttachCandidate(squad, s, DefaultAttachRoles))
				.OrderBy(s =>
				{
					var sCenter = s.CenterUnit();
					return sCenter != null ? (sCenter.CenterPosition - center.CenterPosition).LengthSquared : long.MaxValue;
				})
				.FirstOrDefault();

			if (attachTarget != null)
			{
				squad.AttachedTo = attachTarget;
				squad.FuzzyStateMachine.ChangeState(squad, new SupportFollowState());
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Follow: stay near the attached squad and heal/repair allies. Flee when enemies are in danger range.
	/// </summary>
	sealed class SupportFollowState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			// Flee from any nearby attacker — support units never fight back.
			if (SupportStateHelpers.IsUnderThreat(squad))
			{
				Retreat(squad, flee: true, rearm: false, repair: false);
				squad.AttachedTo = null;
				squad.FuzzyStateMachine.ChangeState(squad, new SupportIdleState());
				return;
			}

			if (squad.AttachedTo == null || !squad.AttachedTo.IsValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SupportIdleState());
				return;
			}

			var followCenter = squad.AttachedTo.CenterUnit();
			if (followCenter == null)
				return;

			var followRange = WDist.FromCells(squad.SquadManager.Info.SupportFollowRangeCells);

			foreach (var unit in squad.OrderableUnits)
			{
				var dist = (unit.CenterPosition - followCenter.CenterPosition).Length;

				if (!unit.IsIdle)
					continue;

				// Heal/repair nearby allies — lowest HP first, then by cost
				var targets = squad.World
					.FindActorsInCircle(unit.CenterPosition, WDist.FromCells(6))
					.Where(a => !a.IsDead && a.IsInWorld && a.Owner == unit.Owner && a != unit)
					.Select(a => new { Actor = a, Health = a.TraitOrDefault<IHealth>() })
					.Where(t => t.Health != null && t.Health.DamageState > DamageState.Undamaged
						&& !SupportStateHelpers.IsMissingSquadMembers(t.Actor));

				var bestTarget = targets
					.Where(t => SupportStateHelpers.CanHeal(unit, t.Actor))
					.OrderBy(t => (float)t.Health.HP / t.Health.MaxHP)
					.ThenByDescending(t => t.Actor.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0)
					.Select(t => t.Actor)
					.FirstOrDefault();

				if (bestTarget != null)
				{
					if (unit.Info.HasTraitInfo<RepairsUnitsInfo>())
						squad.Bot.QueueOrder(new Order("Repair", unit, Target.FromActor(bestTarget), false));
					else
						squad.Bot.QueueOrder(new Order("Heal", unit, Target.FromActor(bestTarget), false));
				}
				else if (dist > followRange.Length)
				{
					squad.Bot.QueueOrder(new Order("Move", unit, Target.FromActor(followCenter), false));
				}
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class SupportGuardState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (SupportStateHelpers.IsUnderThreat(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new SupportFleeState());
				return;
			}

			var baseCenter = SupportStateHelpers.FindBaseCenter(squad);
			if (baseCenter == null)
				return;

			var garrisonRadius = WDist.FromCells(squad.SquadManager.Info.BaseGarrisonRadius);
			foreach (var unit in squad.OrderableUnits)
			{
				if (!unit.IsIdle)
					continue;

				var distanceFromBase = (unit.CenterPosition - baseCenter.Value).Length;
				if (distanceFromBase > garrisonRadius.Length)
				{
					squad.Bot.QueueOrder(new Order("Move", unit,
						Target.FromCell(squad.World, squad.World.Map.CellContaining(baseCenter.Value)), false));
					continue;
				}

				var bestTarget = squad.World
					.FindActorsInCircle(unit.CenterPosition, WDist.FromCells(6))
					.Where(a => !a.IsDead && a.IsInWorld && a.Owner == unit.Owner && a != unit)
					.Select(a => new { Actor = a, Health = a.TraitOrDefault<IHealth>() })
					.Where(t => t.Health != null && t.Health.DamageState > DamageState.Undamaged
						&& !SupportStateHelpers.IsMissingSquadMembers(t.Actor)
						&& SupportStateHelpers.CanHeal(unit, t.Actor))
					.OrderBy(t => (float)t.Health.HP / t.Health.MaxHP)
					.ThenByDescending(t => t.Actor.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0)
					.Select(t => t.Actor)
					.FirstOrDefault();

				if (bestTarget != null)
				{
					if (unit.Info.HasTraitInfo<RepairsUnitsInfo>())
						squad.Bot.QueueOrder(new Order("Repair", unit, Target.FromActor(bestTarget), false));
					else
						squad.Bot.QueueOrder(new Order("Heal", unit, Target.FromActor(bestTarget), false));
				}
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class SupportFleeState : CNStateBase, ICNState
	{
		// Game ticks, not update cycles.
		const int FleeDuration = 6750;
		int fleeTicks;

		public void Activate(CNSquad squad)
		{
			fleeTicks = FleeDuration;
			Retreat(squad, flee: true, rearm: false, repair: false);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			fleeTicks -= squad.TicksSinceLastUpdate;
			if (fleeTicks <= 0 || !SupportStateHelpers.IsUnderThreat(squad))
				squad.FuzzyStateMachine.ChangeState(squad, new SupportGuardState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	static class SupportStateHelpers
	{
		/// <summary>
		/// True for a mob squad that has lost members. Such a squad is permanently "damaged" from a
		/// medic's point of view and cannot be finished: MobSpawnerMaster ties aggregate health to the
		/// member count ("slaves die at evenly-spaced HP thresholds"), and RefreshMasterHP keeps
		/// rewriting the master's HP to match. Members only come back via RestoresInfantrySquads,
		/// which requires reaching full health AT A HOST — never in the field. A medic therefore heals
		/// such a squad forever without ever clearing its damage state.
		/// These belong in a barracks, where CNRepairManagerBotModule already routes damaged
		/// RepairableInBarracks infantry.
		/// </summary>
		public static bool IsMissingSquadMembers(Actor actor)
		{
			// Ask the master when handed a member. A member carries no MobSpawnerMaster trait of its
			// own — the squad rules strip it explicitly — so it used to sail straight through this
			// filter, and medics went on healing the individual soldiers of a depleted squad instead
			// of the master. That is just as futile: AggregateHealth is on by default and recomputes
			// the group's health from the member count every AggregateHealthUpdateDelay ticks,
			// overwriting whatever the medic just restored.
			var slave = actor.TraitOrDefault<MobSpawnerSlave>();
			var master = slave?.Master;
			if (master != null && master != actor && !master.IsDead && master.IsInWorld)
				return IsMissingSquadMembers(master);

			var mob = actor.TraitOrDefault<MobSpawnerMaster>();
			if (mob == null)
				return false;

			var info = actor.Info.TraitInfoOrDefault<MobSpawnerMasterInfo>();
			if (info == null)
				return false;

			// InitialActorCount defaults to -1, meaning "one of every entry in Actors".
			var initial = info.InitialActorCount > 0 ? info.InitialActorCount : info.Actors.Length;
			return mob.AliveSlavesInWorld.Count() < initial;
		}

		public static WPos? FindBaseCenter(CNSquad squad)
		{
			var center = squad.CenterUnit();
			var buildings = squad.SquadManager.GetCachedOwnBuildings()
				.Where(a => IsBaseAnchor(a))
				.ToArray();

			if (buildings.Length > 0)
				return buildings
					.OrderBy(a => center != null ? (a.CenterPosition - center.CenterPosition).LengthSquared : 0)
					.First()
					.CenterPosition;

			if (squad.SquadManager.GetCachedOwnBuildings().Count == 0)
				return center?.CenterPosition;

			return squad.World.Map.CenterOfCell(squad.SquadManager.GetRandomBaseCenter());
		}

		static bool IsBaseAnchor(Actor actor)
		{
			if (actor.Info.Name == "gacnst" || actor.Info.Name == "nacnst" ||
				actor.Info.Name == "gproc" || actor.Info.Name == "nproc")
				return true;

			return actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains("Economy") ?? false;
		}

		/// <summary>
		/// True when any enemy with a weapon is within DangerScanRadius, regardless of own buildings.
		/// Unlike the base ShouldFlee, this does NOT exempt the area around own structures.
		/// </summary>
		public static bool IsUnderThreat(CNSquad squad)
		{
			if (!squad.IsValid)
				return false;

			var dangerRadius = WDist.FromCells(squad.SquadManager.Info.DangerScanRadius);
			return squad.World
				.FindActorsInCircle(squad.CenterPosition(), dangerRadius)
				.Any(u => squad.SquadManager.IsPreferredEnemyUnit(u) && u.Info.HasTraitInfo<AttackBaseInfo>());
		}

		/// <summary>Returns true if the healer can target the given actor.</summary>
		public static bool CanHeal(Actor unit, Actor target)
		{
			var isMechanic = unit.Info.HasTraitInfo<RepairsUnitsInfo>();
			var targetIsBuilding = target.Info.HasTraitInfo<BuildingInfo>();
			if (targetIsBuilding)
				return isMechanic;

			var isMedic = !isMechanic;
			var targetIsMechanical = target.Info.HasTraitInfo<MobileInfo>() &&
									 target.GetEnabledTargetTypes().Overlaps(new BitSet<TargetableType>("Vehicle", "Tank"));

			if (isMedic && targetIsMechanical)
				return false;

			var targetIsInfantry = target.Info.HasTraitInfo<MobileInfo>() &&
								   target.GetEnabledTargetTypes().Overlaps(new BitSet<TargetableType>("Infantry"));

			if (isMechanic && targetIsInfantry)
				return false;

			return true;
		}
	}
}
