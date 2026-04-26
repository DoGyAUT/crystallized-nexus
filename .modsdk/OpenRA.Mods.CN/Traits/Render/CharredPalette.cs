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
using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Creates a desaturated and darkened copy of a source palette, suitable for charred/burned voxel husks.")]
	public class CharredPaletteInfo : TraitInfo
	{
		[FieldLoader.Require]
		[PaletteDefinition]
		[Desc("Name of the new charred palette to register.")]
		public readonly string Name;

		[FieldLoader.Require]
		[PaletteReference]
		[Desc("Name of the source palette to derive from.")]
		public readonly string BasePalette;

		[Desc("How much to desaturate the colors toward gray. 0 = original, 1 = full grayscale.")]
		public readonly float Desaturation = 0.75f;

		[Desc("Brightness multiplier applied after desaturation. 0 = black, 1 = unchanged.")]
		public readonly float Darkness = 0.30f;

		[Desc("Allow palette modifiers (e.g. day/night tint) to adjust this palette.")]
		public readonly bool AllowModifiers = true;

		public override object Create(ActorInitializer init) { return new CharredPalette(this); }
	}

	public class CharredPalette : ILoadsPalettes, IPaletteModifier
	{
		readonly CharredPaletteInfo info;

		public CharredPalette(CharredPaletteInfo info)
		{
			this.info = info;
		}

		void ILoadsPalettes.LoadPalettes(WorldRenderer wr)
		{
			// Register a placeholder palette; IPaletteModifier will fill it each frame.
			wr.AddPalette(info.Name, new ImmutablePalette(new uint[Palette.Size]), info.AllowModifiers);
		}

		void IPaletteModifier.AdjustPalette(IReadOnlyDictionary<string, MutablePalette> palettes)
		{
			if (!palettes.TryGetValue(info.BasePalette, out var src))
				return;

			if (!palettes.TryGetValue(info.Name, out var dst))
				return;

			for (var i = 0; i < Palette.Size; i++)
			{
				var c = src.GetColor(i);
				if (c.A == 0)
				{
					dst.SetColor(i, Color.Transparent);
					continue;
				}

				// Luminance for desaturation
				var lum = c.R * 0.299f + c.G * 0.587f + c.B * 0.114f;
				var desat = info.Desaturation;

				var r = c.R * (1f - desat) + lum * desat;
				var g = c.G * (1f - desat) + lum * desat;
				var b = c.B * (1f - desat) + lum * desat;

				// Darken
				r *= info.Darkness;
				g *= info.Darkness;
				b *= info.Darkness;

				dst.SetColor(i, Color.FromArgb(
					c.A,
					(byte)Math.Clamp(r, 0f, 255f),
					(byte)Math.Clamp(g, 0f, 255f),
					(byte)Math.Clamp(b, 0f, 255f)));
			}
		}
	}
}
