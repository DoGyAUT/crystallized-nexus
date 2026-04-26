#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("When idle, move back near the closest matching allied actor.")]
	public class KeepNearActorsInfo : ConditionalTraitInfo, Requires<IMoveInfo>
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor types to stay near.")]
		public readonly ImmutableArray<string> ActorTypes = [];

		[Desc("Maximum cell radius used to find a matching actor.")]
		public readonly int SearchRadius = 12;

		[Desc("Only move back when farther away than this many cells.")]
		public readonly int LeashRadius = 4;

		[Desc("Stop following when within this range of the target actor.")]
		public readonly int FollowRange = 2;

		[Desc("Use attack-move while returning so the actor can defend itself.")]
		public readonly bool AttackMove = true;

		public override object Create(ActorInitializer init) { return new KeepNearActors(init.Self, this); }
	}

	public class KeepNearActors : ConditionalTrait<KeepNearActorsInfo>, INotifyIdle
	{
		readonly KeepNearActorsInfo info;
		readonly IMove move;

		public KeepNearActors(Actor self, KeepNearActorsInfo info)
			: base(info)
		{
			this.info = info;
			move = self.Trait<IMove>();
		}

		void INotifyIdle.TickIdle(Actor self)
		{
			if (IsTraitDisabled || self.IsDead || !self.IsInWorld)
				return;

			var target = FindClosestAnchor(self);
			if (target == null)
				return;

			if ((target.Location - self.Location).LengthSquared <= info.LeashRadius * info.LeashRadius)
				return;

			var followTarget = Target.FromActor(target);
			if (info.AttackMove)
				self.QueueActivity(new AttackMoveActivity(self,
					() => move.MoveFollow(self, followTarget, WDist.Zero, WDist.FromCells(Math.Max(0, info.FollowRange)))));
			else
				self.QueueActivity(move.MoveFollow(self, followTarget, WDist.Zero, WDist.FromCells(Math.Max(0, info.FollowRange))));
		}

		Actor FindClosestAnchor(Actor self)
		{
			var names = info.ActorTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var radiusSq = info.SearchRadius * info.SearchRadius;

			return self.World.Actors
				.Where(a => a.IsInWorld && !a.IsDead
					&& a.Owner == self.Owner
					&& a != self
					&& names.Contains(a.Info.Name)
					&& (a.Location - self.Location).LengthSquared <= radiusSq)
				.MinByOrDefault(a => (a.Location - self.Location).LengthSquared);
		}
	}
}
