#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Grants weapon range and sight range bonuses when the actor stands on terrain above BaseHeight.",
		"Designed for CN's RMG maps where flat ground is at height 2.")]
	public class HeightAdvantageBonusInfo : TraitInfo
	{
		[Desc("Terrain height at which no bonus is granted. Negative differences are clamped to zero (no malus).")]
		public readonly int BaseHeight = 2;

		[Desc("Percentage bonus to weapon range per height level above BaseHeight.")]
		public readonly int RangeBonusPerLevel = 5;

		[Desc("Percentage bonus to sight range per height level above BaseHeight.")]
		public readonly int SightBonusPerLevel = 8;

		[Desc("Maximum number of height levels that grant a bonus.")]
		public readonly int MaxBonusLevels = 4;

		public override object Create(ActorInitializer init) { return new HeightAdvantageBonus(init, this); }
	}

	public class HeightAdvantageBonus : IRangeModifier, IRevealsShroudModifier
	{
		readonly HeightAdvantageBonusInfo info;
		readonly Actor self;

		public HeightAdvantageBonus(ActorInitializer init, HeightAdvantageBonusInfo info)
		{
			this.info = info;
			self = init.Self;
		}

		int BonusLevels()
		{
			return Math.Clamp(self.World.Map.Height[self.Location] - info.BaseHeight, 0, info.MaxBonusLevels);
		}

		int IRangeModifier.GetRangeModifier()
		{
			return 100 + BonusLevels() * info.RangeBonusPerLevel;
		}

		int IRevealsShroudModifier.GetRevealsShroudModifier()
		{
			return 100 + BonusLevels() * info.SightBonusPerLevel;
		}
	}
}
