#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Reports attack and damage events to CNDynamicMusicController for the dynamic soundtrack.",
		"Intended to be inherited on shared unit/building templates alongside ^PlayerHandicaps.")]
	public class CNCombatSignalReporterInfo : TraitInfo
	{
		[Desc("Weight reported per attack (firing) event.")]
		public readonly float AttackWeight = 1f;

		[Desc("Weight reported per point of damage taken.")]
		public readonly float DamageWeightPerPoint = 0.05f;

		[Desc("Minimum ticks between two reported events from the same actor. Prevents rapid-fire",
			"weapons or multi-hit weapons from flooding the signal.")]
		public readonly int RecordInterval = 15;

		public override object Create(ActorInitializer init) { return new CNCombatSignalReporter(this, init.Self); }
	}

	public class CNCombatSignalReporter : INotifyCreated, INotifyAttack, INotifyDamage, ITick
	{
		readonly CNCombatSignalReporterInfo info;
		CNDynamicMusicController controller;
		int attackCooldown;
		int damageCooldown;

		public CNCombatSignalReporter(CNCombatSignalReporterInfo info, Actor self)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			controller = self.World.WorldActor.TraitOrDefault<CNDynamicMusicController>();
		}

		void ITick.Tick(Actor self)
		{
			if (attackCooldown > 0)
				attackCooldown--;

			if (damageCooldown > 0)
				damageCooldown--;
		}

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (controller == null || attackCooldown > 0)
				return;

			attackCooldown = info.RecordInterval;
			controller.ReportCombatEvent(self.Owner, self.CenterPosition, info.AttackWeight);
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (controller == null || damageCooldown > 0 || e.Damage.Value <= 0)
				return;

			damageCooldown = info.RecordInterval;
			var weight = e.Damage.Value * info.DamageWeightPerPoint;

			controller.ReportCombatEvent(self.Owner, self.CenterPosition, weight);

			if (e.Attacker != null && !e.Attacker.IsDead && e.Attacker.Owner != self.Owner)
				controller.ReportCombatEvent(e.Attacker.Owner, self.CenterPosition, weight);
		}
	}
}
