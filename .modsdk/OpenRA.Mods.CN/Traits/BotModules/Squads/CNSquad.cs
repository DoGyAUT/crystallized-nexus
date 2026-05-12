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
using OpenRA.Mods.CN.Traits;
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

		public int MissingCount => System.Math.Max(0, SlotInfo.Count - CurrentCount);

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

		// --- Slot assignments (from template) ---
		public readonly List<CNSlotAssignment> SlotAssignments = [];

		// --- Type-specific fields ---
		public WDist ArtilleryHangBackRange;  // Artillery: how far behind frontline to stay
		public CNSquad AttachedTo;            // ArtilleryAssault/Support: squad to follow
		public bool IsWaitingForArtillery;    // Assault: hold position while artillery clears defenses
		public Actor CoordinatedAssaultTarget;
		public string[] PreferredTargetCapabilities; // BotCapabilities tags to prioritize as targets (Raider, Stealth, SubAssault, ...)


		// --- Mob-Awareness ---
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
				.Where(a => a != null && !a.IsDead);

		public bool HasCarrier => CarrierUnits.Any();

		public bool IsTemplateBacked => TemplateInfo != null;

		/// <summary>
		/// True for roles that stay near the base (defense, protection, air support).
		/// These squads may be reinforced while operational. Attack/away roles should
		/// not receive single replacement units mid-mission.
		/// </summary>
		public bool AllowsOperationalReinforcement =>
			Type == CNSquadType.Defense ||
			Type == CNSquadType.ArtilleryDefense ||
			Type == CNSquadType.Protection ||
			Type == CNSquadType.AircraftSupport;

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
		}

		public void SetPositionToTarget(WPos pos)
		{
			TargetActor = null;
			Target = Target.FromPos(pos);
		}

		/// <summary>
		/// Validates that the current target is still alive, in-world, and reachable.
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
			long nearestDistance = long.MaxValue;

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
