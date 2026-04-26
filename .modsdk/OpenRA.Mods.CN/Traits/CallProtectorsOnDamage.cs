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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Calls nearby allied actors to attack the source when this actor is damaged.")]
	public class CallProtectorsOnDamageInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor types that should respond.")]
		public readonly ImmutableArray<string> ProtectorActorTypes = [];

		[Desc("Maximum cell radius to search for protectors.")]
		public readonly int SearchRadius = 10;

		[Desc("Maximum number of protectors ordered per damage event.")]
		public readonly int MaxProtectors = 3;

		[Desc("Ticks to wait before calling protectors again.")]
		public readonly int Cooldown = 125;

		[Desc("Stop moving once this close to the attacker.")]
		public readonly int AttackRange = 1;

		public override object Create(ActorInitializer init) { return new CallProtectorsOnDamage(this); }
	}

	public class CallProtectorsOnDamage : ConditionalTrait<CallProtectorsOnDamageInfo>, INotifyDamage, ITick
	{
		int cooldown;

		public CallProtectorsOnDamage(CallProtectorsOnDamageInfo info)
			: base(info) { }

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || cooldown > 0 || self.IsDead || !self.IsInWorld)
				return;

			var attacker = e.Attacker;
			if (attacker == null || attacker == self || attacker.IsDead || !attacker.IsInWorld || !attacker.AppearsHostileTo(self))
				return;

			cooldown = Math.Max(1, Info.Cooldown);
			var names = Info.ProtectorActorTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var radiusSq = Info.SearchRadius * Info.SearchRadius;

			var protectors = self.World.Actors
				.Where(a => a.IsInWorld && !a.IsDead
					&& a.Owner == self.Owner
					&& a != self
					&& names.Contains(a.Info.Name)
					&& (a.Location - self.Location).LengthSquared <= radiusSq)
				.OrderBy(a => (a.Location - self.Location).LengthSquared)
				.Take(Math.Max(0, Info.MaxProtectors));

			foreach (var protector in protectors)
			{
				var move = protector.TraitOrDefault<IMove>();
				if (move == null)
					continue;

				protector.QueueActivity(false, new AttackMoveActivity(protector,
					() => move.MoveWithinRange(Target.FromActor(attacker), WDist.FromCells(Math.Max(0, Info.AttackRange)))));
			}
		}

		void ITick.Tick(Actor self)
		{
			if (cooldown > 0)
				cooldown--;
		}
	}
}
