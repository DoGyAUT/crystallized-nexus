#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus mod.
 */
#endregion

namespace OpenRA.Mods.CN.Traits.BotModules
{
	/// <summary>
	/// Bot diagnostics that survive the game session.
	/// <para>
	/// AIUtils.BotDebug writes through TextNotificationsManager, which puts the line in the in-game
	/// chat overlay and nowhere else. That is unusable for the questions these messages exist to
	/// answer — why one resource field beat another, why a wave gathered where it did — because
	/// reading them means scrolling chat during a live match and they are gone afterwards.
	/// </para>
	/// This writes the same line to the chat AND to the engine's debug.log channel, which Game.cs
	/// registers at startup. Gated on the same Debug.BotDebug setting, so nothing is written to disk
	/// unless bot debugging was asked for.
	/// </summary>
	public static class CNBotLog
	{
		public static void Debug(string format, params object[] args)
		{
			if (!Game.Settings.Debug.BotDebug)
				return;

			var line = format.FormatCurrent(args);

			// "{0}" rather than passing the formatted line as a format string: the line may itself
			// contain braces (cell coordinates print as "123,45" today, but nothing guarantees that),
			// and FormatCurrent would then throw on its own output.
			TextNotificationsManager.Debug("{0}", line);
			Log.Write("debug", line);
		}
	}
}
