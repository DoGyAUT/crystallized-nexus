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

using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Restores missing MobSpawner squad members when a repaired squad reaches full health at this host.")]
	public class RestoresInfantrySquadsInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new RestoresInfantrySquads(); }
	}

	public class RestoresInfantrySquads : INotifyResupply, INotifyPassengerEntered, INotifyPassengerExited
	{
		bool unloadQueued;

		void INotifyResupply.BeforeResupply(Actor host, Actor target, ResupplyType types) { }

		void INotifyResupply.ResupplyTick(Actor host, Actor target, ResupplyType types)
		{
			if (!types.HasFlag(ResupplyType.Repair))
				return;

			var health = target.TraitOrDefault<IHealth>();
			if (health == null || health.DamageState != DamageState.Undamaged)
				return;

			target.TraitOrDefault<MobSpawnerMaster>()?.RestoreSquad(target, host);
		}

		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor passenger)
		{
			if (!passenger.Owner.IsAlliedWith(self.Owner))
				return;

			RestorePassenger(self, passenger);

			if (unloadQueued)
				return;

			unloadQueued = true;
			self.QueueActivity(new Wait(5, false));
			self.QueueActivity(new UnloadCargo(self, self.Trait<Cargo>().Info.LoadRange));
		}

		void INotifyPassengerExited.OnPassengerExited(Actor self, Actor passenger)
		{
			if (self.TraitOrDefault<Cargo>()?.IsEmpty() == true)
				unloadQueued = false;
		}

		static void RestorePassenger(Actor self, Actor passenger)
		{
			var squad = passenger.TraitOrDefault<MobSpawnerMaster>();
			if (squad != null)
			{
				squad.RestoreSquad(passenger, self);
				return;
			}

			var health = passenger.TraitOrDefault<IHealth>();
			var hpToRepair = health != null ? health.MaxHP - health.HP : 0;
			if (hpToRepair > 0)
				health.InflictDamage(passenger, self, new Damage(-hpToRepair), false);
		}
	}
}
