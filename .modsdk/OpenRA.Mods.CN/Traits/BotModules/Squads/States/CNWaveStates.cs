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
	// ---------------------------------------------------------------------------
	// Wave hold: squad parks near a stable position in own base and waits for
	// the squad manager to launch the next coordinated wave. While holding, it
	// only reacts to enemies that come into base-defense range — it does not
	// chase, never breaks formation, and never wanders to a random building
	// every tick (anti-jitter). On wave launch it transitions to MoveToRally.
	// ---------------------------------------------------------------------------

	sealed class CNWaveHoldState : CNStateBase, ICNState
	{
		CPos holdCell;
		WPos holdPos;
		bool holdInitialized;
		int reissueCooldown;

		public void Activate(CNSquad squad)
		{
			holdInitialized = false;
			reissueCooldown = 0;
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var mgr = squad.SquadManager;

			// Wave launched and this squad is a participant → switch to MoveToRally.
			if (mgr.IsWaveLaunched && mgr.WaveParticipants.Contains(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad,
					new CNWaveMoveToRallyState(mgr.WaveRallyCell, mgr.WaveTarget));
				return;
			}

			if (!holdInitialized)
				EstablishHoldPosition(squad);

			// React to enemies that wandered into base defense range — fire in place,
			// no state change so the squad stays anchored for the wave.
			ReactToLocalThreats(squad);

			// Drift correction: if the squad has wandered too far from the hold position
			// (e.g. units chased a target out of range), re-issue the move order. Throttled
			// so we don't spam Move orders every tick.
			if (reissueCooldown > 0)
				reissueCooldown--;
			else if (HasDrifted(squad))
			{
				IssueHoldMove(squad);
				reissueCooldown = squad.SquadManager.Info.AttackForceInterval;
			}
		}

		public void Deactivate(CNSquad squad) { }

		void EstablishHoldPosition(CNSquad squad)
		{
			var mgr = squad.SquadManager;
			var baseCell = mgr.GetRandomBaseCenter();

			// Stable per-squad scatter so squads of the same template don't pile up.
			var seed = (squad.TemplateName ?? "") .GetHashCode() ^ squad.CreatedTick;
			var scatter = mgr.Info.WaveHoldScatterCells;
			if (scatter > 0)
			{
				var dx = ((seed & 0xFFFF) % (scatter * 2 + 1)) - scatter;
				var dy = (((seed >> 16) & 0xFFFF) % (scatter * 2 + 1)) - scatter;
				var scattered = new CPos(baseCell.X + dx, baseCell.Y + dy);
				if (squad.World.Map.Contains(scattered))
					baseCell = scattered;
			}

			holdCell = baseCell;
			holdPos = squad.World.Map.CenterOfCell(holdCell);
			holdInitialized = true;

			IssueHoldMove(squad);
			reissueCooldown = mgr.Info.AttackForceInterval;
		}

		void IssueHoldMove(CNSquad squad)
		{
			var target = Target.FromCell(squad.World, holdCell);
			foreach (var unit in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Move", unit, target, false));
		}

		bool HasDrifted(CNSquad squad)
		{
			var scatter = squad.SquadManager.Info.WaveHoldScatterCells;
			var driftThreshold = WDist.FromCells(System.Math.Max(2, scatter * 2));
			var centroid = squad.CenterPosition();
			return (centroid - holdPos).LengthSquared > (long)driftThreshold.Length * driftThreshold.Length;
		}

		void ReactToLocalThreats(CNSquad squad)
		{
			var mgr = squad.SquadManager;
			var radius = WDist.FromCells(mgr.Info.IdleScanRadius);
			var threats = squad.World.FindActorsInCircle(holdPos, radius)
				.Where(a => mgr.IsPreferredEnemyUnit(a) && a.CanBeViewedByPlayer(squad.Bot.Player))
				.ToList();

			if (threats.Count == 0)
				return;

			foreach (var unit in squad.OrderableUnits)
			{
				if (BusyAttack(unit))
					continue;

				Actor pick = null;
				var bestDist = long.MaxValue;
				foreach (var t in threats)
				{
					if (!CanAttackTarget(unit, t))
						continue;
					var d = (t.CenterPosition - unit.CenterPosition).LengthSquared;
					if (d < bestDist)
					{
						bestDist = d;
						pick = t;
					}
				}

				if (pick != null)
					squad.Bot.QueueOrder(new Order("Attack", unit, Target.FromActor(pick), false));
			}
		}
	}

	// ---------------------------------------------------------------------------
	// Wave move-to-rally: squad marches to the staging cell in front of the
	// enemy base. On arrival (or timeout), it hands off to the role's regular
	// idle state which then engages normally.
	// ---------------------------------------------------------------------------

	sealed class CNWaveMoveToRallyState : CNStateBase, ICNState
	{
		readonly CPos rallyCell;
		Actor waveTarget;
		WPos rallyPos;
		int ticks;

		public CNWaveMoveToRallyState(CPos rallyCell, Actor waveTarget)
		{
			this.rallyCell = rallyCell;
			this.waveTarget = waveTarget;
		}

		public void Activate(CNSquad squad)
		{
			rallyPos = squad.World.Map.CenterOfCell(rallyCell);
			ticks = 0;

			if (waveTarget != null && !waveTarget.IsDead && waveTarget.IsInWorld)
				squad.SetActorToTarget(waveTarget);

			var orderName = CNSquadHelper.GetMovementOrderName(squad);
			var target = Target.FromCell(squad.World, rallyCell);
			squad.Bot.QueueOrder(new Order(orderName, null, target, false,
				groupedActors: squad.OrderableUnits.ToArray()));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;
			if (!squad.IsOperational)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNGroundFleeState());
				return;
			}

			ticks++;

			// Re-target if the original wave target died mid-march so the squad
			// has a fresh objective to switch to on arrival.
			if (waveTarget == null || waveTarget.IsDead || !waveTarget.IsInWorld)
			{
				waveTarget = squad.SquadManager.PickWaveTarget()
					?? CNSquadHelper.FindTarget(squad);
				if (waveTarget != null)
					squad.SetActorToTarget(waveTarget);
			}

			var arrivalDist = WDist.FromCells(squad.SquadManager.Info.AttackWaveStagingArrivalCells);
			var arrivalSq = (long)arrivalDist.Length * arrivalDist.Length;
			var allArrived = squad.Units
				.Where(u => u != null && !u.IsDead && u.IsInWorld)
				.All(u => (u.CenterPosition - rallyPos).LengthSquared <= arrivalSq);

			if (allArrived || ticks >= squad.SquadManager.Info.AttackWaveStagingTimeoutTicks)
			{
				if (waveTarget != null && !waveTarget.IsDead && waveTarget.IsInWorld)
					squad.SetActorToTarget(waveTarget);

				squad.SquadManager.InitializeSquadStateForRole(squad);
			}
		}

		public void Deactivate(CNSquad squad) { }
	}
}
