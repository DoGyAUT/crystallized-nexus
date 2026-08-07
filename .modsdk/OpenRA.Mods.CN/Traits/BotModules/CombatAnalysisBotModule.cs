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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Tracks enemy attack patterns and exposes per-role threat weights and a nemesis player.",
		"Used by CNBaseBuilderBotModule and CNSquadManagerBotModule.")]
	public class CombatAnalysisBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Target type strings that classify an attacker as an air threat (maps to AA defense role).")]
		public readonly FrozenSet<string> AirTargetTypes = new HashSet<string> { "Air" }.ToFrozenSet();

		[Desc("Target type strings that classify an attacker as an infantry threat (maps to AntiInf defense role).")]
		public readonly FrozenSet<string> InfantryTargetTypes = new HashSet<string> { "Infantry" }.ToFrozenSet();

		[Desc("Target type strings that classify an attacker as a vehicle threat (maps to AntiVehicle defense role).")]
		public readonly FrozenSet<string> VehicleTargetTypes = new HashSet<string> { "Vehicle", "Tank" }.ToFrozenSet();

		[Desc("Base threat weight added per qualifying damage event, before value- and category-scaling.")]
		public readonly float WeightPerHit = 10f;

		[Desc("Attacker cost (ValuedInfo.Cost) at which the value-based weight multiplier is 1.0.",
			"Cheaper attackers scale down towards MinValueWeightMultiplier, pricier ones scale up towards MaxValueWeightMultiplier.")]
		public readonly float ValueWeightReference = 1000f;

		[Desc("Floor for the value-based weight multiplier, so a cheap attacker's hits still register instead of scaling away to nothing.")]
		public readonly float MinValueWeightMultiplier = 0.3f;

		[Desc("Ceiling for the value-based weight multiplier, so a single hit from an expensive attacker cannot saturate a threat role by itself.")]
		public readonly float MaxValueWeightMultiplier = 3f;

		[Desc("Bot capability tags (BotCapabilitiesInfo.CapabilitySet) that mark a defended actor as an economy target,",
			"tracked at full weight like buildings. Mirrors the tags CNGroundStates.ScoreRushTarget already treats as economy targets.")]
		public readonly FrozenSet<string> EconomyCapabilities = new HashSet<string> { "Harvester", "Economy" }.ToFrozenSet();

		[Desc("Fraction of the (value-scaled) weight recorded when a combat unit — neither a building nor an economy actor — is hit,",
			"versus the full weight for buildings/economy. Set to 0 to ignore combat-unit hits entirely.")]
		public readonly float CombatUnitWeightFraction = 0.275f;

		[Desc("Minimum ticks between recording direct threat-role damage events against buildings or economy actors.")]
		public readonly int ThreatRecordInterval = 25;

		[Desc("Minimum ticks between recording threat-role damage events against combat units.",
			"Tracked on its own counter from ThreatRecordInterval: combat units get hit far more often than",
			"buildings/economy, and a shared counter would let that noise crowd out the rarer, more important signal.")]
		public readonly int CombatUnitThreatRecordInterval = 25;

		[Desc("Minimum raw damage value for a hit to count as a threat (filters negligible hits).")]
		public readonly int MinDamageThreshold = 1;

		[Desc("Minimum accumulated threat weight required to trigger reactive defense building.")]
		public readonly float ReactThreshold = 20f;

		[Desc("Fraction of weight removed per decay interval (0.0-1.0). 0.1 = lose 10% every interval.")]
		public readonly float DecayRate = 0.1f;

		[Desc("Interval in ticks between weight decay steps.")]
		public readonly int DecayInterval = 1000;

		[Desc("Nemesis score added when an enemy attacks one of our units or buildings.")]
		public readonly float NemesisWeightPerHit = 5f;

		[Desc("Minimum ticks between recording nemesis damage events.")]
		public readonly int NemesisRecordInterval = 25;

		[Desc("Additional nemesis score added when an enemy attacks an ally (not us).")]
		public readonly float NemesisAllyWeightPerHit = 2f;

		[Desc("Minimum nemesis score required before a player is considered the nemesis.")]
		public readonly float NemesisThreshold = 15f;

		public override object Create(ActorInitializer init) { return new CombatAnalysisBotModule(init.Self, this); }
	}

	public class CombatAnalysisBotModule : ConditionalTrait<CombatAnalysisBotModuleInfo>, IBotTick, IBotRespondToAttack
	{
		readonly Dictionary<DefenseRole, float> weights = new()
		{
			[DefenseRole.InfantryDefense] = 0f,
			[DefenseRole.ArmorDefense] = 0f,
			[DefenseRole.AADefense] = 0f,
			[DefenseRole.ArtilleryDefense] = 0f,

			// No SpecialDefense entry: ClassifyAttacker only ever yields AADefense, InfantryDefense
			// or ArmorDefense, so its weight could never leave zero and GetHighestThreatRole could
			// never name it. It is a worth marker, not a threat something attacks with.
		};

		// Per-enemy-player nemesis score: higher = this player attacked us more
		readonly Dictionary<Player, float> nemesisScores = [];
		readonly Dictionary<string, DefenseRole> attackerRoleCache = [];
		readonly Dictionary<string, bool> economyActorCache = [];
		readonly Dictionary<string, float> attackerValueMultiplierCache = [];

		readonly Player self;
		readonly World world;
		int decayTicks;
		int nextThreatRecordTick;
		int nextCombatUnitThreatRecordTick;
		int nextNemesisRecordTick;

		public CombatAnalysisBotModule(Actor self, CombatAnalysisBotModuleInfo info)
			: base(info)
		{
			this.self = self.Owner;
			world = self.World;
		}

		public float GetThreatWeight(DefenseRole role) =>
			weights.TryGetValue(role, out var w) ? w : 0f;

		public bool HasActiveThreat() =>
			weights.Values.Any(w => w >= Info.ReactThreshold);

		/// <summary>
		/// How hard this role is currently being pressed, from 0 at ReactThreshold to 1 once the
		/// weight has grown to <paramref name="saturationFactor"/> times the threshold. Returns 0
		/// while the role is not an active threat at all.
		/// <para>
		/// A ramp rather than a switch, so a single raid and a sustained assault are told apart, and
		/// callers scaling something by it fall back on their own as BotTick decays the weights.
		/// </para>
		/// </summary>
		public float GetThreatIntensity(DefenseRole role, float saturationFactor)
		{
			var threshold = Info.ReactThreshold;
			if (threshold <= 0f)
				return 0f;

			var weight = GetThreatWeight(role);
			if (weight < threshold)
				return 0f;

			var saturation = threshold * Math.Max(1f, saturationFactor);
			if (saturation <= threshold)
				return 1f;

			return Math.Clamp((weight - threshold) / (saturation - threshold), 0f, 1f);
		}

		/// <summary>Returns the role with the highest threat weight at or above ReactThreshold, or Default if none.</summary>
		public DefenseRole GetHighestThreatRole()
		{
			// Seeded just below ReactThreshold, not at ReactThreshold - 1: the old seed let a role
			// one point under the threshold win, so this reported an active role while HasActiveThreat()
			// (which tests >= ReactThreshold) still said there was no threat. Callers like
			// CNUnitBuilderBotModule.BuildPanicUnit gate on one and pick with the other.
			var bestRole = DefenseRole.Default;
			var bestWeight = 0f;
			foreach (var kv in weights)
			{
				if (kv.Value < Info.ReactThreshold || kv.Value <= bestWeight)
					continue;

				bestWeight = kv.Value;
				bestRole = kv.Key;
			}

			return bestRole;
		}

		/// <summary>
		/// Returns the enemy player who has attacked us (or our allies) the most.
		/// Returns null if no player has crossed NemesisThreshold yet.
		/// </summary>
		public Player GetNemesis()
		{
			Player nemesis = null;
			var best = Info.NemesisThreshold - 1f;
			foreach (var kv in nemesisScores)
			{
				if (kv.Value > best)
				{
					best = kv.Value;
					nemesis = kv.Key;
				}
			}

			return nemesis;
		}

		/// <summary>
		/// Called by CNSquadManagerBotModule when an ally is attacked.
		/// Increments the ally-attack weight for the attacker.
		/// </summary>
		public void RegisterAllyAttack(Player attacker)
		{
			if (IsTraitDisabled)
				return;
			if (attacker == null || attacker == self)
				return;
			if (attacker.RelationshipWith(self) != PlayerRelationship.Enemy)
				return;

			nemesisScores.TryGetValue(attacker, out var current);
			nemesisScores[attacker] = Math.Min(100f, current + Info.NemesisAllyWeightPerHit);
		}

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (IsTraitDisabled)
				return;
			if (e.Attacker == null || e.Attacker.Disposed)
				return;
			if (e.Attacker.Owner.RelationshipWith(self.Owner) != PlayerRelationship.Enemy)
				return;
			if (e.Damage.Value < Info.MinDamageThreshold)
				return;

			// Defense threat tracking. Buildings and economy actors (harvesters, refineries, ...) count at
			// full weight; combat units count too, but at a reduced fraction, so a skirmish out on the
			// field registers as a signal without drowning out an attack on something that actually
			// matters. Each category is throttled on its own counter: combat units get hit far more
			// often than buildings/economy, and a shared counter would let that noise crowd out the
			// rarer, more important signal (e.g. harvesters being sniped) almost every time.
			var isSubstance = self.Info.HasTraitInfo<BuildingInfo>() || IsEconomyActor(self.Info);
			var throttleTick = isSubstance ? nextThreatRecordTick : nextCombatUnitThreatRecordTick;
			if (world.WorldTick >= throttleTick)
			{
				var role = ClassifyAttacker(e.Attacker);
				if (role != DefenseRole.Default)
				{
					var categoryFraction = isSubstance ? 1f : Info.CombatUnitWeightFraction;
					var weight = Info.WeightPerHit * GetValueWeightMultiplier(e.Attacker) * categoryFraction;
					weights[role] = Math.Min(100f, weights[role] + weight);
				}

				if (isSubstance)
					nextThreatRecordTick = world.WorldTick + Math.Max(1, Info.ThreatRecordInterval);
				else
					nextCombatUnitThreatRecordTick = world.WorldTick + Math.Max(1, Info.CombatUnitThreatRecordInterval);
			}

			// Nemesis tracking — all owned actors (buildings + units)
			if (world.WorldTick < nextNemesisRecordTick)
				return;

			var attacker = e.Attacker.Owner;
			nemesisScores.TryGetValue(attacker, out var current);
			nemesisScores[attacker] = Math.Min(100f, current + Info.NemesisWeightPerHit);
			nextNemesisRecordTick = world.WorldTick + Math.Max(1, Info.NemesisRecordInterval);
		}

		bool IsEconomyActor(ActorInfo info)
		{
			if (economyActorCache.TryGetValue(info.Name, out var cached))
				return cached;

			var caps = info.TraitInfoOrDefault<BotCapabilitiesInfo>()?.CapabilitySet;
			var isEconomy = caps != null && Info.EconomyCapabilities.Overlaps(caps);
			economyActorCache[info.Name] = isEconomy;
			return isEconomy;
		}

		float GetValueWeightMultiplier(Actor attacker)
		{
			if (attackerValueMultiplierCache.TryGetValue(attacker.Info.Name, out var cached))
				return cached;

			// No ValuedInfo (e.g. a walking husk or a scripted actor) is treated as reference-cost,
			// i.e. a neutral 1.0 multiplier, rather than falling to the floor.
			var cost = attacker.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? Info.ValueWeightReference;
			var multiplier = Info.ValueWeightReference > 0f
				? Math.Clamp(cost / Info.ValueWeightReference, Info.MinValueWeightMultiplier, Info.MaxValueWeightMultiplier)
				: 1f;

			attackerValueMultiplierCache[attacker.Info.Name] = multiplier;
			return multiplier;
		}

		DefenseRole ClassifyAttacker(Actor attacker)
		{
			if (attackerRoleCache.TryGetValue(attacker.Info.Name, out var cachedRole))
				return cachedRole;

			var isAir = false;
			var isInfantry = false;
			var isVehicle = false;

			foreach (var targetable in attacker.TraitsImplementing<ITargetable>())
			{
				foreach (var targetType in targetable.TargetTypes)
				{
					var targetTypeString = targetType.ToString();
					isAir |= Info.AirTargetTypes.Contains(targetTypeString);
					isInfantry |= Info.InfantryTargetTypes.Contains(targetTypeString);
					isVehicle |= Info.VehicleTargetTypes.Contains(targetTypeString);
				}
			}

			var role = DefenseRole.Default;
			if (isAir)
				role = DefenseRole.AADefense;
			else if (isInfantry)
				role = DefenseRole.InfantryDefense;
			else if (isVehicle)
				role = DefenseRole.ArmorDefense;

			attackerRoleCache[attacker.Info.Name] = role;
			return role;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled)
				return;
			if (--decayTicks > 0)
				return;

			decayTicks = Info.DecayInterval;
			var decay = 1f - Info.DecayRate;
			foreach (var key in weights.Keys.ToList())
				weights[key] = Math.Max(0f, weights[key] * decay);
			foreach (var key in nemesisScores.Keys.ToList())
				nemesisScores[key] = Math.Max(0f, nemesisScores[key] * decay);
		}
	}
}
