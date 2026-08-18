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
using OpenRA.Mods.CN.Traits.BotModules;
using OpenRA.Mods.CN.Traits.BotModules.Squads;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Force-fires veinholes down. Veinholes are NoAutoTarget and RequiresForceFire, so nothing shoots",
		"one unless it is told to - which makes this module the whole of the behaviour rather than a nudge",
		"on top of it. Gate it by faction side: GDI burns the weed out, Nod leaves it standing because its",
		"weed harvesters live off it.")]
	public class CNVeinholeAssaultBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Faction sides that hunt veinholes, matched against the player's faction Side. Empty means",
			"every side does.")]
		public readonly FrozenSet<string> Sides = [];

		[ActorReference]
		[Desc("Actor types treated as veinholes.")]
		public readonly FrozenSet<string> VeinholeActorTypes = ["veinhole"];

		[Desc("Ticks between scans for veinholes worth shooting.")]
		public readonly int ScanInterval = 300;

		[Desc("Veinholes worked on at the same time. One at a time keeps the firepower concentrated: a",
			"veinhole regenerates, so half-damaging three of them achieves nothing at all.")]
		public readonly int MaximumSimultaneousTargets = 1;

		[Desc("How far from a veinhole a unit may be to be pulled onto it, in cells. This doubles as the",
			"leash: a veinhole with no units of ours near it is simply never engaged, so the bot does not",
			"march across the map to weed a corner it has no interest in.")]
		public readonly int AttackerSearchRadius = 12;

		[Desc("Units that must be available before a veinhole is engaged at all. Below this the damage",
			"never outruns the regeneration and the units are wasted standing still.")]
		public readonly int MinimumAttackers = 4;

		[Desc("Units assigned to one veinhole.")]
		public readonly int MaximumAttackers = 8;

		[Desc("Allow pulling in units that belong to a squad, when they are idle. Their squad can order them",
			"away again at any time - which is fine, the damage already dealt stays dealt.")]
		public readonly bool AllowSquadUnits = true;

		[Desc("Only consider veinholes the bot can actually see.")]
		public readonly bool CheckTargetsForVisibility = true;

		public override object Create(ActorInitializer init) { return new CNVeinholeAssaultBotModule(init.Self, this); }
	}

	public class CNVeinholeAssaultBotModule : ConditionalTrait<CNVeinholeAssaultBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		// Which veinhole each unit was last sent at, so a unit is not re-ordered every scan while it is
		// already shooting - and so the same handful of units is not counted twice across two veinholes.
		readonly Dictionary<Actor, Actor> assignments = [];

		// Veinholes are map actors and nothing creates more of them at runtime, so the list is collected
		// once and only ever shrinks. Re-walking every actor in the world each scan would cost far more
		// than the handful of holes it would find.
		readonly List<Actor> veinholes = [];

		CNSquadManagerBotModule squadManager;
		bool firstTick = true;
		bool active;
		int scanTicks;

		public CNVeinholeAssaultBotModule(Actor self, CNVeinholeAssaultBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			scanTicks = world.LocalRandom.Next(ScanInterval);
		}

		int ScanInterval => Math.Max(1, Info.ScanInterval);

		void IBotTick.BotTick(IBot bot)
		{
			using var perfScope = CNBotPerf.Sample(bot, nameof(CNVeinholeAssaultBotModule));

			if (firstTick)
			{
				firstTick = false;
				active = Info.Sides.Count == 0 || (player.Faction?.Side != null && Info.Sides.Contains(player.Faction.Side));
				if (active)
				{
					squadManager = player.PlayerActor.TraitsImplementing<CNSquadManagerBotModule>()
						.FirstOrDefault(t => t.IsTraitEnabled());

					foreach (var actor in world.Actors)
						if (Info.VeinholeActorTypes.Contains(actor.Info.Name))
							veinholes.Add(actor);
				}
			}

			if (!active || veinholes.Count == 0)
				return;

			if (--scanTicks > 0)
				return;

			scanTicks = ScanInterval;

			if (player.WinState != WinState.Undefined)
				return;

			Prune();
			QueueAssaultOrders(bot);
		}

		void QueueAssaultOrders(IBot bot)
		{
			var candidates = new List<(Actor Veinhole, long Distance)>();
			foreach (var veinhole in veinholes)
			{
				if (Info.CheckTargetsForVisibility && !veinhole.CanBeViewedByPlayer(player))
					continue;

				var distance = DistanceToOwnUnitsSquared(veinhole);
				if (distance == long.MaxValue)
					continue;

				candidates.Add((veinhole, distance));
			}

			if (candidates.Count == 0)
				return;

			var ordered = candidates
				.OrderBy(c => c.Distance)
				.Take(Math.Max(1, Info.MaximumSimultaneousTargets));

			foreach (var (veinhole, _) in ordered)
				Engage(bot, veinhole);
		}

		long DistanceToOwnUnitsSquared(Actor veinhole)
		{
			var nearest = long.MaxValue;
			foreach (var attacker in CandidateAttackers(veinhole))
				nearest = Math.Min(nearest, (attacker.CenterPosition - veinhole.CenterPosition).LengthSquared);

			return nearest;
		}

		IEnumerable<Actor> CandidateAttackers(Actor veinhole)
		{
			var radius = WDist.FromCells(Math.Max(1, Info.AttackerSearchRadius));
			var target = Target.FromActor(veinhole);

			foreach (var actor in world.FindActorsInCircle(veinhole.CenterPosition, radius))
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				// Assigned to another veinhole already: counting it here would let two veinholes each
				// believe they have enough units and neither of them get any.
				if (assignments.TryGetValue(actor, out var assigned) && assigned != veinhole)
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

		void Engage(IBot bot, Actor veinhole)
		{
			var attackers = CandidateAttackers(veinhole)
				.Take(Math.Max(1, Info.MaximumAttackers))
				.ToList();

			if (attackers.Count < Math.Max(1, Info.MinimumAttackers))
				return;

			var ordered = 0;
			foreach (var attacker in attackers)
			{
				// Already on it and still working: leave it alone. Re-issuing the order every scan resets
				// the approach and the unit spends its time turning around instead of shooting.
				if (assignments.TryGetValue(attacker, out var assigned) && assigned == veinhole && !attacker.IsIdle)
					continue;

				bot.QueueOrder(new Order("ForceAttack", attacker, Target.FromActor(veinhole), false));
				assignments[attacker] = veinhole;
				ordered++;
			}

			if (ordered > 0)
				CNBotLog.Debug("AI ({0}): Burning veinhole at {1} with {2} units",
					player.ClientIndex, veinhole.Location, ordered);
		}

		void Prune()
		{
			veinholes.RemoveAll(v => v.IsDead || !v.IsInWorld);

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
