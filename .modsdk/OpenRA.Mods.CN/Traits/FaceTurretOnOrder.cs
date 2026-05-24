#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Keeps visual turrets facing the target of movement and attack orders until the actor becomes idle.")]
	public class FaceTurretOnOrderInfo : TraitInfo
	{
		[Desc("Order names that update the desired turret facing.")]
		public readonly HashSet<string> ValidOrders = ["Move", "AttackMove", "AssaultMove", "Attack", "ForceAttack"];

		[Desc("Turret names to control. Leave empty to control all turrets.")]
		public readonly HashSet<string> Turrets = [];

		public override object Create(ActorInitializer init) { return new FaceTurretOnOrder(this); }
	}

	public class FaceTurretOnOrder : IResolveOrder, ITick, INotifyBecomingIdle
	{
		static readonly FieldInfo DesiredDirection = typeof(Turreted).GetField("desiredDirection", BindingFlags.Instance | BindingFlags.NonPublic);
		static readonly FieldInfo RealignTick = typeof(Turreted).GetField("realignTick", BindingFlags.Instance | BindingFlags.NonPublic);
		static readonly FieldInfo RealignDesired = typeof(Turreted).GetField("realignDesired", BindingFlags.Instance | BindingFlags.NonPublic);

		readonly FaceTurretOnOrderInfo info;

		Turreted[] turrets;
		AttackBase[] attacks;
		Target target = Target.Invalid;

		public FaceTurretOnOrder(FaceTurretOnOrderInfo info)
		{
			this.info = info;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Stop")
			{
				target = Target.Invalid;
				return;
			}

			if (!info.ValidOrders.Contains(order.OrderString) || !order.Target.IsValidFor(self))
				return;

			target = order.Target;
		}

		void ITick.Tick(Actor self)
		{
			if (target.Type == TargetType.Invalid)
				return;

			turrets ??= self.TraitsImplementing<Turreted>()
				.Where(t => info.Turrets.Count == 0 || info.Turrets.Contains(t.Name))
				.ToArray();

			if (turrets.Length == 0)
			{
				target = Target.Invalid;
				return;
			}

			attacks ??= self.TraitsImplementing<AttackBase>().ToArray();
			if (attacks.Any(a => a.IsAiming))
				return;

			target = target.Recalculate(self.Owner, out _);
			if (!target.IsValidFor(self))
			{
				target = Target.Invalid;
				return;
			}

			foreach (var t in turrets)
				FacePosition(self, t, target.CenterPosition);
		}

		void INotifyBecomingIdle.OnBecomingIdle(Actor self)
		{
			target = Target.Invalid;
		}

		static void FacePosition(Actor self, Turreted turret, WPos targetPosition)
		{
			if (turret.IsTraitDisabled || turret.IsTraitPaused)
				return;

			var turretPosition = self.CenterPosition + turret.Position(self);
			DesiredDirection.SetValue(turret, targetPosition - turretPosition);
			RealignTick.SetValue(turret, 0);
			RealignDesired.SetValue(turret, false);
		}
	}
}
