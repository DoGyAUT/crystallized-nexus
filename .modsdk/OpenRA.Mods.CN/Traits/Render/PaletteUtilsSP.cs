#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Mods.CN.Traits
{
	public enum RBGSwapMode { None, GRB, BGR, RBG, BRG, GBR, Random }

	public sealed class PaletteUtilSP
	{
		public static Color GetRBGSwappedColor(Color original, RBGSwapMode mode)
		{
			switch (mode)
			{
				case RBGSwapMode.GRB:
					return Color.FromArgb(original.A, original.G, original.R, original.B);

				case RBGSwapMode.BGR:
					return Color.FromArgb(original.A, original.B, original.G, original.R);

				case RBGSwapMode.RBG:
					return Color.FromArgb(original.A, original.R, original.B, original.G);

				case RBGSwapMode.BRG:
					return Color.FromArgb(original.A, original.B, original.R, original.G);

				case RBGSwapMode.GBR:
					return Color.FromArgb(original.A, original.G, original.B, original.R);

				default:
					return original;
			}
		}

		public static Color GetLightCastColor(Color original, float intensity, float3 lightcolor)
		{
			var r = (int)(original.R * lightcolor.X * intensity);
			var g = (int)(original.G * lightcolor.Y * intensity);
			var b = (int)(original.B * lightcolor.Z * intensity);
			return Color.FromArgb(original.A, r, g, b);
		}
	}

	public class GenerateLightRemapAfterRPGSwapped : IPaletteRemap
	{
		readonly Dictionary<int, Color> remapColors;

		public GenerateLightRemapAfterRPGSwapped(ImmutablePalette basePalette, RBGSwapMode rpgmode, float intensity, float3 lightcolor, ImmutableArray<int> rpgIndexs, ImmutableArray<int> lightIndexs)
		{
			remapColors = new Dictionary<int, Color>();
			rpgmode = rpgmode != RBGSwapMode.Random ? rpgmode : (RBGSwapMode)Game.CosmeticRandom.Next((int)RBGSwapMode.Random);

			if (rpgIndexs != null && rpgIndexs.Length != 0)
			{
				foreach (var i in rpgIndexs)
					remapColors.Add(i % 255, PaletteUtilSP.GetRBGSwappedColor(basePalette.GetColor(i % 255), rpgmode));

				for (var i = 0; i < 255; i++)
					if (!remapColors.ContainsKey(i))
						remapColors.Add(i, basePalette.GetColor(i));
			}
			else
				for (var i = 0; i < 255; i++)
					remapColors.Add(i, PaletteUtilSP.GetRBGSwappedColor(basePalette.GetColor(i), rpgmode));

			if (lightIndexs != null && lightIndexs.Length != 0)
			{
				foreach (var i in lightIndexs)
					remapColors[i % 255] = PaletteUtilSP.GetLightCastColor(remapColors[i % 255], intensity, lightcolor);
			}
			else
			{
				for (var i = 0; i < 255; i++)
					remapColors[i] = PaletteUtilSP.GetLightCastColor(remapColors[i], intensity, lightcolor);
			}
		}

		public GenerateLightRemapAfterRPGSwapped(IPalette basePalette, RBGSwapMode rpgmode, float intensity, float3 lightcolor, ImmutableArray<int> rpgIndexs, ImmutableArray<int> lightIndexs)
		{
			remapColors = new Dictionary<int, Color>();
			rpgmode = rpgmode != RBGSwapMode.Random ? rpgmode : (RBGSwapMode)Game.CosmeticRandom.Next((int)RBGSwapMode.Random);

			if (rpgIndexs != null && rpgIndexs.Length != 0)
			{
				foreach (var i in rpgIndexs)
					remapColors.Add(i % 255, PaletteUtilSP.GetRBGSwappedColor(basePalette.GetColor(i % 255), rpgmode));

				for (var i = 0; i < 255; i++)
					if (!remapColors.ContainsKey(i))
						remapColors.Add(i, basePalette.GetColor(i));
			}
			else
				for (var i = 0; i < 255; i++)
					remapColors.Add(i, PaletteUtilSP.GetRBGSwappedColor(basePalette.GetColor(i), rpgmode));

			if (lightIndexs != null && lightIndexs.Length != 0)
			{
				foreach (var i in lightIndexs)
					remapColors[i % 255] = PaletteUtilSP.GetLightCastColor(remapColors[i % 255], intensity, lightcolor);
			}
			else
			{
				for (var i = 0; i < 255; i++)
					remapColors[i] = PaletteUtilSP.GetLightCastColor(remapColors[i], intensity, lightcolor);
			}
		}

		Color IPaletteRemap.GetRemappedColor(Color original, int index)
		{
			return remapColors.TryGetValue(index, out var c)
				? c : original;
		}
	}
}
