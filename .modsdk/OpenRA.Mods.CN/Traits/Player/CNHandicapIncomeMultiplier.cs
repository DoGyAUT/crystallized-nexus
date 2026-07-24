#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Modifies the value of resources delivered to this actor based on the owner's named handicap tier (Easy/Normal/Hard/Brutal).")]
	public class CNHandicapIncomeMultiplierInfo : TraitInfo
	{
		[Desc("Percentage modifier per named difficulty tier.")]
		public readonly Dictionary<string, int> Modifiers = new();

		public override object Create(ActorInitializer init) { return new CNHandicapIncomeMultiplier(this, init.Self); }
	}

	public class CNHandicapIncomeMultiplier : IResourceValueModifier
	{
		readonly CNHandicapIncomeMultiplierInfo info;
		readonly Actor self;

		public CNHandicapIncomeMultiplier(CNHandicapIncomeMultiplierInfo info, Actor self)
		{
			this.info = info;
			this.self = self;
		}

		int IResourceValueModifier.GetResourceValueModifier()
		{
			return info.Modifiers.TryGetValue(CNHandicapTiers.Name(self.Owner.Handicap), out var modifier) ? modifier : 100;
		}
	}
}
