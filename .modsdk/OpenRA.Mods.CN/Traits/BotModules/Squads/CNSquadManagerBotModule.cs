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
using OpenRA.Mods.CN.Traits.BotModules.Squads.States;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
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

		[Desc("After unloading, carrier returns to base. False = stays and fights.")]
		public readonly bool ReturnAfterUnload = true;

		[Desc("Restrict this slot to specific factions (empty = all factions).")]
		public readonly string[] Factions = [];
	}

	// ---------------------------------------------------------------------------
	// YAML Info: Team Template
	// ---------------------------------------------------------------------------

	public class CNTeamTemplateInfo
	{
		[Desc("Squad behavior type for this team.")]
		public readonly CNSquadType Role;

		[Desc("Higher priority templates get first pick of available units.")]
		public readonly int Priority = 50;

		[Desc("Maximum number of simultaneous active squads of this template.")]
		public readonly int MaxInstances = 1;

		[Desc("Number of non-optional slots that must be filled to activate.")]
		public readonly int MinSlotsToActivate = 1;

		[Desc("This squad attaches to (follows) active squads of these types.")]
		public readonly CNSquadType[] AttachToRole = [];

		[Desc("Preferred target capability tags in priority order (first match wins). " +
			"Matches actors that have BotCapabilities: <tag>. Applies to Raider, Stealth, SubAssault, JumpJet, and Hover roles.")]
		public readonly string[] PriorityTargetCapabilities = [];

		[Desc("Restrict template to specific factions (empty = all factions).")]
		public readonly string[] Factions = [];

		[Desc("If true, units in active squads of this template can be poached by higher-priority templates.")]
		public readonly bool Poachable = false;

		[Desc("When set, limits MaxInstances based on how many of this building type the player owns. " +
			"Effective MaxInstances = min(MaxInstances, max(1, buildingCount * UnitsPerBuilding / unitsPerSquad)) when buildingCount > 0, else 0.")]
		public readonly string ScaleWithBuilding = null;

		[Desc("Number of units from this squad that each ScaleWithBuilding instance can support. " +
			"Used only when ScaleWithBuilding is set.")]
		public readonly int UnitsPerBuilding = 1;

		[Desc("Priority bonus when a visible enemy unit has the given capability tag (respects shroud). " +
			"Example: Aircraft: 50 adds +50 when a visible enemy has BotCapabilities: Aircraft.")]
		public readonly Dictionary<string, int> ThreatBonuses = [];

		[Desc("Priority bonus when any enemy unit on the map has the given capability tag (ignores shroud). " +
			"Use for tags that are never visible, e.g. Cloaked.")]
		public readonly Dictionary<string, int> ThreatBonusesGlobal = [];

		[Desc("Priority bonus multiplied by the count of visible enemy units with the given capability tag.")]
		public readonly Dictionary<string, int> ThreatBonusesPerUnit = [];

		[Desc("Priority bonus multiplied by the count of enemy units on the map with the given capability tag (ignores shroud).")]
		public readonly Dictionary<string, int> ThreatBonusesGlobalPerUnit = [];

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

		[Desc("Delay (ticks) between rush attack attempts.")]
		public readonly int RushInterval = 600;

		[Desc("Delay (ticks) between removing dead units from squad bookkeeping.")]
		public readonly int CleanupInterval = 10;

		[Desc("Delay (ticks) between enemy threat scans for ThreatBonuses. 0 = disabled.")]
		public readonly int ThreatScanInterval = 150;

		[Desc("If true, ThreatBonuses only consider units belonging to the current nemesis player. " +
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

		[Desc("Per-role priority modifiers while the active strategy profile is Expansion/Eco.")]
		public readonly FrozenDictionary<string, int> EcoRolePriorityModifiers = null;

		[Desc("Per-role priority modifiers while the active strategy profile is Rush.")]
		public readonly FrozenDictionary<string, int> RushRolePriorityModifiers = null;

		[Desc("Per-role priority modifiers while the active strategy profile is Turtle.")]
		public readonly FrozenDictionary<string, int> TurtleRolePriorityModifiers = null;

		[Desc("Per-role priority modifiers while the active strategy profile is Expansion.")]
		public readonly FrozenDictionary<string, int> ExpansionRolePriorityModifiers = null;

		[Desc("Per-role priority modifiers while the active strategy profile is Tech.")]
		public readonly FrozenDictionary<string, int> TechRolePriorityModifiers = null;

		[Desc("Per-role priority modifiers while the active strategy profile is Steamroller.")]
		public readonly FrozenDictionary<string, int> SteamrollerRolePriorityModifiers = null;

		[Desc("Target role shares while the active strategy profile is Rush.")]
		public readonly FrozenDictionary<string, int> RushRoleTargetShares = null;

		[Desc("Target role shares while the active strategy profile is Turtle.")]
		public readonly FrozenDictionary<string, int> TurtleRoleTargetShares = null;

		[Desc("Target role shares while the active strategy profile is Expansion.")]
		public readonly FrozenDictionary<string, int> ExpansionRoleTargetShares = null;

		[Desc("Target role shares while the active strategy profile is Tech.")]
		public readonly FrozenDictionary<string, int> TechRoleTargetShares = null;

		[Desc("Target role shares while the active strategy profile is Steamroller.")]
		public readonly FrozenDictionary<string, int> SteamrollerRoleTargetShares = null;

		[Desc("Minimum total units in attack-wave roles before attack squads leave base. Keys are BotProfile names.")]
		public readonly FrozenDictionary<string, int> AttackWaveMinUnits = null;

		[Desc("Maximum ticks a newly created attack squad waits for its profile's attack wave.")]
		public readonly FrozenDictionary<string, int> AttackWaveMaxHoldTicks = null;

		[Desc("For Steamroller, grow AttackWaveMinUnits by this interval in world ticks. 0 = disabled.")]
		public readonly int SteamrollerAttackWaveMinUnitGrowthInterval = 0;

		[Desc("For Steamroller, add this many units to AttackWaveMinUnits per growth interval.")]
		public readonly int SteamrollerAttackWaveMinUnitGrowthAmount = 0;

		[Desc("Enemy target types to never attack.")]
		public readonly BitSet<TargetableType> IgnoredEnemyTargetTypes;

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
		public readonly World World;
		public readonly Player Player;
		public new readonly CNSquadManagerBotModuleInfo Info;

		public readonly List<CNSquad> Squads = [];

		// Units currently managed by a squad
		readonly HashSet<Actor> activeUnits = [];

		// Ticking counters
		int assignRolesTicks;
		int attackForceTicks;
		int rushTicks;
		int minAttackForceDelayTicks;
		int cleanupTicks;
		int threatScanTicks;

		// Sorted by effective priority (base + active threat bonuses).
		IReadOnlyList<KeyValuePair<string, CNTeamTemplateInfo>> orderedTeams = [];

		// Union of all ThreatBonus/ThreatBonusesGlobal keys — built once at enable.
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

		// Per-tick building caches — one Building scan each per world tick, shared across all squads.
		IReadOnlyList<Actor> cachedOwnBuildings = [];
		int cachedOwnBuildingsTick = -1;
		IReadOnlyList<Actor> cachedEnemyBuildings = [];
		int cachedEnemyBuildingsTick = -1;

		// Reactive defense — tracks multiple simultaneous attackers
		readonly List<Actor> recentAttackers = [];
		const int MaxTrackedAttackers = 4;
		int respondToAttackCooldown;
		const int MaxRespondToAttackCooldown = 30;

		// Nemesis system
		CombatAnalysisBotModule combatAnalysis;
		CNBotProfileBotModule profileModule;

		CPos initialBaseCenter;

		public CNSquadManagerBotModule(Actor self, CNSquadManagerBotModuleInfo info)
			: base(info)
		{
			Info = info;
			World = self.World;
			Player = self.Owner;
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
			profileModule = Player.PlayerActor
				.TraitsImplementing<CNBotProfileBotModule>()
				.FirstOrDefault(t => t.IsTraitEnabled());

			foreach (var (_, template) in Info.Teams)
			{
				foreach (var tag in template.ThreatBonuses.Keys)
					allTrackedVisibleTags.Add(tag);
				foreach (var tag in template.ThreatBonusesGlobal.Keys)
					allTrackedGlobalTags.Add(tag);
				foreach (var tag in template.ThreatBonusesPerUnit.Keys)
					allTrackedVisiblePerUnitTags.Add(tag);
				foreach (var tag in template.ThreatBonusesGlobalPerUnit.Keys)
					allTrackedGlobalPerUnitTags.Add(tag);
			}

			RebuildOrderedTeams();

			var random = World.LocalRandom;
			assignRolesTicks = random.Next(0, Info.AssignRolesInterval);
			attackForceTicks = random.Next(0, Info.AttackForceInterval);
			rushTicks = random.Next(Info.RushInterval / 2, Info.RushInterval);
			minAttackForceDelayTicks = random.Next(0, Info.MinimumAttackForceDelay + 1);
			cleanupTicks = random.Next(0, CleanupInterval);
		}

		void IBotTick.BotTick(IBot bot)
		{
			if ((allTrackedVisibleTags.Count > 0 || allTrackedGlobalTags.Count > 0 ||
		     allTrackedVisiblePerUnitTags.Count > 0 || allTrackedGlobalPerUnitTags.Count > 0) && --threatScanTicks <= 0)
			{
				threatScanTicks = Info.ThreatScanInterval;
				UpdateThreatTags();
			}

			if (--cleanupTicks <= 0)
			{
				cleanupTicks = CleanupInterval;
				foreach (var squad in Squads)
					PurgeDeadUnits(squad);

				CleanSquads();
				activeUnits.RemoveWhere(a => a == null || a.IsDead || !a.IsInWorld);
			}

			if (--attackForceTicks <= 0)
			{
				attackForceTicks = Info.AttackForceInterval;
				foreach (var squad in Squads.ToList())
					squad.Update();
			}

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

			var protectSquad = Squads.FirstOrDefault(s =>
				s.Type == CNSquadType.Protection && s.IsValid && !s.IsTemplateBacked);

			if (protectSquad == null)
			{
				protectSquad = RegisterSquad(bot, CNSquadType.Protection);
				InitializeSquadState(protectSquad);
			}

			protectSquad.SetActorToTarget(attacker);

			var idleUnits = World.ActorsHavingTrait<Mobile>()
				.Where(a => a.Owner == Player &&
				            !a.IsDead &&
				            a.IsInWorld &&
				            !activeUnits.Contains(a) &&
				            !a.Info.HasTraitInfo<MobSpawnerSlaveInfo>() &&
				            a.Info.HasTraitInfo<AttackBaseInfo>())
				.Take(8)
				.ToList();

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
			RebuildOrderedTeams();

			var demand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			foreach (var squad in Squads)
			{
				if (!squad.IsValid || squad.TemplateInfo == null)
					continue;

				foreach (var assignment in squad.SlotAssignments)
				{
					if (assignment.SlotInfo.IsPassenger && !SquadHasLiveCarrier(squad))
						continue;

					var missing = assignment.MissingCount;
					if (missing <= 0)
						continue;

						AddPreferredDemand(
							demand,
							assignment.SlotInfo,
							GetDemandScore(squad.TemplateInfo, assignment.SlotInfo, missing, true),
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
							GetDemandScore(template, slot, slot.Count, false),
							existingByType);
					}
				}
			}

			return demand;
		}

		int GetEffectiveMaxInstances(CNTeamTemplateInfo template)
		{
			if (template.ScaleWithBuilding == null)
				return template.MaxInstances;

			var buildingCount = GetCachedOwnBuildings().Count(a => a.Info.Name == template.ScaleWithBuilding);
			if (buildingCount <= 0)
				return 0;

			var unitsPerSquad = template.Slots.Values.Where(s => !s.Optional).Sum(s => s.Count);
			var scaledMax = unitsPerSquad > 0
				? Math.Max(1, buildingCount * template.UnitsPerBuilding / unitsPerSquad)
				: template.MaxInstances;

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

		int GetDemandScore(CNTeamTemplateInfo template, CNSlotInfo slot, int missingCount, bool existingSquad)
		{
			var score = EffectivePriority(template) * 100 + missingCount;

			if (!slot.Optional)
				score += 40;

			if (slot.IsCarrier || slot.IsAircraftCarrier)
				score += 120;
			else if (slot.IsPassenger)
				score += 80;

			if (existingSquad)
				score += 200;

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

			RebuildOrderedTeams();

			var claimedThisPass = new HashSet<Actor>();
			var availableByType = BuildAvailableUnitsByType(idleUnits, claimedThisPass);

			ReinforceExistingSquads(claimedThisPass, availableByType);

			var templateCounts = BuildTemplateSquadCounts();
			foreach (var (templateName, template) in OrderedTemplates())
			{
				if (!TemplateAppliesToFaction(template))
					continue;
				if (templateCounts.GetValueOrDefault(templateName) >= GetEffectiveMaxInstances(template))
					continue;

				var assignments = TryFillSlots(template, availableByType, claimedThisPass);
				if (assignments == null)
					continue;

				var fulfilledCount = assignments.Count(a => a.IsFulfilled && !a.SlotInfo.Optional);
				if (fulfilledCount < template.MinSlotsToActivate)
					continue;

				var squad = RegisterSquad(bot, template.Role, templateName, template);
				squad.ArtilleryHangBackRange = WDist.FromCells(Info.ArtilleryHangBackCells);

				foreach (var assignment in assignments)
				{
					squad.SlotAssignments.Add(assignment);
					ApplyAssignmentToSquad(squad, assignment, claimedThisPass);
				}

				if (template.AttachToRole.Length > 0)
				{
					var attachTarget = Squads.FirstOrDefault(s =>
						s != squad &&
						s.IsValid &&
						template.AttachToRole.Contains(s.Type));
					squad.AttachedTo = attachTarget;
				}

				InitializeSquadState(squad);
			}

			if (claimedThisPass.Count > 0)
				EvictPoached(claimedThisPass);
		}

		void ReinforceExistingSquads(
			HashSet<Actor> claimedThisPass,
			Dictionary<string, List<Actor>> availableByType)
		{
			var prioritizedSquads = new List<CNSquad>();
			foreach (var squad in Squads)
			{
				if (!squad.IsValid || squad.TemplateInfo == null)
					continue;

				prioritizedSquads.Add(squad);
			}

			prioritizedSquads.Sort(CompareReinforcementPriority);

			foreach (var squad in prioritizedSquads)
			{
				foreach (var assignment in EnumerateAssignmentsForReinforcement(squad))
				{
					var missing = assignment.MissingCount;
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

				foreach (var actor in assignment.Units)
					assignment.Passengers.Add(actor);
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

			foreach (var actor in TakeAvailableUnits(slotInfo, availableByType, alreadyClaimed, slotInfo.Count, localClaimed))
				assignment.Units.Add(actor);

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

		Dictionary<string, List<Actor>> BuildAvailableUnitsByType(IEnumerable<Actor> units, HashSet<Actor> alreadyClaimed)
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

		IEnumerable<KeyValuePair<string, CNTeamTemplateInfo>> OrderedTemplates() => orderedTeams;

		public int GetEffectivePriority(CNTeamTemplateInfo template) => EffectivePriority(template);

		void RebuildOrderedTeams()
		{
			orderedTeams = Info.Teams
				.OrderByDescending(kv => EffectivePriority(kv.Value))
				.ToList();
		}

		int EffectivePriority(CNTeamTemplateInfo template)
		{
			var bonus = 0;
			bonus += GetProfileRolePriorityModifier(template.Role);
			bonus += GetProfileRoleTargetShareModifier(template.Role);

			foreach (var (tag, value) in template.ThreatBonuses)
				if (activeVisibleThreatTags.Contains(tag))
					bonus += value;
			foreach (var (tag, value) in template.ThreatBonusesGlobal)
				if (activeGlobalThreatTags.Contains(tag))
					bonus += value;
			foreach (var (tag, value) in template.ThreatBonusesPerUnit)
				if (activeVisibleThreatCounts.TryGetValue(tag, out var vCount))
					bonus += value * vCount;
			foreach (var (tag, value) in template.ThreatBonusesGlobalPerUnit)
				if (activeGlobalThreatCounts.TryGetValue(tag, out var gCount))
					bonus += value * gCount;
			return template.Priority + bonus;
		}

		int GetProfileRolePriorityModifier(CNSquadType role)
		{
			var map = ActiveProfile() switch
			{
				BotProfile.Rush => Info.RushRolePriorityModifiers,
				BotProfile.Turtle => Info.TurtleRolePriorityModifiers,
				BotProfile.Tech => Info.TechRolePriorityModifiers,
				BotProfile.Steamroller => Info.SteamrollerRolePriorityModifiers,
				BotProfile.Expansion or BotProfile.Eco => Info.ExpansionRolePriorityModifiers ?? Info.EcoRolePriorityModifiers,
				_ => null
			};

			return TryGetRoleValue(map, role, out var value) ? value : 0;
		}

		int GetProfileRoleTargetShareModifier(CNSquadType role)
		{
			var shares = GetProfileRoleTargetShares();
			if (shares == null || shares.Count == 0)
				return 0;

			if (!TryGetRoleValue(shares, role, out var targetShare))
				targetShare = 0;

			var activeTemplateSquads = Squads.Count(s => s.IsValid && s.IsTemplateBacked);
			if (activeTemplateSquads == 0)
				return targetShare > 0 ? Math.Min(30, targetShare) : -10;

			var activeRoleSquads = Squads.Count(s => s.IsValid && s.IsTemplateBacked && s.Type == role);
			if (targetShare <= 0)
				return activeTemplateSquads >= 3 ? -35 : -10;

			var desiredSquads = Math.Max(1, (activeTemplateSquads + 1) * targetShare / 100);
			if (activeRoleSquads < desiredSquads)
				return Math.Min(50, (desiredSquads - activeRoleSquads) * 18 + targetShare / 3);

			var currentShare = activeRoleSquads * 100 / activeTemplateSquads;
			if (currentShare > targetShare + 10)
				return -Math.Min(60, (currentShare - targetShare) * 2);

			return Math.Min(12, targetShare / 4);
		}

		FrozenDictionary<string, int> GetProfileRoleTargetShares()
		{
			return ActiveProfile() switch
			{
				BotProfile.Rush => Info.RushRoleTargetShares,
				BotProfile.Turtle => Info.TurtleRoleTargetShares,
				BotProfile.Tech => Info.TechRoleTargetShares,
				BotProfile.Steamroller => Info.SteamrollerRoleTargetShares,
				BotProfile.Expansion or BotProfile.Eco => Info.ExpansionRoleTargetShares,
				_ => null
			};
		}

		BotProfile ActiveProfile()
		{
			profileModule ??= Player.PlayerActor
				.TraitsImplementing<CNBotProfileBotModule>()
				.FirstOrDefault(t => t.IsTraitEnabled());

			return profileModule?.ActiveProfile ?? BotProfile.Adaptive;
		}

		static bool TryGetRoleValue(FrozenDictionary<string, int> map, CNSquadType role, out int value)
		{
			value = 0;
			return map != null &&
				(map.TryGetValue(role.ToString(), out value) ||
				 map.TryGetValue(role.ToString().ToLowerInvariant(), out value));
		}

		public bool ShouldHoldForAttackWave(CNSquad squad)
		{
			if (squad == null || !squad.IsOperational || !IsAttackWaveRole(squad.Type))
				return false;

			var minUnits = GetAttackWaveMinUnits();
			if (minUnits <= 0 || AttackWaveUnitCount() >= minUnits)
				return false;

			var maxHoldTicks = GetProfileInt(Info.AttackWaveMaxHoldTicks, 0);
			if (maxHoldTicks <= 0)
				return true;

			return World.WorldTick - squad.CreatedTick < maxHoldTicks;
		}

		public void GatherAttackWave(CNSquad squad)
		{
			if (squad == null || !squad.IsValid)
				return;

			var rally = World.Map.CenterOfCell(GetRandomBaseCenter());
			squad.Bot.QueueOrder(new Order("Move", null, Target.FromPos(rally), false,
				groupedActors: squad.OrderableUnits.ToArray()));
		}

		int AttackWaveUnitCount()
		{
			var count = 0;
			foreach (var squad in Squads)
			{
				if (!squad.IsValid || !squad.IsOperational || !IsAttackWaveRole(squad.Type))
					continue;

				count += squad.OrderableUnits.Count();
			}

			return count;
		}

		int GetAttackWaveMinUnits()
		{
			var minUnits = GetProfileInt(Info.AttackWaveMinUnits, 0);
			if (minUnits <= 0 || ActiveProfile() != BotProfile.Steamroller ||
				Info.SteamrollerAttackWaveMinUnitGrowthInterval <= 0 ||
				Info.SteamrollerAttackWaveMinUnitGrowthAmount <= 0)
				return minUnits;

			return minUnits +
				World.WorldTick / Info.SteamrollerAttackWaveMinUnitGrowthInterval *
				Info.SteamrollerAttackWaveMinUnitGrowthAmount;
		}

		int GetProfileInt(FrozenDictionary<string, int> values, int fallback)
		{
			if (values == null)
				return fallback;

			var profile = ActiveProfile();
			return values.TryGetValue(profile.ToString(), out var value) ||
				values.TryGetValue(profile.ToString().ToLowerInvariant(), out value)
					? value
					: fallback;
		}

		static bool IsAttackWaveRole(CNSquadType role)
		{
			return role == CNSquadType.Assault ||
				role == CNSquadType.Rush ||
				role == CNSquadType.ArtilleryAssault;
		}

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
				RebuildOrderedTeams();
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

		void ApplyAssignmentToSquad(CNSquad squad, CNSlotAssignment assignment, HashSet<Actor> claimedThisPass)
		{
			foreach (var unit in assignment.Units)
			{
				squad.Units.Add(unit);
				activeUnits.Add(unit);
				claimedThisPass.Add(unit);
			}

			foreach (var passenger in assignment.Passengers)
			{
				activeUnits.Add(passenger);
				claimedThisPass.Add(passenger);
			}
		}

		void PurgeDeadUnits(CNSquad squad)
		{
			squad.Units.RemoveWhere(a => a == null || a.IsDead || !a.IsInWorld);
			foreach (var assignment in squad.SlotAssignments)
			{
				assignment.Units.RemoveAll(a => a == null || a.IsDead || !a.IsInWorld);
				assignment.Passengers.RemoveAll(a => a == null || a.IsDead || !a.IsInWorld);
			}
		}

		void InitializeSquadState(CNSquad squad)
		{
			switch (squad.Type)
			{
				case CNSquadType.Assault:
				case CNSquadType.Rush:
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
					break;
				case CNSquadType.ArtilleryAssault:
				case CNSquadType.ArtilleryDefense:
					squad.FuzzyStateMachine.ChangeState(squad, new ArtilleryIdleState());
					break;
				case CNSquadType.Protection:
					squad.FuzzyStateMachine.ChangeState(squad, new ProtectionIdleState());
					break;
				case CNSquadType.Defense:
					squad.FuzzyStateMachine.ChangeState(squad, new DefenseIdleState());
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
				case CNSquadType.AircraftSupport:
					squad.FuzzyStateMachine.ChangeState(squad, new AircraftSupportIdleState());
					break;
				case CNSquadType.JumpJet:
				case CNSquadType.Hover:
				case CNSquadType.Air:
				case CNSquadType.Naval:
				default:
					squad.FuzzyStateMachine.ChangeState(squad, new CNGroundIdleState());
					break;
			}
		}

		public CNSquad RegisterSquad(
			IBot bot,
			CNSquadType type,
			string templateName = null,
			CNTeamTemplateInfo templateInfo = null)
		{
			var squad = new CNSquad(bot, this, type, templateName, templateInfo)
			{
				ArtilleryHangBackRange = WDist.FromCells(Info.ArtilleryHangBackCells)
			};

			Squads.Add(squad);
			return squad;
		}

		public void UnregisterSquad(CNSquad squad)
		{
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

		public Actor FindClosestEnemy(Actor sourceActor)
		{
			if (sourceActor == null)
				return null;

			var nemesis = combatAnalysis?.GetNemesis();
			if (nemesis != null)
			{
				var nemesisTarget = FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius), nemesis)
					?? FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius * 4), nemesis);
				if (nemesisTarget != null)
					return nemesisTarget;
			}

			return FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius))
				?? FindClosestEnemy(sourceActor, WDist.FromCells(Info.AttackScanRadius * 4));
		}

		public Actor FindClosestEnemy(Actor sourceActor, WDist radius)
		{
			if (sourceActor == null)
				return null;

			return World.FindActorsInCircle(sourceActor.CenterPosition, radius)
				.Where(a => IsPreferredEnemyUnit(a) && a.CanBeViewedByPlayer(Player))
				.MinByOrDefault(a => (a.CenterPosition - sourceActor.CenterPosition).LengthSquared);
		}

		Actor FindClosestEnemy(Actor sourceActor, WDist radius, Player preferredOwner)
		{
			if (sourceActor == null)
				return null;

			return World.FindActorsInCircle(sourceActor.CenterPosition, radius)
				.Where(a => IsPreferredEnemyUnit(a) &&
				            a.CanBeViewedByPlayer(Player) &&
				            a.Owner == preferredOwner)
				.MinByOrDefault(a => (a.CenterPosition - sourceActor.CenterPosition).LengthSquared);
		}

		public Actor FindClosestEnemyBuilding(Actor sourceActor)
		{
			if (sourceActor == null)
				return null;

			var enemyBuildings = GetCachedEnemyBuildings();

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

		void EvictPoached(HashSet<Actor> claimed)
		{
			foreach (var squad in Squads.Where(s => s.TemplateInfo?.Poachable == true).ToList())
			{
				foreach (var actor in claimed)
					RemoveActorFromSquad(squad, actor);
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

		static int CompareReinforcementPriority(CNSquad a, CNSquad b)
		{
			var priority = b.TemplateInfo.Priority.CompareTo(a.TemplateInfo.Priority);
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
