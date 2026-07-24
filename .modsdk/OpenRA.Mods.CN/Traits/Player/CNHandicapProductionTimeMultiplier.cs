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
	[Desc("Modifies the production time of this actor based on the producer's named handicap tier (Easy/Normal/Hard/Brutal).")]
	public class CNHandicapProductionTimeMultiplierInfo : TraitInfo<CNHandicapProductionTimeMultiplier>, IProductionTimeModifierInfo
	{
		[Desc("Percentage modifier per named difficulty tier.")]
		public readonly Dictionary<string, int> Modifiers = new();

		int IProductionTimeModifierInfo.GetProductionTimeModifier(TechTree techTree, string queue)
		{
			return Modifiers.TryGetValue(CNHandicapTiers.Name(techTree.Owner.Handicap), out var modifier) ? modifier : 100;
		}
	}

	public class CNHandicapProductionTimeMultiplier { }
}
