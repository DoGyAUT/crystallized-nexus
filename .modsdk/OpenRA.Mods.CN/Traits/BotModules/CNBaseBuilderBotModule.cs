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
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public enum BaseBuildingLayout { Random, Grid, BaseGrid, Clustered, Compact, Coverage }

	public enum CNBasePlanCluster { Core, Expansion, Production, Tech, DefensePerimeter, Outpost }

	/// <summary>
	/// What a whole base is for. Derived from what is already there (start position, nearby tiberium,
	/// distance to the front, chokepoint), never configured per map.
	/// </summary>
	public enum CNBaseRole
	{
		/// <summary>The starting base. Keeps the tech buildings; longest standing and best defended.</summary>
		Core,

		/// <summary>Sits on tiberium. Collects the refineries and silos.</summary>
		Economy,

		/// <summary>Closest to the front. Collects unit production above the per-base minimum.</summary>
		Military,

		/// <summary>Small base holding a chokepoint. Defense and support only, no full build-out.</summary>
		Outpost,

		/// <summary>No role. A base that is none of the above steers nothing - it takes its share of the
		/// ordinary build order like any other base and gets no category preference.</summary>
		Secondary,
	}

	/// <summary>
	/// The capability a building contributes to its base. Used for the redundancy floor: every base that
	/// is not an outpost keeps at least one of each of these, and only the surplus follows the base role.
	/// Declared in the order the floor is filled when a base is missing several at once.
	/// </summary>
	public enum CNBaseCapability { None, Power, InfantryProduction, VehicleProduction, AirProduction }

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

		/// <summary>
		/// High-value tower (Obelisk, EMP): outermost radius on enemy approach vector.
		/// <para>
		/// Unlike the roles above this is a statement of WORTH, not of a threat answered, and it is
		/// deliberately not a limit budget. Nothing attacks with "Special": ClassifyAttacker only ever
		/// yields AADefense, InfantryDefense or ArmorDefense, so neither the combat analysis nor a
		/// danger hotspot can ever name this role. As a budget it only ever subtracted - every
		/// high-value tower of both factions shared one narrow cap, so the more threats a tower
		/// answered the harder it was throttled. A tower is now capped by the threats it covers, and
		/// this tag decides preference instead: under active threat the bot reaches for it first.
		/// </para>
		/// </summary>
		SpecialDefense,

		/// <summary>Garrison bunker: universal role (garrisoned infantry mix adapts to local need), placed like artillery.</summary>
		GarrisonDefense,
	}

	/// <summary>
	/// One physical base of the bot: a cluster of construction yards that are close enough to each
	/// other to share a build site, plus every building that is closer to this cluster than to any other.
	/// Placement decisions (plan centers, raster anchor, build radius) are made per base, so a bot with
	/// several construction yards expands each of them separately instead of aiming at the point in between.
	/// </summary>
	public sealed class CNBotBase
	{
		/// <summary>Average location of this base's construction yards.</summary>
		public CPos Center;

		/// <summary>Center snapped to the global base raster. Anchor for the BaseGrid layout.</summary>
		public CPos GridAnchor;

		/// <summary>What this base is for. See <see cref="CNBaseRole"/>.</summary>
		public CNBaseRole Role;

		/// <summary>Lowest ActorID among this base's construction yards, or its buildings when it has none.
		/// Stable key for the role cache.</summary>
		public uint AnchorId;

		/// <summary>False for a group of buildings left behind by a construction yard that packed up.
		/// Such a group still holds ground and still counts, but nothing new is planned into it.</summary>
		public bool IsBuildSite => ConstructionYards.Count > 0;

		public readonly List<Actor> ConstructionYards = [];
		public readonly List<Actor> Buildings = [];

		public int CountOf(string actorType)
		{
			var count = 0;
			foreach (var b in Buildings)
				if (b.Info.Name == actorType)
					count++;

			return count;
		}

		public int CountOfAny(ISet<string> actorTypes)
		{
			var count = 0;
			foreach (var b in Buildings)
				if (actorTypes.Contains(b.Info.Name))
					count++;

			return count;
		}

		public CPos? AverageLocationOf(ISet<string> actorTypes)
		{
			var count = 0;
			long x = 0;
			long y = 0;
			foreach (var b in Buildings)
			{
				if (!actorTypes.Contains(b.Info.Name))
					continue;

				x += b.Location.X;
				y += b.Location.Y;
				count++;
			}

			if (count == 0)
				return null;

			return new CPos((int)(x / count), (int)(y / count));
		}
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

		[Desc("Maximum percentage of total base buildings allowed per defense role.",
			"Use key 'Total' to cap all defenses combined.",
			"Example: Total: 25, InfantryDefense: 10 — max 25% of base can be defenses, of which max 10% are anti-infantry.")]
		public readonly FrozenDictionary<string, int> DefenseRoleLimits = null;

		[Desc("Percent added to a defense role's limit while the combat analysis reports an active threat in that role.",
			"Scales with how hard the role is pressed and falls away on its own as the threat weights decay.")]
		public readonly int ThreatDefenseRoleBoostPct = 50;

		[Desc("Percent added to the Total defense limit while any role is under threat, driven by the strongest one.",
			"Damped relative to the role boost: without it Total stays the binding cap and the amount of defense",
			"could not grow at all, but matching the role boost would turn the whole budget into towers.")]
		public readonly int ThreatDefenseTotalBoostPct = 25;

		[Desc("Threat weight at which the boosts above reach their full value, as a multiple of the combat analysis",
			"ReactThreshold. Lower means the bot reacts to a light raid almost as strongly as to a full assault.")]
		public readonly float ThreatDefenseSaturationFactor = 3f;

		[Desc("Percent added to every defense limit for each usable way into the bot's bases beyond the first,",
			"applied with no attack under way. A base with several approaches needs more defense than one with a",
			"single choke. 0 disables proactive scaling.")]
		public readonly int ChokepointDefenseBoostPct = 10;

		[Desc("Upper bound on the percent the chokepoint boost may add.")]
		public readonly int ChokepointDefenseBoostMaxPct = 40;

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

		[Desc("Cell pitch of the shared base raster used by the " + nameof(BaseBuildingLayout.BaseGrid) + " layout.",
			"One pitch for the whole base: every building origin snaps to this raster (or to an exact subdivision",
			"of it for types that declare a tighter MinSpacing). Footprints that do not fit inside one raster cell",
			"together with their padding simply consume the neighbouring cells - " + nameof(GlobalMinSpacing) + " enforces the gap.")]
		public readonly int BaseGridCellSize = 4;

		[Desc("Construction yards within this distance of each other are treated as one base.",
			"Each base gets its own plan centers, raster anchor and build radius.")]
		public readonly int BaseClusterRadius = 14;

		[Desc("Give every base a role (see " + nameof(CNBaseRole) + ") and send the surplus of a category to the",
			"base that role belongs to. The per-base minimum below is filled first, so no base loses a basic capability.")]
		public readonly bool EnableBaseRoles = true;

		[Desc("Ticks between base role reevaluations.")]
		public readonly int BaseRoleUpdateInterval = 250;

		[Desc("A base with at most this many buildings that also sits on a chokepoint becomes an Outpost:",
			"defense and support only, and it is exempt from the per-base minimum.")]
		public readonly int OutpostMaxStructures = 8;

		[Desc("Distance from a base center to a known chokepoint at which the base counts as holding it.")]
		public readonly int OutpostChokepointRadius = 12;

		[Desc("How close a base has to be to a remembered attack to be given the Military role.",
			"Beyond this the bot has no front near that base and the base keeps its economic role.")]
		public readonly int MilitaryRoleThreatRadius = 32;

		[Desc("Minimum ticks a base keeps a role before it may be given a different one. A role change",
			"switches what gets built there but takes nothing back, so a role that follows the decaying",
			"danger memory tick by tick leaves half-finished structure in both directions.")]
		public readonly int BaseRoleMinimumHoldTicks = 3000;

		[Desc("Percent of " + nameof(MilitaryRoleThreatRadius) + " a base that already holds the Military role",
			"may be away from the threat before it loses it. Above 100 this is the hysteresis band that keeps",
			"a front base from flipping back the moment the remembered attack decays a little.")]
		public readonly int MilitaryRoleReleasePct = 150;

		[Desc("How far the defense reference may drift before the cached access bearings, high-ground edges",
			"and chokepoint anchors are rebuilt. Same idea as CNTacticalMapBotModule's BaseMoveThreshold.")]
		public readonly int FlankCacheMoveThreshold = 6;

		[Desc("Refineries a base needs before it counts as having economic substance and may take the",
			"Economy role. A base that already works tiberium qualifies whatever the map says.")]
		public readonly int EconomyRoleMinimumRefineries = 1;

		[Desc("Valuable resource cells the nearest resource map indice needs, when the base has no refinery",
			"yet, before the base counts as having economic substance. Matched to " + nameof(MinFiniteFieldCellsForRefinery) + ".")]
		public readonly int EconomyRoleMinimumResourceCells = 12;

		[Desc("Percent bonus on the base closest to the front when the global defense budget is distributed",
			"across bases. 100 = that base may claim twice its size-proportional share. The global budget",
			"itself (DefenseRoleLimits) is unaffected - this only decides which base the next defense goes to.")]
		public readonly int FrontBaseDefenseShareBonusPct = 100;

		[Desc("How many buildings per base may be built past the global BuildingFractions cap in order to fill",
			"that base's capability floor. This is the only way the fraction cap can be exceeded; BuildingLimits",
			"still apply unconditionally. 0 disables the exception (the floor then silently fails when a",
			"fraction is globally saturated).")]
		public readonly int MaxCapabilityFloorExceptionsPerBase = 3;

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

		[Desc("Refineries the bot must own before unit production is allowed to resume. A hard floor on",
			"the economy, deliberately independent of the budget-weighted refinery target: that target",
			"is an expansion goal, and holding every unit — including the MCVs an expansion needs —",
			"hostage to it stalls the bot outright whenever the next refinery is slow to arrive.")]
		public readonly int ProductionPauseRefineryCount = 1;

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

		[Desc("Maximum number of defense placement cells that receive expensive scoring and placement checks.",
			"Multiplied on each retry, since a retry that reconsiders the same cells cannot succeed where",
			"the previous one failed.")]
		public readonly int DefensePlacementCandidateLimit = 48;

		[Desc("Tech buildings that are spread across bases instead of being concentrated at the spawn base.",
			"For structures whose value is the ground they cover — radar and other detection or vision",
			"buildings — one at home leaves every other base blind. Everything else in TechTypes is",
			"expensive, fragile and built once, and belongs in the safest base the bot has.")]
		public readonly FrozenSet<string> DistributedTechTypes = FrozenSet<string>.Empty;

		[Desc("Ticks to wait before ordering another refinery after one could not be placed. Separate from",
			"StructureProductionResumeDelay, which is the recovery delay for a base that cannot place",
			"anything at all: a refinery that missed its spot is a routine miss on awkward terrain and",
			"should be retried well before that.")]
		public readonly int RefineryPlacementRetryDelay = 600;

		[Desc("Ticks to wait before ordering another defense after one could not be placed anywhere.",
			"Short on purpose: the base grows and the threat hotspot moves, so a blocked spot often frees",
			"up again quickly. This delays only defenses, never the rest of the build queue.")]
		public readonly int DefensePlacementRetryDelay = 600;

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
			"(e.g. facing a map edge / cliff / water with no enemy approach). 0 disables the sealed-flank test entirely.")]
		public readonly int SealedFlankPenaltyWeight = 80;

		[Desc("Treat a sealed flank as a veto rather than only as a score penalty. The veto can never starve",
			"placement: when every candidate faces a sealed flank the bot falls back to the scored ordering.")]
		public readonly bool VetoSealedFlankDefenses = true;

		[Desc("How far beyond a defense candidate the bot probes for reachable ground when the map has no known",
			"access corridors at all. Nothing passable within this distance means the cell faces nowhere.")]
		public readonly int SealedFlankProbeCells = 6;

		[Desc("Bonus for defense candidates placed on the base side of a sealable chokepoint wall/gate line.")]
		public readonly int ChokepointDefenseAnchorWeight = 160;

		[Desc("How strongly the nearest known enemy base/building influences defense placement when no stronger danger hotspot exists.")]
		public readonly int DefensePlacementEnemyDirectionWeight = 70;

		[Desc("Score per still-uncovered protectable building an anti-air candidate would bring into range.",
			"Comparable in size to DefensePlacementEnemyDirectionWeight and the topology weights on purpose:",
			"coverage should be able to break a tie between two cells facing the threat, but never place AA",
			"away from where air attacks actually come from. 0 ignores coverage entirely.")]
		public readonly int AACoverageWeight = 40;

		[Desc("BotCapabilities tags that make a building worth covering with anti-air. Counting every building",
			"equally let walls and power plants outvote the refinery. Falls back to counting all buildings",
			"while the bot owns nothing tagged with any of these.")]
		public readonly FrozenSet<string> AAProtectedCapabilities =
			new HashSet<string> { "Production", "Tech", "Superweapon", "Economy" }.ToFrozenSet();

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

		[Desc("Weight of the measured harvester drive length (squared, in cells) in refinery placement. " +
			"Catches fields that are adjacent on the map but only reachable the long way round - a refinery " +
			"below a cliff whose tiberium sits on the terrace above looks perfectly placed until the " +
			"harvesters have to drive out around the long way and give up on it. 0 falls back to " +
			"straight-line distance and skips the pathfinder query (one per field per placement decision).")]
		public readonly int RefineryDetourPenalty = 6;

		[Desc("Detour percent assumed per level of height difference when the bot owns nothing that can " +
			"be asked for a path - the moment right after the MCV deploys, when the first and most " +
			"consequential refinery is sited. A field on another terrace is only reachable by a ramp, " +
			"and a ramp is a detour, so terrain stands in for the measurement that cannot be made yet.")]
		public readonly int RefineryUnmeasuredHeightDetourPercent = 150;

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

		/// <summary>Raw position of the most recent attacker. Jumps with every hit - see <see cref="GetDefenseReference"/>.</summary>
		public CPos DefenseCenter { get; private set; }

		/// <summary>
		/// Where the bot currently believes it is threatened. Prefers the weighted danger hotspot, which
		/// merges attacks within DefenseDangerMemoryMergeRadius and is scored by how often and how hard the
		/// bot was hit there, over the raw position of whoever shot last. The raw position moves with every
		/// single attacker and made defense planning chase individual units around the base.
		/// </summary>
		public CPos GetDefenseReference(CPos fallback)
		{
			return GetRecordedDangerHotspot(fallback)
				?? (DefenseCenter == default ? fallback : DefenseCenter);
		}

		// Staleness window for the cached active BuildingFractions / DefenseRoleLimits tables.
		const int ActiveTableMaxAgeTicks = 25;

		// DefenseRoleLimits key capping all defenses together rather than a single role.
		public const string TotalDefenseLimitKey = "Total";

		// Staleness window for the cached supported-refinery capacity sweep.
		const int SupportedRefineryCapacityMaxAgeTicks = 50;

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
		CPos? baseGridOrigin;
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
		readonly List<WVec> cachedAccessBearings = [];
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

		// Both active-value tables are rebuilt from the same small set of inputs (profile, tech stage,
		// threat flag) and then frozen. ToFrozenDictionary builds a read-optimised hash structure and is
		// meant for build-once/read-many — but these were freshly merged and frozen on every call, and
		// ChooseBuildingToBuild alone calls GetActiveDefenseRoleLimits three times per pass. Cached until
		// one of the inputs actually changes; the panic path additionally depends on which production
		// buildings exist, so it carries a short staleness window on top.
		FrozenDictionary<string, int> cachedBuildingFractions;
		FrozenDictionary<string, int> cachedDefenseRoleLimits;
		(BotProfile Profile, TechStage Stage, bool UnderThreat) cachedFractionKey = (BotProfile.Adaptive, TechStage.Early, false);
		(BotProfile Profile, TechStage Stage, bool UnderThreat) cachedDefenseKey = (BotProfile.Adaptive, TechStage.Early, false);
		int cachedFractionTick = int.MinValue;
		int cachedDefenseTick = int.MinValue;

		public bool IsUnderActiveThreat() =>
			combatAnalysis != null && !combatAnalysis.IsTraitDisabled && combatAnalysis.HasActiveThreat();

		// --- Profile + tech-stage aware getters ---
		// BuildingFractions: merge TechStage overlay first, then apply strategic budget scaling.
		public FrozenDictionary<string, int> GetActiveBuildingFractions()
		{
			var key = (ActiveProfile, ActiveTechStage, IsUnderActiveThreat());
			if (cachedBuildingFractions != null && cachedFractionKey == key
				&& world.WorldTick - cachedFractionTick < ActiveTableMaxAgeTicks)
				return cachedBuildingFractions;

			cachedFractionKey = key;
			cachedFractionTick = world.WorldTick;
			cachedBuildingFractions = BuildActiveBuildingFractions();
			return cachedBuildingFractions;
		}

		FrozenDictionary<string, int> BuildActiveBuildingFractions()
		{
			var stageOverride = ActiveTechStage switch
			{
				TechStage.Early => Info.EarlyBuildingFractions,
				TechStage.Mid => Info.MidBuildingFractions,
				TechStage.Late => Info.LateBuildingFractions,
				_ => null
			};

			var underThreat = IsUnderActiveThreat();

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

			// The threat flag is part of the key so an attack starting or ending takes effect at once
			// rather than up to ActiveTableMaxAgeTicks later. Its magnitude is not: the threat weights
			// move continuously and would defeat the cache entirely, so the staleness window carries
			// that, which bounds it to well under a second.
			var key = (ActiveProfile, ActiveTechStage, IsUnderActiveThreat());
			if (cachedDefenseRoleLimits != null && cachedDefenseKey == key
				&& world.WorldTick - cachedDefenseTick < ActiveTableMaxAgeTicks)
				return cachedDefenseRoleLimits;

			cachedDefenseKey = key;
			cachedDefenseTick = world.WorldTick;
			cachedDefenseRoleLimits = BuildActiveDefenseRoleLimits();
			return cachedDefenseRoleLimits;
		}

		FrozenDictionary<string, int> BuildActiveDefenseRoleLimits()
		{
			var merged = Info.DefenseRoleLimits != null
				? new Dictionary<string, int>(Info.DefenseRoleLimits)
				: [];

			// The profile stays multiplicative: it is a posture, a statement about how this bot plays
			// at all, so it belongs in the baseline everything else is measured against.
			ApplyProfileDefenseBudget(merged);

			// The other two are surcharges ON that baseline and are added, never multiplied onto each
			// other. They answer different questions: the chokepoint share is static and describes the
			// shape of the map, the threat share is dynamic and describes what is happening right now.
			// Multiplying them would let map geometry amplify the reaction to an attack - the same
			// assault drawing a bigger answer on a choke-heavy map than on an open one, which means
			// nothing. Additive keeps them legible: "this much because of the map" plus "this much
			// more because of this attack".
			var baseline = new Dictionary<string, int>(merged);
			ApplyThreatDefenseBudget(merged, baseline);
			ApplyChokepointDefenseBudget(merged, baseline);

			return merged.ToFrozenDictionary();
		}

		/// <summary>
		/// Reactive scaling: how much defense the bot is allowed to build depends on what is actually
		/// happening to it. The limits are otherwise threat-blind - the same 25% Total applied whether
		/// nothing had happened all game or three enemies were attacking at once, and only the profile
		/// scaled them. A role being pressed gets more room, and Total grows with it so the amount can
		/// rise rather than merely shift between roles.
		/// <para>
		/// Nothing here has to be undone: the boost is derived from the combat analysis weights every
		/// time the table is rebuilt, so it fades out by itself as those weights decay.
		/// </para>
		/// </summary>
		void ApplyThreatDefenseBudget(Dictionary<string, int> values, IReadOnlyDictionary<string, int> baseline)
		{
			if (combatAnalysis == null || combatAnalysis.IsTraitDisabled)
				return;

			if (Info.ThreatDefenseRoleBoostPct <= 0 && Info.ThreatDefenseTotalBoostPct <= 0)
				return;

			var strongest = 0f;
			foreach (var key in baseline.Keys)
			{
				if (key == TotalDefenseLimitKey)
					continue;

				if (!Enum.TryParse<DefenseRole>(key, true, out var role) || role == DefenseRole.Default)
					continue;

				var intensity = combatAnalysis.GetThreatIntensity(role, Info.ThreatDefenseSaturationFactor);
				if (intensity <= 0f)
					continue;

				strongest = Math.Max(strongest, intensity);
				values[key] += Surcharge(baseline[key], Info.ThreatDefenseRoleBoostPct, intensity);
			}

			if (strongest > 0f && baseline.TryGetValue(TotalDefenseLimitKey, out var total))
				values[TotalDefenseLimitKey] += Surcharge(total, Info.ThreatDefenseTotalBoostPct, strongest);
		}

		/// <summary>
		/// Proactive scaling: a base the enemy can walk into from four directions needs more defense
		/// than one behind a single choke, and that is knowable before the first attack. Uses the same
		/// chokepoint set the placement logic already works from, so this adds no new scan.
		/// </summary>
		void ApplyChokepointDefenseBudget(Dictionary<string, int> values, IReadOnlyDictionary<string, int> baseline)
		{
			if (Info.ChokepointDefenseBoostPct <= 0 || TacticalMapModule == null || !TacticalMapModule.TopologyReady)
				return;

			// One way in is the baseline the neutral limits already assume, so only the extra
			// approaches count.
			var extraApproaches = TacticalMapModule.GetUsefulChokepointsForOwnBase().Count - 1;
			if (extraApproaches <= 0)
				return;

			var boostPct = Math.Min(Info.ChokepointDefenseBoostMaxPct, extraApproaches * Info.ChokepointDefenseBoostPct);
			if (boostPct <= 0)
				return;

			foreach (var key in baseline.Keys)
				values[key] += Surcharge(baseline[key], boostPct, 1f);
		}

		/// <summary>How much to add on top of a baseline limit, as a percentage of that baseline.</summary>
		static int Surcharge(int baseValue, int percent, float scale)
		{
			if (percent <= 0 || scale <= 0f)
				return 0;

			return (int)Math.Round(baseValue * (percent / 100f) * scale, MidpointRounding.AwayFromZero);
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
				// SpecialDefense used to be scaled here too. It is no longer a limit budget, so the
				// key never appears in this table; high-value towers are now gated by the threat roles
				// they cover and preferred through selection instead.
				var scale = defenseScale;
				if (key == "ArtilleryDefense")
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

		public CPos GetBasePlanCenterForActor(ActorInfo actorInfo, CNBotBase targetBase, CPos fallbackCenter, bool isDefense, bool isRefinery)
		{
			return GetBasePlanCenter(GetBasePlanClusterForActor(actorInfo, isDefense, isRefinery), targetBase, fallbackCenter);
		}

		// Plan centers are computed from the buildings of ONE base only. Averaging over all bases put the
		// target between them, so from the second construction yard on the bot dropped single buildings
		// into the no man's land in between.
		public CPos GetBasePlanCenter(CNBasePlanCluster cluster, CNBotBase targetBase, CPos fallbackCenter)
		{
			return cluster switch
			{
				CNBasePlanCluster.Expansion => targetBase.AverageLocationOf(Info.RefineryTypes) ?? ResourceConyardCenter ?? fallbackCenter,
				CNBasePlanCluster.Production => targetBase.AverageLocationOf(Info.ProductionTypes) ?? fallbackCenter,
				CNBasePlanCluster.Tech => targetBase.AverageLocationOf(Info.TechTypes) ?? targetBase.AverageLocationOf(Info.ProductionTypes) ?? fallbackCenter,
				CNBasePlanCluster.DefensePerimeter => GetDefenseReference(fallbackCenter),
				CNBasePlanCluster.Outpost => ResourceConyardCenter ?? fallbackCenter,
				_ => targetBase.AverageLocationOf(Info.ConstructionYardTypes) ?? fallbackCenter
			};
		}

		// --- Base clustering ---
		// Assigned as a whole, never filled in place: the debug overlay reads this from the render thread
		// and must never observe a half-built list.
		List<CNBotBase> cachedBotBases = [];
		int cachedBotBasesTick = -1;

		/// <summary>
		/// The bot's bases, rebuilt at most once per tick. Construction yards within BaseClusterRadius of
		/// each other form one base (single linkage); a building joins the closest base that is near enough
		/// to have built it. Buildings left over - typically a base whose construction yard packed up and
		/// drove off - form their own yard-less groups instead of being handed to a base across the map.
		/// </summary>
		public IReadOnlyList<CNBotBase> GetBases()
		{
			if (world.WorldTick == cachedBotBasesTick && cachedBotBases.Count > 0)
				return cachedBotBases;

			cachedBotBasesTick = world.WorldTick;
			var bases = new List<CNBotBase>();

			var conyards = new List<Actor>();
			foreach (var a in ConstructionYardBuildings.Actors)
				if (!a.IsDead && a.IsInWorld)
					conyards.Add(a);

			if (conyards.Count == 0)
			{
				// No construction yard left at all: keep one synthetic base so placement still has an anchor.
				var orphan = new CNBotBase { Center = BaseOrigin };
				orphan.Buildings.AddRange(GetCachedPlayerBuildings());
				if (orphan.Buildings.Count > 0)
					orphan.Center = AverageLocation(orphan.Buildings);

				orphan.GridAnchor = SnapToBaseGrid(orphan.Center);
				bases.Add(orphan);
				ApplyBaseRoles(bases);
				cachedBotBases = bases;
				return cachedBotBases;
			}

			foreach (var group in ClusterByDistance(conyards, Info.BaseClusterRadius))
			{
				var b = new CNBotBase();
				b.ConstructionYards.AddRange(group);
				b.Center = AverageLocation(group);
				b.GridAnchor = SnapToBaseGrid(b.Center);
				b.AnchorId = LowestActorId(group);
				bases.Add(b);
			}

			// A building belongs to the closest base that could plausibly have built it, i.e. one whose build
			// radius reaches it. Without that bound a construction yard packing up (UnDeployConyard runs on
			// MoveConyardTick) dropped its entire base onto whichever base was left, however far away: plan
			// centers, NearBuilding clusters, the raster anchor and the defense share were then all computed
			// over a base scattered across half the map, and jumped again when the yard redeployed.
			var membershipRadius = GetBaseMembershipRadius();
			var membershipRadiusSq = (long)membershipRadius * membershipRadius;
			List<Actor> unclaimed = null;

			foreach (var building in GetCachedPlayerBuildings())
			{
				CNBotBase best = null;
				var bestDistance = long.MaxValue;
				foreach (var b in bases)
				{
					var distance = (building.Location - b.Center).LengthSquared;
					if (distance >= bestDistance)
						continue;

					bestDistance = distance;
					best = b;
				}

				if (best != null && bestDistance <= membershipRadiusSq)
					best.Buildings.Add(building);
				else
					(unclaimed ??= []).Add(building);
			}

			// What is left over keeps its own identity: a yard-less base that still holds ground and is worth
			// counting, but is not a build site - see GetOrderedBasesForBuilding.
			if (unclaimed != null)
			{
				foreach (var group in ClusterByDistance(unclaimed, Info.BaseClusterRadius))
				{
					var b = new CNBotBase();
					b.Buildings.AddRange(group);
					b.Center = AverageLocation(group);
					b.GridAnchor = SnapToBaseGrid(b.Center);
					b.AnchorId = LowestActorId(group);
					bases.Add(b);
				}
			}

			ApplyBaseRoles(bases);
			cachedBotBases = bases;
			return cachedBotBases;
		}

		/// <summary>
		/// How far from its center a base still counts a building as its own. Uses the largest radius the
		/// base could ever reach rather than its current one, so the membership test does not depend on the
		/// building count it is about to produce.
		/// </summary>
		int GetBaseMembershipRadius()
		{
			return Info.DynamicBaseRadius ? GetActiveMaxDynamicBaseRadius() : Info.MaxBaseRadius;
		}

		static uint LowestActorId(List<Actor> actors)
		{
			var lowest = uint.MaxValue;
			foreach (var a in actors)
				if (a.ActorID < lowest)
					lowest = a.ActorID;

			return lowest;
		}

		// Single-linkage clustering: two actors within radius of each other belong to the same group.
		// Used for construction yards and, with the same rule, for buildings no base reaches any more.
		static List<List<Actor>> ClusterByDistance(List<Actor> actors, int radius)
		{
			var parent = new int[actors.Count];
			for (var i = 0; i < parent.Length; i++)
				parent[i] = i;

			int Find(int x)
			{
				while (parent[x] != x)
				{
					parent[x] = parent[parent[x]];
					x = parent[x];
				}

				return x;
			}

			var clusterRadius = Math.Max(1, radius);
			var clusterRadiusSq = clusterRadius * clusterRadius;
			for (var i = 0; i < actors.Count; i++)
			{
				for (var j = i + 1; j < actors.Count; j++)
				{
					if ((actors[i].Location - actors[j].Location).LengthSquared > clusterRadiusSq)
						continue;

					var ri = Find(i);
					var rj = Find(j);
					if (ri != rj)
						parent[ri] = rj;
				}
			}

			var byRoot = new Dictionary<int, List<Actor>>();
			for (var i = 0; i < actors.Count; i++)
			{
				var root = Find(i);
				if (!byRoot.TryGetValue(root, out var group))
					byRoot[root] = group = [];

				group.Add(actors[i]);
			}

			return byRoot.Values.ToList();
		}

		/// <summary>
		/// Pure read for the debug overlay (render thread): hands back whatever the sim thread last
		/// published, and never rebuilds or recomputes anything.
		/// </summary>
		public IReadOnlyList<CNBotBase> BasesForOverlay() => cachedBotBases;

		/// <summary>Build radius the overlay draws for a base. Same value the placement search uses.</summary>
		public int GetBaseRadiusForOverlay(CNBotBase targetBase) => GetEffectiveMaxBaseRadius(targetBase.Buildings.Count);

		// --- Base roles ---
		readonly Dictionary<uint, (CNBaseRole Role, int SinceTick)> baseRoleByAnchor = [];
		int nextBaseRoleTick;

		// The role decision needs the chokepoint list, so it runs on its own slow timer; in between, the
		// per-tick rebuild just reads back what was decided for the same construction yard.
		void ApplyBaseRoles(List<CNBotBase> bases)
		{
			if (!Info.EnableBaseRoles)
				return;

			if (world.WorldTick >= nextBaseRoleTick)
			{
				nextBaseRoleTick = world.WorldTick + Math.Max(1, Info.BaseRoleUpdateInterval);
				EvaluateBaseRoles(bases);
				return;
			}

			foreach (var b in bases)
				b.Role = baseRoleByAnchor.TryGetValue(b.AnchorId, out var held) ? held.Role : CNBaseRole.Secondary;

			// A single base is always the core, whatever a stale entry says.
			if (bases.Count == 1)
				bases[0].Role = CNBaseRole.Core;
		}

		void EvaluateBaseRoles(List<CNBotBase> bases)
		{
			// A role change switches what gets built in a base but takes nothing back, so a role that
			// follows the decaying danger memory tick by tick leaves half-finished structure in both
			// directions. A base that has held a role for less than BaseRoleMinimumHoldTicks keeps it, and
			// the roles that are exclusive are not handed out again while somebody still holds them.
			var holdTicks = Math.Max(0, Info.BaseRoleMinimumHoldTicks);
			var locked = new bool[bases.Count];
			var coreHeld = false;
			var militaryHeld = false;

			for (var i = 0; i < bases.Count; i++)
			{
				if (!baseRoleByAnchor.TryGetValue(bases[i].AnchorId, out var held))
					continue;

				// A group that lost its construction yard cannot keep a steering role - every one of them
				// describes what gets built somewhere, and nothing gets built there any more.
				if (!bases[i].IsBuildSite && held.Role != CNBaseRole.Secondary)
					continue;

				if (world.WorldTick - held.SinceTick >= holdTicks)
					continue;

				locked[i] = true;
				bases[i].Role = held.Role;
				coreHeld |= held.Role == CNBaseRole.Core;
				militaryHeld |= held.Role == CNBaseRole.Military;
			}

			// Everything unlocked starts with no role. Economy is earned further down by actually having
			// tiberium to work; it used to be the blanket default, which made a base at the far end of the
			// map with not a grain of resource just as much an "Economy" base as one sitting in a field.
			for (var i = 0; i < bases.Count; i++)
				if (!locked[i])
					bases[i].Role = CNBaseRole.Secondary;

			// Core and Military describe what a base is FOR, so they only go to bases that can still build.
			var freeBuildSites = new List<CNBotBase>();
			for (var i = 0; i < bases.Count; i++)
				if (!locked[i] && bases[i].IsBuildSite)
					freeBuildSites.Add(bases[i]);

			if (!coreHeld && freeBuildSites.Count > 0)
				GetBaseNearestIn(freeBuildSites, BaseOrigin).Role = CNBaseRole.Core;
			else if (!coreHeld && bases.Count > 0)
				GetBaseNearestIn(bases, BaseOrigin).Role = CNBaseRole.Core;

			if (bases.Count > 1)
			{
				var chokepoints = TacticalMapModule != null && TacticalMapModule.TopologyReady
					? TacticalMapModule.GetUsefulChokepointsForOwnBase()
					: null;

				if (chokepoints != null && chokepoints.Count > 0)
				{
					var outpostRadius = Math.Max(1, Info.OutpostChokepointRadius);
					var outpostRadiusSq = outpostRadius * outpostRadius;
					for (var i = 0; i < bases.Count; i++)
					{
						var b = bases[i];
						if (locked[i] || !b.IsBuildSite || b.Role == CNBaseRole.Core
							|| b.Buildings.Count > Info.OutpostMaxStructures)
							continue;

						foreach (var chokepoint in chokepoints)
						{
							if ((chokepoint.Cell - b.Center).LengthSquared > outpostRadiusSq)
								continue;

							b.Role = CNBaseRole.Outpost;
							break;
						}
					}
				}

				// The front has to be an attack that actually happened. GetBestDefenseHotspot falls back to
				// the nearest map chokepoint when nothing has been attacked yet - fine for aiming the first
				// turrets, but as a role trigger it meant every map handed out a front from tick 0, so the
				// second base was always claimed as Military and Economy could not occur below three bases.
				var front = GetRecordedDangerHotspot(BaseOrigin) ?? (DefenseCenter == default ? null : DefenseCenter);
				if (front != null && !militaryHeld)
				{
					// And the base has to be at that front, not merely the closest one the bot happens to
					// own: an attack on the main base used to relabel an expansion on the far side of the map.
					var threatRadius = Math.Max(1, Info.MilitaryRoleThreatRadius);
					var threatRadiusSq = (long)threatRadius * threatRadius;

					CNBotBase military = null;
					var bestDistance = long.MaxValue;
					foreach (var b in freeBuildSites)
					{
						if (b.Role != CNBaseRole.Secondary)
							continue;

						var distance = (b.Center - front.Value).LengthSquared;
						if (distance > threatRadiusSq || distance >= bestDistance)
							continue;

						bestDistance = distance;
						military = b;
					}

					if (military != null)
						military.Role = CNBaseRole.Military;
				}
				else if (front != null && militaryHeld)
				{
					// Releasing the Military role needs clearly weaker evidence than gaining it, otherwise the
					// base flips back as soon as the remembered attack decays a little past the radius.
					var releaseRadius = Math.Max(1, Info.MilitaryRoleThreatRadius) * Math.Max(100, Info.MilitaryRoleReleasePct) / 100;
					var releaseRadiusSq = (long)releaseRadius * releaseRadius;
					foreach (var b in bases)
						if (b.Role == CNBaseRole.Military && (b.Center - front.Value).LengthSquared > releaseRadiusSq)
							b.Role = CNBaseRole.Secondary;
				}
				else if (militaryHeld)
				{
					// Nothing remembered anywhere any more: the front is gone, not merely further away.
					foreach (var b in bases)
						if (b.Role == CNBaseRole.Military)
							b.Role = CNBaseRole.Secondary;
				}
			}

			// Economy last and on evidence: whatever carries no role by now and actually has tiberium to work.
			// Build sites only, like every other steering role - a group without a construction yard is
			// skipped by GetOrderedBasesForBuilding, so a role pointing at it steers nothing at all.
			for (var i = 0; i < bases.Count; i++)
				if (!locked[i] && bases[i].IsBuildSite && bases[i].Role == CNBaseRole.Secondary
					&& HasEconomicSubstance(bases[i]))
					bases[i].Role = CNBaseRole.Economy;

			// Stamp the tick only where the role actually changed, so the hold measures how long a base has
			// really been what it is. Entries of bases that no longer exist are dropped.
			var refreshed = new Dictionary<uint, (CNBaseRole Role, int SinceTick)>(bases.Count);
			foreach (var b in bases)
			{
				var sinceTick = baseRoleByAnchor.TryGetValue(b.AnchorId, out var previous) && previous.Role == b.Role
					? previous.SinceTick
					: world.WorldTick;

				refreshed[b.AnchorId] = (b.Role, sinceTick);
			}

			baseRoleByAnchor.Clear();
			foreach (var kv in refreshed)
				baseRoleByAnchor[kv.Key] = kv.Value;
		}

		/// <summary>
		/// Whether this base has anything to run an economy on: refineries already standing in it, or a
		/// resource map indice with enough valuable cells within its reach. Same data the MCV expansion
		/// scores its sites from, so "Economy" now means the same thing in both places.
		/// </summary>
		bool HasEconomicSubstance(CNBotBase targetBase)
		{
			if (targetBase.CountOfAny(Info.RefineryTypes) >= Math.Max(1, Info.EconomyRoleMinimumRefineries))
				return true;

			if (ResourceMapModule == null)
				return false;

			var indice = ResourceMapModule.FindClosestIndiceFromCPos(targetBase.Center);
			if (indice == null || indice.ResourceCellsCount < Math.Max(1, Info.EconomyRoleMinimumResourceCells))
				return false;

			// FindClosestIndiceFromCPos always answers, however far away that indice is - so the reach
			// still has to be checked, or every base on the map would inherit the nearest field.
			var reach = GetBaseMembershipRadius();
			return (indice.ResourceCellsCenter - targetBase.Center).LengthSquared <= (long)reach * reach;
		}

		// How much of the global defense budget a base may claim: what it has to protect, doubled (by
		// default) for the base at the front. Never zero, so a brand new expansion is not weighted out.
		static long DefenseShareWeight(CNBotBase targetBase, CNBotBase frontBase, int frontBonus)
		{
			var weight = Math.Max(1, targetBase.Buildings.Count);
			return targetBase == frontBase ? weight * frontBonus / 100 : weight;
		}

		static CNBotBase GetBaseNearestIn(IReadOnlyList<CNBotBase> bases, CPos cell)
		{
			var best = bases[0];
			var bestDistance = (cell - best.Center).LengthSquared;
			for (var i = 1; i < bases.Count; i++)
			{
				var distance = (cell - bases[i].Center).LengthSquared;
				if (distance >= bestDistance)
					continue;

				bestDistance = distance;
				best = bases[i];
			}

			return best;
		}

		// --- Per-base capability floor ---
		readonly Dictionary<string, CNBaseCapability> capabilityByActorType = [];

		public CNBaseCapability GetBaseCapability(string actorType)
		{
			if (capabilityByActorType.TryGetValue(actorType, out var cached))
				return cached;

			var capability = CNBaseCapability.None;
			if (Info.PowerTypes.Contains(actorType))
				capability = CNBaseCapability.Power;
			else
			{
				var actorInfo = world.Map.Rules.Actors.GetValueOrDefault(actorType);
				var produces = actorInfo?.TraitInfos<ProductionInfo>();
				if (produces != null)
				{
					foreach (var production in produces)
					{
						if (production.Produces.Contains("Infantry"))
							capability = CNBaseCapability.InfantryProduction;
						else if (production.Produces.Contains("Vehicle"))
							capability = CNBaseCapability.VehicleProduction;
						else if (production.Produces.Contains("Air"))
							capability = CNBaseCapability.AirProduction;

						if (capability != CNBaseCapability.None)
							break;
					}
				}
			}

			capabilityByActorType[actorType] = capability;
			return capability;
		}

		// True while this base is still missing its one guaranteed building of that capability. Outposts are
		// exempt - they are meant to stay small. Air production is in the floor like everything else: it may
		// spread across bases, so losing one base no longer costs the bot its entire air force.
		bool LacksCapabilityFloor(CNBotBase targetBase, CNBaseCapability capability)
		{
			if (capability == CNBaseCapability.None)
				return false;

			if (targetBase.Role == CNBaseRole.Outpost || !targetBase.IsBuildSite)
				return false;

			foreach (var building in targetBase.Buildings)
				if (GetBaseCapability(building.Info.Name) == capability)
					return false;

			return true;
		}

		// Capabilities in the order a base fills them when it is missing several at once. Only the first
		// MaxCapabilityFloorExceptionsPerBase of them may bypass the global fraction cap at any one time;
		// as soon as one is satisfied the next moves up, so nothing is permanently excluded.
		static readonly CNBaseCapability[] FloorCapabilities =
		[
			CNBaseCapability.Power,
			CNBaseCapability.InfantryProduction,
			CNBaseCapability.VehicleProduction,
			CNBaseCapability.AirProduction,
		];

		/// <summary>
		/// True when this building type is needed to fill some base's capability floor and may therefore be
		/// built even though its BuildingFractions share is globally used up. Without this the floor fails
		/// silently: the main base holds the whole quota and an expansion never gets its first barracks.
		/// </summary>
		public bool AllowsCapabilityFloorException(string actorType)
		{
			if (!Info.EnableBaseRoles || Info.MaxCapabilityFloorExceptionsPerBase <= 0)
				return false;

			var capability = GetBaseCapability(actorType);
			if (capability == CNBaseCapability.None)
				return false;

			foreach (var targetBase in GetBases())
			{
				if (!LacksCapabilityFloor(targetBase, capability))
					continue;

				// Hard bound: a base may only ever have this many buildings in flight past the cap.
				var rank = 0;
				foreach (var candidate in FloorCapabilities)
				{
					if (candidate == capability)
						break;

					if (LacksCapabilityFloor(targetBase, candidate))
						rank++;
				}

				if (rank < Info.MaxCapabilityFloorExceptionsPerBase)
					return true;
			}

			return false;
		}

		// Which base a category belongs in once the floor is covered.
		CNBaseRole? GetPreferredRoleFor(string actorType)
		{
			if (Info.RefineryTypes.Contains(actorType) || Info.SiloTypes.Contains(actorType))
				return CNBaseRole.Economy;

			if (Info.ProductionTypes.Contains(actorType) || Info.NavalProductionTypes.Contains(actorType))
				return CNBaseRole.Military;

			if (Info.TechTypes.Contains(actorType))
				return CNBaseRole.Core;

			return null;
		}

		static CPos AverageLocation(List<Actor> actors)
		{
			long x = 0;
			long y = 0;
			foreach (var a in actors)
			{
				x += a.Location.X;
				y += a.Location.Y;
			}

			return new CPos((int)(x / actors.Count), (int)(y / actors.Count));
		}

		/// <summary>
		/// Snaps a cell onto the global base raster. All bases share one lattice (anchored at the bot's
		/// original base location), so raster anchors stay commensurate between bases and never shift
		/// under buildings that are already placed.
		/// </summary>
		public CPos SnapToBaseGrid(CPos cell)
		{
			var grid = Math.Max(1, Info.BaseGridCellSize);
			var origin = BaseOrigin;

			int Snap(int value, int originValue)
			{
				var delta = value - originValue;
				var offset = (delta % grid + grid) % grid;
				return value - offset + (offset * 2 >= grid ? grid : 0);
			}

			return new CPos(Snap(cell.X, origin.X), Snap(cell.Y, origin.Y));
		}

		/// <summary>The bot's original base location. Fixed on first use, unlike initialBaseCenter.</summary>
		public CPos BaseOrigin
		{
			get
			{
				baseGridOrigin ??= initialBaseCenter;
				return baseGridOrigin.Value;
			}
		}

		/// <summary>The base around the bot's starting position. Stable anchor for base-wide decisions.</summary>
		public CNBotBase PrimaryBase => GetBaseNearest(BaseOrigin);

		public CNBotBase GetBaseNearest(CPos cell)
		{
			var bases = GetBases();
			var best = bases[0];
			var bestDistance = (cell - best.Center).LengthSquared;
			for (var i = 1; i < bases.Count; i++)
			{
				var distance = (cell - bases[i].Center).LengthSquared;
				if (distance >= bestDistance)
					continue;

				bestDistance = distance;
				best = bases[i];
			}

			return best;
		}

		/// <summary>
		/// Bases in the order they should be tried for one build order. Defenses and refineries follow the
		/// threat / resource anchor, everything else goes to the base that has the fewest of that type.
		/// </summary>
		public IReadOnlyList<CNBotBase> GetOrderedBasesForBuilding(string actorType, BuildingType type, string nearBuilding)
		{
			var bases = GetBases();

			// Groups without a construction yard are not build sites. They are skipped entirely unless the
			// bot has nothing else left, in which case placement falls back to whatever it still holds.
			if (bases.Any(b => b.IsBuildSite))
				bases = bases.Where(b => b.IsBuildSite).ToList();

			if (bases.Count <= 1)
				return bases;

			if (type == BuildingType.Refinery)
			{
				var resourceAnchor = ResourceConyardCenter ?? BaseOrigin;
				return bases.OrderBy(b => (b.Center - resourceAnchor).LengthSquared).ToList();
			}

			if (type == BuildingType.Defense)
			{
				var threatAnchor = GetDefenseReference(BaseOrigin);

				// The defense budget itself stays global - DefenseRoleLimits are checked against the bot's
				// total building count, so the bot never walls itself in just because it owns more bases.
				// What is distributed here is WHERE the next defense goes. Each base gets a share of the
				// defenses that exist, weighted by how much it has to protect, and the base closest to the
				// front gets a bonus on top of that share. Ordering by the largest shortfall means an exposed
				// expansion catches up first instead of waiting for the main base to stop consuming the pot.
				var frontBase = GetBaseNearestIn(bases, threatAnchor);
				var frontBonus = 100 + Math.Max(0, Info.FrontBaseDefenseShareBonusPct);

				var totalWeight = 0L;
				var totalDefenses = 0;
				foreach (var b in bases)
				{
					totalWeight += DefenseShareWeight(b, frontBase, frontBonus);
					totalDefenses += b.CountOfAny(Info.DefenseTypes);
				}

				if (totalWeight <= 0)
					return bases.OrderBy(b => (b.Center - threatAnchor).LengthSquared).ToList();

				var defenses = totalDefenses;
				var weightSum = totalWeight;
				return bases
					.OrderByDescending(b => defenses * DefenseShareWeight(b, frontBase, frontBonus) * 100 / weightSum
						- b.CountOfAny(Info.DefenseTypes) * 100L)
					.ThenBy(b => (b.Center - threatAnchor).LengthSquared)
					.ToList();
			}

			// A NearBuilding entry only makes sense in a base that actually has that building.
			var wantsNearBuilding = !string.IsNullOrEmpty(nearBuilding) && nearBuilding != actorType;

			// Tech buildings go to the base nearest the spawn. They are expensive, fragile, usually
			// built once, and losing one costs a whole branch of the tech tree — the safest base the
			// bot owns is the one it started at. Under the generic ordering below they tended to end
			// up in the newest forward expansion instead, because that base has none of them and the
			// fewest buildings overall, and spawn distance only ever broke a tie. An MCV pushed toward
			// the front also lands in broken terrain often enough that the placement then fails
			// outright and the item is cancelled and re-queued in a loop.
			//
			// A NearBuilding constraint still wins: gadept next to a war factory is worth more than
			// gadept at home.
			// DistributedTechTypes are the exception: radar and similar structures earn their keep by
			// covering ground, so concentrating them at the spawn wastes them. They keep the generic
			// ordering below, which spreads a type across the bases that have none.
			if (Info.TechTypes.Contains(actorType) && !Info.DistributedTechTypes.Contains(actorType))
				return bases
					.OrderByDescending(b => wantsNearBuilding && b.CountOf(nearBuilding) > 0)
					.ThenBy(b => (b.Center - BaseOrigin).LengthSquared)
					.ToList();

			// Need is measured against the base's own share, not against the raw count: a fresh expansion
			// has none of anything, so a plain "fewest wins" would redirect the whole build order there.
			// A base wants the type once it holds fewer than BuildingFractions says for its current size;
			// the expansion grows into its own deficits as refineries and defenses arrive.
			var fractions = GetActiveBuildingFractions();
			var fraction = fractions != null && fractions.TryGetValue(actorType, out var f) ? f : 0;

			// Redundancy floor before specialisation: a base that is still missing its one guaranteed power
			// plant / infantry / vehicle production gets this building first, whatever its role says. Only
			// the surplus follows the role, so losing the specialised base never removes a capability outright.
			var capability = Info.EnableBaseRoles ? GetBaseCapability(actorType) : CNBaseCapability.None;
			var preferredRole = Info.EnableBaseRoles ? GetPreferredRoleFor(actorType) : null;

			return bases
				.OrderByDescending(b => LacksCapabilityFloor(b, capability))
				.ThenByDescending(b => wantsNearBuilding && b.CountOf(nearBuilding) > 0)

				// An outpost holds a chokepoint with defense and support; it is the last choice for anything
				// that belongs to a role, but still a choice if no other base can take it.
				.ThenBy(b => preferredRole != null && b.Role == CNBaseRole.Outpost)
				.ThenByDescending(b => preferredRole != null && b.Role == preferredRole.Value)
				.ThenBy(b => b.CountOf(actorType) * 100 - fraction * b.Buildings.Count)
				.ThenBy(b => b.Buildings.Count)
				.ThenBy(b => (b.Center - BaseOrigin).LengthSquared)
				.ToList();
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

			// initialBaseCenter follows every MCV expansion. The raster origin must not: it is fixed to
			// the first reported base location so the lattice never shifts under buildings already placed.
			baseGridOrigin ??= newLocation;
		}

		void IBotPositionsUpdated.UpdatedDefenseCenter(CPos newLocation)
		{
			DefenseCenter = newLocation;
		}

		// Gated on the hard production floor, not on the refinery *target*. Those were the same number
		// until the target became budget-weighted, at which point raising it for production-heavy
		// profiles silently stopped them building any units at all while they were short of it —
		// MCVs included, so they could not expand their way out either.
		bool IBotRequestPauseUnitProduction.PauseUnitProduction => !IsTraitDisabled && !HasProductionFloorRefineries() &&
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

					// One circle query, two tallies: this used to run FindActorsInCircle twice over the
					// identical circle, once per conyard, inside the periodic resource-location scan.
					var refs = 0;
					var enemies = 0;
					foreach (var actor in world.FindActorsInCircle(conyard.CenterPosition, WDist.FromCells(Info.MaxBaseRadius)))
					{
						if (actor.Owner == player && Info.RefineryTypes.Contains(actor.Info.Name))
							refs++;
						else if (actor.Owner.RelationshipWith(player) == PlayerRelationship.Enemy)
							enemies++;
					}

					var suitable = -enemies - refs;

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

		/// <summary>
		/// The strongest remembered attack near the reference, or null when nothing has actually been
		/// attacked. Unlike <see cref="GetBestDefenseHotspot"/> this does NOT fall back to map topology, so
		/// callers that need evidence of a real threat do not get handed the nearest chokepoint instead.
		/// </summary>
		public CPos? GetRecordedDangerHotspot(CPos reference, DefenseRole role = DefenseRole.Default)
		{
			if (!Info.EnableDefenseDangerMemory || defenseDangerMemory.Count == 0)
				return null;

			CPos? bestHotspot = null;
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

			return bestHotspot;
		}

		public CPos? GetBestDefenseHotspot(CPos reference, DefenseRole role = DefenseRole.Default)
		{
			var bestHotspot = GetRecordedDangerHotspot(reference, role);

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

		// World-space vector between two cells. Direction maths on raw CPos deltas is skewed on the
		// RectangularIsometric grid, where a step in X and a step in Y are not the same physical
		// distance — "toward the enemy" then means something different depending on the bearing.
		WVec WorldVec(CPos from, CPos to) => world.Map.CenterOfCell(to) - world.Map.CenterOfCell(from);

		// Both terms are ratios over targetLenSq (projection along the axis, and perpendicular offset
		// relative to the axis length), so they are scale-invariant: moving the maths to world space
		// corrects the geometry WITHOUT changing the magnitude the configured weights are tuned against.
		long ScoreCellToward(CPos cell, CPos center, CPos target, int weight)
		{
			if (weight <= 0 || center == target)
				return 0;

			var toTarget = WorldVec(center, target);
			var toCell = WorldVec(center, cell);
			var targetLenSq = Math.Max(1, toTarget.HorizontalLengthSquared);
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
			// Tolerance rather than exact equality. The reference used to be DefenseCenter, which changed at
			// most every DefenseCenterUpdateInterval; since it comes from the decaying danger memory the
			// argmax can flip between a handful of remembered hotspots, and each flip rebuilt the bearings,
			// the high-ground set and the chokepoint anchors. A few cells of drift changes none of them
			// meaningfully - the cone test is 45 degrees wide.
			var threshold = Math.Max(0, Info.FlankCacheMoveThreshold);
			if (cachedFlankCenter != new CPos(int.MinValue, int.MinValue)
				&& (cachedFlankCenter - defenseCenter).LengthSquared <= threshold * threshold)
				return;

			cachedFlankCenter = defenseCenter;

			// Bearings are stored in world space so the cone tests below compare like with like.
			// GetAccessBearings hands back cell deltas (chokepoint cell minus reference).
			cachedAccessBearings.Clear();
			foreach (var bearing in TacticalMapModule.GetAccessBearings(defenseCenter))
				cachedAccessBearings.Add(WorldVec(defenseCenter, defenseCenter + bearing));

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
				// Distance gate stays in cells (it only asks "is this far enough from the centre to
				// have a meaningful bearing"); the cone test itself runs in world space, see VectorFacesAccess.
				if ((cell - defenseCenter).LengthSquared >= 9 && !VectorFacesAccess(WorldVec(defenseCenter, cell)))
					adjust -= Info.SealedFlankPenaltyWeight;
			}

			return adjust;
		}

		/// <summary>
		/// True when nothing an attacker could arrive from lies beyond this cell, so a defense there would
		/// cover a map edge, a cliff or open water. Used as a veto by defense placement.
		/// </summary>
		public bool IsSealedFlankCell(CPos cell, CPos defenseCenter)
		{
			if (TacticalMapModule == null || Info.SealedFlankPenaltyWeight <= 0)
				return false;

			EnsureFlankCache(defenseCenter);

			// Too close to the centre to have a meaningful bearing - same gate the score penalty uses.
			if ((cell - defenseCenter).LengthSquared < 9)
				return false;

			if (cachedAccessBearings.Count > 0)
				return !VectorFacesAccess(WorldVec(defenseCenter, cell));

			// No known corridors on this map: the score penalty used to silently do nothing here, which is
			// how turrets ended up lining the map edge. Fall back to asking the terrain directly whether
			// there is any ground out there at all.
			return !HasReachableGroundBeyond(cell, defenseCenter);
		}

		// Coarse outward probe: step away from the base along the candidate's bearing and look for ground an
		// attacker could stand on. Uses the harvester locomotors the module already holds - not exactly an
		// attacker's movement class, but enough to tell solid ground from map edge, cliff and water.
		bool HasReachableGroundBeyond(CPos cell, CPos defenseCenter)
		{
			if (HarvesterLocomotorsList.Length == 0)
				return true;

			var delta = cell - defenseCenter;
			var step = new CVec(Math.Sign(delta.X), Math.Sign(delta.Y));
			if (step == CVec.Zero)
				return true;

			var probe = Math.Max(1, Info.SealedFlankProbeCells);
			for (var i = 1; i <= probe; i++)
			{
				var next = cell + step * i;
				if (!world.Map.Contains(next))
					return false;

				foreach (var locomotor in HarvesterLocomotorsList)
					if (locomotor.MovementCostForCell(next) != PathGraph.MovementCostForUnreachableCell)
						return true;
			}

			return false;
		}

		bool HighGroundEdgeFacesAccess(CNHighGroundEdge edge, CPos defenseCenter)
		{
			if (cachedAccessBearings.Count == 0)
				return false;

			return VectorFacesAccess(WorldVec(defenseCenter, edge.Cell))
				&& VectorFacesAccess(WorldVec(edge.Cell, edge.Cell + edge.Outward));
		}

		// True if v points within ~45 degrees of any access-corridor bearing. World space throughout:
		// the same angular test on cell deltas accepts a visibly different cone depending on the
		// bearing, because the isometric grid stretches one axis relative to the other.
		bool VectorFacesAccess(WVec v)
		{
			if (v.HorizontalLengthSquared == 0)
				return true;

			foreach (var b in cachedAccessBearings)
			{
				var dot = (long)v.X * b.X + (long)v.Y * b.Y;
				if (dot <= 0)
					continue;

				var cross = (long)v.X * b.Y - (long)v.Y * b.X;

				// |cross| <= dot is the 45-degree cone. Compared without squaring: at world scale
				// both sides reach ~1e11 and squaring them would overflow a long.
				if (Math.Abs(cross) <= dot)
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

				// World space, as in ScoreCellToward: approachDot is normalised by threatLenSq, so the
				// magnitude the configured weight is tuned against is unchanged — only the geometry.
				var toThreat = WorldVec(coreCenter, threat.Location);
				var toCell = WorldVec(coreCenter, cell);
				var threatLenSq = Math.Max(1, toThreat.HorizontalLengthSquared);
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
				CNBotLog.Debug("{0} has no possible rallypoint near {1}", producer.Owner, producer.Location);
				return producer.Location;
			}

			return possibleRallyPoints.Random(world.LocalRandom);
		}

		Locomotor[] LocomotorsForProducibles(Actor producer)
		{
			// Per-actor production
			var productions = producer.TraitsImplementing<Production>();

			// Player-wide production.
			// FORK PATCH: upstream OpenRA has `!=` here (BaseBuilderBotModule.cs), which collects the
			// production traits of every OTHER player — so the fallback derived rally-point locomotors
			// from enemy factories instead of our own. `==` is what the comment above it describes.
			if (!productions.Any())
				productions = producer.World.ActorsWithTrait<Production>().Where(x => x.Actor.Owner == producer.Owner).Select(x => x.Trait);

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

		/// <summary>
		/// The bot has the bare minimum economy needed to justify building units at all. Separate from
		/// HasMinimalRefineryCount, which measures against the budget-weighted expansion target.
		/// </summary>
		public bool HasProductionFloorRefineries() =>
			AIUtils.CountActorByCommonName(RefineryBuildings) >= Math.Max(1, Info.ProductionPauseRefineryCount);

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

		// Walks every resource indice. Reached via GetTargetRefineryCount -> HasAdequateRefineryCount,
		// which ShouldExpandEconomy, ShouldAddProduction, the profile module and the queue manager all
		// call — so this ran the full sweep many times per tick. The underlying indices are refreshed
		// one per UpdateResourceMapInverval ticks, so a short cache cannot go meaningfully stale.
		int cachedSupportedRefineryCapacity;
		int cachedSupportedRefineryCapacityTick = int.MinValue;

		int GetSupportedRefineryCapacity()
		{
			if (ResourceMapModule == null)
				return int.MaxValue;

			// The int.MinValue "never computed" sentinel underflows this subtraction: WorldTick minus
			// int.MinValue wraps to a large negative number, the age test passes on the very first
			// call, and the method returns the zero-initialised cache — forever, because the line that
			// records the tick sits below and is never reached. The scan never ran once.
			//
			// GetTargetRefineryCount clamps its target to this value, so the target collapsed to
			// Math.Max(activeMinRefinery, 0), i.e. exactly the minimum. That is why bots never built
			// more than two refineries no matter how much tiberium the map held, and why no placement
			// or field-viability message ever appeared: the economy check was satisfied at two.
			if (cachedSupportedRefineryCapacityTick != int.MinValue &&
				world.WorldTick - cachedSupportedRefineryCapacityTick < SupportedRefineryCapacityMaxAgeTicks)
				return cachedSupportedRefineryCapacity;

			// Only fields the bot can actually work count. Summing every indice on the map answers
			// "how much tiberium exists", but the number wanted here is "how much can we haul", and a
			// field on the far side of the map contributes nothing to that — it just inflates the cap
			// until the target formula's own terms (production buildings, extra construction yards,
			// economy overflow) run unchecked and the bot rings a single field with refineries.
			//
			// A field counts once the bot has a refinery in it or a base within building range of it.
			// Expanding to a new field therefore raises the ceiling, which is the intended order:
			// first reach the tiberium, then build the refineries for it.
			var supportedCapacity = 0;
			var scored = 0;
			var largestField = 0;
			for (var i = 0; i < ResourceMapModule.GetIndicesLength(); i++)
			{
				var indice = ResourceMapModule.GetIndice(i);
				if (indice == null)
					continue;

				if (indice.ResourceCellsCount > largestField)
					largestField = indice.ResourceCellsCount;

				if (!IsIndiceWithinReach(indice))
					continue;

				var capacity = GetSupportedRefineryCapacity(indice);
				supportedCapacity += capacity;
				if (capacity > 0)
					scored++;
			}

			// This caps GetTargetRefineryCount, so when it comes out low the bot stops wanting
			// refineries entirely — no placement is ever attempted and nothing else reports why.
			CNBotLog.Debug(
				"{0} refinery capacity: {1} from {2}/{3} indices (largest field {4} cells, thresholds {5}/{6})",
				player, supportedCapacity, scored, ResourceMapModule.GetIndicesLength(),
				largestField, Info.MinFiniteFieldCellsForRefinery, Info.MinFiniteFieldCellsForExtraRefinery);

			cachedSupportedRefineryCapacityTick = world.WorldTick;
			cachedSupportedRefineryCapacity = Math.Max(Info.InititalMinimumRefineryCount, supportedCapacity);
			return cachedSupportedRefineryCapacity;
		}

		/// <summary>
		/// True if the bot already works this field or has a base close enough to build at it. Anything
		/// further out is tiberium it does not have access to yet, and counting it toward refinery
		/// capacity would licence refineries it has nowhere to put.
		/// </summary>
		bool IsIndiceWithinReach(CNResourceIndice indice)
		{
			if (indice.PlayerRefineryCount > 0)
				return true;

			var reach = GetEffectiveMaxBaseRadius();
			var reachSq = (long)reach * reach;

			foreach (var b in GetBases())
			{
				if (!b.IsBuildSite)
					continue;

				if ((b.Center - indice.IndiceCenter).LengthSquared <= reachSq)
					return true;
			}

			return false;
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
			// Sell one refinery each time, and only ever an actual refinery type.
			//
			// This used to select on the Refinery trait, which in CN also sits on nawast. Waste
			// facilities are not refineries to any other part of the bot — RefineryTypes is gproc and
			// nproc — so they inflated the count this threshold is checked against while the sale
			// itself took a real refinery. A Nod bot on an exhausted field could sell its way down
			// past the intended floor because two nawast were padding the total.
			var refineries = world.ActorsHavingTrait<Refinery>()
				.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.RefineryTypes.Contains(a.Info.Name))
				.ToArray();

			if (refineries.Length <= GetActiveInititalMinimumRefineryCount() + Info.AdditionalMinimumRefineryCount)
				return;

			// Hard floor independent of the budget-weighted target above: whatever the strategy says,
			// a bot that sells its last refinery has no way back. The target is an expansion goal and
			// can legitimately be low; this is survival.
			if (refineries.Length <= Math.Max(1, Info.ProductionPauseRefineryCount))
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

			// Selling an idle refinery only makes sense while another one is still working. When the
			// field runs dry every refinery goes idle at once, and then this loop would sell them one
			// after another for as long as the count threshold allowed — the bot dismantling its own
			// economy precisely when it has none. Keeping them costs power; not having one when the
			// expansion finally reaches tiberium costs the game.
			if (!refineries.Any(IsRefineryActive))
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

			CNBotLog.Debug($"CN AI: Selling {sellCandidate} to recover refinery economy. Cash {cash}, refinery cost {refineryCost}.");
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

			CNBotLog.Debug($"CN AI: Selling {sellCandidate} to recover harvester. Cash {cash}, harvester cost {harvesterCost}.");
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

			CNBotLog.Debug($"CN AI: Selling {sellCandidate} to recover MCV. Cash {cash}, MCV cost {mcvCost}.");
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
			CNBotLog.Debug($"CN AI: Terminal bankruptcy — selling all and going kamikaze. Cash {cash}.");

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
