#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Orders;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Orders
{
	/// <summary>
	/// Context-sensitive order generator for the unified "Attack" command card button.
	/// <list type="bullet">
	/// <item>Enemy actor target  → issues "Attack" order.</item>
	/// <item>Own / ally actor    → issues "ForceAttack" (same as Ctrl+click on enemy).</item>
	/// <item>Ground / cell       → issues "AttackMove".</item>
	/// </list>
	/// Replaces the three old separate buttons (Attack Move, Force Attack, and implicit cursor attack).
	/// </summary>
	public sealed class AttackCommandOrderGenerator : UnitOrderGenerator
	{
		Actor[] subjects;

		protected override MouseActionType ActionType => MouseActionType.ConfirmOrder;

		public AttackCommandOrderGenerator(World world, IEnumerable<Actor> subjects)
			: base(world)
		{
			this.subjects = subjects.Where(a => !a.IsDead && a.IsInWorld).ToArray();
		}

		bool HasAttackMove(Actor actor) =>
			actor.Info.HasTraitInfo<AttackMoveInfo>();

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var queued = mi.Modifiers.HasModifier(Modifiers.Shift);
			if (!queued)
				world.CancelInputMode();

			var target = TargetForInput(world, cell, worldPixel, mi);

			// Ground target → AttackMove
			if (target.Type == TargetType.Invalid || target.Type == TargetType.Terrain)
			{
				var attackMovers = subjects.Where(HasAttackMove).ToArray();
				if (attackMovers.Length == 0)
					yield break;

				cell = world.Map.Clamp(cell);
				yield return new Order("AttackMove", null, Target.FromCell(world, cell), queued, null, attackMovers);
				yield break;
			}

			// Actor target — force-attack own/allied units
			var modifiers = TargetModifiers.None;
			if (queued)
				modifiers |= TargetModifiers.ForceQueue;

			if (target.Type == TargetType.Actor)
			{
				var relationship = target.Actor.Owner.RelationshipWith(world.LocalPlayer);
				if (relationship == PlayerRelationship.Ally || relationship == PlayerRelationship.Neutral)
					modifiers |= TargetModifiers.ForceAttack;
			}

			foreach (var actor in subjects)
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;

				var bestOrder = FindBestAttackOrder(actor, target, cell, modifiers);
				if (bestOrder == null)
					continue;

				var issued = bestOrder.Trait.IssueOrder(actor, bestOrder.Order, bestOrder.Target, queued);
				if (issued != null)
					yield return issued;
			}
		}

		public override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var target = TargetForInput(world, cell, worldPixel, mi);

			if (target.Type == TargetType.Invalid || target.Type == TargetType.Terrain)
			{
				var hasAttackMovers = subjects.Any(HasAttackMove);
				if (!hasAttackMovers)
					return "attackmove-blocked";

				var explored = subjects.FirstOrDefault()?.Owner.Shroud.IsExplored(cell) ?? false;
				// Check for any actor that can't move into shroud
				var blockedByShroud = !explored && subjects.Any(a =>
					a.Info.TraitInfoOrDefault<AttackMoveInfo>() is AttackMoveInfo info
					&& !info.MoveIntoShroud);

				return blockedByShroud ? "attackmove-blocked" : "attackmove";
			}

			var queued = mi.Modifiers.HasModifier(Modifiers.Shift);
			var modifiers = TargetModifiers.None;
			if (queued)
				modifiers |= TargetModifiers.ForceQueue;

			if (target.Type == TargetType.Actor)
			{
				var relationship = target.Actor.Owner.RelationshipWith(world.LocalPlayer);
				if (relationship == PlayerRelationship.Ally || relationship == PlayerRelationship.Neutral)
					modifiers |= TargetModifiers.ForceAttack;
			}

			foreach (var actor in subjects)
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;

				var bestOrder = FindBestAttackOrder(actor, target, cell, modifiers);
				if (bestOrder?.Cursor != null)
					return bestOrder.Cursor;
			}

			return "attack";
		}

		public override void SelectionChanged(World world, IEnumerable<Actor> selected)
		{
			subjects = selected.Where(a => !a.IsDead && a.IsInWorld).ToArray();
			if (subjects.Length == 0)
				world.CancelInputMode();
		}

		public override bool InputOverridesSelection(World world, int2 xy, MouseInput mi) => true;
		public override bool ClearSelectionOnLeftClick => false;

		/// <summary>
		/// Finds the highest-priority attack-related order targeter for the actor.
		/// </summary>
		static AttackOrderResult FindBestAttackOrder(Actor actor, Target target, CPos cell, TargetModifiers modifiers)
		{
			var current = target;
			for (var pass = 0; pass < 2; pass++)
			{
				foreach (var issueOrder in actor.TraitsImplementing<IIssueOrder>())
				{
					foreach (var targeter in issueOrder.Orders.OrderByDescending(o => o.OrderPriority))
					{
						if (targeter.OrderID != "Attack" && targeter.OrderID != "ForceAttack")
							continue;

						var localModifiers = modifiers;
						string cursor = null;
						if (targeter.CanTarget(actor, current, ref localModifiers, ref cursor))
							return new AttackOrderResult(actor, targeter, issueOrder, cursor, current);
					}
				}

				current = Target.FromCell(actor.World, cell);
			}

			return null;
		}

		sealed class AttackOrderResult(Actor actor, IOrderTargeter order, IIssueOrder trait, string cursor, in Target target)
		{
			public readonly Actor Actor = actor;
			public readonly IOrderTargeter Order = order;
			public readonly IIssueOrder Trait = trait;
			public readonly string Cursor = cursor;
			public ref readonly Target Target => ref targetField;
			readonly Target targetField = target;
		}
	}
}
