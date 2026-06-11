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
		const int RethinkInterval = 3;
		int rethinkTicks;

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
			if (squad.PreferredTargetCapabilities != null && squad.PreferredTargetCapabilities.Length > 0)
				target = FindPriorityTarget(squad, squad.PreferredTargetCapabilities, center);

			target ??= CNSquadHelper.FindUnprotectedTarget(squad);

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
		const int MaxStuckTicks = 120;
		int lastActivityTick;
		CPos lastCenterPos;

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

			// Stealth units uncloak to fire (UncloakOn: Attack), so being "revealed" is the normal
			// state mid-attack — not a reason to bail. Previously any uncloaked unit triggered a
			// flee, so the squad retreated right after its first shot and never committed to a kill.
			// Fleeing is now driven purely by the threat/health assessment, so the squad presses the
			// attack and only pulls back when it is actually being beaten.
			if (ShouldFlee(squad))
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
			// Default (not Raider) profile: stealth tanks are hard-hitting ambushers that should
			// trade with an even or weaker enemy and only flee when injured/outgunned. The Raider
			// profile fled even at full health against a healthy enemy, which made them too timid.
			return ShouldFlee(squad, enemies =>
				!CNAttackOrFleeFuzzy.Default.CanAttack(squad.Units, enemies, squad.SquadManager.GetAttackFuzzyBoost()));
		}
	}

	sealed class StealthFleeState : CNStateBase, ICNState
	{
		const int RecloakWaitTicks = 150;
		const int MinRetreatCells = 6;
		const int MaxRetreatCells = 14;
		const int ReengageThreatDistanceCells = 10;
		int fleeStartTick;

		public void Activate(CNSquad squad)
		{
			fleeStartTick = squad.World.WorldTick;

			var retreatCell = FindRetreatCell(squad);
			if (retreatCell.HasValue)
			{
				var target = Target.FromCell(squad.World, retreatCell.Value);
				foreach (var unit in squad.OrderableUnits)
					squad.Bot.QueueOrder(new Order("Move", unit, target, false));
			}
			else
				GoToRandomOwnBuilding(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			var enemy = RaiderAttackState.FindClosestThreat(squad, center, squad.SquadManager.Info.DangerScanRadius);
			var allCloaked = squad.OrderableUnits.All(IsCloakedOrUncloakable);

			if (enemy == null ||
				(allCloaked && HasOpenedKiteDistance(center, enemy)) ||
				squad.World.WorldTick - fleeStartTick >= RecloakWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
		}

		public void Deactivate(CNSquad squad) { }

		static CPos? FindRetreatCell(CNSquad squad)
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
			CPos? bestCell = null;
			var bestScore = int.MinValue;

			for (var dy = -MaxRetreatCells; dy <= MaxRetreatCells; dy++)
			{
				for (var dx = -MaxRetreatCells; dx <= MaxRetreatCells; dx++)
				{
					var distanceSquared = dx * dx + dy * dy;
					if (distanceSquared < MinRetreatCells * MinRetreatCells ||
						distanceSquared > MaxRetreatCells * MaxRetreatCells)
						continue;

					var candidate = origin + new CVec(dx, dy);
					if (!map.Contains(candidate) || !mobile.CanEnterCell(candidate))
						continue;

					var score = ScoreRetreatCell(squad, candidate, baseCell);
					if (score <= bestScore)
						continue;

					bestScore = score;
					bestCell = candidate;
				}
			}

			return bestCell;
		}

		static int ScoreRetreatCell(CNSquad squad, CPos candidate, CPos baseCell)
		{
			var candidatePos = squad.World.Map.CenterOfCell(candidate);
			var closestEnemyDistance = int.MaxValue;
			var closestThreatCell = CPos.Zero;
			var foundThreat = false;

			foreach (var actor in squad.World.FindActorsInCircle(candidatePos,
				WDist.FromCells(squad.SquadManager.Info.DangerScanRadius + 4)))
			{
				if (!squad.SquadManager.IsPreferredEnemyUnit(actor) ||
					!actor.Info.HasTraitInfo<AttackBaseInfo>())
					continue;

				var distance = (int)(actor.CenterPosition - candidatePos).LengthSquared;
				if (distance < closestEnemyDistance)
				{
					closestEnemyDistance = distance;
					closestThreatCell = actor.Location;
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

		static bool IsCloakedOrUncloakable(Actor actor)
		{
			var cloaks = actor.TraitsImplementing<Cloak>().Where(c => !c.IsTraitDisabled).ToList();
			return cloaks.Count == 0 || cloaks.All(c => c.Cloaked);
		}

		static bool HasOpenedKiteDistance(Actor center, Actor enemy)
		{
			var minDistance = WDist.FromCells(ReengageThreatDistanceCells).Length;
			return HorizontalLengthSquared(center.CenterPosition - enemy.CenterPosition) >= (long)minDistance * minDistance;
		}
	}
}
