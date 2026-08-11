using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Grants condition as long as a valid power state is maintained.")]
	public class GrantConditionOnResourceStateInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("Condition to grant.")]
		public readonly string Condition = null;

		[FieldLoader.Require]
		[Desc("Minimal amount of resource at which the condition is granted.")]
		public readonly int MinimalCash = 0;

		public override object Create(ActorInitializer init) { return new GrantConditionOnResourceState(this); }
	}

	public class GrantConditionOnResourceState : ConditionalTrait<GrantConditionOnResourceStateInfo>, INotifyOwnerChanged, INotifyPowerLevelChanged
	{
		PlayerResources resources;
		int conditionToken = Actor.InvalidConditionToken;

		bool shouldGrantCondition;

		public GrantConditionOnResourceState(GrantConditionOnResourceStateInfo info)
			: base(info) { }

		protected override void Created(Actor self)
		{
			resources = self.Owner.PlayerActor.Trait<PlayerResources>();

			base.Created(self);

			Update(self);
		}

		protected override void TraitEnabled(Actor self)
		{
			Update(self);
		}

		protected override void TraitDisabled(Actor self)
		{
			Update(self);
		}

		void Update(Actor self)
		{
			shouldGrantCondition = !IsTraitDisabled && resources.Cash >= Info.MinimalCash;

			if (shouldGrantCondition && conditionToken == Actor.InvalidConditionToken)
				conditionToken = self.GrantCondition(Info.Condition);
			else if (!shouldGrantCondition && conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}

		void INotifyPowerLevelChanged.PowerLevelChanged(Actor self)
		{
			Update(self);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			resources = newOwner.PlayerActor.Trait<PlayerResources>();
			Update(self);
		}
	}
}
