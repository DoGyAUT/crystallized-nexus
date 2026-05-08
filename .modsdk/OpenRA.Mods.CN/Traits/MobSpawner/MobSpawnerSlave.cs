#region Copyright & License Information
/*
 * Originally written by Boolbada of OP Mod.
 * Ported to Crystallized Nexus by CN contributors.
 * Follows GPLv3 License as the OpenRA engine.
 */
#endregion

using System;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Slave actor controlled by a MobSpawnerMaster.")]
	public class MobSpawnerSlaveInfo : BaseSpawnerSlaveInfo
	{
		public override object Create(ActorInitializer init) { return new MobSpawnerSlave(init, this); }
	}

	public class MobSpawnerSlave : BaseSpawnerSlave, INotifySelected, INotifyDamage
	{
		readonly Actor self;

		public IMove[] Moves { get; private set; }
		public IPositionable Positionable { get; private set; }

		MobSpawnerMaster spawnerMaster;
		Actor master;

		public bool IsMoving { get { return self.CurrentActivity is Move; } }

		public MobSpawnerSlave(ActorInitializer init, MobSpawnerSlaveInfo info)
			: base(init, info)
		{
			self = init.Self;
		}

		protected override void Created(Actor self)
		{
			base.Created(self);

			Moves = self.TraitsImplementing<IMove>().ToArray();

			var positionables = self.TraitsImplementing<IPositionable>().ToArray();
			if (positionables.Length != 1)
				throw new InvalidOperationException($"Actor {self.Info.Name} has {positionables.Length} IPositionable traits (expected exactly 1).");

			Positionable = positionables[0];
		}

		public override void LinkMaster(Actor self, Actor master, BaseSpawnerMaster spawnerMaster)
		{
			base.LinkMaster(self, master, spawnerMaster);
			this.master = master;
			this.spawnerMaster = spawnerMaster as MobSpawnerMaster;
		}

		public void Move(Actor self, CPos location)
		{
			if (Moves.Length == 0)
				return;

			var masterMobile = master.TraitOrDefault<Mobile>();
			if (masterMobile != null && masterMobile.IsTraitDisabled)
				return;

			foreach (var mv in Moves)
				if (mv.IsTraitEnabled())
				{
					self.QueueActivity(mv.MoveTo(location, 2));
					break;
				}
		}

		public void AttackMove(Actor self, CPos location)
		{
			if (Moves.Length == 0)
				return;

			var masterMobile = master.TraitOrDefault<Mobile>();
			if (masterMobile != null && masterMobile.IsTraitDisabled)
				return;

			foreach (var mv in Moves)
				if (mv.IsTraitEnabled())
				{
					self.CancelActivity();
					self.QueueActivity(new AttackMoveActivity(self, () => mv.MoveTo(location, 1)));
					break;
				}
		}

		void INotifySelected.Selected(Actor self)
		{
			if (spawnerMaster == null || spawnerMaster.Info.SlavesHaveFreeWill)
				return;

			// Add the master — master's INotifySelected then expands the selection
			// to all remaining slaves. Contains-guard prevents re-entrant recursion.
			if (!self.World.Selection.Contains(Master))
				self.World.Selection.Add(Master);
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			// Master HP are exclusively managed by MobSpawnerMaster.SetNexusHealth,
			// which aggregates all slave HP every AggregateHealthUpdateDelay ticks.
			// Do NOT forward damage to master here — that would double-count every hit
			// and cause the master HP bar to drop twice as fast (or recurse infinitely).
		}
	}
}
