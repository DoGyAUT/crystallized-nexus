#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Tick-based day/night cycle. Produces the clear-weather base ambient tint",
		"for the current time of day. Does NOT write TintPostProcessEffect itself -",
		"WeatherTintEffect consumes this base and remains the sole tint writer so the",
		"ion-storm tint can layer on top. Purely visual; no gameplay effect.",
		"The lobby dropdown picks the day length (or 'Frozen' = static at StartHour).")]
	public class DayNightCycleInfo : TraitInfo, ILobbyOptions
	{
		// ── Lobby option ─────────────────────────────────────────────
		[Desc("Descriptive label for the lobby option.")]
		public readonly string CycleLabel = "Day/Night Cycle";

		[Desc("Tooltip description for the lobby option.")]
		public readonly string CycleDescription = "Animate ambient lighting over a full day. 'Frozen' keeps the map's start time.";

		[Desc("Default lobby value: frozen, short, medium or long.")]
		public readonly string CycleDefault = "frozen";

		[Desc("Prevent the option from being changed in the lobby.")]
		public readonly bool CycleLocked = false;

		[Desc("Display the option in the lobby.")]
		public readonly bool CycleVisible = true;

		[Desc("Display order for the lobby options panel.")]
		public readonly int CycleDisplayOrder = 1;

		[Desc("Category for the option in the lobby.")]
		public readonly string CycleCategory = null;

		[Desc("Ticks for a full day at the 'short' setting.")]
		public readonly int ShortTicksPerDay = 18000;

		[Desc("Ticks for a full day at the 'medium' setting.")]
		public readonly int MediumTicksPerDay = 36000;

		[Desc("Ticks for a full day at the 'long' setting.")]
		public readonly int LongTicksPerDay = 72000;

		// ── Start time (lobby) ───────────────────────────────────────
		[Desc("Fallback hour of day (0-23) if the start-time lobby option",
			"is unavailable.")]
		public readonly int StartHour = 12;

		[Desc("Descriptive label for the start-time lobby option.")]
		public readonly string StartLabel = "Start Time";

		[Desc("Tooltip description for the start-time lobby option.")]
		public readonly string StartDescription = "Time of day the match begins (and the static time when Frozen).";

		[Desc("Default lobby value: dawn, morning, noon, afternoon, dusk or night.")]
		public readonly string StartDefault = "noon";

		[Desc("Prevent the start-time option from being changed in the lobby.")]
		public readonly bool StartLocked = false;

		[Desc("Display the start-time option in the lobby.")]
		public readonly bool StartVisible = true;

		[Desc("Display order for the start-time lobby option.")]
		public readonly int StartDisplayOrder = 5;

		[Desc("Category for the start-time lobby option.")]
		public readonly string StartCategory = null;

		// ── Keyframe tints (R, G, B, Ambient) ────────────────────────
		[Desc("Ambient tint at midnight (hour 0 / 24). Calibrated as moonlight:",
			"~75% brightness with a subtle cool/blue cast so units stay readable.")]
		public readonly float NightRed = 0.78f;
		public readonly float NightGreen = 0.85f;
		public readonly float NightBlue = 1.00f;
		public readonly float NightAmbient = 0.75f;

		[Desc("Ambient tint at dawn (hour 6).")]
		public readonly float DawnRed = 1.05f;
		public readonly float DawnGreen = 0.92f;
		public readonly float DawnBlue = 0.80f;
		public readonly float DawnAmbient = 0.90f;

		[Desc("Ambient tint at midday (hour 12).")]
		public readonly float DayRed = 1.00f;
		public readonly float DayGreen = 1.00f;
		public readonly float DayBlue = 1.00f;
		public readonly float DayAmbient = 1.00f;

		[Desc("Ambient tint at dusk (hour 18).")]
		public readonly float DuskRed = 1.10f;
		public readonly float DuskGreen = 0.85f;
		public readonly float DuskBlue = 0.70f;
		public readonly float DuskAmbient = 0.85f;

		// ── Phase conditions (opt-in) ────────────────────────────────
		// Granted to every actor that has a matching ExternalCondition for
		// the active phase (only one phase condition active at a time).
		// Inert by default: nothing happens unless an actor opts in via
		// ExternalCondition, so the cycle stays purely visual otherwise.
		[Desc("Hour [0-23] at which the dawn phase begins.")]
		public readonly int DawnStartHour = 5;

		[Desc("Hour [0-23] at which the full-day phase begins.")]
		public readonly int DayStartHour = 8;

		[Desc("Hour [0-23] at which the dusk phase begins.")]
		public readonly int DuskStartHour = 17;

		[Desc("Hour [0-23] at which the night phase begins.")]
		public readonly int NightStartHour = 20;

		[GrantedConditionReference]
		[Desc("Condition granted during the dawn phase. Empty to disable.")]
		public readonly string DawnCondition = "daynight-dawn";

		[GrantedConditionReference]
		[Desc("Condition granted during the full-day phase. Empty to disable.")]
		public readonly string DayCondition = "daynight-day";

		[GrantedConditionReference]
		[Desc("Condition granted during the dusk phase. Empty to disable.")]
		public readonly string DuskCondition = "daynight-dusk";

		[GrantedConditionReference]
		[Desc("Condition granted during the night phase. Empty to disable.")]
		public readonly string NightCondition = "daynight-night";

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			var values = new Dictionary<string, string>
			{
				{ "frozen", "Frozen" },
				{ "short", "Short" },
				{ "medium", "Medium" },
				{ "long", "Long" },
			};

			yield return new LobbyOption(map, "daynight",
				CycleLabel, CycleDescription, CycleVisible, CycleDisplayOrder,
				values, CycleDefault, CycleLocked, CycleCategory);

			var startValues = new Dictionary<string, string>
			{
				{ "dawn", "Dawn" },
				{ "morning", "Morning" },
				{ "noon", "Noon" },
				{ "afternoon", "Afternoon" },
				{ "dusk", "Dusk" },
				{ "night", "Night" },
			};

			yield return new LobbyOption(map, "daynightstart",
				StartLabel, StartDescription, StartVisible, StartDisplayOrder,
				startValues, StartDefault, StartLocked, StartCategory);
		}

		public override object Create(ActorInitializer init) { return new DayNightCycle(this); }
	}

	public enum DayNightPhase { Dawn, Day, Dusk, Night }

	public class DayNightCycle : INotifyCreated, IWorldLoaded, ITick, INotifyActorDisposing
	{
		readonly DayNightCycleInfo info;

		bool progress;
		int ticksPerDay;
		int startHour;
		int phaseTicks;

		World world;
		DayNightPhase? currentPhase;
		string grantedCondition;
		readonly Dictionary<Actor, int> conditionTokens = [];
		bool actorAddedSubscribed;

		/// <summary>Base ambient tint for the current time of day (clear weather).</summary>
		public float BaseRed { get; private set; }
		public float BaseGreen { get; private set; }
		public float BaseBlue { get; private set; }
		public float BaseAmbient { get; private set; }

		/// <summary>Day fraction, 0.0 = midnight, 0.5 = noon.</summary>
		public float CurrentPhase01 { get; private set; }

		/// <summary>0.0 at noon, 1.0 at midnight.</summary>
		public float NightFactor01 { get; private set; }

		public DayNightCycle(DayNightCycleInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			var value = self.World.LobbyInfo.GlobalSettings.OptionOrDefault("daynight", info.CycleDefault);
			switch (value)
			{
				case "short": ticksPerDay = info.ShortTicksPerDay; progress = true; break;
				case "medium": ticksPerDay = info.MediumTicksPerDay; progress = true; break;
				case "long": ticksPerDay = info.LongTicksPerDay; progress = true; break;
				default: ticksPerDay = info.MediumTicksPerDay; progress = false; break;
			}

			var start = self.World.LobbyInfo.GlobalSettings.OptionOrDefault("daynightstart", info.StartDefault);
			startHour = start switch
			{
				"dawn" => 6,
				"morning" => 9,
				"noon" => 12,
				"afternoon" => 15,
				"dusk" => 18,
				"night" => 0,
				_ => ((info.StartHour % 24) + 24) % 24
			};
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;
			phaseTicks = (int)((startHour / 24f) * ticksPerDay) % ticksPerDay;
			Recompute();
		}

		void ITick.Tick(Actor self)
		{
			if (progress)
			{
				phaseTicks = (phaseTicks + 1) % ticksPerDay;
				Recompute();
			}
		}

		void Recompute()
		{
			CurrentPhase01 = ticksPerDay > 0 ? phaseTicks / (float)ticksPerDay : 0f;

			// Hour of day in [0, 24).
			var hour = CurrentPhase01 * 24f;

			// Piecewise lerp across the four anchors:
			// Night@0 -> Dawn@6 -> Day@12 -> Dusk@18 -> Night@24.
			float r, g, b, a;
			if (hour < 6f)
			{
				var t = hour / 6f;
				r = Lerp(info.NightRed, info.DawnRed, t);
				g = Lerp(info.NightGreen, info.DawnGreen, t);
				b = Lerp(info.NightBlue, info.DawnBlue, t);
				a = Lerp(info.NightAmbient, info.DawnAmbient, t);
			}
			else if (hour < 12f)
			{
				var t = (hour - 6f) / 6f;
				r = Lerp(info.DawnRed, info.DayRed, t);
				g = Lerp(info.DawnGreen, info.DayGreen, t);
				b = Lerp(info.DawnBlue, info.DayBlue, t);
				a = Lerp(info.DawnAmbient, info.DayAmbient, t);
			}
			else if (hour < 18f)
			{
				var t = (hour - 12f) / 6f;
				r = Lerp(info.DayRed, info.DuskRed, t);
				g = Lerp(info.DayGreen, info.DuskGreen, t);
				b = Lerp(info.DayBlue, info.DuskBlue, t);
				a = Lerp(info.DayAmbient, info.DuskAmbient, t);
			}
			else
			{
				var t = (hour - 18f) / 6f;
				r = Lerp(info.DuskRed, info.NightRed, t);
				g = Lerp(info.DuskGreen, info.NightGreen, t);
				b = Lerp(info.DuskBlue, info.NightBlue, t);
				a = Lerp(info.DuskAmbient, info.NightAmbient, t);
			}

			BaseRed = r;
			BaseGreen = g;
			BaseBlue = b;
			BaseAmbient = a;

			// 1 at midnight, 0 at noon (smooth).
			NightFactor01 = (float)((Math.Cos(CurrentPhase01 * 2.0 * Math.PI) + 1.0) * 0.5);

			// Phase detection -> grant/revoke world-actor conditions on change.
			if (world != null)
			{
				var phase = PhaseOf(hour);
				if (phase != currentPhase)
					SwitchPhase(phase);
			}
		}

		DayNightPhase PhaseOf(float hour)
		{
			var h = (int)hour;
			if (h >= info.NightStartHour || h < info.DawnStartHour) return DayNightPhase.Night;
			if (h < info.DayStartHour) return DayNightPhase.Dawn;
			if (h < info.DuskStartHour) return DayNightPhase.Day;
			return DayNightPhase.Dusk;
		}

		string ConditionFor(DayNightPhase p)
		{
			return p switch
			{
				DayNightPhase.Dawn => info.DawnCondition,
				DayNightPhase.Day => info.DayCondition,
				DayNightPhase.Dusk => info.DuskCondition,
				_ => info.NightCondition,
			};
		}

		void SwitchPhase(DayNightPhase newPhase)
		{
			// Revoke the previous phase's grants (uses the condition string
			// that was actually granted, not the new one).
			RevokeAll();

			currentPhase = newPhase;
			grantedCondition = ConditionFor(newPhase);

			if (!actorAddedSubscribed)
			{
				world.ActorAdded += OnActorAdded;
				actorAddedSubscribed = true;
			}

			if (string.IsNullOrEmpty(grantedCondition))
				return;

			foreach (var actor in world.Actors.Where(a => a.IsInWorld && !a.IsDead))
				TryGrantCondition(actor);
		}

		void TryGrantCondition(Actor actor)
		{
			if (string.IsNullOrEmpty(grantedCondition))
				return;

			if (conditionTokens.ContainsKey(actor))
				return;

			var external = actor.TraitsImplementing<ExternalCondition>()
				.FirstOrDefault(e => e.Info.Condition == grantedCondition);

			if (external == null)
				return;

			var token = external.GrantCondition(actor, this);
			if (token != Actor.InvalidConditionToken)
				conditionTokens[actor] = token;
		}

		void RevokeAll()
		{
			if (conditionTokens.Count == 0 || string.IsNullOrEmpty(grantedCondition))
			{
				conditionTokens.Clear();
				return;
			}

			foreach (var kvp in conditionTokens.ToList())
			{
				var actor = kvp.Key;
				var token = kvp.Value;

				if (actor.IsDead || actor.Disposed)
					continue;

				var external = actor.TraitsImplementing<ExternalCondition>()
					.FirstOrDefault(e => e.Info.Condition == grantedCondition);

				external?.TryRevokeCondition(actor, this, token);
			}

			conditionTokens.Clear();
		}

		void OnActorAdded(Actor actor)
		{
			TryGrantCondition(actor);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (world != null && actorAddedSubscribed)
			{
				world.ActorAdded -= OnActorAdded;
				actorAddedSubscribed = false;
			}

			RevokeAll();
		}

		static float Lerp(float from, float to, float t) { return from + (to - from) * t; }
	}
}
