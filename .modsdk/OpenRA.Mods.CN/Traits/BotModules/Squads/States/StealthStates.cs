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
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	sealed class StealthIdleState : CNStateBase, ICNState
	{
		int rethinkTicks;
		const int RethinkInterval = 3;

		public void Activate(CNSquad squad) { rethinkTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (--rethinkTicks > 0 && squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthApproachState());
				return;
			}

			rethinkTicks = RethinkInterval;
			var center = squad.CenterUnit();
			if (center == null)
				return;

			Actor target = null;
			if (squad.PreferredTargetTypes != null && squad.PreferredTargetTypes.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetTypes, center);

			target ??= squad.SquadManager.FindClosestEnemy(center, WDist.FromCells(squad.SquadManager.Info.AttackScanRadius));

			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new StealthApproachState());
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class StealthApproachState : CNStateBase, ICNState
	{
		int lastActivityTick;
		CPos lastCenterPos;
		const int MaxStuckTicks = 120;

		public void Activate(CNSquad squad)
		{
			lastActivityTick = squad.World.WorldTick;
			lastCenterPos = squad.CenterUnit()?.Location ?? CPos.Zero;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (!squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
				return;
			}

			var anyRevealed = squad.OrderableUnits.Any(u =>
			{
				return u.TraitsImplementing<Cloak>().Any(c => !c.IsTraitDisabled && !c.Cloaked);
			});

			if (anyRevealed || ShouldFlee(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthFleeState());
				return;
			}

			var currentPos = squad.CenterUnit()?.Location ?? CPos.Zero;
			var anyAttacking = squad.Units.Any(u => !u.IsDead && !u.IsIdle);

			if (currentPos != lastCenterPos || anyAttacking)
			{
				lastActivityTick = squad.World.WorldTick;
				lastCenterPos = currentPos;
			}

			if (squad.World.WorldTick > lastActivityTick + MaxStuckTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
				return;
			}

			foreach (var unit in squad.OrderableUnits)
				if (unit.IsIdle)
					squad.Bot.QueueOrder(new Order("Attack", unit, squad.Target, false));
		}

		public void Deactivate(CNSquad squad) { }

		protected override bool ShouldFlee(CNSquad squad)
		{
			return ShouldFlee(squad, enemies =>
				!CNAttackOrFleeFuzzy.Raider.CanAttack(squad.Units, enemies));
		}
	}

	sealed class StealthFleeState : CNStateBase, ICNState
	{
		int fleeStartTick;
		const int RecloakWaitTicks = 150;

		public void Activate(CNSquad squad)
		{
			fleeStartTick = squad.World.WorldTick;
			GoToRandomOwnBuilding(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (squad.World.WorldTick > fleeStartTick + RecloakWaitTicks)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
			}
		}

		public void Deactivate(CNSquad squad) { }
	}
}
