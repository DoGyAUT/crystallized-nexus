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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public enum BaseBuildingLayout { Random, Grid, BaseGrid, Clustered, Compact, Coverage }

	public enum CNBasePlanCluster { Core, Expansion, Production, Tech, DefensePerimeter, Outpost }

	public enum DefenseRole
	{
		/// <summary>Default: use existing inner/outer placement logic.</summary>
		Default,

		/// <summary>Anti-infantry: spread evenly around mid-radius to cover all entry points.</summary>
		InfantryDefense,

		/// <summary>Anti-vehicle: outer radius, sorted toward the enemy.</summary>
		ArmorDefense,

		/// <summary>Anti-air: coverage-based, placed to maximise base buildings in weapon range.</summary>
		AADefense,

		/// <summary>Artillery: inner-to-mid radius behind other defenses, aimed toward enemy.</summary>
		ArtilleryDefense,

		/// <summary>Special (Obelisk, EMP): outermost radius on enemy approach vector.</summary>
		Special,
	}

	public class CNBuildingLayoutEntry
	{
		public readonly BaseBuildingLayout Layout = BaseBuildingLayout.Random;
		public readonly int MinSpacing = 1;

		[Desc("If set, place this building near the average location of all existing buildings of this named type.",
			"Falls back to base center when none exist yet. Set to the same type for self-clustering.")]
		public readonly string NearBuilding = null;

		[Desc("Maximum number of same-type buildings to place in one self-cluster. 0 = unlimited.")]
		public readonly int ClusterGroupSize = 0;

		[Desc("Distance in cells between self-cluster groups when ClusterGroupSize is enabled.")]
		public readonly int ClusterGroupSpacing = 6;
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Manages AI base construction.")]
	public class CNBaseBuilderBotModuleInfo : ConditionalTraitInfo, NotBefore<CNResourceMapBotModuleInfo>, NotBefore<IResourceLayerInfo>
	{
		[Desc("Tells the AI what building types are considered construction yards.")]
		public readonly FrozenSet<string> ConstructionYardTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what building types are considered refineries.")]
		public readonly FrozenSet<string> RefineryTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what building types are considered power plants.")]
		public readonly FrozenSet<string> PowerTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what building types are considered production facilities.")]
		public readonly FrozenSet<string> ProductionTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what building types are considered tech buildings.")]
		public readonly FrozenSet<string> TechTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what building types are considered naval production facilities.")]
		public readonly FrozenSet<string> NavalProductionTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what building types are considered silos (resource storage).")]
		public readonly FrozenSet<string> SiloTypes = FrozenSet<string>.Empty;

		[Desc("Tells the AI what building types are considered defenses.")]
		public readonly FrozenSet<string> DefenseTypes = FrozenSet<string>.Empty;

		[FieldLoader.LoadUsing(nameof(LoadDefenseRoles))]
		[Desc("Maps tactical roles to lists of defense building types.",
			"Valid roles: AntiInf, AntiVehicle, AA, Artillery, Special.",
			"Example: AA: gasam, nasam")]
		public readonly Dictionary<DefenseRole, FrozenSet<string>> DefenseRoles = [];

		[Desc("Maximum percentage of total base buildings allowed per defense role.",
			"Use key 'Total' to cap all defenses combined.",
			"Example: Total: 25, AntiInf: 10 — max 25% of base can be defenses, of which max 10% are anti-infantry.")]
		public readonly FrozenDictionary<string, int> DefenseRoleLimits = null;

		static object LoadDefenseRoles(MiniYaml yaml)
		{
			var result = new Dictionary<DefenseRole, FrozenSet<string>>();
			var node = yaml.NodeWithKeyOrDefault("DefenseRoles");
			if (node == null) return result;
			foreach (var n in node.Value.Nodes)
				if (Enum.TryParse<DefenseRole>(n.Key, out var role))
					result[role] = n.Value.Value
						.Split(',')
						.Select(s => s.Trim())
						.Where(s => s.Length > 0)
						.ToFrozenSet();
			return result;
		}

		internal static readonly FrozenSet<string> DefaultAirThreatTargetTypes = new HashSet<string> { "Air" }.ToFrozenSet();
		internal static readonly FrozenSet<string> DefaultInfantryThreatTargetTypes = new HashSet<string> { "Infantry" }.ToFrozenSet();
		internal static readonly FrozenSet<string> DefaultVehicleThreatTargetTypes = new HashSet<string> { "Vehicle", "Tank" }.ToFrozenSet();

		[Desc("Wall types the AI can build (LineBuild actors).")]
		public readonly FrozenSet<string> WallTypes = FrozenSet<string>.Empty;

		[Desc("Gate types the AI can build by replacing wall segments.")]
		public readonly FrozenSet<string> GateTypes = FrozenSet<string>.Empty;

		[Desc("Building types that should be surrounded by walls.")]
		public readonly FrozenSet<string> ProtectedByWalls = FrozenSet<string>.Empty;

		[Desc("Profiles that build a coarse perimeter wall around the core base. Empty = disabled.")]
		public readonly FrozenSet<string> BasePerimeterWallProfiles = FrozenSet<string>.Empty;

		[Desc("Minimum number of non-wall core structures before the AI starts a perimeter wall.")]
		public readonly int BasePerimeterWallMinimumStructures = 8;

		[Desc("Per-profile BasePerimeterWallMinimumStructures override (BotProfile name keyed).")]
		public readonly FrozenDictionary<string, int> ProfileBasePerimeterWallMinimumStructures = null;

		[Desc("Extra cells between the core base footprint and the perimeter wall.")]
		public readonly int BasePerimeterWallPadding = 4;

		[Desc("Maximum number of gates the AI should place in the core perimeter.")]
		public readonly int BasePerimeterMaxGateCount = 2;

		[Desc("Do not place perimeter walls on valuable resource cells within this distance.")]
		public readonly int BasePerimeterResourceAvoidanceRadius = 2;

		[Desc("Minimum number of wall cells that should exist on the perimeter before replacing them with gates.")]
		public readonly int BasePerimeterGateWallThreshold = 10;

		[Desc("Plug narrow map chokepoints (from CNTacticalMapBotModule) with a wall line and a single gate. Opt-in.")]
		public readonly bool EnableChokepointSealing = false;

		[Desc("Bot profiles that seal chokepoints when EnableChokepointSealing is set. Empty = all profiles.")]
		public readonly FrozenSet<string> ChokepointSealProfiles = FrozenSet<string>.Empty;

		[Desc("Locomotor names used by harvesters. When set, refinery placement filters out resource cells " +
			"with no passable path, preventing refineries next to cliffs.")]
		public readonly FrozenSet<string> HarvesterLocomotors = FrozenSet<string>.Empty;

		[Desc("Building types that should only be queued when VeinsOnlyResourceTypes exist somewhere on the map.")]
		public readonly FrozenSet<string> VeinsOnlyBuildingTypes = FrozenSet<string>.Empty;

		[Desc("Resource types that must exist on the map for VeinsOnlyBuildingTypes to be buildable (e.g. veins for the Nod Vein Harvester).")]
		public readonly FrozenSet<string> VeinsOnlyResourceTypes = FrozenSet<string>.Empty;

		[Desc("Minimum number of matching resource cells inside one resource-map area before VeinsOnlyBuildingTypes become buildable.")]
		public readonly int VeinsOnlyMinimumResourceCells = 10;

		[Desc("Production queues AI uses for buildings.")]
		public readonly FrozenSet<string> BuildingQueues = new HashSet<string> { "Building" }.ToFrozenSet();

		[Desc("Production queues AI uses for defenses.")]
		public readonly FrozenSet<string> DefenseQueues = new HashSet<string> { "Defense" }.ToFrozenSet();

		[Desc("Minimum distance in cells from center of the base when checking for building placement.")]
		public readonly int MinBaseRadius = 2;

		[Desc("Radius in cells around the center of the base to expand.")]
		public readonly int MaxBaseRadius = 20;

		[Desc("If true, MaxBaseRadius grows dynamically based on number of buildings.")]
		public readonly bool DynamicBaseRadius = false;

		[Desc("When DynamicBaseRadius is true, add this many cells per building built.")]
		public readonly float RadiusPerBuilding = 0.5f;

		[Desc("When DynamicBaseRadius is true, radius cannot exceed this value.")]
		public readonly int MaxDynamicBaseRadius = 30;

		[Desc("Per-profile MaxDynamicBaseRadius override (keyed by BotProfile name: Rush, Turtle, Tech, Expansion, Steamroller).")]
		public readonly FrozenDictionary<string, int> ProfileMaxDynamicBaseRadius = null;

		[Desc("Per-profile RadiusPerBuilding override expressed as cells × 100 (e.g. 50 = 0.5). Keyed by BotProfile name.")]
		public readonly FrozenDictionary<string, int> ProfileRadiusPerBuildingCentum = null;

		[Desc("Default layout mode for all buildings not listed in BuildingLayouts.")]
		public readonly BaseBuildingLayout DefaultLayout = BaseBuildingLayout.Random;

		[Desc("Default minimum spacing between buildings of the same type (cells).")]
		public readonly int SameTypeMinSpacing = 1;

		[Desc("Minimum spacing in cells between ANY two buildings regardless of type. 0 = disabled.")]
		public readonly int GlobalMinSpacing = 0;

		[FieldLoader.LoadUsing(nameof(LoadBuildingLayouts))]
		[Desc("Per-building-type layout overrides.")]
		public readonly Dictionary<string, CNBuildingLayoutEntry> BuildingLayouts = [];

		static object LoadBuildingLayouts(MiniYaml yaml)
		{
			var result = new Dictionary<string, CNBuildingLayoutEntry>();
			var node = yaml.NodeWithKeyOrDefault("BuildingLayouts");
			if (node == null) return result;
			foreach (var n in node.Value.Nodes)
			{
				var entry = new CNBuildingLayoutEntry();
				FieldLoader.Load(entry, n.Value);
				result[n.Key] = entry;
			}

			return result;
		}

		[Desc("Minimum excess power the AI should try to maintain.")]
		public readonly int MinimumExcessPower = 0;

		[Desc("The targeted excess power the AI tries to maintain cannot rise above this.")]
		public readonly int MaximumExcessPower = 0;

		[Desc("Increase maintained excess power by this amount for every ExcessPowerIncreaseThreshold of base buildings.")]
		public readonly int ExcessPowerIncrement = 0;

		[Desc("Increase maintained excess power by ExcessPowerIncrement for every N base buildings.")]
		public readonly int ExcessPowerIncreaseThreshold = 1;

		[Desc("Number of refineries to build before building any production building.")]
		public readonly int InititalMinimumRefineryCount = 1;

		[Desc("Number of refineries to build additionally after building any production building.")]
		public readonly int AdditionalMinimumRefineryCount = 1;

		[Desc("Additional delay (in ticks) between structure production checks when there is no active production.",
			"StructureProductionRandomBonusDelay is added to this.")]
		public readonly int StructureProductionInactiveDelay = 90;

		[Desc("Additional delay (in ticks) added between structure production checks when actively building things.",
			"Note: this should be at least as large as the typical order latency to avoid duplicated build choices.")]
		public readonly int StructureProductionActiveDelay = 18;

		[Desc("A random delay (in ticks) of up to this is added to active/inactive production delays.")]
		public readonly int StructureProductionRandomBonusDelay = 10;

		[Desc("Delay (in ticks) until retrying to build structure after the last 3 consecutive attempts failed.")]
		public readonly int StructureProductionResumeDelay = 1500;

		[Desc("After how many failed attempts to place a structure should AI give up and wait",
			"for StructureProductionResumeDelay before retrying.")]
		public readonly int MaximumFailedPlacementAttempts = 3;

		[Desc("How many randomly chosen cells with resources to check when deciding refinery placement.")]
		public readonly int MaxResourceCellsToCheck = 3;

		[Desc("Maximum number of refineries allowed near the same resource cluster. 0 = no limit.")]
		public readonly int MaxRefineriesPerCluster = 0;

		[Desc("Radius in cells used to determine whether two refineries belong to the same resource cluster.")]
		public readonly int RefineryClusterRadius = 10;

		[Desc("Delay (in ticks) until rechecking for new BaseProviders.")]
		public readonly int CheckForNewBasesDelay = 1500;

		[Desc("Chance that the AI will place the defenses in the direction of the closest enemy building.")]
		public readonly int PlaceDefenseTowardsEnemyChance = 100;

		[Desc("Minimum range at which to build defensive structures near a combat hotspot.")]
		public readonly int MinimumDefenseRadius = 5;

		[Desc("Maximum range at which to build defensive structures near a combat hotspot.")]
		public readonly int MaximumDefenseRadius = 20;

		[Desc("Defense buildings placed within this radius from base center form the inner defensive line " +
			"and are placed compactly around the base regardless of enemy direction. " +
			"Buildings placed beyond this radius form the outer line and face toward the enemy. " +
			"Set to 0 to disable (all defense placed in enemy direction).")]
		public readonly int DefenseInnerRadius = 0;

		[Desc("Minimum spacing for defense buildings in the inner line.")]
		public readonly int DefenseInnerMinSpacing = 2;

		[Desc("Minimum spacing for defense buildings in the outer line.")]
		public readonly int DefenseOuterMinSpacing = 3;

		[Desc("Minimum padding between defense building footprints and valuable resource cells.",
			"Reserves field edges for refinery placement.")]
		public readonly int DefenseResourceAvoidanceRadius = 4;

		[Desc("If true, the AI remembers recent attacks and biases future defense placement toward repeated danger areas.")]
		public readonly bool EnableDefenseDangerMemory = true;

		[Desc("Danger score added when one of the AI's actors is attacked.")]
		public readonly int DefenseDangerMemoryAttackWeight = 120;

		[Desc("Additional danger score added when one of the AI's buildings is attacked.")]
		public readonly int DefenseDangerMemoryBuildingAttackWeight = 160;

		[Desc("Minimum ticks between recording danger memory from attack events. Higher values reduce CPU spikes in large bot matches.")]
		public readonly int DefenseDangerMemoryRecordInterval = 25;

		[Desc("Minimum ticks between moving the defense center from attack events. Higher values prevent every projectile hit from shifting defense planning.")]
		public readonly int DefenseCenterUpdateInterval = 75;

		[Desc("Attacks within this cell radius are merged into the same remembered danger hotspot.")]
		public readonly int DefenseDangerMemoryMergeRadius = 5;

		[Desc("Maximum number of remembered danger hotspots.")]
		public readonly int DefenseDangerMemoryMaxEntries = 12;

		[Desc("Maximum number of danger hotspots considered when scoring one defense placement.")]
		public readonly int DefensePlacementMaxHotspots = 4;

		[Desc("Maximum number of defense placement cells that receive expensive scoring and placement checks.")]
		public readonly int DefensePlacementCandidateLimit = 48;

		[Desc("Danger hotspots below this score are ignored for defense placement.")]
		public readonly int DefenseDangerMemoryMinimumWeight = 80;

		[Desc("Interval in ticks between danger memory decay steps.")]
		public readonly int DefenseDangerMemoryDecayInterval = 125;

		[Desc("Danger score removed from each hotspot on each decay interval.")]
		public readonly int DefenseDangerMemoryDecayAmount = 10;

		[Desc("How strongly remembered danger hotspots influence defense placement.")]
		public readonly int DefensePlacementDangerWeight = 100;

		[Desc("How strongly proactive map-topology chokepoints (bridges, ramps, passages from CNTacticalMapBotModule)",
			"influence defense placement. Kept below DefensePlacementDangerWeight so real attacks still dominate.")]
		public readonly int TopologyHotspotWeight = 60;

		[Desc("Extra weight added to a candidate that sits on a high-ground cliff edge overlooking a reachable approach",
			"(height advantage + natural wall). Scaled by the number of height levels above the flat baseline.")]
		public readonly int HighGroundEdgeWeight = 90;

		[Desc("Penalty applied to a defense candidate whose direction from the defense center matches no access corridor",
			"(e.g. facing a map edge / cliff / water with no enemy approach). 0 disables the sealed-flank penalty.")]
		public readonly int SealedFlankPenaltyWeight = 80;

		[Desc("Bonus for defense candidates placed on the base side of a sealable chokepoint wall/gate line.")]
		public readonly int ChokepointDefenseAnchorWeight = 160;

		[Desc("How strongly the nearest known enemy base/building influences defense placement when no stronger danger hotspot exists.")]
		public readonly int DefensePlacementEnemyDirectionWeight = 70;

		[Desc("How strongly remembered danger hotspots make tech-building placement avoid exposed cells.")]
		public readonly int TechPlacementDangerAvoidanceWeight = 120;

		[Desc("How strongly tech-building placement prefers cells close to the chosen base/production core.")]
		public readonly int TechPlacementCoreBiasWeight = 2;


		[Desc("Try to build another production building if there is too much cash.")]
		public readonly int NewProductionCashThreshold = 5000;

		[Desc("Chance to build another production building if there is too much cash.")]
		public readonly int NewProductionChance = 50;

		// --- Per-tech-stage BuildingFractions overrides (null = use base BuildingFractions) ---
		public readonly FrozenDictionary<string, int> EarlyBuildingFractions = null;
		public readonly FrozenDictionary<string, int> MidBuildingFractions = null;
		public readonly FrozenDictionary<string, int> LateBuildingFractions = null;

		[Desc("Target cash thresholds per strategy budget category for opportunistic extra production.")]
		public readonly FrozenDictionary<string, int> BudgetNewProductionCashThresholds = null;

		[Desc("Target chances per strategy budget category for opportunistic extra production.")]
		public readonly FrozenDictionary<string, int> BudgetNewProductionChances = null;

		[Desc("Target minimum excess power per strategy budget category.")]
		public readonly FrozenDictionary<string, int> BudgetMinimumExcessPower = null;

		[Desc("Target maximum excess power per strategy budget category.")]
		public readonly FrozenDictionary<string, int> BudgetMaximumExcessPower = null;

		[Desc("Target initial minimum refinery counts per strategy budget category.")]
		public readonly FrozenDictionary<string, int> BudgetInitialMinimumRefineryCounts = null;

		[Desc("Radius in cells around a factory scanned for rally points by the AI.")]
		public readonly int RallyPointScanRadius = 8;

		[Desc("Radius in cells around each building with ProvideBuildableArea",
			"to check for a 3x3 area of water where naval structures can be built.",
			"Should match maximum adjacency of naval structures.")]
		public readonly int CheckForWaterRadius = 8;

		[Desc("Terrain types which are considered water for base building purposes.")]
		public readonly FrozenSet<string> WaterTerrainTypes = new HashSet<string> { "Water" }.ToFrozenSet();

		[Desc("What buildings to the AI should build.", "What integer percentage of the total base must be this type of building.")]
		public readonly FrozenDictionary<string, int> BuildingFractions = null;

		[Desc("What buildings should the AI have a maximum limit to build.")]
		public readonly FrozenDictionary<string, int> BuildingLimits = null;

		[Desc("When should the AI start building specific buildings.")]
		public readonly FrozenDictionary<string, int> BuildingDelays = null;

		[Desc("Only queue construction of a new structure when above this requirement.")]
		public readonly int ProductionMinCashRequirement = 500;

		// --- Economy overflow signal ---
		[Desc("Ticks between economy samples used to compute the EconomyOverflow signal.")]
		public readonly int EconomyOverflowSampleInterval = 25;

		[Desc("Number of samples retained for the EconomyOverflow moving average.")]
		public readonly int EconomyOverflowSampleWindow = 12;

		[Desc("Average cash below this contributes 0 to the EconomyOverflow signal.")]
		public readonly int EconomyOverflowCashFloor = 3000;

		[Desc("Average cash at/above this contributes the maximum to the EconomyOverflow signal.")]
		public readonly int EconomyOverflowCashCeiling = 12000;

		[Desc("Credits-per-tick income rate below this contributes 0 to the EconomyOverflow signal.")]
		public readonly int EconomyOverflowIncomeFloor = 20;

		[Desc("Credits-per-tick income rate at/above this contributes the maximum to the EconomyOverflow signal.")]
		public readonly int EconomyOverflowIncomeCeiling = 90;

		[Desc("Weight (out of 100) of the cash score in the EconomyOverflow signal.")]
		public readonly int EconomyOverflowCashWeight = 50;

		[Desc("Weight (out of 100) of the income score in the EconomyOverflow signal.")]
		public readonly int EconomyOverflowIncomeWeight = 50;

		[Desc("Percent bonus to per-type BuildingLimits at EconomyOverflow factor 1.0.")]
		public readonly int EconomyOverflowBuildingLimitBonusPct = 75;

		[Desc("Additional refinery target slots at EconomyOverflow factor 1.0 (still bounded by map-supported capacity).")]
		public readonly int EconomyOverflowRefineryBonus = 2;

		[Desc("Delay (in ticks) between reassigning rally points.")]
		public readonly int AssignRallyPointsInterval = 100;

		[Desc("Delay (in ticks) for finding a good resource to place a refinery next to.")]
		public readonly int CheckBestResourceLocationInterval = 151;

		[Desc("Interval (in ticks) between checking whether to sell a redundant refinery. Set to -1 to disable.")]
		public readonly int SellRefineryInterval = 5000;

		[Desc("Distance (in cells) for refineries finding redundant refineries.")]
		public readonly int SellRefineryTooCloseCellDistance = 6;

		[Desc("Maximum distance (in cells) from resources before refineries are eligible to be sold.")]
		public readonly int SellRefineryNoResourceDistance = 12;

		[Desc("If a finite field near a refinery has this many or fewer valuable resource cells left, the refinery becomes a sell candidate.")]
		public readonly int SellRefineryLowResourceThreshold = 8;

		[Desc("Minimum valuable cells required before placing the first refinery on a finite field.")]
		public readonly int MinFiniteFieldCellsForRefinery = 12;

		[Desc("Minimum valuable cells required before placing an additional refinery on a finite field.")]
		public readonly int MinFiniteFieldCellsForExtraRefinery = 28;

		[Desc("Minimum valuable cells required before placing an additional refinery on a respawning field.")]
		public readonly int MinRespawningFieldCellsForExtraRefinery = 6;

		[Desc("Maximum refinery count per area. Area size is defined in " + nameof(ResourceMapBotModule) + ".")]
		public readonly int MaxRefineryPerIndice = 2;

		[Desc($"AI will move mcv when those numbers of refinery <= productions + tech - {nameof(ExpansionTolerate)}.")]
		public readonly ImmutableArray<int> ExpansionTolerate = [0, 1];

		[Desc($"AI will move the only mcv when those numbers of refinery <= productions + tech - {nameof(ForceExpansionTolerate)}.")]
		public readonly ImmutableArray<int> ForceExpansionTolerate = [2, 3];

		[Desc("Decrease the expansion tolerate by Cash / this. Used to prevent AI from expanding when it has enough cash.")]
		public readonly int PerExpansionTolerateOnCash = 12000;

		[Desc("Interval (in ticks) between checking whether a bankrupt AI should sell a building to recover a refinery.")]
		public readonly int BankruptcyRecoveryInterval = 125;

		[Desc("Do not sell a building for refinery recovery unless this much cash is still missing after accounting for current reserves.")]
		public readonly int BankruptcyRecoveryMinimumShortfall = 100;

		[Desc("Hard cash ceiling: bankruptcy recovery (tiers 1-3) never fires while the bot has more",
			"than this in cash+resources, even when other conditions match. Prevents premature selling during transient cash dips.")]
		public readonly int BankruptcyRecoveryMaxCash = 500;

		[Desc("Harvester actor types. Used by bankruptcy recovery to detect missing harvesters and queue replacements.")]
		public readonly FrozenSet<string> HarvesterTypes = FrozenSet<string>.Empty;

		[Desc("MCV actor types. Used by bankruptcy recovery when the bot has lost its construction yard but can still produce an MCV from a factory.")]
		public readonly FrozenSet<string> McvTypes = FrozenSet<string>.Empty;

		[Desc("Factory actor types that can produce an MCV. Used by bankruptcy recovery and terminal-state detection.")]
		public readonly FrozenSet<string> McvFactoryTypes = FrozenSet<string>.Empty;

		[Desc("If true, a bot with no path to economic recovery sells all remaining buildings",
			"and AttackMoves every unit toward the nearest enemy structure (terminal kamikaze).")]
		public readonly bool BankruptcyKamikazeEnabled = true;

		[Desc("Cash threshold below which terminal kamikaze may fire (avoids triggering while the bot still has buying power).")]
		public readonly int BankruptcyKamikazeMaxCash = 200;

		public override object Create(ActorInitializer init) { return new CNBaseBuilderBotModule(init.Self, this); }
	}

	public class CNBaseBuilderBotModule : ConditionalTrait<CNBaseBuilderBotModuleInfo>, IGameSaveTraitData,
		IBotTick, IBotPositionsUpdated, IBotRespondToAttack, IBotRequestPauseUnitProduction, IBotSuggestRefineryProduction, INotifyActorDisposing
	{
		public CPos GetRandomBaseCenter()
		{
			var randomConstructionYard = ConstructionYardBuildings.Actors
				.RandomOrDefault(world.LocalRandom);

			return randomConstructionYard?.Location ?? initialBaseCenter;
		}

		public int GetEffectiveMaxBaseRadius()
		{
			if (!Info.DynamicBaseRadius)
				return Info.MaxBaseRadius;

			var buildingCount = world.ActorsHavingTrait<Building>().Count(a => a.Owner == player);
			return GetEffectiveMaxBaseRadius(buildingCount);
		}

		// Use when playerBuildings.Length is already available to avoid a redundant world scan.
		public int GetEffectiveMaxBaseRadius(int playerBuildingCount)
		{
			if (!Info.DynamicBaseRadius)
				return Info.MaxBaseRadius;

			var dynamic = Info.MaxBaseRadius + (int)(playerBuildingCount * GetActiveRadiusPerBuilding());
			return Math.Min(dynamic, GetActiveMaxDynamicBaseRadius());
		}

		public int GetActiveMaxDynamicBaseRadius()
		{
			if (profileModule != null && Info.ProfileMaxDynamicBaseRadius != null &&
				Info.ProfileMaxDynamicBaseRadius.TryGetValue(profileModule.ActiveProfile.ToString(), out var v))
				return v;

			return Info.MaxDynamicBaseRadius;
		}

		public float GetActiveRadiusPerBuilding()
		{
			if (profileModule != null && Info.ProfileRadiusPerBuildingCentum != null &&
				Info.ProfileRadiusPerBuildingCentum.TryGetValue(profileModule.ActiveProfile.ToString(), out var centum))
				return centum / 100f;

			return Info.RadiusPerBuilding;
		}

		public int GetActiveBasePerimeterWallMinimumStructures()
		{
			if (profileModule != null && Info.ProfileBasePerimeterWallMinimumStructures != null &&
				Info.ProfileBasePerimeterWallMinimumStructures.TryGetValue(profileModule.ActiveProfile.ToString(), out var v))
				return v;

			return Info.BasePerimeterWallMinimumStructures;
		}

		public CPos DefenseCenter { get; private set; }

		// Actor, ActorCount.
		public Dictionary<string, int> BuildingsBeingProduced = [];
		public IBotBaseExpansion[] BaseExpansionModules;
		public CNResourceMapBotModule ResourceMapModule;
		public CNTacticalMapBotModule TacticalMapModule;

		readonly World world;
		readonly Player player;
		PowerManager playerPower;
		IResourceLayer resourceLayer;
		IBotPositionsUpdated[] positionsUpdatedModules;
		CPos initialBaseCenter;
		public CPos? ResourceConyardCenter;
		public IPathFinder PathFinder { get; private set; }
		public Locomotor[] HarvesterLocomotorsList = Array.Empty<Locomotor>();

		bool veinsChecked;
		bool cachedVeinsExist;
		public Dictionary<Actor, (CPos ConyardLoc, CPos ResourceLoc)> RequestedRefineries = [];

		// Set by QueueManager when HasViableRefineryField returns false: no buildable spot exists
		// for a second refinery, so PauseUnitProduction must not hold units hostage indefinitely.
		public bool RefineryExpansionBlocked { get; set; }
		readonly Dictionary<CPos, DefenseDangerHotspot> defenseDangerMemory = [];
		readonly Dictionary<string, DefenseRole> defenseThreatRoleCache = [];

		// Per-defense-center cache for the topology terrain terms (recomputed only when the center moves).
		CPos cachedFlankCenter = new(int.MinValue, int.MinValue);
		List<CVec> cachedAccessBearings = [];
		readonly Dictionary<CPos, CNHighGroundEdge> cachedHighGround = [];
		List<CPos> cachedChokepointDefenseAnchors = [];


		public readonly struct DefensePlacementThreat
		{
			public readonly CPos Location;
			public readonly int Weight;

			public DefensePlacementThreat(CPos location, int weight)
			{
				Location = location;
				Weight = weight;
			}
		}

		readonly Stack<TraitPair<RallyPoint>> rallyPoints = [];
		int assignRallyPointsTicks;
		int checkBestResourceLocationTicks;
		int sellRefineryTick;
		int bankruptcyRecoveryTick;
		bool terminalRecoveryTriggered;
		int defenseDangerMemoryDecayTick;
		int nextDefenseDangerMemoryRecordTick;
		int nextDefenseCenterUpdateTick;
		bool firstTick = true;
		CNBotProfileBotModule profileModule;
		CombatAnalysisBotModule combatAnalysis;

		BotProfile ActiveProfile => profileModule?.ActiveProfile ?? BotProfile.Adaptive;
		TechStage ActiveTechStage => profileModule?.ActiveTechStage ?? TechStage.Early;

		public PlayerResources PlayerResources { get; private set; }

		// --- Economy overflow signal state ---
		readonly Queue<(int Cash, int Earned, int Tick)> economySamples = new();
		int economyOverflowTick;

		// Generic "economy overflow" factor (0..1000, milli units) sampled from cash + earned-income trend.
		// Handicap-agnostic so it rewards both handicap bonuses and resource-rich maps.
		public int EconomyOverflowMilli { get; private set; }
		public float EconomyOverflow => EconomyOverflowMilli / 1000f;

		// Credits-per-tick rate (latest Earned minus oldest sample / dt). Stays positive for a few
		// hundred ticks after the last harvester dump; bankruptcy recovery uses this to wait through
		// transient cash dips instead of selling buildings unnecessarily.
		public int IncomeRate { get; private set; }

		IReadOnlyList<Actor> cachedPlayerBuildings = [];
		int cachedPlayerBuildingsTick = -1;

		ILookup<string, ProductionQueue> cachedQueues;
		int cachedQueuesTick = -25;

		ILookup<string, ProductionQueue> GetCachedQueues()
		{
			if (world.WorldTick - cachedQueuesTick >= 25)
			{
				cachedQueues = AIUtils.FindQueuesByCategory(player);
				cachedQueuesTick = world.WorldTick;
			}

			return cachedQueues;
		}

		public IReadOnlyList<Actor> GetCachedPlayerBuildings()
		{
			var tick = world.WorldTick;
			if (tick != cachedPlayerBuildingsTick)
			{
				cachedPlayerBuildings = world.ActorsHavingTrait<Building>()
					.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld)
					.ToList();
				cachedPlayerBuildingsTick = tick;
			}

			return cachedPlayerBuildings;
		}

		// --- Profile + tech-stage aware getters ---
		// BuildingFractions: merge TechStage overlay first, then apply strategic budget scaling.
		public FrozenDictionary<string, int> GetActiveBuildingFractions()
		{
			var stageOverride = ActiveTechStage switch
			{
				TechStage.Early => Info.EarlyBuildingFractions,
				TechStage.Mid => Info.MidBuildingFractions,
				TechStage.Late => Info.LateBuildingFractions,
				_ => null
			};

			var underThreat = combatAnalysis != null && !combatAnalysis.IsTraitDisabled && combatAnalysis.HasActiveThreat();

			if ((stageOverride == null || stageOverride.Count == 0) && profileModule == null && !underThreat)
				return Info.BuildingFractions;

			var merged = Info.BuildingFractions != null
				? new Dictionary<string, int>(Info.BuildingFractions)
				: [];

			if (stageOverride != null)
				foreach (var kv in stageOverride)
					merged[kv.Key] = kv.Value;

			ApplyProfileBuildingBudget(merged);

			if (underThreat)
				ApplyPanicProductionBoost(merged);

			return merged.ToFrozenDictionary();
		}

		// Under active attack, a base with no Infantry (or, against an armor-heavy attack, no Vehicle)
		// production building can't respond at all - CNUnitBuilderBotModule's panic unit-spam has nothing
		// to build from. Force whichever is missing to the front of the queue until it exists; the normal
		// deficit-based selection in CNBaseBuilderQueueManager takes back over once count > 0.
		void ApplyPanicProductionBoost(Dictionary<string, int> fractions)
		{
			const int PanicFraction = 1000;

			var wantVehicle = combatAnalysis.GetHighestThreatRole() == DefenseRole.ArmorDefense;
			var hasInfantryProduction = false;
			var hasVehicleProduction = false;

			foreach (var building in GetCachedPlayerBuildings())
			{
				var produces = world.Map.Rules.Actors[building.Info.Name].TraitInfos<ProductionInfo>();
				if (produces.Any(p => p.Produces.Contains("Infantry")))
					hasInfantryProduction = true;
				if (produces.Any(p => p.Produces.Contains("Vehicle")))
					hasVehicleProduction = true;
			}

			if (hasInfantryProduction && (hasVehicleProduction || !wantVehicle))
				return;

			foreach (var name in fractions.Keys.ToArray())
			{
				var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(name);
				var produces = actorInfo?.TraitInfos<ProductionInfo>();
				if (produces == null)
					continue;

				if (!hasInfantryProduction && produces.Any(p => p.Produces.Contains("Infantry")))
					fractions[name] = PanicFraction;
				else if (wantVehicle && !hasVehicleProduction && produces.Any(p => p.Produces.Contains("Vehicle")))
					fractions[name] = PanicFraction;
			}
		}

		public FrozenDictionary<string, int> GetActiveDefenseRoleLimits()
		{
			if (profileModule == null)
				return Info.DefenseRoleLimits;

			var merged = Info.DefenseRoleLimits != null
				? new Dictionary<string, int>(Info.DefenseRoleLimits)
				: [];

			ApplyProfileDefenseBudget(merged);

			return merged.ToFrozenDictionary();
		}

		void ApplyProfileBuildingBudget(Dictionary<string, int> values)
		{
			if (profileModule == null)
				return;

			var strategy = profileModule.CurrentStrategy;
			var productionScale = BudgetScale(strategy.ProductionBudget, 25, 0.5f, 2.2f);
			var techScale = BudgetScale(strategy.TechBudget, 20, 0.5f, 2.2f);

			foreach (var key in values.Keys.ToArray())
			{
				var scale = 1f;
				if (Info.ProductionTypes.Contains(key) || Info.NavalProductionTypes.Contains(key))
					scale = productionScale;
				else if (Info.TechTypes.Contains(key))
					scale = techScale;

				if (Math.Abs(scale - 1f) < 0.01f)
					continue;

				values[key] = Math.Max(1, (int)Math.Round(values[key] * scale, MidpointRounding.AwayFromZero));
			}
		}

		void ApplyProfileDefenseBudget(Dictionary<string, int> values)
		{
			if (profileModule == null)
				return;

			var strategy = profileModule.CurrentStrategy;
			var defenseScale = Math.Clamp(0.5f + strategy.DefenseBudget / 60f, 0.5f, 1.4f);
			var techScale = BudgetScale(strategy.TechBudget, 25, 0.8f, 1.3f);

			foreach (var key in values.Keys.ToArray())
			{
				var scale = defenseScale;
				if (key == "ArtilleryDefense" || key == "Special")
					scale *= techScale;

				values[key] = Math.Max(1, (int)Math.Round(values[key] * scale, MidpointRounding.AwayFromZero));
			}
		}

		static float BudgetScale(int budget, int neutralBudget, float minimum, float maximum)
		{
			return Math.Clamp((float)budget / Math.Max(1, neutralBudget), minimum, maximum);
		}

		public int GetActiveNewProductionCashThreshold()
		{
			return GetBudgetWeightedValue(Info.BudgetNewProductionCashThresholds, Info.NewProductionCashThreshold);
		}

		public int GetActiveNewProductionChance()
		{
			return GetBudgetWeightedValue(Info.BudgetNewProductionChances, Info.NewProductionChance);
		}

		public int GetActiveMinimumExcessPower()
		{
			return GetBudgetWeightedValue(Info.BudgetMinimumExcessPower, Info.MinimumExcessPower);
		}

		public int GetActiveMaximumExcessPower()
		{
			return GetBudgetWeightedValue(Info.BudgetMaximumExcessPower, Info.MaximumExcessPower);
		}

		public int GetActiveInititalMinimumRefineryCount()
		{
			return GetBudgetWeightedValue(Info.BudgetInitialMinimumRefineryCounts, Info.InititalMinimumRefineryCount);
		}

		public bool ShouldBuildBasePerimeterWalls()
		{
			if (Info.BasePerimeterWallProfiles.Count == 0)
				return false;

			var profile = ActiveProfile == BotProfile.Adaptive && profileModule != null
				? profileModule.ActiveProfile
				: ActiveProfile;

			return Info.BasePerimeterWallProfiles.Contains(profile.ToString());
		}

		public bool ShouldSealChokepoints()
		{
			if (!Info.EnableChokepointSealing || TacticalMapModule == null
				|| Info.WallTypes.Count == 0)
				return false;

			if (Info.ChokepointSealProfiles.Count == 0)
				return true;

			var profile = ActiveProfile == BotProfile.Adaptive && profileModule != null
				? profileModule.ActiveProfile
				: ActiveProfile;

			return Info.ChokepointSealProfiles.Contains(profile.ToString());
		}

		int GetBudgetWeightedValue(FrozenDictionary<string, int> budgetValues, int fallback)
		{
			if (profileModule == null || budgetValues == null || budgetValues.Count == 0)
				return fallback;

			var strategy = profileModule.CurrentStrategy;
			var budgetWeights = new[]
			{
				("Expansion", strategy.ExpansionBudget),
				("Tech", strategy.TechBudget),
				("Defense", strategy.DefenseBudget),
				("Production", strategy.ProductionBudget),
			};

			var totalWeight = 0;
			var weightedValue = 0;
			foreach (var weight in budgetWeights)
			{
				if (weight.Item2 <= 0 || !budgetValues.TryGetValue(weight.Item1, out var value))
					continue;

				weightedValue += value * weight.Item2;
				totalWeight += weight.Item2;
			}

			if (totalWeight <= 0)
				return fallback;

			return (int)Math.Round(weightedValue / (double)totalWeight, MidpointRounding.AwayFromZero);
		}

		public CNBasePlanCluster GetBasePlanClusterForActor(ActorInfo actorInfo, bool isDefense, bool isRefinery)
		{
			if (actorInfo == null)
				return CNBasePlanCluster.Core;

			if (isRefinery || Info.RefineryTypes.Contains(actorInfo.Name))
				return CNBasePlanCluster.Expansion;

			if (isDefense || Info.DefenseTypes.Contains(actorInfo.Name))
				return CNBasePlanCluster.DefensePerimeter;

			if (Info.ProductionTypes.Contains(actorInfo.Name) || Info.NavalProductionTypes.Contains(actorInfo.Name))
				return CNBasePlanCluster.Production;

			if (Info.TechTypes.Contains(actorInfo.Name))
				return CNBasePlanCluster.Tech;

			return CNBasePlanCluster.Core;
		}

		public CPos GetBasePlanCenterForActor(ActorInfo actorInfo, CPos fallbackCenter, bool isDefense, bool isRefinery)
		{
			return GetBasePlanCenter(GetBasePlanClusterForActor(actorInfo, isDefense, isRefinery), fallbackCenter);
		}

		public CPos GetBasePlanCenter(CNBasePlanCluster cluster, CPos fallbackCenter)
		{
			return cluster switch
			{
				CNBasePlanCluster.Expansion => AverageBuildingLocation(Info.RefineryTypes) ?? ResourceConyardCenter ?? fallbackCenter,
				CNBasePlanCluster.Production => AverageBuildingLocation(Info.ProductionTypes) ?? fallbackCenter,
				CNBasePlanCluster.Tech => AverageBuildingLocation(Info.TechTypes) ?? AverageBuildingLocation(Info.ProductionTypes) ?? fallbackCenter,
				CNBasePlanCluster.DefensePerimeter => DefenseCenter == default ? fallbackCenter : DefenseCenter,
				CNBasePlanCluster.Outpost => ResourceConyardCenter ?? fallbackCenter,
				_ => AverageBuildingLocation(Info.ConstructionYardTypes) ?? fallbackCenter
			};
		}

		CPos? AverageBuildingLocation(IEnumerable<string> typeNames)
		{
			var typeSet = typeNames as ISet<string> ?? typeNames.ToHashSet();
			var buildings = GetCachedPlayerBuildings()
				.Where(b => typeSet.Contains(b.Info.Name))
				.ToArray();

			if (buildings.Length == 0)
				return null;

			return new CPos(
				(int)buildings.Average(b => b.Location.X),
				(int)buildings.Average(b => b.Location.Y));
		}

		readonly CNBaseBuilderQueueManager[] builders;
		int currentBuilderIndex = 0;

		public readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> RefineryBuildings;
		readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> powerBuildings;
		public readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> ConstructionYardBuildings;
		public readonly ActorIndex.OwnerAndNamesAndTrait<BuildingInfo> ProductionBuildings;

		public CNBaseBuilderBotModule(Actor self, CNBaseBuilderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			builders = new CNBaseBuilderQueueManager[info.BuildingQueues.Count + info.DefenseQueues.Count];
			RefineryBuildings = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.RefineryTypes, player);
			powerBuildings = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.PowerTypes, player);
			ConstructionYardBuildings = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.ConstructionYardTypes, player);
			ProductionBuildings = new ActorIndex.OwnerAndNamesAndTrait<BuildingInfo>(world, info.ProductionTypes, player);
		}

		protected override void Created(Actor self)
		{
			playerPower = self.Owner.PlayerActor.TraitOrDefault<PowerManager>();
			PlayerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			resourceLayer = self.World.WorldActor.TraitOrDefault<IResourceLayer>();
			PathFinder = self.World.WorldActor.TraitOrDefault<IPathFinder>();
			positionsUpdatedModules = self.Owner.PlayerActor.TraitsImplementing<IBotPositionsUpdated>().ToArray();
			BaseExpansionModules = self.Owner.PlayerActor.TraitsImplementing<IBotBaseExpansion>().ToArray();

			if (Info.HarvesterLocomotors.Count > 0)
				HarvesterLocomotorsList = world.WorldActor.TraitsImplementing<Locomotor>()
					.Where(l => Info.HarvesterLocomotors.Contains(l.Info.Name))
					.ToArray();

			var i = 0;

			foreach (var building in Info.BuildingQueues)
				builders[i++] = new CNBaseBuilderQueueManager(this, building, player, playerPower, PlayerResources, resourceLayer);

			foreach (var defense in Info.DefenseQueues)
				builders[i++] = new CNBaseBuilderQueueManager(this, defense, player, playerPower, PlayerResources, resourceLayer);
		}

		protected override void TraitEnabled(Actor self)
		{
			// Avoid all AIs reevaluating assignments on the same tick, randomize their initial evaluation delay.
			assignRallyPointsTicks = world.LocalRandom.Next(0, Info.AssignRallyPointsInterval);
			checkBestResourceLocationTicks = world.LocalRandom.Next(0, Info.CheckBestResourceLocationInterval);
			sellRefineryTick = Info.SellRefineryInterval < 0 ? 0 : world.LocalRandom.Next(0, Info.SellRefineryInterval);
			bankruptcyRecoveryTick = world.LocalRandom.Next(0, Math.Max(1, Info.BankruptcyRecoveryInterval));
			defenseDangerMemoryDecayTick = world.LocalRandom.Next(0, Math.Max(1, Info.DefenseDangerMemoryDecayInterval));
		}

		void IBotPositionsUpdated.UpdatedBaseCenter(CPos newLocation)
		{
			initialBaseCenter = newLocation;
		}

		void IBotPositionsUpdated.UpdatedDefenseCenter(CPos newLocation)
		{
			DefenseCenter = newLocation;
		}

		bool IBotRequestPauseUnitProduction.PauseUnitProduction => !IsTraitDisabled && !HasMinimalRefineryCount() &&
			HasEconomyRecoveryPath() && !RefineryExpansionBlocked;

		void IBotTick.BotTick(IBot bot)
		{
			if (firstTick)
			{
				ResourceMapModule = bot.Player.PlayerActor.TraitsImplementing<CNResourceMapBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				profileModule = bot.Player.PlayerActor.TraitsImplementing<CNBotProfileBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				TacticalMapModule = bot.Player.PlayerActor.TraitsImplementing<CNTacticalMapBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				combatAnalysis = bot.Player.PlayerActor.TraitsImplementing<CombatAnalysisBotModule>().FirstOrDefault(t => t.IsTraitEnabled());
				firstTick = false;
			}

			if (--economyOverflowTick <= 0)
			{
				economyOverflowTick = Math.Max(1, Info.EconomyOverflowSampleInterval);
				UpdateEconomyOverflow();
			}

			if (--assignRallyPointsTicks <= 0)
			{
				assignRallyPointsTicks = Math.Max(2, Info.AssignRallyPointsInterval);
				foreach (var rp in world.ActorsWithTrait<RallyPoint>().Where(rp => rp.Actor.Owner == player))
					rallyPoints.Push(rp);
			}
			else
			{
				// PERF: Spread out rally point assignments updates across multiple ticks.
				var updateCount = Exts.IntegerDivisionRoundingAwayFromZero(rallyPoints.Count, assignRallyPointsTicks);
				for (var i = 0; i < updateCount; i++)
				{
					var rp = rallyPoints.Pop();
					if (rp.Actor.Owner == player && !rp.Actor.Disposed)
						SetRallyPoint(bot, rp);
				}
			}

			if (--checkBestResourceLocationTicks <= 0 && resourceLayer != null)
			{
				checkBestResourceLocationTicks = Info.CheckBestResourceLocationInterval;

				// Clear outdated refinery requests: dead/disposed MCVs would otherwise block
				// recovery checks (TryRecoverRefinery) indefinitely. Also drop requests whose
				// target indice can no longer support another refinery.
				foreach (var mcv in RequestedRefineries.Keys.ToList())
				{
					if (mcv == null || mcv.IsDead || mcv.Disposed || !mcv.IsInWorld || mcv.Owner != player)
					{
						RequestedRefineries.Remove(mcv);
						continue;
					}

					if (ResourceMapModule != null)
					{
						var requestedIndice = ResourceMapModule.FindClosestIndiceFromCPos(RequestedRefineries[mcv].ResourceLoc);
						if (!CanSupportAnotherRefinery(requestedIndice, 0))
							RequestedRefineries.Remove(mcv);
					}
				}

				Actor bestconyard = null;
				var best = int.MinValue;

				foreach (var conyard in ConstructionYardBuildings.Actors)
				{
					if (conyard.IsDead)
						continue;

					if (!world.Map.FindTilesInAnnulus(conyard.Location, Info.MinBaseRadius, Info.MaxBaseRadius)
						.Any(c => ResourceMapModule != null
						? ResourceMapModule.Info.ValuableResourceTypes.Contains(resourceLayer.GetResource(c).Type)
						: resourceLayer.GetResource(c).Type != null))
						continue;

					var refs = world.FindActorsInCircle(conyard.CenterPosition, WDist.FromCells(Info.MaxBaseRadius))
							.Count(a => a.Owner == player && Info.RefineryTypes.Contains(a.Info.Name));

					var suitable = -world.FindActorsInCircle(conyard.CenterPosition, WDist.FromCells(Info.MaxBaseRadius))
							.Count(a => a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy) - refs;

					if (suitable > best)
					{
						best = suitable;
						bestconyard = conyard;
					}
				}

				ResourceConyardCenter = bestconyard?.Location;
			}

			BuildingsBeingProduced.Clear();

			// PERF: We tick only one type of valid queue at a time
			// if AI gets enough cash, it can fill all of its queues with enough ticks
			var findQueue = false;
			var queuesByCategory = GetCachedQueues();
			for (int i = 0, builderIndex = currentBuilderIndex; i < builders.Length; i++)
			{
				if (++builderIndex >= builders.Length)
					builderIndex = 0;

				--builders[builderIndex].WaitTicks;

				var queues = queuesByCategory[builders[builderIndex].Category].ToArray();
				if (queues.Length != 0)
				{
					if (!findQueue)
					{
						currentBuilderIndex = builderIndex;
						findQueue = true;
					}

					// Refresh "BuildingsBeingProduced" only when AI can produce
					if (PlayerResources.GetCashAndResources() >= Info.ProductionMinCashRequirement)
					{
						foreach (var queue in queues)
						{
							var producing = queue.AllQueued().FirstOrDefault();
							if (producing == null)
								continue;

							if (BuildingsBeingProduced.TryGetValue(producing.Item, out var number))
								BuildingsBeingProduced[producing.Item] = number + 1;
							else
								BuildingsBeingProduced.Add(producing.Item, 1);
						}
					}
				}
			}

			if (--bankruptcyRecoveryTick <= 0)
			{
				bankruptcyRecoveryTick = Math.Max(1, Info.BankruptcyRecoveryInterval);
				TryRecoverBankruptEconomy(bot, queuesByCategory);
			}

			DecayDefenseDangerMemory();

			builders[currentBuilderIndex].Tick(bot, queuesByCategory);

			if (Info.SellRefineryInterval >= 0 && --sellRefineryTick <= 0)
			{
				SellUselessRefinery(bot);
				sellRefineryTick = Info.SellRefineryInterval;
			}
		}

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (e.Attacker == null || e.Attacker.Disposed)
				return;

			if (e.Attacker.Owner.RelationshipWith(self.Owner) != PlayerRelationship.Enemy)
				return;

			if (!e.Attacker.Info.HasTraitInfo<ITargetableInfo>())
				return;

			var isBuilding = self.Info.HasTraitInfo<BuildingInfo>();
			if (isBuilding && world.WorldTick >= nextDefenseCenterUpdateTick)
			{
				foreach (var n in positionsUpdatedModules)
					n.UpdatedDefenseCenter(e.Attacker.Location);

				nextDefenseCenterUpdateTick = world.WorldTick + Math.Max(1, Info.DefenseCenterUpdateInterval);
			}

			if (!Info.EnableDefenseDangerMemory || world.WorldTick < nextDefenseDangerMemoryRecordTick)
				return;

			var weight = Info.DefenseDangerMemoryAttackWeight;
			if (isBuilding)
				weight += Info.DefenseDangerMemoryBuildingAttackWeight;

			RecordDefenseDanger(e.Attacker.Location, ClassifyDefenseThreat(e.Attacker), weight);
			nextDefenseDangerMemoryRecordTick = world.WorldTick + Math.Max(1, Info.DefenseDangerMemoryRecordInterval);
		}

		DefenseRole ClassifyDefenseThreat(Actor attacker)
		{
			if (defenseThreatRoleCache.TryGetValue(attacker.Info.Name, out var cachedRole))
				return cachedRole;

			var isAir = false;
			var isInfantry = false;
			var isVehicle = false;

			foreach (var targetable in attacker.TraitsImplementing<ITargetable>())
			{
				foreach (var targetType in targetable.TargetTypes)
				{
					var targetTypeString = targetType.ToString();
					isAir |= CNBaseBuilderBotModuleInfo.DefaultAirThreatTargetTypes.Contains(targetTypeString);
					isInfantry |= CNBaseBuilderBotModuleInfo.DefaultInfantryThreatTargetTypes.Contains(targetTypeString);
					isVehicle |= CNBaseBuilderBotModuleInfo.DefaultVehicleThreatTargetTypes.Contains(targetTypeString);
				}
			}

			var role = DefenseRole.Default;
			if (isAir)
				role = DefenseRole.AADefense;
			else if (isInfantry)
				role = DefenseRole.InfantryDefense;
			else if (isVehicle)
				role = DefenseRole.ArmorDefense;

			defenseThreatRoleCache[attacker.Info.Name] = role;
			return role;
		}

		void RecordDefenseDanger(CPos location, DefenseRole role, int weight)
		{
			if (!Info.EnableDefenseDangerMemory || weight <= 0)
				return;

			var mergeRadiusSq = Math.Max(0, Info.DefenseDangerMemoryMergeRadius);
			mergeRadiusSq *= mergeRadiusSq;

			CPos? key = null;
			var bestDistance = int.MaxValue;
			foreach (var hotspot in defenseDangerMemory.Keys)
			{
				var distance = (hotspot - location).LengthSquared;
				if (distance > mergeRadiusSq || distance >= bestDistance)
					continue;

				key = hotspot;
				bestDistance = distance;
			}

			var memoryKey = key ?? location;
			if (!defenseDangerMemory.TryGetValue(memoryKey, out var memory))
			{
				memory = new DefenseDangerHotspot();
				defenseDangerMemory[memoryKey] = memory;
			}

			memory.Add(role, weight);

			var maxEntries = Math.Max(1, Info.DefenseDangerMemoryMaxEntries);
			while (defenseDangerMemory.Count > maxEntries)
			{
				var weakest = defenseDangerMemory.MinByOrDefault(kv => kv.Value.TotalWeight).Key;
				defenseDangerMemory.Remove(weakest);
			}
		}

		void DecayDefenseDangerMemory()
		{
			if (!Info.EnableDefenseDangerMemory || defenseDangerMemory.Count == 0)
				return;

			if (--defenseDangerMemoryDecayTick > 0)
				return;

			defenseDangerMemoryDecayTick = Math.Max(1, Info.DefenseDangerMemoryDecayInterval);
			var decay = Math.Max(1, Info.DefenseDangerMemoryDecayAmount);

			foreach (var key in defenseDangerMemory.Keys.ToArray())
			{
				defenseDangerMemory[key].Decay(decay);
				if (defenseDangerMemory[key].TotalWeight <= 0)
					defenseDangerMemory.Remove(key);
			}
		}

		public CPos? GetBestDefenseHotspot(CPos reference, DefenseRole role = DefenseRole.Default)
		{
			CPos? bestHotspot = null;

			if (Info.EnableDefenseDangerMemory && defenseDangerMemory.Count > 0)
			{
				var minimumWeight = Math.Max(1, Info.DefenseDangerMemoryMinimumWeight);
				var bestScore = int.MinValue;

				foreach (var kv in defenseDangerMemory)
				{
					var weight = role == DefenseRole.Default ? kv.Value.TotalWeight : kv.Value.GetRoleWeight(role);
					if (weight < minimumWeight)
						continue;

					// Prefer serious repeated attacks, but keep nearby pressure responsive.
					var score = weight - (kv.Key - reference).LengthSquared / 8;
					if (score <= bestScore)
						continue;

					bestScore = score;
					bestHotspot = kv.Key;
				}
			}

			// Early game: no attack recorded yet -> aim the first defenses at the nearest map chokepoint.
			if (bestHotspot == null && TacticalMapModule != null && Info.TopologyHotspotWeight > 0)
			{
				var topology = TacticalMapModule.GetTopologyHotspots(reference);
				var bestScore = long.MinValue;
				foreach (var threat in topology)
				{
					var score = (long)threat.Weight - (threat.Location - reference).LengthSquared / 8;
					if (score <= bestScore)
						continue;

					bestScore = score;
					bestHotspot = threat.Location;
				}
			}

			return bestHotspot;
		}

		public DefenseRole GetBestDefenseHotspotRole(CPos reference)
		{
			var hotspot = GetBestDefenseHotspot(reference);
			if (!hotspot.HasValue || !defenseDangerMemory.TryGetValue(hotspot.Value, out var memory))
				return DefenseRole.Default;

			return memory.GetDominantRole(Math.Max(1, Info.DefenseDangerMemoryMinimumWeight));
		}

		public DefensePlacementThreat[] GetDefensePlacementThreats(CPos reference, DefenseRole role = DefenseRole.Default)
		{
			var maxHotspots = Math.Max(1, Info.DefensePlacementMaxHotspots);
			var minimumWeight = Math.Max(1, Info.DefenseDangerMemoryMinimumWeight);
			var candidates = new List<(CPos Location, int Weight, long Score)>();

			// Reactive: remembered attack hotspots (dominant signal).
			if (Info.EnableDefenseDangerMemory && Info.DefensePlacementDangerWeight > 0)
			{
				foreach (var kv in defenseDangerMemory)
				{
					var weight = role == DefenseRole.Default ? kv.Value.TotalWeight : kv.Value.GetRoleWeight(role);
					if (weight < minimumWeight)
						continue;

					var finalWeight = weight * Info.DefensePlacementDangerWeight / 100;
					var score = (long)finalWeight - (kv.Key - reference).LengthSquared / 8;
					candidates.Add((kv.Key, finalWeight, score));
				}
			}

			// Proactive: static map topology (bridges, cliff ramps, narrow passages) from the cartographer.
			if (TacticalMapModule != null && Info.TopologyHotspotWeight > 0)
			{
				foreach (var threat in TacticalMapModule.GetTopologyHotspots(reference))
				{
					var finalWeight = threat.Weight * Info.TopologyHotspotWeight / 100;
					if (finalWeight <= 0)
						continue;

					var score = (long)finalWeight - (threat.Location - reference).LengthSquared / 8;
					candidates.Add((threat.Location, finalWeight, score));
				}
			}

			if (candidates.Count == 0)
				return [];

			return candidates
				.OrderByDescending(c => c.Score)
				.Take(maxHotspots)
				.Select(c => new DefensePlacementThreat(c.Location, c.Weight))
				.ToArray();
		}

		static long ScoreCellToward(CPos cell, CPos center, CPos target, int weight)
		{
			if (weight <= 0 || center == target)
				return 0;

			var toTarget = target - center;
			var toCell = cell - center;
			var targetLenSq = Math.Max(1, toTarget.LengthSquared);
			var dot = (long)toCell.X * toTarget.X + (long)toCell.Y * toTarget.Y;
			if (dot <= 0)
				return 0;

			var cross = (long)toCell.X * toTarget.Y - (long)toCell.Y * toTarget.X;
			var alignmentPenalty = Math.Abs(cross) / targetLenSq;

			return dot * weight / targetLenSq - alignmentPenalty * weight;
		}

		public long ScoreDefensePlacement(CPos cell, CPos defenseCenter, CPos enemyTarget, DefensePlacementThreat[] dangerHotspots)
		{
			var score = ScoreCellToward(cell, defenseCenter, enemyTarget, Info.DefensePlacementEnemyDirectionWeight);

			if (dangerHotspots != null)
			{
				foreach (var threat in dangerHotspots)
				{
					if (threat.Weight <= 0)
						continue;

					score += ScoreCellToward(cell, defenseCenter, threat.Location, threat.Weight);

					var distanceSq = (cell - threat.Location).LengthSquared;
					var radius = Math.Max(1, Info.MaximumDefenseRadius + 4);
					var radiusSq = radius * radius;
					if (distanceSq < radiusSq)
						score += (long)threat.Weight * (radiusSq - distanceSq) / radiusSq;
				}
			}

			score += ScoreTopologyTerrain(cell, defenseCenter);
			return score;
		}

		void EnsureFlankCache(CPos defenseCenter)
		{
			if (cachedFlankCenter == defenseCenter)
				return;

			cachedFlankCenter = defenseCenter;
			cachedAccessBearings = TacticalMapModule.GetAccessBearings(defenseCenter).ToList();
			cachedHighGround.Clear();
			foreach (var edge in TacticalMapModule.GetHighGroundEdges(defenseCenter))
				if (HighGroundEdgeFacesAccess(edge, defenseCenter))
					cachedHighGround[edge.Cell] = edge;

			cachedChokepointDefenseAnchors = TacticalMapModule.GetChokepointDefenseAnchors(defenseCenter).ToList();
		}

		// High-ground (height advantage / natural wall) bonus and sealed-flank (no enemy access) penalty.
		long ScoreTopologyTerrain(CPos cell, CPos defenseCenter)
		{
			if (TacticalMapModule == null)
				return 0;

			EnsureFlankCache(defenseCenter);
			long adjust = 0;

			// Reward cells on a high-ground cliff edge overlooking a reachable approach.
			if (Info.HighGroundEdgeWeight > 0 && cachedHighGround.TryGetValue(cell, out var edge))
				adjust += (long)Info.HighGroundEdgeWeight * (1 + edge.HeightLevels);

			if (Info.ChokepointDefenseAnchorWeight > 0 && cachedChokepointDefenseAnchors.Count > 0)
			{
				var bestAnchorSq = cachedChokepointDefenseAnchors.Min(a => (cell - a).LengthSquared);
				const int AnchorRadius = 4;
				const int AnchorRadiusSq = AnchorRadius * AnchorRadius;
				if (bestAnchorSq < AnchorRadiusSq)
					adjust += (long)Info.ChokepointDefenseAnchorWeight * (AnchorRadiusSq - bestAnchorSq) / AnchorRadiusSq;
			}

			// Penalize cells facing a sealed flank (no access corridor: map edge / cliff / water).
			if (Info.SealedFlankPenaltyWeight > 0 && cachedAccessBearings.Count > 0)
			{
				var v = cell - defenseCenter;
				if (v.LengthSquared >= 9)
				{
					var aligned = false;
					foreach (var b in cachedAccessBearings)
					{
						var dot = (long)v.X * b.X + (long)v.Y * b.Y;
						if (dot <= 0)
							continue;

						// Within ~45 degrees of this corridor bearing counts as "facing an access".
						var cross = (long)v.X * b.Y - (long)v.Y * b.X;
						if (cross * cross <= dot * dot)
						{
							aligned = true;
							break;
						}
					}

					if (!aligned)
						adjust -= Info.SealedFlankPenaltyWeight;
				}
			}

			return adjust;
		}

		bool HighGroundEdgeFacesAccess(CNHighGroundEdge edge, CPos defenseCenter)
		{
			if (cachedAccessBearings.Count == 0)
				return false;

			return VectorFacesAccess(edge.Cell - defenseCenter) && VectorFacesAccess(edge.Outward);
		}

		bool VectorFacesAccess(CVec v)
		{
			if (v.LengthSquared == 0)
				return true;

			foreach (var b in cachedAccessBearings)
			{
				var dot = (long)v.X * b.X + (long)v.Y * b.Y;
				if (dot <= 0)
					continue;

				var cross = (long)v.X * b.Y - (long)v.Y * b.X;
				if (cross * cross <= dot * dot)
					return true;
			}

			return false;
		}

		public long ScoreTechPlacementSafety(CPos cell, CPos coreCenter, DefensePlacementThreat[] dangerHotspots)
		{
			var score = (long)(cell - coreCenter).LengthSquared * Math.Max(0, Info.TechPlacementCoreBiasWeight);

			if (dangerHotspots == null || dangerHotspots.Length == 0 || Info.TechPlacementDangerAvoidanceWeight <= 0)
				return score;

			var radius = Math.Max(1, Info.MaximumDefenseRadius + 6);
			var radiusSq = radius * radius;
			foreach (var threat in dangerHotspots)
			{
				if (threat.Weight <= 0)
					continue;

				var toThreat = threat.Location - coreCenter;
				var toCell = cell - coreCenter;
				var threatLenSq = Math.Max(1, toThreat.LengthSquared);
				var approachDot = (long)toCell.X * toThreat.X + (long)toCell.Y * toThreat.Y;
				if (approachDot > 0)
				{
					// Tech buildings are high-value targets: penalize the whole approach side,
					// not only cells that are very close to the exact chokepoint/hotspot.
					score += approachDot * threat.Weight * Info.TechPlacementDangerAvoidanceWeight / (25 * threatLenSq);
				}

				var distanceSq = (cell - threat.Location).LengthSquared;
				if (distanceSq >= radiusSq)
					continue;

				score += (long)threat.Weight * Info.TechPlacementDangerAvoidanceWeight * (radiusSq - distanceSq) / (100 * radiusSq);
			}

			return score;
		}

		sealed class DefenseDangerHotspot
		{
			readonly Dictionary<DefenseRole, int> roleWeights = [];

			public int TotalWeight { get; private set; }

			public void Add(DefenseRole role, int weight)
			{
				TotalWeight = Math.Min(10000, TotalWeight + weight);

				if (role == DefenseRole.Default)
					return;

				roleWeights.TryGetValue(role, out var oldWeight);
				roleWeights[role] = Math.Min(10000, oldWeight + weight);
			}

			public int GetRoleWeight(DefenseRole role)
			{
				if (role == DefenseRole.Default)
					return TotalWeight;

				return roleWeights.TryGetValue(role, out var weight) ? weight : 0;
			}

			public DefenseRole GetDominantRole(int minimumWeight)
			{
				var bestRole = DefenseRole.Default;
				var bestWeight = minimumWeight - 1;
				foreach (var kv in roleWeights)
				{
					if (kv.Value <= bestWeight)
						continue;

					bestWeight = kv.Value;
					bestRole = kv.Key;
				}

				return bestRole;
			}

			public void Decay(int amount)
			{
				TotalWeight = Math.Max(0, TotalWeight - amount);
				foreach (var key in roleWeights.Keys.ToArray())
				{
					var value = roleWeights[key] - amount;
					if (value <= 0)
						roleWeights.Remove(key);
					else
						roleWeights[key] = value;
				}
			}
		}

		void SetRallyPoint(IBot bot, TraitPair<RallyPoint> rp)
		{
			var needsRallyPoint = rp.Trait.Path.Count == 0;

			if (!needsRallyPoint)
			{
				var locomotors = LocomotorsForProducibles(rp.Actor);
				needsRallyPoint = !IsRallyPointValid(rp.Actor.Location, rp.Trait.Path[0], locomotors, rp.Actor.Info.TraitInfoOrDefault<BuildingInfo>());
			}

			if (needsRallyPoint)
			{
				bot.QueueOrder(new Order("SetRallyPoint", rp.Actor, Target.FromCell(world, ChooseRallyLocationNear(rp.Actor)), false)
				{
					SuppressVisualFeedback = true
				});
			}
		}

		// Won't work for shipyards...
		CPos ChooseRallyLocationNear(Actor producer)
		{
			var locomotors = LocomotorsForProducibles(producer);
			var possibleRallyPoints = world.Map.FindTilesInCircle(producer.Location, Info.RallyPointScanRadius)
				.Where(c => IsRallyPointValid(producer.Location, c, locomotors, producer.Info.TraitInfoOrDefault<BuildingInfo>()))
				.ToList();

			if (possibleRallyPoints.Count == 0)
			{
				AIUtils.BotDebug("{0} has no possible rallypoint near {1}", producer.Owner, producer.Location);
				return producer.Location;
			}

			return possibleRallyPoints.Random(world.LocalRandom);
		}

		Locomotor[] LocomotorsForProducibles(Actor producer)
		{
			// Per-actor production
			var productions = producer.TraitsImplementing<Production>();

			// Player-wide production
			if (!productions.Any())
				productions = producer.World.ActorsWithTrait<Production>().Where(x => x.Actor.Owner != producer.Owner).Select(x => x.Trait);

			var produces = productions.SelectMany(p => p.Info.Produces).ToHashSet();
			var locomotors = Array.Empty<Locomotor>();
			if (produces.Count > 0)
			{
				// Per-actor production
				var productionQueues = producer.TraitsImplementing<ProductionQueue>();

				// Player-wide production
				if (!productionQueues.Any())
					productionQueues = producer.Owner.PlayerActor.TraitsImplementing<ProductionQueue>();

				productionQueues = productionQueues.Where(pq => produces.Contains(pq.Info.Type));

				var producibles = productionQueues.SelectMany(pq => pq.BuildableItems());
				var locomotorNames = producibles
					.Select(p => p.TraitInfoOrDefault<MobileInfo>())
					.Where(mi => mi != null)
					.Select(mi => mi.Locomotor)
					.ToHashSet();

				if (locomotorNames.Count != 0)
					locomotors = world.WorldActor.TraitsImplementing<Locomotor>()
						.Where(l => locomotorNames.Contains(l.Info.Name))
						.ToArray();
			}

			return locomotors;
		}

		bool IsRallyPointValid(CPos producerLocation, CPos rallyPointLocation, Locomotor[] locomotors, BuildingInfo buildingInfo)
		{
			return
				(PathFinder == null ||
					locomotors.All(l => PathFinder.PathMightExistForLocomotorBlockedByImmovable(l, producerLocation, rallyPointLocation)))
				&&
				(buildingInfo == null ||
					world.IsCellBuildable(rallyPointLocation, null, buildingInfo));
		}

		// Require at least one refinery, unless we can't build it.
		public bool HasAdequateRefineryCount() =>
			Info.RefineryTypes.Count == 0 ||
			AIUtils.CountActorByCommonName(RefineryBuildings) >= OptimalRefineryCount() ||
			AIUtils.CountActorByCommonName(powerBuildings) == 0 ||
			AIUtils.CountActorByCommonName(ConstructionYardBuildings) == 0;

		int OptimalRefineryCount() =>
			GetTargetRefineryCount();
		public bool HasMinimalRefineryCount() =>
			AIUtils.CountActorByCommonName(RefineryBuildings) >= GetActiveInititalMinimumRefineryCount();

		public bool HasEconomyRecoveryPath()
		{
			return HasEconomyRecoveryPath(GetCachedQueues());
		}

		bool HasEconomyRecoveryPath(ILookup<string, ProductionQueue> queuesByCategory)
		{
			return AIUtils.CountActorByCommonName(ConstructionYardBuildings) > 0 ||
				AIUtils.CountActorByCommonName(RefineryBuildings) > 0 ||
				HasQueuedOrProducingActor(Info.RefineryTypes, queuesByCategory) ||
				HasQueuedOrProducingActor(Info.McvTypes, queuesByCategory) ||
				GetCheapestBuildableActorCost(Info.RefineryTypes, queuesByCategory) > 0 ||
				GetCheapestBuildableActorCost(Info.McvTypes, queuesByCategory) > 0;
		}

		public int GetTargetRefineryCount()
		{
			var productions = AIUtils.CountActorByCommonName(ProductionBuildings);
			var constructionYards = AIUtils.CountActorByCommonName(ConstructionYardBuildings);

			var activeMinRefinery = GetActiveInititalMinimumRefineryCount();
			var target = productions > 0
				? activeMinRefinery + Info.AdditionalMinimumRefineryCount
				: activeMinRefinery;

			if (productions > 1)
				target += (productions - 1) / 2;

			if (constructionYards > 1)
				target += constructionYards - 1;

			if (RequestedRefineries.Count > 0)
				target++;

			if (Info.EconomyOverflowRefineryBonus > 0 && EconomyOverflowMilli > 0)
				target += Info.EconomyOverflowRefineryBonus * EconomyOverflowMilli / 1000;

			var supportedCapacity = GetSupportedRefineryCapacity();
			target = Math.Min(target, supportedCapacity);

			return Math.Max(activeMinRefinery, target);
		}

		void UpdateEconomyOverflow()
		{
			if (PlayerResources == null)
				return;

			var cash = PlayerResources.GetCashAndResources();
			var earned = PlayerResources.Earned;
			var tick = world.WorldTick;

			economySamples.Enqueue((cash, earned, tick));

			var window = Math.Max(1, Info.EconomyOverflowSampleWindow);
			while (economySamples.Count > window)
				economySamples.Dequeue();

			if (economySamples.Count < 2)
			{
				EconomyOverflowMilli = 0;
				return;
			}

			long cashSum = 0;
			foreach (var (sampleCash, _, _) in economySamples)
				cashSum += sampleCash;
			var avgCash = (int)(cashSum / economySamples.Count);

			var (_, oldestEarned, oldestTick) = economySamples.Peek();
			var dt = Math.Max(1, tick - oldestTick);
			var incomeRate = (earned - oldestEarned) / dt;
			IncomeRate = incomeRate;

			var cashFloor = Info.EconomyOverflowCashFloor;
			var cashCeil = Math.Max(cashFloor + 1, Info.EconomyOverflowCashCeiling);
			var cashScoreMilli = Math.Clamp((long)(avgCash - cashFloor) * 1000 / (cashCeil - cashFloor), 0L, 1000L);

			var incomeFloor = Info.EconomyOverflowIncomeFloor;
			var incomeCeil = Math.Max(incomeFloor + 1, Info.EconomyOverflowIncomeCeiling);
			var incomeScoreMilli = Math.Clamp((long)(incomeRate - incomeFloor) * 1000 / (incomeCeil - incomeFloor), 0L, 1000L);

			var weightSum = Math.Max(1, Info.EconomyOverflowCashWeight + Info.EconomyOverflowIncomeWeight);
			var blended = (cashScoreMilli * Info.EconomyOverflowCashWeight + incomeScoreMilli * Info.EconomyOverflowIncomeWeight) / weightSum;

			EconomyOverflowMilli = (int)Math.Clamp(blended, 0L, 1000L);
		}

		public int GetScaledBuildingLimit(int baseLimit)
		{
			if (baseLimit <= 0 || EconomyOverflowMilli <= 0 || Info.EconomyOverflowBuildingLimitBonusPct <= 0)
				return baseLimit;

			var bonus = baseLimit * Info.EconomyOverflowBuildingLimitBonusPct * EconomyOverflowMilli / (100 * 1000);
			return baseLimit + bonus;
		}

		public bool HasEconomicFloat()
		{
			return HasEconomicFloatFor(GetActiveNewProductionCashThreshold());
		}

		public bool HasEconomicFloatFor(int threshold)
		{
			var cash = PlayerResources.GetCashAndResources();
			if (cash < threshold)
				return false;

			var productions = Math.Max(1, AIUtils.CountActorByCommonName(ProductionBuildings));
			return cash >= threshold + productions * 1000;
		}

		int GetProductionCategoryThreshold()
		{
			if (Info.BudgetNewProductionCashThresholds != null &&
				Info.BudgetNewProductionCashThresholds.TryGetValue("Production", out var v))
				return v;

			return Info.NewProductionCashThreshold;
		}

		public bool HasStoragePressure()
		{
			return PlayerResources.ResourceCapacity > 0 &&
				PlayerResources.Resources * 100 >= PlayerResources.ResourceCapacity * 70;
		}

		public int CountPendingRefineriesForIndice(CNResourceIndice indice)
		{
			if (ResourceMapModule == null || indice == null)
				return 0;

			return RequestedRefineries.Values.Count(req =>
			{
				var pendingIndice = ResourceMapModule.FindClosestIndiceFromCPos(req.ResourceLoc);
				return pendingIndice != null && pendingIndice.IndiceCenter == indice.IndiceCenter;
			});
		}

		public bool CanSupportAnotherRefinery(CNResourceIndice indice)
		{
			if (indice == null)
				return false;

			return CanSupportAnotherRefinery(indice, CountPendingRefineriesForIndice(indice));
		}

		public bool CanSupportAnotherRefinery(CNResourceIndice indice, int pendingRefineries)
		{
			if (indice == null)
				return false;

			var totalRefineries = indice.PlayerRefineryCount + pendingRefineries;
			if (Info.MaxRefineryPerIndice > 0 && totalRefineries >= Info.MaxRefineryPerIndice)
				return false;

			if (indice.HasRespawningResourceSource)
			{
				if (totalRefineries <= 0)
					return indice.ResourceCellsCount > 0 || indice.ResourceCreatorLocs.Length > 0;

				return indice.ResourceCellsCount >= Info.MinRespawningFieldCellsForExtraRefinery;
			}

			if (indice.ResourceCellsCount < Info.MinFiniteFieldCellsForRefinery)
				return false;

			if (totalRefineries <= 0)
				return true;

			return indice.ResourceCellsCount >= Info.MinFiniteFieldCellsForExtraRefinery;
		}

		public bool CanSupportAnotherRefinery(CPos resourceLoc)
		{
			return ResourceMapModule == null || CanSupportAnotherRefinery(ResourceMapModule.FindClosestIndiceFromCPos(resourceLoc));
		}

		public bool HasViableRefineryExpansionOpportunity()
		{
			if (ResourceMapModule == null)
				return true;

			for (var i = 0; i < ResourceMapModule.GetIndicesLength(); i++)
			{
				var indice = ResourceMapModule.GetIndice(i);
				if (indice == null || indice.ResourceCellsCount <= 0)
					continue;

				if (CanSupportAnotherRefinery(indice))
					return true;
			}

			return false;
		}

		public bool ShouldExpandEconomy()
		{
			if (RequestedRefineries.Count > 0)
				return true;

			if (!HasViableRefineryExpansionOpportunity())
				return false;

			return !HasAdequateRefineryCount();
		}

		int GetSupportedRefineryCapacity()
		{
			if (ResourceMapModule == null)
				return int.MaxValue;

			var supportedCapacity = 0;
			for (var i = 0; i < ResourceMapModule.GetIndicesLength(); i++)
				supportedCapacity += GetSupportedRefineryCapacity(ResourceMapModule.GetIndice(i));

			return Math.Max(Info.InititalMinimumRefineryCount, supportedCapacity);
		}

		int GetSupportedRefineryCapacity(CNResourceIndice indice)
		{
			if (indice == null)
				return 0;

			var maxPerIndice = Math.Max(1, Info.MaxRefineryPerIndice > 0 ? Info.MaxRefineryPerIndice : 2);

			if (indice.HasRespawningResourceSource)
			{
				if (indice.ResourceCellsCount <= 0 && indice.ResourceCreatorLocs.Length <= 0)
					return 0;

				return indice.ResourceCellsCount >= Info.MinRespawningFieldCellsForExtraRefinery
					? maxPerIndice
					: 1;
			}

			if (indice.ResourceCellsCount < Info.MinFiniteFieldCellsForRefinery)
				return 0;

			return indice.ResourceCellsCount >= Info.MinFiniteFieldCellsForExtraRefinery
				? maxPerIndice
				: 1;
		}

		public bool ShouldAddProduction()
		{
			if (!HasAdequateRefineryCount())
				return false;

			return HasEconomicFloatFor(GetProductionCategoryThreshold()) && !HasStoragePressure();
		}

		public int GetExpansionCashThrottle()
		{
			if (!HasEconomicFloat())
				return PlayerResources.GetCashAndResources() / Math.Max(Info.PerExpansionTolerateOnCash, 1);

			return Math.Max(0, PlayerResources.GetCashAndResources() / Math.Max(Info.PerExpansionTolerateOnCash * 2, 1));
		}

		public int CountNearbyValuableResources(CPos center, int radius)
		{
			if (resourceLayer == null)
				return 0;

			return world.Map.FindTilesInAnnulus(center, 0, radius)
				.Count(c => ResourceMapModule != null
					? ResourceMapModule.Info.ValuableResourceTypes.Contains(resourceLayer.GetResource(c).Type)
					: resourceLayer.GetResource(c).Type != null);
		}

		public bool HasNearbyRespawningResourceSource(CPos center, int radius)
		{
			if (ResourceMapModule == null)
				return false;

			return world.FindActorsInCircle(world.Map.CenterOfCell(center), WDist.FromCells(radius))
				.Any(a => ResourceMapModule.Info.ResourceCreatorTypes.Contains(a.Info.Name) && a.TraitOrDefault<ISeedableResource>() != null);
		}

		CNResourceIndice GetRefineryIndice(Actor refinery)
		{
			return ResourceMapModule?.FindClosestIndiceFromCPos(refinery.Location);
		}

		bool IsFiniteRefineryExhausted(Actor refinery)
		{
			var nearbyResources = CountNearbyValuableResources(refinery.Location, Info.SellRefineryNoResourceDistance);
			if (nearbyResources > 0)
				return false;

			if (HasNearbyRespawningResourceSource(refinery.Location, Info.SellRefineryNoResourceDistance))
				return false;

			return !(GetRefineryIndice(refinery)?.HasRespawningResourceSource ?? false);
		}

		bool IsFiniteRefineryLowValue(Actor refinery)
		{
			var indice = GetRefineryIndice(refinery);
			if (indice == null || indice.HasRespawningResourceSource || indice.PlayerRefineryCount <= 1)
				return false;

			var nearbyResources = CountNearbyValuableResources(refinery.Location, Info.SellRefineryNoResourceDistance);
			return nearbyResources <= Info.SellRefineryLowResourceThreshold || indice.ResourceCellsCount <= Info.SellRefineryLowResourceThreshold;
		}

		int ScoreRefinerySellCandidate(Actor refinery)
		{
			var score = 0;
			var indice = GetRefineryIndice(refinery);
			var nearbyResources = CountNearbyValuableResources(refinery.Location, Info.SellRefineryNoResourceDistance);
			var hasNearbyRespawn = HasNearbyRespawningResourceSource(refinery.Location, Info.SellRefineryNoResourceDistance)
				|| (indice?.HasRespawningResourceSource ?? false);

			if (!hasNearbyRespawn && nearbyResources <= 0)
				score += 1000;

			if (IsFiniteRefineryLowValue(refinery))
				score += 500;

			if (indice != null && indice.PlayerRefineryCount > 1)
				score += (indice.PlayerRefineryCount - 1) * 100;

			if (indice != null && indice.ResourceCellsCenter != CPos.Zero)
				score += (refinery.Location - indice.ResourceCellsCenter).LengthSquared;

			return score;
		}

		int CountResourceCellsNear(CPos center, int radius, FrozenSet<string> resourceTypes)
		{
			if (resourceLayer == null || resourceTypes.Count == 0)
				return 0;

			return world.Map.FindTilesInAnnulus(center, 0, radius)
				.Count(c => resourceTypes.Contains(resourceLayer.GetResource(c).Type));
		}

		// Cached once per game: veins don't appear mid-game on pre-placed maps.
		public bool HasVeinResources()
		{
			if (Info.VeinsOnlyResourceTypes.Count == 0)
				return false;
			if (veinsChecked)
				return cachedVeinsExist;
			veinsChecked = true;

			if (ResourceMapModule != null)
			{
				var minCells = Math.Max(1, Info.VeinsOnlyMinimumResourceCells);
				for (var i = 0; i < ResourceMapModule.GetIndicesLength(); i++)
				{
					var indice = ResourceMapModule.GetIndice(i);
					if (indice == null)
						continue;

					if (CountResourceCellsNear(indice.IndiceCenter, ResourceMapModule.GetIndiceScanRadius(), Info.VeinsOnlyResourceTypes) >= minCells)
					{
						cachedVeinsExist = true;
						return true;
					}
				}
			}

			cachedVeinsExist = resourceLayer != null &&
				world.Map.AllCells.Count(c => Info.VeinsOnlyResourceTypes.Contains(resourceLayer.GetResource(c).Type)) >=
				Math.Max(1, Info.VeinsOnlyMinimumResourceCells);
			return cachedVeinsExist;
		}

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			return
			[
				new("InitialBaseCenter", FieldSaver.FormatValue(initialBaseCenter)),
				new("DefenseCenter", FieldSaver.FormatValue(DefenseCenter))
			];
		}

		void SellUselessRefinery(IBot bot)
		{
			// Sell one refinery each time. Preserve at least one refinery.
			var refineries = world.ActorsHavingTrait<Refinery>().Where(a => a.Owner == player).ToArray();

			if (refineries.Length <= GetActiveInititalMinimumRefineryCount() + Info.AdditionalMinimumRefineryCount)
				return;

			// A refinery is active if a harvester is currently docked/reserved at its dock,
			// or if a player-owned harvester is within SellRefineryNoResourceDistance cells
			// (i.e. on its way back or harvesting the nearby field).
			bool IsRefineryActive(Actor refinery)
			{
				if (refinery.TraitsImplementing<IDockHost>().Any(host => host.ReservationCount > 0))
					return true;

				var radiusSq = Info.SellRefineryNoResourceDistance * Info.SellRefineryNoResourceDistance;
				return world.ActorsHavingTrait<Harvester>()
					.Any(h => h.Owner == player && !h.IsDead && h.IsInWorld
						&& (h.Location - refinery.Location).LengthSquared <= radiusSq);
			}

			for (var i = 0; i < refineries.Length; i++)
			{
				for (var j = i + 1; j < refineries.Length; j++)
				{
					if ((refineries[i].Location - refineries[j].Location).LengthSquared <= Info.SellRefineryTooCloseCellDistance * Info.SellRefineryTooCloseCellDistance)
					{
						// Prefer selling the inactive one; skip the pair if both are active.
						var sellTarget = !IsRefineryActive(refineries[i]) ? refineries[i]
									   : !IsRefineryActive(refineries[j]) ? refineries[j]
									   : null;

						if (sellTarget != null)
						{
							bot.QueueOrder(new Order("Sell", sellTarget, Target.FromActor(sellTarget), false));
							return;
						}
					}
				}
			}

			if (ResourceMapModule == null)
				return;

			Actor bestCandidate = null;
			var bestScore = 0;
			foreach (var refinery in refineries)
			{
				if (IsRefineryActive(refinery))
					continue;

				if (!IsFiniteRefineryExhausted(refinery) && !IsFiniteRefineryLowValue(refinery))
					continue;

				var score = ScoreRefinerySellCandidate(refinery);
				if (score > bestScore)
				{
					bestScore = score;
					bestCandidate = refinery;
				}
			}

			if (bestCandidate != null)
				bot.QueueOrder(new Order("Sell", bestCandidate, Target.FromActor(bestCandidate), false));
		}

		int GetCheapestBuildableRefineryCost(ILookup<string, ProductionQueue> queuesByCategory)
			=> GetCheapestBuildableActorCost(Info.RefineryTypes, queuesByCategory);

		bool HasQueuedOrProducingRefinery(ILookup<string, ProductionQueue> queuesByCategory)
			=> HasQueuedOrProducingActor(Info.RefineryTypes, queuesByCategory);

		static int GetCheapestBuildableActorCost(IReadOnlyCollection<string> types, ILookup<string, ProductionQueue> queuesByCategory)
		{
			if (types == null || types.Count == 0)
				return -1;

			var best = int.MaxValue;
			foreach (var queue in queuesByCategory.SelectMany(q => q))
			{
				foreach (var item in queue.BuildableItems())
				{
					if (!types.Contains(item.Name))
						continue;

					best = Math.Min(best, queue.GetProductionCost(item));
				}
			}

			return best == int.MaxValue ? -1 : best;
		}

		bool HasQueuedOrProducingActor(IReadOnlyCollection<string> types, ILookup<string, ProductionQueue> queuesByCategory)
		{
			if (types == null || types.Count == 0)
				return false;

			if (BuildingsBeingProduced.Keys.Any(types.Contains))
				return true;

			return queuesByCategory.SelectMany(q => q)
				.SelectMany(q => q.AllQueued())
				.Any(p => types.Contains(p.Item));
		}

		Actor ChooseEmergencySellCandidate()
		{
			var buildings = world.ActorsHavingTrait<Building>().Where(a => a.Owner == player && !a.IsDead && a.IsInWorld).ToArray();

			if (buildings.Length == 0)
				return null;

			var powerCount = AIUtils.CountActorByCommonName(powerBuildings);
			var conyardCount = AIUtils.CountActorByCommonName(ConstructionYardBuildings);
			var productionCount = AIUtils.CountActorByCommonName(ProductionBuildings);

			Actor best = null;
			var bestScore = int.MinValue;

			foreach (var building in buildings)
			{
				if (Info.RefineryTypes.Contains(building.Info.Name))
					continue;

				// Never sell the last conyard: without it we cannot recover.
				if (Info.ConstructionYardTypes.Contains(building.Info.Name) && conyardCount <= 1)
				{
					continue;
				}

				// Keep at least one production building and enough power to actually place the refinery afterwards.
				if (Info.ProductionTypes.Contains(building.Info.Name) && productionCount <= 1)
					continue;

				if (Info.PowerTypes.Contains(building.Info.Name) && powerCount <= 1)
					continue;

				var score = 0;
				var valued = building.Info.TraitInfoOrDefault<ValuedInfo>();
				var recoverValue = (valued?.Cost ?? 0) / 2;
				score += recoverValue;

				if (Info.DefenseTypes.Contains(building.Info.Name))
					score += 3000;
				else if (Info.TechTypes.Contains(building.Info.Name))
					score += 2400;
				else if (Info.SiloTypes.Contains(building.Info.Name))
					score += 2000;
				else if (Info.ProductionTypes.Contains(building.Info.Name))
					score += 800;
				else if (Info.PowerTypes.Contains(building.Info.Name))
					score += 400;
				else
					score += 1200;

				// Prefer surplus copies instead of unique tech/core structures.
				var sameTypeCount = buildings.Count(a => a.Info.Name == building.Info.Name);
				if (sameTypeCount > 1)
					score += (sameTypeCount - 1) * 600;

				// Avoid selling buildings that are helping with repairs or unit production unless they are duplicates.
				if (building.Info.Name == "gadept" && sameTypeCount <= 1)
					score -= 1000;

				if (score > bestScore)
				{
					bestScore = score;
					best = building;
				}
			}

			return best;
		}

		void TryRecoverBankruptEconomy(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			// Recovery tiers — each returns true if it took an action (queued a sell), false otherwise.
			// Tier 1: missing refinery, conyard alive → sell to fund refinery.
			if (TryRecoverRefinery(bot, queuesByCategory))
				return;

			// Tier 2: refinery alive but no harvester → sell to fund harvester.
			if (TryRecoverHarvester(bot, queuesByCategory))
				return;

			// Tier 3: no conyard but a factory can still produce an MCV → sell to fund MCV.
			if (TryRecoverMcv(bot, queuesByCategory))
				return;

			// Tier 4: no path back. Spend remaining buildings on a kamikaze attack so the bot dies
			// loudly instead of sitting on a useless rump base.
			TryTerminalRecovery(bot, queuesByCategory);
		}

		bool TryRecoverRefinery(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			if (Info.RefineryTypes.Count == 0)
				return false;

			if (AIUtils.CountActorByCommonName(RefineryBuildings) > 0)
				return false;

			if (RequestedRefineries.Count > 0 || HasQueuedOrProducingRefinery(queuesByCategory))
				return false;

			if (AIUtils.CountActorByCommonName(ConstructionYardBuildings) <= 0)
				return false;

			var refineryCost = GetCheapestBuildableRefineryCost(queuesByCategory);
			if (refineryCost <= 0)
				return false;

			var cash = PlayerResources.GetCashAndResources();
			if (Info.BankruptcyRecoveryMaxCash >= 0 && cash > Info.BankruptcyRecoveryMaxCash)
				return false;

			var shortfall = refineryCost - cash;
			if (shortfall <= Info.BankruptcyRecoveryMinimumShortfall)
				return false;

			// Active income → wait for cash to accumulate instead of preemptively selling.
			// The sampling window (~300 ticks) keeps this positive for a while after the last
			// harvester dump, which naturally debounces brief refinery losses.
			if (IncomeRate > 0)
				return false;

			var sellCandidate = ChooseEmergencySellCandidate();
			if (sellCandidate == null)
				return false;

			AIUtils.BotDebug($"CN AI: Selling {sellCandidate} to recover refinery economy. Cash {cash}, refinery cost {refineryCost}.");
			bot.QueueOrder(new Order("Sell", sellCandidate, Target.FromActor(sellCandidate), false));
			return true;
		}

		bool TryRecoverHarvester(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			if (Info.HarvesterTypes.Count == 0)
				return false;

			// Only meaningful while we still have at least one refinery to dock at.
			if (AIUtils.CountActorByCommonName(RefineryBuildings) <= 0)
				return false;

			if (HasLiveHarvester())
				return false;

			if (HasQueuedOrProducingActor(Info.HarvesterTypes, queuesByCategory))
				return false;

			var harvesterCost = GetCheapestBuildableActorCost(Info.HarvesterTypes, queuesByCategory);
			if (harvesterCost <= 0)
				return false;

			var cash = PlayerResources.GetCashAndResources();
			if (Info.BankruptcyRecoveryMaxCash >= 0 && cash > Info.BankruptcyRecoveryMaxCash)
				return false;

			var shortfall = harvesterCost - cash;
			if (shortfall <= Info.BankruptcyRecoveryMinimumShortfall)
				return false;

			// Active income → another harvester is probably still dumping. Wait it out.
			if (IncomeRate > 0)
				return false;

			var sellCandidate = ChooseEmergencySellCandidate();
			if (sellCandidate == null)
				return false;

			AIUtils.BotDebug($"CN AI: Selling {sellCandidate} to recover harvester. Cash {cash}, harvester cost {harvesterCost}.");
			bot.QueueOrder(new Order("Sell", sellCandidate, Target.FromActor(sellCandidate), false));
			return true;
		}

		bool TryRecoverMcv(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			if (Info.McvTypes.Count == 0)
				return false;

			// Skip if we still have a conyard — the standard tech ladder will re-deploy from there.
			if (AIUtils.CountActorByCommonName(ConstructionYardBuildings) > 0)
				return false;

			if (HasQueuedOrProducingActor(Info.McvTypes, queuesByCategory))
				return false;

			var mcvCost = GetCheapestBuildableActorCost(Info.McvTypes, queuesByCategory);
			if (mcvCost <= 0)
				return false;

			var cash = PlayerResources.GetCashAndResources();
			if (Info.BankruptcyRecoveryMaxCash >= 0 && cash > Info.BankruptcyRecoveryMaxCash)
				return false;

			var shortfall = mcvCost - cash;
			if (shortfall <= Info.BankruptcyRecoveryMinimumShortfall)
				return false;

			// Active income → economy is still alive (e.g. lost conyard mid-game while a refinery
			// still works). Wait for the next dump before tearing down buildings.
			if (IncomeRate > 0)
				return false;

			var sellCandidate = ChooseEmergencySellCandidate();
			if (sellCandidate == null)
				return false;

			AIUtils.BotDebug($"CN AI: Selling {sellCandidate} to recover MCV. Cash {cash}, MCV cost {mcvCost}.");
			bot.QueueOrder(new Order("Sell", sellCandidate, Target.FromActor(sellCandidate), false));
			return true;
		}

		void TryTerminalRecovery(IBot bot, ILookup<string, ProductionQueue> queuesByCategory)
		{
			if (!Info.BankruptcyKamikazeEnabled || terminalRecoveryTriggered)
				return;

			var cash = PlayerResources.GetCashAndResources();
			if (cash > Info.BankruptcyKamikazeMaxCash)
				return;

			if (HasEconomyRecoveryPath(queuesByCategory))
				return;

			terminalRecoveryTriggered = true;
			AIUtils.BotDebug($"CN AI: Terminal bankruptcy — selling all and going kamikaze. Cash {cash}.");

			ExecuteKamikaze(bot);
		}

		bool HasLiveHarvester()
		{
			foreach (var actor in world.ActorsHavingTrait<Harvester>())
				if (actor.Owner == player && !actor.IsDead && actor.IsInWorld)
					return true;

			return false;
		}

		void ExecuteKamikaze(IBot bot)
		{
			// Pick a target: nearest enemy building if any.
			Actor target = null;
			var anyOwnUnit = world.ActorsHavingTrait<Mobile>()
				.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.IsInWorld);

			if (anyOwnUnit != null)
				target = FindClosestEnemyBuilding(anyOwnUnit);

			target ??= world.Actors.FirstOrDefault(a =>
					!a.IsDead && a.IsInWorld &&
					a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy &&
					a.Info.HasTraitInfo<BuildingInfo>());

			if (target != null)
			{
				var attackTarget = Target.FromCell(world, world.Map.CellContaining(target.CenterPosition));

				foreach (var unit in world.ActorsHavingTrait<Mobile>())
				{
					if (unit.Owner != player || unit.IsDead || !unit.IsInWorld)
						continue;
					bot.QueueOrder(new Order("AttackMove", unit, attackTarget, false));
				}

				foreach (var aircraft in world.ActorsHavingTrait<Aircraft>())
				{
					if (aircraft.Owner != player || aircraft.IsDead || !aircraft.IsInWorld)
						continue;
					bot.QueueOrder(new Order("AttackMove", aircraft, attackTarget, false));
				}
			}

			// Sell every remaining building so the cash refund fuels the dying attack and the
			// rump base disappears instead of sitting around as a passive husk.
			foreach (var building in world.ActorsHavingTrait<Building>())
			{
				if (building.Owner != player || building.IsDead || !building.IsInWorld)
					continue;
				bot.QueueOrder(new Order("Sell", building, Target.FromActor(building), false));
			}
		}

		Actor FindClosestEnemyBuilding(Actor sourceActor)
		{
			if (sourceActor == null)
				return null;

			Actor closest = null;
			var bestDistSq = long.MaxValue;
			var sourcePos = sourceActor.CenterPosition;

			foreach (var actor in world.Actors)
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;
				if (actor.Owner.RelationshipWith(player) != PlayerRelationship.Enemy)
					continue;
				if (!actor.Info.HasTraitInfo<BuildingInfo>())
					continue;

				var distSq = (actor.CenterPosition - sourcePos).LengthSquared;
				if (distSq < bestDistSq)
				{
					bestDistSq = distSq;
					closest = actor;
				}
			}

			return closest;
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			if (self.World.IsReplay)
				return;

			var initialBaseCenterNode = data.NodeWithKeyOrDefault("InitialBaseCenter");
			if (initialBaseCenterNode != null)
				initialBaseCenter = FieldLoader.GetValue<CPos>("InitialBaseCenter", initialBaseCenterNode.Value.Value);

			var defenseCenterNode = data.NodeWithKeyOrDefault("DefenseCenter");
			if (defenseCenterNode != null)
				DefenseCenter = FieldLoader.GetValue<CPos>("DefenseCenter", defenseCenterNode.Value.Value);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			RefineryBuildings.Dispose();
			powerBuildings.Dispose();
			ConstructionYardBuildings.Dispose();
			ProductionBuildings.Dispose();
		}

		void IBotSuggestRefineryProduction.RequestLocation(CPos refineryLocation, CPos conyardLocation, Actor expandActor)
		{
			if (CanSupportAnotherRefinery(refineryLocation))
				RequestedRefineries[expandActor] = (conyardLocation, refineryLocation);
		}
	}
}
