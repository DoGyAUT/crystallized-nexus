#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.CN.Traits.BotModules.Squads;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Shoots destroyable cliffs open. A cliff is worth demolishing in two situations, and the module",
		"only ever acts on those: inside a region this bot holds, where the collapse joins up its own",
		"ground, and inside enemy-held ground it is currently attacking, where the collapse is a new way",
		"in. Reads the region graph from CNTacticalMapBotModule/CNRegionManagerBotModule.")]
	public class CNCliffDemolitionBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Ticks between scans for cliffs worth demolishing.")]
		public readonly int ScanInterval = 250;

		[Desc("Cliffs being worked on at the same time. One at a time keeps the firepower concentrated:",
			"a cliff half-damaged in three places is still three cliffs.")]
		public readonly int MaximumSimultaneousTargets = 1;

		[Desc("Demolish cliffs standing in regions this bot holds.")]
		public readonly bool DemolishInHeldRegions = true;

		[Desc("Demolish cliffs standing in enemy-held regions this bot has units in - that is, regions it",
			"is attacking. Without units nearby there is nothing the new opening would be for.")]
		public readonly bool DemolishInAttackedRegions = true;

		[Desc("Only demolish a cliff whose sides belong to different regions. A cliff with the same ground",
			"on both sides is already walked around, and blowing it open buys nothing.")]
		public readonly bool RequireSeparatedSides = true;

		[Desc("How far from a cliff a unit may be to be pulled onto it, in cells. This is a marching",
			"distance, not only a filter: the units are ordered to attack and walk there, so this is",
			"effectively how far the bot will send somebody to open a cliff.")]
		public readonly int AttackerSearchRadius = 25;

		[Desc("Units that must be available before a cliff is engaged at all. Sending one tank at four",
			"thousand hit points only feeds it.")]
		public readonly int MinimumAttackers = 3;

		[Desc("Units assigned to one cliff.")]
		public readonly int MaximumAttackers = 6;

		[Desc("Allow pulling in units that belong to a squad, when they are idle. Their squad can order",
			"them away again at any time - which is fine, the damage already dealt stays dealt.")]
		public readonly bool AllowSquadUnits = true;

		[Desc("Only consider cliffs the bot can actually see.")]
		public readonly bool CheckTargetsForVisibility = true;

		public override object Create(ActorInitializer init) { return new CNCliffDemolitionBotModule(init.Self, this); }
	}

	public class CNCliffDemolitionBotModule : ConditionalTrait<CNCliffDemolitionBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		// Which cliff each unit was last sent at, so a unit is not re-ordered every scan while it is
		// already shooting - and so the same handful of units is not counted twice across two cliffs.
		readonly Dictionary<Actor, Actor> assignments = [];

		CNTacticalMapBotModule tacticalMap;
		CNRegionManagerBotModule regionManager;
		CNSquadManagerBotModule squadManager;
		bool firstTick = true;
		int scanTicks;

		public CNCliffDemolitionBotModule(Actor self, CNCliffDemolitionBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			scanTicks = world.LocalRandom.Next(ScanInterval);
		}

		int ScanInterval => Math.Max(1, Info.ScanInterval);

		void IBotTick.BotTick(IBot bot)
		{
			using var perfScope = CNBotPerf.Sample(bot, nameof(CNCliffDemolitionBotModule));

			if (firstTick)
			{
				tacticalMap = player.PlayerActor.TraitsImplementing<CNTacticalMapBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
				regionManager = player.PlayerActor.TraitsImplementing<CNRegionManagerBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
				squadManager = player.PlayerActor.TraitsImplementing<CNSquadManagerBotModule>()
					.FirstOrDefault(t => t.IsTraitEnabled());
				firstTick = false;
			}

			if (--scanTicks > 0)
				return;

			scanTicks = ScanInterval;

			if (player.WinState != WinState.Undefined || tacticalMap == null || !tacticalMap.TopologyReady)
				return;

			PruneAssignments();
			QueueDemolitionOrders(bot);
		}

		void QueueDemolitionOrders(IBot bot)
		{
			var candidates = new List<(Actor Cliff, bool Enemy)>();
			foreach (var actor in world.ActorsHavingTrait<CNDestroyableCliff>())
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;

				if (Info.CheckTargetsForVisibility && !actor.CanBeViewedByPlayer(player))
					continue;

				if (!ShouldDemolish(actor, out var enemyGround))
					continue;

				candidates.Add((actor, enemyGround));
			}

			if (candidates.Count == 0)
				return;

			// An opening into ground we are already fighting over is worth more than tidying up our own,
			// and closer beats further among equals - the units are there either way.
			var ordered = candidates
				.OrderByDescending(c => c.Enemy)
				.ThenBy(c => DistanceToOwnUnitsSquared(c.Cliff))
				.Take(Math.Max(1, Info.MaximumSimultaneousTargets));

			foreach (var (cliff, enemyGround) in ordered)
				Engage(bot, cliff, enemyGround);
		}

		/// <summary>
		/// Whether this cliff is one of the two kinds worth shooting, and which kind. Read off the passable
		/// cells around the footprint rather than the cliff's own cells: the cliff is impassable, so it is
		/// in no region at all, and the regions it separates are exactly the ones touching it.
		/// </summary>
		bool ShouldDemolish(Actor cliff, out bool enemyGround)
		{
			enemyGround = false;

			var regionIds = AdjacentRegions(cliff);
			if (regionIds.Count == 0)
				return false;

			if (Info.RequireSeparatedSides && regionIds.Count < 2)
				return false;

			var held = false;
			var enemyHeld = false;
			foreach (var regionId in regionIds)
			{
				if (regionManager?.GetRegionState(regionId)?.Claimed ?? false)
					held = true;

				var owner = tacticalMap.GetRegionOwner(regionId);
				if (owner != null && player.RelationshipWith(owner) == PlayerRelationship.Enemy)
					enemyHeld = true;
			}

			// Attacking is read from where our units are, not from squad bookkeeping: a squad's declared
			// target says what it means to do, and what matters here is whether there is anything standing
			// next to the cliff to walk through the hole once it is open.
			if (Info.DemolishInAttackedRegions && enemyHeld && HasOwnUnitsNear(cliff))
			{
				enemyGround = true;
				return true;
			}

			return Info.DemolishInHeldRegions && held;
		}

		HashSet<int> AdjacentRegions(Actor cliff)
		{
			var regionIds = new HashSet<int>();
			var footprint = cliff.Trait<CNDestroyableCliff>().Footprint;
			if (footprint.Count == 0)
				return regionIds;

			var cells = footprint.ToHashSet();
			foreach (var cell in footprint)
			{
				foreach (var direction in CVec.Directions)
				{
					var neighbour = cell + direction;
					if (cells.Contains(neighbour) || !world.Map.Contains(neighbour))
						continue;

					var regionId = tacticalMap.GetRegionIdAt(neighbour);
					if (regionId >= 0)
						regionIds.Add(regionId);
				}
			}

			return regionIds;
		}

		bool HasOwnUnitsNear(Actor cliff)
		{
			return CandidateAttackers(cliff).Any();
		}

		long DistanceToOwnUnitsSquared(Actor cliff)
		{
			var nearest = long.MaxValue;
			foreach (var attacker in CandidateAttackers(cliff))
				nearest = Math.Min(nearest, (attacker.CenterPosition - cliff.CenterPosition).LengthSquared);

			return nearest;
		}

		IEnumerable<Actor> CandidateAttackers(Actor cliff)
		{
			var radius = WDist.FromCells(Math.Max(1, Info.AttackerSearchRadius));
			var target = Target.FromActor(cliff);

			foreach (var actor in world.FindActorsInCircle(cliff.CenterPosition, radius))
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				// Assigned to another cliff already: counting it here would let two cliffs each believe they
				// have enough units and neither of them get any.
				if (assignments.TryGetValue(actor, out var assigned) && assigned != cliff)
					continue;

				if (!actor.Info.HasTraitInfo<IPositionableInfo>())
					continue;

				var attack = actor.TraitsImplementing<AttackBase>().FirstEnabledTraitOrDefault();
				if (attack == null || !attack.HasAnyValidWeapons(target))
					continue;

				if (squadManager != null && squadManager.IsUnitAssignedToSquad(actor)
					&& (!Info.AllowSquadUnits || !actor.IsIdle))
					continue;

				yield return actor;
			}
		}

		void Engage(IBot bot, Actor cliff, bool enemyGround)
		{
			var attackers = CandidateAttackers(cliff)
				.Take(Math.Max(1, Info.MaximumAttackers))
				.ToList();

			if (attackers.Count < Math.Max(1, Info.MinimumAttackers))
				return;

			var ordered = 0;
			foreach (var attacker in attackers)
			{
				// Already on it and still working: leave it alone. Re-issuing the order every scan resets
				// the approach and the unit spends its time turning around instead of shooting.
				if (assignments.TryGetValue(attacker, out var assigned) && assigned == cliff && !attacker.IsIdle)
					continue;

				bot.QueueOrder(new Order("ForceAttack", attacker, Target.FromActor(cliff), false));
				assignments[attacker] = cliff;
				ordered++;
			}

			if (ordered > 0)
				CNBotLog.Debug("AI ({0}): Demolishing cliff at {1} with {2} units ({3} ground)",
					player.ClientIndex, cliff.Location, ordered, enemyGround ? "enemy" : "own");
		}

		void PruneAssignments()
		{
			var stale = assignments
				.Where(kv => kv.Key.IsDead || !kv.Key.IsInWorld || kv.Key.Owner != player
					|| kv.Value.IsDead || !kv.Value.IsInWorld)
				.Select(kv => kv.Key)
				.ToArray();

			foreach (var attacker in stale)
				assignments.Remove(attacker);
		}
	}
}
