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

		[Desc("How much the map's own shape - doors, bridges, ramps - counts toward preferring Turtle, as a",
			"fraction of what live danger counts for. Deliberately small: exposed ground is a reason to lean",
			"defensive, being shot at is a reason to turn defensive, and the two used to be added together",
			"as one number. 0 ignores map shape entirely.")]
		public readonly float AdaptiveStaticExposureWeight = 0.25f;

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

		[Desc("Ticks between emergency danger checks while the full profile evaluation is on cooldown. The",
			"danger memory records an attack at most every 30 ticks, so reading it oftener than this cannot",
			"see anything new and only costs a threat scan per bot per tick.")]
		public readonly int AdaptiveEmergencyCheckInterval = 25;

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

		[Desc("Score for Tech while the economy is built out and earning (see AdaptiveTechCashTrendPerMinute).",
			"This is the profile's whole case for itself, so it has to be able to win: measured over 200",
			"evaluations, Expansion reached 9.1, Turtle 7.3 and Steamroller 6.8, while Tech - at the 2.5 this",
			"replaces - topped out at 1.5 and was never once chosen. The gating condition is deliberately",
			"strict (economy finished, not still expanding), so when it is met the profile should be a real",
			"contender rather than a rounding error.")]
		public readonly float AdaptiveTechCashRichBonus = 7f;

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
			"Prevents rapid oscillation: a profile has to beat the current one by this margin to trigger a " +
			"switch. Kept below 1.0 on purpose - at 2.0 it stopped being hysteresis and started deciding " +
			"which states were reachable at all. Two worked examples from the score table below: once the " +
			"danger passes, Turtle scores 0 but holds 2.0 against Expansion's 1.5, so it never leaves; and " +
			"a cash-rich bot scores Tech at 2.5 against a running Expansion's 1.5 + 2.0, so the income " +
			"signal the profile exists for could never trigger the switch it was written for. The right " +
			"value is a matter for a played match - this is the largest one that leaves both transitions " +
			"possible.")]
		public readonly float AdaptiveProfileMomentumBonus = 0.75f;

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

		// Re-evaluation cadence for the tech stage. One second against thresholds measured in thousands
		// of ticks: far finer than the thing being measured, and a stage that arrives a second late
		// changes nothing downstream.
		const int TechStageInterval = 25;

		readonly World world;
		readonly Player player;
		Actor playerActor;
		int techStageTicks = 1;
		int switchCooldown;
		int activeProfileSinceTick;
		int lastEvalEarned;
		int lastEvalCashTick;
		int emergencyCheckTicks;

		// High-water mark for UpdateTechStage - see there.
		TechStage highestTechStage = TechStage.Early;
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
			using var perfScope = CNBotPerf.Sample(bot, nameof(CNBotProfileBotModule));

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

			// Not every tick. The stage is decided by which buildings the bot owns and by tick thresholds
			// measured in thousands of ticks, and it can only ever move forward - highestTechStage sees
			// to that. Re-deciding it forty times a second meant scanning the whole building list once
			// or twice per bot per tick for an answer that changes a handful of times per match. This
			// module has no spikes at all, just a constant drip, and this was most of it.
			if (--techStageTicks <= 0)
			{
				techStageTicks = TechStageInterval;
				UpdateTechStage();
			}

			if (Info.Profile != BotProfile.Adaptive)
			{
				SwitchTo(Info.Profile, false);
				UpdateStrategySnapshot();
				return;
			}

			if (--switchCooldown > 0)
			{
				// Except for the emergency, which is a response to being attacked and cannot wait for the
				// evaluation cadence. SwitchTo lets it past the minimum intent hold, but the cooldown sits
				// in front of the whole evaluation - so a heavy assault beginning just after one pass left
				// the bot in Rush or Expansion for up to AdaptiveSwitchCooldownTicks with the emergency
				// threshold long since exceeded. Only the danger reading is taken here; the full five-
				// profile comparison stays on the long interval, where it belongs.
				// On its own clock, not every tick: the reading walks the threat list, sorts it and
				// allocates, while the danger memory it reads from only records an attack every 30 ticks
				// anyway. Checking oftener than the data can change buys nothing and costs a scan per bot
				// per tick - which is what the first version of this did.
				if (--emergencyCheckTicks <= 0)
				{
					emergencyCheckTicks = Math.Max(1, Info.AdaptiveEmergencyCheckInterval);
					if (TryEmergencyTurtle())
						switchCooldown = Info.AdaptiveSwitchCooldownTicks;
				}

				UpdateStrategySnapshot();
				return;
			}

			EvaluateAndSwitch();
			UpdateStrategySnapshot();
			switchCooldown = Info.AdaptiveSwitchCooldownTicks;
		}

		/// <summary>
		/// The danger reading on its own, and the switch to Turtle if it has gone past the emergency
		/// threshold. Split out of the full evaluation so it can run while that one is still on cooldown:
		/// an emergency is a response to being attacked, and waiting out the evaluation cadence to notice
		/// is the one thing it must not do. Returns whether it switched.
		/// </summary>
		bool TryEmergencyTurtle()
		{
			if (baseBuilder == null || ActiveProfile == BotProfile.Turtle)
				return false;

			// Reactive only. The combined placement list also carries doors, bridges and ramps, which are
			// constant on a map - an emergency triggered by terrain would fire on the first tick and never
			// clear.
			var dangerScore = baseBuilder.GetReactiveDangerScore(baseBuilder.DefenseCenter);

			LastDangerScore = dangerScore;
			if (dangerScore < Info.AdaptiveEmergencyTurtleDangerThreshold)
				return false;

			SwitchTo(BotProfile.Turtle, emergency: true);
			return true;
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

			// Never backwards. The tick thresholds come from the ACTIVE profile, so a switch swaps them
			// underneath an already-reached stage: a bot that hit Mid at 3000 under Tech and then turned
			// Turtle, whose threshold is 6000, dropped back to Early on the next tick unless it happened
			// to own a tech building. BuildingFractions and the rush offset then moved backwards with it.
			// Tech reached is a fact about what the bot has done, not about which profile it is running.
			if (ActiveTechStage < highestTechStage)
				ActiveTechStage = highestTechStage;
			else
				highestTechStage = ActiveTechStage;
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
			// Danger is what has actually been done to us; exposure is what the ground looks like. Summing
			// the combined placement list conflated the two: four bridges came to 528 on an untouched map,
			// past the 420 that turns a bot Turtle, so map shape alone could flip the profile - and
			// LastDangerScore is published to allies as this bot's danger, so it told the team it was under
			// attack as well. Kept apart now, and the static half is weighted in far below the live one.
			var dangerScore = baseBuilder.GetReactiveDangerScore(baseBuilder.DefenseCenter);
			var exposureScore = baseBuilder.GetStaticExposureScore(baseBuilder.DefenseCenter);

			LastDangerScore = dangerScore;

			// Emergency Turtle: bypass hysteresis entirely - but only to GET there.
			//
			// This used to return unconditionally, and that is what froze adaptive bots. Once a bot was
			// already turtling, every later evaluation hit this branch, did nothing (SwitchTo returns
			// immediately when the profile is unchanged) and left again before the five-profile
			// comparison below - which is the only thing that can ever take a bot back out of Turtle.
			// The danger score it would have to fall under first is a decaying memory of being shot at,
			// and in a busy match it sits near its cap indefinitely.
			// Measured over an 86-minute six-bot match: nine evaluations in total, all of them inside
			// the first twentieth of the match, none afterwards. Bots picked Expansion early, were
			// attacked once, and spent the rest of the game on Turtle thresholds - which is visible in
			// play as an expansion held at the Turtle cash requirement of 12000 by a bot whose last
			// recorded decision was Expansion.
			//
			// Letting the evaluation run while turtling is not a risk: danger is already an input to the
			// ordinary scoring, and Turtle rises with it, so a bot under real pressure keeps choosing
			// Turtle on merit rather than by having the alternatives hidden from it. A switch away is
			// additionally held for AdaptiveMinimumIntentHoldTicks, so this cannot oscillate quickly.
			if (dangerScore >= Info.AdaptiveEmergencyTurtleDangerThreshold && ActiveProfile != BotProfile.Turtle)
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

			// What the economy EARNS, not what the balance happens to do. Sampled between evaluations, so
			// the window is however long the last hold lasted; dividing by the elapsed ticks keeps the rate
			// comparable regardless.
			// Taken from Earned rather than from the balance, because the balance is income minus spending
			// and this is asked as an income question. A bot earning 3000 a minute and spending all 3000 on
			// production held a trend of zero and never cleared the threshold, so its tech score stayed
			// negative while its economy was in fact strong - and a one-off refund could make a dying
			// economy look rich from the other direction.
			var earned = playerResources.Earned;
			var cashTrendPerMinute = 0f;
			if (lastEvalCashTick > 0 && world.WorldTick > lastEvalCashTick)
			{
				var ticksPerMinute = 60000f / world.Timestep;
				cashTrendPerMinute = (earned - lastEvalEarned) * ticksPerMinute / (world.WorldTick - lastEvalCashTick);
			}

			lastEvalEarned = earned;
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

			// Exposed ground is a reason to lean defensive, just a much weaker one than being shot at, and
			// it must never on its own reach the threshold that live danger is measured against.
			var exposureRatio = Math.Min(1f, (float)exposureScore / Math.Max(1, Info.AdaptiveTurtleDangerThreshold))
				* Math.Max(0f, Info.AdaptiveStaticExposureWeight);

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
						dangerRatio * 3f + exposureRatio + (hasActiveThreat ? 2f : 0f),

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
						(cashRich && ActiveTechStage != TechStage.Late ? Info.AdaptiveTechCashRichBonus : -2f)
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

				// A defeated ally keeps no ground and fights nobody, but its last recorded danger and
				// coverage went on steering this bot's strategy to the end of the match.
				if (p.WinState != WinState.Undefined)
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
				$"{player.ResolvedPlayerName}: {from} → {to}{(emergency ? " (emergency)" : "")}");
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
