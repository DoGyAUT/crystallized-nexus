#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	public enum WeatherState
	{
		Clear,
		Warning,
		Storm,
		Clearing
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Controls dynamic ion storm lifecycle (Clear/Warning/Storm/Clearing).",
		"Reads a lobby option to enable/disable. Grants a condition to all actors",
		"with a matching ExternalCondition during storm phases.",
		"Plays a music track during the storm. Use WeatherTintEffect for visual tint.")]
	public class WeatherControllerInfo : TraitInfo, ILobbyOptions
	{
		// ── Lobby option ─────────────────────────────────────────────
		[Desc("Descriptive label for the lobby option.")]
		public readonly string IonStormLabel = "Ion Storms";

		[Desc("Tooltip description for the lobby option.")]
		public readonly string IonStormDescription = "Periodic ion storms reduce visibility, disable radar, and damage units.";

		[Desc("Default lobby option value (enabled or disabled).")]
		public readonly bool IonStormDefault = true;

		[Desc("Prevent the option from being changed in the lobby.")]
		public readonly bool IonStormLocked = false;

		[Desc("Display the option in the lobby.")]
		public readonly bool IonStormVisible = true;

		[Desc("Display order for the lobby options panel.")]
		public readonly int IonStormDisplayOrder = 0;

		[Desc("Category for the ion storm option in the lobby.")]
		public readonly string IonStormCategory = null;

		// ── Timing ───────────────────────────────────────────────────
		[Desc("Minimum ticks in Clear state before a storm begins.")]
		public readonly int MinClearDuration = 4500;

		[Desc("Maximum ticks in Clear state before a storm begins.")]
		public readonly int MaxClearDuration = 12000;

		[Desc("Ticks for the Warning phase (sky darkening transition).")]
		public readonly int WarningDuration = 375;

		[Desc("Minimum ticks in full Storm state.")]
		public readonly int MinStormDuration = 1500;

		[Desc("Maximum ticks in full Storm state.")]
		public readonly int MaxStormDuration = 4500;

		[Desc("Ticks for the Clearing phase (storm fading out).")]
		public readonly int ClearingDuration = 375;

		// ── Music ────────────────────────────────────────────────────
		[Desc("Music track to play during the storm. Leave empty to keep normal music.")]
		public readonly string StormMusic = null;

		// ── Condition ────────────────────────────────────────────────
		[Desc("Condition to grant to all actors with a matching ExternalCondition",
			"during Warning, Storm, and Clearing phases.")]
		public readonly string Condition = "ionstorm";

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, "ionstorms",
				IonStormLabel, IonStormDescription,
				IonStormVisible, IonStormDisplayOrder,
				IonStormDefault, IonStormLocked, IonStormCategory);
		}

		public override object Create(ActorInitializer init) { return new WeatherController(init, this); }
	}

	public class WeatherController : ITick, INotifyCreated, IWorldLoaded
	{
		public readonly WeatherControllerInfo Info;
		readonly World world;

		bool enabled;
		int stateTicks;
		int stateDuration;
		readonly Dictionary<Actor, int> conditionTokens = [];
		bool conditionsGranted;

		MusicPlaylist musicPlaylist;
		MusicInfo previousTrack;

		/// <summary>True during Warning, Storm, and Clearing phases.</summary>
		public bool IsIonStormActive => enabled && State != WeatherState.Clear;

		/// <summary>Current weather state.</summary>
		public WeatherState State { get; private set; } = WeatherState.Clear;

		/// <summary>Current storm intensity, 0.0 (clear) to 1.0 (full storm).</summary>
		public float Intensity { get; private set; }

		public WeatherController(ActorInitializer init, WeatherControllerInfo info)
		{
			Info = info;
			world = init.World;
		}

		void INotifyCreated.Created(Actor self)
		{
			var options = self.World.LobbyInfo.GlobalSettings.LobbyOptions;
			enabled = options.TryGetValue("ionstorms", out var option)
				? option.IsEnabled
				: Info.IonStormDefault;
		}

		void IWorldLoaded.WorldLoaded(World w, Graphics.WorldRenderer wr)
		{
			if (!enabled)
				return;

			musicPlaylist = w.WorldActor.TraitOrDefault<MusicPlaylist>();
			SetState(WeatherState.Clear);
		}

		void ITick.Tick(Actor self)
		{
			if (!enabled)
				return;

			stateTicks++;

			switch (State)
			{
				case WeatherState.Clear:
					Intensity = 0f;
					if (stateTicks >= stateDuration)
						SetState(WeatherState.Warning);

					break;

				case WeatherState.Warning:
					Intensity = Math.Min((float)stateTicks / stateDuration, 1f);

					if (!conditionsGranted && stateTicks >= stateDuration / 2)
						GrantConditionToAll();

					if (stateTicks >= stateDuration)
					{
						SetState(WeatherState.Storm);
						StartStormMusic();
					}

					break;

				case WeatherState.Storm:
					Intensity = 1f;
					if (stateTicks >= stateDuration)
						SetState(WeatherState.Clearing);

					break;

				case WeatherState.Clearing:
					Intensity = Math.Max(1f - (float)stateTicks / stateDuration, 0f);
					if (stateTicks >= stateDuration)
					{
						RevokeConditionFromAll();
						StopStormMusic();
						SetState(WeatherState.Clear);
					}

					break;
			}
		}

		void SetState(WeatherState newState)
		{
			State = newState;
			stateTicks = 0;

			switch (newState)
			{
				case WeatherState.Clear:
					stateDuration = world.SharedRandom.Next(Info.MinClearDuration, Info.MaxClearDuration + 1);
					break;

				case WeatherState.Warning:
					stateDuration = Info.WarningDuration;
					break;

				case WeatherState.Storm:
					stateDuration = world.SharedRandom.Next(Info.MinStormDuration, Info.MaxStormDuration + 1);
					break;

				case WeatherState.Clearing:
					stateDuration = Info.ClearingDuration;
					break;
			}
		}

		// ── Conditions ───────────────────────────────────────────────
		void GrantConditionToAll()
		{
			if (conditionsGranted || string.IsNullOrEmpty(Info.Condition))
				return;

			conditionsGranted = true;

			foreach (var actor in world.Actors.Where(a => a.IsInWorld && !a.IsDead))
				TryGrantCondition(actor);

			world.ActorAdded += OnActorAdded;
		}

		void RevokeConditionFromAll()
		{
			if (!conditionsGranted)
				return;

			conditionsGranted = false;
			world.ActorAdded -= OnActorAdded;

			foreach (var kvp in conditionTokens.ToList())
			{
				var actor = kvp.Key;
				var token = kvp.Value;

				if (actor.IsDead || actor.Disposed)
					continue;

				var external = actor.TraitsImplementing<ExternalCondition>()
					.FirstOrDefault(e => e.Info.Condition == Info.Condition);

				external?.TryRevokeCondition(actor, this, token);
			}

			conditionTokens.Clear();
		}

		void TryGrantCondition(Actor actor)
		{
			if (conditionTokens.ContainsKey(actor))
				return;

			var external = actor.TraitsImplementing<ExternalCondition>()
				.FirstOrDefault(e => e.Info.Condition == Info.Condition);

			if (external == null)
				return;

			var token = external.GrantCondition(actor, this);
			if (token != Actor.InvalidConditionToken)
				conditionTokens[actor] = token;
		}

		void OnActorAdded(Actor actor)
		{
			if (conditionsGranted)
				TryGrantCondition(actor);
		}

		// ── Music ────────────────────────────────────────────────────
		void StartStormMusic()
		{
			if (string.IsNullOrEmpty(Info.StormMusic) || musicPlaylist == null)
				return;

			previousTrack = musicPlaylist.CurrentSong();

			if (!world.Map.Rules.Music.TryGetValue(Info.StormMusic, out var stormTrack))
				return;

			if (stormTrack.Exists)
				musicPlaylist.Play(stormTrack);
		}

		void StopStormMusic()
		{
			if (string.IsNullOrEmpty(Info.StormMusic) || musicPlaylist == null)
				return;

			if (previousTrack != null && previousTrack.Exists)
				musicPlaylist.Play(previousTrack);
			else
				musicPlaylist.Stop();

			previousTrack = null;
		}
	}
}
