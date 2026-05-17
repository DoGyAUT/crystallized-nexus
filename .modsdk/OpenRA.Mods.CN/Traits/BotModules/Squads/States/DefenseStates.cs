using System;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads.States
{
	sealed class DefenseIdleState : CNStateBase, ICNState
	{
		// If all units are within this many cells of the base center, don't issue a return move.
		const int AlreadyHomeRadiusCells = 10;

		public void Activate(CNSquad squad) { }

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			var center = squad.CenterUnit();
			if (center == null)
				return;

			var dangerRadius = WDist.FromCells(squad.SquadManager.Info.DangerScanRadius);
			var buildings = squad.SquadManager.GetCachedOwnBuildings();
			var basePos = buildings.Count > 0
				? buildings.Select(b => b.CenterPosition).Average()
				: squad.World.Map.CenterOfCell(squad.SquadManager.GetRandomBaseCenter());

			// 1. Immediate threat near the squad itself
			var threat = squad.World
				.FindActorsInCircle(center.CenterPosition, dangerRadius)
				.Where(a => squad.SquadManager.IsPreferredEnemyUnit(a) && a.Info.HasTraitInfo<AttackBaseInfo>())
				.MinByOrDefault(a => (a.CenterPosition - center.CenterPosition).LengthSquared);

			// 2. Threat near the base — same radius as the squad scan but anchored to the base
			// center. Intentionally NOT wider: a larger radius would catch enemies engaged by
			// attack squads in the mid-field and pull the defense garrison out of the base.
			if (threat == null)
			{
				threat = squad.World
					.FindActorsInCircle(basePos, dangerRadius)
					.Where(a => squad.SquadManager.IsPreferredEnemyUnit(a) && a.Info.HasTraitInfo<AttackBaseInfo>())
					.MinByOrDefault(a => (a.CenterPosition - basePos).LengthSquared);
			}

			// 3. Enemies near own harvesters — catches raiders attacking in the Tiberium fields
			if (threat == null)
			{
				foreach (var harvester in squad.World.ActorsHavingTrait<Harvester>()
					.Where(a => a.Owner == squad.SquadManager.Player && !a.IsDead && a.IsInWorld))
				{
					threat = squad.World
						.FindActorsInCircle(harvester.CenterPosition, dangerRadius)
						.Where(a => squad.SquadManager.IsPreferredEnemyUnit(a))
						.MinByOrDefault(a => (a.CenterPosition - harvester.CenterPosition).LengthSquared);

					if (threat != null)
						break;
				}
			}

			if (threat != null)
			{
				squad.SetActorToTarget(threat);
				squad.FuzzyStateMachine.ChangeState(squad, new DefenseEngageState());
			}
			else
			{
				// Only return to base if the squad has actually drifted away from it.
				// Without this check the squad endlessly shuffles between random buildings.
				var homeRadius = WDist.FromCells(AlreadyHomeRadiusCells);
				var alreadyHome = squad.Units
					.Where(u => !u.IsDead && u.IsInWorld)
					.All(u => buildings.Any(b => (u.CenterPosition - b.CenterPosition).Length <= homeRadius.Length));

				if (!alreadyHome)
					squad.FuzzyStateMachine.ChangeState(squad, new DefenseReturnState());
			}
		}

		public void Deactivate(CNSquad squad) { }
	}

	sealed class DefenseEngageState : CNStateBase, ICNState
	{
		int orderTick;
		const int OrderInterval = 3;
		const int MaxPursuitCells = 15;

		public void Activate(CNSquad squad)
		{
			orderTick = 0;
			IssueAttack(squad);
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsOperational)
				return;

			if (!squad.IsTargetValid)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new DefenseIdleState());
				return;
			}

			// Return home if threat moved too far from base — prevents smechs
			// chasing raiders across the entire map
			var buildings = squad.SquadManager.GetCachedOwnBuildings();
			var basePos = buildings.Count > 0
				? buildings.Select(b => b.CenterPosition).Average()
				: squad.World.Map.CenterOfCell(squad.SquadManager.GetRandomBaseCenter());
			WPos targetPos;
			try
			{
				if (squad.Target.Type == TargetType.Invalid)
				{
					squad.FuzzyStateMachine.ChangeState(squad, new DefenseIdleState());
					return;
				}

				targetPos = squad.Target.CenterPosition;
			}
			catch (InvalidOperationException)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new DefenseIdleState());
				return;
			}

			if ((targetPos - basePos).Length > WDist.FromCells(MaxPursuitCells).Length)
			{
				squad.FuzzyStateMachine.ChangeState(squad, new DefenseReturnState());
				return;
			}

			if (--orderTick <= 0)
			{
				orderTick = OrderInterval;
				IssueAttack(squad);
			}
		}

		public void Deactivate(CNSquad squad) { }

		static void IssueAttack(CNSquad squad)
		{
			if (squad.Target.Type == TargetType.Invalid) return;

			var attackers = squad.OrderableUnits
				.Where(u => !u.IsDead)
				.Where(u =>
				{
					var ab = u.TraitOrDefault<AttackBase>();
					if (ab == null) return false;
					var t = squad.Target;
					return ab.HasAnyValidWeapons(t, false);
				})
				.ToArray();

			if (attackers.Length == 0) return;

			squad.Bot.QueueOrder(new Order("AttackMove", null, squad.Target, false,
				groupedActors: attackers));
		}
	}

	sealed class DefenseReturnState : CNStateBase, ICNState
	{
		int waitTicks;
		CPos returnCell;
		const int MaxWaitTicks = 15;
		const int ArrivalRadiusCells = 4;

		public void Activate(CNSquad squad)
		{
			waitTicks = 0;
			returnCell = RandomBuildingLocation(squad);

			var target = Target.FromCell(squad.World, returnCell);
			foreach (var a in squad.OrderableUnits)
				squad.Bot.QueueOrder(new Order("Move", a, target, false));
		}

		public void Tick(CNSquad squad)
		{
			if (!squad.IsValid)
				return;

			waitTicks++;

			var returnPos = squad.World.Map.CenterOfCell(returnCell);
			var arrivalDist = WDist.FromCells(ArrivalRadiusCells);
			var units = squad.Units.Where(u => !u.IsDead).ToList();

			if (!units.Any()) return;

			var allArrived = units.All(u => (u.CenterPosition - returnPos).Length <= arrivalDist.Length);

			if (allArrived || waitTicks > MaxWaitTicks)
				squad.FuzzyStateMachine.ChangeState(squad, new DefenseIdleState());
		}

		public void Deactivate(CNSquad squad) { }
	}
}
