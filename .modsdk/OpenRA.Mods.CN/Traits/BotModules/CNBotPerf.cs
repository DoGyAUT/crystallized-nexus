#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits.BotModules
{
	/// <summary>
	/// Where the bots spend their tick, per module and per player.
	/// <para>
	/// The engine's own long-tick logging charges everything a bot does to one line -
	/// "Trait: ModularBot" - because that is the trait it ticks. Late-game profiling runs kept
	/// showing that single entry as the largest cost in the game without saying which of the
	/// fourteen CN modules produced it, so this measures each one on the mod side.
	/// </para>
	/// Two things it deliberately does differently from the engine hook: it accumulates instead of
	/// logging per occurrence (a threshold-triggered line per slow tick is itself a cost, and it
	/// hides steady overhead that never crosses the threshold), and it reports per window as well as
	/// cumulative, because the question being asked is what grows as the match goes on.
	/// <para>
	/// Gated on Debug.EnableSimulationPerfLogging and written to the perf log, so a measuring run
	/// does not need Debug.BotDebug and the bot's own chatter - several MB of it over a long match -
	/// stays out of the numbers.
	/// </para>
	/// </summary>
	public static class CNBotPerf
	{
		// One report per this many world ticks. 5000 is a little over three minutes of game time:
		// short enough to show the late-game trend across a match, long enough that the report
		// itself (one line per module per bot) stays a rounding error in the log.
		const int ReportInterval = 5000;

		sealed class Entry
		{
			public long WindowStopwatchTicks;
			public long TotalStopwatchTicks;
			public int WindowCalls;
			public int TotalCalls;
			public long WindowMaxStopwatchTicks;
		}

		static readonly Dictionary<(Player Owner, string Label), Entry> Entries = [];
		static int nextReportTick = ReportInterval;
		static int lastSeenTick;

		public static Scope Sample(IBot bot, string label)
		{
			return Sample(bot?.Player, label);
		}

		/// <summary>For measuring inside helpers that were never handed the IBot.</summary>
		public static Scope Sample(Player player, string label)
		{
			if (!Game.Settings.Debug.EnableSimulationPerfLogging || player == null)
				return default;

			return new Scope((player, label), Stopwatch.GetTimestamp(), player.World);
		}

		public readonly struct Scope : IDisposable
		{
			readonly (Player Owner, string Label) key;
			readonly long start;
			readonly World world;

			public Scope((Player Owner, string Label) key, long start, World world)
			{
				this.key = key;
				this.start = start;
				this.world = world;
			}

			public void Dispose()
			{
				if (key.Owner == null)
					return;

				Record(key, Stopwatch.GetTimestamp() - start);
				MaybeReport(world);
			}
		}

		static void Record((Player Owner, string Label) key, long elapsed)
		{
			if (!Entries.TryGetValue(key, out var entry))
				Entries[key] = entry = new Entry();

			entry.WindowStopwatchTicks += elapsed;
			entry.TotalStopwatchTicks += elapsed;
			entry.WindowCalls++;
			entry.TotalCalls++;
			if (elapsed > entry.WindowMaxStopwatchTicks)
				entry.WindowMaxStopwatchTicks = elapsed;
		}

		static void MaybeReport(World world)
		{
			if (world == null)
				return;

			var tick = world.WorldTick;

			// Statics outlive a match: the process goes back to the menu and starts another game with
			// the tick counter back at zero. Without this the second match reports one merged set of
			// numbers and then waits out the first match's remaining interval before saying anything.
			if (tick < lastSeenTick)
			{
				Entries.Clear();
				nextReportTick = ReportInterval;
			}

			lastSeenTick = tick;

			if (tick < nextReportTick)
				return;

			nextReportTick = tick + ReportInterval;
			Report(tick);
		}

		static void Report(int tick)
		{
			var frequency = (double)Stopwatch.Frequency;

			// Sorted by what the window cost, so the top of each report is the thing to look at.
			foreach (var pair in Entries.OrderByDescending(e => e.Value.WindowStopwatchTicks))
			{
				var entry = pair.Value;
				if (entry.WindowCalls == 0)
					continue;

				// Invariant formatting: these lines exist to be aggregated by script afterwards, and
				// the culture-aware path writes decimal commas that turn "4,4" into two fields.
				Log.Write("perf", "CNBotPerf [tick {0}] {1} | {2} | window {3:0.0} ms in {4} calls (max {5:0.0} ms) | total {6:0.0} ms in {7} calls"
					.FormatInvariant(
						tick,
						pair.Key.Owner,
						pair.Key.Label,
						1000 * entry.WindowStopwatchTicks / frequency,
						entry.WindowCalls,
						1000 * entry.WindowMaxStopwatchTicks / frequency,
						1000 * entry.TotalStopwatchTicks / frequency,
						entry.TotalCalls));

				entry.WindowStopwatchTicks = 0;
				entry.WindowCalls = 0;
				entry.WindowMaxStopwatchTicks = 0;
			}
		}
	}
}
