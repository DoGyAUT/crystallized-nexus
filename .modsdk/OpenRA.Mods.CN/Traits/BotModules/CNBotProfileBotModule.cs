#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.CN.Traits.BotModules.Squads;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public enum BotProfile { Rush, Turtle, Tech, Expansion, Steamroller, Adaptive }
	public enum TechStage { Early, Mid, Late }

	public readonly struct CNBotStrategySnapshot
	{
		public readonly BotProfile Profile;
		public readonly TechStage TechStage;
		public readonly int ExpansionBudget;
		public readonly int TechBudget;
		public readonly int DefenseBudget;
		public readonly int ProductionBudget;
		public readonly int HarvesterTargetPercent;

		public CNBotStrategySnapshot(
			BotProfile profile,
			TechStage techStage,
			int expansionBudget,
			int techBudget,
			int defenseBudget,
			int productionBudget,
			int harvesterTargetPercent)
		{
			Profile = profile;
			TechStage = techStage;
			ExpansionBudget = expansionBudget;
			TechBudget = techBudget;
			DefenseBudget = defenseBudget;
			ProductionBudget = productionBudget;
			HarvesterTargetPercent = harvesterTargetPercent;
		}
	}

	public sealed class CNBotProfileBudget
	{
		public readonly int Expansion = 25;
		public readonly int Tech = 15;
		public readonly int Defense = 20;
		public readonly int Production = 25;

		public CNBotProfileBudget() { }

		public CNBotProfileBudget(int expansion, int tech, int defense, int production)
		{
			Expansion = expansion;
			Tech = tech;
			Defense = defense;
			Production = production;
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Sets the AI's strategic profile and drives adaptive profile switching and tech-stage detection.")]
	public class CNBotProfileBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Strategic profile this bot runs. Adaptive switches automatically based on game state.")]
		public readonly BotProfile Profile = BotProfile.Adaptive;

		[Desc("Subset of TechTypes (defined in CNBaseBuilderBotModule) that signal Late-game tech.",
			"E.g. Tech Center, Pyramid, Missile Silo. Empty = no Late stage detected.")]
		public readonly FrozenSet<string> LateTechTypes = FrozenSet<string>.Empty;

		[Desc("Cash reserve below this value (after grace period) triggers Expansion mode.")]
		public readonly int AdaptiveExpansionIncomeThreshold = 150;

		[Desc("Sum of defense danger hotspot weights above this triggers Turtle mode.")]
		public readonly int AdaptiveTurtleDangerThreshold = 350;

		[Desc("Total alive combat units across all squads above this triggers Rush mode (when not under attack).")]
		public readonly int AdaptiveRushUnitThreshold = 15;

		[Desc("Ticks between profile re-evaluations in Adaptive mode.")]
		public readonly int AdaptiveSwitchCooldownTicks = 1500;

		[Desc("World tick after which low-cash Expansion mode can trigger. Prevents false positives in the opening.")]
		public readonly int AdaptiveExpansionGraceTicks = 3000;

		[Desc("Added to RushUnitThreshold in Early tech stage (bot needs more units to consider rushing with basic troops).")]
		public readonly int RushUnitThresholdEarlyOffset = 5;

		[Desc("Added to RushUnitThreshold in Late tech stage (advanced units punch harder, lower threshold).")]
		public readonly int RushUnitThresholdLateOffset = -3;

		[Desc("Minimum ticks an adaptive bot keeps a non-emergency strategic intent before switching again.")]
		public readonly int AdaptiveMinimumIntentHoldTicks = 3000;

		[Desc("Danger score that immediately overrides adaptive hysteresis into Turtle.")]
		public readonly int AdaptiveEmergencyTurtleDangerThreshold = 600;

		[Desc("Show each adaptive profile switch in the chat, to spectators only. Anyone playing in the " +
			"match sees nothing - which strategy a bot has adopted is information a human opponent should " +
			"not be handed. Needs no debug setting; it is meant for watching a playtest.")]
		public readonly bool AnnounceProfileToObservers = true;

		[Desc("Known enemy emplacements at which the opponent counts as fully dug in. The count comes from " +
			"what the bot has seen or been shot by, so it grows through contact rather than being read " +
			"off the map. 0 disables every fortification term below.")]
		public readonly int AdaptiveEnemyFortifiedDefenses = 6;

		[Desc("Score subtracted from Rush at full enemy fortification. A rush is a small early force, " +
			"which is exactly what a line of emplacements exists to shred.")]
		public readonly float AdaptiveFortificationRushPenalty = 3f;

		[Desc("Score added to Steamroller at full enemy fortification - the opposite of the Rush term, " +
			"and deliberately so. Steamroller waits for a large late army; it is what you bring to crack " +
			"a fortified position rather than what the position is built to stop.")]
		public readonly float AdaptiveFortificationSteamrollerBonus = 2f;

		[Desc("Score added to Tech at full enemy fortification: out-range what cannot be walked into.")]
		public readonly float AdaptiveFortificationTechBonus = 2f;

		[Desc("Score added to Expansion at full enemy fortification: take the map while they sit still.")]
		public readonly float AdaptiveFortificationExpansionBonus = 1f;

		[Desc("Earliest tick at which Adaptive may choose Steamroller.")]
		public readonly int AdaptiveSteamrollerEarliestTick = 7500;

		[Desc("Army size, as a multiple of this bot's own rush threshold, at which Adaptive may choose " +
			"Steamroller. Relative rather than an absolute count: the same fifteen units are a rush in a " +
			"rich game and a steamroller in a poor one, and a fixed count simply never fires on a map " +
			"where nobody can afford the army it names.")]
		public readonly float AdaptiveSteamrollerArmyRatio = 1.6f;

		[Desc("Army ratio required instead when the enemy is fortified. Lower, because a dug-in opponent " +
			"is what Steamroller exists for - waiting for a bigger army against a position that keeps " +
			"growing more emplacements is how the moment gets missed.")]
		public readonly float AdaptiveSteamrollerFortifiedArmyRatio = 1f;

		[Desc("Fortification ratio from which the enemy counts as dug in for the above.")]
		public readonly float AdaptiveSteamrollerFortifiedRatio = 0.5f;

		[Desc("Score added to Steamroller per whole army ratio above the threshold it had to clear.")]
		public readonly float AdaptiveSteamrollerArmySurplusScore = 1.5f;

		[Desc("Cash gained per minute above which Adaptive prefers Tech while not under immediate threat. " +
			"A rate, not a balance: a bot that spends everything it earns sits near zero however well its " +
			"economy runs, and a bot mining its last field dry can hold a full till - neither says whether " +
			"teching is affordable, but a balance that climbs does.")]
		public readonly int AdaptiveTechCashTrendPerMinute = 400;

		[Desc("Profiles the adaptive bot randomly picks from at game start. " +
			"Empty = always start as Expansion.")]
		public readonly BotProfile[] AdaptiveStartProfiles = [];

		[Desc("Score bonus added to the currently active profile during each evaluation. " +
			"Prevents rapid oscillation: a profile has to beat the current one by this margin to trigger a switch.")]
		public readonly float AdaptiveProfileMomentumBonus = 2f;

		[Desc("Score deducted per allied adaptive bot already running a given profile. " +
			"Encourages teammates to spread across different strategies.")]
		public readonly float TeamAdaptiveCoverageWeight = 1.5f;

		[Desc("Score bonus added to Turtle when an allied adaptive bot is under heavy attack. " +
			"Helps the team coordinate shared defence.")]
		public readonly float TeamAdaptiveThreatShareBonus = 1.5f;

		[Desc("Score bonus added to Rush per ally already in Rush during Early tech stage, " +
			"but only when this bot also meets the rush unit threshold. " +
			"Nudges rush-ready bots to synchronise without forcing bots that aren't ready.")]
		public readonly float TeamEarlyRushSynergyBonus = 1.5f;

		[Desc("Score bonus added to Turtle for the bot on the team with the most danger. " +
			"Encourages the frontline bot to defend while others push.")]
		public readonly float TeamFrontlineTurtleBonus = 2f;

		[Desc("Score bonus added to Steamroller and Tech for the bot on the team with the least danger. " +
			"Encourages safer back-line bots to press the offensive.")]
		public readonly float TeamBacklinePushBonus = 1.5f;

		[Desc("Target world ticks for entering Mid tech stage. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> MidTechTicks = null;

		[Desc("Target world ticks for entering Late tech stage. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> LateTechTicks = null;

		[Desc("Default target world tick for entering Mid tech stage.")]
		public readonly int DefaultMidTechTick = 4500;

		[Desc("Default target world tick for entering Late tech stage.")]
		public readonly int DefaultLateTechTick = 12000;

		[Desc("Percentage multiplier applied to the profile's TechBudget per named handicap tier",
			"(Easy/Normal/Hard/Brutal, see CNHandicapTiers). 100 = no change. Missing tiers or an",
			"empty dict leave TechBudget unscaled - this only affects difficulty, not bot profile.")]
		public readonly Dictionary<string, int> TechBudgetDifficultyScale = [];

		[Desc("Percentage multiplier applied to MidTechTicks/LateTechTicks per named handicap tier.",
			"Lower than 100 makes that difficulty reach tech stages sooner; 100 = no change.")]
		public readonly Dictionary<string, int> TechTickDifficultyScale = [];

		[Desc("Rush profile budget shares: expansion, tech, defense, production.")]
		public readonly int RushExpansionBudget = 12;
		public readonly int RushTechBudget = 8;
		public readonly int RushDefenseBudget = 8;
		public readonly int RushProductionBudget = 42;

		[Desc("Turtle profile budget shares: expansion, tech, defense, production.")]
		public readonly int TurtleExpansionBudget = 30;
		public readonly int TurtleTechBudget = 14;
		public readonly int TurtleDefenseBudget = 42;
		public readonly int TurtleProductionBudget = 14;

		[Desc("Tech profile budget shares: expansion, tech, defense, production.")]
		public readonly int TechExpansionBudget = 18;
		public readonly int TechTechBudget = 40;
		public readonly int TechDefenseBudget = 24;
		public readonly int TechProductionBudget = 18;

		[Desc("Expansion profile budget shares: expansion, tech, defense, production.")]
		public readonly int ExpansionExpansionBudget = 42;
		public readonly int ExpansionTechBudget = 16;
		public readonly int ExpansionDefenseBudget = 16;
		public readonly int ExpansionProductionBudget = 14;

		[Desc("Steamroller profile budget shares: expansion, tech, defense, production.")]
		public readonly int SteamrollerExpansionBudget = 16;
		public readonly int SteamrollerTechBudget = 12;
		public readonly int SteamrollerDefenseBudget = 8;
		public readonly int SteamrollerProductionBudget = 64;

		[Desc("Per-profile harvester target multiplier in percent. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> HarvesterTargetPercents = null;

		public override object Create(ActorInitializer init) { return new CNBotProfileBotModule(init.Self, this); }
	}

	public class CNBotProfileBotModule : ConditionalTrait<CNBotProfileBotModuleInfo>, IBotTick
	{
		public BotProfile ActiveProfile { get; private set; }
		public TechStage ActiveTechStage { get; private set; }
		public CNBotStrategySnapshot CurrentStrategy { get; private set; }
		public int LastDangerScore { get; private set; }

		readonly World world;
		readonly Player player;
		Actor playerActor;
		int switchCooldown;
		int activeProfileSinceTick;
		int lastEvalCash;
		int lastEvalCashTick;
		int profileConditionToken = Actor.InvalidConditionToken;

		CNBaseBuilderBotModule baseBuilder;
		CombatAnalysisBotModule combatAnalysis;
		CNSquadManagerBotModule squadManager;
		CNMcvExpansionManagerBotModule mcvExpansion;
		PlayerResources playerResources;

		public CNBotProfileBotModule(Actor self, CNBotProfileBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			ActiveProfile = ResolveInitialProfile();
			CurrentStrategy = CreateStrategySnapshot(ActiveProfile, ActiveTechStage);
			activeProfileSinceTick = world.WorldTick;
		}

		protected override void Created(Actor self)
		{
			playerActor = self;
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();

			if (Info.Profile == BotProfile.Adaptive)
				profileConditionToken = self.GrantCondition(ProfileConditionName(ActiveProfile));
		}

		void IBotTick.BotTick(IBot bot)
		{
			// Re-resolved whenever the cached one is no longer enabled, not once at startup.
			//
			// There is one squad manager, base builder and MCV manager PER PROFILE, each gated by a
			// RequiresCondition, and this module is what switches between them. Caching the reference
			// on the first tick therefore left it pointing at an instance that the module's own next
			// decision disabled. The fortification input read zero for the rest of the match while the
			// active squad manager knew of twenty-two enemy emplacements, and every other read through
			// these references was equally stale. Same pattern CNUnitBuilderBotModule already uses.
			if (baseBuilder == null || !baseBuilder.IsTraitEnabled())
				baseBuilder = bot.Player.PlayerActor
					.TraitsImplementing<CNBaseBuilderBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());

			if (combatAnalysis == null || !combatAnalysis.IsTraitEnabled())
				combatAnalysis = bot.Player.PlayerActor
					.TraitsImplementing<CombatAnalysisBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());

			if (squadManager == null || !squadManager.IsTraitEnabled())
				squadManager = bot.Player.PlayerActor
					.TraitsImplementing<CNSquadManagerBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());

			if (mcvExpansion == null || !mcvExpansion.IsTraitEnabled())
				mcvExpansion = bot.Player.PlayerActor
					.TraitsImplementing<CNMcvExpansionManagerBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());

			UpdateTechStage();

			if (Info.Profile != BotProfile.Adaptive)
			{
				SwitchTo(Info.Profile, false);
				UpdateStrategySnapshot();
				return;
			}

			if (--switchCooldown > 0)
			{
				UpdateStrategySnapshot();
				return;
			}

			EvaluateAndSwitch();
			UpdateStrategySnapshot();
			switchCooldown = Info.AdaptiveSwitchCooldownTicks;
		}

		void UpdateTechStage()
		{
			if (baseBuilder == null)
				return;

			var buildings = baseBuilder.GetCachedPlayerBuildings();
			var midTick = ScaleByDifficulty(GetProfileTick(Info.MidTechTicks, Info.DefaultMidTechTick), Info.TechTickDifficultyScale);
			var lateTick = ScaleByDifficulty(GetProfileTick(Info.LateTechTicks, Info.DefaultLateTechTick), Info.TechTickDifficultyScale);

			if (Info.LateTechTypes.Count > 0 && buildings.Any(a => Info.LateTechTypes.Contains(a.Info.Name)))
				ActiveTechStage = TechStage.Late;
			else if (lateTick > 0 && world.WorldTick >= lateTick && HasStableEconomy())
				ActiveTechStage = TechStage.Late;
			else if (baseBuilder.Info.TechTypes.Count > 0 && buildings.Any(a => baseBuilder.Info.TechTypes.Contains(a.Info.Name)))
				ActiveTechStage = TechStage.Mid;
			else if (midTick > 0 && world.WorldTick >= midTick && HasMinimalProductionBase())
				ActiveTechStage = TechStage.Mid;
			else
				ActiveTechStage = TechStage.Early;
		}

		// Profiles considered during scored evaluation (excludes Adaptive — that is the mode, not a target).
		static readonly BotProfile[] ProfileCandidates =
		[
			BotProfile.Turtle,
			BotProfile.Expansion,
			BotProfile.Rush,
			BotProfile.Steamroller,
			BotProfile.Tech,
		];

		void EvaluateAndSwitch()
		{
			if (baseBuilder == null)
				return;

			// Collect inputs once.
			var dangerThreats = baseBuilder.GetDefensePlacementThreats(baseBuilder.DefenseCenter);
			var dangerScore = 0;
			foreach (var t in dangerThreats)
				dangerScore += t.Weight;

			LastDangerScore = dangerScore;

			// Emergency Turtle: bypass hysteresis entirely.
			if (dangerScore >= Info.AdaptiveEmergencyTurtleDangerThreshold)
			{
				SwitchTo(BotProfile.Turtle, emergency: true);
				return;
			}

			// Respect minimum intent hold before any non-emergency switch.
			if (world.WorldTick - activeProfileSinceTick < Info.AdaptiveMinimumIntentHoldTicks)
				return;

			var hasActiveThreat = combatAnalysis?.HasActiveThreat() ?? false;
			var offensiveUnits = CountOffensiveSquadUnits();
			var cash = playerResources.GetCashAndResources();
			var stableEco = HasStableEconomy();

			// Whether the till is filling, rather than how full it happens to be right now. Sampled
			// between evaluations, so the window is however long the last hold lasted; dividing by the
			// elapsed ticks keeps the rate comparable regardless.
			var cashTrendPerMinute = 0f;
			if (lastEvalCashTick > 0 && world.WorldTick > lastEvalCashTick)
			{
				var ticksPerMinute = 60000f / world.Timestep;
				cashTrendPerMinute = (cash - lastEvalCash) * ticksPerMinute / (world.WorldTick - lastEvalCashTick);
			}

			lastEvalCash = cash;
			lastEvalCashTick = world.WorldTick;

			// Team coordination: sample allied adaptive bots once per evaluation.
			var alliedAdaptive = GetAlliedAdaptiveModules();
			var maxAllyDangerRatio = alliedAdaptive.Length > 0
				? alliedAdaptive.Max(a => (float)a.LastDangerScore / Math.Max(1, Info.AdaptiveTurtleDangerThreshold))
				: 0f;

			// Frontline rank: fraction of allies with lower danger than this bot (0 = safest, 1 = most exposed).
			var frontlineRank = 0.5f;
			if (alliedAdaptive.Length > 0)
			{
				var lowerDangerCount = alliedAdaptive.Count(a => a.LastDangerScore < LastDangerScore);
				frontlineRank = (float)lowerDangerCount / alliedAdaptive.Length;
			}

			// Normalized danger: 0 at no threat, 1.0 at the normal Turtle threshold.
			var dangerRatio = (float)dangerScore / Math.Max(1, Info.AdaptiveTurtleDangerThreshold);

			var rushThreshold = Info.AdaptiveRushUnitThreshold
				+ (ActiveTechStage == TechStage.Early ? Info.RushUnitThresholdEarlyOffset : 0)
				+ (ActiveTechStage == TechStage.Late ? Info.RushUnitThresholdLateOffset : 0);

			var needsExpansion = world.WorldTick > Info.AdaptiveExpansionGraceTicks
				&& cash < Info.AdaptiveExpansionIncomeThreshold
				&& (!baseBuilder.HasAdequateRefineryCount() || baseBuilder.ShouldExpandEconomy());

			// The first two inputs about anything other than itself.
			//
			// Every other term below asks how much danger I am in, how many units I have, how much cash.
			// Nothing asked what the opponent was doing, which is why the bot would keep choosing Rush
			// against a player who had walled off every approach - it could see its own army was large
			// and drew the obvious conclusion from that alone.
			//
			// Fortification is measured from emplacements the bot has actually seen or been shot by, so
			// it grows through contact rather than being read off the map.
			var fortifiedRatio = squadManager != null && Info.AdaptiveEnemyFortifiedDefenses > 0
				? Math.Min(1f, (float)squadManager.KnownEnemyDefenseCount / Info.AdaptiveEnemyFortifiedDefenses)
				: 0f;

			// Absolute cash under 150 was the only economic trigger, so a bot could be mining its last
			// field dry and feel fine as long as the till happened to be full.
			var starving = mcvExpansion != null && mcvExpansion.IsResourceStarved();

			// Both of the following used to be absolute: 22 offensive units and 6000 cash. Neither was
			// reached once in a whole eight-player game - armies peaked at 15 and the till at 4038 - so
			// two of the five profiles were unreachable by construction rather than by judgement. Both
			// now measure against something that moves with the game.
			var armyRatio = (float)offensiveUnits / Math.Max(1, rushThreshold);
			var steamrollerArmyRatio = fortifiedRatio >= Info.AdaptiveSteamrollerFortifiedRatio
				? Info.AdaptiveSteamrollerFortifiedArmyRatio
				: Info.AdaptiveSteamrollerArmyRatio;

			var steamrollerReady = world.WorldTick >= Info.AdaptiveSteamrollerEarliestTick
				&& armyRatio >= steamrollerArmyRatio
				&& stableEco;

			var cashRich = stableEco && cashTrendPerMinute >= Info.AdaptiveTechCashTrendPerMinute;

			// Score each candidate; current profile gets a momentum bonus so it takes
			// a meaningful advantage for a rival profile to trigger a switch.
			var best = BotProfile.Expansion;
			var bestScore = float.MinValue;
			var scoreReport = new System.Text.StringBuilder();

			foreach (var candidate in ProfileCandidates)
			{
				var score = candidate == ActiveProfile ? Info.AdaptiveProfileMomentumBonus : 0f;

				score += candidate switch
				{
					// Turtle: scales with danger + active threat. Has no penalty — being
					// defensive is always valid when under pressure.
					BotProfile.Turtle =>
						dangerRatio * 3f + (hasActiveThreat ? 2f : 0f),

					// Expansion: small constant baseline (reasonable fallback) plus a large
					// bonus when the economy genuinely needs rebuilding. Running the fields dry counts
					// for as much as an empty till, and taking ground is also the sane answer to an
					// opponent who has dug in rather than come out.
					BotProfile.Expansion =>
						1.5f + (needsExpansion ? 4f : 0f) + (starving ? 4f : 0f)
						+ fortifiedRatio * Info.AdaptiveFortificationExpansionBonus,

					// Rush: bonus scales with unit surplus above threshold; threat reduces it.
					// Hard penalty when below threshold so a small army doesn't rush prematurely.
					// Fortification cuts it hardest: a rush is precisely what a wall of emplacements
					// is built to stop, and throwing one at it is how squads get ground down for nothing.
					BotProfile.Rush =>
						offensiveUnits >= rushThreshold
							? 3f + (offensiveUnits - rushThreshold) * 0.1f
							  - dangerRatio * 2f - (hasActiveThreat ? 2f : 0f)
							  - fortifiedRatio * Info.AdaptiveFortificationRushPenalty
							: -3f,

					// Steamroller: large bonus when all conditions align, hard penalty otherwise.
					// Unit surplus above its threshold keeps the score growing to outpace Rush.
					//
					// Fortification argues FOR it, not against. This is the opposite of the Rush term
					// above and the distinction matters: a rush is a small early force, which is exactly
					// what a line of emplacements exists to shred. A steamroller waits for tick 7500 and
					// thirty-two units - it is the thing you bring to crack a position, not the thing
					// the position is built to stop.
					BotProfile.Steamroller =>
						steamrollerReady
							? 4.5f + (armyRatio - steamrollerArmyRatio) * Info.AdaptiveSteamrollerArmySurplusScore
							  + fortifiedRatio * Info.AdaptiveFortificationSteamrollerBonus
							: -5f,

					// Tech: modest bonus during the cash-rich window before late-game, and the answer to
					// a fortified opponent - out-range what cannot be walked into.
					BotProfile.Tech =>
						(cashRich && ActiveTechStage != TechStage.Late ? 2.5f : -2f)
						+ fortifiedRatio * Info.AdaptiveFortificationTechBonus,

					_ => 0f
				};

				if (alliedAdaptive.Length > 0)
				{
					// Coverage penalty always applies to avoid blind overlap.
					score -= alliedAdaptive.Count(a => a.ActiveProfile == candidate) * Info.TeamAdaptiveCoverageWeight;

					// Early rush synergy: nudge bots that are already rush-ready to join allies
					// who are rushing. Only fires when this bot itself meets the threshold —
					// prevents dragging an under-strength bot into a rush it can't sustain.
					if (candidate == BotProfile.Rush
						&& ActiveTechStage == TechStage.Early
						&& offensiveUnits >= rushThreshold)
					{
						var allyRushCount = alliedAdaptive.Count(a => a.ActiveProfile == BotProfile.Rush);
						score += allyRushCount * Info.TeamEarlyRushSynergyBonus;
					}

					// Threat sharing: ally under heavy attack makes Turtle more attractive.
					if (candidate == BotProfile.Turtle && maxAllyDangerRatio > 0.5f)
						score += maxAllyDangerRatio * Info.TeamAdaptiveThreatShareBonus;

					// Frontline bias: most-exposed bot leans Turtle; safest bot leans Steamroller/Tech.
					if (candidate == BotProfile.Turtle)
						score += frontlineRank * Info.TeamFrontlineTurtleBonus;
					else if (candidate == BotProfile.Steamroller || candidate == BotProfile.Tech)
						score += (1f - frontlineRank) * Info.TeamBacklinePushBonus;
				}

				scoreReport
					.Append(scoreReport.Length > 0 ? ", " : "")
					.Append(candidate.ToString())
					.Append(' ')
					.Append(score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

				if (score > bestScore)
				{
					bestScore = score;
					best = candidate;
				}
			}

			// This module decided the bot's whole strategy and reported nothing. The effect was visible
			// downstream and unattributable: a log line read "expansion held: needs 12000", which is the
			// Turtle cash threshold, and neither we nor the player could see why the bot was on Turtle or
			// for how long. Every input and every score, so a surprising choice can be traced to the term
			// that caused it.
			CNBotLog.Debug(
				"{0} profile eval: {1} → {2} | danger {3} (ratio {4:0.00}), threat {5}, units {6}/{7} (army {8:0.00}, " +
				"steamroller needs {9:0.00}), cash {10} ({11:+0;-0;0}/min, tech needs {12}), eco stable {13}, " +
				"starving {14}, enemy defences {15} (ratio {16:0.00}) | {17}",
				player, ActiveProfile, best, dangerScore, dangerRatio, hasActiveThreat, offensiveUnits,
				rushThreshold, armyRatio, steamrollerArmyRatio, cash, cashTrendPerMinute,
				Info.AdaptiveTechCashTrendPerMinute, stableEco, starving,
				squadManager?.KnownEnemyDefenseCount ?? 0, fortifiedRatio, scoreReport);

			SwitchTo(best, emergency: false);
		}

		int CountOffensiveSquadUnits()
		{
			if (squadManager == null)
				return 0;

			var totalUnits = 0;
			foreach (var squad in squadManager.Squads)
			{
				if (!IsOffensiveSquad(squad.Type))
					continue;

				foreach (var u in squad.Units)
					if (u != null && !u.IsDead && u.IsInWorld)
						totalUnits++;
			}

			return totalUnits;
		}

		CNBotProfileBotModule[] GetAlliedAdaptiveModules()
		{
			var result = new System.Collections.Generic.List<CNBotProfileBotModule>();
			foreach (var p in world.Players)
			{
				if (p == player || !player.IsAlliedWith(p))
					continue;

				foreach (var m in p.PlayerActor.TraitsImplementing<CNBotProfileBotModule>())
				{
					if (m.IsTraitEnabled() && m.Info.Profile == BotProfile.Adaptive)
						result.Add(m);
				}
			}

			return result.ToArray();
		}

		bool HasMinimalProductionBase()
		{
			// "Minimal" means the production floor, not the budget-weighted refinery target — a bot
			// with one refinery and a barracks has a minimal production base by any reading, and
			// gating the mid-tech step on the expansion goal delays it for no gain.
			return baseBuilder != null &&
				baseBuilder.HasProductionFloorRefineries() &&
				baseBuilder.GetCachedPlayerBuildings().Any(a => baseBuilder.Info.ProductionTypes.Contains(a.Info.Name));
		}

		bool HasStableEconomy()
		{
			return baseBuilder != null &&
				baseBuilder.HasAdequateRefineryCount() &&
				!baseBuilder.ShouldExpandEconomy();
		}

		int GetProfileTick(FrozenDictionary<string, int> ticks, int fallback)
		{
			if (ticks == null || ticks.Count == 0)
				return fallback;

			return ticks.TryGetValue(ActiveProfile.ToString(), out var value) ? value : fallback;
		}

		void UpdateStrategySnapshot()
		{
			CurrentStrategy = CreateStrategySnapshot(ActiveProfile, ActiveTechStage);
		}

		CNBotStrategySnapshot CreateStrategySnapshot(BotProfile profile, TechStage techStage)
		{
			var budget = GetProfileBudget(profile);
			return new CNBotStrategySnapshot(
				profile,
				techStage,
				budget.Expansion,
				ScaleByDifficulty(budget.Tech, Info.TechBudgetDifficultyScale),
				budget.Defense,
				budget.Production,
				GetProfileValue(Info.HarvesterTargetPercents, profile, DefaultHarvesterTargetPercent(profile)));
		}

		int ScaleByDifficulty(int value, Dictionary<string, int> scales)
		{
			if (scales == null || scales.Count == 0)
				return value;

			return scales.TryGetValue(CNHandicapTiers.Name(player.Handicap), out var percent) ? value * percent / 100 : value;
		}

		CNBotProfileBudget GetProfileBudget(BotProfile profile)
		{
			return profile switch
			{
				BotProfile.Rush => new CNBotProfileBudget(
					Info.RushExpansionBudget, Info.RushTechBudget, Info.RushDefenseBudget, Info.RushProductionBudget),
				BotProfile.Turtle => new CNBotProfileBudget(
					Info.TurtleExpansionBudget, Info.TurtleTechBudget, Info.TurtleDefenseBudget, Info.TurtleProductionBudget),
				BotProfile.Tech => new CNBotProfileBudget(
					Info.TechExpansionBudget, Info.TechTechBudget, Info.TechDefenseBudget, Info.TechProductionBudget),
				BotProfile.Expansion => new CNBotProfileBudget(
					Info.ExpansionExpansionBudget, Info.ExpansionTechBudget, Info.ExpansionDefenseBudget, Info.ExpansionProductionBudget),
				BotProfile.Steamroller => new CNBotProfileBudget(
					Info.SteamrollerExpansionBudget, Info.SteamrollerTechBudget, Info.SteamrollerDefenseBudget, Info.SteamrollerProductionBudget),
				_ => new CNBotProfileBudget()
			};
		}

		static int GetProfileValue(FrozenDictionary<string, int> values, BotProfile profile, int fallback)
		{
			if (values == null || values.Count == 0)
				return fallback;

			return values.TryGetValue(profile.ToString(), out var value) ? value : fallback;
		}

		static int DefaultHarvesterTargetPercent(BotProfile profile)
		{
			return profile switch
			{
				BotProfile.Rush => 95,
				BotProfile.Turtle => 125,
				BotProfile.Tech => 105,
				BotProfile.Expansion => 125,
				BotProfile.Steamroller => 115,
				_ => 100
			};
		}

		/// <summary>
		/// Tells spectators which strategy a bot just adopted, and tells nobody else.
		/// <para>
		/// Watching a match, the one thing you cannot see is what the bot thinks it is doing - a bot
		/// that stops attacking looks broken whether it switched to Turtle on purpose or is stuck.
		/// Playing against it, knowing that is a straight information leak, so the line is gated on the
		/// viewer having no player of their own. Every client simulates the bot, so this check is made
		/// per client and each one answers it for itself.
		/// </para>
		/// Display only: it writes to the chat overlay and touches nothing the simulation reads, so it
		/// cannot desync a game where one participant is watching and another is playing.
		/// </summary>
		void AnnounceProfileToObservers(BotProfile from, BotProfile to, bool emergency)
		{
			if (!Info.AnnounceProfileToObservers || Info.Profile != BotProfile.Adaptive)
				return;

			if (world.LocalPlayer != null)
				return;

			TextNotificationsManager.AddSystemLine("Bot",
				$"{player.PlayerName}: {from} → {to}{(emergency ? " (emergency)" : "")}");
		}

		void SwitchTo(BotProfile nextProfile, bool emergency)
		{
			if (!emergency && ActiveProfile == nextProfile)
				return;

			if (!emergency && Info.Profile == BotProfile.Adaptive &&
				world.WorldTick - activeProfileSinceTick < Info.AdaptiveMinimumIntentHoldTicks)
				return;

			if (ActiveProfile != nextProfile)
			{
				AnnounceProfileToObservers(ActiveProfile, nextProfile, emergency);

				ActiveProfile = nextProfile;
				activeProfileSinceTick = world.WorldTick;

				if (Info.Profile == BotProfile.Adaptive && playerActor != null)
				{
					if (profileConditionToken != Actor.InvalidConditionToken)
						playerActor.RevokeCondition(profileConditionToken);
					profileConditionToken = playerActor.GrantCondition(ProfileConditionName(nextProfile));
				}
			}
		}

		static string ProfileConditionName(BotProfile profile) => profile switch
		{
			BotProfile.Rush => "cn-profile-rush",
			BotProfile.Turtle => "cn-profile-turtle",
			BotProfile.Tech => "cn-profile-tech",
			BotProfile.Expansion => "cn-profile-expansion",
			BotProfile.Steamroller => "cn-profile-steamroller",
			_ => "cn-profile-expansion"
		};

		BotProfile ResolveInitialProfile()
		{
			if (Info.Profile != BotProfile.Adaptive)
				return Info.Profile;

			if (Info.AdaptiveStartProfiles.Length > 0)
				return Info.AdaptiveStartProfiles[world.LocalRandom.Next(Info.AdaptiveStartProfiles.Length)];

			return BotProfile.Expansion;
		}

		static bool IsOffensiveSquad(CNSquadType type)
		{
			return type == CNSquadType.Assault ||
				type == CNSquadType.Rush ||
				type == CNSquadType.Raider ||
				type == CNSquadType.Stealth ||
				type == CNSquadType.SubterraneanAssault ||
				type == CNSquadType.AircraftAttack ||
				type == CNSquadType.AircraftRaider ||
				type == CNSquadType.AirTransport;
		}
	}
}
