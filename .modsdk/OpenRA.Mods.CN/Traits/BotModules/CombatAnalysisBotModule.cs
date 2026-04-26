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

		[Desc("Threat weight added per qualifying damage event.")]
		public readonly float WeightPerHit = 10f;

		[Desc("Minimum ticks between recording direct threat-role damage events.")]
		public readonly int ThreatRecordInterval = 25;

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
			[DefenseRole.Special] = 0f,
		};

		// Per-enemy-player nemesis score: higher = this player attacked us more
		readonly Dictionary<Player, float> nemesisScores = [];
		readonly Dictionary<string, DefenseRole> attackerRoleCache = [];

		readonly Player self;
		readonly World world;
		int decayTicks;
		int nextThreatRecordTick;
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

		/// <summary>Returns the role with the highest threat weight above ReactThreshold, or Default if none.</summary>
		public DefenseRole GetHighestThreatRole()
		{
			var bestRole = DefenseRole.Default;
			var bestWeight = Info.ReactThreshold - 1f;
			foreach (var kv in weights)
			{
				if (kv.Value > bestWeight)
				{
					bestWeight = kv.Value;
					bestRole = kv.Key;
				}
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

			// Defense threat tracking — buildings only. This is throttled because
			// IBotRespondToAttack can fire for every projectile hit.
			if (self.Info.HasTraitInfo<BuildingInfo>() && world.WorldTick >= nextThreatRecordTick)
			{
				var role = ClassifyAttacker(e.Attacker);
				if (role != DefenseRole.Default)
					weights[role] = Math.Min(100f, weights[role] + Info.WeightPerHit);

				nextThreatRecordTick = world.WorldTick + Math.Max(1, Info.ThreatRecordInterval);
			}

			// Nemesis tracking — all owned actors (buildings + units)
			if (world.WorldTick < nextNemesisRecordTick)
				return;

			var attacker = e.Attacker.Owner;
			nemesisScores.TryGetValue(attacker, out var current);
			nemesisScores[attacker] = Math.Min(100f, current + Info.NemesisWeightPerHit);
			nextNemesisRecordTick = world.WorldTick + Math.Max(1, Info.NemesisRecordInterval);
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
