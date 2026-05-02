#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Adds a secondary health pool (ablative armor or regenerating shield).",
		"When used alongside CNHealth, damage is routed through this layer automatically.",
		"Set RegenerateRate to 0 for ablative, or > 0 for shield mode.",
		"Supports multiple instances via @ suffixes.")]
	public class SecondaryHealthInfo : ConditionalTraitInfo
	{
		[Desc("Maximum hit points for this layer.")]
		public readonly int MaxHP = 10000;

		[Desc("Starting hit points. -1 means start at MaxHP.")]
		public readonly int InitialHP = -1;

		[Desc("Armor type identifier for this layer.",
			"Reserved for future Versus-based damage scaling.")]
		public readonly string ArmorType = "Shield";

		[Desc("HP restored per regeneration step. 0 = ablative (no regeneration).")]
		public readonly int RegenerateRate = 0;

		[Desc("Ticks after last damage before regeneration begins.")]
		public readonly int RegenerateDelay = 100;

		[Desc("Ticks between each regeneration step.")]
		public readonly int RegenerateInterval = 5;

		[Desc("DamageTypes that bypass this layer entirely (damage goes straight to primary HP).")]
		public readonly BitSet<DamageType> BypassDamageTypes = default;

		[Desc("DamageTypes that partially penetrate this layer.",
			"PiercePercentage of damage leaks through to primary HP.")]
		public readonly BitSet<DamageType> PierceDamageTypes = default;

		[Desc("Percentage of damage that leaks to primary HP when PierceDamageTypes match (0-100).")]
		public readonly int PiercePercentage = 50;

		[Desc("DamageTypes that trigger healing on this layer instead of primary HP.",
			"E.g. RepairShield on a heal weapon routes healing to this layer.")]
		public readonly BitSet<DamageType> RepairDamageTypes = default;

		[GrantedConditionReference]
		[Desc("Condition granted while this layer has HP remaining (> 0).")]
		public readonly string FullCondition = null;

		[GrantedConditionReference]
		[Desc("Condition granted while this layer is depleted (== 0).")]
		public readonly string EmptyCondition = null;

		[Desc("Play this sound when the layer is depleted.")]
		public readonly string DepletedSound = null;

		[Desc("Play this sound when the layer finishes regenerating to full.")]
		public readonly string RechargedSound = null;

		[Desc("Selection bar color (RRGGBB hex).")]
		public readonly Color BarColor = Color.FromArgb(255, 64, 64, 255);

		public override object Create(ActorInitializer init) { return new SecondaryHealth(init.Self, this); }
	}

	public class SecondaryHealth : ConditionalTrait<SecondaryHealthInfo>, ITick, ISelectionBar, ISelectionBarAboveHealth
	{
		readonly Actor self;
		int damageCooldown;
		int regenTick;

		int fullConditionToken = Actor.InvalidConditionToken;
		int emptyConditionToken = Actor.InvalidConditionToken;

		public int CurrentHP { get; private set; }
		public int MaxHP => Info.MaxHP;
		public string ArmorType => Info.ArmorType;
		public bool NeedsRepair => !IsTraitDisabled && CurrentHP < Info.MaxHP;

		public SecondaryHealth(Actor self, SecondaryHealthInfo info)
			: base(info)
		{
			this.self = self;
			CurrentHP = info.InitialHP < 0 ? info.MaxHP : Math.Min(info.InitialHP, info.MaxHP);
		}

		protected override void Created(Actor self)
		{
			base.Created(self);
			UpdateConditions();
		}

		void UpdateConditions()
		{
			var hasHP = !IsTraitDisabled && CurrentHP > 0;
			var isEmpty = !IsTraitDisabled && CurrentHP <= 0;

			if (!string.IsNullOrEmpty(Info.FullCondition))
			{
				if (hasHP && fullConditionToken == Actor.InvalidConditionToken)
					fullConditionToken = self.GrantCondition(Info.FullCondition);
				else if (!hasHP && fullConditionToken != Actor.InvalidConditionToken)
					fullConditionToken = self.RevokeCondition(fullConditionToken);
			}

			if (!string.IsNullOrEmpty(Info.EmptyCondition))
			{
				if (isEmpty && emptyConditionToken == Actor.InvalidConditionToken)
					emptyConditionToken = self.GrantCondition(Info.EmptyCondition);
				else if (!isEmpty && emptyConditionToken != Actor.InvalidConditionToken)
					emptyConditionToken = self.RevokeCondition(emptyConditionToken);
			}
		}

		/// <summary>
		/// Called by CNHealth.InflictDamage for positive damage.
		/// Absorbs what it can and returns a new Damage with the passthrough remainder.
		/// </summary>
		public Damage AbsorbDamage(Damage incoming)
		{
			if (IsTraitDisabled || CurrentHP <= 0)
				return incoming;

			if (!Info.BypassDamageTypes.IsEmpty && incoming.DamageTypes.Overlaps(Info.BypassDamageTypes))
				return incoming;

			var pierceAmount = 0;
			if (!Info.PierceDamageTypes.IsEmpty && incoming.DamageTypes.Overlaps(Info.PierceDamageTypes))
				pierceAmount = incoming.Value * Info.PiercePercentage / 100;

			var absorbable = incoming.Value - pierceAmount;
			if (absorbable <= 0)
				return incoming;

			var absorbed = Math.Min(absorbable, CurrentHP);
			CurrentHP -= absorbed;

			damageCooldown = Info.RegenerateDelay;
			regenTick = 0;

			if (CurrentHP <= 0 && !string.IsNullOrEmpty(Info.DepletedSound))
				Game.Sound.Play(SoundType.World, Info.DepletedSound, self.CenterPosition);

			UpdateConditions();

			var overflow = absorbable - absorbed;
			return new Damage(pierceAmount + overflow, incoming.DamageTypes);
		}

		/// <summary>
		/// Returns true if this layer accepts healing for the given Damage.
		/// </summary>
		public bool AcceptsRepair(Damage healing)
		{
			if (IsTraitDisabled || Info.RepairDamageTypes.IsEmpty || CurrentHP >= Info.MaxHP)
				return false;

			return healing.DamageTypes.Overlaps(Info.RepairDamageTypes);
		}

		/// <summary>
		/// Called by CNHealth.InflictDamage for negative damage (healing).
		/// Consumes what it can and returns a new Damage with the unconsumed remainder.
		/// Input and output are both negative.
		/// </summary>
		public Damage RepairDamage(Damage healing)
		{
			if (IsTraitDisabled)
				return healing;

			var healAmount = -healing.Value;
			var missing = Info.MaxHP - CurrentHP;
			if (missing <= 0)
				return healing;

			var applied = Math.Min(healAmount, missing);
			CurrentHP += applied;

			UpdateConditions();

			return new Damage(-(healAmount - applied), healing.DamageTypes);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || Info.RegenerateRate <= 0 || CurrentHP >= Info.MaxHP)
				return;

			if (damageCooldown > 0)
			{
				damageCooldown--;
				return;
			}

			if (regenTick > 0)
			{
				regenTick--;
				return;
			}

			regenTick = Info.RegenerateInterval;
			CurrentHP = Math.Min(CurrentHP + Info.RegenerateRate, Info.MaxHP);

			if (CurrentHP >= Info.MaxHP && !string.IsNullOrEmpty(Info.RechargedSound))
				Game.Sound.Play(SoundType.World, Info.RechargedSound, self.CenterPosition);

			UpdateConditions();
		}

		float ISelectionBar.GetValue()
		{
			if (IsTraitDisabled || Info.MaxHP <= 0)
				return 0f;

			return (float)CurrentHP / Info.MaxHP;
		}

		bool ISelectionBar.DisplayWhenEmpty => false;
		Color ISelectionBar.GetColor() => Info.BarColor;

		protected override void TraitEnabled(Actor self) => UpdateConditions();
		protected override void TraitDisabled(Actor self) => UpdateConditions();
	}
}
