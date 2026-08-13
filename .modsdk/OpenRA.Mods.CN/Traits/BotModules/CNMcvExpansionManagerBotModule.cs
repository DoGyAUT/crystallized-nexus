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
using OpenRA.Mods.CN.Traits.BotModules;
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

		[Desc("Resource cells left across all fields the bot works, at or below which it counts as starving " +
			"and expands regardless of cash. Its income is about to stop, and the cash thresholds that " +
			"normally gate expansion can never be met once it has. 0 disables the starvation trigger.",
			"Means 'running low', not 'empty'. At 40 - halved to 20 by the regrowth rule, which nearly " +
			"every Tiberian Sun field triggers - a played match logged bots sitting on 37 and 63 cells " +
			"with no cash at all and still not qualifying. By the time a field is down to twenty cells " +
			"the expansion needed to arrive minutes ago.")]
		public readonly int StarvationFieldCells = 120;

		[Desc("Percent of StarvationFieldCells used as the threshold when a worked field has a seeding " +
			"tree in it. Lower rather than zero: regrowth is not inexhaustible, and a field mined faster " +
			"than it seeds runs its owner dry all the same.")]
		public readonly int StarvationRespawningPercent = 50;

		[Desc("Extra construction yards a starving bot may found beyond its configured ceiling. Bounded " +
			"rather than unlimited: a profile set to two yards should not grow without end, but it must " +
			"not be trapped on a dead field either.")]
		public readonly int StarvationExtraConstructionYards = 2;

		[Desc("Also expand when a genuinely good free spot exists, instead of only when the bot is rich.",
			"The construction yard counts above stay in force as the ceiling.")]
		public readonly bool EnableOpportunityExpansion = true;

		[Desc("Cash required for an expansion triggered by a good spot rather than by " + nameof(BuildAdditionalMCVCashAmount) + ".",
			"Should cover the MCV itself plus a little headroom.")]
		public readonly int OpportunityExpansionCashAmount = 3500;

		[Desc("How attractive a free spot has to be to trigger an expansion, in per-mille of one resource map",
			"indice's area. Map-portable: 0 means the spot has to score net positive (resources there, no own",
			"construction yard or refinery in range, no enemy nearby, path not absurd), higher demands more.")]
		public readonly int ExpansionAttractionThresholdPermille = 0;

		[Desc("Per-profile override of " + nameof(ExpansionAttractionThresholdPermille) + ".")]
		public readonly FrozenDictionary<string, int> ExpansionAttractionThresholdsPermille = null;

		[Desc("Delay (in ticks) for giving orders to idle MCVs.")]
		public readonly int ScanForNewMcvInterval = 20;

		[Desc("Delay (in ticks) for checking and building a MCV.")]
		public readonly int BuildMcvInterval = 101;

		[Desc("Delay (in ticks) for moving a conyard to better expansion. Only work with more than 1 conyard.")]
		public readonly int MoveConyardTick = 5700;

		[Desc("Should moving the oldest or newest conyard be preferred? Random ordering if unset.")]
		public readonly bool? MoveOldConyardFirst = null;

		[Desc("A construction yard may only pack up and drive off while the base it is the sole build source",
			"of has at most this many buildings. Beyond that, relocating it abandons an established base:",
			"the stock stays behind as dead weight the bot can no longer build around, and it continues from",
			"the new, tiny site instead. -1 disables the check.")]
		public readonly int RelocateConyardMaxBaseSize = 12;

		[Desc("When economy is already covered, prefer CheckBase expansions as forward outposts instead of only resource expansions.")]
		public readonly bool EnableStrategicOutposts = true;

		[Desc("Cells around the starting MCV searched for tiberium before deciding whether to deploy on",
			"the spot or walk to a field first.",
			"This is a last-resort test for a spawn with no workable tiberium at all, not a judgement of",
			"how good the spawn is. Map authors place spawns deliberately, and a central one with fields",
			"fifteen or twenty cells out is a fine start — walking away from it to sit closer to one",
			"field trades a good position for a worse one. Keep this wide enough that only a genuinely",
			"stranded spawn fails it.")]
		public readonly int StartDeployResourceSearchRadius = 25;

		[Desc("Valuable resource cells that must lie within " + nameof(StartDeployResourceSearchRadius),
			"for the starting MCV to deploy where it stands. Below this it looks for a field instead —",
			"the one relocation decision the bot can never revisit later.")]
		public readonly int StartDeployMinResourceCells = 12;

		[Desc("How long a travelling MCV keeps the deploy cell it picked. Expansion scoring is relative to",
			"the MCV's own position, so the ranking of candidate fields shifts as it drives; re-picking",
			"every scan makes it change its mind mid-journey and arc across the map. The expiry only",
			"exists so an MCV that cannot reach its choice eventually reconsiders.")]
		public readonly int McvDeployGoalHoldTicks = 1500;

		[Desc("Cells of clearance kept between a deploying construction yard and valuable resource cells.",
			"CRmodeTryMaintainRange measures to the centre of a field, which on a large one still lands",
			"inside it, so this is what actually keeps the yard out of the tiberium.",
			"Sized to leave a lane free between yard and field rather than to merely clear the edge:",
			"the refinery belongs in that lane, and a yard parked against the field takes the cells it",
			"needs. 0 disables the clearance.")]
		public readonly int McvResourceClearance = 6;

		[Desc("How many MCVs may be travelling at the same time. One means expansions are founded strictly",
			"one after another — build, drive, deploy, only then the next — which is what a land-grab",
			"profile cannot afford. Ignored while the bot has no construction yard: rebuilding a base",
			"never needs more than one spare.")]
		public readonly int MaxConcurrentMcvs = 1;

		[Desc("Per-profile override for " + nameof(MaxConcurrentMcvs) + ".")]
		public readonly FrozenDictionary<string, int> MaxConcurrentMcvCounts = null;

		[Desc("Cells around a candidate deploy cell examined for usable building ground. The deploy check",
			"itself only asks whether the construction yard fits, which a ledge between a cliff and a",
			"tiberium field satisfies while offering nowhere to put the rest of the base.")]
		public readonly int DeploySiteCheckRadius = 8;

		[Desc("Flat, buildable cells that must lie within " + nameof(DeploySiteCheckRadius) + " for a",
			"deploy cell to count as a viable base site. A candidate below this is skipped in favour of",
			"the next one; if no candidate clears it, the best available cell is used anyway rather than",
			"leaving the MCV wandering.")]
		public readonly int DeploySiteMinBuildableCells = 60;

		[Desc("Percent of the straight line from a deploy candidate to its tiberium field that may be",
			"undriveable before the candidate is passed over. Straight-line closeness to the field is",
			"what ranks candidates, and under a cliff is as close as it gets - so without this a yard",
			"lands below the terrace its tiberium sits on and every haul pays for it. Only candidates",
			"that clear it are considered; if none can be built on, the full list is used anyway rather",
			"than leaving the MCV wandering.")]
		public readonly int DeployMaxBlockedLinePercent = 20;

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

		[Desc("How much a perfect region adds to a candidate's attraction, as a percentage of the indice-side",
			"square every other term is measured in. " + nameof(CNRegionManagerBotModule) + " scores each",
			"region on its resources, its buildable space and how many ways in it has - the same three",
			"questions this module answers per raster square, asked of the ground's actual shape instead.",
			"A candidate in a region scoring 100 gets this much; one in a region scoring 0 gets nothing.",
			"Deliberately below the distance term's reach, so a good region tilts a decision without",
			"sending an MCV across the map for it. 0 disables. Nothing happens when the region graph is",
			"unavailable, so a map it cannot read behaves exactly as before.")]
		public readonly int RegionValueAttractionPercent = 25;

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
		// Samples along the line from a prospective yard site to the field. Enough to tell a cliff from
		// a gap, and cheap enough to run over every deploy candidate.
		const int DeployLineSamples = 20;

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

		// Deploy cell a travelling MCV has committed to, with an expiry as a safety net.
		readonly Dictionary<Actor, (CPos Cell, int UntilTick)> mcvDeployGoals = [];
		readonly Dictionary<CPos, (ExpansionGoal Goal, int UntilTick)> pendingExpansionGoalLocks = [];
		readonly Dictionary<Actor, (ExpansionGoal Goal, int UntilTick, CPos DeployCell)> conyardExpansionGoalLocks = [];

		CPos[] enemyBaseLocationsCache = [];

		PathFinder pathfinder;
		CNResourceMapBotModule resourceMapModule;
		CNRegionManagerBotModule regionManagerModule;
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

			// Projection/rejection are computed in WORLD space, not cell space. On the RectangularIsometric
			// grid a CVec's X and Y are not the same physical length, so doing the dot/cross product on raw
			// cell coordinates skewed the corridor: CBmodeOutpostCorridorWidth meant something different
			// along the north-south axis than along east-west, and "58% of the way to the enemy" landed
			// somewhere else entirely depending on which way the two bases happened to lie.
			var map = world.Map;
			var friendlyPos = map.CenterOfCell(friendlyBaseCenter);
			var enemyPos = map.CenterOfCell(enemyBaseCenter);
			var candidatePos = map.CenterOfCell(candidate);

			var axis = enemyPos - friendlyPos;
			var lenSq = (long)axis.X * axis.X + (long)axis.Y * axis.Y;
			if (lenSq <= 0)
				return 0;

			var toCandidate = candidatePos - friendlyPos;
			var dot = (long)toCandidate.X * axis.X + (long)toCandidate.Y * axis.Y;
			if (dot <= 0 || dot >= lenSq)
				return 0;

			var progress = (int)(dot * 100 / lenSq);
			var preferredProgress = Info.CBmodeOutpostPreferredProgress.Clamp(1, 99);
			var progressTolerance = Math.Max(1, Info.CBmodeOutpostProgressTolerance);
			var progressDelta = Math.Abs(progress - preferredProgress);
			if (progressDelta > progressTolerance)
				return 0;

			// Corridor width converted to world units so the comparison stays in one metric. The
			// perpendicular offset is |cross| / |axis| — divided before squaring, because at world
			// scale cross itself already reaches ~4e10 and cross * cross would overflow a long.
			var corridorWidth = WDist.FromCells(Math.Max(1, Info.CBmodeOutpostCorridorWidth)).Length;
			var corridorWidthSq = (long)corridorWidth * corridorWidth;
			var cross = (long)toCandidate.X * axis.Y - (long)toCandidate.Y * axis.X;
			var perpendicularDist = Math.Abs(cross) / Math.Max(1, axis.HorizontalLength);
			var perpendicularDistSq = perpendicularDist * perpendicularDist;
			if (perpendicularDistSq > corridorWidthSq)
				return 0;

			var enemyMinRange = WDist.FromCells(Math.Max(0, Info.CBmodeOutpostEnemyMinRange));
			if (enemyMinRange.Length > 0 &&
				(candidatePos - enemyPos).HorizontalLengthSquared < (long)enemyMinRange.Length * enemyMinRange.Length)
				return 0;

			var friendlyMinRange = WDist.FromCells(Math.Max(0, Info.CBmodeOutpostFriendlyMinRange));
			if (friendlyMinRange.Length > 0 &&
				(candidatePos - friendlyPos).HorizontalLengthSquared < (long)friendlyMinRange.Length * friendlyMinRange.Length)
				return 0;

			var progressScore = (progressTolerance - progressDelta) * Info.CBmodeFrontlineOutpostBonus / progressTolerance;
			var corridorScore = (int)((corridorWidthSq - perpendicularDistSq) * Info.CBmodeFrontlineOutpostBonus / corridorWidthSq);

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

		/// <summary>
		/// The safe route as a list of move waypoints, source-first, ending on the deploy cell.
		/// <para>
		/// Waypoints exist so a plain Move order cannot re-route the MCV across a whole enemy base:
		/// each leg is short enough that the engine's own pathing stays on the corridor that
		/// FindSafeMcvPath approved. They are all handed over at once — issuing one per scan left the
		/// MCV standing at every leg's end until the next scan came round, which is the stop-start
		/// crawl seen in testing.
		/// </para>
		/// </summary>
		IEnumerable<CPos> BuildSafeWaypoints(List<CPos> path, CPos finalCell)
		{
			if (path != null && path.Count > 0)
			{
				// Paths are returned reversed, target -> source, so walk backwards to travel outward.
				var maxStep = Math.Max(1, Info.McvSafeMoveWaypointPathCells);
				for (var i = path.Count - 1 - maxStep; i > 0; i -= maxStep)
					yield return path[i];
			}

			yield return finalCell;
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

		public (CPos? ExpandLocation, int Attraction, CPos? CheckSpot) GetExpansionCenter(Actor mcv, Mobile mobile, bool allowfallback,
			BotMcvExpansionMode? modeOverride = null)
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
			switch (modeOverride ?? mcvExpansionMode)
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

						attraction += CalculateRegionValueBonus(indiceCenter, indiceSideLengthSquare);

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
						.Where(a => a.Owner.IsAlliedWith(player) && resourceMapModule.Info.RefineryTypes.Contains(a.Info.Name))
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

						var threatPenalty = CalculateThreats(indiceSideLengthSquare, i);
						attraction -= threatPenalty;

						// Scored at the resource centre rather than the indice centre: that is the ground the
						// MCV is actually being sent to work, and the two can sit in different regions when a
						// raster square straddles a boundary.
						attraction += CalculateRegionValueBonus(resourceCellsCenter, indiceSideLengthSquare);

						var coordinationPenalty = CalculateExpansionCoordinationPenalty(resCenter, mcv, indiceSideLengthSquare);
						attraction -= coordinationPenalty;

						var occupancyBefore = attraction;

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

						// Why one field beats another is impossible to read from the outside: the terms are
						// path length, threat, coordination with other MCVs and how crowded the field
						// already is. Logged per candidate so a surprising choice can be traced to the
						// term that caused it.
						CNBotLog.Debug(
							"{0} mcv field {1}: attraction {2} (cells {3}, threat -{4}, coordination -{5}, occupancy -{6})",
							player, indiceCenter, attraction, resourceCellsCount,
							threatPenalty, coordinationPenalty, occupancyBefore - attraction);

						if (attraction > cr_best)
						{
							cr_best = attraction;
							cr_checkspot = indiceCenter;
							cr_suitablespot = resCenter;
						}
					}

					if (cr_suitablespot == null)
						return (null, int.MinValue, null);

					CNBotLog.Debug("{0} mcv chose field {1} with attraction {2}", player, cr_checkspot, cr_best);

					return (cr_suitablespot, cr_best, cr_checkspot);

				case BotMcvExpansionMode.CheckCurrentLocation:
					return (mcv.Location, int.MaxValue, null);

				default:
					return (null, int.MinValue, null);
			}
		}

		/// <summary>
		/// What the region a candidate stands in is worth, in the same currency as every other attraction
		/// term (see the block comment in <see cref="GetExpansionCenter"/>): a share of
		/// <paramref name="indiceSideLengthSquare"/>, scaled by the region's 0-100 score.
		/// <para>
		/// The indice terms above ask "how much tiberium is in this raster square" and "is there room to
		/// build in it". A region asks the same of the ground's actual shape, and adds the question neither
		/// can reach - how many ways into it there are. It overlaps the resource term on purpose rather
		/// than replacing it: one measures the square an MCV would deploy in, the other the pocket it would
		/// be committing to.
		/// </para>
		/// Zero whenever the region graph is unavailable, so nothing changes on a map it cannot read.
		/// </summary>
		int CalculateRegionValueBonus(CPos cell, int indiceSideLengthSquare)
		{
			if (regionManagerModule == null || !regionManagerModule.Ready || Info.RegionValueAttractionPercent <= 0)
				return 0;

			var state = regionManagerModule.GetRegionStateAt(cell);
			if (state == null)
				return 0;

			return state.Value * indiceSideLengthSquare * Info.RegionValueAttractionPercent / (100 * 100);
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
				regionManagerModule = bot.Player.PlayerActor.TraitsImplementing<CNRegionManagerBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				SwitchExpansionMode(Info.InitialExpansionMode);
				currentExpansionGoal = Info.InitialExpansionMode == BotMcvExpansionMode.CheckResource
					? ExpansionGoal.Economy : ExpansionGoal.BaseExtension;

				// Requires<CNResourceMapBotModuleInfo> only guarantees the trait exists, not that it is
				// enabled — a RequiresCondition on the resource map used to take this straight into a
				// NullReferenceException on the very first bot tick. Stay dormant until it comes up
				// instead: every expansion decision below is derived from the resource map.
				if (resourceMapModule == null)
					return;

				// Wait for the resource map's initial sweep to finish before deciding what to do with the
				// starting MCV. That sweep is amortised over a few ticks, so on the very first one the
				// bot knows almost nothing about where the tiberium is — and this is the one decision
				// it can never revisit: UnDeployConyard needs either a second construction yard or an
				// established economy, so a yard planted out of harvester reach stays there.
				if (!resourceMapModule.InitialScanComplete)
					return;

				pathDistanceSquareFactor = resourceMapModule.GetIndiceRowCount() * resourceMapModule.GetIndiceRowCount()
					+ resourceMapModule.GetIndiceColumnCount() * resourceMapModule.GetIndiceColumnCount();

				// Normally the starting MCV deploys where it stands: the opening is worth more than a
				// better spot. Only when the spawn has no worthwhile tiberium within reach is it worth
				// walking first, because everything downstream — refinery placement, harvester range,
				// the refinery target itself — is built on having a field near the base.
				DeployMcvs(bot, !HasWorthwhileResourcesAtStart());
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

				// A deployed MCV leaves the world, which is also how a commitment is retired.
				foreach (var amcv in mcvDeployGoals.Keys.ToList())
					if (amcv.IsDead || !amcv.IsInWorld)
						mcvDeployGoals.Remove(amcv);

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

		// Is there a free spot worth sending an MCV to? Scored with the very same function the MCV itself
		// uses to pick its target, just anchored on an existing construction yard and without a Mobile, so
		// it costs one pass over the resource indices and no pathfinding.
		bool HasAttractiveExpansionSpot(string profileKey)
		{
			if (resourceMapModule == null || resourceMapModule.GetIndicesLength() <= 0)
				return false;

			var reference = constructionYards.Actors.FirstOrDefault(a => !a.IsDead && a.IsInWorld);
			if (reference == null)
				return false;

			var (_, attraction, _) = GetExpansionCenter(reference, null, false, BotMcvExpansionMode.CheckResource);
			if (attraction == int.MinValue)
				return false;

			var permille = profileKey != null
				&& Info.ExpansionAttractionThresholdsPermille != null
				&& Info.ExpansionAttractionThresholdsPermille.TryGetValue(profileKey, out var profilePermille)
				? profilePermille : Info.ExpansionAttractionThresholdPermille;

			var indiceSideLength = resourceMapModule.GetIndiceSideLength();
			return attraction >= permille * indiceSideLength * indiceSideLength / 1000;
		}

		/// <summary>
		/// True when the fields the bot actually works are nearly mined out, so its income is about to
		/// stop whatever else it does.
		/// <para>
		/// Only worked fields count — tiberium elsewhere on the map is exactly what expanding is for, so
		/// counting it here would mask the very condition being tested.
		/// </para>
		/// A blossom tree in the field lowers the bar rather than removing the check. Treating "regrows"
		/// as "never starves" made this dead on arrival: nearly every Tiberian Sun field has a seeding
		/// tree, so the first version never once reported starvation in a played match. Regrowth is not
		/// inexhaustible either - a field mined faster than it seeds runs its owner dry regardless.
		/// </summary>
		public bool IsResourceStarved()
		{
			if (resourceMapModule == null || Info.StarvationFieldCells <= 0)
				return false;

			var worked = 0;
			var cells = 0;
			var respawning = false;
			for (var i = 0; i < resourceMapModule.GetIndicesLength(); i++)
			{
				var indice = resourceMapModule.GetIndice(i);
				if (indice == null || indice.PlayerRefineryCount <= 0)
					continue;

				worked++;
				cells += indice.ResourceCellsCount;
				respawning |= indice.HasRespawningResourceSource;
			}

			// No worked field at all is a different situation - the bot has not started mining yet, or
			// just lost its refineries - and neither is answered by founding another base.
			if (worked <= 0)
				return false;

			var threshold = respawning
				? Info.StarvationFieldCells * Math.Clamp(Info.StarvationRespawningPercent, 0, 100) / 100
				: Info.StarvationFieldCells;

			starvationReport = $"{cells} cells over {worked} field(s), threshold {threshold}{(respawning ? ", regrowing" : "")}";
			return cells <= threshold;
		}

		// Last computed starvation inputs, for the expansion log. The verdict alone was not enough to
		// tell "the fields are still full" from "the check disqualified itself" - which is exactly how
		// the regrowth bug above survived a whole match unnoticed.
		string starvationReport = "not evaluated";

		void BuildMCV(IBot bot)
		{
			if (Info.McvTypes.Count <= 0)
				return;
			if (AIUtils.CountActorByCommonName(mcvFactories) <= 0)
				return;
			var mcvNum = AIUtils.CountActorByCommonName(mcvs);
			var conyardNum = AIUtils.CountActorByCommonName(constructionYards);

			var profileKey = profileModule?.ActiveProfile.ToString();
			var additionalCYCount = profileKey != null
				&& Info.AdditionalConstructionYardCounts != null
				&& Info.AdditionalConstructionYardCounts.TryGetValue(profileKey, out var profileAdditional)
				? profileAdditional : Info.AdditionalConstructionYardCount;
			var buildCashAmount = profileKey != null
				&& Info.BuildAdditionalMCVCashAmounts != null
				&& Info.BuildAdditionalMCVCashAmounts.TryGetValue(profileKey, out var profileCash)
				? profileCash : Info.BuildAdditionalMCVCashAmount;

			var maxConcurrentMcvs = profileKey != null
				&& Info.MaxConcurrentMcvCounts != null
				&& Info.MaxConcurrentMcvCounts.TryGetValue(profileKey, out var profileConcurrent)
				? profileConcurrent : Info.MaxConcurrentMcvs;

			// With no construction yard the bot is rebuilding its base and one spare MCV is all that
			// helps. With one, the cap is how many expansions may be under way at once — at 1 (the
			// default, and the old hardcoded behaviour) every expansion waits for the previous MCV to
			// finish driving and deploying, which is what kept a land-grab profile crawling.
			if (conyardNum <= 0 ? mcvNum > 1 : mcvNum >= Math.Max(1, maxConcurrentMcvs))
				return;

			// Running out of tiberium is its own reason to expand, and it used to be no reason at all.
			// Both gates below ask about cash and yard counts; neither asks whether the ground the bot
			// stands on still has anything in it. A bot that had mined its spawn dry therefore kept
			// building at home until it lost, and its opponent took the free fields uncontested.
			var starving = IsResourceStarved();

			// The construction yard count is now purely a ceiling, not the trigger. Before, it only rose
			// above the minimum once the bot was sitting on BuildAdditionalMCVCashAmount, so profiles with
			// no additional yards configured never expanded however good a free spot was, and the rest only
			// expanded when rich enough to have hoarded five figures.
			//
			// Starvation lifts the ceiling by a bounded amount rather than removing it: a profile
			// configured for two yards should not grow without limit, but it must not be trapped on a
			// dead field either.
			var yardCeiling = Info.MinimumConstructionYardCount + additionalCYCount;
			if (starving)
				yardCeiling += Math.Max(0, Info.StarvationExtraConstructionYards);

			if (conyardNum + mcvNum >= yardCeiling)
				return;

			// Replacing a lost base is unconditional; expanding beyond the minimum needs a reason.
			if (conyardNum + mcvNum >= Info.MinimumConstructionYardCount)
			{
				var cash = playerResources.GetCashAndResources();
				var richEnough = cash >= buildCashAmount;
				var opportunity = Info.EnableOpportunityExpansion
					&& cash >= Info.OpportunityExpansionCashAmount
					&& HasAttractiveExpansionSpot(profileKey);

				// Starvation deliberately skips the cash bar. Those thresholds exist so a comfortable bot
				// spends its surplus on ground; a starving one has no surplus and never will again,
				// because the income that would produce it is exactly what has run out.
				if (!richEnough && !opportunity && !starving)
				{
					CNBotLog.Debug("{0} expansion held: cash {1} (needs {2} or {3}+spot), yards {4}/{5}, starving {6} ({7})",
						player, cash, buildCashAmount, Info.OpportunityExpansionCashAmount,
						conyardNum + mcvNum, yardCeiling, starving, starvationReport);
					return;
				}

				CNBotLog.Debug("{0} expansion approved: cash {1}, yards {2}/{3}, rich {4}, opportunity {5}, starving {6} ({7})",
					player, cash, conyardNum + mcvNum, yardCeiling, richEnough, opportunity, starving, starvationReport);
			}

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

		// True while this construction yard is the sole build source of a base that has outgrown
		// RelocateConyardMaxBaseSize. Moving it then leaves the whole base behind: without a yard the group
		// is no longer a build site (see CNBotBase.IsBuildSite), so the bot carries on around the handful of
		// buildings at the new spot while the old stock sits there as dead weight.
		bool WouldAbandonEstablishedBase(Actor conyard)
		{
			if (cnBaseBuilder == null || Info.RelocateConyardMaxBaseSize < 0)
				return false;

			foreach (var b in cnBaseBuilder.GetBases())
			{
				if (!b.ConstructionYards.Contains(conyard))
					continue;

				// Another yard in the same base keeps building there after this one leaves.
				return b.ConstructionYards.Count <= 1 && b.Buildings.Count > Info.RelocateConyardMaxBaseSize;
			}

			return false;
		}

		void UnDeployConyard(IBot bot)
		{
			if (mustUndeployCoyard != null && mustUndeployCoyard.IsInWorld && !mustUndeployCoyard.IsDead && mustUndeployCoyard.Owner == player)
			{
				// The stuck-base recovery in CNBaseBuilderQueueManager nominates a yard whenever placement
				// keeps failing around it - which is exactly what a FULL base looks like. Relocating out of
				// one would abandon the very base that ran out of room, so it is refused here too.
				if (IsExpansionGoalLocked(mustUndeployCoyard) || WouldAbandonEstablishedBase(mustUndeployCoyard))
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
				// And a yard that is the only build source of an established base does not leave it at all.
				var movableMCV = conyardslist.FirstOrDefault(a => !a.TraitsImplementing<ProductionQueue>()
				.Any(t => t.Enabled && t.AllQueued().Any(q => resourceMapModule.Info.RefineryTypes.Contains(q.Item)))
				&& !WouldAbandonEstablishedBase(a));

				if (movableMCV != null)
					bot.QueueOrder(new Order("DeployTransform", movableMCV, true));

				undeployEvenNoBase = false;
			}
		}

		/// <summary>
		/// Flat, buildable ground around a candidate deploy cell — a proxy for "is there room for a base
		/// here at all". The deploy check only asks whether the construction yard itself fits, which a
		/// ledge wedged between a cliff and a tiberium field passes while leaving nowhere for the
		/// refinery, the power plants or the factories that have to follow.
		/// </summary>
		/// <summary>
		/// True if the cell an MCV committed to is still worth driving to: on the map, still free for
		/// the yard, and still reachable. Deliberately the cheap reachability test — the expensive,
		/// threat-aware routing runs once per scan anyway and drops the commitment itself if it fails.
		/// </summary>
		bool CanStillDeployAt(Actor mcv, ActorInfo actorInfo, BuildingInfo bi, CVec offset, CPos cell)
		{
			if (!world.Map.Contains(cell))
				return false;

			if (!world.CanPlaceBuilding(cell + offset, actorInfo, bi, mcv))
				return false;

			var mobile = mcv.TraitOrDefault<Mobile>();
			return mobile == null
				|| pathfinder.PathMightExistForLocomotorBlockedByImmovable(mobile.Locomotor, mcv.Location, cell);
		}

		/// <summary>
		/// True if a construction yard placed here would sit on or right against tiberium. The yard is
		/// the one building guaranteed to be surrounded by others later, so planting it in a field
		/// costs that ground twice: once for the yard's own footprint and again for everything the base
		/// grid then packs around it — including the refinery, which wants exactly those cells.
		/// </summary>
		bool IsTooCloseToResources(CPos cell, BuildingInfo bi)
		{
			if (Info.McvResourceClearance <= 0 || resourceMapModule == null)
				return false;

			// Footprint half-extent plus the clearance: CountValuableResourceCellsNear measures from a
			// single cell, so the yard's own size has to be folded into the radius.
			var dims = bi?.Dimensions ?? new CVec(1, 1);
			var footprintReach = (Math.Max(dims.X, dims.Y) + 1) / 2;

			return resourceMapModule.CountValuableResourceCellsNear(cell, footprintReach + Info.McvResourceClearance) > 0;
		}

		/// <summary>
		/// Whether a harvester could drive the straight line from a prospective yard site to the field,
		/// sampled rather than pathed. A full path would answer "yes" for a site under a cliff too - the
		/// road exists, it just runs the long way round, and that is precisely the case being excluded.
		/// What separates the two is whether the direct line is driveable at all.
		/// </summary>
		bool LineToFieldIsDrivable(CPos from, CPos to)
		{
			var locomotors = cnBaseBuilder?.HarvesterLocomotorsList;
			if (locomotors == null || locomotors.Length == 0)
				return true;

			var straight = (to - from).Length;
			if (straight <= 0)
				return true;

			var samples = Math.Min(straight, DeployLineSamples);
			var blocked = 0;
			for (var i = 1; i <= samples; i++)
			{
				var step = new CPos(
					from.X + (to.X - from.X) * i / samples,
					from.Y + (to.Y - from.Y) * i / samples);

				if (!world.Map.Contains(step)
					|| !locomotors.All(l => l.MovementCostForCell(step) != PathGraph.MovementCostForUnreachableCell))
					blocked++;
			}

			return blocked * 100 / samples <= Info.DeployMaxBlockedLinePercent;
		}

		int CountBuildableCellsAround(CPos center, BuildingInfo bi)
		{
			var radius = Math.Max(1, Info.DeploySiteCheckRadius);
			var count = 0;

			foreach (var cell in world.Map.FindTilesInAnnulus(center, 0, radius))
			{
				if (!world.Map.Contains(cell))
					continue;

				// Ramps are the important exclusion: they read as ordinary terrain but nothing can be
				// built on them, so a slope-heavy pocket looks far better than it plays.
				if (world.Map.Ramp[cell] != 0)
					continue;

				if (bi != null && !bi.TerrainTypes.Contains(world.Map.GetTerrainInfo(cell).Type))
					continue;

				count++;
			}

			return count;
		}

		/// <summary>
		/// True if any starting MCV stands within reach of enough tiberium to be worth deploying on the
		/// spot. With none, deploying in place strands the base: harvesters haul across the map, the
		/// refinery placement fallback has no field to aim at, and no later mechanism moves the yard.
		/// </summary>
		bool HasWorthwhileResourcesAtStart()
		{
			if (resourceMapModule == null)
				return true;

			var radius = Math.Max(1, Info.StartDeployResourceSearchRadius);
			var required = Math.Max(1, Info.StartDeployMinResourceCells);

			var anyMcv = false;
			foreach (var mcv in mcvs.Actors)
			{
				if (mcv.IsDead || !mcv.IsInWorld || mcv.Owner != player)
					continue;

				anyMcv = true;

				// Straight-line proximity is not enough: tiberium on a plateau or across a chasm can sit
				// a handful of cells away and still be unreachable, and deploying next to it strands the
				// bot exactly as a spawn with no tiberium at all would. A field that is further in a
				// straight line but on the same level is the better spawn, and rejecting the unreachable
				// one here sends the MCV to look for it — ChooseMcvDeployLocation then scores candidates
				// by real path length.
				var mobile = mcv.TraitOrDefault<Mobile>();
				var reachable = mobile == null
					? (Func<CPos, bool>)null
					: cell => pathfinder.PathMightExistForLocomotorBlockedByImmovable(mobile.Locomotor, mcv.Location, cell);

				if (resourceMapModule.CountValuableResourceCellsNear(mcv.Location, radius, reachable) >= required)
					return true;
			}

			// No MCV to judge: leave the existing behaviour alone rather than sending anything walking.
			return !anyMcv;
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
				CPos? resLoc = null;
				CPos? checkloc;

				// Stay with the cell already chosen. Candidate fields are scored relative to the MCV's
				// own position, so their ranking shifts with every metre it drives — re-deciding on each
				// scan had MCVs swing out across the map and come back. The commitment is dropped only
				// when the cell stops being usable, or when the hold expires as a safety net.
				if (mcvDeployGoals.TryGetValue(mcv, out var held)
					&& world.WorldTick < held.UntilTick
					&& CanStillDeployAt(mcv, actorInfo, bi, transformsInfo.Offset, held.Cell))
				{
					desiredLocation = held.Cell;
					checkloc = held.Cell;
				}
				else
				{
					var (deployLocation, chosenResLoc, chosenCheckloc) = ChooseMcvDeployLocation(mcv, actorInfo, bi, transformsInfo.Offset, allowfallback);
					allowfallback = true;
					desiredLocation = deployLocation;
					resLoc = chosenResLoc;
					checkloc = chosenCheckloc;

					if (desiredLocation == null)
					{
						mcvDeployGoals.Remove(mcv);
						mcvRetryCooldown[mcv] = world.WorldTick + 150;
						return;
					}

					mcvDeployGoals[mcv] = (desiredLocation.Value, world.WorldTick + Math.Max(1, Info.McvDeployGoalHoldTicks));
				}

				var safePath = FindSafeMcvPath(mcv, mcv.Location, desiredLocation.Value);
				if (safePath == null)
				{
					// No safe route: give up on this cell rather than holding it until the expiry.
					// FindSafeMcvPath weighs threats, so this is a stronger test than the plain
					// reachability check that keeps the commitment alive.
					FindBadDeploySpot(checkloc);
					mcvDeployGoals.Remove(mcv);
					mcvRetryCooldown[mcv] = world.WorldTick + 150;
					return;
				}

				activeMCVs[mcv] = checkloc;
				mcvRetryCooldown.Remove(mcv);
				if (resLoc != null)
				{
					foreach (var srp in suggestRefineryProduction)
						srp.RequestLocation(resLoc.Value, desiredLocation.Value, mcv);
				}

				// Nothing to do while it is already driving its route. This runs every
				// ScanForNewMcvInterval (20 ticks) and the orders queue rather than replace, so
				// re-issuing would pile up waypoints computed from stale positions.
				if (!mcv.IsIdle)
					return;

				// Whole route and the deploy in one order block: the MCV sets off once and unpacks on
				// arrival. Handing out a single leg per scan made it stop at the end of each one and
				// wait for the next scan before moving again.
				foreach (var waypoint in BuildSafeWaypoints(safePath, desiredLocation.Value))
					bot.QueueOrder(new Order("Move", mcv, Target.FromCell(world, waypoint), true));
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

				// The yard fitting says nothing about whether a base fits around it, nor about whether it
				// is standing in the tiberium it came for. Both are preferences rather than vetoes,
				// because refusing to deploy is worse than deploying awkwardly and on a tight map every
				// candidate is awkward — but they are not equally important, so the compromises are
				// ranked instead of lumped together.
				//
				// Clear of the tiberium but cramped beats roomy but sitting in the field: a yard in the
				// tiberium permanently costs harvestable ground, blocks the refinery from the cells it
				// wants and gets overgrown, while a tight plot still grows outward as the base expands.
				CPos? PickFrom(IEnumerable<CPos> candidates)
				{
					CPos? bestcell = null;
					CPos? clearButCramped = null;
					CPos? roomyButNearResources = null;
					CPos? lastResort = null;
					var clearButCrampedRoom = -1;
					var roomyButNearResourcesRoom = -1;
					var lastResortRoom = -1;

					foreach (var cell in candidates)
					{
						if (!world.CanPlaceBuilding(cell + offset, transformIntoInfo, transformIntoBuildingInfo, mcv))
							continue;

						// Roomiest of each class, not the first. Candidates are ordered by closeness to the
						// target, so taking the first settled for whatever sat nearest the field — the strip
						// between tiberium and map edge included.
						var room = CountBuildableCellsAround(cell, transformIntoBuildingInfo);
						var hasRoom = room >= Info.DeploySiteMinBuildableCells;
						var clearOfResources = !IsTooCloseToResources(cell, transformIntoBuildingInfo);

						if (hasRoom && clearOfResources)
						{
							bestcell = cell;
							break;
						}

						if (clearOfResources)
						{
							if (room > clearButCrampedRoom)
							{
								clearButCrampedRoom = room;
								clearButCramped = cell;
							}
						}
						else if (hasRoom)
						{
							if (room > roomyButNearResourcesRoom)
							{
								roomyButNearResourcesRoom = room;
								roomyButNearResources = cell;
							}
						}
						else if (room > lastResortRoom)
						{
							lastResortRoom = room;
							lastResort = cell;
						}
					}

					return bestcell ?? clearButCramped ?? roomyButNearResources ?? lastResort;
				}

				// Candidates are ranked by straight-line closeness to the field, and on a terraced map
				// the cells nearest the tiberium in a straight line are the ones directly under the cliff
				// it sits on. That is how a bot came to found an expansion where every field cost 77 to 90
				// cells of driving for 13 to 16 of straight line - and once the yard stands there, refinery
				// placement can only choose between bad options, because the decision was already made.
				//
				// So look first at the cells that can actually drive to the field, and only fall back to
				// the full list if none of them can be built on. Deploying awkwardly beats not deploying.
				var bestcell = PickFrom(cells.Where(c => LineToFieldIsDrivable(c, target)));
				bestcell ??= PickFrom(cells);

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
