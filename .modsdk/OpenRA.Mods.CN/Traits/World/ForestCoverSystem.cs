#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	// ── Per-tree marker ───────────────────────────────────────────────────────
	[Desc("Registers this actor as a forest cover source with ForestCoverSystem.",
		"Replaces ProximityExternalCondition for forest cover — more efficient for large forests.")]
	public class ForestCoverSourceInfo : TraitInfo
	{
		[Desc("Radius of forest cover granted by this actor.")]
		public readonly WDist Range = new(1512);

		public override object Create(ActorInitializer init) => new ForestCoverSource(init.Self, this);
	}

	public class ForestCoverSource : INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly ForestCoverSourceInfo info;
		ForestCoverSystem system;

		public ForestCoverSource(Actor self, ForestCoverSourceInfo info)
		{
			this.info = info;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			system = self.World.WorldActor.TraitOrDefault<ForestCoverSystem>();
			system?.AddSource(self.Location, info.Range);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			system?.RemoveSource(self.Location, info.Range);
		}
	}

	// ── World manager ─────────────────────────────────────────────────────────
	[TraitLocation(SystemActors.World)]
	[Desc("Manages the forest-cover condition using a cell influence map.",
		"Replaces per-tree ProximityExternalCondition with a single centralized system.",
		"Requires ForestCoverSource on tree actors and ExternalCondition on unit actors.")]
	public class ForestCoverSystemInfo : TraitInfo
	{
		[Desc("Condition granted to units standing in forest cells.",
			"Must be listed in ExternalConditions on the receiving actors.")]
		public readonly string Condition = "forest-cover";

		[Desc("Ticks between unit-position checks.",
			"Lower values are more responsive; higher values reduce per-frame CPU cost.")]
		public readonly int CheckInterval = 5;

		public override object Create(ActorInitializer init) => new ForestCoverSystem(init.Self, this);
	}

	public class ForestCoverSystem : ITick, IWorldLoaded
	{
		readonly ForestCoverSystemInfo info;
		readonly World world;

		// Reference-counted influence map: cell → number of tree sources covering it.
		// A cell is "in forest" when the cell is present.
		readonly Dictionary<CPos, int> forestCells = [];

		// Only actors that actually declare the forest-cover ExternalCondition are
		// tracked — buildings, trees, projectiles etc. are never iterated. This is
		// the key scalability win over scanning every actor in the world.
		readonly HashSet<Actor> coverable = [];

		// Last evaluated cell per coverable actor — lets us skip the per-actor
		// forest lookup/grant logic when the actor hasn't moved cell and the
		// forest map itself hasn't changed since the previous pass.
		readonly Dictionary<Actor, CPos> lastCell = [];

		// One condition token per covered unit — avoids N tokens per unit when N trees overlap.
		readonly Dictionary<Actor, int> tokens = [];

		// Bumped whenever a tree source is added/removed. When it changes between
		// passes the unmoved-actor short-circuit is bypassed so stationary units
		// under a tree that just appeared/was destroyed still update.
		int forestRevision;
		int lastProcessedRevision;

		int tickCount;
		bool ready;

		public ForestCoverSystem(Actor self, ForestCoverSystemInfo info)
		{
			this.info = info;
			world = self.World;
		}

		void IWorldLoaded.WorldLoaded(World w, Graphics.WorldRenderer wr)
		{
			// Seed the coverable set once, then maintain it incrementally.
			foreach (var actor in world.Actors)
				if (IsCoverable(actor))
					coverable.Add(actor);

			world.ActorAdded += OnActorAdded;
			world.ActorRemoved += OnActorRemoved;
			ready = true;
		}

		// An actor can receive forest cover only if it declares an ExternalCondition
		// for our condition. Checked against ActorInfo so it is stable and cheap.
		bool IsCoverable(Actor actor)
		{
			return actor.Info.TraitInfos<ExternalConditionInfo>().Any(c => c.Condition == info.Condition);
		}

		// Called by ForestCoverSource when a tree enters the world.
		public void AddSource(CPos center, WDist range)
		{
			foreach (var cell in CellsInRange(center, range))
			{
				forestCells.TryGetValue(cell, out var count);
				forestCells[cell] = count + 1;
			}

			forestRevision++;
		}

		// Called by ForestCoverSource when a tree leaves the world (e.g. destroyed).
		public void RemoveSource(CPos center, WDist range)
		{
			foreach (var cell in CellsInRange(center, range))
			{
				if (!forestCells.TryGetValue(cell, out var count))
					continue;

				if (count <= 1)
					forestCells.Remove(cell);
				else
					forestCells[cell] = count - 1;
			}

			forestRevision++;
		}

		IEnumerable<CPos> CellsInRange(CPos center, WDist range)
		{
			// FindTilesInCircle takes an integer cell radius; over-approximate then filter by WPos.
			var cellRadius = (range.Length + 1023) / 1024;
			var treeCenter = world.Map.CenterOfCell(center);

			foreach (var cell in world.Map.FindTilesInCircle(center, cellRadius))
			{
				var delta = world.Map.CenterOfCell(cell) - treeCenter;
				if (delta.HorizontalLengthSquared <= range.LengthSquared)
					yield return cell;
			}
		}

		void ITick.Tick(Actor self)
		{
			if (!ready)
				return;

			if (++tickCount < info.CheckInterval)
				return;
			tickCount = 0;

			// If the forest map changed since the last pass, every coverable actor
			// must be re-checked (a tree may have appeared/vanished under a unit
			// that did not move). Otherwise only moved units need work.
			var forestChanged = forestRevision != lastProcessedRevision;
			lastProcessedRevision = forestRevision;

			foreach (var actor in coverable)
			{
				if (!actor.IsInWorld || actor.IsDead || actor.OccupiesSpace == null)
					continue;

				var cell = world.Map.CellContaining(actor.CenterPosition);

				if (!forestChanged && lastCell.TryGetValue(actor, out var lc) && lc == cell)
					continue;

				lastCell[actor] = cell;

				var inForest = forestCells.ContainsKey(cell);
				var hasToken = tokens.ContainsKey(actor);

				if (inForest && !hasToken)
					TryGrant(actor);
				else if (!inForest && hasToken)
					TryRevoke(actor);
			}
		}

		void TryGrant(Actor actor)
		{
			var external = actor.TraitsImplementing<ExternalCondition>()
				.FirstOrDefault(e => e.Info.Condition == info.Condition && e.CanGrantCondition(this));

			if (external == null)
				return;

			var token = external.GrantCondition(actor, this);
			if (token != Actor.InvalidConditionToken)
				tokens[actor] = token;
		}

		void TryRevoke(Actor actor)
		{
			if (!tokens.TryGetValue(actor, out var token))
				return;

			tokens.Remove(actor);

			foreach (var external in actor.TraitsImplementing<ExternalCondition>())
				if (external.TryRevokeCondition(actor, this, token))
					break;
		}

		void OnActorAdded(Actor actor)
		{
			if (IsCoverable(actor))
				coverable.Add(actor);
		}

		void OnActorRemoved(Actor actor)
		{
			coverable.Remove(actor);
			tokens.Remove(actor);
			lastCell.Remove(actor);
		}
	}
}
