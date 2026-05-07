#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Frozen;
using System.Linq;
using OpenRA.Mods.CN.Traits.BotModules.Squads;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public enum BotProfile { Eco, Rush, Turtle, Tech, Expansion, Steamroller, Adaptive }
	public enum TechStage { Early, Mid, Late }

	public readonly struct CNBotStrategySnapshot
	{
		public readonly BotProfile Profile;
		public readonly TechStage TechStage;
		public readonly int EcoBudget;
		public readonly int TechBudget;
		public readonly int DefenseBudget;
		public readonly int ProductionBudget;
		public readonly int HarvesterTargetPercent;

		public CNBotStrategySnapshot(
			BotProfile profile,
			TechStage techStage,
			int ecoBudget,
			int techBudget,
			int defenseBudget,
			int productionBudget,
			int harvesterTargetPercent)
		{
			Profile = profile;
			TechStage = techStage;
			EcoBudget = ecoBudget;
			TechBudget = techBudget;
			DefenseBudget = defenseBudget;
			ProductionBudget = productionBudget;
			HarvesterTargetPercent = harvesterTargetPercent;
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

		[Desc("Cash reserve below this value (after grace period) triggers Eco mode.")]
		public readonly int AdaptiveEcoIncomeThreshold = 150;

		[Desc("Sum of defense danger hotspot weights above this triggers Turtle mode.")]
		public readonly int AdaptiveTurtleDangerThreshold = 350;

		[Desc("Total alive combat units across all squads above this triggers Rush mode (when not under attack).")]
		public readonly int AdaptiveRushUnitThreshold = 15;

		[Desc("Ticks between profile re-evaluations in Adaptive mode.")]
		public readonly int AdaptiveSwitchCooldownTicks = 1500;

		[Desc("World tick after which low-cash Eco mode can trigger. Prevents false positives in the opening.")]
		public readonly int AdaptiveEcoGraceTicks = 3000;

		[Desc("Added to RushUnitThreshold in Early tech stage (bot needs more units to consider rushing with basic troops).")]
		public readonly int RushUnitThresholdEarlyOffset = 5;

		[Desc("Added to RushUnitThreshold in Late tech stage (advanced units punch harder, lower threshold).")]
		public readonly int RushUnitThresholdLateOffset = -3;

		[Desc("Minimum ticks an adaptive bot keeps a non-emergency strategic intent before switching again.")]
		public readonly int AdaptiveMinimumIntentHoldTicks = 3000;

		[Desc("Danger score that immediately overrides adaptive hysteresis into Turtle.")]
		public readonly int AdaptiveEmergencyTurtleDangerThreshold = 600;

		[Desc("Earliest tick at which Adaptive may choose Steamroller.")]
		public readonly int AdaptiveSteamrollerEarliestTick = 7500;

		[Desc("Offensive unit count at which Adaptive may choose Steamroller.")]
		public readonly int AdaptiveSteamrollerUnitThreshold = 32;

		[Desc("Cash threshold at which Adaptive prefers Tech while not under immediate threat.")]
		public readonly int AdaptiveTechCashThreshold = 6500;

		[Desc("Fallback strategic intent for Adaptive when no stronger trigger is active. Eco is treated as Expansion.")]
		public readonly BotProfile AdaptiveFallbackProfile = BotProfile.Expansion;

		[Desc("Target world ticks for entering Mid tech stage. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> MidTechTicks = null;

		[Desc("Target world ticks for entering Late tech stage. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> LateTechTicks = null;

		[Desc("Default target world tick for entering Mid tech stage.")]
		public readonly int DefaultMidTechTick = 4500;

		[Desc("Default target world tick for entering Late tech stage.")]
		public readonly int DefaultLateTechTick = 12000;

		[Desc("Per-profile eco budget shares. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> EcoBudgets = null;

		[Desc("Per-profile tech budget shares. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> TechBudgets = null;

		[Desc("Per-profile defense budget shares. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> DefenseBudgets = null;

		[Desc("Per-profile production budget shares. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> ProductionBudgets = null;

		[Desc("Per-profile harvester target multiplier in percent. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> HarvesterTargetPercents = null;

		public override object Create(ActorInitializer init) { return new CNBotProfileBotModule(init.Self, this); }
	}

	public class CNBotProfileBotModule : ConditionalTrait<CNBotProfileBotModuleInfo>, IBotTick
	{
		public BotProfile ActiveProfile { get; private set; }
		public TechStage ActiveTechStage { get; private set; }
		public CNBotStrategySnapshot CurrentStrategy { get; private set; }

		readonly World world;
		readonly Player player;
		int switchCooldown;
		int activeProfileSinceTick;
		bool firstTick = true;

		CNBaseBuilderBotModule baseBuilder;
		CombatAnalysisBotModule combatAnalysis;
		CNSquadManagerBotModule squadManager;
		PlayerResources playerResources;

		public CNBotProfileBotModule(Actor self, CNBotProfileBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			ActiveProfile = ResolveInitialProfile(info.Profile, info.AdaptiveFallbackProfile);
			CurrentStrategy = CreateStrategySnapshot(ActiveProfile, ActiveTechStage);
			activeProfileSinceTick = world.WorldTick;
		}

		protected override void Created(Actor self)
		{
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (firstTick)
			{
				baseBuilder = bot.Player.PlayerActor
					.TraitsImplementing<CNBaseBuilderBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
				combatAnalysis = bot.Player.PlayerActor
					.TraitsImplementing<CombatAnalysisBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
				squadManager = bot.Player.PlayerActor
					.TraitsImplementing<CNSquadManagerBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
				firstTick = false;
			}

			UpdateTechStage();

			if (Info.Profile != BotProfile.Adaptive)
			{
				SwitchTo(ResolveInitialProfile(Info.Profile, Info.AdaptiveFallbackProfile), false);
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
			var midTick = GetProfileTick(Info.MidTechTicks, Info.DefaultMidTechTick);
			var lateTick = GetProfileTick(Info.LateTechTicks, Info.DefaultLateTechTick);

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

		void EvaluateAndSwitch()
		{
			if (baseBuilder == null)
				return;

			// 1. Reactive Turtle: remembered attack hotspots exceeded threshold.
			var dangerThreats = baseBuilder.GetDefensePlacementThreats(baseBuilder.DefenseCenter);
			var dangerScore = 0;
			foreach (var t in dangerThreats)
				dangerScore += t.Weight;

			if (dangerScore >= Info.AdaptiveTurtleDangerThreshold)
			{
				SwitchTo(BotProfile.Turtle, dangerScore >= Info.AdaptiveEmergencyTurtleDangerThreshold);
				return;
			}

			// 2. Proactive Turtle: CombatAnalysis detects enemy actively threatening.
			if (combatAnalysis != null && combatAnalysis.HasActiveThreat())
			{
				SwitchTo(BotProfile.Turtle, false);
				return;
			}

			if (world.WorldTick - activeProfileSinceTick < Info.AdaptiveMinimumIntentHoldTicks)
				return;

			// 3. Eco: cash-starved and the economy still needs attention.
			if (world.WorldTick > Info.AdaptiveEcoGraceTicks)
			{
				var cash = playerResources.GetCashAndResources();
				if (cash < Info.AdaptiveEcoIncomeThreshold &&
					(!baseBuilder.HasAdequateRefineryCount() || baseBuilder.ShouldExpandEconomy()))
				{
					SwitchTo(BotProfile.Expansion, false);
					return;
				}
			}

			// 4. Steamroller: large mid/late army with enough economy to keep growing.
			var offensiveUnits = CountOffensiveSquadUnits();
			if (world.WorldTick >= Info.AdaptiveSteamrollerEarliestTick &&
				offensiveUnits >= Info.AdaptiveSteamrollerUnitThreshold &&
				HasStableEconomy())
			{
				SwitchTo(BotProfile.Steamroller, false);
				return;
			}

			// 5. Tech: enough cash and a stable base but not yet late-game.
			if (ActiveTechStage != TechStage.Late &&
				playerResources.GetCashAndResources() >= Info.AdaptiveTechCashThreshold &&
				HasStableEconomy())
			{
				SwitchTo(BotProfile.Tech, false);
				return;
			}

			// 6. Rush: large enough army and no active threat.
			var rushThreshold = Info.AdaptiveRushUnitThreshold
				+ (ActiveTechStage == TechStage.Early ? Info.RushUnitThresholdEarlyOffset : 0)
				+ (ActiveTechStage == TechStage.Late ? Info.RushUnitThresholdLateOffset : 0);

			if (offensiveUnits >= rushThreshold)
			{
				SwitchTo(BotProfile.Rush, false);
				return;
			}

			SwitchTo(ResolveProfileAlias(Info.AdaptiveFallbackProfile), false);
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

		bool HasMinimalProductionBase()
		{
			return baseBuilder != null &&
				baseBuilder.HasMinimalRefineryCount() &&
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
			return new CNBotStrategySnapshot(
				profile,
				techStage,
				GetProfileValue(Info.EcoBudgets, profile, DefaultEcoBudget(profile)),
				GetProfileValue(Info.TechBudgets, profile, DefaultTechBudget(profile)),
				GetProfileValue(Info.DefenseBudgets, profile, DefaultDefenseBudget(profile)),
				GetProfileValue(Info.ProductionBudgets, profile, DefaultProductionBudget(profile)),
				GetProfileValue(Info.HarvesterTargetPercents, profile, DefaultHarvesterTargetPercent(profile)));
		}

		static int GetProfileValue(FrozenDictionary<string, int> values, BotProfile profile, int fallback)
		{
			if (values == null || values.Count == 0)
				return fallback;

			return values.TryGetValue(profile.ToString(), out var value) ? value : fallback;
		}

		static int DefaultEcoBudget(BotProfile profile)
		{
			return profile switch
			{
				BotProfile.Rush => 14,
				BotProfile.Turtle => 30,
				BotProfile.Tech => 22,
				BotProfile.Expansion => 34,
				BotProfile.Steamroller => 24,
				_ => 25
			};
		}

		static int DefaultTechBudget(BotProfile profile)
		{
			return profile switch
			{
				BotProfile.Rush => 8,
				BotProfile.Turtle => 12,
				BotProfile.Tech => 30,
				BotProfile.Expansion => 14,
				BotProfile.Steamroller => 14,
				_ => 15
			};
		}

		static int DefaultDefenseBudget(BotProfile profile)
		{
			return profile switch
			{
				BotProfile.Rush => 10,
				BotProfile.Turtle => 34,
				BotProfile.Tech => 24,
				BotProfile.Expansion => 20,
				BotProfile.Steamroller => 12,
				_ => 20
			};
		}

		static int DefaultProductionBudget(BotProfile profile)
		{
			return profile switch
			{
				BotProfile.Rush => 36,
				BotProfile.Turtle => 18,
				BotProfile.Tech => 24,
				BotProfile.Expansion => 22,
				BotProfile.Steamroller => 40,
				_ => 25
			};
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

		void SwitchTo(BotProfile nextProfile, bool emergency)
		{
			nextProfile = ResolveProfileAlias(nextProfile);
			if (!emergency && ActiveProfile == nextProfile)
				return;

			if (!emergency && Info.Profile == BotProfile.Adaptive &&
				world.WorldTick - activeProfileSinceTick < Info.AdaptiveMinimumIntentHoldTicks)
				return;

			if (ActiveProfile != nextProfile)
			{
				ActiveProfile = nextProfile;
				activeProfileSinceTick = world.WorldTick;
			}
		}

		static BotProfile ResolveInitialProfile(BotProfile configuredProfile, BotProfile fallbackProfile)
		{
			return configuredProfile == BotProfile.Adaptive ? ResolveProfileAlias(fallbackProfile) : ResolveProfileAlias(configuredProfile);
		}

		static BotProfile ResolveProfileAlias(BotProfile profile)
		{
			return profile == BotProfile.Eco || profile == BotProfile.Adaptive ? BotProfile.Expansion : profile;
		}

		static bool IsOffensiveSquad(CNSquadType type)
		{
			return type == CNSquadType.Assault ||
				type == CNSquadType.Rush ||
				type == CNSquadType.Raider ||
				type == CNSquadType.Hover ||
				type == CNSquadType.JumpJet ||
				type == CNSquadType.Stealth ||
				type == CNSquadType.SubterraneanAssault ||
				type == CNSquadType.AircraftAttack ||
				type == CNSquadType.AirTransport;
		}
	}
}
