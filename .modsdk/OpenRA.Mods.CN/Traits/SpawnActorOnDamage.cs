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
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Spawns one or more actors when this actor is damaged.")]
	public class SpawnActorOnDamageInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actors to randomly spawn.")]
		public readonly ImmutableArray<string> Actors = [];

		[Desc("Chance that a damage event spawns actors.")]
		public readonly int Probability = 100;

		[Desc("Ticks before another damage event can spawn actors.")]
		public readonly int Cooldown = 125;

		[Desc("How many actors to spawn per trigger.")]
		public readonly int SpawnCount = 1;

		[Desc("Maximum cell radius around this actor to place spawned actors.")]
		public readonly int SpawnRadius = 1;

		[Desc("Owner of the spawned actor.")]
		public readonly OwnerType OwnerType = OwnerType.Victim;

		[Desc("Map player to use when OwnerType is InternalName.")]
		public readonly string InternalOwner = "Neutral";

		[Desc("Only spawn when the lobby Creeps option is enabled.")]
		public readonly bool RequiresLobbyCreeps = false;

		[Desc("Do not trigger on zero-damage events.")]
		public readonly bool IgnoreZeroDamage = true;

		[SequenceReference]
		[Desc("Optional body sequence to play when spawning actors.")]
		public readonly string Sequence = null;

		[Desc("Sprite body to use for Sequence.")]
		public readonly string Body = "body";

		public override object Create(ActorInitializer init) { return new SpawnActorOnDamage(init.Self, this); }
	}

	public class SpawnActorOnDamage : ConditionalTrait<SpawnActorOnDamageInfo>, INotifyDamage, ITick
	{
		readonly SpawnActorOnDamageInfo info;
		readonly WithSpriteBody spriteBody;
		int cooldown;

		public SpawnActorOnDamage(Actor self, SpawnActorOnDamageInfo info)
			: base(info)
		{
			this.info = info;
			spriteBody = string.IsNullOrEmpty(info.Sequence)
				? null
				: self.TraitsImplementing<WithSpriteBody>().FirstOrDefault(w => w.Info.Name == info.Body);
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || self.IsDead || !self.IsInWorld || cooldown > 0)
				return;

			if (info.IgnoreZeroDamage && e.Damage.Value <= 0)
				return;

			if (info.RequiresLobbyCreeps && self.World.WorldActor.TraitOrDefault<MapCreeps>()?.Enabled != true)
				return;

			if (info.Probability < 100 && self.World.SharedRandom.Next(100) >= Math.Max(0, info.Probability))
				return;

			cooldown = Math.Max(1, info.Cooldown);

			if (!string.IsNullOrEmpty(info.Sequence) && spriteBody != null)
				spriteBody.PlayCustomAnimation(self, info.Sequence);

			for (var i = 0; i < info.SpawnCount; i++)
				TrySpawn(self, e.Attacker);
		}

		void ITick.Tick(Actor self)
		{
			if (cooldown > 0)
				cooldown--;
		}

		bool TrySpawn(Actor self, Actor attacker)
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
				.Where(self.World.Map.Contains)
				.OrderBy(_ => self.World.SharedRandom.Next())
				.ToArray();

			foreach (var cell in candidates)
			{
				var subCell = positionable.SharesCell
					? self.World.ActorMap.FreeSubCell(cell)
					: SubCell.FullCell;

				if (subCell == SubCell.Invalid || !positionable.CanEnterCell(self.World, null, cell, subCell))
					continue;

				var owner = ResolveOwner(self, attacker);
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

		Player ResolveOwner(Actor self, Actor attacker)
		{
			if (info.OwnerType == OwnerType.InternalName)
				return self.World.Players.FirstOrDefault(p => p.InternalName == info.InternalOwner) ?? self.Owner;

			if (info.OwnerType == OwnerType.Killer && attacker != null)
				return attacker.Owner;

			return self.Owner;
		}
	}
}
