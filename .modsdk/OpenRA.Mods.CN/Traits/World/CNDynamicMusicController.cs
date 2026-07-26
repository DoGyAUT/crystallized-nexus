#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	public enum CNMusicState { Peace, Tension, Combat, BigBattle }

	[TraitLocation(SystemActors.World)]
	[Desc("Layered dynamic soundtrack: crossfades 4 always-looping stems (Peace/Tension/Combat/BigBattle) per",
		"faction based on the followed player's (RenderPlayer, falling back to LocalPlayer) combat situation.",
		"Factions without a configured score are left untouched (normal MusicPlaylist jukebox keeps playing).",
		"Attach CNCombatSignalReporter to shared unit/building templates to feed combat events into this trait.")]
	public class CNDynamicMusicControllerInfo : TraitInfo
	{
		[Desc("Peace stem filename per faction Side (e.g. GDI, Nod). Sides missing here never activate dynamic music.")]
		public readonly Dictionary<string, string> PeaceStems = new();

		[Desc("Tension stem filename per faction Side.")]
		public readonly Dictionary<string, string> TensionStems = new();

		[Desc("Combat stem filename per faction Side.")]
		public readonly Dictionary<string, string> CombatStems = new();

		[Desc("Big battle stem filename per faction Side.")]
		public readonly Dictionary<string, string> BigBattleStems = new();

		[Desc("Radius around the tracked player's buildings that is scanned for enemy presence (Tension state).")]
		public readonly WDist TensionScanRadius = WDist.FromCells(20);

		[Desc("Ticks between tension scans / base position cache refreshes.")]
		public readonly int TensionScanInterval = 50;

		[Desc("Combat events within this radius of one of the owner's buildings count at full weight.")]
		public readonly WDist BaseProximityRadius = WDist.FromCells(15);

		[Desc("Weight multiplier for combat events far from any of the owner's buildings (field skirmishes).")]
		public readonly float FieldWeightMultiplier = 0.4f;

		[Desc("Accumulated weight required to enter the Combat state.")]
		public readonly float CombatThreshold = 5f;

		[Desc("Accumulated weight required to enter the BigBattle state.")]
		public readonly float BigBattleThreshold = 20f;

		[Desc("Ticks between weight decay steps.")]
		public readonly int DecayInterval = 125;

		[Desc("Fraction of weight removed per decay interval (0.0-1.0). At the default DecayInterval,",
			"0.2 takes ~30s to cool from BigBattleThreshold down to CombatThreshold.")]
		public readonly float DecayRate = 0.2f;

		[Desc("Upper clamp for accumulated per-player combat weight.")]
		public readonly float MaxWeight = 100f;

		[Desc("Per-tick volume step used to fade stems toward their target volume.")]
		public readonly float FadeStep = 0.008f;

		public override object Create(ActorInitializer init) { return new CNDynamicMusicController(this, init.Self); }
	}

	public class CNDynamicMusicController : ITick, INotifyActorDisposing
	{
		readonly CNDynamicMusicControllerInfo info;
		readonly World world;

		readonly Dictionary<Player, float> combatWeight = [];
		readonly Dictionary<Player, WPos[]> baseCache = [];

		readonly ISound[] stems = new ISound[4];
		readonly float[] currentVolume = new float[4];

		string attemptedFaction;
		string loadedFaction;
		bool jukeboxPaused;
		bool tensionActive;
		int scanTicks;
		int decayTicks;

		public CNDynamicMusicController(CNDynamicMusicControllerInfo info, Actor self)
		{
			this.info = info;
			world = self.World;
		}

		public void ReportCombatEvent(Player owner, WPos pos, float weight)
		{
			if (owner == null || owner.NonCombatant)
				return;

			var multiplier = info.FieldWeightMultiplier;
			if (baseCache.TryGetValue(owner, out var bases))
			{
				var radiusSq = (long)info.BaseProximityRadius.Length * info.BaseProximityRadius.Length;
				if (bases.Any(basePos => (pos - basePos).LengthSquared <= radiusSq))
					multiplier = 1f;
			}

			combatWeight[owner] = System.Math.Min(info.MaxWeight, combatWeight.GetValueOrDefault(owner) + weight * multiplier);
		}

		void ITick.Tick(Actor self)
		{
			var player = world.RenderPlayer ?? world.LocalPlayer;

			if (player == null)
			{
				FadeTo(0f, 0f, 0f, 0f);
				return;
			}

			var faction = player.Faction.Side ?? player.Faction.InternalName;
			if (faction != attemptedFaction)
			{
				attemptedFaction = faction;
				LoadFaction(faction);
			}

			if (loadedFaction == null)
				return;

			if (--scanTicks <= 0)
			{
				scanTicks = info.TensionScanInterval;
				RefreshBaseCache(player);
				tensionActive = ScanForTension(player);
			}

			if (--decayTicks <= 0)
			{
				decayTicks = info.DecayInterval;
				DecayWeights();
			}

			UpdateMix(player);
		}

		void LoadFaction(string faction)
		{
			StopStems();
			loadedFaction = null;

			if (!info.PeaceStems.TryGetValue(faction, out var peace))
				return;

			info.TensionStems.TryGetValue(faction, out var tension);
			info.CombatStems.TryGetValue(faction, out var combat);
			info.BigBattleStems.TryGetValue(faction, out var bigBattle);

			stems[0] = Game.Sound.PlayLooped(SoundType.World, peace);
			stems[1] = tension != null ? Game.Sound.PlayLooped(SoundType.World, tension) : null;
			stems[2] = combat != null ? Game.Sound.PlayLooped(SoundType.World, combat) : null;
			stems[3] = bigBattle != null ? Game.Sound.PlayLooped(SoundType.World, bigBattle) : null;

			for (var i = 0; i < 4; i++)
			{
				currentVolume[i] = 0f;

				// The Sound Effects volume slider force-sets the gain of every active non-music/video
				// sound whenever it changes. Exempt our stems so they keep following our own mix instead
				// of being flattened to a single volume by the settings screen.
				if (stems[i] != null)
					Game.Sound.ExemptFromVolumeSlider(stems[i]);
			}

			loadedFaction = faction;

			if (!jukeboxPaused)
			{
				Game.Sound.StopMusic();
				jukeboxPaused = true;
			}
		}

		void RefreshBaseCache(Player player)
		{
			baseCache[player] = world.ActorsHavingTrait<Building>()
				.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld)
				.Select(a => a.CenterPosition)
				.ToArray();
		}

		bool ScanForTension(Player player)
		{
			if (!baseCache.TryGetValue(player, out var bases) || bases.Length == 0)
				return false;

			return bases.Any(basePos => world.FindActorsInCircle(basePos, info.TensionScanRadius)
				.Any(a => a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy));
		}

		void DecayWeights()
		{
			foreach (var p in combatWeight.Keys.ToArray())
			{
				var w = combatWeight[p] * (1f - info.DecayRate);
				if (w < 0.01f)
					combatWeight.Remove(p);
				else
					combatWeight[p] = w;
			}
		}

		void UpdateMix(Player player)
		{
			var weight = combatWeight.GetValueOrDefault(player);

			var state = CNMusicState.Peace;
			if (weight >= info.BigBattleThreshold)
				state = CNMusicState.BigBattle;
			else if (weight >= info.CombatThreshold)
				state = CNMusicState.Combat;
			else if (tensionActive)
				state = CNMusicState.Tension;

			// Exclusive layers: only the current intensity's stem plays at a time. FadeStep-based
			// crossfading in Step() still gives a smooth transition between whichever stems are
			// ramping up/down, but they don't stay stacked on top of each other afterwards.
			FadeTo(
				state == CNMusicState.Peace ? 1f : 0f,
				state == CNMusicState.Tension ? 1f : 0f,
				state == CNMusicState.Combat ? 1f : 0f,
				state == CNMusicState.BigBattle ? 1f : 0f);
		}

		void FadeTo(float peace, float tension, float combat, float bigBattle)
		{
			Step(0, peace);
			Step(1, tension);
			Step(2, combat);
			Step(3, bigBattle);
		}

		void Step(int index, float target)
		{
			if (stems[index] == null)
				return;

			if (currentVolume[index] < target)
				currentVolume[index] = System.Math.Min(target, currentVolume[index] + info.FadeStep);
			else if (currentVolume[index] > target)
				currentVolume[index] = System.Math.Max(target, currentVolume[index] - info.FadeStep);

			stems[index].Volume = currentVolume[index] * Game.Settings.Sound.MusicVolume;
		}

		void StopStems()
		{
			for (var i = 0; i < 4; i++)
			{
				if (stems[i] != null)
				{
					Game.Sound.UnexemptFromVolumeSlider(stems[i]);
					Game.Sound.StopSound(stems[i]);
				}

				stems[i] = null;
			}
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			StopStems();
		}
	}
}
