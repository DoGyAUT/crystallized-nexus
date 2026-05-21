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
	[Desc("Occasionally plays combat-chatter voices when the unit opens fire or takes damage.",
		"Self-gating: does nothing unless the unit's VoiceSet defines the voice,",
		"so it can be added to a shared infantry template safely.")]
	public class CombatChatterInfo : TraitInfo
	{
		[VoiceReference]
		[Desc("Voice phrase to play when the unit attacks (from voices.yaml).")]
		public readonly string Voice = "Attack";

		[VoiceReference]
		[Desc("Voice phrase to play when the unit takes damage (from voices.yaml).")]
		public readonly string DamageVoice = "Feedback";

		[Desc("Minimum ticks between two chatter lines.")]
		public readonly int MinDelay = 250;

		[Desc("Maximum ticks between two chatter lines.")]
		public readonly int MaxDelay = 450;

		[Desc("Percent chance to actually speak when the cooldown has elapsed and the unit fires.")]
		public readonly int Chance = 10;

		[Desc("Percent chance to actually speak when the cooldown has elapsed and the unit takes damage.")]
		public readonly int DamageChance = 5;

		[Desc("Playback volume. Played positionally (distance-attenuated), so",
			"chatter is only audible near the unit, not map-wide.")]
		public readonly float Volume = 1f;

		public override object Create(ActorInitializer init) { return new CombatChatter(this); }
	}

	public class CombatChatter : INotifyAttack, INotifyDamage, ITick
	{
		readonly CombatChatterInfo info;
		int attackCooldown;
		int damageCooldown;

		public CombatChatter(CombatChatterInfo info)
		{
			this.info = info;
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
			TryPlayVoice(self, info.Voice, info.Chance, ref attackCooldown);
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (e.Damage.Value <= 0)
				return;

			TryPlayVoice(self, info.DamageVoice, info.DamageChance, ref damageCooldown);
		}

		void TryPlayVoice(Actor self, string voice, int chance, ref int cooldown)
		{
			if (cooldown > 0)
				return;

			// Voiced.PlayVoice -> Sound.PlayPredefined throws if the phrase is
			// not in the unit's voice pool, so units whose VoiceSet has no line
			// for this event must be skipped entirely.
			if (!self.HasVoice(voice))
				return;

			// One attempt per cooldown window, whether or not it speaks, so
			// chatter stays evenly spaced instead of clustering on volleys.
			cooldown = self.World.SharedRandom.Next(info.MinDelay, info.MaxDelay + 1);

			// PlayVoiceLocal -> positional + distance-attenuated, so combat
			// chatter is only heard near the unit.
			if (self.World.SharedRandom.Next(100) < chance)
				self.PlayVoiceLocal(voice, info.Volume);
		}
	}
}
