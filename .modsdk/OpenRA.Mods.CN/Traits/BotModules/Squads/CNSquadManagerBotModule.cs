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
using OpenRA.Mods.CN.Traits.BotModules.Squads.States;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads
{
	// ---------------------------------------------------------------------------
	// YAML Info: Slot
	// ---------------------------------------------------------------------------
	public class CNSlotInfo
	{
		[FieldLoader.Require]
		[Desc("Ordered list of actor type names to fill this slot (first available wins).")]
		public readonly string[] AllowedTypes = [];

		[Desc("How many units should fill this slot.")]
		public readonly int Count = 1;

		[Desc("If true, squad can activate without this slot being full.")]
		public readonly bool Optional = false;

		[Desc("Ground carrier: loads IsPassenger units before moving.")]
		public readonly bool IsCarrier = false;

		[Desc("Aircraft carrier: flies to pickup point, then to LZ.")]
		public readonly bool IsAircraftCarrier = false;

		[Desc("If true, carrier drops passengers via parachute instead of landing.")]
		public readonly bool IsParadrop = false;

		[Desc("Units in this slot board a carrier before the squad departs.")]
		public readonly bool IsPassenger = false;

		[Desc("Units in this slot fight alongside passengers at the LZ but never board the carrier. They follow the carrier overland and attack after unload.")]
		public readonly bool IsEscort = false;

		[Desc("After unloading, carrier returns to base. False = stays and fights.")]
		public readonly bool ReturnAfterUnload = true;

		[Desc("Restrict this slot to specific factions (empty = all factions).")]
		public readonly string[] Factions = [];
	}

	// ---------------------------------------------------------------------------
	// YAML Info: Team Template
	// ---------------------------------------------------------------------------
	public class CNSquadNeedRuleInfo
	{
		[Desc("Enemy BotCapabilities that raise the need for the matching squad tag.")]
		public readonly string[] EnemyCapabilities = [];

		[Desc("Score added for each visible enemy with one of EnemyCapabilities.")]
		public readonly int VisibleWeight = 10;

		[Desc("Score added for each enemy with one of EnemyCapabilities, ignoring shroud.")]
		public readonly int GlobalWeight = 0;

		[Desc("Flat score added when any visible enemy with one of EnemyCapabilities exists.")]
		public readonly int VisibleBonus = 0;

		[Desc("Flat score added when any enemy with one of EnemyCapabilities exists, ignoring shroud.")]
		public readonly int GlobalBonus = 0;
	}

	public class CNTeamTemplateInfo
	{
		[Desc("Squad behavior type for this team.")]
		public readonly CNSquadType Role;

		[Desc("Free-form squad response tags used by the tag/need scoring model.")]
		public readonly string[] Tags = [];

		[Desc("Optional score adjustment for tagged templates. Use this to prefer a squad over another squad with similar tags.")]
		public readonly int Bias = 0;

		[Desc("Score penalty per active squad of this exact tagged template. -1 uses the module RepeatPenalty.")]
		public readonly int RepeatPenalty = -1;

		[Desc("Maximum number of simultaneous active squads of this template.")]
		public readonly int MaxInstances = 1;

		[Desc("Number of non-optional slots that must be filled to activate.")]
		public readonly int MinSlotsToActivate = 1;

		[Desc("This squad attaches to (follows) active squads of these types.")]
		public readonly CNSquadType[] AttachToRole = [];

		[Desc("Preferred target capability tags in priority order (first match wins). " +
			"Matches actors that have BotCapabilities: <tag>. Applies to every role whose target search " +
			"runs through CNSquadHelper.FindTarget - the ground assault and wave states included, not " +
			"just the raider/stealth roles this used to claim.")]
		public readonly string[] PriorityTargetCapabilities = [];

		[Desc("Restrict template to specific factions (empty = all factions).")]
		public readonly string[] Factions = [];

		[Desc("If true, units in active squads of this template can be poached by higher-priority templates.")]
		public readonly bool Poachable = false;

		[Desc("If true, support squads stay near the base instead of attaching to attack squads.")]
		public readonly bool StayInBase = false;

		[Desc("Transport/SubterraneanTransport only: after dropping its passengers (engineers) behind enemy " +
			"lines, the squad orders them to capture the nearest valuable enemy building and immediately " +
			"sell it for cash + denial. The (empty) carriers then return home.")]
		public readonly bool CaptureAndSell = false;

		[Desc("When set, limits MaxInstances based on how many of this building type the player owns. " +
			"Effective MaxInstances = min(MaxInstances, numberOfBuildings * SquadsPerBuilding) when buildingCount > 0, else 0.")]
		public readonly string ScaleWithBuilding = null;

		[Desc("Number of squad instances each ScaleWithBuilding instance can support. " +
			"Used only when ScaleWithBuilding is set. Formula: scaledMax = buildingCount * SquadsPerBuilding.")]
		public readonly int SquadsPerBuilding = 1;

		[Desc("If true, this template's units skip the production queue's normal DesiredCashReserve/" +
			"AdditionalCashReservePerQueue buffer when checking affordability - only the flat " +
			"ProductionMinCashRequirement applies. For rare, expensive, high-priority units (heavy Bias, " +
			"Count:1 slots) that would otherwise almost never clear the cash-reserve bar against cheaper, " +
			"more frequent demand from other squads.")]
		public readonly bool IgnoresCashReserve = false;

		[Desc("Slot definitions keyed by slot name.")]
		[FieldLoader.LoadUsing(nameof(LoadSlots))]
		public readonly Dictionary<string, CNSlotInfo> Slots = [];

		static object LoadSlots(MiniYaml yaml)
		{
			var slots = new Dictionary<string, CNSlotInfo>();
			var slotsNode = yaml.NodeWithKeyOrDefault("Slots");
			if (slotsNode == null)
				return slots;
			foreach (var node in slotsNode.Value.Nodes)
				slots[node.Key] = FieldLoader.Load<CNSlotInfo>(node.Value);
			return slots;
		}
	}

	// ---------------------------------------------------------------------------
	// YAML Info: Module
	// ---------------------------------------------------------------------------
	[Desc("CN custom squad manager. Manages template-based squads with slot-filling logic.")]
	public class CNSquadManagerBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Delay (ticks) between role assignment passes.")]
		public readonly int AssignRolesInterval = 50;

		[Desc("Delay (ticks) between attack force creation attempts.")]
		public readonly int AttackForceInterval = 75;

		[Desc("Ticks an offensive template squad may remain idle without a valid target before its units are released back to the assignment pool.")]
		public readonly int NoTargetIdleReleaseTicks = 300;

		[Desc("Delay (ticks) between rush attack attempts.")]
		public readonly int RushInterval = 600;

		[Desc("Delay (ticks) between removing dead units from squad bookkeeping.")]
		public readonly int CleanupInterval = 10;

		[Desc("Delay (ticks) between enemy capability scans for NeedRules. 0 = disabled.")]
		public readonly int ThreatScanInterval = 150;

		[Desc("If true, NeedRules only consider units belonging to the current nemesis player. " +
			"Falls back to all enemies when no nemesis is determined. Recommended for FFA/2v2 games.")]
		public readonly bool ThreatOnlyFromNemesis = true;

		[Desc("Minimum delay (ticks) before first attack force is created.")]
		public readonly int MinimumAttackForceDelay = 0;

		[Desc("Scan radius (cells) when squad is idle looking for enemies.")]
		public readonly int IdleScanRadius = 10;

		[Desc("Scan radius (cells) for flee decisions.")]
		public readonly int DangerScanRadius = 10;

		[Desc("Scan radius (cells) when squads are moving to attack.")]
		public readonly int AttackScanRadius = 12;

		[Desc("Scan radius (cells) for protection squads.")]
		public readonly int ProtectionScanRadius = 8;

		[Desc("How many cells artillery stays behind the frontline.")]
		public readonly int ArtilleryHangBackCells = 8;

		[Desc("How many cells support squads stay behind their attached squad.")]
		public readonly int SupportFollowRangeCells = 10;

		[Desc("Base score added to tagged templates by squad role. Used by the simplified tag/need scoring model.")]
		public readonly Dictionary<CNSquadType, int> RoleWeights = [];

		[Desc("Static score added for each matching squad tag. Useful for always-useful tags such as Frontline or Support.")]
		public readonly Dictionary<string, int> TagWeights = [];

		[Desc("Dynamic need rules keyed by squad tag. Enemy BotCapabilities raise the need for templates with the same tag.")]
		[FieldLoader.LoadUsing(nameof(LoadNeedRules))]
		public readonly Dictionary<string, CNSquadNeedRuleInfo> NeedRules = [];

		[Desc("Optional list of valid squad tags. If set, templates, TagWeights, and NeedRules using unknown tags fail at ruleset load.")]
		public readonly string[] KnownSquadTags = [];

		[Desc("Base score for templates that use Tags.")]
		public readonly int TaggedTemplateBaseScore = 50;

		[Desc("Default score penalty per already active squad of the same tagged template.")]
		public readonly int RepeatPenalty = 8;

		[Desc("Score penalty per already active squad sharing each tag.")]
		public readonly int TagSaturationPenalty = 4;

		[Desc("Score penalty per already active squad of the same role when picking roles.")]
		public readonly int RoleSaturationPenalty = 8;

		[Desc("Maximum absolute dynamic score contribution from one NeedRules entry. 0 disables the cap.")]
		public readonly int MaxNeedScorePerTag = 100;

		[Desc("Score penalty applied to a tag's rolling performance for each unit lost from a squad carrying that tag.")]
		public readonly int PerformancePenaltyPerLoss = 8;

		[Desc("Per-cleanup-pass decay (0-1) applied to every tag's rolling performance penalty, recovering it back toward 0 " +
			"when its squads stop losing units. Lower = forgets losses faster.")]
		public readonly float PerformanceDecay = 0.97f;

		[Desc("Maximum (most negative) rolling performance penalty a single tag can accumulate. 0 disables the cap.")]
		public readonly int MaxPerformancePenaltyPerTag = 40;

		[Desc("Randomness exponent for template selection (squad formation and production). " +
			"Templates are chosen by a weighted lottery where the chance is proportional to " +
			"max(1, EffectiveScore)^TemplateSelectionSharpness, so every template keeps getting used over time. " +
			"0 = fully even (need ignored), 1-2 = lightly steered by need, higher approaches strict best-first.")]
		public readonly int TemplateSelectionSharpness = 2;

		[Desc("Ticks an idle unit may wait before the salvage pass forces it into any matching offensive template.")]
		public readonly int IdleSalvageTicks = 300;

		[Desc("Ticks a support squad scans for an attach target before falling back to base garrison duty.")]
		public readonly int MaxIdleScanTicks = 600;

		[Desc("Cells support garrison squads are allowed to roam from the base center.")]
		public readonly int BaseGarrisonRadius = 8;

		[Desc("Enemy target types to never attack.")]
		public readonly BitSet<TargetableType> IgnoredEnemyTargetTypes;

		[Desc("Ticks a launched wave may spend gathering before it is written off. AttackWaveMaxActiveTicks " +
			"only starts once the first squad leaves the rally, so without this a wave that never sets off " +
			"would hold the slot forever. Generous on purpose: it must cover the march plus the staging " +
			"wait on a large map, and only fires when not one squad ever got moving.")]
		public readonly int AttackWaveLaunchGraceTicks = 3000;

		[Desc("Cells a launched wave's squads may run ahead of the wave's centre before they hold and let " +
			"the rest catch up. Only applies in the direction of the objective, so a squad that has fallen " +
			"behind is never told to wait. 0 disables cohesion and lets every squad advance at its own pace.")]
		public readonly int WaveCohesionCells = 12;

		[Desc("Master switch for target claiming. When false, squads pick targets without regard for what " +
			"other squads have already committed to - the behaviour before the claim registry existed.")]
		public readonly bool TargetClaimingEnabled = true;

		[Desc("How much better suited another squad must already be - measured as the percentage-point gap " +
			"between the shares of each squad's units that counter the target - before this one leaves the " +
			"target to them. This is what turns the per-squad counter bonus into an actual division of " +
			"labour, so the anti-infantry group takes the infantry and the tanks go for the tanks. " +
			"0 disables deference and lets every squad simply prefer what suits it.")]
		public readonly int CounterDeferenceMargin = 34;

		[Desc("Percent of a squad's free units that are ordered to attack its target directly instead of " +
			"attack-moving toward it. Direct orders concentrate fire, killing one enemy at a time rather " +
			"than spreading damage across everything in range - the clearest difference between a bot and " +
			"a player leading units by hand. 0 restores attack-move for everyone.")]
		public readonly int FocusFireStrictness = 100;

		[Desc("Cells beyond which a squad stops issuing direct attack orders. An attack order makes units " +
			"pursue and an attack-move does not, so without a leash a squad strings itself out chasing a " +
			"fleeing target.")]
		public readonly int PursuitLeashCells = 8;

		[Desc("Ticks between refreshes of the remembered enemy defences. The bot only counts defences it " +
			"has seen, and keeps them in mind while they are under fog, so a squad that was driven off " +
			"does not forget what drove it off the moment it loses sight. 0 disables the memory, which " +
			"leaves the bot blind to defences entirely rather than all-seeing.")]
		public readonly int EnemyDefenseMemoryInterval = 50;

		[Desc("If true, attacks stage at the most lightly defended way in that the topology scan knows of, " +
			"instead of marching down the straight line from base to target. False keeps the direct line.")]
		public readonly bool AvoidDefendedApproaches = true;

		[Desc("How far from the target to look for a way in, in cells. Beyond this the detour costs more " +
			"than the defences it avoids.")]
		public readonly int ApproachSearchRadiusCells = 24;

		[Desc("How much longer than the direct line the route through a chosen way in may be, in percent. " +
			"Without this the coldest chokepoint wins even when it lies beyond the target, and squads " +
			"march past the objective to gather behind it.")]
		public readonly int ApproachMaxDetourPercent = 40;

		[Desc("Cells short of the chosen way in that the squads actually gather. A chokepoint is narrow by " +
			"definition, and assembling inside one bunches the squad up where it is easiest to shell.")]
		public readonly int ApproachStandOffCells = 6;

		[Desc("How many points along the run-in are sampled when weighing one approach against another. " +
			"Emplacements cover approaches rather than the buildings behind them, so the fire is on the " +
			"stretch between the gathering point and the objective and has to be summed along it.")]
		public readonly int ApproachThreatSamples = 10;

		[Desc("Score penalty per point of static-defence damage per salvo covering a target, in hundredths. " +
			"Makes squads prefer objectives that are not sitting under a battery of guns, so a beaten squad " +
			"tries somewhere else rather than walking back into what just killed it. 0 ignores defences.")]
		public readonly int DefenseThreatPenaltyPercent = 200;

		[Desc("Cells from the squad's centre at which a unit is called back. PursuitLeashCells only " +
			"governs whether a pursuit is started; an attack activity then follows its target for as long " +
			"as it lives, so this is what actually ends the chase. 0 disables the recall.")]
		public readonly int PursuitRecallCells = 16;

		[Desc("Enemy gun platforms in contact from which the squad treats the engagement as a stand-up " +
			"fight and stops withdrawing damaged units, trading to the finish instead. A matchup it " +
			"cannot win still pulls the whole squad through the normal flee check. 0 always withdraws.")]
		public readonly int StandUpFightMinEnemies = 5;

		[Desc("Percent of a building's remaining health that squads may commit damage to before it counts " +
			"as covered and stops attracting more of them. Above 100 to leave room for shots that miss or " +
			"land after it dies.")]
		public readonly int OverkillFactorBuilding = 115;

		[Desc("OverkillFactorBuilding for mobile targets. Higher, because they dodge, retreat and get " +
			"repaired, so a committed salvo is less likely to land in full.")]
		public readonly int OverkillFactorMobile = 140;

		[Desc("Master switch for the coordinated attack wave system. If false, squads attack independently as soon as they form.")]
		public readonly bool AttackWaveEnabled = true;

		[Desc("Ticks before the first wave can fire, measured from the start of the game rather than " +
			"from this module enabling — an adaptive profile switch swaps one profile's squad manager " +
			"for another mid-game and must not re-arm the delay.")]
		public readonly int AttackWaveInitialDelay = 3000;

		[Desc("Minimum ticks between the launch of one wave and the next. This is the spacing between " +
			"waves, not the rate at which readiness is evaluated — see AttackWaveCheckInterval.")]
		public readonly int AttackWaveInterval = 4500;

		[Desc("Ticks between readiness evaluations once the wave cooldown has expired. Keep well below " +
			"AttackWaveInterval: this is what decides how quickly a wave goes out after the last " +
			"required squad is ready.")]
		public readonly int AttackWaveCheckInterval = 250;

		[Desc("Minimum ready (Operational) wave-eligible squads required to launch a wave. " +
			"Grows over time up to AttackWaveMaxMinReadySquads when AttackWaveSizeGrowthInterval > 0.")]
		public readonly int AttackWaveMinReadySquads = 2;

		[Desc("Hard cap on AttackWaveMinReadySquads after growth.")]
		public readonly int AttackWaveMaxMinReadySquads = 6;

		[Desc("After waiting this many times AttackWaveInterval without reaching the normal threshold, " +
			"launch a fallback wave with whatever is available (>= AttackWaveFallbackMinSquads).")]
		public readonly int AttackWaveMaxSkipsBeforeFallback = 2;

		[Desc("Minimum ready squads required for a fallback wave (when the normal threshold has been skipped too often).")]
		public readonly int AttackWaveFallbackMinSquads = 1;

		[Desc("Damage state at which a unit is pulled out of a fighting squad and released for repair.",
			"Undamaged disables withdrawal entirely. Deliberately not set to Light: a squad that sheds",
			"a member on the first scratch bleeds itself out before it reaches anything.")]
		public readonly DamageState WithdrawDamageState = DamageState.Critical;

		[Desc("Percent of its living units a squad must retain when withdrawing damaged members.",
			"Withdrawal stops once the squad is down to this share, so it never dismantles itself",
			"one casualty at a time — at that point the whole squad retreating is the right answer.")]
		public readonly int MinStrengthPercentAfterWithdraw = 60;

		[Desc("Percent of its original size a retreating squad must still have to regroup and return.",
			"Below this it dissolves instead, so its survivors are folded into a fresh full-strength",
			"squad rather than limping back out understrength.")]
		public readonly int SquadRegroupStrengthPercent = 50;

		[Desc("Ticks between updates of a squad that is currently engaged — it holds a live target. Kept",
			"well below AttackForceInterval: this is how long a squad in combat needs to notice that its",
			"target died, that it is being beaten, or that something better is in reach. Squads without a",
			"target keep the slower AttackForceInterval cadence, which is what bounds the cost: idle squads",
			"are the many, engaged squads the few.")]
		public readonly int EngagedSquadInterval = 15;

		[Desc("Ticks between AttackWaveMinReadySquads growth steps. 0 = disabled.")]
		public readonly int AttackWaveSizeGrowthInterval = 0;

		[Desc("Amount added to AttackWaveMinReadySquads each growth interval. Capped by AttackWaveMaxMinReadySquads.")]
		public readonly int AttackWaveSizeGrowthAmount = 1;

		[Desc("How far along the line from our own base to the target the wave stages, in percent. " +
			"0 is at home, 100 is on top of the target. Scales with the distance actually involved, so the " +
			"same value means the same thing on a small map and a large one.")]
		public readonly int AttackWaveStagingProgressPercent = 65;

		[Desc("Cells of safe distance from the target that the staging point must keep, overriding " +
			"AttackWaveStagingProgressPercent when the two disagree. Only a floor for short distances: " +
			"at any real separation the percentage already keeps the wave further out than this.")]
		public readonly int AttackWaveStagingOffsetCells = 12;

		[Desc("Max ticks a wave waits at the rally point for stragglers before transitioning to attack.")]
		public readonly int AttackWaveStagingTimeoutTicks = 600;

		[Desc("Hard timeout for a launched wave. If participants are still in hold/rally after this many ticks, " +
			"force them into their role state and allow the next wave.")]
		public readonly int AttackWaveMaxActiveTicks = 1800;

		[Desc("Percent cut to AttackWaveInterval at EconomyOverflow factor 1.0 (faster waves when bot has surplus economy).")]
		public readonly int EconomyOverflowWaveIntervalCutPct = 40;

		[Desc("Percent cut to the current AttackWaveMinReadySquads threshold at EconomyOverflow factor 1.0 (lower bar to launch).")]
		public readonly int EconomyOverflowWaveMinReadyCutPct = 40;

		[Desc("Max additive boost to the fuzzy attack-or-flee threshold at EconomyOverflow factor 1.0 (more aggressive engagements). Internally clamped to 0..10.")]
		public readonly double EconomyOverflowFuzzyAttackBoost = 8.0;

		[Desc("Cells of arrival tolerance at the rally point.")]
		public readonly int AttackWaveStagingArrivalCells = 5;

		[Desc("Percent of the wave's participating squads that must have reached the rally point before any of them " +
			"moves on to the attack. Below 100 the wave tolerates stragglers; the staging timeout releases it either way.")]
		public readonly int AttackWaveStagingMinArrivedPercent = 66;

		[Desc("Cells of random scatter around the hold position so wave-holding squads don't stack on top of each other.")]
		public readonly int WaveHoldScatterCells = 4;

		[Desc("Radius in cells around an own building within which a forming squad counts as being at home. " +
			"Squads formed outside it skip the wave hold and go straight to their role state, so units " +
			"already in the field are not recalled.")]
		public readonly int WaveHoldHomeRadiusCells = 20;

		[Desc("Squad roles that participate in coordinated waves. Protection is always excluded regardless of this list. " +
			"Roles not listed attack independently as soon as they are formed.")]
		public readonly CNSquadType[] WaveParticipantRoles =
		[
			CNSquadType.Assault,
			CNSquadType.ArtilleryAssault,
			CNSquadType.SubterraneanAssault,
		];

		[Desc("Team template definitions.")]
		[FieldLoader.LoadUsing(nameof(LoadTeams))]
		public readonly Dictionary<string, CNTeamTemplateInfo> Teams = [];

		static object LoadTeams(MiniYaml yaml)
		{
			var teams = new Dictionary<string, CNTeamTemplateInfo>();
			var teamsNode = yaml.NodeWithKeyOrDefault("Teams");
			if (teamsNode == null)
				return teams;
			foreach (var node in teamsNode.Value.Nodes)
				teams[node.Key] = FieldLoader.Load<CNTeamTemplateInfo>(node.Value);
			return teams;
		}

		static object LoadNeedRules(MiniYaml yaml)
		{
			var rules = new Dictionary<string, CNSquadNeedRuleInfo>();
			var rulesNode = yaml.NodeWithKeyOrDefault("NeedRules");
			if (rulesNode == null)
				return rules;
			foreach (var node in rulesNode.Value.Nodes)
				rules[node.Key] = FieldLoader.Load<CNSquadNeedRuleInfo>(node.Value);
			return rules;
		}

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			// Protection is reactive — putting it into a wave would leave the base
			// without emergency response units between waves.
			if (WaveParticipantRoles != null)
			{
				foreach (var role in WaveParticipantRoles)
					if (role == CNSquadType.Protection)
						throw new YamlException(
							$"CNSquadManagerBotModule on {ai.Name}: WaveParticipantRoles must not contain {role} — " +
							"reactive protection is excluded from coordinated waves by design.");
			}

			if (KnownSquadTags.Length > 0)
			{
				var knownTags = new HashSet<string>(KnownSquadTags, StringComparer.OrdinalIgnoreCase);
				foreach (var tag in TagWeights.Keys)
					if (!knownTags.Contains(tag))
						throw new YamlException($"CNSquadManagerBotModule on {ai.Name}: TagWeights contains unknown squad tag `{tag}`.");

				foreach (var tag in NeedRules.Keys)
					if (!knownTags.Contains(tag))
						throw new YamlException($"CNSquadManagerBotModule on {ai.Name}: NeedRules contains unknown squad tag `{tag}`.");

				foreach (var (templateName, template) in Teams)
					foreach (var tag in template.Tags)
						if (!knownTags.Contains(tag))
							throw new YamlException($"CNSquadManagerBotModule on {ai.Name}: template `{templateName}` contains unknown squad tag `{tag}`.");
			}

			foreach (var (templateName, template) in Teams)
				if (template.Tags.Length == 0)
					throw new YamlException($"CNSquadManagerBotModule on {ai.Name}: template `{templateName}` must define at least one squad tag.");
		}

		public override object Create(ActorInitializer init)
		{
			return new CNSquadManagerBotModule(init.Self, this);
		}
	}

	// ---------------------------------------------------------------------------
	// Runtime module
	// ---------------------------------------------------------------------------
	public class CNSquadManagerBotModule : ConditionalTrait<CNSquadManagerBotModuleInfo>,
		IBotTick, IBotRespondToAttack, INotifyActorDisposing
	{
		const int MaxTrackedAttackers = 4;
		const int MaxRespondToAttackCooldown = 30;

		public readonly World World;
		public readonly Player Player;
		public new readonly CNSquadManagerBotModuleInfo Info;

		public readonly List<CNSquad> Squads = [];

		// Units currently managed by a squad
		readonly HashSet<Actor> activeUnits = [];

		// Ticking counters
		int assignRolesTicks;
		int minAttackForceDelayTicks;
		int cleanupTicks;
		int threatScanTicks;
		int defenseMemoryTicks;

		// Tracked enemy capability keys from NeedRules, built once at enable.
		readonly HashSet<string> allTrackedVisibleTags = [];
		readonly HashSet<string> allTrackedGlobalTags = [];
		readonly HashSet<string> allTrackedVisiblePerUnitTags = [];
		readonly HashSet<string> allTrackedGlobalPerUnitTags = [];

		// Pre-allocated scratch sets swapped each scan — no allocations per tick.
		HashSet<string> activeVisibleThreatTags = [];
		HashSet<string> scratchVisibleThreatTags = [];
		HashSet<string> activeGlobalThreatTags = [];
		HashSet<string> scratchGlobalThreatTags = [];

		// Pre-allocated scratch dicts for per-unit count tracking.
		Dictionary<string, int> activeVisibleThreatCounts = [];
		Dictionary<string, int> scratchVisibleThreatCounts = [];
		Dictionary<string, int> activeGlobalThreatCounts = [];
		Dictionary<string, int> scratchGlobalThreatCounts = [];

		// Rolling per-tag performance penalty, <= 0. Dragged down by units lost from squads
		// carrying the tag, recovers back toward 0 over time via PerformanceDecay when a tag's
		// squads stop losing units. Read by GetTagScore, written in PurgeDeadUnits.
		readonly Dictionary<string, float> tagPerformance = [];

		// Per-tick building caches — one Building scan each per world tick, shared across all squads.
		IReadOnlyList<Actor> cachedOwnBuildings = [];
		int cachedOwnBuildingsTick = -1;
		IReadOnlyList<Actor> cachedEnemyBuildings = [];
		int cachedEnemyBuildingsTick = -1;
		IReadOnlyList<Actor> cachedEnemyUnits = [];
		int cachedEnemyUnitsTick = -1;

		// Reactive defense — tracks multiple simultaneous attackers
		readonly List<Actor> recentAttackers = [];
		int respondToAttackCooldown;

		// Nemesis system
		CombatAnalysisBotModule combatAnalysis;
		CNBaseBuilderBotModule baseBuilder;
		CNTacticalMapBotModule tacticalMap;
		CPos initialBaseCenter;

		// Wave manager state
		int waveCooldownTicks;
		int waveCheckTicks;
		int waveGrowthTicks;
		int waveWaitingSinceTick;
		int waveCurrentMinReady;

		// 0 until the wave actually sets off. AttackWaveMaxActiveTicks is a budget for the attack, and
		// starting it at the launch spent most of it on gathering and marching before a shot was fired.
		int waveStartedTick;
		int waveLaunchTick;
		HashSet<CNSquadType> waveEligibleRoleSet = [];
		readonly Dictionary<Actor, int> idleTickCounters = [];

		public bool IsWaveLaunched { get; private set; }
		public Actor WaveTarget { get; private set; }
		public CPos WaveRallyCell { get; private set; }
		public readonly HashSet<CNSquad> WaveParticipants = [];

		public bool IsWaveEligible(CNSquadType type) => waveEligibleRoleSet.Contains(type);

		public CNSquadManagerBotModule(Actor self, CNSquadManagerBotModuleInfo info)
			: base(info)
		{
			Info = info;
			World = self.World;
			Player = self.Owner;
		}

		protected override void TraitDisabled(Actor self)
		{
			foreach (var squad in Squads.ToList())
				UnregisterSquad(squad);
		}

		protected override void TraitEnabled(Actor self)
		{
			var startCell = self.World.Map.AllCells.FirstOrDefault();
			var foundTiles = self.World.Map.FindTilesInCircle(startCell, 1);

			if (foundTiles.Any())
				initialBaseCenter = foundTiles.First();
			else if (startCell != default)
				initialBaseCenter = startCell;
			else
				initialBaseCenter = new CPos(0, 0);

			combatAnalysis = Player.PlayerActor
				.TraitsImplementing<CombatAnalysisBotModule>()
				.FirstOrDefault();
			baseBuilder = Player.PlayerActor
				.TraitsImplementing<CNBaseBuilderBotModule>()
				.FirstOrDefault();
			tacticalMap = Player.PlayerActor
				.TraitsImplementing<CNTacticalMapBotModule>()
				.FirstOrDefault();

			foreach (var (_, rule) in Info.NeedRules)
			{
				foreach (var capability in rule.EnemyCapabilities)
				{
					if (rule.VisibleBonus != 0)
						allTrackedVisibleTags.Add(capability);
					if (rule.GlobalBonus != 0)
						allTrackedGlobalTags.Add(capability);
					if (rule.VisibleWeight != 0)
						allTrackedVisiblePerUnitTags.Add(capability);
					if (rule.GlobalWeight != 0)
						allTrackedGlobalPerUnitTags.Add(capability);
				}
			}

			var random = World.LocalRandom;
			assignRolesTicks = random.Next(0, Info.AssignRolesInterval);
			minAttackForceDelayTicks = random.Next(0, Info.MinimumAttackForceDelay + 1);
			cleanupTicks = random.Next(0, CleanupInterval);

			// Wave system init
			waveEligibleRoleSet = [.. Info.WaveParticipantRoles ?? []];
			waveEligibleRoleSet.Remove(CNSquadType.Protection);

			// The initial delay counts from the start of the game, not from this module enabling. The
			// adaptive bot swaps one profile's squad manager for another mid-game, and re-arming the
			// full delay on every switch meant a profile whose delay exceeds the adaptive hold time
			// (Tech 5000, Turtle 6000 against AdaptiveMinimumIntentHoldTicks 3000) never reached its
			// first wave evaluation at all. The stagger — which exists to keep several bots from
			// attacking on the same tick — only applies while that initial delay is still running;
			// a mid-game switch has no reason to re-stagger against anyone.
			var staggerWindow = Math.Max(1, Info.AttackWaveInterval / 4);
			var remainingInitialDelay = Math.Max(0, Info.AttackWaveInitialDelay - World.WorldTick);
			waveCooldownTicks = remainingInitialDelay > 0
				? remainingInitialDelay + random.Next(0, staggerWindow)
				: 0;
			waveCheckTicks = 0;
			waveCurrentMinReady = Math.Max(1, Info.AttackWaveMinReadySquads);
			waveGrowthTicks = Info.AttackWaveSizeGrowthInterval;
			waveWaitingSinceTick = 0;
			waveStartedTick = 0;
			IsWaveLaunched = false;
			WaveTarget = null;
			WaveParticipants.Clear();
		}

		void IBotTick.BotTick(IBot bot)
		{
			// Its own cadence rather than the threat scan's: that one is gated on tags being tracked at
			// all, and the defence memory has to keep working regardless of how a profile is configured.
			if (Info.EnemyDefenseMemoryInterval > 0 && --defenseMemoryTicks <= 0)
			{
				defenseMemoryTicks = Info.EnemyDefenseMemoryInterval;
				UpdateKnownEnemyDefenses();
			}

			if (Info.ThreatScanInterval > 0 && (allTrackedVisibleTags.Count > 0 || allTrackedGlobalTags.Count > 0 ||
			 allTrackedVisiblePerUnitTags.Count > 0 || allTrackedGlobalPerUnitTags.Count > 0) && --threatScanTicks <= 0)
			{
				threatScanTicks = Info.ThreatScanInterval;
				UpdateThreatTags();
			}

			if (--cleanupTicks <= 0)
			{
				cleanupTicks = CleanupInterval;
				foreach (var squad in Squads)
					RecordLossesAndPurgeDeadUnits(squad);

				DecayTagPerformance();
				CleanSquads();
				activeUnits.RemoveWhere(a => a == null || a.IsDead || !a.IsInWorld);
				foreach (var actor in idleTickCounters.Keys
					.Where(a => a == null || a.IsDead || !a.IsInWorld)
					.ToList())
					idleTickCounters.Remove(actor);
			}

			TrackIdleTime();

			// Per-squad schedule rather than one global timer. A squad holding a live target is updated
			// on the much shorter EngagedSquadInterval, because AttackForceInterval — 55 to 95 ticks,
			// so two to four seconds — is how long a squad in combat used to stand around after its
			// target died before anything re-evaluated it. That delay, not the decision quality, is
			// most of what reads as the bots being slow-witted.
			//
			// Squads without a target stay on the old cadence, which keeps the cost bounded: target
			// searching is the expensive part and idle squads are the majority. Staggering by squad
			// also spreads the work across ticks instead of updating the whole army on one of them.
			foreach (var squad in Squads.ToList())
			{
				if (World.WorldTick < squad.NextUpdateTick)
					continue;

				squad.Update();
				ReleaseStaleNoTargetSquad(squad);

				var interval = squad.IsTargetValid || squad.FuzzyStateMachine.IsInTimeCriticalState
					? Info.EngagedSquadInterval
					: Info.AttackForceInterval;
				squad.NextUpdateTick = World.WorldTick + Math.Max(1, interval);
			}

			if (Info.AttackWaveEnabled)
				TickWaveSystem();

			if (--assignRolesTicks <= 0)
			{
				assignRolesTicks = Info.AssignRolesInterval;
				if (minAttackForceDelayTicks <= 0)
					TryFillTemplates(bot);
			}

			if (minAttackForceDelayTicks > 0)
				minAttackForceDelayTicks--;

			if (respondToAttackCooldown > 0 && respondToAttackCooldown-- == MaxRespondToAttackCooldown)
			{
				recentAttackers.RemoveAll(a => !IsValidAttackResponseTarget(a));
				foreach (var attacker in recentAttackers.ToList())
					ProtectOwn(bot, attacker);
				recentAttackers.Clear();
			}
		}

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (!IsValidAttackResponseTarget(e.Attacker))
				return;

			if (!IsLiveEnemyActor(e.Attacker))
				return;

			// Being shot by an emplacement is contact, and contact is knowledge. The defence memory used
			// to fill only from what the bot could see at the moment of a periodic scan, which in practice
			// was almost nothing: nine rally decisions in a played match ran with zero or one remembered
			// defence, and eight only by the end. A squad does not have to look at the turret shelling it.
			if (e.Attacker.Info.HasTraitInfo<BuildingInfo>() && e.Attacker.Info.HasTraitInfo<AttackBaseInfo>())
				knownEnemyDefenses[e.Attacker.Location] = e.Attacker.Info.Name;

			if (IsProtectedTechBuilding(self))
				ProtectOwn(bot, e.Attacker, self);

			if (!recentAttackers.Contains(e.Attacker))
			{
				if (recentAttackers.Count >= MaxTrackedAttackers)
					recentAttackers.RemoveAt(0);
				recentAttackers.Add(e.Attacker);
			}

			respondToAttackCooldown = MaxRespondToAttackCooldown;

			if (combatAnalysis != null &&
				self.Owner != Player &&
				self.Owner.RelationshipWith(Player) == PlayerRelationship.Ally)
				combatAnalysis.RegisterAllyAttack(e.Attacker.Owner);
		}

		public bool IsNemesis(Player player) =>
			combatAnalysis?.GetNemesis() == player && player != null;

		public CNTacticalMapBotModule GetTacticalMap() => tacticalMap;

		static bool IsValidAttackResponseTarget(Actor attacker)
		{
			return attacker != null &&
				!attacker.IsDead &&
				attacker.IsInWorld &&
				attacker.Owner != null &&
				attacker.OccupiesSpace != null;
		}

		int CleanupInterval => Info.CleanupInterval > 0 ? Info.CleanupInterval : 1;

		bool ShouldRespondToAttack(Actor defended, Actor attacker)
		{
			if (!IsValidAttackResponseTarget(attacker))
				return false;

			var basePos = World.Map.CenterOfCell(GetRandomBaseCenter());
			var maxDefenseRange = WDist.FromCells(Info.DangerScanRadius * 2);
			if ((attacker.CenterPosition - basePos).LengthSquared <=
				(long)maxDefenseRange.Length * maxDefenseRange.Length)
				return true;

			return IsProtectedTechBuilding(defended);
		}

		static bool IsProtectedTechBuilding(Actor actor)
		{
			return actor != null &&
				!actor.IsDead &&
				actor.IsInWorld &&
				actor.Info.HasTraitInfo<BuildingInfo>() &&
				(actor.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet.Contains("Tech") ?? false);
		}

		void ProtectOwn(IBot bot, Actor attacker, Actor defended = null)
		{
			if (!IsValidAttackResponseTarget(attacker))
				return;

			if (!ShouldRespondToAttack(defended, attacker))
				return;

			var idleUnits = World.ActorsHavingTrait<Mobile>()
				.Where(a => a.Owner == Player &&
							!a.IsDead &&
							a.IsInWorld &&
							!activeUnits.Contains(a) &&
							!a.Info.HasTraitInfo<MobSpawnerSlaveInfo>() &&
							CanActorAttackTarget(a, attacker))
				.Take(8)
				.ToList();

			var protectSquad = Squads.FirstOrDefault(s =>
				s.Type == CNSquadType.Protection && s.IsValid && !s.IsTemplateBacked);

			if (protectSquad == null && idleUnits.Count == 0)
				return;

			if (protectSquad != null &&
				!CNSquadHelper.CanSquadEngage(protectSquad, attacker) &&
				idleUnits.Count == 0)
				return;

			if (protectSquad == null)
			{
				protectSquad = RegisterSquad(bot, CNSquadType.Protection);
				InitializeSquadState(protectSquad);
			}

			protectSquad.SetActorToTarget(attacker);

			foreach (var unit in idleUnits)
			{
				protectSquad.Units.Add(unit);
				activeUnits.Add(unit);
			}

			if (idleUnits.Count > 0)
				bot.QueueOrder(new Order("AttackMove", null,
					Target.FromActor(attacker), false,
					groupedActors: idleUnits.ToArray()));

			if (!protectSquad.IsValid)
				UnregisterSquad(protectSquad);
		}

		void TrackIdleTime()
		{
			var seen = new HashSet<Actor>();

			void Track(Actor actor)
			{
				if (actor.Owner != Player || actor.IsDead || !actor.IsInWorld || actor.Info.HasTraitInfo<MobSpawnerSlaveInfo>())
					return;

				seen.Add(actor);
				if (activeUnits.Contains(actor) || actor.CurrentActivity is Enter)
				{
					idleTickCounters.Remove(actor);
					return;
				}

				idleTickCounters[actor] = idleTickCounters.GetValueOrDefault(actor) + 1;
			}

			foreach (var actor in World.ActorsHavingTrait<Mobile>())
				Track(actor);
			foreach (var actor in World.ActorsHavingTrait<Aircraft>())
				Track(actor);

			foreach (var actor in idleTickCounters.Keys.Where(a => !seen.Contains(a)).ToList())
				idleTickCounters.Remove(actor);
		}

		static bool CanActorAttackTarget(Actor actor, Actor target)
		{
			if (actor == null || target == null || actor.IsDead || target.IsDead)
				return false;

			if (!actor.Info.HasTraitInfo<AttackBaseInfo>())
				return false;

			var targetTypes = target.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty)
				return false;

			foreach (var arm in actor.TraitsImplementing<Armament>())
			{
				if (arm.IsTraitDisabled || arm.IsTraitPaused)
					continue;
				if (arm.Weapon.IsValidTarget(targetTypes))
					return true;
			}

			return false;
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			Squads.Clear();
			activeUnits.Clear();
		}

		// ---------------------------------------------------------------------------
		// Demand / reinforcement
		// ---------------------------------------------------------------------------
		public Dictionary<string, int> GetCurrentDemand(Dictionary<string, int> existingByType = null)
		{
			var demand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			foreach (var squad in Squads)
			{
				if (!squad.IsValid || squad.TemplateInfo == null)
					continue;

				// Don't build replacements for operational attack squads — a lone unit
				// crossing the map to join a fight is wasteful and easy to kill.
				// Home-role squads (defense, protection, air support) still get top-ups.
				// Exception: a transport still loading at base keeps recruiting passengers
				// until its carriers are full, so transports no longer leave half-empty.
				if (squad.IsOperational && !squad.AllowsOperationalReinforcement && !AcceptingPassengerTopUp(squad))
					continue;

				foreach (var assignment in squad.SlotAssignments)
				{
					if (assignment.SlotInfo.IsPassenger && !SquadHasLiveCarrier(squad))
						continue;

					var missing = assignment.SlotInfo.IsPassenger ? assignment.MissingToRecruit : assignment.MissingCount;
					if (missing <= 0)
						continue;

					AddPreferredDemand(
						demand,
						assignment.SlotInfo,
						GetUnitDemandScore(squad.TemplateInfo, assignment.SlotInfo, missing, true),
						existingByType);
				}
			}

			var templateCounts = BuildTemplateSquadCounts();
			foreach (var (templateName, template) in OrderedTemplates())
			{
				if (!TemplateAppliesToFaction(template))
					continue;

				var activeCount = templateCounts.GetValueOrDefault(templateName);
				var missingInstances = Math.Max(0, GetEffectiveMaxInstances(template) - activeCount);
				if (missingInstances <= 0)
					continue;

				foreach (var (_, slot) in template.Slots)
				{
					if (slot.IsPassenger)
						continue;

					if (slot.AllowedTypes.Length == 0)
						continue;

					for (var i = 0; i < missingInstances; i++)
					{
						AddPreferredDemand(
							demand,
							slot,
							GetUnitDemandScore(template, slot, slot.Count, false),
							existingByType);
					}
				}
			}

			return demand;
		}

		public int GetEffectiveMaxInstances(CNTeamTemplateInfo template)
		{
			if (template.ScaleWithBuilding == null)
				return template.MaxInstances;

			var buildingCount = GetCachedOwnBuildings().Count(a => a.Info.Name == template.ScaleWithBuilding);
			if (buildingCount <= 0)
				return 0;

			var scaledMax = buildingCount * template.SquadsPerBuilding;
			return Math.Min(template.MaxInstances, scaledMax);
		}

		public int GetTemplateUnitCap(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
				return 0;

			var cap = 0;
			foreach (var (_, template) in OrderedTemplates())
			{
				if (!TemplateAppliesToFaction(template))
					continue;

				foreach (var (_, slot) in template.Slots)
				{
					if (slot.AllowedTypes.Length == 0)
						continue;

					foreach (var allowedType in slot.AllowedTypes)
					{
						if (!string.Equals(allowedType, typeName, StringComparison.OrdinalIgnoreCase))
							continue;

						cap += slot.Count * GetEffectiveMaxInstances(template);
						break;
					}
				}
			}

			return cap;
		}

		HashSet<string> typesIgnoringCashReserve;

		// Unit types that appear in any slot of an IgnoresCashReserve template - cached since the
		// template roster is static config, and this is looked up per candidate unit during production.
		public IReadOnlySet<string> GetTypesIgnoringCashReserve()
		{
			if (typesIgnoringCashReserve != null)
				return typesIgnoringCashReserve;

			typesIgnoringCashReserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (_, template) in OrderedTemplates())
			{
				if (!template.IgnoresCashReserve || !TemplateAppliesToFaction(template))
					continue;

				foreach (var (_, slot) in template.Slots)
					foreach (var allowedType in slot.AllowedTypes)
						typesIgnoringCashReserve.Add(allowedType);
			}

			return typesIgnoringCashReserve;
		}

		static void AddPreferredDemand(
			Dictionary<string, int> demand,
			CNSlotInfo slot,
			int score,
			Dictionary<string, int> existingByType)
		{
			if (score <= 0 || slot.AllowedTypes.Length == 0)
				return;

			var preferredTypes = slot.AllowedTypes;
			if (existingByType != null)
			{
				var bestExisting = int.MaxValue;
				var scarceTypes = new List<string>();

				foreach (var candidate in slot.AllowedTypes)
				{
					var existing = existingByType.GetValueOrDefault(candidate);
					if (existing < bestExisting)
					{
						bestExisting = existing;
						scarceTypes.Clear();
						scarceTypes.Add(candidate);
					}
					else if (existing == bestExisting)
						scarceTypes.Add(candidate);
				}

				if (scarceTypes.Count > 0)
					preferredTypes = scarceTypes.ToArray();
			}

			var baseShare = score / preferredTypes.Length;
			var remainder = score % preferredTypes.Length;

			for (var i = 0; i < preferredTypes.Length; i++)
			{
				var share = baseShare + (i < remainder ? 1 : 0);
				if (share <= 0)
					continue;

				var typeName = preferredTypes[i];
				demand[typeName] = demand.GetValueOrDefault(typeName) + share;
			}
		}

		int GetUnitDemandScore(CNTeamTemplateInfo template, CNSlotInfo slot, int missingCount, bool existingSquad)
		{
			var score = EffectiveScore(template) * 100 + missingCount * 10;

			if (!slot.Optional)
				score += 40;

			if (slot.IsCarrier || slot.IsAircraftCarrier)
				score += 120;
			else if (slot.IsPassenger)
				score += 80;

			if (existingSquad)
			{
				score += 200;

				var slotsFilled = slot.Count - missingCount;
				if (slotsFilled > 0)
					score += slotsFilled * 30;
			}

			return score;
		}

		// ---------------------------------------------------------------------------
		// Template slot-filling
		// ---------------------------------------------------------------------------
		void TryFillTemplates(IBot bot)
		{
			var idleUnits = BuildIdleUnitSet();
			if (idleUnits.Count == 0)
				return;

			var claimedThisPass = new HashSet<Actor>();
			var claimedOwners = new Dictionary<Actor, CNSquad>();
			var availableByType = BuildAvailableUnitsByType(idleUnits, claimedThisPass);

			ReinforceExistingSquads(claimedThisPass, claimedOwners, availableByType);
			RunOffensivePass(bot, claimedThisPass, claimedOwners, availableByType);
			RunIdleSalvagePass(bot, claimedThisPass, claimedOwners, availableByType);

			if (claimedThisPass.Count > 0)
				EvictPoached(claimedOwners);
		}

		void RunOffensivePass(
			IBot bot,
			HashSet<Actor> claimedThisPass,
			Dictionary<Actor, CNSquad> claimedOwners,
			Dictionary<string, List<Actor>> availableByType)
		{
			var exhaustedRoles = new HashSet<CNSquadType>();

			while (HasAvailableUnit(availableByType, claimedThisPass))
			{
				var role = PickNeededRole(exhaustedRoles);
				if (role == null)
					return;

				if (TryCreateBestTemplateForRole(bot, role.Value, claimedThisPass, claimedOwners, availableByType))
					continue;

				exhaustedRoles.Add(role.Value);
			}
		}

		void RunIdleSalvagePass(
			IBot bot,
			HashSet<Actor> claimedThisPass,
			Dictionary<Actor, CNSquad> claimedOwners,
			Dictionary<string, List<Actor>> availableByType)
		{
			if (Info.IdleSalvageTicks <= 0)
				return;

			foreach (var idleUnit in IdleSalvageCandidates(availableByType, claimedThisPass).ToList())
			{
				var candidates = OrderedTemplates()
					.Where(kv => IsAttackRole(kv.Value.Role) &&
						TemplateAppliesToFaction(kv.Value) &&
						TemplateAcceptsUnit(kv.Value, idleUnit) &&
						CountTemplateSquads(kv.Key) < GetEffectiveMaxInstances(kv.Value))
					.OrderByDescending(kv => EffectiveScore(kv.Value))
					.ToList();

				foreach (var (templateName, template) in candidates)
				{
					var trialClaimed = new HashSet<Actor>(claimedThisPass);
					var assignments = TryFillSlots(template, availableByType, trialClaimed);
					if (!CanActivateTemplate(template, assignments))
						continue;

					CreateSquadFromAssignments(bot, templateName, template, assignments, claimedThisPass, claimedOwners);
					break;
				}
			}
		}

		bool TryCreateBestTemplateForRole(
			IBot bot,
			CNSquadType role,
			HashSet<Actor> claimedThisPass,
			Dictionary<Actor, CNSquad> claimedOwners,
			Dictionary<string, List<Actor>> availableByType)
		{
			var candidates = Info.Teams
				.Where(kv => kv.Value.Role == role &&
					TemplateAppliesToFaction(kv.Value) &&
					CountTemplateSquads(kv.Key) < GetEffectiveMaxInstances(kv.Value))
				.ToList();

			foreach (var (templateName, template) in WeightedTemplateOrder(candidates, kv => EffectiveScore(kv.Value)))
			{
				var trialClaimed = new HashSet<Actor>(claimedThisPass);
				var assignments = TryFillSlots(template, availableByType, trialClaimed);
				if (!CanActivateTemplate(template, assignments))
					continue;

				CreateSquadFromAssignments(bot, templateName, template, assignments, claimedThisPass, claimedOwners);
				return true;
			}

			return false;
		}

		void CreateSquadFromAssignments(
			IBot bot,
			string templateName,
			CNTeamTemplateInfo template,
			List<CNSlotAssignment> assignments,
			HashSet<Actor> claimedThisPass,
			Dictionary<Actor, CNSquad> claimedOwners)
		{
			var squad = RegisterSquad(bot, template.Role, templateName, template);
			squad.ArtilleryHangBackRange = WDist.FromCells(Info.ArtilleryHangBackCells);

			foreach (var assignment in assignments)
			{
				squad.SlotAssignments.Add(assignment);
				ApplyAssignmentToSquad(squad, assignment, claimedThisPass, claimedOwners);
			}

			if (template.AttachToRole.Length > 0)
			{
				// Nearest, not first in the list. The artillery idle state only re-picks once its host
				// becomes invalid, so a poor seed here is carried for the rest of the squad's life —
				// an artillery group could end up towed behind a squad on the far side of the map.
				var origin = squad.CenterPosition();
				squad.AttachedTo = Squads
					.Where(s => s != squad && s.IsValid && template.AttachToRole.Contains(s.Type))
					.MinByOrDefault(s => (s.CenterPosition() - origin).LengthSquared);
			}

			foreach (var unit in squad.Units)
				idleTickCounters.Remove(unit);
			foreach (var passenger in squad.PassengerUnits)
				idleTickCounters.Remove(passenger);

			InitializeSquadState(squad);
		}

		static bool CanActivateTemplate(CNTeamTemplateInfo template, List<CNSlotAssignment> assignments)
		{
			if (assignments == null)
				return false;

			var fulfilledCount = assignments.Count(a => a.IsFulfilled && !a.SlotInfo.Optional);
			return fulfilledCount >= template.MinSlotsToActivate;
		}

		CNSquadType? PickNeededRole(HashSet<CNSquadType> exhaustedRoles)
		{
			return PickBestScoredRole(exhaustedRoles);
		}

		CNSquadType? PickBestScoredRole(HashSet<CNSquadType> exhaustedRoles)
		{
			var activeCounts = new Dictionary<CNSquadType, int>();
			foreach (var squad in Squads)
			{
				if (!squad.IsValid || !squad.IsTemplateBacked || !IsAttackRole(squad.Type))
					continue;

				activeCounts[squad.Type] = activeCounts.GetValueOrDefault(squad.Type) + 1;
			}

			CNSquadType? bestRole = null;
			var bestScore = int.MinValue;

			foreach (var (templateName, template) in Info.Teams)
			{
				if (!IsAttackRole(template.Role) || exhaustedRoles.Contains(template.Role))
					continue;
				if (!TemplateAppliesToFaction(template))
					continue;
				if (CountTemplateSquads(templateName) >= GetEffectiveMaxInstances(template))
					continue;

				var score = EffectiveScore(template) - Info.RoleSaturationPenalty * activeCounts.GetValueOrDefault(template.Role);
				if (score <= bestScore)
					continue;

				bestScore = score;
				bestRole = template.Role;
			}

			return bestScore > 0 ? bestRole : null;
		}

		static bool IsAttackRole(CNSquadType role) => role != CNSquadType.Protection;

		static bool HasAvailableUnit(Dictionary<string, List<Actor>> availableByType, HashSet<Actor> claimed)
		{
			foreach (var (_, actors) in availableByType)
				foreach (var actor in actors)
					if (actor != null && !actor.IsDead && actor.IsInWorld && !claimed.Contains(actor))
						return true;

			return false;
		}

		IEnumerable<Actor> IdleSalvageCandidates(Dictionary<string, List<Actor>> availableByType, HashSet<Actor> claimed)
		{
			foreach (var (_, actors) in availableByType)
				foreach (var actor in actors)
				{
					if (actor == null || actor.IsDead || !actor.IsInWorld || claimed.Contains(actor))
						continue;

					if (idleTickCounters.GetValueOrDefault(actor) >= Info.IdleSalvageTicks)
						yield return actor;
				}
		}

		static bool TemplateAcceptsUnit(CNTeamTemplateInfo template, Actor unit)
		{
			foreach (var (_, slot) in template.Slots)
			{
				if (slot.IsPassenger)
					continue;

				foreach (var type in slot.AllowedTypes)
					if (string.Equals(type, unit.Info.Name, StringComparison.OrdinalIgnoreCase))
						return true;
			}

			return false;
		}

		void ReinforceExistingSquads(
			HashSet<Actor> claimedThisPass,
			Dictionary<Actor, CNSquad> claimedOwners,
			Dictionary<string, List<Actor>> availableByType)
		{
			var prioritizedSquads = new List<CNSquad>();
			foreach (var squad in Squads)
			{
				if (!squad.IsValid || squad.TemplateInfo == null)
					continue;

				if (squad.IsOperational && !squad.AllowsOperationalReinforcement && !AcceptingPassengerTopUp(squad))
					continue;

				prioritizedSquads.Add(squad);
			}

			prioritizedSquads.Sort(CompareReinforcementPriority);

			foreach (var squad in prioritizedSquads)
			{
				foreach (var assignment in EnumerateAssignmentsForReinforcement(squad))
				{
					var missing = assignment.SlotInfo.IsPassenger ? assignment.MissingToRecruit : assignment.MissingCount;
					if (missing <= 0)
						continue;

					foreach (var actor in TakeAvailableUnits(assignment.SlotInfo, availableByType, claimedThisPass, missing))
					{
						if (assignment.SlotInfo.IsPassenger)
							assignment.Passengers.Add(actor);
						else
						{
							assignment.Units.Add(actor);
							squad.Units.Add(actor);
						}

						activeUnits.Add(actor);
						claimedOwners[actor] = squad;
						idleTickCounters.Remove(actor);
					}
				}
			}
		}

		List<CNSlotAssignment> TryFillSlots(
			CNTeamTemplateInfo template,
			Dictionary<string, List<Actor>> availableByType,
			HashSet<Actor> alreadyClaimed)
		{
			var assignments = new List<CNSlotAssignment>();
			var localClaimed = new HashSet<Actor>();
			var normalAssignments = new List<CNSlotAssignment>();
			var carrierAssignments = new List<CNSlotAssignment>();
			var passengerAssignments = new List<CNSlotAssignment>();

			foreach (var (_, slotInfo) in template.Slots)
			{
				if (slotInfo.IsPassenger)
				{
					passengerAssignments.Add(FillSlot(slotInfo, availableByType, alreadyClaimed, localClaimed));
					continue;
				}

				if (slotInfo.IsCarrier || slotInfo.IsAircraftCarrier)
				{
					carrierAssignments.Add(FillSlot(slotInfo, availableByType, alreadyClaimed, localClaimed));
					continue;
				}

				normalAssignments.Add(FillSlot(slotInfo, availableByType, alreadyClaimed, localClaimed));
			}

			assignments.AddRange(normalAssignments);
			assignments.AddRange(carrierAssignments);

			var hasCarrier = assignments.Any(a =>
				(a.SlotInfo.IsCarrier || a.SlotInfo.IsAircraftCarrier) && a.Units.Count > 0);

			foreach (var assignment in passengerAssignments)
			{
				if (!hasCarrier)
				{
					assignments.Add(new CNSlotAssignment(assignment.SlotInfo));
					continue;
				}

				assignment.Passengers.AddRange(assignment.Units);
				assignment.Units.Clear();
				assignments.Add(assignment);
			}

			return assignments;
		}

		CNSlotAssignment FillSlot(
			CNSlotInfo slotInfo,
			Dictionary<string, List<Actor>> availableByType,
			HashSet<Actor> alreadyClaimed,
			HashSet<Actor> localClaimed)
		{
			var assignment = new CNSlotAssignment(slotInfo);
			if (slotInfo.Factions.Length > 0 && !slotInfo.Factions.Contains(Player.Faction.InternalName))
				return assignment;

			assignment.Units.AddRange(TakeAvailableUnits(slotInfo, availableByType, alreadyClaimed, slotInfo.Count, localClaimed));

			return assignment;
		}

		IEnumerable<Actor> TakeAvailableUnits(
			CNSlotInfo slotInfo,
			Dictionary<string, List<Actor>> availableByType,
			HashSet<Actor> alreadyClaimed,
			int maxCount,
			HashSet<Actor> localClaimed = null)
		{
			var taken = new List<Actor>();
			var startIndex = slotInfo.AllowedTypes.Length > 1 ? World.LocalRandom.Next(slotInfo.AllowedTypes.Length) : 0;

			for (var offset = 0; offset < slotInfo.AllowedTypes.Length && taken.Count < maxCount; offset++)
			{
				var typeName = slotInfo.AllowedTypes[(startIndex + offset) % slotInfo.AllowedTypes.Length];
				if (!availableByType.TryGetValue(typeName, out var candidates))
					continue;

				foreach (var candidate in candidates)
				{
					if (candidate == null || candidate.IsDead || !candidate.IsInWorld)
						continue;
					if (alreadyClaimed.Contains(candidate) || (localClaimed != null && localClaimed.Contains(candidate)))
						continue;

					taken.Add(candidate);
					alreadyClaimed.Add(candidate);
					localClaimed?.Add(candidate);
					if (taken.Count >= maxCount)
						break;
				}
			}

			return taken;
		}

		static Dictionary<string, List<Actor>> BuildAvailableUnitsByType(IEnumerable<Actor> units, HashSet<Actor> alreadyClaimed)
		{
			var dict = new Dictionary<string, List<Actor>>(StringComparer.OrdinalIgnoreCase);
			foreach (var unit in units)
			{
				if (unit == null || unit.IsDead || !unit.IsInWorld || alreadyClaimed.Contains(unit))
					continue;

				if (!dict.TryGetValue(unit.Info.Name, out var list))
				{
					list = [];
					dict[unit.Info.Name] = list;
				}

				list.Add(unit);
			}

			return dict;
		}

		HashSet<Actor> BuildIdleUnitSet()
		{
			var units = new HashSet<Actor>();
			AddUnclaimedMobileUnits(units);

			foreach (var squad in Squads)
			{
				if (!squad.IsValid || squad.TemplateInfo?.Poachable != true)
					continue;
				if (squad.Type == CNSquadType.Transport || squad.Type == CNSquadType.SubterraneanTransport)
					continue;

				foreach (var actor in squad.Units)
					if (actor != null && !actor.IsDead && actor.IsInWorld)
						units.Add(actor);
			}

			return units;
		}

		bool TemplateAppliesToFaction(CNTeamTemplateInfo template)
		{
			return template.Factions.Length == 0 || template.Factions.Contains(Player.Faction.InternalName);
		}

		IEnumerable<KeyValuePair<string, CNTeamTemplateInfo>> OrderedTemplates() =>
			Info.Teams.OrderByDescending(kv => EffectiveScore(kv.Value));

		public int GetEffectiveScore(CNTeamTemplateInfo template) => EffectiveScore(template);

		// Returns the given templates in weighted-random order. The chance of a template being
		// drawn next is proportional to max(1, weight)^TemplateSelectionSharpness, so production
		// keeps cycling through every template instead of locking onto the single best one.
		// Sharpness 0 = even spread (need ignored), 1-2 = light bias, higher approaches best-first.
		// Uses LocalRandom to match the determinism model of the rest of the bot code.
		public List<T> WeightedTemplateOrder<T>(IReadOnlyList<T> items, Func<T, int> weight)
		{
			var result = new List<T>(items.Count);
			if (items.Count == 0)
				return result;

			var pool = new List<T>(items);
			var weights = new List<double>(pool.Count);
			var k = Math.Max(0, Info.TemplateSelectionSharpness);

			foreach (var item in pool)
				weights.Add(k == 0 ? 1.0 : Math.Pow(Math.Max(1, weight(item)), k));

			while (pool.Count > 0)
			{
				var total = 0.0;
				foreach (var w in weights)
					total += w;

				var roll = World.LocalRandom.NextFloat() * total;
				var idx = pool.Count - 1;
				for (var i = 0; i < pool.Count; i++)
				{
					roll -= weights[i];
					if (roll <= 0)
					{
						idx = i;
						break;
					}
				}

				result.Add(pool[idx]);
				pool.RemoveAt(idx);
				weights.RemoveAt(idx);
			}

			return result;
		}

		public bool IsUnitAssignedToSquad(Actor actor)
		{
			return actor != null && activeUnits.Contains(actor);
		}

		int EffectiveScore(CNTeamTemplateInfo template) => TaggedEffectiveScore(template);

		int TaggedEffectiveScore(CNTeamTemplateInfo template)
		{
			var score = Info.TaggedTemplateBaseScore + template.Bias;

			if (Info.RoleWeights.TryGetValue(template.Role, out var roleWeight))
				score += roleWeight;

			foreach (var tag in template.Tags)
				score += GetTagScore(tag) - Info.TagSaturationPenalty * CountTaggedSquads(tag);

			var repeatPenalty = template.RepeatPenalty >= 0 ? template.RepeatPenalty : Info.RepeatPenalty;
			score -= repeatPenalty * Squads.Count(s => s.IsValid && s.IsTemplateBacked && s.TemplateInfo == template);

			return score;
		}

		int GetTagScore(string tag)
		{
			var score = Info.TagWeights.GetValueOrDefault(tag);
			score += (int)tagPerformance.GetValueOrDefault(tag);

			if (!Info.NeedRules.TryGetValue(tag, out var rule))
				return score;

			var visibleCount = 0;
			var globalCount = 0;
			var hasVisible = false;
			var hasGlobal = false;

			foreach (var capability in rule.EnemyCapabilities)
			{
				if (activeVisibleThreatCounts.TryGetValue(capability, out var vc))
					visibleCount += vc;
				if (activeGlobalThreatCounts.TryGetValue(capability, out var gc))
					globalCount += gc;
				hasVisible |= activeVisibleThreatTags.Contains(capability) || vc > 0;
				hasGlobal |= activeGlobalThreatTags.Contains(capability) || gc > 0;
			}

			var dynamicScore = visibleCount * rule.VisibleWeight + globalCount * rule.GlobalWeight;
			if (hasVisible)
				dynamicScore += rule.VisibleBonus;
			if (hasGlobal)
				dynamicScore += rule.GlobalBonus;

			if (Info.MaxNeedScorePerTag > 0)
				dynamicScore = Math.Clamp(dynamicScore, -Info.MaxNeedScorePerTag, Info.MaxNeedScorePerTag);

			return score + dynamicScore;
		}

		int CountTaggedSquads(string tag) =>
			Squads.Count(s =>
				s.IsValid &&
				s.IsTemplateBacked &&
				s.TemplateInfo?.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) == true);

		void UpdateThreatTags()
		{
			scratchVisibleThreatTags.Clear();
			scratchGlobalThreatTags.Clear();
			scratchVisibleThreatCounts.Clear();
			scratchGlobalThreatCounts.Clear();

			var needVisibility = allTrackedVisibleTags.Count > 0 || allTrackedVisiblePerUnitTags.Count > 0;
			var hasPerUnit = allTrackedVisiblePerUnitTags.Count > 0 || allTrackedGlobalPerUnitTags.Count > 0;

			// When ThreatOnlyFromNemesis is set, restrict to the nemesis player.
			// Fall back to all enemies if no nemesis has been determined yet.
			var nemesis = Info.ThreatOnlyFromNemesis ? combatAnalysis?.GetNemesis() : null;

			foreach (var a in World.ActorsHavingTrait<BotCapabilities>())
			{
				if (!IsLiveEnemyActor(a)) continue;
				if (nemesis != null && a.Owner != nemesis) continue;

				var caps = a.Trait<BotCapabilities>().Info.CapabilitySet;
				var visible = needVisibility && a.CanBeViewedByPlayer(Player);

				foreach (var cap in caps)
				{
					if (visible)
					{
						if (allTrackedVisibleTags.Contains(cap))
							scratchVisibleThreatTags.Add(cap);
						if (allTrackedVisiblePerUnitTags.Contains(cap))
							scratchVisibleThreatCounts[cap] = (scratchVisibleThreatCounts.TryGetValue(cap, out var vc) ? vc : 0) + 1;
					}

					if (allTrackedGlobalTags.Contains(cap))
						scratchGlobalThreatTags.Add(cap);
					if (allTrackedGlobalPerUnitTags.Contains(cap))
						scratchGlobalThreatCounts[cap] = (scratchGlobalThreatCounts.TryGetValue(cap, out var gc) ? gc : 0) + 1;
				}

				// Early exit only when no per-unit counting is needed and all binary tags are found.
				if (!hasPerUnit &&
					scratchVisibleThreatTags.Count == allTrackedVisibleTags.Count &&
					scratchGlobalThreatTags.Count == allTrackedGlobalTags.Count)
					break;
			}

			var changed = !scratchVisibleThreatTags.SetEquals(activeVisibleThreatTags)
				|| !scratchGlobalThreatTags.SetEquals(activeGlobalThreatTags)
				|| !ThreatCountsEqual(scratchVisibleThreatCounts, activeVisibleThreatCounts)
				|| !ThreatCountsEqual(scratchGlobalThreatCounts, activeGlobalThreatCounts);

			if (changed)
			{
				(activeVisibleThreatTags, scratchVisibleThreatTags) = (scratchVisibleThreatTags, activeVisibleThreatTags);
				(activeGlobalThreatTags, scratchGlobalThreatTags) = (scratchGlobalThreatTags, activeGlobalThreatTags);
				(activeVisibleThreatCounts, scratchVisibleThreatCounts) = (scratchVisibleThreatCounts, activeVisibleThreatCounts);
				(activeGlobalThreatCounts, scratchGlobalThreatCounts) = (scratchGlobalThreatCounts, activeGlobalThreatCounts);
			}
		}

		static bool ThreatCountsEqual(Dictionary<string, int> a, Dictionary<string, int> b)
		{
			if (a.Count != b.Count) return false;
			foreach (var (key, val) in a)
				if (!b.TryGetValue(key, out var bVal) || bVal != val)
					return false;
			return true;
		}

		int CountTemplateSquads(string templateName) =>
			Squads.Count(s =>
				s.IsValid &&
				s.IsTemplateBacked &&
				string.Equals(s.TemplateName, templateName, StringComparison.OrdinalIgnoreCase));

		// O(N_squads) one pass — callers that iterate all templates use this instead of calling
		// CountTemplateSquads per template, which would be O(N_templates × N_squads).
		Dictionary<string, int> BuildTemplateSquadCounts()
		{
			var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var squad in Squads)
				if (squad.IsValid && squad.IsTemplateBacked && squad.TemplateName != null)
					counts[squad.TemplateName] = counts.GetValueOrDefault(squad.TemplateName) + 1;
			return counts;
		}

		void ApplyAssignmentToSquad(
			CNSquad squad,
			CNSlotAssignment assignment,
			HashSet<Actor> claimedThisPass,
			Dictionary<Actor, CNSquad> claimedOwners)
		{
			foreach (var unit in assignment.Units)
			{
				squad.Units.Add(unit);
				activeUnits.Add(unit);
				claimedThisPass.Add(unit);
				claimedOwners[unit] = squad;
			}

			foreach (var passenger in assignment.Passengers)
			{
				activeUnits.Add(passenger);
				claimedThisPass.Add(passenger);
				claimedOwners[passenger] = squad;
			}
		}

		void RecordLossesAndPurgeDeadUnits(CNSquad squad)
		{
			// Only true deaths count against a tag's performance - units leaving Units/IsInWorld
			// for other reasons (e.g. boarding a carrier) are not losses.
			if (squad.TemplateInfo != null)
			{
				var deaths = squad.Units.Count(a => a != null && a.IsDead);
				if (deaths > 0)
					foreach (var tag in squad.TemplateInfo.Tags)
						ApplyPerformancePenalty(tag, deaths);
			}

			squad.Units.RemoveWhere(a => a == null || a.IsDead || !a.IsInWorld);
			foreach (var assignment in squad.SlotAssignments)
			{
				assignment.Units.RemoveAll(a => a == null || a.IsDead || !a.IsInWorld);

				// NOTE: passengers are intentionally NOT purged on !IsInWorld. A
				// passenger that has boarded a transport is alive but removed from
				// the world until it unloads. Treating that as dead would empty the
				// squad's passenger list the instant loading completes, making
				// "all aboard" indistinguishable from "no passengers" and stranding
				// loaded transports forever.
				assignment.Passengers.RemoveAll(a => a == null || a.IsDead);
			}

			// The claim was sized against the squad as it was when it picked the target. Now that the
			// dead have been removed, re-price it - otherwise a squad reduced to one survivor keeps
			// reserving the firepower of five and waves everyone else off a target it can no longer kill.
			RefreshTargetClaim(squad);
		}

		void ApplyPerformancePenalty(string tag, int deaths)
		{
			var value = tagPerformance.GetValueOrDefault(tag) - deaths * Info.PerformancePenaltyPerLoss;
			if (Info.MaxPerformancePenaltyPerTag > 0)
				value = Math.Max(value, -Info.MaxPerformancePenaltyPerTag);

			tagPerformance[tag] = value;
			CNBotLog.Debug($"CN AI: Tag '{tag}' lost {deaths} unit(s), performance penalty now {value:0.0}.");
		}

		// Recovers every tracked tag's performance penalty back toward 0 each cleanup pass, so a
		// tag that stops losing units stops being avoided instead of staying penalised forever.
		void DecayTagPerformance()
		{
			if (tagPerformance.Count == 0)
				return;

			foreach (var tag in tagPerformance.Keys.ToList())
			{
				var value = tagPerformance[tag] * Info.PerformanceDecay;
				if (value > -0.5f)
					tagPerformance.Remove(tag);
				else
					tagPerformance[tag] = value;
			}
		}

		void InitializeSquadState(CNSquad squad)
		{
			// Wave-eligible squads park in a hold state near the base until the
			// wave manager launches them as part of a coordinated attack.
			//
			// Squads that form out in the field are exempt. An adaptive profile switch dissolves every
			// squad and re-forms it through here, so without this check units that were mid-attack were
			// handed a hold cell back home and turned around two seconds after the profile changed.
			// The same applies to any squad rebuilt from survivors far from base.
			if (Info.AttackWaveEnabled && waveEligibleRoleSet.Contains(squad.Type) && IsAtHome(squad))
			{
				squad.FuzzyStateMachine.ChangeState(squad, new CNWaveHoldState());
				return;
			}

			InitializeSquadStateForRole(squad);
		}

		/// <summary>
		/// True if the squad's centre sits within WaveHoldHomeRadiusCells of one of our buildings.
		/// A bot with no buildings left is never "at home" — it has nowhere to hold.
		/// </summary>
		bool IsAtHome(CNSquad squad)
		{
			var buildings = GetCachedOwnBuildings();
			if (buildings.Count == 0)
				return false;

			var center = squad.CenterPosition();
			if (center == WPos.Zero)
				return false;

			var radius = WDist.FromCells(Math.Max(1, Info.WaveHoldHomeRadiusCells)).Length;
			var radiusSq = (long)radius * radius;

			foreach (var building in buildings)
				if ((center - building.CenterPosition).LengthSquared <= radiusSq)
					return true;

			return false;
		}

		public void InitializeSquadStateForRole(CNSquad squad)
		{
			switch (squad.Type)
			{
				case CNSquadType.Assault:
				case CNSquadType.Rush:
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
					break;
				case CNSquadType.ArtilleryAssault:
					squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryIdleState());
					break;
				case CNSquadType.Protection:
					squad.FuzzyStateMachine.ChangeState(squad, new ProtectionIdleState());
					break;
				case CNSquadType.SubterraneanAssault:
					squad.FuzzyStateMachine.ChangeState(squad, new SubAssaultIdleState());
					break;
				case CNSquadType.SubterraneanTransport:
					squad.FuzzyStateMachine.ChangeState(squad, new SubTransportIdleState());
					break;
				case CNSquadType.Stealth:
					squad.FuzzyStateMachine.ChangeState(squad, new StealthIdleState());
					break;
				case CNSquadType.Raider:
					squad.FuzzyStateMachine.ChangeState(squad, new RaiderIdleState());
					break;
				case CNSquadType.Support:
					squad.FuzzyStateMachine.ChangeState(squad, new SupportIdleState());
					break;
				case CNSquadType.Transport:
					squad.FuzzyStateMachine.ChangeState(squad, new TransportIdleState());
					break;
				case CNSquadType.AirTransport:
					squad.FuzzyStateMachine.ChangeState(squad, new AirTransportIdleState());
					break;
				case CNSquadType.AircraftAttack:
					squad.FuzzyStateMachine.ChangeState(squad, new AircraftAttackIdleState());
					break;
				case CNSquadType.AircraftRaider:
					squad.FuzzyStateMachine.ChangeState(squad, new AircraftRaiderIdleState());
					break;
				case CNSquadType.Air:
				case CNSquadType.Naval:
				default:
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
					break;
			}
		}

		// ---------------------------------------------------------------------------
		// Wave system
		// ---------------------------------------------------------------------------
		void TickWaveSystem()
		{
			// Growth: ramp up the minimum-ready threshold over time, capped.
			if (Info.AttackWaveSizeGrowthInterval > 0)
			{
				waveGrowthTicks--;
				if (waveGrowthTicks <= 0)
				{
					waveGrowthTicks = Info.AttackWaveSizeGrowthInterval;
					var cap = Math.Max(waveCurrentMinReady, Info.AttackWaveMaxMinReadySquads);
					waveCurrentMinReady = Math.Min(cap, waveCurrentMinReady + Info.AttackWaveSizeGrowthAmount);
				}
			}

			if (IsWaveLaunched)
				MonitorActiveWave();

			if (waveCooldownTicks > 0)
			{
				waveCooldownTicks--;
				return;
			}

			// AttackWaveInterval used to be both the spacing between waves and the rate at which
			// readiness was evaluated, because the cooldown was re-armed after every evaluation
			// whether or not a wave went out. A single evaluation that missed the threshold — by one
			// squad, a second too early — therefore cost a full interval: up to 4800 ticks on Turtle
			// and Expansion. That got worse the higher the threshold grew, which is exactly backwards.
			//
			// Now the evaluation runs on its own short cadence and only a launched wave re-arms the
			// interval, so the spacing between waves is unchanged but "ready but not yet asked"
			// waiting is gone.
			if (--waveCheckTicks > 0)
				return;

			waveCheckTicks = Math.Max(1, Info.AttackWaveCheckInterval);

			if (EvaluateWaveTrigger())
				waveCooldownTicks = Math.Max(1, GetScaledAttackWaveInterval());
		}

		int GetScaledAttackWaveInterval()
		{
			var baseInterval = Info.AttackWaveInterval;
			var milli = baseBuilder?.EconomyOverflowMilli ?? 0;
			if (milli <= 0 || Info.EconomyOverflowWaveIntervalCutPct <= 0)
				return baseInterval;

			var cut = baseInterval * Info.EconomyOverflowWaveIntervalCutPct * milli / (100 * 1000);
			return Math.Max(1, baseInterval - cut);
		}

		int GetScaledWaveMinReadyThreshold()
		{
			var threshold = Math.Max(1, waveCurrentMinReady);
			var milli = baseBuilder?.EconomyOverflowMilli ?? 0;
			if (milli <= 0 || Info.EconomyOverflowWaveMinReadyCutPct <= 0)
				return threshold;

			var cut = threshold * Info.EconomyOverflowWaveMinReadyCutPct * milli / (100 * 1000);
			return Math.Max(1, threshold - cut);
		}

		public double GetAttackFuzzyBoost()
		{
			var milli = baseBuilder?.EconomyOverflowMilli ?? 0;
			if (milli <= 0 || Info.EconomyOverflowFuzzyAttackBoost <= 0.0)
				return 0.0;

			return Info.EconomyOverflowFuzzyAttackBoost * (milli / 1000.0);
		}

		/// <summary>Evaluates wave readiness. Returns true if a wave was actually launched.</summary>
		bool EvaluateWaveTrigger()
		{
			var ready = new List<CNSquad>();
			foreach (var squad in Squads)
			{
				if (!waveEligibleRoleSet.Contains(squad.Type))
					continue;
				if (!squad.IsValid || !squad.IsOperational)
					continue;
				if (!squad.FuzzyStateMachine.IsInState<CNWaveHoldState>())
					continue;
				if (IsWaveLaunched && WaveParticipants.Contains(squad))
					continue;

				ready.Add(squad);
			}

			var threshold = GetScaledWaveMinReadyThreshold();
			if (ready.Count < threshold)
			{
				// The fallback is measured in elapsed time, not in evaluations. It used to count
				// skipped evaluations, which was the same thing only while an evaluation happened
				// exactly once per interval; with the faster cadence a plain counter would fire the
				// fallback almost immediately and no bot would ever assemble a full-size wave.
				if (waveWaitingSinceTick == 0)
					waveWaitingSinceTick = World.WorldTick;

				var fallbackDelay = (long)Math.Max(1, Info.AttackWaveMaxSkipsBeforeFallback) *
					Math.Max(1, GetScaledAttackWaveInterval());

				var fallbackReady =
					World.WorldTick - waveWaitingSinceTick >= fallbackDelay &&
					ready.Count >= Math.Max(1, Info.AttackWaveFallbackMinSquads);

				if (!fallbackReady)
					return false;
			}

			// LaunchWave can still decline (no enemy building to aim at). Only a wave that actually
			// went out clears the wait clock, otherwise a bot that cannot see a target would keep
			// pushing its own fallback deadline back.
			if (!LaunchWave(ready))
				return false;

			waveWaitingSinceTick = 0;
			return true;
		}

		/// <summary>Launches a wave. Returns false if there was nothing to launch or nothing to aim at.</summary>
		bool LaunchWave(IList<CNSquad> participants)
		{
			if (participants == null || participants.Count == 0)
				return false;

			var target = PickWaveTarget();
			if (target == null)
				return false;

			// One wave at a time. This clear was harmless while participation ended at release - the set
			// was empty by then - but now that a wave outlives its launch, starting a second one would
			// silently drop the first wave's squads out of the registry mid-attack: no target advancing,
			// no cohesion, no shared expiry. Reachable wherever AttackWaveInterval is shorter than
			// AttackWaveMaxActiveTicks, which is every rush game once the economy bonus shortens it.
			if (IsWaveLaunched)
				return false;

			// A representative of what is about to march, so "how hot is this approach" is measured
			// against the armour that will actually be standing in it.
			ActorInfo victim = null;
			foreach (var participant in participants)
			{
				victim = participant?.CenterUnit()?.Info;
				if (victim != null)
					break;
			}

			var rally = ComputeRallyCell(target, victim);

			WaveTarget = target;
			WaveRallyCell = rally;
			WaveParticipants.Clear();
			foreach (var s in participants)
				WaveParticipants.Add(s);

			waveLaunchTick = World.WorldTick;
			waveStartedTick = 0;
			IsWaveLaunched = true;
			return true;
		}

		void MonitorActiveWave()
		{
			// Housekeeping first, so it also runs while the wave is still gathering: a wave whose squads
			// all died during staging must end there, and a target that falls before anyone arrives must
			// be replaced rather than ending the wave.
			WaveParticipants.RemoveWhere(s => s == null || !s.IsValid);
			if (WaveParticipants.Count == 0)
			{
				ClearActiveWave();
				return;
			}

			if (!AdvanceWaveTargetIfLost())
				return;

			// Arm the attack clock the moment the first participant leaves staging — that is when the wave
			// stops gathering and starts attacking, and what AttackWaveMaxActiveTicks is meant to budget.
			// Started at the launch instead, most of it went on marching and waiting before a shot was
			// fired; on a large map with a long staging timeout barely a third was left for the attack.
			if (waveStartedTick == 0)
			{
				foreach (var squad in WaveParticipants)
				{
					if (squad.FuzzyStateMachine.IsInAnyState<CNWaveHoldState, CNWaveMoveToRallyState>())
						continue;

					waveStartedTick = World.WorldTick;
					break;
				}
			}

			// A wave that never sets off would otherwise hold the slot forever, since the attack clock
			// never starts. AttackWaveStagingTimeoutTicks bounds the wait of a single squad, not the wave.
			if (waveStartedTick == 0)
			{
				if (World.WorldTick - waveLaunchTick >= Math.Max(1, Info.AttackWaveLaunchGraceTicks))
					ClearActiveWave();

				return;
			}

			var waveExpired = World.WorldTick - waveStartedTick >= Math.Max(1, Info.AttackWaveMaxActiveTicks);
			if (waveExpired)
			{
				foreach (var squad in WaveParticipants.ToList())
				{
					if (squad == null || !squad.IsValid)
						continue;

					// Only squads still staging need releasing. The rest are in their own attack states
					// by now and must not be reset to idle mid-engagement - harmless while the wave ended
					// at release, destructive now that participation outlives it.
					if (!squad.FuzzyStateMachine.IsInAnyState<CNWaveHoldState, CNWaveMoveToRallyState>())
						continue;

					if (WaveTarget != null && !WaveTarget.IsDead && WaveTarget.IsInWorld)
						squad.SetActorToTarget(WaveTarget);

					InitializeSquadStateForRole(squad);
				}

				ClearActiveWave();
			}
		}

		/// <summary>
		/// The wave outlives its first objective: when the target falls, advance to the next one and push
		/// it to every participant, instead of disbanding and letting each squad wander off after whatever
		/// its own scan turns up next. Returns false when there is nothing left worth attacking, in which
		/// case the wave has been cleared and the caller must stop.
		/// </summary>
		bool AdvanceWaveTargetIfLost()
		{
			if (WaveTarget != null && !WaveTarget.IsDead && WaveTarget.IsInWorld)
				return true;

			var nextTarget = PickWaveTarget();
			if (nextTarget == null)
			{
				ClearActiveWave();
				return false;
			}

			WaveTarget = nextTarget;
			foreach (var squad in WaveParticipants)
				if (squad != null && squad.IsValid)
					squad.SetActorToTarget(nextTarget);

			return true;
		}

		/// <summary>
		/// Average position of the wave's living participants, or null while no wave is running.
		/// The reference point squads keep formation against once the wave has been released.
		/// </summary>
		public WPos? WaveCenterPosition()
		{
			if (!IsWaveLaunched || WaveParticipants.Count == 0)
				return null;

			long x = 0, y = 0, z = 0;
			var count = 0;
			foreach (var squad in WaveParticipants)
			{
				if (squad == null || !squad.IsValid)
					continue;

				// Squads that fell under strength are sent home but stay registered, and averaging them
				// in drags the centre back across the map: three squads at the enemy base plus one
				// retreating straggler put the "centre" behind the trio, so all three read as having
				// outrun the wave and turned around to follow a squad that was never coming.
				if (!squad.IsOperational)
					continue;

				var pos = squad.CenterPosition();
				x += pos.X;
				y += pos.Y;
				z += pos.Z;
				count++;
			}

			// A single squad cannot outrun itself, and with one contributor the centre is that squad's own
			// position - every cohesion test against it would compare a squad to itself.
			return count < 2 ? null : new WPos((int)(x / count), (int)(y / count), (int)(z / count));
		}

		/// <summary>
		/// True while this squad has run so far ahead of the rest of the wave that it should wait.
		/// Measured against the wave's centre and only in the direction of the objective, so a squad
		/// that is merely off to one side is left alone.
		/// </summary>
		public bool HasOutrunTheWave(CNSquad squad)
		{
			if (squad == null || Info.WaveCohesionCells <= 0 || !IsWaveLaunched || !WaveParticipants.Contains(squad))
				return false;

			var center = WaveCenterPosition();
			if (center == null || WaveTarget == null || WaveTarget.IsDead || !WaveTarget.IsInWorld)
				return false;

			var squadPos = squad.CenterPosition();
			var allowed = WDist.FromCells(Info.WaveCohesionCells);
			if ((squadPos - center.Value).LengthSquared <= (long)allowed.Length * allowed.Length)
				return false;

			// Ahead means closer to the objective than the wave's centre is. A squad that fell behind is
			// also far from the centre, but telling it to wait would strand it for good.
			var targetPos = WaveTarget.CenterPosition;
			return (squadPos - targetPos).LengthSquared < (center.Value - targetPos).LengthSquared;
		}

		void ClearActiveWave()
		{
			IsWaveLaunched = false;
			WaveTarget = null;
			waveStartedTick = 0;
			WaveParticipants.Clear();
		}

		public Actor PickWaveTarget()
		{
			var ownCenter = GetWaveSourceActor();
			if (ownCenter == null)
				return null;

			var buildings = GetCachedEnemyBuildings();
			if (buildings.Count == 0)
				return null;

			var nemesis = combatAnalysis?.GetNemesis();

			// Prefer high-value structures from the nemesis player; fall back to any enemy building.
			Actor BestOf(IEnumerable<Actor> candidates)
			{
				return candidates
					.OrderByDescending(WaveTargetValueScore)
					.ThenBy(a => (a.CenterPosition - ownCenter.CenterPosition).LengthSquared)
					.FirstOrDefault();
			}

			if (nemesis != null)
			{
				var fromNemesis = BestOf(buildings.Where(b => b.Owner == nemesis));
				if (fromNemesis != null)
					return fromNemesis;
			}

			return BestOf(buildings);
		}

		static int WaveTargetValueScore(Actor a)
		{
			var caps = a.Info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			if (caps == null)
				return 0;

			var score = 0;
			if (caps.Contains("Production")) score += 30;
			if (caps.Contains("Tech")) score += 25;
			if (caps.Contains("Superweapon")) score += 50;
			if (caps.Contains("Economy")) score += 20;
			if (caps.Contains("Power")) score += 10;
			if (caps.Contains("Defense")) score -= 15;
			return score;
		}

		Actor GetWaveSourceActor()
		{
			var buildings = GetCachedOwnBuildings();
			return buildings.Count > 0 ? buildings[World.LocalRandom.Next(buildings.Count)] : null;
		}

		CPos ComputeRallyCell(Actor target, ActorInfo victim)
		{
			var ownCell = GetRandomBaseCenter();
			var ownPos = World.Map.CenterOfCell(ownCell);
			var enemyPos = target.CenterPosition;

			var delta = ownPos - enemyPos;
			var deltaLen = delta.Length;

			// Degenerate case (own base ≈ target). Fall back to enemy cell.
			if (deltaLen < 1024)
				return World.Map.CellContaining(enemyPos);

			// How far short of the target the wave gathers. Taking a share of the run rather than a
			// fixed number of cells is what makes this map-independent: a flat offset meant "half the
			// way there" on a small map and "just outside their defences" on a large one, which is why
			// raising it twice only moved the problem to the next map.
			var progress = Math.Clamp(Info.AttackWaveStagingProgressPercent, 0, 100);
			var backoff = (int)((long)deltaLen * (100 - progress) / 100);

			// The floor only bites on short runs, where the percentage alone would put the wave inside
			// the enemy's defences. Capped at the full distance: keeping more distance from the target
			// than we have to give would push the rally point past our own base.
			backoff = Math.Max(backoff, WDist.FromCells(Math.Max(0, Info.AttackWaveStagingOffsetCells)).Length);
			backoff = Math.Min(backoff, deltaLen);

			// rally = enemy + backoff * (own - enemy) / |own - enemy|
			var rallyX = enemyPos.X + (int)((long)delta.X * backoff / deltaLen);
			var rallyY = enemyPos.Y + (int)((long)delta.Y * backoff / deltaLen);
			var rallyZ = enemyPos.Z + (int)((long)delta.Z * backoff / deltaLen);
			var rallyPos = new WPos(rallyX, rallyY, rallyZ);

			var cell = World.Map.CellContaining(rallyPos);

			// If outside map bounds, snake along the line back toward own base until we hit a map cell.
			if (!World.Map.Contains(cell))
			{
				var step = new CVec(Math.Sign(ownCell.X - cell.X), Math.Sign(ownCell.Y - cell.Y));
				for (var i = 0; i < 8 && !World.Map.Contains(cell); i++)
					cell = new CPos(cell.X + step.X, cell.Y + step.Y);

				if (!World.Map.Contains(cell))
					cell = new CPos((ownCell.X + World.Map.CellContaining(enemyPos).X) / 2,
						(ownCell.Y + World.Map.CellContaining(enemyPos).Y) / 2);
			}

			// Everything above stages the wave on the straight line from base to target, which is how
			// attacks kept forming up in front of whatever fortification happened to sit on that line.
			// If the topology scan knows a way in that is under fewer guns, gather there instead.
			// Measured along the final leg, from where the squads gather to what they are attacking —
			// the stretch they actually have to cross. Comparing the two staging cells instead compared
			// two points that are cold by construction.
			var approach = PickApproachCell(target, victim);
			var usable = approach != null && World.Map.Contains(approach.Value);
			var approachThreat = usable
				? GetDefenseThreatAlong(World.Map.CenterOfCell(approach.Value), target.CenterPosition, victim)
				: -1;
			var directThreat = GetDefenseThreatAlong(World.Map.CenterOfCell(cell), target.CenterPosition, victim);

			// Logged unconditionally, and with more than the two staging cells, because the first version
			// of this line could not distinguish the three ways it fails: no candidate found at all, an
			// empty defence memory, or - the current suspicion - measuring in the wrong place. The rally
			// sits AttackWaveStagingProgressPercent short of the target, which is deliberately outside
			// defensive range, so both staging cells read zero however much the bot knows. The threat at
			// the target is what says whether the memory has anything in it.
			CNBotLog.Debug(
				"{0} wave rally: direct {1} (threat {2}) vs approach {3} (threat {4}); target threat {5}, defences known {6}",
				Player, cell, directThreat,
				usable ? approach.Value.ToString() : "none", approachThreat,
				GetDefenseThreatAt(target.CenterPosition, victim), knownEnemyDefenses.Count);

			if (usable && approachThreat < directThreat)
				return approach.Value;

			return cell;
		}

		public CNSquad RegisterSquad(
			IBot bot,
			CNSquadType type,
			string templateName = null,
			CNTeamTemplateInfo templateInfo = null)
		{
			var squad = new CNSquad(bot, this, type, templateName, templateInfo)
			{
				ArtilleryHangBackRange = WDist.FromCells(Info.ArtilleryHangBackCells),

				// Stagger the first update so a batch of squads formed on the same tick does not
				// then update in lockstep for the rest of their lives.
				NextUpdateTick = World.WorldTick + World.LocalRandom.Next(0, Math.Max(1, Info.AttackForceInterval))
			};

			Squads.Add(squad);
			return squad;
		}

		public void UnregisterSquad(CNSquad squad)
		{
			// Before anything else: a dead squad must stop reserving its target, or the damage it can no
			// longer deal would keep every other squad away from it for the rest of the match.
			ClearTargetClaim(squad);

			// Same principle, and the reason this belongs here rather than at any of the fifteen call
			// sites: a dissolved squad keeps its Units set, so IsValid stays true and the wave's only
			// purge — RemoveWhere(!IsValid) — never sees it. The entry would point at a squad that no
			// longer exists until the last of its former units happened to die, and while the wave holds
			// at least one such entry it never reaches zero participants and never clears. With one wave
			// allowed at a time, that means no further wave for the rest of the timer.
			WaveParticipants.Remove(squad);

			Squads.Remove(squad);
			foreach (var unit in squad.Units)
				activeUnits.Remove(unit);
			foreach (var assignment in squad.SlotAssignments)
			{
				foreach (var actor in assignment.Units)
					activeUnits.Remove(actor);
				foreach (var passenger in assignment.Passengers)
					activeUnits.Remove(passenger);
			}
		}

		void CleanSquads()
		{
			for (var i = Squads.Count - 1; i >= 0; i--)
				if (!Squads[i].IsValid)
					UnregisterSquad(Squads[i]);
		}

		void ReleaseStaleNoTargetSquad(CNSquad squad)
		{
			if (squad == null || !Squads.Contains(squad) || !IsReleasableNoTargetSquad(squad))
				return;

			if (!IsNoTargetIdleState(squad) || squad.IsTargetValid || HasBusyOrderableUnits(squad))
			{
				squad.NoTargetIdleTicks = 0;
				return;
			}

			// Actual elapsed time rather than the assumed interval — with a per-squad cadence the
			// gap between updates is no longer always AttackForceInterval.
			squad.NoTargetIdleTicks += squad.TicksSinceLastUpdate;
			if (squad.NoTargetIdleTicks >= Math.Max(1, Info.NoTargetIdleReleaseTicks))
				UnregisterSquad(squad);
		}

		static bool IsReleasableNoTargetSquad(CNSquad squad)
		{
			if (!squad.IsValid || !squad.IsTemplateBacked)
				return false;

			switch (squad.Type)
			{
				case CNSquadType.Assault:
				case CNSquadType.Rush:
				case CNSquadType.Raider:
				case CNSquadType.Stealth:
				case CNSquadType.SubterraneanAssault:
				case CNSquadType.SubterraneanTransport:
				case CNSquadType.ArtilleryAssault:
				case CNSquadType.AircraftAttack:
				case CNSquadType.AircraftRaider:
				case CNSquadType.Transport:
				case CNSquadType.AirTransport:
					return true;
				default:
					return false;
			}
		}

		static bool IsNoTargetIdleState(CNSquad squad)
		{
			var isAttackIdle = squad.FuzzyStateMachine.IsInState<CNGroundIdleState>() ||
				squad.FuzzyStateMachine.IsInState<RaiderIdleState>() ||
				squad.FuzzyStateMachine.IsInState<StealthIdleState>() ||
				squad.FuzzyStateMachine.IsInState<SubAssaultIdleState>() ||
				squad.FuzzyStateMachine.IsInState<ArtilleryIdleState>() ||
				squad.FuzzyStateMachine.IsInState<AircraftAttackIdleState>() ||
				squad.FuzzyStateMachine.IsInState<AircraftRaiderIdleState>();

			if (isAttackIdle)
				return true;

			var isTransportIdle = squad.FuzzyStateMachine.IsInState<TransportIdleState>() ||
				squad.FuzzyStateMachine.IsInState<AirTransportIdleState>() ||
				squad.FuzzyStateMachine.IsInState<SubTransportIdleState>();

			if (!isTransportIdle)
				return false;

			// Transport already carrying — actively working, not idle.
			if (HasCargo(squad))
				return false;

			// Carrier alive but cargo empty: squad is waiting for passengers to walk over and board.
			// Releasing here destroys the (often expensive) carrier reservation, which is why
			// AirTransport squads in particular were chronically passive.
			return !HasLiveCarrier(squad);
		}

		static bool HasLiveCarrier(CNSquad squad)
		{
			foreach (var carrier in squad.CarrierUnits)
				if (!carrier.IsDead && carrier.IsInWorld)
					return true;

			return false;
		}

		static bool HasBusyOrderableUnits(CNSquad squad)
		{
			foreach (var unit in squad.OrderableUnits)
				if (!unit.IsIdle)
					return true;

			return false;
		}

		static bool HasCargo(CNSquad squad)
		{
			foreach (var carrier in squad.CarrierUnits)
			{
				var cargo = carrier.TraitOrDefault<Cargo>();
				if (cargo != null && !cargo.IsEmpty())
					return true;
			}

			return false;
		}

		// ---------------------------------------------------------------------------
		// Target claims
		//
		// Squads used to pick targets in complete ignorance of each other: every one of them
		// scanned from its own leader and took whatever was closest. Five squads would converge
		// on one harvester while a war factory stood untouched, and ten aircraft would keep
		// bombing a building three of them had already killed.
		//
		// The registry below is the shared bookkeeping that fixes both: each squad records how
		// much damage it has committed to its target, and a target that already has enough
		// damage committed stops attracting further squads.
		// ---------------------------------------------------------------------------
		readonly Dictionary<CNSquad, (Actor Target, int Damage, int Suitability)> targetClaims = [];
		readonly Dictionary<Actor, int> committedDamage = [];
		readonly Dictionary<Actor, int> bestClaimSuitability = [];
		readonly Dictionary<(string Attacker, string Target), int> damagePerSalvoCache = [];

		/// <summary>
		/// Rough damage one actor of <paramref name="attacker"/>'s type lands on one actor of
		/// <paramref name="target"/>'s type per salvo.
		/// <para>
		/// Deliberately an estimate built from static rules rather than a simulation: it sums every
		/// damage warhead of every armament, applies the Versus percentages for the target's armor
		/// types, and multiplies by burst. It ignores range, reload, accuracy, damage modifiers and
		/// conditionally disabled armor — none of which change the answer to "is this target already
		/// covered" enough to be worth the cost. Cached per actor-type pair, so the reflection-ish
		/// walk happens once per pairing and never again.
		/// </para>
		/// </summary>
		public int EstimateDamagePerSalvo(ActorInfo attacker, ActorInfo target)
		{
			if (attacker == null || target == null)
				return 0;

			var key = (attacker.Name, target.Name);
			if (damagePerSalvoCache.TryGetValue(key, out var cached))
				return cached;

			var targetTypes = target.GetAllTargetTypes();

			var total = 0;
			foreach (var armament in attacker.TraitInfos<ArmamentInfo>())
			{
				var weapon = armament.WeaponInfo;
				if (weapon == null)
					continue;

				// Only weapons that can actually be fired at this target count. Without this the Nod
				// helicopter booked its air-to-air launcher against buildings, and every ground armament
				// counted against aircraft - inflating the claim and waving other squads off a target
				// the attacker cannot in fact hurt that hard.
				if (!weapon.IsValidTarget(targetTypes))
					continue;

				var perShot = 0;
				foreach (var warhead in weapon.Warheads)
				{
					if (warhead is not DamageWarhead damageWarhead || damageWarhead.Damage <= 0)
						continue;

					// Warhead.IsValidTarget is protected, so the same test is spelled out here against the
					// public fields: a warhead counts only if the target's types overlap ValidTargets and
					// are not overruled by InvalidTargets (e.g. Hellfire declares InvalidTargets: Infantry).
					if (!damageWarhead.ValidTargets.Overlaps(targetTypes) || damageWarhead.InvalidTargets.Overlaps(targetTypes))
						continue;

					perShot += Util.ApplyPercentageModifiers(damageWarhead.Damage, [EstimateVersus(damageWarhead, target)]);
				}

				total += perShot * Math.Max(1, weapon.Burst);
			}

			damagePerSalvoCache[key] = total;
			return total;
		}

		// Mirrors DamageWarhead.DamageVersus, but off ActorInfo instead of a live victim and hit shape:
		// every armor type the target declares that the warhead has a Versus entry for is applied as a
		// percentage modifier, exactly as the engine does it.
		static int EstimateVersus(DamageWarhead warhead, ActorInfo target)
		{
			if (warhead.Versus.Count == 0)
				return 100;

			var modifiers = new List<int>();
			foreach (var armor in target.TraitInfos<ArmorInfo>())
				if (armor.Type != null && warhead.Versus.TryGetValue(armor.Type, out var versus))
					modifiers.Add(versus);

			return modifiers.Count == 0 ? 100 : Util.ApplyPercentageModifiers(100, modifiers);
		}

		/// <summary>Damage the squad's living units land on the target in one salvo.</summary>
		public int EstimateSquadDamage(CNSquad squad, Actor target)
		{
			if (squad == null || target == null || target.IsDead || !target.IsInWorld)
				return 0;

			var total = 0;
			foreach (var unit in squad.OrderableUnits)
				total += EstimateDamagePerSalvo(unit.Info, target.Info);

			return total;
		}

		/// <summary>
		/// Records that <paramref name="squad"/> is committing to <paramref name="target"/>, replacing
		/// any claim it held before. Called from CNSquad.SetActorToTarget, the single funnel through
		/// which every squad target assignment passes.
		/// </summary>
		public void SetTargetClaim(CNSquad squad, Actor target)
		{
			if (squad == null)
				return;

			ClearTargetClaim(squad);

			if (target == null || target.IsDead || !target.IsInWorld)
				return;

			var damage = EstimateSquadDamage(squad, target);
			var suitability = (int)(CNSquadHelper.CounterFraction(squad, target) * 100);
			targetClaims[squad] = (target, damage, suitability);
			committedDamage[target] = committedDamage.GetValueOrDefault(target) + damage;
			bestClaimSuitability[target] = Math.Max(bestClaimSuitability.GetValueOrDefault(target), suitability);
		}

		/// <summary>Re-prices an existing claim after the squad's strength changed.</summary>
		public void RefreshTargetClaim(CNSquad squad)
		{
			if (squad != null && targetClaims.TryGetValue(squad, out var claim))
				SetTargetClaim(squad, claim.Target);
		}

		public void ClearTargetClaim(CNSquad squad)
		{
			if (squad == null || !targetClaims.TryGetValue(squad, out var claim))
				return;

			targetClaims.Remove(squad);
			if (claim.Target == null)
				return;

			var remaining = committedDamage.GetValueOrDefault(claim.Target) - claim.Damage;
			if (remaining > 0)
				committedDamage[claim.Target] = remaining;
			else
				committedDamage.Remove(claim.Target);

			// Recompute the best suitability from the remaining claimants. Only runs when a claim
			// changes, not per candidate during a scan, so the walk is affordable here.
			var best = 0;
			foreach (var (_, other) in targetClaims)
				if (other.Target == claim.Target && other.Suitability > best)
					best = other.Suitability;

			if (best > 0)
				bestClaimSuitability[claim.Target] = best;
			else
				bestClaimSuitability.Remove(claim.Target);
		}

		/// <summary>
		/// True if another squad that counters this target markedly better has already claimed it, so
		/// <paramref name="squad"/> should leave it to them and take something it fights better.
		/// <para>
		/// This is what turns the per-squad CounterFraction bonus into an actual division of labour:
		/// without it, an anti-armor group and an anti-infantry group both simply prefer what suits
		/// them, and nothing stops the anti-armor group taking the infantry when it happens to be the
		/// closer target. The margin means a squad only defers to a clearly better answer, not to a
		/// marginally better one.
		/// </para>
		/// </summary>
		public bool IsTargetBetterServed(CNSquad squad, Actor target, double ownSuitability)
		{
			if (!Info.TargetClaimingEnabled || Info.CounterDeferenceMargin <= 0)
				return false;
			if (target == null || target.IsDead || !target.IsInWorld)
				return false;

			// Already ours: keep it. Deferring here would make a squad talk itself off the target it is
			// currently attacking, and the best-suitability figure below includes its own claim anyway.
			if (targetClaims.TryGetValue(squad, out var own) && own.Target == target)
				return false;

			var best = bestClaimSuitability.GetValueOrDefault(target);
			return best - (int)(ownSuitability * 100) >= Info.CounterDeferenceMargin;
		}

		/// <summary>
		/// True if other squads have already committed enough damage to finish this target, so
		/// <paramref name="squad"/> should look elsewhere. The squad's own claim is excluded, or a squad
		/// would talk itself out of the target it is already attacking.
		/// </summary>
		public bool IsTargetOversubscribed(CNSquad squad, Actor target)
		{
			if (!Info.TargetClaimingEnabled || target == null || target.IsDead || !target.IsInWorld)
				return false;

			var committed = committedDamage.GetValueOrDefault(target);
			if (committed <= 0)
				return false;

			if (squad != null && targetClaims.TryGetValue(squad, out var own) && own.Target == target)
				committed -= own.Damage;

			if (committed <= 0)
				return false;

			// IHealth, not Health: this mod's actors carry CNHealth, which implements IHealth rather than
			// deriving from Health. TraitOrDefault<Health>() therefore returned null for every actor in
			// the game and switched the whole overkill check off. CNStateBase queries IHealth throughout.
			var health = target.TraitOrDefault<IHealth>();
			if (health == null)
				return false;

			var factor = target.Info.HasTraitInfo<BuildingInfo>()
				? Info.OverkillFactorBuilding
				: Info.OverkillFactorMobile;

			return committed >= health.HP * Math.Max(100, factor) / 100;
		}

		// ---------------------------------------------------------------------------
		// Defended approaches
		//
		// The topology scan has always known where the ways in are - CNTacticalMapBotModule maps
		// chokepoints, ramps and bridges - but only defence placement ever asked. Attacks marched
		// at whatever the straight line from base to target ran into, which meant walking into the
		// same fortified front over and over while an undefended flank stood open.
		// ---------------------------------------------------------------------------
		readonly Dictionary<string, int> maxWeaponRangeCache = [];

		// What the bot has actually seen of the enemy's defences, kept under fog until it looks again.
		//
		// A plain visibility check would be worse than knowing everything: a squad approaches, sees the
		// turret, backs off, loses sight, forgets, and walks straight back in. Remembering closes that
		// loop and is also the honest model — every other target decision in this bot already refuses to
		// act on what it has not seen, and this one was the odd exception.
		readonly Dictionary<CPos, string> knownEnemyDefenses = [];

		/// <summary>
		/// How many enemy emplacements the bot has seen or been shot by. A measure of how dug in the
		/// opponent is, learned rather than read off the map, and the only thing the strategy layer has
		/// ever known about what the enemy is actually doing.
		/// </summary>
		public int KnownEnemyDefenseCount => knownEnemyDefenses.Count;

		/// <summary>
		/// Refreshes the remembered defences from what is visible right now: anything in sight is
		/// recorded, and anything remembered on a cell the bot can currently see, but which is no longer
		/// there, is forgotten. Cells under fog keep whatever was last seen.
		/// </summary>
		void UpdateKnownEnemyDefenses()
		{
			foreach (var building in GetCachedEnemyBuildings())
			{
				if (building.IsDead || !building.IsInWorld || !building.Info.HasTraitInfo<AttackBaseInfo>())
					continue;

				if (building.CanBeViewedByPlayer(Player))
					knownEnemyDefenses[building.Location] = building.Info.Name;
			}

			scratchForgottenDefenses.Clear();
			foreach (var (cell, _) in knownEnemyDefenses)
			{
				if (!Player.Shroud.IsVisible(cell))
					continue;

				var stillThere = false;
				foreach (var actor in World.ActorMap.GetActorsAt(cell))
				{
					if (actor.Owner == Player || !actor.Info.HasTraitInfo<AttackBaseInfo>() || !actor.Info.HasTraitInfo<BuildingInfo>())
						continue;

					stillThere = true;
					break;
				}

				if (!stillThere)
					scratchForgottenDefenses.Add(cell);
			}

			foreach (var cell in scratchForgottenDefenses)
				knownEnemyDefenses.Remove(cell);
		}

		readonly List<CPos> scratchForgottenDefenses = [];

		/// <summary>Longest weapon range this actor type can bring to bear.</summary>
		public int MaxWeaponRange(ActorInfo info)
		{
			if (info == null)
				return 0;

			if (maxWeaponRangeCache.TryGetValue(info.Name, out var cached))
				return cached;

			var range = 0;
			foreach (var armament in info.TraitInfos<ArmamentInfo>())
			{
				var weapon = armament.WeaponInfo;
				if (weapon != null && weapon.Range.Length > range)
					range = weapon.Range.Length;
			}

			maxWeaponRangeCache[info.Name] = range;
			return range;
		}

		/// <summary>
		/// Damage per salvo that enemy static defences covering <paramref name="pos"/> would put on a
		/// unit of <paramref name="victim"/>'s type — the measure of how hot an approach is.
		/// Returns 0 for a null victim, so callers without a representative unit simply express no
		/// preference rather than being steered by a meaningless number.
		/// </summary>
		public int GetDefenseThreatAt(WPos pos, ActorInfo victim)
		{
			if (victim == null)
				return 0;

			var total = 0;
			foreach (var (cell, typeName) in knownEnemyDefenses)
			{
				var info = World.Map.Rules.Actors.GetValueOrDefault(typeName);
				if (info == null)
					continue;

				var range = MaxWeaponRange(info);
				if (range <= 0)
					continue;

				if ((World.Map.CenterOfCell(cell) - pos).LengthSquared > (long)range * range)
					continue;

				total += EstimateDamagePerSalvo(info, victim);
			}

			return total;
		}

		/// <summary>
		/// Total defensive fire a unit would be exposed to walking from <paramref name="from"/> to
		/// <paramref name="to"/>, sampled along the way.
		/// <para>
		/// Point samples cannot answer this, and measuring at the endpoints answered nothing at all.
		/// Emplacements in this mod reach seven to ten cells; they cover approaches, not the buildings
		/// behind them. The staging point sits deliberately short of the target and the target sits
		/// inside the base, so both read zero however much the bot knows — which is exactly what a
		/// played match showed, nine rally decisions comparing zero against zero. The fire is on the
		/// stretch between them, and a gauntlet has to be integrated, not sampled at its ends.
		/// </para>
		/// Overlapping samples are intentional: a turret covering several consecutive samples counts
		/// several times, because the squad really is under its guns for that much longer.
		/// </summary>
		public int GetDefenseThreatAlong(WPos from, WPos to, ActorInfo victim)
		{
			if (victim == null)
				return 0;

			var samples = Math.Max(1, Info.ApproachThreatSamples);
			var total = 0;
			for (var i = 1; i <= samples; i++)
			{
				var point = new WPos(
					from.X + (int)((long)(to.X - from.X) * i / samples),
					from.Y + (int)((long)(to.Y - from.Y) * i / samples),
					from.Z + (int)((long)(to.Z - from.Z) * i / samples));

				total += GetDefenseThreatAt(point, victim);
			}

			return total;
		}

		/// <summary>
		/// The most lightly defended chokepoint within reach of <paramref name="target"/>, or null when
		/// the tactical map has nothing to offer — in which case the caller stays on the direct line.
		/// </summary>
		public CPos? PickApproachCell(Actor target, ActorInfo victim)
		{
			if (!Info.AvoidDefendedApproaches || target == null || victim == null || tacticalMap == null)
				return null;

			// The useful set, not the raw one: these are the chokepoints reachable from this bot's own
			// base that lead somewhere real. The raw list includes dead ends and pockets across
			// impassable terrain, and staging at one of those is how the first version produced rally
			// points the squads could not sensibly walk to.
			var chokepoints = tacticalMap.GetUsefulChokepointsForOwnBase();
			if (chokepoints.Count == 0)
				return null;

			var basePos = World.Map.CenterOfCell(GetRandomBaseCenter());
			var directLength = (target.CenterPosition - basePos).Length;
			if (directLength <= 0)
				return null;

			var reach = WDist.FromCells(Math.Max(1, Info.ApproachSearchRadiusCells));
			var reachSq = (long)reach.Length * reach.Length;
			var maxRouteLength = directLength + directLength * Math.Max(0, Info.ApproachMaxDetourPercent) / 100;

			CPos? best = null;
			var bestThreat = int.MaxValue;
			foreach (var chokepoint in chokepoints)
			{
				var pos = World.Map.CenterOfCell(chokepoint.Cell);
				if ((pos - target.CenterPosition).LengthSquared > reachSq)
					continue;

				// The way in has to lie between us and the objective. Picking purely by temperature let
				// the coldest chokepoint win even when it sat on the far side of the enemy base, so the
				// squads marched past the target to gather behind it.
				if ((pos - basePos).Length + (target.CenterPosition - pos).Length > maxRouteLength)
					continue;

				var threat = GetDefenseThreatAt(pos, victim);
				if (threat >= bestThreat)
					continue;

				bestThreat = threat;
				best = chokepoint.Cell;
			}

			if (best == null)
				return null;

			// Gather short of the passage rather than inside it. A chokepoint is by definition narrow;
			// assembling a squad in one bunches it up exactly where it is easiest to shell.
			return StandOffCell(best.Value, basePos, Info.ApproachStandOffCells);
		}

		/// <summary>Steps back from <paramref name="cell"/> toward <paramref name="towards"/>.</summary>
		CPos StandOffCell(CPos cell, WPos towards, int cells)
		{
			if (cells <= 0)
				return cell;

			var pos = World.Map.CenterOfCell(cell);
			var delta = towards - pos;
			var length = delta.Length;
			if (length <= 0)
				return cell;

			var step = WDist.FromCells(cells).Length;
			if (step >= length)
				return cell;

			var backed = new WPos(
				pos.X + (int)((long)delta.X * step / length),
				pos.Y + (int)((long)delta.Y * step / length),
				pos.Z + (int)((long)delta.Z * step / length));

			var backedCell = World.Map.CellContaining(backed);
			return World.Map.Contains(backedCell) ? backedCell : cell;
		}

		/// <summary>
		/// True while enemy static defences can still reach this squad. A gun emplacement never chases,
		/// so a retreat that waits only for a pursuer to give up ends the moment the squad steps outside
		/// DangerScanRadius — whereupon it marches back into exactly the same guns.
		/// </summary>
		public bool IsUnderDefensiveFire(CNSquad squad)
		{
			var leader = squad?.CenterUnit();
			return leader != null && GetDefenseThreatAt(leader.CenterPosition, leader.Info) > 0;
		}

		/// <summary>
		/// True if the squad puts out more damage per salvo against the nearest defence firing on it
		/// than all covering defences put out against the squad — the case for pressing the attack home
		/// rather than trickling units out of range one at a time.
		/// </summary>
		public bool OutTradesDefenses(CNSquad squad)
		{
			var leader = squad?.CenterUnit();
			if (leader == null)
				return false;

			var incoming = GetDefenseThreatAt(leader.CenterPosition, leader.Info);
			if (incoming <= 0)
				return false;

			Actor nearest = null;
			var nearestDistSq = long.MaxValue;
			foreach (var building in GetCachedEnemyBuildings())
			{
				if (building.IsDead || !building.IsInWorld || !building.Info.HasTraitInfo<AttackBaseInfo>())
					continue;

				var range = MaxWeaponRange(building.Info);
				if (range <= 0)
					continue;

				var distSq = (building.CenterPosition - leader.CenterPosition).LengthSquared;
				if (distSq > (long)range * range || distSq >= nearestDistSq)
					continue;

				nearestDistSq = distSq;
				nearest = building;
			}

			// Measured against one defence rather than all of them because the squad focuses its fire:
			// what matters is whether it can kill what it is shooting at faster than the position kills it.
			return nearest != null && EstimateSquadDamage(squad, nearest) > incoming;
		}

		public Actor FindClosestEnemy(Actor sourceActor, Func<Actor, bool> additionalFilter = null)
		{
			if (sourceActor == null)
				return null;

			var nemesis = combatAnalysis?.GetNemesis();
			if (nemesis != null)
			{
				var nemesisTarget = FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius), nemesis, additionalFilter)
					?? FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius * 4), nemesis, additionalFilter);
				if (nemesisTarget != null)
					return nemesisTarget;
			}

			return FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius), additionalFilter)
				?? FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius * 4), additionalFilter);
		}

		public Actor FindClosestEnemy(Actor sourceActor, WDist radius, Func<Actor, bool> additionalFilter = null)
		{
			if (sourceActor == null)
				return null;

			return World.FindActorsInCircle(sourceActor.CenterPosition, radius)
				.Where(a => IsPreferredEnemyUnit(a) &&
							a.CanBeViewedByPlayer(Player) &&
							!a.Info.HasTraitInfo<LineBuildInfo>() &&
							(additionalFilter == null || additionalFilter(a)))
				.MinByOrDefault(a => (a.CenterPosition - sourceActor.CenterPosition).LengthSquared);
		}

		Actor FindClosestEnemy(Actor sourceActor, WDist radius, Player preferredOwner, Func<Actor, bool> additionalFilter = null)
		{
			if (sourceActor == null)
				return null;

			return World.FindActorsInCircle(sourceActor.CenterPosition, radius)
				.Where(a => IsPreferredEnemyUnit(a) &&
							a.CanBeViewedByPlayer(Player) &&
							a.Owner == preferredOwner &&
							!a.Info.HasTraitInfo<LineBuildInfo>() &&
							(additionalFilter == null || additionalFilter(a)))
				.MinByOrDefault(a => (a.CenterPosition - sourceActor.CenterPosition).LengthSquared);
		}

		public Actor FindClosestEnemyBuilding(Actor sourceActor, Func<Actor, bool> additionalFilter = null)
		{
			if (sourceActor == null)
				return null;

			var enemyBuildings = GetCachedEnemyBuildings();
			if (additionalFilter != null)
				enemyBuildings = enemyBuildings.Where(additionalFilter).ToList();

			var nemesis = combatAnalysis?.GetNemesis();
			if (nemesis != null)
			{
				var nemesisBuilding = enemyBuildings
					.Where(a => a.Owner == nemesis)
					.MinByOrDefault(a => (a.CenterPosition - sourceActor.CenterPosition).LengthSquared);
				if (nemesisBuilding != null)
					return nemesisBuilding;
			}

			return enemyBuildings
				.MinByOrDefault(a => (a.CenterPosition - sourceActor.CenterPosition).LengthSquared);
		}

		public bool IsPreferredEnemyUnit(Actor actor)
		{
			if (!IsLiveEnemyActor(actor))
				return false;

			var targetTypes = actor.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty)
				return false;

			if (!Info.IgnoredEnemyTargetTypes.IsEmpty && targetTypes.Overlaps(Info.IgnoredEnemyTargetTypes))
				return false;

			return true;
		}

		public bool IsLiveEnemyActor(Actor actor)
		{
			if (actor == null || actor.IsDead || !actor.IsInWorld || actor.Owner == null)
				return false;
			if (actor.Owner.NonCombatant || actor.Owner.WinState != WinState.Undefined)
				return false;
			if (actor.Owner.RelationshipWith(Player) != PlayerRelationship.Enemy)
				return false;

			return true;
		}

		void EvictPoached(Dictionary<Actor, CNSquad> claimedOwners)
		{
			foreach (var squad in Squads.Where(s => s.TemplateInfo?.Poachable == true).ToList())
			{
				foreach (var (actor, owner) in claimedOwners)
				{
					if (squad == owner)
						continue;

					RemoveActorFromSquad(squad, actor);
				}
			}
		}

		static void RemoveActorFromSquad(CNSquad squad, Actor actor)
		{
			if (actor == null)
				return;

			squad.Units.Remove(actor);
			foreach (var assignment in squad.SlotAssignments)
			{
				assignment.Units.Remove(actor);
				assignment.Passengers.Remove(actor);
			}
		}

		/// <summary>
		/// Releases one unit from its squad back into the manager's free pool. Clearing activeUnits is
		/// what makes it eligible again: while a unit counts as active it is invisible to
		/// IBotNotifyIdleBaseUnits, so CNRepairManagerBotModule never sees a damaged squad member and
		/// cannot send it to a repair bay.
		/// </summary>
		public void ReleaseUnitFromSquad(CNSquad squad, Actor actor)
		{
			if (squad == null || actor == null)
				return;

			RemoveActorFromSquad(squad, actor);
			activeUnits.Remove(actor);
		}

		public IReadOnlyList<Actor> GetCachedOwnBuildings()
		{
			if (World.WorldTick == cachedOwnBuildingsTick)
				return cachedOwnBuildings;
			cachedOwnBuildings = World.ActorsHavingTrait<Building>()
				.Where(a => a.Owner == Player && !a.IsDead)
				.ToList();
			cachedOwnBuildingsTick = World.WorldTick;
			return cachedOwnBuildings;
		}

		public IReadOnlyList<Actor> GetCachedEnemyBuildings()
		{
			if (World.WorldTick == cachedEnemyBuildingsTick)
				return cachedEnemyBuildings;
			cachedEnemyBuildings = World.ActorsHavingTrait<Building>()
				.Where(a => IsLiveEnemyActor(a)
						 && !a.Info.HasTraitInfo<LineBuildInfo>())
				.ToList();
			cachedEnemyBuildingsTick = World.WorldTick;
			return cachedEnemyBuildings;
		}

		// Enemy mobile units and aircraft, filtered to what a squad's target search would consider (live enemy,
		// visible to this bot). Squad target-finding used to scan World.Actors/ActorsHavingTrait directly per
		// call; with several squads searching in the same tick (more likely with more bots/squads active at
		// once), each repeated that scan independently. This is cached per-tick the same way
		// GetCachedEnemyBuildings already is, so repeated searches within a tick share one filtered list.
		public IReadOnlyList<Actor> GetCachedEnemyUnits()
		{
			if (World.WorldTick == cachedEnemyUnitsTick)
				return cachedEnemyUnits;

			var units = new List<Actor>();
			foreach (var actor in World.ActorsHavingTrait<Mobile>())
				if (IsLiveEnemyActor(actor) && actor.CanBeViewedByPlayer(Player))
					units.Add(actor);
			foreach (var actor in World.ActorsHavingTrait<Aircraft>())
				if (IsLiveEnemyActor(actor) && actor.CanBeViewedByPlayer(Player))
					units.Add(actor);

			cachedEnemyUnits = units;
			cachedEnemyUnitsTick = World.WorldTick;
			return cachedEnemyUnits;
		}

		public CPos GetRandomBaseCenter()
		{
			var buildings = GetCachedOwnBuildings();
			return buildings.Count > 0 ? buildings.Random(World.LocalRandom).Location : initialBaseCenter;
		}

		public static Actor ClosestTo(IEnumerable<Actor> actors, Actor target)
		{
			if (target == null)
				return null;
			return actors.MinByOrDefault(a =>
				(a.CenterPosition - target.CenterPosition).LengthSquared);
		}

		int CompareReinforcementPriority(CNSquad a, CNSquad b)
		{
			var priority = EffectiveScore(b.TemplateInfo).CompareTo(EffectiveScore(a.TemplateInfo));
			if (priority != 0)
				return priority;

			var operational = a.IsOperational.CompareTo(b.IsOperational);
			if (operational != 0)
				return operational;

			var criticalMissing = CountCriticalMissingSlots(b).CompareTo(CountCriticalMissingSlots(a));
			if (criticalMissing != 0)
				return criticalMissing;

			return 0;
		}

		static int CountCriticalMissingSlots(CNSquad squad)
		{
			var total = 0;

			foreach (var assignment in squad.SlotAssignments)
			{
				if (assignment.SlotInfo.Optional)
					continue;

				var missing = assignment.MissingCount;
				if (missing <= 0)
					continue;

				total += IsCarrierOrPassengerSlot(assignment.SlotInfo) ? missing * 100 : missing;
			}

			return total;
		}

		static IEnumerable<CNSlotAssignment> EnumerateAssignmentsForReinforcement(CNSquad squad)
		{
			foreach (var assignment in squad.SlotAssignments)
				if (IsCarrierSlot(assignment.SlotInfo))
					yield return assignment;

			foreach (var assignment in squad.SlotAssignments)
				if (assignment.SlotInfo.IsPassenger && SquadHasLiveCarrier(squad))
					yield return assignment;

			foreach (var assignment in squad.SlotAssignments)
				if (!assignment.SlotInfo.Optional &&
					!IsCarrierOrPassengerSlot(assignment.SlotInfo))
					yield return assignment;

			foreach (var assignment in squad.SlotAssignments)
				if (assignment.SlotInfo.Optional && !IsCarrierOrPassengerSlot(assignment.SlotInfo))
					yield return assignment;
		}

		static bool IsCarrierOrPassengerSlot(CNSlotInfo slotInfo)
		{
			return slotInfo.IsCarrier || slotInfo.IsAircraftCarrier || slotInfo.IsPassenger;
		}

		static bool IsCarrierSlot(CNSlotInfo slotInfo)
		{
			return slotInfo.IsCarrier || slotInfo.IsAircraftCarrier;
		}

		// A transport still loading at base (AcceptingPassengers) keeps pulling passengers until its
		// carriers are full, even once operational. Without this, a transport whose carrier slot fills
		// before enough infantry exists would lock in as "done" and depart half-empty (reinforcement
		// is otherwise blocked for operational attack squads).
		static bool AcceptingPassengerTopUp(CNSquad squad)
		{
			return squad.AcceptingPassengers
				&& SquadHasLiveCarrier(squad)
				&& squad.RecruitedPassengerCount < squad.DesiredPassengerCount;
		}

		static bool SquadHasLiveCarrier(CNSquad squad)
		{
			foreach (var assignment in squad.SlotAssignments)
			{
				if (!IsCarrierSlot(assignment.SlotInfo))
					continue;

				if (assignment.Units.Any(a => a != null && !a.IsDead && a.IsInWorld))
					return true;
			}

			return false;
		}

		void AddUnclaimedMobileUnits(HashSet<Actor> units)
		{
			foreach (var actor in World.ActorsHavingTrait<Mobile>())
			{
				if (actor.Owner != Player || actor.IsDead || !actor.IsInWorld || activeUnits.Contains(actor))
					continue;
				if (actor.Info.HasTraitInfo<MobSpawnerSlaveInfo>())
					continue;
				if (actor.CurrentActivity is Enter)
					continue;

				// Leave damaged infantry alone so the repair manager can send them to heal.
				if (actor.Info.HasTraitInfo<RepairableInBarracksInfo>())
				{
					var health = actor.TraitOrDefault<IHealth>();
					if (health != null && health.DamageState > DamageState.Undamaged)
						continue;
				}

				units.Add(actor);
			}

			foreach (var actor in World.ActorsHavingTrait<Aircraft>())
			{
				if (actor.Owner != Player || actor.IsDead || !actor.IsInWorld || activeUnits.Contains(actor))
					continue;
				if (actor.Info.HasTraitInfo<MobSpawnerSlaveInfo>())
					continue;
				if (actor.CurrentActivity is Enter)
					continue;
				units.Add(actor);
			}
		}
	}
}
