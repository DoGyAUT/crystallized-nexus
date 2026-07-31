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
			var seed = (squad.TemplateName ?? "").GetHashCode() ^ squad.CreatedTick;
			var scatter = mgr.Info.WaveHoldScatterCells;
			if (scatter > 0)
			{
				var dx = (seed & 0xFFFF) % (scatter * 2 + 1) - scatter;
				var dy = ((seed >> 16) & 0xFFFF) % (scatter * 2 + 1) - scatter;
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
	// enemy base and waits there for the rest of the wave. It hands off to the
	// role's regular idle state — which then engages normally — once enough of
	// the wave has assembled, or when the staging timeout runs out.
	// ---------------------------------------------------------------------------
	sealed class CNWaveMoveToRallyState : CNStateBase, ICNState
	{
		const int MaxNoProgressTicks = 225;
		const int MinProgressDistance = 512;
		readonly CPos rallyCell;
		Actor waveTarget;
		WPos rallyPos;
		int stagingStartTick;
		int lastProgressTick;
		long lastDistanceSq;

		public CNWaveMoveToRallyState(CPos rallyCell, Actor waveTarget)
		{
			this.rallyCell = rallyCell;
			this.waveTarget = waveTarget;
		}

		public void Activate(CNSquad squad)
		{
			rallyPos = squad.World.Map.CenterOfCell(rallyCell);
			stagingStartTick = 0;
			lastProgressTick = squad.World.WorldTick;
			lastDistanceSq = (squad.CenterPosition() - rallyPos).LengthSquared;

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
				// Route back through the squad's own role state machine instead of hardcoding the
				// ground flee/dissolve path — WaveParticipantRoles also includes Artillery and
				// Subterranean squads, whose own flee states explicitly persist and reform rather
				// than dissolving (see ArtilleryFleeState / SubAssaultReburrowState). Retreat orders
				// are still generic and issued regardless of role.
				Retreat(squad, flee: true, rearm: true, repair: true);
				squad.SquadManager.InitializeSquadStateForRole(squad);
				return;
			}

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

			// The staging timeout is a budget for WAITING at the rally point, not for getting there.
			// It used to start when the squad set off, so on a large map the march consumed it whole
			// and the squad was already "timed out" the moment it arrived — no waiting happened at all.
			var selfArrived = HasArrivedAtRally(squad, rallyPos, arrivalSq);
			if (selfArrived && stagingStartTick == 0)
				stagingStartTick = squad.World.WorldTick;

			var currentDistanceSq = (squad.CenterPosition() - rallyPos).LengthSquared;
			const long ProgressThresholdSq = (long)MinProgressDistance * MinProgressDistance;
			if (currentDistanceSq + ProgressThresholdSq < lastDistanceSq)
			{
				lastDistanceSq = currentDistanceSq;
				lastProgressTick = squad.World.WorldTick;
			}

			var nearbyEnemy = squad.World
				.FindActorsInCircle(squad.CenterPosition(), WDist.FromCells(squad.SquadManager.Info.AttackScanRadius))
				.FirstOrDefault(a => squad.SquadManager.IsPreferredEnemyUnit(a) && a.CanBeViewedByPlayer(squad.Bot.Player));
			if (nearbyEnemy != null)
				squad.SetActorToTarget(nearbyEnemy);

			var timedOut = stagingStartTick != 0 &&
				squad.World.WorldTick - stagingStartTick >= squad.SquadManager.Info.AttackWaveStagingTimeoutTicks;

			// Stuck detection only covers the march. Once the squad is at the rally point it stops
			// closing on it by definition, so leaving this armed would evict every waiting squad
			// after MaxNoProgressTicks and defeat the staging. From then on the timeout governs,
			// with the manager's AttackWaveMaxActiveTicks as the outer backstop.
			var stuck = stagingStartTick == 0 && squad.World.WorldTick - lastProgressTick >= MaxNoProgressTicks;

			if (EnoughWaveParticipantsArrived(squad, rallyPos, arrivalSq) || nearbyEnemy != null || timedOut || stuck)
			{
				if (nearbyEnemy == null && waveTarget != null && !waveTarget.IsDead && waveTarget.IsInWorld)
					squad.SetActorToTarget(waveTarget);

				squad.SquadManager.InitializeSquadStateForRole(squad);
			}
		}

		public void Deactivate(CNSquad squad) { }

		/// <summary>
		/// True once two thirds of a squad's live units stand within arrival tolerance of the rally
		/// point. Every wave shares one rally cell, so this is safe to ask about any participant.
		/// </summary>
		static bool HasArrivedAtRally(CNSquad squad, WPos rallyPos, long arrivalSq)
		{
			var live = 0;
			var arrived = 0;
			foreach (var unit in squad.Units)
			{
				if (unit == null || unit.IsDead || !unit.IsInWorld)
					continue;

				live++;
				if ((unit.CenterPosition - rallyPos).LengthSquared <= arrivalSq)
					arrived++;
			}

			return live > 0 && arrived >= System.Math.Max(1, live * 2 / 3);
		}

		/// <summary>
		/// The condition for leaving the rally point: enough of the WAVE has assembled — not just
		/// enough of this one squad. Judging only its own units made the rally cell a shared waypoint
		/// rather than a meeting point, so squads filed into the enemy base one after another.
		/// <para>
		/// Every participant evaluates this against the same unpruned participant set within a single
		/// manager tick (squads are updated before MonitorActiveWave prunes), so the wave releases as
		/// a body instead of cascading one squad at a time.
		/// </para>
		/// </summary>
		static bool EnoughWaveParticipantsArrived(CNSquad squad, WPos rallyPos, long arrivalSq)
		{
			var mgr = squad.SquadManager;

			// The wave dissolved (target died, hard timeout) or this squad was dropped from it —
			// fall back to judging itself, otherwise it would wait for a wave that no longer exists.
			if (!mgr.IsWaveLaunched || !mgr.WaveParticipants.Contains(squad))
				return HasArrivedAtRally(squad, rallyPos, arrivalSq);

			var participants = 0;
			var arrived = 0;
			foreach (var participant in mgr.WaveParticipants)
			{
				if (participant == null || !participant.IsValid)
					continue;

				participants++;
				if (HasArrivedAtRally(participant, rallyPos, arrivalSq))
					arrived++;
			}

			if (participants == 0)
				return true;

			var percent = System.Math.Clamp(mgr.Info.AttackWaveStagingMinArrivedPercent, 1, 100);
			return arrived >= System.Math.Max(1, participants * percent / 100);
		}
	}
}
