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
	/// Idle: wait for an Assault/Rush squad to attach to, or find an enemy building.
	/// For ArtilleryDefense: just hold near base and scan for threats.
	/// </summary>
	sealed class ArtilleryIdleState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			if (squad.Type == CNSquadType.ArtilleryAssault)
			{
				// Find the squad we should attach to
				if (squad.AttachedTo == null || !squad.AttachedTo.IsValid)
				{
					squad.AttachedTo = squad.SquadManager.Squads
						.FirstOrDefault(s => s.IsValid &&
							(s.Type == CNSquadType.Assault || s.Type == CNSquadType.Rush));
				}

				if (squad.AttachedTo != null)
					squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryHangBackState());
			}
			else
			{
				// ArtilleryDefense: look for nearby threats
				var center = squad.CenterUnit();
				if (center == null)
					return;

				var threat = squad.SquadManager.FindClosestEnemy(center,
					WDist.FromCells(squad.SquadManager.Info.DangerScanRadius));

				if (threat != null)
				{
					squad.SetActorToTarget(threat);
					squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryBombardState());
				}
				else
				{
					// Hold near base
					GoToRandomOwnBuilding(squad);
				}
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// HangBack: follow the attached Assault squad, staying HangBackRange behind the leader.
	/// Switches to Bombard when enemies are within weapon range.
	/// </summary>
	sealed class ArtilleryHangBackState : CNStateBase, ICNState
	{
		public void Activate(CNSquad squad) { }

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
			foreach (var unit in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Move", unit,
					Target.FromCell(squad.World, hangBackCell), false));
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// Bombard: fire on enemies within range. Do not move unless target is lost or we need to flee.
	/// </summary>
	sealed class ArtilleryBombardState : CNStateBase, ICNState
	{
		int staleTicks;
		const int MaxStaleTicks = 5;

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

					// No target found (or no center unit) — go back to hang-back or idle
					if (squad.Type == CNSquadType.ArtilleryAssault)
						squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryHangBackState());
					else
						squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryIdleState());
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
