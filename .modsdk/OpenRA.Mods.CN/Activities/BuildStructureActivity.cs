#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Activities
{
	/// <summary>
	/// Orders a Builder unit to walk onto a structure footprint, then removes it from the world
	/// (caching it in <see cref="PlayerBuilders"/>) and spawns the chosen structure at 1 HP.
	/// The structure's <see cref="UnderConstruction"/> trait ramps it up and returns the builder.
	/// </summary>
	public sealed class BuildStructure : Activity
	{
		readonly Mobile mobile;
		readonly PlayerResources resources;
		readonly CPos topLeft;
		readonly string buildingName;
		readonly ActorInfo buildingActor;
		readonly BuildingInfo buildingInfo;
		readonly List<CPos> tiles;
		readonly int cost;
		readonly Color targetLineColor;
		readonly MoveCooldownHelper moveCooldownHelper;

		bool placed;

		public BuildStructure(Actor self, CPos topLeft, string buildingName, Color targetLineColor)
		{
			this.topLeft = topLeft;
			this.buildingName = buildingName;
			this.targetLineColor = targetLineColor;

			mobile = self.Trait<Mobile>();
			resources = self.Owner.PlayerActor.Trait<PlayerResources>();
			buildingActor = self.World.Map.Rules.Actors[buildingName];
			buildingInfo = buildingActor.TraitInfo<BuildingInfo>();
			tiles = buildingInfo.Tiles(topLeft).ToList();
			cost = buildingActor.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			moveCooldownHelper = new MoveCooldownHelper(self.World, mobile) { RetryIfDestinationBlocked = true };
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || placed)
				return true;

			// Standing on the footprint: place the structure.
			if (tiles.Contains(self.Location))
			{
				Place(self);
				return true;
			}

			var result = moveCooldownHelper.Tick(false);
			if (result != null)
				return result.Value;

			var dest = ChooseFootprintCell(self);
			if (dest == null)
				return true;

			moveCooldownHelper.NotifyMoveQueued();
			QueueChild(mobile.MoveTo(dest.Value, 0));
			return false;
		}

		CPos? ChooseFootprintCell(Actor self)
		{
			var enterable = tiles
				.Where(c => mobile.CanEnterCell(c, self))
				.ToList();

			var candidates = enterable.Count > 0 ? enterable : tiles;
			if (candidates.Count == 0)
				return null;

			return candidates
				.OrderBy(c => (c - self.Location).LengthSquared)
				.First();
		}

		void Place(Actor self)
		{
			placed = true;

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;

				// Builder occupies one of its own footprint cells, so ignore it for the placement check.
				if (!w.CanPlaceBuilding(topLeft, buildingActor, buildingInfo, self))
					return;

				if (cost > 0 && !resources.TakeCash(cost, true))
					return;

				// Remove the builder from the map and cache it until the structure is done.
				w.Remove(self);
				self.Owner.PlayerActor.Trait<PlayerBuilders>().Store(self);

				var faction = buildingActor.TraitInfoOrDefault<BuildableInfo>()?.ForceFaction
					?? self.Owner.Faction.InternalName;

				w.CreateActor(buildingName, new TypeDictionary
				{
					new LocationInit(topLeft),
					new OwnerInit(self.Owner),
					new FactionInit(faction),
					new HealthInit(1),
					new BuilderInit(self),

					// Suppress the structure's own WithMakeAnimation auto-play; UnderConstruction +
					// WithBuildAnimation drive the build-up (condition + stretched make animation) instead.
					new SkipMakeAnimsInit(),
				});
			});
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			yield return new TargetLineNode(Target.FromCell(self.World, topLeft), targetLineColor);
		}
	}
}
