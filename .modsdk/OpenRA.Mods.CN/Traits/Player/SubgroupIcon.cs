#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Overrides the icon shown in the selection subgroup bar for this actor. " +
		"Takes priority over the BuildableInfo icon sequence.")]
	public class SubgroupIconInfo : TraitInfo<SubgroupIcon>
	{
		[Desc("Image (sequence set) to use for the icon. Defaults to the actor name.")]
		public readonly string Image = null;

		[SequenceReference(nameof(Image))]
		[Desc("Sequence within the image to use as the icon.")]
		public readonly string Sequence = "icon";

		[PaletteReference(nameof(IsPlayerPalette))]
		[Desc("Palette to use for the icon.")]
		public readonly string Palette = "chrome";

		[Desc("Custom palette is a player palette BaseName.")]
		public readonly bool IsPlayerPalette = false;
	}

	public class SubgroupIcon { }
}
