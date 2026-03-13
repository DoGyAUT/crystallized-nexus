#region Copyright & License Information
/*
 * Originally written by Boolbada of OP Mod.
 * Ported to Crystallized Nexus by CN contributors.
 * Follows GPLv3 License as the OpenRA engine.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	// What to do with slaves when master is killed or mind controlled
	public enum SpawnerSlaveDisposal
	{
		DoNothing,
		KillSlaves,
		GiveSlavesToAttacker
	}

	[Desc("Can be slaved to a MobSpawnerMaster.")]
	public class BaseSpawnerSlaveInfo : TraitInfo
	{
		[GrantedConditionReference]
		[Desc("Condition granted to slaves when the master actor is killed.")]
		public readonly string MasterDeadCondition = null;

		[Desc("Can these actors be mind controlled or captured?")]
		public readonly bool AllowOwnerChange = false;

		public override object Create(ActorInitializer init) { return new BaseSpawnerSlave(init, this); }
	}

	public class BaseSpawnerSlave : INotifyCreated, INotifyKilled, INotifyOwnerChanged
	{
		protected AttackBase[] attackBases;

		readonly BaseSpawnerSlaveInfo info;

		public bool HasFreeWill = false;

		int masterDeadToken = Actor.InvalidConditionToken;
		BaseSpawnerMaster spawnerMaster = null;

		public Actor Master { get; private set; }

		public BaseSpawnerSlave(ActorInitializer init, BaseSpawnerSlaveInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self) { Created(self); }

		protected virtual void Created(Actor self)
		{
			attackBases = self.TraitsImplementing<AttackBase>().ToArray();
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (Master == null || Master.IsDead)
				return;

			spawnerMaster.OnSlaveKilled(Master, self);
		}

		public virtual void LinkMaster(Actor self, Actor master, BaseSpawnerMaster spawnerMaster)
		{
			Master = master;
			this.spawnerMaster = spawnerMaster;
		}

		bool TargetSwitched(in Target lastTarget, in Target newTarget)
		{
			if (newTarget.Type != lastTarget.Type)
				return true;

			if (newTarget.Type == TargetType.Terrain)
				return newTarget.CenterPosition != lastTarget.CenterPosition;

			if (newTarget.Type == TargetType.Actor)
				return lastTarget.Actor != newTarget.Actor;

			return false;
		}

		public void Stop(Actor self)
		{
			lastTarget = Target.Invalid;
			self.CancelActivity();

			foreach (var ab in attackBases)
				if (!ab.IsTraitDisabled)
					ab.OnStopOrder(self);
		}

		Target lastTarget = Target.Invalid;

		public void Attack(Actor self, in Target target)
		{
			if (!TargetSwitched(lastTarget, target))
				return;

			if (target.Type != TargetType.Terrain && !target.IsValidFor(self))
			{
				Stop(self);
				return;
			}

			lastTarget = target;

			foreach (var ab in attackBases)
			{
				if (ab.IsTraitDisabled)
					continue;

				ab.AttackTarget(target, AttackSource.Default, false, true, target.RequiresForceFire);
			}
		}

		public virtual void OnMasterKilled(Actor self, Actor attacker, SpawnerSlaveDisposal disposal)
		{
			if (!string.IsNullOrEmpty(info.MasterDeadCondition))
				masterDeadToken = self.GrantCondition(info.MasterDeadCondition);

			switch (disposal)
			{
				case SpawnerSlaveDisposal.KillSlaves:
					self.Kill(attacker);
					break;
				case SpawnerSlaveDisposal.GiveSlavesToAttacker:
					self.CancelActivity();
					self.ChangeOwner(attacker.Owner);
					break;
				default:
					break;
			}
		}

		public virtual void OnMasterOwnerChanged(Actor self, Player oldOwner, Player newOwner, SpawnerSlaveDisposal disposal)
		{
			switch (disposal)
			{
				case SpawnerSlaveDisposal.KillSlaves:
					self.Kill(self);
					break;
				case SpawnerSlaveDisposal.GiveSlavesToAttacker:
					self.CancelActivity();
					self.ChangeOwner(newOwner);
					break;
				default:
					break;
			}
		}

		public virtual void OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			if (Master == null || !Master.IsDead)
				return;

			if (Master.Owner == newOwner)
				return;

			if (info.AllowOwnerChange)
				return;

			self.Kill(self);
		}
	}
}
