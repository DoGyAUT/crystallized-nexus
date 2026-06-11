#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Periodically spawns actors near this actor.")]
	public class SpawnActorOnTimerInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actors to randomly spawn.")]
		public readonly ImmutableArray<string> Actors = [];

		[Desc("Ticks before the first spawn. Supports one value or a min/max range.")]
		public readonly ImmutableArray<int> InitialDelay = [0];

		[Desc("Ticks between spawns. Supports one value or a min/max range.")]
		public readonly ImmutableArray<int> Interval = [1500];

		[Desc("How many actors to spawn per interval.")]
		public readonly int SpawnCount = 1;

		[Desc("Maximum cell radius around this actor to place spawned actors.")]
		public readonly int SpawnRadius = 1;

		[Desc("Do not spawn if this many matching actors are already nearby. Use -1 to disable.")]
		public readonly int MaxNearbyActors = -1;

		[ActorReference]
		[Desc("Actor types counted by MaxNearbyActors. Defaults to Actors when empty.")]
		public readonly ImmutableArray<string> NearbyActorTypes = [];

		[Desc("Cell radius used for MaxNearbyActors.")]
		public readonly int NearbyScanRadius = 8;

		[Desc("Do not spawn if this many matching actors already exist on the map. Use -1 to disable.")]
		public readonly int MaxGlobalActors = -1;

		[ActorReference]
		[Desc("Actor types counted by MaxGlobalActors. Defaults to Actors when empty.")]
		public readonly ImmutableArray<string> GlobalActorTypes = [];

		[Desc("Owner of the spawned actor. Killer falls back to Victim because this is timer based.")]
		public readonly OwnerType OwnerType = OwnerType.Victim;

		[Desc("Map player to use when OwnerType is InternalName.")]
		public readonly string InternalOwner = "Creeps";

		[Desc("Only spawn when the lobby Creeps option is enabled.")]
		public readonly bool RequiresLobbyCreeps = false;

		public override object Create(ActorInitializer init) { return new SpawnActorOnTimer(init.Self, this); }
	}

	public class SpawnActorOnTimer : ConditionalTrait<SpawnActorOnTimerInfo>, ITick
	{
		readonly SpawnActorOnTimerInfo info;
		int ticks;

		public SpawnActorOnTimer(Actor self, SpawnActorOnTimerInfo info)
			: base(info)
		{
			this.info = info;
			ticks = RandomRange(self.World.SharedRandom, info.InitialDelay, 0);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || self.IsDead || !self.IsInWorld)
				return;

			if (info.RequiresLobbyCreeps && self.World.WorldActor.TraitOrDefault<MapCreeps>()?.Enabled != true)
				return;

			if (--ticks > 0)
				return;

			ticks = RandomRange(self.World.SharedRandom, info.Interval, 1);

			if (info.SpawnCount <= 0 || IsNearbyLimitReached(self) || IsGlobalLimitReached(self))
				return;

			for (var i = 0; i < info.SpawnCount; i++)
				TrySpawn(self);
		}

		bool IsNearbyLimitReached(Actor self)
		{
			if (info.MaxNearbyActors < 0)
				return false;

			var names = (info.NearbyActorTypes.Length > 0 ? info.NearbyActorTypes : info.Actors)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var radiusSq = info.NearbyScanRadius * info.NearbyScanRadius;
			var count = self.World.Actors.Count(a => a.IsInWorld && !a.IsDead
				&& a.Owner == self.Owner
				&& names.Contains(a.Info.Name)
				&& (a.Location - self.Location).LengthSquared <= radiusSq);

			return count >= info.MaxNearbyActors;
		}

		bool IsGlobalLimitReached(Actor self)
		{
			if (info.MaxGlobalActors < 0)
				return false;

			var names = (info.GlobalActorTypes.Length > 0 ? info.GlobalActorTypes : info.Actors)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var count = self.World.Actors.Count(a => a.IsInWorld && !a.IsDead
				&& names.Contains(a.Info.Name));

			return count >= info.MaxGlobalActors;
		}

		bool TrySpawn(Actor self)
		{
			if (info.Actors.Length == 0)
				return false;

			var actorName = info.Actors.Random(self.World.SharedRandom).ToLowerInvariant();
			if (!self.World.Map.Rules.Actors.TryGetValue(actorName, out var actorInfo))
				return false;

			var positionable = actorInfo.TraitInfoOrDefault<IPositionableInfo>();
			if (positionable == null)
				return false;

			var candidates = self.World.Map.FindTilesInCircle(self.Location, Math.Max(0, info.SpawnRadius))
				.Where(c => self.World.Map.Contains(c))
				.OrderBy(_ => self.World.SharedRandom.Next())
				.ToArray();

			foreach (var cell in candidates)
			{
				var subCell = positionable.SharesCell
					? self.World.ActorMap.FreeSubCell(cell)
					: SubCell.FullCell;

				if (subCell == SubCell.Invalid || !positionable.CanEnterCell(self.World, null, cell, subCell))
					continue;

				var owner = ResolveOwner(self);
				self.World.AddFrameEndTask(w => w.CreateActor(actorName,
				[
					new OwnerInit(owner),
					new LocationInit(cell),
					new SubCellInit(subCell),
					new FacingInit(new WAngle(w.SharedRandom.Next(1024)))
				]));

				return true;
			}

			return false;
		}

		Player ResolveOwner(Actor self)
		{
			if (info.OwnerType == OwnerType.InternalName)
				return self.World.Players.FirstOrDefault(p => p.InternalName == info.InternalOwner) ?? self.Owner;

			return self.Owner;
		}

		static int RandomRange(MersenneTwister random, ImmutableArray<int> range, int fallback)
		{
			if (range.Length == 0)
				return fallback;

			if (range.Length == 1 || range[1] <= range[0])
				return Math.Max(0, range[0]);

			return random.Next(Math.Max(0, range[0]), Math.Max(1, range[1]));
		}
	}
}
