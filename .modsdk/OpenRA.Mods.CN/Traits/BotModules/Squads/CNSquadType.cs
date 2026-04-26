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

namespace OpenRA.Mods.CN.Traits.BotModules.Squads
{
	public enum CNSquadType
	{
		// Standard types (mirroring vanilla)
		Assault,               // General ground attack, Mob-aware
		Rush,                  // Early attack with small force
		Air,                   // Air units
		AircraftAttack,        // Air strike: attack enemy base/targets, then return to rearm
		AircraftSupport,       // Air support: escort and assist allied squads
		Naval,                 // Naval units
		Protection,            // Reactive base defense (triggered by attack)

		// Artillery
		ArtilleryAssault,      // Follows Assault squads, hangs back, bombards
		ArtilleryDefense,      // Holds defensive line near base

		// Specialized ground
		SubterraneanAssault,   // SUBTANK: burrow approach → surface → attack → reborrow
		SubterraneanTransport, // SAPC: burrow → behind enemy lines → unload (ambush)
		Stealth,               // Cloaked units (STNK): priority targets, no auto-attack
		JumpJet,               // Jump-jet infantry: grouped, over terrain, ground targets
		Hover,                 // Hover units: can cross water/cliffs

		// Support
		Support,               // Repair vehicles / medics, follows Assault squads
		Transport,             // APC: load infantry → move → unload → return

		// Tactical
		Raider,                // Targets soft units (harvesters, arty), flees on resistance

		// Defensive
		Defense,               // Permanently stationed in base, guards perimeter
	}
}
