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
		public readonly WDist Range = new WDist(1512);

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
		// A cell is "in forest" when count > 0.
		readonly Dictionary<CPos, int> forestCells = [];

		// One condition token per covered unit — avoids N tokens per unit when N trees overlap.
		readonly Dictionary<Actor, int> tokens = [];

		int tickCount;
		bool ready;

		public ForestCoverSystem(Actor self, ForestCoverSystemInfo info)
		{
			this.info = info;
			world = self.World;
		}

		void IWorldLoaded.WorldLoaded(World w, OpenRA.Graphics.WorldRenderer wr)
		{
			ready = true;
			world.ActorRemoved += OnActorRemoved;
		}

		// Called by ForestCoverSource when a tree enters the world.
		public void AddSource(CPos center, WDist range)
		{
			foreach (var cell in CellsInRange(center, range))
			{
				forestCells.TryGetValue(cell, out var count);
				forestCells[cell] = count + 1;
			}
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

			foreach (var actor in world.Actors)
			{
				if (!actor.IsInWorld || actor.IsDead || actor.OccupiesSpace == null)
					continue;

				var cell = world.Map.CellContaining(actor.CenterPosition);
				var inForest = forestCells.Count > 0 && forestCells.ContainsKey(cell);
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

		void OnActorRemoved(Actor actor)
		{
			tokens.Remove(actor);
		}
	}
}
