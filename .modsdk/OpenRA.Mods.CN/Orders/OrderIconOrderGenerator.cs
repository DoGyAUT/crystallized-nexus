#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Orders;
using OpenRA.Orders;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Orders
{
	/// <summary>
	/// Generic order generator activated when the player clicks an OrderIcon button
	/// for an ability-type order (Deploy, Unload, Repair, etc.).
	/// Filters UnitOrderGenerator behaviour to only the specific <see cref="orderID"/>
	/// so unrelated order targeters on the selected actors are ignored.
	/// </summary>
	public sealed class OrderIconOrderGenerator : UnitOrderGenerator
	{
		readonly string orderID;
		Actor[] subjects;

		public bool IsForOrder(string id) => orderID == id;

		protected override MouseActionType ActionType => MouseActionType.ConfirmOrder;

		public OrderIconOrderGenerator(World world, IEnumerable<Actor> subjects, string orderID)
			: base(world)
		{
			this.orderID = orderID;
			this.subjects = subjects.Where(a => !a.IsDead && a.IsInWorld).ToArray();
		}

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var queued = mi.Modifiers.HasModifier(Modifiers.Shift);
			if (!queued)
				world.CancelInputMode();

			var target = TargetForInput(world, cell, worldPixel, mi);

			var orders = new List<Order>();
			foreach (var actor in subjects)
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;

				foreach (var issueOrder in actor.TraitsImplementing<IIssueOrder>())
				{
					foreach (var targeter in issueOrder.Orders)
					{
						if (targeter.OrderID != orderID)
							continue;

						var modifiers = TargetModifiers.None;
						if (queued)
							modifiers |= TargetModifiers.ForceQueue;

						string cursor = null;
						if (!targeter.CanTarget(actor, target, ref modifiers, ref cursor))
							continue;

						var order = issueOrder.IssueOrder(actor, targeter, target, queued);
						if (order != null)
							orders.Add(order);

						break;
					}
				}
			}

			return orders;
		}

		public override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var target = TargetForInput(world, cell, worldPixel, mi);
			var queued = mi.Modifiers.HasModifier(Modifiers.Shift);

			foreach (var actor in subjects)
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;

				foreach (var issueOrder in actor.TraitsImplementing<IIssueOrder>())
				{
					foreach (var targeter in issueOrder.Orders)
					{
						if (targeter.OrderID != orderID)
							continue;

						var modifiers = TargetModifiers.None;
						if (queued)
							modifiers |= TargetModifiers.ForceQueue;

						string cursor = null;
						if (targeter.CanTarget(actor, target, ref modifiers, ref cursor) && cursor != null)
							return cursor;
					}
				}
			}

			return null;
		}

		public override void SelectionChanged(World world, IEnumerable<Actor> selected)
		{
			subjects = selected.Where(a => !a.IsDead && a.IsInWorld).ToArray();
			if (subjects.Length == 0)
				world.CancelInputMode();
		}

		public override bool InputOverridesSelection(World world, int2 xy, MouseInput mi) => true;
		public override bool ClearSelectionOnLeftClick => false;
	}
}
