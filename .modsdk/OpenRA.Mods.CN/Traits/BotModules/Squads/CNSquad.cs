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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads
{
	/// <summary>
	/// A filled slot assignment: which units were assigned to a specific slot in a team template.
	/// </summary>
	public class CNSlotAssignment
	{
		public readonly CNSlotInfo SlotInfo;
		public readonly List<Actor> Units = [];
		public readonly List<Actor> Passengers = [];

		public CNSlotAssignment(CNSlotInfo slotInfo) { SlotInfo = slotInfo; }

		public int CurrentCount => SlotInfo.IsPassenger ? AlivePassengerCount : AliveUnitCount;

		public int AliveUnitCount => Units.Count(a => a != null && !a.IsDead && a.IsInWorld);

		public int AlivePassengerCount => Passengers.Count(a => a != null && !a.IsDead && a.IsInWorld);

		// Passenger recruitment counts boarded units too: a boarded passenger leaves the world
		// (IsInWorld == false) but is still alive and committed to this squad, so it must count
		// toward "recruited" or the slot would keep demanding replacements and overfill the carrier.
		public int RecruitedPassengerCount => Passengers.Count(a => a != null && !a.IsDead);

		public int MissingCount => System.Math.Max(0, SlotInfo.Count - CurrentCount);

		// Missing relative to the recruitment target (boarded-aware). Used while a transport is still
		// loading so passenger top-up fills to capacity instead of chasing the (dropping) in-world count.
		public int MissingToRecruit => System.Math.Max(0,
			SlotInfo.Count - (SlotInfo.IsPassenger ? RecruitedPassengerCount : AliveUnitCount));

		public bool IsFulfilled =>
			SlotInfo.Optional ||
			CurrentCount >= SlotInfo.Count;
	}

	/// <summary>
	/// A CN squad: a group of units with a shared role, state machine, and targeting.
	/// Analogous to Squad.cs in the engine but standalone and CN-specific.
	/// </summary>
	public class CNSquad
	{
		// --- Core ---
		public readonly HashSet<Actor> Units = [];
		public readonly CNSquadType Type;
		public readonly string TemplateName;
		public readonly CNTeamTemplateInfo TemplateInfo;
		public readonly int CreatedTick;

		internal readonly IBot Bot;
		internal readonly World World;
		internal readonly CNSquadManagerBotModule SquadManager;
		internal readonly MersenneTwister Random;
		internal readonly CNStateMachine FuzzyStateMachine;

		// --- Targeting ---
		internal Target Target { get; private set; }
		internal Actor TargetActor { get; private set; }
		internal int NoTargetIdleTicks;

		/// <summary>
		/// Game ticks that passed since this squad was last updated. States count their timeouts in
		/// game ticks by adding this rather than incrementing per update, so a squad that is updated
		/// more often — see CNSquadManagerBotModule's engaged/idle cadence — does not silently run
		/// every stuck detector, wait window and reissue interval down faster.
		/// </summary>
		public int TicksSinceLastUpdate { get; private set; } = 1;

		/// <summary>
		/// World tick at which the squad manager should next update this squad. Set by the manager
		/// from the engaged/idle cadence; staggering it per squad also spreads the update cost
		/// across ticks instead of putting the whole army on one.
		/// </summary>
		public int NextUpdateTick { get; set; }

		int lastUpdatedTick;

		// --- Slot assignments (from template) ---
		public readonly List<CNSlotAssignment> SlotAssignments = [];

		// --- Type-specific fields ---
		public WDist ArtilleryHangBackRange;  // Artillery: how far behind frontline to stay
		public CNSquad AttachedTo;            // ArtilleryAssault/Support: squad to follow
		public string[] PreferredTargetCapabilities; // BotCapabilities tags to prioritize as targets (Raider, Stealth, SubAssault, ...)

		/// <summary>True if any unit in this squad is a MobSpawnerMaster.</summary>
		public bool HasMobs => Units.Any(a => a != null && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<MobSpawnerMasterInfo>());

		/// <summary>
		/// Units that should receive direct orders.
		/// Slaves are excluded — they follow their master automatically.
		/// </summary>
		public IEnumerable<Actor> OrderableUnits =>
			HasMobs
				? Units.Where(a => a != null && !a.IsDead && a.IsInWorld && !a.Info.HasTraitInfo<MobSpawnerSlaveInfo>())
				: Units.Where(a => a != null && !a.IsDead && a.IsInWorld);

		// --- Carrier/Passenger helpers ---
		public IEnumerable<Actor> CarrierUnits =>
			SlotAssignments
				.Where(s => s.SlotInfo.IsCarrier || s.SlotInfo.IsAircraftCarrier)
				.SelectMany(s => s.Units)
				.Where(a => a != null && !a.IsDead);

		public IEnumerable<Actor> PassengerUnits =>
			SlotAssignments
				.Where(s => s.SlotInfo.IsPassenger)
				.SelectMany(s => s.Passengers)
				.Where(a => a != null && !a.IsDead)
				.SelectMany(ExpandPassengerUnit)
				.Distinct()
				.Where(a => a != null && !a.IsDead);

		public IEnumerable<Actor> EscortUnits =>
			SlotAssignments
				.Where(s => s.SlotInfo.IsEscort)
				.SelectMany(s => s.Units)
				.Where(a => a != null && !a.IsDead);

		public bool HasCarrier => CarrierUnits.Any();

		/// <summary>
		/// True while this transport squad is still at base loading and may receive passenger
		/// top-ups even after becoming operational. Set by the transport idle/load states and
		/// cleared once the squad departs (attack-move/unload/return/done).
		/// </summary>
		public bool AcceptingPassengers { get; set; }

		/// <summary>Total passengers this squad's template wants (sum of IsPassenger slot counts).</summary>
		public int DesiredPassengerCount =>
			SlotAssignments.Where(s => s.SlotInfo.IsPassenger).Sum(s => s.SlotInfo.Count);

		/// <summary>Passengers committed to this squad (boarded + still walking), excluding dead.</summary>
		public int RecruitedPassengerCount =>
			SlotAssignments.Where(s => s.SlotInfo.IsPassenger).Sum(s => s.RecruitedPassengerCount);

		static IEnumerable<Actor> ExpandPassengerUnit(Actor actor)
		{
			if (actor == null || actor.IsDead)
				yield break;

			yield return actor;

			var mob = actor.TraitOrDefault<MobSpawnerMaster>();
			if (mob == null)
				yield break;

			foreach (var slave in mob.AliveSlavesInWorld)
				yield return slave;
		}

		public bool IsTemplateBacked => TemplateInfo != null;

		/// <summary>
		/// True for reactive/home roles that may be reinforced while operational.
		/// Attack/away roles should not receive single replacement units mid-mission.
		/// </summary>
		public bool AllowsOperationalReinforcement =>
			Type == CNSquadType.Protection ||
			(Type == CNSquadType.Support && TemplateInfo?.StayInBase == true);

		public CNSquad(
			IBot bot,
			CNSquadManagerBotModule squadManager,
			CNSquadType type,
			string templateName = null,
			CNTeamTemplateInfo templateInfo = null)
		{
			Bot = bot;
			SquadManager = squadManager;
			World = bot.Player.PlayerActor.World;
			Random = World.LocalRandom;
			Type = type;
			TemplateName = templateName;
			TemplateInfo = templateInfo;
			CreatedTick = World.WorldTick;
			PreferredTargetCapabilities = templateInfo?.PriorityTargetCapabilities;
			FuzzyStateMachine = new CNStateMachine();
			Target = Target.Invalid;
		}

		public void Update()
		{
			// First update of a squad's life has no previous tick to measure against; one tick keeps
			// any elapsed-time test from firing on the very first pass.
			TicksSinceLastUpdate = lastUpdatedTick == 0
				? 1
				: System.Math.Max(1, World.WorldTick - lastUpdatedTick);
			lastUpdatedTick = World.WorldTick;

			if (IsValid)
				FuzzyStateMachine.Update(this);
		}

		public bool IsValid => Units.Any(a => a != null && !a.IsDead && a.IsInWorld);

		public bool IsOperational
		{
			get
			{
				if (!IsValid)
					return false;

				if (TemplateInfo == null || SlotAssignments.Count == 0)
					return true;

				return OperationalSlotCount() >= TemplateInfo.MinSlotsToActivate;
			}
		}

		public int OperationalSlotCount()
		{
			var fulfilled = 0;

			foreach (var assignment in SlotAssignments)
			{
				if (assignment == null || assignment.SlotInfo.Optional)
					continue;

				if (assignment.CurrentCount >= assignment.SlotInfo.Count)
					fulfilled++;
			}

			return fulfilled;
		}

		public bool NeedsReinforcement =>
			TemplateInfo != null && SlotAssignments.Any(a => a.MissingCount > 0);

		public void SetActorToTarget(Actor actor)
		{
			TargetActor = actor;
			Target = actor != null ? Target.FromActor(actor) : Target.Invalid;
			if (actor != null)
				NoTargetIdleTicks = 0;

			// Single funnel for every squad target assignment, so the manager's claim registry stays in
			// step without each state having to remember to report what it picked.
			SquadManager.SetTargetClaim(this, actor);
		}

		public void SetPositionToTarget(WPos pos)
		{
			TargetActor = null;
			Target = Target.FromPos(pos);

			// The second funnel. Dropping the actor without releasing the claim left a squad reserving
			// damage against a target it had walked away from — a subterranean squad moving to its
			// ambush position kept other squads off its former target for as long as that actor lived.
			SquadManager.ClearTargetClaim(this);
		}

		/// <summary>
		/// Validates that the current target is still alive, in-world, and reachable.
		/// INVARIANT: the final "any in-world unit" clause is satisfied by the
		/// carriers for transport squads — boarded passengers are alive but out of
		/// world, so target validity must never depend on passengers being in-world.
		/// </summary>
		public bool IsTargetValid =>
			TargetActor != null &&
			SquadManager.IsLiveEnemyActor(TargetActor) &&
			!TargetActor.Info.HasTraitInfo<HuskInfo>() &&
			Units.Any(u => u != null && !u.IsDead && u.IsInWorld);

		public bool IsTargetVisible =>
			TargetActor != null &&
			TargetActor.CanBeViewedByPlayer(Bot.Player);

		/// <summary>Average world position of all living units in the squad.</summary>
		public WPos CenterPosition()
		{
			long x = 0;
			long y = 0;
			long z = 0;
			var count = 0;

			foreach (var actor in Units)
			{
				if (actor == null || actor.IsDead || !actor.IsInWorld)
					continue;

				var pos = actor.CenterPosition;
				x += pos.X;
				y += pos.Y;
				z += pos.Z;
				count++;
			}

			return count == 0
				? WPos.Zero
				: new WPos((int)(x / count), (int)(y / count), (int)(z / count));
		}

		/// <summary>Unit closest to the squad's center position.</summary>
		public Actor CenterUnit()
		{
			var center = CenterPosition();
			Actor nearest = null;
			var nearestDistance = long.MaxValue;

			foreach (var actor in Units)
			{
				if (actor == null || actor.IsDead || !actor.IsInWorld)
					continue;

				var distance = (actor.CenterPosition - center).LengthSquared;
				if (distance >= nearestDistance)
					continue;

				nearest = actor;
				nearestDistance = distance;
			}

			return nearest;
		}
	}
}
