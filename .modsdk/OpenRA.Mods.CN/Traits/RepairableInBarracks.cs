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

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Allows infantry to enter barracks for repairs instead of being repaired while standing outside.")]
	public class RepairableInBarracksInfo : TraitInfo, Requires<IHealthInfo>, Requires<IMoveInfo>, IObservesVariablesInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		public readonly FrozenSet<string> RepairActors = FrozenSet<string>.Empty;

		[VoiceReference]
		public readonly string Voice = "Action";

		[ConsumedConditionReference]
		[Desc("Boolean expression defining the condition under which the regular enter cursor is disabled.")]
		public readonly BooleanExpression RequireForceMoveCondition = null;

		[CursorReference]
		[Desc("Cursor to display when able to enter a barracks for repairs.")]
		public readonly string EnterCursor = "enter";

		[CursorReference]
		[Desc("Cursor to display when unable to enter a barracks for repairs.")]
		public readonly string EnterBlockedCursor = "enter-blocked";

		public override object Create(ActorInitializer init) { return new RepairableInBarracks(init.Self, this); }
	}

	public class RepairableInBarracks : IIssueOrder, IResolveOrder, IOrderVoice, IObservesVariables
	{
		public readonly RepairableInBarracksInfo Info;
		readonly Actor self;
		readonly IHealth health;
		bool requireForceMove;

		public RepairableInBarracks(Actor self, RepairableInBarracksInfo info)
		{
			this.self = self;
			Info = info;
			health = self.Trait<IHealth>();
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				yield return new EnterAlliedActorTargeter<BuildingInfo>(
					"Repair",
					5,
					Info.EnterCursor,
					Info.EnterBlockedCursor,
					CanRepairAt,
					_ => ShouldRepair());
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order.OrderID == "Repair")
				return new Order(order.OrderID, self, target, queued);

			return null;
		}

		bool CanRepairAt(Actor target, TargetModifiers modifiers)
		{
			if (requireForceMove && !modifiers.HasModifier(TargetModifiers.ForceMove))
				return false;

			return CanRepairAt(target);
		}

		bool CanRepairAt(Actor target)
		{
			var cargo = target.TraitOrDefault<Cargo>();
			return Info.RepairActors.Contains(target.Info.Name)
				&& cargo != null
				&& !cargo.IsTraitDisabled
				&& cargo.Info.Types.Contains(self.Info.TraitInfo<PassengerInfo>().CargoType);
		}

		bool ShouldRepair()
		{
			return health.DamageState > DamageState.Undamaged;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			return order.OrderString == "Repair" && ShouldRepair() ? Info.Voice : null;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "Repair" || order.Target.Type != TargetType.Actor)
				return;

			if (!CanRepairAt(order.Target.Actor) || !ShouldRepair())
				return;

			self.QueueActivity(order.Queued, new EnterBarracksRepair(self, order.Target, Color.Green));
			self.ShowTargetLines();
		}

		public Actor FindRepairBuilding(Actor self)
		{
			var repairBuilding = self.World.ActorsWithTrait<RestoresInfantrySquads>()
				.Where(a => !a.Actor.IsDead && a.Actor.IsInWorld
					&& a.Actor.Owner.IsAlliedWith(self.Owner)
					&& Info.RepairActors.Contains(a.Actor.Info.Name)
					&& CanRepairAt(a.Actor))
				.OrderBy(a => a.Actor.Owner == self.Owner ? 0 : 1)
				.ThenBy(p => (self.Location - p.Actor.Location).LengthSquared);

			return repairBuilding.FirstOrDefault().Actor;
		}

		IEnumerable<VariableObserver> IObservesVariables.GetVariableObservers()
		{
			if (Info.RequireForceMoveCondition != null)
				yield return new VariableObserver(RequireForceMoveConditionChanged, Info.RequireForceMoveCondition.Variables);
		}

		void RequireForceMoveConditionChanged(Actor self, IReadOnlyDictionary<string, int> conditions)
		{
			requireForceMove = Info.RequireForceMoveCondition.Evaluate(conditions);
		}
	}

	sealed class EnterBarracksRepair : Enter
	{
		readonly Passenger passenger;

		Actor enterActor;
		Cargo enterCargo;

		public EnterBarracksRepair(Actor self, in Target target, Color? targetLineColor)
			: base(self, target, targetLineColor)
		{
			passenger = self.Trait<Passenger>();
		}

		protected override bool TryStartEnter(Actor self, Actor targetActor)
		{
			enterActor = targetActor;
			enterCargo = targetActor.TraitOrDefault<Cargo>();

			if (enterCargo == null || enterCargo.IsTraitDisabled || !passenger.Reserve(self, enterCargo))
			{
				Cancel(self, true);
				return false;
			}

			return true;
		}

		protected override void TickInner(Actor self, in Target target, bool targetIsDeadOrHiddenActor)
		{
			if (enterCargo != null && enterCargo.IsTraitDisabled)
				Cancel(self, true);
		}

		protected override void OnEnterComplete(Actor self, Actor targetActor)
		{
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || targetActor != enterActor || !enterCargo.CanLoad(self))
					return;

				enterCargo.Load(enterActor, self);
				w.Remove(self);
			});
		}

		protected override void OnLastRun(Actor self)
		{
			passenger.Unreserve(self);
		}

		public override void Cancel(Actor self, bool keepQueue = false)
		{
			passenger.Unreserve(self);
			base.Cancel(self, keepQueue);
		}
	}
}
