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
	/// Idle: scan for an active Assault, Rush, Protection, or Defense squad to attach to.
	/// </summary>
	sealed class SupportIdleState : CNStateBase, ICNState
	{
		int scanTicks;
		const int ScanInterval = 3;

		public void Activate(CNSquad squad) { scanTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (--scanTicks > 0)
				return;

			scanTicks = ScanInterval;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			var attachTarget = squad.SquadManager.Squads
				.Where(s => s != squad && s.IsOperational &&
							(s.Type == CNSquadType.Assault ||
							 s.Type == CNSquadType.Rush ||
							 s.Type == CNSquadType.Protection ||
							 s.Type == CNSquadType.Defense))
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
			if (IsUnderThreat(squad))
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
					.Where(t => t.Health != null && t.Health.DamageState > DamageState.Undamaged);

				var bestTarget = targets
					.Where(t => CanHeal(unit, t.Actor))
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

		/// <summary>
		/// True when any enemy with a weapon is within DangerScanRadius, regardless of own buildings.
		/// Unlike the base ShouldFlee, this does NOT exempt the area around own structures — support
		/// units must flee even during a base attack.
		/// </summary>
		static bool IsUnderThreat(CNSquad squad)
		{
			if (!squad.IsValid)
				return false;

			var dangerRadius = WDist.FromCells(squad.SquadManager.Info.DangerScanRadius);
			return squad.World
				.FindActorsInCircle(squad.CenterPosition(), dangerRadius)
				.Any(u => squad.SquadManager.IsPreferredEnemyUnit(u) && u.Info.HasTraitInfo<AttackBaseInfo>());
		}

		/// <summary>Returns true if the healer can target the given actor.</summary>
		static bool CanHeal(Actor unit, Actor target)
		{
			var isMedic = !unit.Info.HasTraitInfo<RepairsUnitsInfo>();
			var targetIsMechanical = target.Info.HasTraitInfo<MobileInfo>() &&
									 target.GetEnabledTargetTypes().Overlaps(new BitSet<TargetableType>("Vehicle", "Tank"));

			if (isMedic && targetIsMechanical)
				return false;

			var isMechanic = unit.Info.HasTraitInfo<RepairsUnitsInfo>();
			var targetIsInfantry = target.Info.HasTraitInfo<MobileInfo>() &&
								   target.GetEnabledTargetTypes().Overlaps(new BitSet<TargetableType>("Infantry"));

			if (isMechanic && targetIsInfantry)
				return false;

			return true;
		}
	}
}
