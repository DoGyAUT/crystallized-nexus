#region Copyright & License Information
/*
 * Copyright 2019-2025 The Crystallized Nexus Developers (see CREDITS)
 * This file is part of Crystallized Nexus, which is free software.
 * It is made available to you under the terms of the GNU General Public
 * License as published by the Free Software Foundation, either version 3
 * of the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Generates a palette with a single RGB color and a linear alpha gradient across all 256 indices.",
		"Index 0 = fully opaque, Index 255 = fully transparent (or reversed via Invert).")]
	public class AlphaGradientPaletteInfo : TraitInfo
	{
		[FieldLoader.Require]
		[PaletteDefinition]
		[Desc("Internal palette name.")]
		public readonly string Name;

		[Desc("Red component of the palette color.")]
		public readonly int R = 0;

		[Desc("Green component of the palette color.")]
		public readonly int G = 0;

		[Desc("Blue component of the palette color.")]
		public readonly int B = 0;

		[Desc("If true, index 0 = transparent and index 255 = opaque (matching SHP brightness-to-index mapping).",
			"If false, index 0 = opaque and index 255 = transparent.")]
		public readonly bool Invert = true;

		[Desc("Index used for fully transparent pixels (e.g. background).")]
		public readonly int TransparentIndex = 0;

		[Desc("Allow palette modifiers to adjust this palette.")]
		public readonly bool AllowModifiers = true;

		public override object Create(ActorInitializer init) { return new AlphaGradientPalette(this); }
	}

	public class AlphaGradientPalette : ILoadsPalettes
	{
		readonly AlphaGradientPaletteInfo info;

		public AlphaGradientPalette(AlphaGradientPaletteInfo info)
		{
			this.info = info;
		}

		void ILoadsPalettes.LoadPalettes(WorldRenderer wr)
		{
			var r = (byte)Math.Clamp(info.R, 0, 255);
			var g = (byte)Math.Clamp(info.G, 0, 255);
			var b = (byte)Math.Clamp(info.B, 0, 255);

			var colors = new uint[Palette.Size];
			for (var i = 0; i < Palette.Size; i++)
			{
				if (i == info.TransparentIndex)
				{
					colors[i] = 0; // Fully transparent
					continue;
				}

				// Linear alpha: Invert=true means higher index = more opaque
				var alpha = info.Invert ? (byte)i : (byte)(255 - i);

				colors[i] = (uint)((alpha << 24) | (r << 16) | (g << 8) | b);
			}

			wr.AddPalette(info.Name, new ImmutablePalette(colors), info.AllowModifiers);
		}
	}
}
