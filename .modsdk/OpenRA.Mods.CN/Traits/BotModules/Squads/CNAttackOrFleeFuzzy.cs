#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using AI.Fuzzy.Library;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules.Squads
{
	/// <summary>
	/// CN-specific fuzzy logic for attack/flee decisions.
	/// Provides Default (standard) and Raider (aggressive flee) instances.
	/// </summary>
	sealed class CNAttackOrFleeFuzzy
	{
		// --- Default rules (same as vanilla) ---
		static readonly string[] DefaultRulesNormalOwnHealth =
		[
			"if ((OwnHealth is Normal) " +
			"and ((EnemyHealth is NearDead) or (EnemyHealth is Injured) or (EnemyHealth is Normal)) " +
			"and ((RelativeAttackPower is Weak) or (RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Attack"
		];

		static readonly string[] DefaultRulesInjuredOwnHealth =
		[
			"if ((OwnHealth is Injured) " +
			"and (EnemyHealth is NearDead) " +
			"and ((RelativeAttackPower is Weak) or (RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Attack",

			"if ((OwnHealth is Injured) " +
			"and ((EnemyHealth is Injured) or (EnemyHealth is Normal)) " +
			"and ((RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Attack",

			"if ((OwnHealth is Injured) " +
			"and ((EnemyHealth is Injured) or (EnemyHealth is Normal)) " +
			"and (RelativeAttackPower is Weak) " +
			"and (RelativeSpeed is Slow)) " +
			"then AttackOrFlee is Attack",

			"if ((OwnHealth is Injured) " +
			"and ((EnemyHealth is Injured) or (EnemyHealth is Normal)) " +
			"and (RelativeAttackPower is Weak) " +
			"and ((RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Flee",

			"if ((OwnHealth is Injured) " +
			"and ((EnemyHealth is NearDead) or (EnemyHealth is Injured) or (EnemyHealth is Normal)) " +
			"and ((RelativeAttackPower is Weak) or (RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and (RelativeSpeed is Slow)) " +
			"then AttackOrFlee is Attack"
		];

		static readonly string[] DefaultRulesNearDeadOwnHealth =
		[
			"if ((OwnHealth is NearDead) " +
			"and ((EnemyHealth is NearDead) or (EnemyHealth is Injured)) " +
			"and ((RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal))) " +
			"then AttackOrFlee is Attack",

			"if ((OwnHealth is NearDead) " +
			"and ((EnemyHealth is NearDead) or (EnemyHealth is Injured)) " +
			"and (RelativeAttackPower is Weak) " +
			"and ((RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Flee",

			"if ((OwnHealth is NearDead) " +
			"and (EnemyHealth is Normal) " +
			"and (RelativeAttackPower is Weak) " +
			"and ((RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Flee",

			"if (OwnHealth is NearDead) " +
			"and (EnemyHealth is Normal) " +
			"and ((RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and (RelativeSpeed is Fast) " +
			"then AttackOrFlee is Flee",

			"if (OwnHealth is NearDead) " +
			"and (EnemyHealth is Injured) " +
			"and (RelativeAttackPower is Equal) " +
			"and (RelativeSpeed is Fast) " +
			"then AttackOrFlee is Flee"
		];

		// --- Raider rules: flee unless clearly winning ---
		static readonly string[] RaiderRulesNormalOwnHealth =
		[

			// A fresh raider force that is clearly stronger should actually open the fight. This case
			// used to match no rule at all: Normal enemy health was only covered by the Weak/Equal
			// flee rule, while the Strong attack rule required the enemy to be damaged already. The
			// fuzzy result was then NaN, which CanAttack deliberately treats as Flee, so raiders turned
			// away from exactly the isolated full-health target they were built to pick off.
			"if ((OwnHealth is Normal) " +
			"and (EnemyHealth is Normal) " +
			"and (RelativeAttackPower is Strong) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Attack",

			// Only attack if clearly stronger
			"if ((OwnHealth is Normal) " +
			"and ((EnemyHealth is NearDead) or (EnemyHealth is Injured)) " +
			"and ((RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Attack",

			// Flee otherwise
			"if ((OwnHealth is Normal) " +
			"and (EnemyHealth is Normal) " +
			"and ((RelativeAttackPower is Weak) or (RelativeAttackPower is Equal)) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Flee"
		];

		static readonly string[] RaiderRulesInjuredOwnHealth =
		[
			"if ((OwnHealth is Injured) " +
			"and (EnemyHealth is NearDead) " +
			"and (RelativeAttackPower is Strong) " +
			"and ((RelativeSpeed is Slow) or (RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Attack",

			"if ((OwnHealth is Injured) " +
			"and ((EnemyHealth is Injured) or (EnemyHealth is Normal)) " +
			"and ((RelativeAttackPower is Weak) or (RelativeAttackPower is Equal) or (RelativeAttackPower is Strong)) " +
			"and ((RelativeSpeed is Equal) or (RelativeSpeed is Fast))) " +
			"then AttackOrFlee is Flee"
		];

		static readonly string[] RaiderRulesNearDeadOwnHealth =
		[
			"if (OwnHealth is NearDead) " +
			"then AttackOrFlee is Flee"
		];

		// --- Singleton instances ---
		public static readonly CNAttackOrFleeFuzzy Default =
			new(null, null, null);

		public static readonly CNAttackOrFleeFuzzy Raider =
			new(RaiderRulesNormalOwnHealth, RaiderRulesInjuredOwnHealth, RaiderRulesNearDeadOwnHealth);

		// --- Fuzzy engine ---
		readonly MamdaniFuzzySystem fuzzyEngine = new();

		CNAttackOrFleeFuzzy(
			IEnumerable<string> rulesNormal,
			IEnumerable<string> rulesInjured,
			IEnumerable<string> rulesNearDead)
		{
			lock (fuzzyEngine)
			{
				var playerHealth = new FuzzyVariable("OwnHealth", 0.0, 100.0);
				playerHealth.Terms.Add(new FuzzyTerm("NearDead", new TrapezoidMembershipFunction(0, 0, 20, 40)));
				playerHealth.Terms.Add(new FuzzyTerm("Injured", new TrapezoidMembershipFunction(30, 50, 50, 70)));
				playerHealth.Terms.Add(new FuzzyTerm("Normal", new TrapezoidMembershipFunction(50, 80, 100, 100)));
				fuzzyEngine.Input.Add(playerHealth);

				var enemyHealth = new FuzzyVariable("EnemyHealth", 0.0, 100.0);
				enemyHealth.Terms.Add(new FuzzyTerm("NearDead", new TrapezoidMembershipFunction(0, 0, 20, 40)));
				enemyHealth.Terms.Add(new FuzzyTerm("Injured", new TrapezoidMembershipFunction(30, 50, 50, 70)));
				enemyHealth.Terms.Add(new FuzzyTerm("Normal", new TrapezoidMembershipFunction(50, 80, 100, 100)));
				fuzzyEngine.Input.Add(enemyHealth);

				var relPower = new FuzzyVariable("RelativeAttackPower", 0.0, 1000.0);
				relPower.Terms.Add(new FuzzyTerm("Weak", new TrapezoidMembershipFunction(0, 0, 70, 90)));
				relPower.Terms.Add(new FuzzyTerm("Equal", new TrapezoidMembershipFunction(85, 100, 100, 115)));
				relPower.Terms.Add(new FuzzyTerm("Strong", new TrapezoidMembershipFunction(110, 150, 150, 1000)));
				fuzzyEngine.Input.Add(relPower);

				var relSpeed = new FuzzyVariable("RelativeSpeed", 0.0, 1000.0);
				relSpeed.Terms.Add(new FuzzyTerm("Slow", new TrapezoidMembershipFunction(0, 0, 70, 90)));
				relSpeed.Terms.Add(new FuzzyTerm("Equal", new TrapezoidMembershipFunction(85, 100, 100, 115)));
				relSpeed.Terms.Add(new FuzzyTerm("Fast", new TrapezoidMembershipFunction(110, 150, 150, 1000)));
				fuzzyEngine.Input.Add(relSpeed);

				var decision = new FuzzyVariable("AttackOrFlee", 0.0, 50.0);
				decision.Terms.Add(new FuzzyTerm("Attack", new TrapezoidMembershipFunction(0, 15, 15, 30)));
				decision.Terms.Add(new FuzzyTerm("Flee", new TrapezoidMembershipFunction(25, 35, 35, 50)));
				fuzzyEngine.Output.Add(decision);

				foreach (var rule in rulesNormal ?? DefaultRulesNormalOwnHealth)
					fuzzyEngine.Rules.Add(fuzzyEngine.ParseRule(rule));
				foreach (var rule in rulesInjured ?? DefaultRulesInjuredOwnHealth)
					fuzzyEngine.Rules.Add(fuzzyEngine.ParseRule(rule));
				foreach (var rule in rulesNearDead ?? DefaultRulesNearDeadOwnHealth)
					fuzzyEngine.Rules.Add(fuzzyEngine.ParseRule(rule));
			}
		}

		public bool CanAttack(IReadOnlyCollection<Actor> ownUnits, IReadOnlyCollection<Actor> enemyUnits, double attackThresholdBoost = 0.0)
		{
			double attackChance;
			var inputValues = new Dictionary<FuzzyVariable, double>();
			lock (fuzzyEngine)
			{
				inputValues.Add(fuzzyEngine.InputByName("OwnHealth"), NormalizedHealth(ownUnits, 100));
				inputValues.Add(fuzzyEngine.InputByName("EnemyHealth"), NormalizedHealth(enemyUnits, 100));
				inputValues.Add(fuzzyEngine.InputByName("RelativeAttackPower"), RelativePower(ownUnits, enemyUnits));
				inputValues.Add(fuzzyEngine.InputByName("RelativeSpeed"), RelativeSpeed(ownUnits, enemyUnits));

				var result = fuzzyEngine.Calculate(inputValues);
				attackChance = result[fuzzyEngine.OutputByName("AttackOrFlee")];
			}

			// Clamp the boost so the Flee term stays reachable (Flee plateau starts near 35).
			var boost = Math.Clamp(attackThresholdBoost, 0.0, 10.0);
			return !double.IsNaN(attackChance) && attackChance < 30.0 + boost;
		}

		static float NormalizedHealth(IEnumerable<Actor> actors, int normalize)
		{
			var sumMax = 0;
			var sumCur = 0;
			foreach (var a in actors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;
				if (a.Info.HasTraitInfo<IHealthInfo>())
				{
					sumMax += a.Trait<IHealth>().MaxHP;
					sumCur += a.Trait<IHealth>().HP;
				}
			}

			if (sumMax == 0)
				return 0.0f;

			return (int)((long)sumCur * normalize / sumMax);
		}

		static float RelativePower(IReadOnlyCollection<Actor> own, IReadOnlyCollection<Actor> enemy)
		{
			return RelativeValue(own, enemy, 100, SumOfValues<AttackBaseInfo>, a =>
			{
				var sum = 0;
				foreach (var arm in a.TraitsImplementing<Armament>())
				{
					// Conditional alternatives are still traits on the actor. Counting both sides of a
					// condition (BikeMissile plus its elite replacement, deployed plus mobile guns, ...)
					// rated a unit as if it could fire every loadout at once and sent it into fights its
					// currently enabled weapons could not carry.
					if (arm.IsTraitDisabled || arm.IsTraitPaused)
						continue;

					var burst = arm.Weapon.Burst;
					var delay = (arm.Weapon.ReloadDelay +
						arm.Weapon.BurstDelays[0] * (burst - 1)).Clamp(1, 200);
					foreach (var wh in arm.Weapon.Warheads.OfType<DamageWarhead>())
						sum += wh.Damage * burst / delay * 100;
				}

				return sum;
			});
		}

		static float RelativeSpeed(IReadOnlyCollection<Actor> own, IReadOnlyCollection<Actor> enemy)
		{
			return RelativeValue(own, enemy, 100, Average<MobileInfo>,
				a => a.Info.TraitInfo<MobileInfo>().Speed);
		}

		static float RelativeValue(
			IReadOnlyCollection<Actor> own,
			IReadOnlyCollection<Actor> enemy,
			float normalize,
			Func<IReadOnlyCollection<Actor>, Func<Actor, int>, float> func,
			Func<Actor, int> getValue)
		{
			if (enemy.Count == 0) return 999.0f;
			if (own.Count == 0) return 0.0f;
			return (func(own, getValue) / func(enemy, getValue) * normalize).Clamp(0.0f, 999.0f);
		}

		static float SumOfValues<T>(IEnumerable<Actor> actors, Func<Actor, int> getValue)
			where T : ITraitInfoInterface
		{
			var sum = 0;
			foreach (var a in actors)
				if (!a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<T>())
					sum += getValue(a);
			return sum;
		}

		static float Average<T>(IEnumerable<Actor> actors, Func<Actor, int> getValue)
			where T : ITraitInfoInterface
		{
			var sum = 0;
			var count = 0;
			foreach (var a in actors)
			{
				if (!a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<T>())
				{
					sum += getValue(a);
					count++;
				}
			}

			return count == 0 ? 0.0f : sum / count;
		}
	}
}
