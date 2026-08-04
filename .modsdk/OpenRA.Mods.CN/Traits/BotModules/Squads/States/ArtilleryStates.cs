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
using OpenRA.Mods.Common.Traits;
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

			// Find the squad we should attach to. Spread artillery squads out instead of stacking
			// them all behind whichever assault squad happens to be first in the list: prefer one
			// nobody has claimed yet, and among equals the nearest, so each push gets its own
			// fire support instead of one squad towing the entire artillery park.
			if (squad.AttachedTo == null || !squad.AttachedTo.IsValid)
			{
				var taken = squad.SquadManager.Squads
					.Where(s => s != squad && s.AttachedTo != null)
					.Select(s => s.AttachedTo)
					.ToHashSet();

				var attachable = squad.SquadManager.Squads
					.Where(s => s.IsValid &&
						(s.Type == CNSquadType.Assault || s.Type == CNSquadType.Rush))
					.ToList();

				squad.AttachedTo =
					PickNearest(squad, attachable.Where(s => !taken.Contains(s) && s.IsTargetValid)) ??
					PickNearest(squad, attachable.Where(s => !taken.Contains(s))) ??
					PickNearest(squad, attachable.Where(s => s.IsTargetValid)) ??
					PickNearest(squad, attachable);
			}

			if (squad.AttachedTo != null)
				squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryHangBackState());
		}

		static CNSquad PickNearest(CNSquad squad, IEnumerable<CNSquad> candidates)
		{
			var origin = squad.CenterPosition();
			return candidates.MinByOrDefault(s => (s.CenterPosition() - origin).LengthSquared);
		}

		public void Deactivate(CNSquad squad) { }
	}

	/// <summary>
	/// HangBack: follow the attached Assault squad, staying HangBackRange behind the leader.
	/// Switches to Bombard when enemies are within weapon range.
	/// </summary>
	sealed class ArtilleryHangBackState : CNStateBase, ICNState
	{
		const int HangBackSearchRadius = 4;

		// Even when the hang-back cell hasn't moved, the order is refreshed on this interval.
		// Suppressing the reissue purely on "same cell" stranded any unit whose order had been
		// overridden in the meantime (retreat, repair run): it never received the move again and
		// sat where it stopped while the rest of the battery followed the assault.
		const int ReissueIntervalTicks = 150;

		CPos? lastHangBackCell;
		int lastOrderTick;

		public void Activate(CNSquad squad)
		{
			lastHangBackCell = null;
			lastOrderTick = squad.World.WorldTick - ReissueIntervalTicks;
		}

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

			var anchorCell = squad.World.Map.CellContaining(hangBackPos);
			var hangBackCell = FindHangBackCell(squad, anchorCell);
			if (hangBackCell == lastHangBackCell &&
				squad.World.WorldTick - lastOrderTick < ReissueIntervalTicks)
				return;

			lastHangBackCell = hangBackCell;
			lastOrderTick = squad.World.WorldTick;
			foreach (var unit in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("AttackMove", unit,
					Target.FromCell(squad.World, hangBackCell), false));
		}

		// Refines the raw vector-offset anchor: must be enterable terrain, and among enterable
		// candidates nearby, prefers higher ground (better sightline/range) while staying close
		// to the intended hang-back position. Falls back to the anchor itself if nothing better
		// is enterable (e.g. locomotor unavailable, or the anchor is already the best option).
		static CPos FindHangBackCell(CNSquad squad, CPos anchor)
		{
			var map = squad.World.Map;
			if (!map.Contains(anchor))
				return anchor;

			var mobile = squad.OrderableUnits.FirstOrDefault(u => !u.IsDead && u.IsInWorld)?.TraitOrDefault<Mobile>();
			if (mobile == null)
				return anchor;

			CPos? best = null;
			var bestScore = int.MinValue;

			for (var dy = -HangBackSearchRadius; dy <= HangBackSearchRadius; dy++)
			{
				for (var dx = -HangBackSearchRadius; dx <= HangBackSearchRadius; dx++)
				{
					var cell = anchor + new CVec(dx, dy);
					if (!map.Contains(cell) || !mobile.CanEnterCell(cell))
						continue;

					var score = map.Height[cell] * 40 - (dx * dx + dy * dy);
					if (score <= bestScore)
						continue;

					bestScore = score;
					best = cell;
				}
			}

			return best ?? anchor;
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
		// Game ticks, not update cycles: how long the battery tolerates having no valid target
		// before it goes looking for a new one.
		const int MaxStaleTicks = 375;
		int staleTicks;

		public void Activate(CNSquad squad) { staleTicks = 0; }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			// Revalidate target
			if (!squad.IsTargetValid)
			{
				staleTicks += squad.TicksSinceLastUpdate;
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

			// Kill-securing: finish off a critically damaged enemy in easy reach before
			// continuing to bombard the main target — a near-dead unit contributes nothing
			// further, so the free kill denies the enemy that unit's cost.
			var bombardCenter = squad.CenterUnit();
			if (bombardCenter != null)
			{
				var killSecureTarget = FindKillSecureTarget(squad, bombardCenter.CenterPosition);
				if (killSecureTarget != null && killSecureTarget != squad.TargetActor)
					squad.SetActorToTarget(killSecureTarget);
			}

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
