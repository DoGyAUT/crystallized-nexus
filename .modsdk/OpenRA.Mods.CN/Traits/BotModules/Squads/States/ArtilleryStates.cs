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
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	/// <summary>
	/// Idle: wait for an Assault/Rush squad to attach to.
	/// </summary>
	sealed class ArtilleryIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Find the squad we should attach to.
			if (squad.AttachedTo == null || !squad.AttachedTo.IsValid)
			{
				var attachable = squad.SquadManager.Squads
					.Where(s => s.IsValid &&
						(s.Type == CNSquadType.Assault || s.Type == CNSquadType.Rush))
					.ToArray();

				squad.AttachedTo = attachable.FirstOrDefault(s => s.IsTargetValid) ??
					attachable.FirstOrDefault();
			}

			if (squad.AttachedTo != null)
				squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryHangBackState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// HangBack: follow the attached Assault squad, staying HangBackRange behind the leader.
	/// Switches to Bombard when enemies are within weapon range.
	/// </summary>
	sealed class ArtilleryHangBackState : CNStateBase, ICNState
	{
		CPos? lastHangBackCell;

		public void Activate(CNSquad squad) { lastHangBackCell = null; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Lost our attached squad
			if (squad.AttachedTo == null || !squad.AttachedTo.IsValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryIdleState());
				return;
			}

			var attachedLeader = squad.AttachedTo.CenterUnit();
			if (attachedLeader == null)
				return;

			var coordinatedTarget = FindCoordinatedTarget(squad);
			if (coordinatedTarget != null)
			{
				squad.SetActorToTarget(coordinatedTarget);
				squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryBombardState());
				return;
			}

			// Check if any enemy is within our attack scan radius
			var target = squad.SquadManager.FindClosestEnemy(
				squad.CenterUnit() ?? attachedLeader,
				WDist.FromCells(squad.SquadManager.Info.AttackScanRadius));

			if (target != null)
			{
				squad.SetActorToTarget(target);
				squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryBombardState());
				return;
			}

			// Calculate hang-back position: behind the attached squad leader
			var hangBackOffset = squad.ArtilleryHangBackRange;
			var directionToEnemy = WVec.Zero;
			if (squad.AttachedTo.IsTargetValid && squad.AttachedTo.Target.Type != TargetType.Invalid)
			{
				try
				{
					directionToEnemy = squad.AttachedTo.Target.CenterPosition - attachedLeader.CenterPosition;
				}
				catch (InvalidOperationException)
				{
					directionToEnemy = WVec.Zero;
				}
			}

			WPos hangBackPos;
			if (directionToEnemy != WVec.Zero)
			{
				// Move to a position behind the attached squad
				var normalized = directionToEnemy * 1024 / directionToEnemy.Length;
				var offset = new WVec(-normalized.X, -normalized.Y, 0) *
					hangBackOffset.Length / 1024;
				hangBackPos = attachedLeader.CenterPosition + offset;
			}
			else
			{
				hangBackPos = attachedLeader.CenterPosition;
			}

			var hangBackCell = squad.World.Map.CellContaining(hangBackPos);
			if (hangBackCell == lastHangBackCell)
				return;

			lastHangBackCell = hangBackCell;
			foreach (var unit in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("AttackMove", unit,
					Target.FromCell(squad.World, hangBackCell), false));
		}

		static Actor FindCoordinatedTarget(CNSquad squad)
		{
			var attached = squad.AttachedTo;
			if (attached == null || !attached.IsTargetValid)
				return null;

			var target = attached.TargetActor;
			if (target != null && attached.SquadManager.IsLiveEnemyActor(target) && IsDefenseStructure(target))
				return target;

			return FindDefenseNearTarget(squad, attached.TargetActor, squad.SquadManager.Info.AttackScanRadius);
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Bombard: fire on enemies within range. Do not move unless target is lost or we need to flee.
	/// </summary>
	sealed class ArtilleryBombardState : CNStateBase, ICNState
	{
		const int MaxStaleTicks = 5;
		int staleTicks;

		public void Activate(CNSquad squad) { staleTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Revalidate target
			if (!squad.IsTargetValid)
			{
				staleTicks++;
				if (staleTicks > MaxStaleTicks)
				{
					// Try to find a new target in range
					var center = squad.CenterUnit();
					if (center != null)
					{
						var newTarget = squad.SquadManager.FindClosestEnemy(center,
							WDist.FromCells(squad.SquadManager.Info.AttackScanRadius));
						if (newTarget != null)
						{
							squad.SetActorToTarget(newTarget);
							staleTicks = 0;
							return;
						}
					}

					// No target found (or no center unit) — go back to hang-back.
					squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryHangBackState());
					return;
				}

				return;
			}

			staleTicks = 0;

			// Issue attack orders only to units not already attacking
			foreach (var unit in squad.OrderableUnits)
				if (!BusyAttack(unit))
					squad.Bot.QueueOrder(new Order("AttackMove", unit, squad.Target, false));

			// Flee check (only if we're too exposed)
			if (ShouldFlee(squad))
				squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryFleeState());
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Flee: retreat to base. Artillery squads don't dissolve — they reform and return to idle.
	/// </summary>
	sealed class ArtilleryFleeState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			GoToRandomOwnBuilding(squad);
			squad.AttachedTo = null;
			squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryIdleState());
		}

		public void Deactivate(CNSquad squad) { }
	}
}
