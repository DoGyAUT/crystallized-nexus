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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Manages AI MCVs and expansion.")]
	public class CNMcvExpansionManagerBotModuleInfo : ConditionalTraitInfo, Requires<CNResourceMapBotModuleInfo>, NotBefore<CNResourceMapBotModuleInfo>
	{
		[Desc("Actor types that are considered MCVs (deploy into base builders).")]
		public readonly FrozenSet<string> McvTypes = FrozenSet<string>.Empty;

		[Desc("Actor types that are considered construction yards (base builders).")]
		public readonly FrozenSet<string> ConstructionYardTypes = FrozenSet<string>.Empty;

		[Desc("Actor types that are able to produce MCVs.")]
		public readonly FrozenSet<string> McvFactoryTypes = FrozenSet<string>.Empty;

		[Desc("Try to maintain at least this many ConstructionYardTypes, build an MCV if number is below this.")]
		public readonly int MinimumConstructionYardCount = 1;

		[Desc("Try to maintain at additional this many ConstructionYardTypes.")]
		public readonly int AdditionalConstructionYardCount = 0;

		[Desc("Per-profile additional construction yard counts. Overrides AdditionalConstructionYardCount for the active profile.")]
		public readonly FrozenDictionary<string, int> AdditionalConstructionYardCounts = null;

		[Desc("Build additional MCV if cash is above this.")]
		public readonly int BuildAdditionalMCVCashAmount = 5000;

		[Desc("Per-profile cash thresholds to trigger additional MCV building. Overrides BuildAdditionalMCVCashAmount for the active profile.")]
		public readonly FrozenDictionary<string, int> BuildAdditionalMCVCashAmounts = null;

		[Desc("Delay (in ticks) for giving orders to idle MCVs.")]
		public readonly int ScanForNewMcvInterval = 20;

		[Desc("Delay (in ticks) for checking and building a MCV.")]
		public readonly int BuildMcvInterval = 101;

		[Desc("Delay (in ticks) for moving a conyard to better expansion. Only work with more than 1 conyard.")]
		public readonly int MoveConyardTick = 5700;

		[Desc("Should moving the oldest or newest conyard be preferred? Random ordering if unset.")]
		public readonly bool? MoveOldConyardFirst = null;

		[Desc("When economy is already covered, prefer CheckBase expansions as forward outposts instead of only resource expansions.")]
		public readonly bool EnableStrategicOutposts = true;

		[Desc("Initial expansion mode chosen by AI.")]
		public readonly BotMcvExpansionMode InitialExpansionMode = BotMcvExpansionMode.CheckResource;

		[Desc("Allow the bot to switch expansion mode automatically on enough failure or successful attempts.")]
		public readonly bool ExpansionModeAutoSwitch = true;

		/* those are CheckResource mode options */
		[Desc("Minimum distance (in cells) from the found resource creator location when checking for MCV deployment location.")]
		public readonly int CRmodeMinDeployRadius = 2;

		[Desc("Maximum distance (in cells) the found resource creator location when checking for MCV deployment location.")]
		public readonly int CRmodeMaxDeployRadius = 20;

		[Desc("When moving to a resource, what distance (in cells) to resource should we attempt to maintain?")]
		public readonly int CRmodeTryMaintainRange = 8;

		[Desc("Distance (in cells) to avoid a friendly conyard when choosing an expansion location.",
					"Recommended to set it equal or larger than ResourceMapStrideRadius.")]
		public readonly int CRmodeFriendlyConyardDislikeRange = 14;

		[Desc("Distance (in cells) to avoid a friendly refinery when choosing an expansion location.",
					"Recommended to set it equal or larger than ResourceMapStrideRadius.")]
		public readonly int CRmodeFriendlyRefineryDislikeRange = 14;

		[Desc("Bonus attraction for resource indices with respawning resource sources.")]
		public readonly int CRmodeRespawningFieldBonus = 96;

		[Desc("Penalty per existing friendly refinery already serving the candidate indice.")]
		public readonly int CRmodeExistingRefineryPenalty = 80;

		[Desc("Cells around known enemy base buildings that MCV expansion routing should avoid when possible.")]
		public readonly int McvEnemyBaseAvoidanceRadius = 10;

		[Desc("Cells around known enemy base buildings that MCV expansion routing treats as blocked.")]
		public readonly int McvEnemyBaseHardAvoidanceRadius = 4;

		[Desc("Additional path cost applied inside McvEnemyBaseAvoidanceRadius.")]
		public readonly int McvEnemyBaseRoutePenalty = 2048;

		[Desc("Maximum number of path cells an MCV moves in one expansion order before reevaluating the route.")]
		public readonly int McvSafeMoveWaypointPathCells = 12;

		[Desc("Distance in cells for MCVs to avoid expansion targets already claimed by other MCVs.")]
		public readonly int McvExpansionCoordinationRadius = 24;

		[Desc("Maximum score penalty for selecting an expansion target that is too close to another MCV's target.")]
		public readonly int McvExpansionCoordinationPenalty = 768;

		/* those are CheckBase mode options */
		[Desc("Minimum distance (in cells) from center of the base expansion when checking for MCV deployment location.")]
		public readonly int CBmodeMinDeployRadius = 2;

		[Desc("Maximum distance (in cells) from center of the base expansion when checking for MCV deployment location.")]
		public readonly int CBmodeMaxDeployRadius = 20;

		[Desc("Score bonus for CheckBase candidates that sit on the frontline between friendly and enemy bases.")]
		public readonly int CBmodeFrontlineOutpostBonus = 384;

		[Desc("Preferred progress percentage from friendly base toward enemy base for outposts.")]
		public readonly int CBmodeOutpostPreferredProgress = 58;

		[Desc("Allowed progress percentage tolerance around CBmodeOutpostPreferredProgress.")]
		public readonly int CBmodeOutpostProgressTolerance = 22;

		[Desc("Maximum perpendicular distance in cells from the friendly-to-enemy line for frontline outpost candidates.")]
		public readonly int CBmodeOutpostCorridorWidth = 12;

		[Desc("Minimum distance in cells from known enemy bases for frontline outpost candidates.")]
		public readonly int CBmodeOutpostEnemyMinRange = 18;

		[Desc("Minimum distance in cells from friendly construction yards for frontline outpost candidates.")]
		public readonly int CBmodeOutpostFriendlyMinRange = 12;

		[Desc("Minimum time in ticks that a newly deployed expansion conyard is protected from being moved again.")]
		public readonly int ExpansionGoalMinimumHoldTicks = 3000;

		[Desc("Maximum distance in cells for matching a newly deployed conyard to the MCV deploy cell that created it.")]
		public readonly int ExpansionGoalMatchRadius = 6;

		public override object Create(ActorInitializer init) { return new CNMcvExpansionManagerBotModule(init.Self, this); }
	}

	public class CNMcvExpansionManagerBotModule :
		ConditionalTrait<CNMcvExpansionManagerBotModuleInfo>,
		IBotTick,
		IBotRespondToAttack,
		IBotBaseExpansion,
		INotifyActorDisposing
	{
		// When ExpansionModeAutoSwitch is true, if the AI fails to find a deploy spot enough time even in CheckBase mode
		// NegativeMaxFailedAttempts is applied to make AI switch bettween modes more frequently until a successful attempt
		const int CRmodPositiveMaxFailedAttempts = 3;
		const int CBmodPositiveMaxFailedAttempts = 2;
		const int NegativeMaxFailedAttempts = 0;

		enum ExpansionGoal
		{
			Economy,
			BaseExtension,
			DefenseOutpost
		}

		readonly World world;
		readonly Player player;
		readonly ActorIndex.OwnerAndNamesAndTrait<TransformsInfo> mcvs;
		readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> constructionYards;
		readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> mcvFactories;

		IBotPositionsUpdated[] notifyPositionsUpdated;
		IBotRequestUnitProduction[] requestUnitProduction;
		IBotSuggestRefineryProduction[] suggestRefineryProduction;
		CNBaseBuilderBotModule cnBaseBuilder;
		CNBotProfileBotModule profileModule;

		readonly Dictionary<Actor, CPos?> activeMCVs = [];
		readonly Dictionary<Actor, int> mcvRetryCooldown = [];
		readonly Dictionary<CPos, (ExpansionGoal Goal, int UntilTick)> pendingExpansionGoalLocks = [];
		readonly Dictionary<Actor, (ExpansionGoal Goal, int UntilTick, CPos DeployCell)> conyardExpansionGoalLocks = [];

		CPos[] enemyBaseLocationsCache = [];

		PathFinder pathfinder;
		CNResourceMapBotModule resourceMapModule;
		PlayerResources playerResources;
		Actor mustUndeployCoyard;

		int scanInterval;
		int buildMCVInterval;
		int moveConyardInterval;
		bool firstTick = true;
		bool undeployEvenNoBase = false;
		bool allowfallback = true;

		BotMcvExpansionMode mcvExpansionMode;
		ExpansionGoal currentExpansionGoal = ExpansionGoal.Economy;
		int mcvDeploymentMinDeployRadius;
		int mcvDeploymentMaxDeployRadius;
		int mcvDeploymentTryMaintainRange;
		int maxFailedAttempts;

		int failedAttempts;
		CPos? lastFailedCheckSpot;

		// It is unnecessary to respond every tick, we only need to respond once in a while.
		int attackrespondcooldown = 20;

		int pathDistanceSquareFactor;

		public CNMcvExpansionManagerBotModule(Actor self, CNMcvExpansionManagerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			mcvs = new ActorIndex.OwnerAndNamesAndTrait<TransformsInfo>(world, info.McvTypes, player);
			constructionYards = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.ConstructionYardTypes, player);
			mcvFactories = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.McvFactoryTypes, player);
		}

		protected override void Created(Actor self)
		{
			// Special case handling is required for the Player actor.
			// Created is called before Player.PlayerActor is assigned,
			// so we must query player traits from self, which refers
			// for bot modules always to the Player actor.
			notifyPositionsUpdated = self.TraitsImplementing<IBotPositionsUpdated>().ToArray();
			requestUnitProduction = self.TraitsImplementing<IBotRequestUnitProduction>().ToArray();
			suggestRefineryProduction = self.TraitsImplementing<IBotSuggestRefineryProduction>().ToArray();
			cnBaseBuilder = self.TraitsImplementing<CNBaseBuilderBotModule>().FirstOrDefault();
			pathfinder = world.WorldActor.Trait<PathFinder>();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
		}

		protected override void TraitEnabled(Actor self)
		{
			// Avoid all AIs reevaluating assignments on the same tick, randomize their initial evaluation delay.
			scanInterval = world.LocalRandom.Next(Info.ScanForNewMcvInterval, Info.ScanForNewMcvInterval << 1);
			buildMCVInterval = world.LocalRandom.Next(Info.BuildMcvInterval, Info.BuildMcvInterval << 1);
			moveConyardInterval = world.LocalRandom.Next(Info.MoveConyardTick, Info.MoveConyardTick << 1);
		}

		void SwitchExpansionMode(BotMcvExpansionMode nextMode)
		{
			mcvExpansionMode = nextMode;
			switch (nextMode)
			{
				case BotMcvExpansionMode.CheckResource:
					mcvDeploymentMinDeployRadius = Info.CRmodeMinDeployRadius;
					mcvDeploymentMaxDeployRadius = Info.CRmodeMaxDeployRadius;
					mcvDeploymentTryMaintainRange = Info.CRmodeTryMaintainRange;
					break;

				case BotMcvExpansionMode.CheckBase:
					mcvDeploymentMinDeployRadius = Info.CBmodeMinDeployRadius;
					mcvDeploymentMaxDeployRadius = Info.CBmodeMaxDeployRadius;
					mcvDeploymentTryMaintainRange = (Info.CBmodeMaxDeployRadius + Info.CBmodeMinDeployRadius) >> 1;
					break;

				case BotMcvExpansionMode.CheckCurrentLocation:
					mcvDeploymentMinDeployRadius = Info.CBmodeMinDeployRadius;
					mcvDeploymentMaxDeployRadius = Info.CBmodeMaxDeployRadius;
					mcvDeploymentTryMaintainRange = 0;
					break;

				default:
					break;
			}
		}

		void SetExpansionGoal(ExpansionGoal goal)
		{
			currentExpansionGoal = goal;

			switch (goal)
			{
				case ExpansionGoal.Economy:
					SwitchExpansionMode(BotMcvExpansionMode.CheckResource);
					break;

				case ExpansionGoal.BaseExtension:
				case ExpansionGoal.DefenseOutpost:
					SwitchExpansionMode(BotMcvExpansionMode.CheckBase);
					break;
			}
		}

		void FindBadDeploySpot(CPos? failedSpot)
		{
			lastFailedCheckSpot = failedSpot;

			if (!Info.ExpansionModeAutoSwitch)
			{
				if (++failedAttempts >= maxFailedAttempts)
					failedAttempts = maxFailedAttempts;
				return;
			}

			if (++failedAttempts >= maxFailedAttempts)
			{
				failedAttempts = 0;
				switch (mcvExpansionMode)
				{
					case BotMcvExpansionMode.CheckResource:
						SwitchExpansionMode(BotMcvExpansionMode.CheckBase);
						break;

					case BotMcvExpansionMode.CheckBase:
						SwitchExpansionMode(BotMcvExpansionMode.CheckResource);
						maxFailedAttempts = NegativeMaxFailedAttempts;
						break;

					case BotMcvExpansionMode.CheckCurrentLocation:
						SwitchExpansionMode(BotMcvExpansionMode.CheckResource);
						maxFailedAttempts = NegativeMaxFailedAttempts;
						break;
				}
			}
		}

		void FindGoodDeploySpot()
		{
			lastFailedCheckSpot = null;

			if (!Info.ExpansionModeAutoSwitch)
			{
				if (--failedAttempts <= -maxFailedAttempts)
					failedAttempts = -maxFailedAttempts;
				return;
			}

			if (--failedAttempts <= -maxFailedAttempts)
			{
				switch (mcvExpansionMode)
				{
					case BotMcvExpansionMode.CheckResource:
						maxFailedAttempts = CRmodPositiveMaxFailedAttempts;
						failedAttempts = -maxFailedAttempts;
						break;

					case BotMcvExpansionMode.CheckBase:
						maxFailedAttempts = CRmodPositiveMaxFailedAttempts;
						failedAttempts = maxFailedAttempts - 1;
						SwitchExpansionMode(BotMcvExpansionMode.CheckResource);
						break;

					case BotMcvExpansionMode.CheckCurrentLocation:
						maxFailedAttempts = CBmodPositiveMaxFailedAttempts;
						failedAttempts = maxFailedAttempts - 1;
						SwitchExpansionMode(BotMcvExpansionMode.CheckBase);
						break;
				}
			}
		}

		CPos[] GetKnownEnemyBaseLocations()
		{
			if (resourceMapModule == null || resourceMapModule.Info.EnemyBaseBuildingTypes.Count == 0)
				return [];

			return world.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && !a.Disposed && a.IsInWorld
					&& a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy
					&& resourceMapModule.Info.EnemyBaseBuildingTypes.Contains(a.Info.Name))
				.Select(a => a.Location)
				.ToArray();
		}

		CPos? GetOwnBaseCenter()
		{
			var bases = constructionYards.Actors
				.Where(a => !a.IsDead && !a.Disposed && a.IsInWorld && a.Owner == player)
				.Select(a => a.Location)
				.ToArray();

			if (bases.Length == 0)
				return null;

			return new CPos((int)bases.Average(c => c.X), (int)bases.Average(c => c.Y));
		}

		CPos? GetEnemyBaseCenter()
		{
			var enemyBases = enemyBaseLocationsCache;
			if (enemyBases.Length == 0)
				return null;

			return new CPos((int)enemyBases.Average(c => c.X), (int)enemyBases.Average(c => c.Y));
		}

		int CalculateFrontlineOutpostBonus(CPos candidate, CPos friendlyBaseCenter, CPos enemyBaseCenter)
		{
			if (!Info.EnableStrategicOutposts || Info.CBmodeFrontlineOutpostBonus <= 0)
				return 0;

			var vx = enemyBaseCenter.X - friendlyBaseCenter.X;
			var vy = enemyBaseCenter.Y - friendlyBaseCenter.Y;
			var lenSq = (long)vx * vx + (long)vy * vy;
			if (lenSq <= 0)
				return 0;

			var cx = candidate.X - friendlyBaseCenter.X;
			var cy = candidate.Y - friendlyBaseCenter.Y;
			var dot = (long)cx * vx + (long)cy * vy;
			if (dot <= 0 || dot >= lenSq)
				return 0;

			var progress = (int)(dot * 100 / lenSq);
			var preferredProgress = Info.CBmodeOutpostPreferredProgress.Clamp(1, 99);
			var progressTolerance = Math.Max(1, Info.CBmodeOutpostProgressTolerance);
			var progressDelta = Math.Abs(progress - preferredProgress);
			if (progressDelta > progressTolerance)
				return 0;

			var corridorWidth = Math.Max(1, Info.CBmodeOutpostCorridorWidth);
			var cross = (long)cx * vy - (long)cy * vx;
			var perpendicularDistSq = cross * cross / lenSq;
			if (perpendicularDistSq > corridorWidth * corridorWidth)
				return 0;

			var enemyMinRange = Math.Max(0, Info.CBmodeOutpostEnemyMinRange);
			if (enemyMinRange > 0 && (candidate - enemyBaseCenter).LengthSquared < enemyMinRange * enemyMinRange)
				return 0;

			var friendlyMinRange = Math.Max(0, Info.CBmodeOutpostFriendlyMinRange);
			if (friendlyMinRange > 0 && (candidate - friendlyBaseCenter).LengthSquared < friendlyMinRange * friendlyMinRange)
				return 0;

			var progressScore = (progressTolerance - progressDelta) * Info.CBmodeFrontlineOutpostBonus / progressTolerance;
			var corridorScore = (int)((corridorWidth * corridorWidth - perpendicularDistSq) * Info.CBmodeFrontlineOutpostBonus / (corridorWidth * corridorWidth));

			return (progressScore + corridorScore) / 2;
		}

		Func<CPos, int> CreateMcvEnemyBaseAvoidanceCost(CPos source, CPos target)
		{
			var enemyBases = enemyBaseLocationsCache;
			if (enemyBases.Length == 0 || Info.McvEnemyBaseAvoidanceRadius <= 0)
				return null;

			var avoidRadiusSq = Info.McvEnemyBaseAvoidanceRadius * Info.McvEnemyBaseAvoidanceRadius;
			var hardRadiusSq = Info.McvEnemyBaseHardAvoidanceRadius * Info.McvEnemyBaseHardAvoidanceRadius;
			var hardRadiusEnabled = Info.McvEnemyBaseHardAvoidanceRadius > 0;

			return cell =>
			{
				if (cell == source || cell == target)
					return 0;

				var nearestDistSq = enemyBases.Min(baseLoc => (cell - baseLoc).LengthSquared);
				if (hardRadiusEnabled && nearestDistSq <= hardRadiusSq)
					return PathGraph.PathCostForInvalidPath;

				if (nearestDistSq > avoidRadiusSq)
					return 0;

				// Stronger near the enemy base core, lower toward the edge.
				return Info.McvEnemyBaseRoutePenalty * (avoidRadiusSq - nearestDistSq + 1) / avoidRadiusSq;
			};
		}

		List<CPos> FindSafeMcvPath(Actor mcv, CPos source, CPos target)
		{
			var customCost = CreateMcvEnemyBaseAvoidanceCost(source, target);
			var path = pathfinder.FindPathToTargetCells(
				mcv,
				source,
				[target],
				BlockedByActor.Immovable,
				customCost,
				mcv,
				laneBias: false);

			if (path == null || path == PathFinder.NoPath || path.Count == 0)
				return null;

			return path;
		}

		CPos ChooseSafeMoveWaypoint(List<CPos> path, CPos finalCell)
		{
			if (path == null || path.Count == 0)
				return finalCell;

			var maxStep = Math.Max(1, Info.McvSafeMoveWaypointPathCells);
			if (path.Count <= maxStep + 1)
				return finalCell;

			// Paths are returned reversed, target -> source. Pick a waypoint near the source
			// so the normal Move order can't re-route across a whole enemy base in one go.
			return path[Math.Max(0, path.Count - 1 - maxStep)];
		}

		void TrackExpansionGoal(CPos deployCell)
		{
			if (Info.ExpansionGoalMinimumHoldTicks <= 0)
				return;

			pendingExpansionGoalLocks[deployCell] = (currentExpansionGoal, world.WorldTick + Info.ExpansionGoalMinimumHoldTicks);
		}

		void CleanupExpansionGoalLocks()
		{
			foreach (var kv in conyardExpansionGoalLocks.ToList())
				if (kv.Key.IsDead || !kv.Key.IsInWorld || world.WorldTick >= kv.Value.UntilTick)
					conyardExpansionGoalLocks.Remove(kv.Key);

			if (pendingExpansionGoalLocks.Count == 0)
				return;

			var matchRadius = Math.Max(0, Info.ExpansionGoalMatchRadius);
			var matchRadiusSq = matchRadius * matchRadius;
			foreach (var (deployCell, expansionLock) in pendingExpansionGoalLocks.ToList())
			{
				if (world.WorldTick >= expansionLock.UntilTick)
				{
					pendingExpansionGoalLocks.Remove(deployCell);
					continue;
				}

				var conyard = constructionYards.Actors
					.Where(a => !a.IsDead && !a.Disposed && a.IsInWorld && a.Owner == player
						&& (a.Location - deployCell).LengthSquared <= matchRadiusSq)
					.MinByOrDefault(a => (a.Location - deployCell).LengthSquared);

				if (conyard == null)
					continue;

				conyardExpansionGoalLocks[conyard] = (expansionLock.Goal, expansionLock.UntilTick, deployCell);
				pendingExpansionGoalLocks.Remove(deployCell);
			}
		}

		bool IsExpansionGoalLocked(Actor conyard)
		{
			if (!conyardExpansionGoalLocks.TryGetValue(conyard, out var expansionLock))
				return false;

			if (conyard.IsDead || !conyard.IsInWorld || world.WorldTick >= expansionLock.UntilTick)
			{
				conyardExpansionGoalLocks.Remove(conyard);
				return false;
			}

			return true;
		}

		int CalculateExpansionCoordinationPenalty(CPos candidate, Actor mcv, int indiceSideLengthSquare)
		{
			var radius = Math.Max(0, Info.McvExpansionCoordinationRadius);
			if (radius == 0 || Info.McvExpansionCoordinationPenalty <= 0)
				return 0;

			var radiusSq = radius * radius;
			var maxPenalty = Math.Max(Info.McvExpansionCoordinationPenalty, indiceSideLengthSquare << 1);
			var penalty = 0;

			void AddPenalty(CPos target)
			{
				var distSq = (candidate - target).LengthSquared;
				if (distSq > radiusSq)
					return;

				penalty += maxPenalty * (radiusSq - distSq + 1) / radiusSq;
			}

			foreach (var (otherMcv, target) in activeMCVs)
				if (otherMcv != mcv && target.HasValue)
					AddPenalty(target.Value);

			foreach (var (deployCell, expansionLock) in pendingExpansionGoalLocks)
				if (world.WorldTick < expansionLock.UntilTick)
					AddPenalty(deployCell);

			foreach (var (conyard, expansionLock) in conyardExpansionGoalLocks)
				if (!conyard.IsDead && conyard.IsInWorld && world.WorldTick < expansionLock.UntilTick)
					AddPenalty(expansionLock.DeployCell);

			return penalty;
		}

		public (CPos? ExpandLocation, int Attraction, CPos? CheckSpot) GetExpansionCenter(Actor mcv, Mobile mobile, bool allowfallback)
		{
			/*
			 * indiceSideLengthSquare (which is equal to indiceSideLength * indiceSideLength) is used as the basic unit to calculate the attraction of a candidate,
			 * we  compare the attraction on the same scale on different factors, such as candidate's distance to current MCV and ally construction yard & refinery within range, etc:
			 *
			 * 1). the weight of candidate's distance-square to current MCV
			 *
			 *     a) if not Mobile: range from 0 to -indiceSideLengthSquare.
			 *
			 *     The reason why:
			 *
			 *     It is calculated as "(candidate - mcv.Location).LengthSquared / pathDistanceSquareFactor".
			 *     note that: pathDistanceSquareFactor = resourceMapIndicesColumnCount * resourceMapIndicesColumnCount + resourceMapIndicesRowCount * resourceMapIndicesRowCount,
			 *
			 *     Consider a map, we divide it at the length of indiceSideLength = r, and then its resourceMapIndicesColumnCount = a, resourceMapIndicesRowCount = b,
			 *     so the map.width ≈ a*r, map.height ≈ b*r,
			 *     the maximum euclid distance-square between two points on the map is (a*r)(a*r) + (b*r)(b*r),
			 *     so the maximum "weight of candidate's distance to current MCV" is from 0 to -((a*r)(a*r) + (b*r)(b*r)) / (a*a + b*b) = -r*r = -indiceSideLengthSquare.
			 *
			 *     b) if Mobile: range depends on pathfinding distance in cell.
			 *
			 *     It is calculated as "pathfindDistance * pathfindDistance / pathDistanceSquareFactor".
			 *
			 * 2). the weight of friendly construction yard within range: -indiceSideLengthSquare. If it belongs to an ally, -indiceSideLengthSquare/2.
			 *
			 * 3). the weight of enemy within range: -indiceSideLengthSquare*8 for base building, otherwise -indiceSideLengthSquare/64
			 *
			 * 4). the weight of friendly refinery within range (not for CheckBase mode): -indiceSideLengthSquare. If it belongs to an ally, -indiceSideLengthSquare/2.
			 *
			 * 5). the weight of resource amount (only for CheckResource mode): from 0 to +indiceSideLengthSquare/8.
			 *
			 *     The reason why:
			 *
			 *     The maximum resource amount in a indice of resource map is approximately indiceSideLengthSquare (full of it), but a stride full of resources is less likely to
			 *     have room for buildings. So we prefer the indice have half of resource cells the most, which may give us enough room to place buildings.
			 *
			 *     so the weight can be: (indiceSideLengthSquare/2) - |(indiceResourceCellCount - (indiceSideLengthSquare/2))|, range from (0 to +indiceSideLengthSquare/2).
			 *
			 *     Note: In practive resource weight is not very important, we cannot let MCV go a long way just for a rich resource spot.
			 *     We have to take only 1/4 of it, wich is (0 to +indiceSideLengthSquare/8),
			 *     and apply some additional method to filter the indice for acceptable resource (not too low).
			 */
			var indiceSideLengthSquare = resourceMapModule.GetIndiceSideLength() * resourceMapModule.GetIndiceSideLength();
			switch (mcvExpansionMode)
			{
				/*
				 * CheckBase mode only considers the distance to current MCV, ally construction yard within range and enemy buildings within range.
				 * Attaction has a base value of indiceSideLengthSquare >> 1 (1/2 of the maximum distance weight, 1/ sqrt(2) ≈ 1/1.4 of maximum euclid distance in map)
				 */
				case BotMcvExpansionMode.CheckBase:
					var cb_conyardlocs = world.ActorsHavingTrait<Building>()
						.Where(a => a.Owner.IsAlliedWith(player) && Info.ConstructionYardTypes.Contains(a.Info.Name))
						.Select(a => (a.Location, a.Owner == player))
						.ToArray();
					CPos? cb_suitablespot = null;
					CPos? cb_checkspot = null;
					var cb_best = int.MinValue;
					var ownBaseCenter = GetOwnBaseCenter();
					var enemyBaseCenter = GetEnemyBaseCenter();

					for (var i = 0; i < resourceMapModule.GetIndicesLength(); i++)
					{
						var indiceCenter = resourceMapModule.GetIndice(i).IndiceCenter;

						if (lastFailedCheckSpot == indiceCenter)
							continue;

						var attraction = indiceSideLengthSquare >> 1;

						attraction -= (indiceCenter - mcv.Location).LengthSquared / pathDistanceSquareFactor;

						attraction -= CalculateThreats(indiceSideLengthSquare, i);

						if (currentExpansionGoal == ExpansionGoal.DefenseOutpost && ownBaseCenter.HasValue && enemyBaseCenter.HasValue)
							attraction += CalculateFrontlineOutpostBonus(indiceCenter, ownBaseCenter.Value, enemyBaseCenter.Value);

						attraction -= CalculateExpansionCoordinationPenalty(indiceCenter, mcv, indiceSideLengthSquare);

						foreach (var (location, isAlly) in cb_conyardlocs)
						{
							var sdistance = (indiceCenter - location).LengthSquared;
							if (sdistance <= indiceSideLengthSquare)
							{
								if (isAlly)
									attraction -= indiceSideLengthSquare;
								else
									attraction -= indiceSideLengthSquare << 1;
							}
						}

						if (!allowfallback)
						{
							var sdistance = (indiceCenter - mcv.Location).LengthSquared;
							if (sdistance <= indiceSideLengthSquare)
								attraction -= indiceSideLengthSquare << 1;
						}

						if (attraction > cb_best)
						{
							cb_best = attraction;
							cb_checkspot = indiceCenter;
							cb_suitablespot = indiceCenter;
						}
					}

					return (cb_suitablespot ?? mcv.Location, cb_best, cb_checkspot);

				/*
				 * CheckResource mode considers the distance to current MCV, ally construction yard & refinery within range,
				 * Attaction has a base value of:
				 * 1. if not Mobile: indiceSideLengthSquare >> 2 (1/4 of the maximum distance weight, = 0.5 of the maximum euclid distance in map)
				 * 2. if Mobile: indiceSideLengthSquare >> 1 (1/2 of the maximum distance weight, ≈ 0.71 of the maximum euclid distance in map)
				 */
				case BotMcvExpansionMode.CheckResource:

					var cr_refinarylocs = world.ActorsHavingTrait<Refinery>()
						.Where(a => a.Owner == player && resourceMapModule.Info.RefineryTypes.Contains(a.Info.Name))
						.Select(a => (a.Location, a.Owner != player))
						.ToArray();

					var cr_conyardlocs = world.ActorsHavingTrait<Building>()
						.Where(a => a.Owner.IsAlliedWith(player) && Info.ConstructionYardTypes.Contains(a.Info.Name))
						.Select(a => (a.Location, a.Owner != player))
						.ToArray();

					// We only take indice has more than the half of average indice value (in weight calculation), to skip the indice with very poor resource
					// when failedAttempts is acceptable.
					var thresholdRes = 0;
					for (var i = 0; i < resourceMapModule.GetIndicesLength(); i++)
					{
						var resourceCellCounts = resourceMapModule.GetIndice(i).ResourceCellsCount;
						thresholdRes += (indiceSideLengthSquare >> 1) - Math.Abs(resourceCellCounts - (indiceSideLengthSquare >> 1));
					}

					thresholdRes = (thresholdRes / resourceMapModule.GetIndicesLength()) >> 1;

					CPos? cr_suitablespot = null;
					CPos? cr_checkspot = null;
					var cr_best = int.MinValue;

					for (var i = 0; i < resourceMapModule.GetIndicesLength(); i++)
					{
						var indice = resourceMapModule.GetIndice(i);
						var indiceCenter = indice.IndiceCenter;
						var resourceCellsCount = indice.ResourceCellsCount;
						var resourceCellsCenter = indice.ResourceCellsCenter;
						var resourceCreatorLocs = indice.ResourceCreatorLocs;

						if ((failedAttempts > maxFailedAttempts >> 1 && resourceCellsCount <= thresholdRes) || lastFailedCheckSpot == indiceCenter)
							continue;

						if (cnBaseBuilder != null && !cnBaseBuilder.CanSupportAnotherRefinery(indice))
							continue;

						var attraction = 0;
						if (mobile == null)
						{
							attraction = indiceSideLengthSquare >> 2;
							attraction -= (resourceCellsCenter - mcv.Location).LengthSquared / pathDistanceSquareFactor;
						}
						else
						{
							attraction = indiceSideLengthSquare >> 1;

							// Terrain-only pathfind for scoring — same as vanilla. Full safe path is computed in DeployMcv.
							var path = pathfinder.FindPathToTargetCells(mcv, mcv.Location, [resourceCellsCenter], BlockedByActor.None);

							if (path == PathFinder.NoPath)
								continue;

							attraction -= path.Count * path.Count / pathDistanceSquareFactor;
						}

						// it is better that resource cells takes only half of the indice cells, which give us the place to place building.
						attraction += ((indiceSideLengthSquare >> 1) - Math.Abs(resourceCellsCount - (indiceSideLengthSquare >> 1))) >> 2;
						attraction += 8 * resourceCreatorLocs.Length;

						if (indice.HasRespawningResourceSource)
							attraction += Info.CRmodeRespawningFieldBonus;

						if (indice.PlayerRefineryCount > 0)
							attraction -= indice.PlayerRefineryCount * Info.CRmodeExistingRefineryPenalty;

						var resCenter = resourceCreatorLocs.Length == 0 || world.LocalRandom.Next(2) > 0 ? resourceCellsCenter : resourceCreatorLocs.Random(world.LocalRandom);

						attraction -= CalculateThreats(indiceSideLengthSquare, i);

						attraction -= CalculateExpansionCoordinationPenalty(resCenter, mcv, indiceSideLengthSquare);

						foreach (var (location, isAlly) in cr_refinarylocs)
						{
							var sdistance = (resCenter - location).LengthSquared;
							if (sdistance <= Info.CRmodeFriendlyRefineryDislikeRange * Info.CRmodeFriendlyRefineryDislikeRange)
							{
								if (isAlly)
									attraction -= indiceSideLengthSquare;
								else
									attraction -= indiceSideLengthSquare << 1;
							}
						}

						foreach (var (location, isAlly) in cr_conyardlocs)
						{
							var sdistance = (resCenter - location).LengthSquared;
							if (sdistance <= Info.CRmodeFriendlyConyardDislikeRange * Info.CRmodeFriendlyConyardDislikeRange)
							{
								if (isAlly)
									attraction -= indiceSideLengthSquare;
								else
									attraction -= indiceSideLengthSquare << 1;
							}
						}

						if (!allowfallback)
						{
							var sdistance = (resCenter - mcv.Location).LengthSquared;
							if (sdistance <= Info.CRmodeFriendlyConyardDislikeRange * Info.CRmodeFriendlyConyardDislikeRange)
								attraction -= indiceSideLengthSquare << 1;
						}

						if (attraction > cr_best)
						{
							cr_best = attraction;
							cr_checkspot = indiceCenter;
							cr_suitablespot = resCenter;
						}
					}

					if (cr_suitablespot == null)
						return (null, int.MinValue, null);

					return (cr_suitablespot, cr_best, cr_checkspot);

				case BotMcvExpansionMode.CheckCurrentLocation:
					return (mcv.Location, int.MaxValue, null);

				default:
					return (null, int.MinValue, null);
			}
		}

		int CalculateThreats(int indiceSideLengthSquare, int index)
		{
			var baseIndice = resourceMapModule.GetIndice(index);

			var (indiceCount, nearbyEnemyThreat, nearbyEnemyBaseThreat) = resourceMapModule.GetNearbyIndicesThreat(index);

			var indiceEnemyBaseThreat = Math.Max(baseIndice.EnemyBaseCount - baseIndice.FriendlyBaseCount, 0);

			var indiceEnemyUnitThreat = Math.Max(baseIndice.EnemyUnitCount - baseIndice.FriendlyUnitCount, 0);

			if (indiceCount == 0)
				return (indiceEnemyUnitThreat * indiceSideLengthSquare >> 6) + (indiceEnemyBaseThreat * indiceSideLengthSquare << 3);

			return ((indiceEnemyUnitThreat * indiceSideLengthSquare + nearbyEnemyThreat * indiceSideLengthSquare / indiceCount) >> 6) +
							((indiceEnemyBaseThreat * indiceSideLengthSquare + nearbyEnemyBaseThreat * indiceSideLengthSquare / indiceCount) << 3);
		}

		void IBotTick.BotTick(IBot bot)
		{
			attackrespondcooldown--;

			if (firstTick)
			{
				resourceMapModule = bot.Player.PlayerActor.TraitsImplementing<CNResourceMapBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				profileModule = bot.Player.PlayerActor.TraitsImplementing<CNBotProfileBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				SwitchExpansionMode(Info.InitialExpansionMode);
				currentExpansionGoal = Info.InitialExpansionMode == BotMcvExpansionMode.CheckResource
					? ExpansionGoal.Economy : ExpansionGoal.BaseExtension;

				pathDistanceSquareFactor = resourceMapModule.GetIndiceRowCount() * resourceMapModule.GetIndiceRowCount()
					+ resourceMapModule.GetIndiceColumnCount() * resourceMapModule.GetIndiceColumnCount();

				DeployMcvs(bot, false);
				firstTick = false;
			}

			CleanupExpansionGoalLocks();

			if (--scanInterval <= 0)
			{
				foreach (var amcv in activeMCVs.Keys.ToList())
				{
					if (amcv.IsDead || !amcv.IsInWorld)
						activeMCVs.Remove(amcv);
				}

				foreach (var amcv in mcvRetryCooldown.Keys.ToList())
					if (amcv.IsDead || !amcv.IsInWorld)
						mcvRetryCooldown.Remove(amcv);

				scanInterval = Info.ScanForNewMcvInterval;
				DeployMcvs(bot, true);
			}

			if (--buildMCVInterval <= 0)
			{
				buildMCVInterval = Info.BuildMcvInterval;
				BuildMCV(bot);
			}

			if (--moveConyardInterval <= 0)
			{
				foreach (var amcv in activeMCVs.Keys.ToList())
				{
					if (amcv.IsDead || !amcv.IsInWorld)
						activeMCVs.Remove(amcv);
				}

				moveConyardInterval = Info.MoveConyardTick;
				UnDeployConyard(bot);
			}
		}

		void BuildMCV(IBot bot)
		{
			if (Info.McvTypes.Count <= 0)
				return;
			if (AIUtils.CountActorByCommonName(mcvFactories) <= 0)
				return;
			var mcvNum = AIUtils.CountActorByCommonName(mcvs);
			var conyardNum = AIUtils.CountActorByCommonName(constructionYards);

			var profileKey = profileModule != null ? profileModule.ActiveProfile.ToString() : null;
			var additionalCYCount = profileKey != null
				&& Info.AdditionalConstructionYardCounts != null
				&& Info.AdditionalConstructionYardCounts.TryGetValue(profileKey, out var profileAdditional)
				? profileAdditional : Info.AdditionalConstructionYardCount;
			var buildCashAmount = profileKey != null
				&& Info.BuildAdditionalMCVCashAmounts != null
				&& Info.BuildAdditionalMCVCashAmounts.TryGetValue(profileKey, out var profileCash)
				? profileCash : Info.BuildAdditionalMCVCashAmount;
			var mcvShouldHave = playerResources.GetCashAndResources() >= buildCashAmount
				? Info.MinimumConstructionYardCount + additionalCYCount : Info.MinimumConstructionYardCount;

			// If we only have 1 MCV and no conyard, we should be allowed to build another MCV.
			// Otherwise, when an mcv is on the move and we should wait.
			if ((conyardNum <= 0 && mcvNum > 1) || (conyardNum > 0 && mcvNum > 0))
				return;

			if (conyardNum + mcvNum >= mcvShouldHave)
				return;

			// We have MCV in production queue, let's wait.
			if (mcvFactories.Actors
				.Any(a => !a.IsDead && a.TraitsImplementing<ProductionQueue>().Any(t => t.Enabled && t.AllQueued().Any(q => Info.McvTypes.Contains(q.Item)))))
				return;

			// We have MCV in production queue, let's wait.
			if (player.PlayerActor.TraitsImplementing<ProductionQueue>()
				.Any(t => t.Enabled && t.AllQueued().Any(q => Info.McvTypes.Contains(q.Item))))
				return;
			var unitBuilder = requestUnitProduction.FirstEnabledTraitOrDefault();
			if (unitBuilder == null)
				return;
			var mcvType = Info.McvTypes.Random(world.LocalRandom);

			// Make sure we only request one MCV at a time.
			if (unitBuilder.RequestedProductionCount(bot, mcvType) <= 0)
				unitBuilder.RequestUnitProduction(bot, mcvType);
		}

		void DeployMcvs(IBot bot, bool chooseLocation)
		{
			// Refresh enemy base cache once per scan, shared across all MCVs and all pathfinds this tick.
			enemyBaseLocationsCache = GetKnownEnemyBaseLocations();

			// mcvs.Actors is a pre-built index for this player's MCV types — no world scan needed.
			foreach (var mcv in mcvs.Actors)
			{
				if (mcv.IsDead || !mcv.IsInWorld || !mcv.IsIdle)
					continue;
				if (mcvRetryCooldown.TryGetValue(mcv, out var retryTick) && world.WorldTick < retryTick)
					continue;
				DeployMcv(bot, mcv, chooseLocation);
			}
		}

		void UnDeployConyard(IBot bot)
		{
			if (mustUndeployCoyard != null && mustUndeployCoyard.IsInWorld && !mustUndeployCoyard.IsDead && mustUndeployCoyard.Owner == player)
			{
				if (IsExpansionGoalLocked(mustUndeployCoyard))
				{
					mustUndeployCoyard = null;
					return;
				}

				bot.QueueOrder(new Order("DeployTransform", mustUndeployCoyard, true));
				mustUndeployCoyard = null;

				return;
			}

			if (activeMCVs.Count > 0)
				return;

			var conyards = constructionYards.Actors
				.Where(a => !a.IsDead && !IsExpansionGoalLocked(a));

			var moveOldConyardFirst = Info.MoveOldConyardFirst ?? world.LocalRandom.Next(2) > 0;

			if (moveOldConyardFirst)
				conyards = conyards.OrderBy(a => a.ActorID);
			else
				conyards = conyards.OrderByDescending(a => a.ActorID);

			var conyardslist = conyards.ToList();

			if (conyardslist.Count > 1 || undeployEvenNoBase)
			{
				// We don't want to interrupt refinery production, otherwise it may cause a dead loop of deploy/undeploy.
				var movableMCV = conyardslist.FirstOrDefault(a => !a.TraitsImplementing<ProductionQueue>()
				.Any(t => t.Enabled && t.AllQueued().Any(q => resourceMapModule.Info.RefineryTypes.Contains(q.Item))));

				if (movableMCV != null)
					bot.QueueOrder(new Order("DeployTransform", movableMCV, true));

				undeployEvenNoBase = false;
			}
		}

		// Find any MCV and deploy them at a sensible location.
		void DeployMcv(IBot bot, Actor mcv, bool move)
		{
			CPos? desiredLocation = null;
			var deployAsOutpost = move && mcvExpansionMode == BotMcvExpansionMode.CheckBase;
			var transformsInfo = mcv.Info.TraitInfo<TransformsInfo>();
			var actorInfo = world.Map.Rules.Actors[transformsInfo.IntoActor];
			var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return;

			if (move)
			{
				var (deployLocation, resLoc, checkloc) = ChooseMcvDeployLocation(mcv, actorInfo, bi, transformsInfo.Offset, allowfallback);
				allowfallback = true;
				desiredLocation = deployLocation;
				if (desiredLocation == null)
				{
					mcvRetryCooldown[mcv] = world.WorldTick + 150;
					return;
				}

				var safePath = FindSafeMcvPath(mcv, mcv.Location, desiredLocation.Value);
				if (safePath == null)
				{
					FindBadDeploySpot(checkloc);
					mcvRetryCooldown[mcv] = world.WorldTick + 150;
					return;
				}

				var moveLocation = ChooseSafeMoveWaypoint(safePath, desiredLocation.Value);
				var movingToFinalDeployCell = moveLocation == desiredLocation.Value;

				activeMCVs[mcv] = checkloc;
				mcvRetryCooldown.Remove(mcv);
				if (movingToFinalDeployCell && resLoc != null)
				{
					foreach (var srp in suggestRefineryProduction)
						srp.RequestLocation(resLoc.Value, desiredLocation.Value, mcv);
				}

				bot.QueueOrder(new Order("Move", mcv, Target.FromCell(world, moveLocation), true));

				if (!movingToFinalDeployCell)
					return;
			}
			else
			{
				if (!world.CanPlaceBuilding(mcv.Location + transformsInfo.Offset, actorInfo, bi, mcv))
					return;
				desiredLocation = mcv.Location;
			}

			bot.QueueOrder(new Order("DeployTransform", mcv, true));
			TrackExpansionGoal(desiredLocation.Value);

			// When we don't have a construction yard, we notify the new location to other traits for defence,
			// If not, we only notify sometimes, because we are not sure if mcv can successfully deploy at the desired location.
			// TODO: This could be addressed via INotifyTransform.
			if (deployAsOutpost || constructionYards.Actors.All(a => a.IsDead) || world.LocalRandom.Next(2) > 0)
			{
				foreach (var n in notifyPositionsUpdated)
				{
					n.UpdatedBaseCenter(desiredLocation.Value);
					n.UpdatedDefenseCenter(desiredLocation.Value);
				}
			}
		}

		// First, find a suitable expansion location according to current mode,
		// Then, find a deployable cell around it.
		(CPos? DeployLoc, CPos? ResourceLoc, CPos? CheckLoc) ChooseMcvDeployLocation(
			Actor mcv,
			ActorInfo transformIntoInfo,
			BuildingInfo transformIntoBuildingInfo,
			CVec offset,
			bool allowfallback)
		{
			if (!mcv.Info.HasTraitInfo<IMoveInfo>())
				return (null, null, null);

			var mobile = mcv.TraitOrDefault<Mobile>();

			var (expandCenter, attraction, checkspot) = GetExpansionCenter(mcv, mobile, allowfallback);

			// Find the deployable cell
			CPos? FindDeployCell(CPos? sourceCell, CPos? targetCell, int minRange, int maxRange, int tryMaintainRange)
			{
				if (!sourceCell.HasValue || !targetCell.HasValue)
					return null;

				var target = targetCell.Value;
				var source = sourceCell.Value;

				var cells = world.Map.FindTilesInAnnulus(target, minRange, maxRange);

				/* First, sort the cells that keep tryMaintainRange to target (meanwhile direction is from center to target) the first to be considered
				 * by using following code. The idea is to use a linear combination of two distances-square for sorting weight.
				 *
				 * See comments in https://github.com/OpenRA/OpenRA/pull/22028#issuecomment-3242518793 for explaination.
				 */
				if (source != target)
				{
					var theta = tryMaintainRange;
					var deta = (target - source).Length - tryMaintainRange;
					cells = cells.OrderBy(c => deta * (c - target).LengthSquared + theta * (c - source).LengthSquared);
				}
				else
					cells = cells.Shuffle(world.LocalRandom);

				CPos? bestcell = null;
				foreach (var cell in cells)
				{
					if (world.CanPlaceBuilding(cell + offset, transformIntoInfo, transformIntoBuildingInfo, mcv))
					{
						bestcell = cell;
						break;
					}
				}

				// If no deployble cell found, return null
				if (bestcell == null)
					return null;

				if (source != target && mobile != null && !pathfinder.PathMightExistForLocomotorBlockedByImmovable(mobile.Locomotor, source, bestcell.Value))
					bestcell = null;

				// If the best deploy cell is not ideal ( >= tryMaintainRange + 2), which means there might be some huge blockers
				// so we fall back to default behavior, which is the directly closest cell to target
				if (!bestcell.HasValue || (source != target && (bestcell.Value - target).LengthSquared >= (tryMaintainRange + 2) * (tryMaintainRange + 2)))
				{
					cells = cells.OrderBy(c => (c - target).LengthSquared);
					foreach (var cell in cells)
					{
						if (world.CanPlaceBuilding(cell + offset, transformIntoInfo, transformIntoBuildingInfo, mcv))
						{
							if (mobile != null && !pathfinder.PathMightExistForLocomotorBlockedByImmovable(mobile.Locomotor, source, cell))
								return null;

							return (!bestcell.HasValue) || (cell - target).LengthSquared < (bestcell.Value - target).LengthSquared ? cell : bestcell;
						}
					}
				}

				return bestcell;
			}

			var bc = FindDeployCell(mcv.Location, expandCenter, mcvDeploymentMinDeployRadius, mcvDeploymentMaxDeployRadius, mcvDeploymentTryMaintainRange);

			// At last, if the attraction of the found expansion location is good enough (>0) and deploy cell found,
			// we consider it as a good expansion, otherwise, we consider it as a bad expansion.
			if (bc.HasValue && attraction > 0)
				FindGoodDeploySpot();
			else
				FindBadDeploySpot(bc.HasValue ? null : checkspot);

			if (mcvExpansionMode == BotMcvExpansionMode.CheckResource && expandCenter.HasValue && bc.HasValue)
				return (bc, expandCenter, checkspot);

			return (bc, null, checkspot);
		}

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (attackrespondcooldown <= 0 && Info.McvTypes.Contains(self.Info.Name))
			{
				attackrespondcooldown = 20;

				DeployMcv(bot, self, false);

				if (AIUtils.CountActorByCommonName(constructionYards) == 0)
				{
					foreach (var n in notifyPositionsUpdated)
						n.UpdatedBaseCenter(self.Location);
				}
			}
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			mcvs.Dispose();
			constructionYards.Dispose();
			mcvFactories.Dispose();
		}

		void IBotBaseExpansion.UpdateExpansionParams(IBot bot, bool fallback, bool undeployEvenNoBase, Actor mustUndeploy)
		{
			moveConyardInterval = 20; // allow some order latency
			allowfallback = fallback;
			this.undeployEvenNoBase = undeployEvenNoBase;
			mustUndeployCoyard = mustUndeploy;

			if (mustUndeploy != null)
				SetExpansionGoal(ExpansionGoal.BaseExtension);
			else if (Info.EnableStrategicOutposts && fallback && (cnBaseBuilder?.HasAdequateRefineryCount() ?? false))
				SetExpansionGoal(ExpansionGoal.DefenseOutpost);
			else if (fallback && !(cnBaseBuilder?.ShouldExpandEconomy() ?? true))
				SetExpansionGoal(ExpansionGoal.BaseExtension);
			else
				SetExpansionGoal(ExpansionGoal.Economy);
		}
	}
}
