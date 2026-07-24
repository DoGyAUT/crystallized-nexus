#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

namespace OpenRA.Mods.CN.Traits
{
	// Must stay in sync with the CnHandicapTiers value/label mapping in the engine's LobbyUtils.
	public static class CNHandicapTiers
	{
		static readonly string[] Names = { "Normal", "Easy", "Hard", "Brutal" };

		public static string Name(int handicap)
		{
			return handicap >= 0 && handicap < Names.Length ? Names[handicap] : "Normal";
		}
	}
}
