#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * Based on Health.cs from the OpenRA engine (GPLv3).
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	/// <summary>
	/// Init type used by the map editor when saving health percentages for CNHealth actors.
	/// Mirrors HealthInit so maps that store CNHealthInit: can still load correctly.
	/// </summary>
	public class CNHealthInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public CNHealthInit(int value)
			: base(value) { }
	}

	public class CNHealthInfo : TraitInfo, IHealthInfo, IRulesetLoaded, IEditorActorOptions
	{
		[Desc("HitPoints")]
		public readonly int HP = 0;

		[Desc("Trigger interfaces such as AnnounceOnKill?")]
		public readonly bool NotifyAppliedDamage = true;

		[Desc("Display order for the health slider in the map editor")]
		public readonly int EditorHealthDisplayOrder = 2;

		public override object Create(ActorInitializer init) { return new CNHealth(init, this); }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (!ai.HasTraitInfo<HitShapeInfo>())
				throw new YamlException("Actors with CNHealth need at least one HitShape trait!");
		}

		int IHealthInfo.MaxHP => HP;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorSlider("Health", EditorHealthDisplayOrder, 0, 100, 5,
				actor =>
				{
					return actor.GetInitOrDefault<CNHealthInit>()?.Value
						?? actor.GetInitOrDefault<HealthInit>()?.Value
						?? 100;
				},
				(actor, value) => actor.ReplaceInit(new CNHealthInit((int)value)));
		}
	}

	public class CNHealth : IHealth, ISync, ITick, INotifyCreated, INotifyOwnerChanged
	{
		public readonly CNHealthInfo Info;
		INotifyDamageStateChanged[] notifyDamageStateChanged;
		INotifyDamage[] notifyDamage;
		INotifyDamage[] notifyDamagePlayer;
		IDamageModifier[] damageModifiers;
		IDamageModifier[] damageModifiersPlayer;
		INotifyKilled[] notifyKilled;
		INotifyKilled[] notifyKilledPlayer;

		SecondaryHealth[] secondaryHealthLayers;

		public int DisplayHP { get; private set; }

		public CNHealth(ActorInitializer init, CNHealthInfo info)
		{
			Info = info;
			MaxHP = HP = info.HP > 0 ? info.HP : 1;

			// Accept both HealthInit (vanilla) and CNHealthInit (saved by our editor)
			var healthInitValue = init.GetOrDefault<HealthInit>()?.Value
				?? init.GetOrDefault<CNHealthInit>()?.Value;
			if (healthInitValue != null)
				HP = (int)(healthInitValue.Value * (long)MaxHP / 100);

			DisplayHP = HP;
		}

		[VerifySync]
		public int HP { get; private set; }
		public int MaxHP { get; }

		public bool IsDead => HP <= 0;
		public bool RemoveOnDeath = true;

		public DamageState DamageState
		{
			get
			{
				if (HP <= 0)
					return DamageState.Dead;

				if (HP == MaxHP)
					return secondaryHealthLayers != null && secondaryHealthLayers.Any(l => l.NeedsRepair)
						? DamageState.Light
						: DamageState.Undamaged;

				if (HP * 100L < MaxHP * 25L)
					return DamageState.Critical;

				if (HP * 100L < MaxHP * 50L)
					return DamageState.Heavy;

				if (HP * 100L < MaxHP * 75L)
					return DamageState.Medium;

				return DamageState.Light;
			}
		}

		void INotifyCreated.Created(Actor self)
		{
			notifyDamageStateChanged = self.TraitsImplementing<INotifyDamageStateChanged>().ToArray();
			notifyDamage = self.TraitsImplementing<INotifyDamage>().ToArray();
			notifyDamagePlayer = self.Owner.PlayerActor.TraitsImplementing<INotifyDamage>().ToArray();
			damageModifiers = self.TraitsImplementing<IDamageModifier>().ToArray();
			damageModifiersPlayer = self.Owner.PlayerActor.TraitsImplementing<IDamageModifier>().ToArray();
			notifyKilled = self.TraitsImplementing<INotifyKilled>().ToArray();
			notifyKilledPlayer = self.Owner.PlayerActor.TraitsImplementing<INotifyKilled>().ToArray();

			var layers = self.TraitsImplementing<SecondaryHealth>().ToArray();
			secondaryHealthLayers = layers.Length > 0 ? layers : null;
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			notifyDamagePlayer = newOwner.PlayerActor.TraitsImplementing<INotifyDamage>().ToArray();
			damageModifiersPlayer = newOwner.PlayerActor.TraitsImplementing<IDamageModifier>().ToArray();
			notifyKilledPlayer = newOwner.PlayerActor.TraitsImplementing<INotifyKilled>().ToArray();
		}

		public void Resurrect(Actor self, Actor repairer)
		{
			if (!IsDead)
				return;

			HP = MaxHP;

			var ai = new AttackInfo
			{
				Attacker = repairer,
				Damage = new Damage(-MaxHP),
				DamageState = DamageState,
				PreviousDamageState = DamageState.Dead,
			};

			foreach (var nd in notifyDamage)
				nd.Damaged(self, ai);
			foreach (var nd in notifyDamagePlayer)
				nd.Damaged(self, ai);

			foreach (var nd in notifyDamageStateChanged)
				nd.DamageStateChanged(self, ai);

			if (Info.NotifyAppliedDamage && repairer != null && repairer.IsInWorld && !repairer.IsDead)
			{
				foreach (var nd in repairer.TraitsImplementing<INotifyAppliedDamage>())
					nd.AppliedDamage(repairer, self, ai);
				foreach (var nd in repairer.Owner.PlayerActor.TraitsImplementing<INotifyAppliedDamage>())
					nd.AppliedDamage(repairer, self, ai);
			}
		}

		public void InflictDamage(Actor self, Actor attacker, Damage damage, bool ignoreModifiers)
		{
			if (IsDead)
				return;

			var oldState = DamageState;

			// Apply damage modifiers
			if (!ignoreModifiers && damage.Value > 0)
			{
				var appliedDamage = (decimal)damage.Value;
				foreach (var dm in damageModifiers)
				{
					var modifier = dm.GetDamageModifier(attacker, damage);
					if (modifier != 100)
						appliedDamage *= modifier / 100m;
				}

				foreach (var dm in damageModifiersPlayer)
				{
					var modifier = dm.GetDamageModifier(attacker, damage);
					if (modifier != 100)
						appliedDamage *= modifier / 100m;
				}

				damage = new Damage((int)appliedDamage, damage.DamageTypes);
			}

			var notificationDamage = damage;

			// Route through SecondaryHealth layers for real hits only.
			// ignoreModifiers calls are internal bookkeeping and bypass layers.
			if (!ignoreModifiers && secondaryHealthLayers != null)
			{
				if (damage.Value > 0)
				{
					foreach (var layer in secondaryHealthLayers)
					{
						if (damage.Value <= 0)
							break;

						damage = layer.AbsorbDamage(damage);
					}
				}
				else if (damage.Value < 0)
				{
					foreach (var layer in secondaryHealthLayers)
					{
						if (damage.Value >= 0)
							break;

						if (!layer.AcceptsRepair(damage))
							continue;

						damage = layer.RepairDamage(damage);
					}
				}
			}

			HP = (HP - damage.Value).Clamp(0, MaxHP);

			var notifyAi = new AttackInfo
			{
				Attacker = attacker,
				Damage = notificationDamage,
				DamageState = DamageState,
				PreviousDamageState = oldState,
			};

			var ai = new AttackInfo
			{
				Attacker = attacker,
				Damage = damage,
				DamageState = DamageState,
				PreviousDamageState = oldState,
			};

			foreach (var nd in notifyDamage)
				nd.Damaged(self, notifyAi);
			foreach (var nd in notifyDamagePlayer)
				nd.Damaged(self, notifyAi);

			if (DamageState != oldState)
				foreach (var nd in notifyDamageStateChanged)
					nd.DamageStateChanged(self, ai);

			if (Info.NotifyAppliedDamage && attacker != null && attacker.IsInWorld && !attacker.IsDead)
			{
				foreach (var nd in attacker.TraitsImplementing<INotifyAppliedDamage>())
					nd.AppliedDamage(attacker, self, ai);
				foreach (var nd in attacker.Owner.PlayerActor.TraitsImplementing<INotifyAppliedDamage>())
					nd.AppliedDamage(attacker, self, ai);
			}

			if (HP == 0)
			{
				foreach (var nd in notifyKilled)
					nd.Killed(self, ai);
				foreach (var nd in notifyKilledPlayer)
					nd.Killed(self, ai);

				if (RemoveOnDeath)
					self.Dispose();
			}
		}

		public void Kill(Actor self, Actor attacker, BitSet<DamageType> damageTypes)
		{
			InflictDamage(self, attacker, new Damage(MaxHP, damageTypes), true);
		}

		void ITick.Tick(Actor self)
		{
			if (HP >= DisplayHP)
				DisplayHP = HP;
			else
				DisplayHP = (2 * DisplayHP + HP) / 3;
		}
	}
}
