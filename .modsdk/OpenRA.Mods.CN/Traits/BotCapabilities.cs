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

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Declares AI capability tags used by CNSquadManagerBotModule for threat detection.")]
	public class BotCapabilitiesInfo : TraitInfo
	{
		[Desc("Capability tags, e.g.: Infantry, Aircraft, Cloak, HeavySiege, Raider.")]
		public readonly string[] Capabilities = [];

		IReadOnlySet<string> capabilitySet;
		public IReadOnlySet<string> CapabilitySet =>
			capabilitySet ??= new HashSet<string>(Capabilities, System.StringComparer.OrdinalIgnoreCase);

		public override object Create(ActorInitializer init) => new BotCapabilities(this);
	}

	public class BotCapabilities
	{
		public readonly BotCapabilitiesInfo Info;
		public BotCapabilities(BotCapabilitiesInfo info) { Info = info; }
	}
}
