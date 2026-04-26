#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 */
#endregion

using System;
using System.IO;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Mods.CN.UtilityCommands
{
	sealed class ExportSpriteFramesCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--export-frames";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 3;
		}

		[Desc("SPRITEFILE OUTPREFIX", "Export the frames of a sprite file (e.g. .tem/.shp) as PNG images using a grayscale palette.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var spriteFile = args[1];
			var outPrefix = args[2];

			ISpriteFrame[] frames;
			TypeDictionary metadata;

			using (var stream = File.OpenRead(spriteFile))
				frames = FrameLoader.GetFrames(stream, utility.ModData.SpriteLoaders, Path.GetFileName(spriteFile), out metadata);

			if (frames == null || frames.Length == 0)
				throw new InvalidDataException($"{spriteFile} could not be parsed as a sprite file.");

			var grayscalePalette = BuildGrayscalePalette();
			for (var i = 0; i < frames.Length; i++)
			{
				var frame = frames[i];
				var path = $"{outPrefix}-{i:D2}.png";

				switch (frame.Type)
				{
					case SpriteFrameType.Indexed8:
						new Png(frame.Data, SpriteFrameType.Indexed8, frame.Size.Width, frame.Size.Height, grayscalePalette).Save(path);
						break;
					case SpriteFrameType.Bgra32:
					case SpriteFrameType.Bgr24:
					case SpriteFrameType.Rgba32:
					case SpriteFrameType.Rgb24:
						new Png(frame.Data, frame.Type, frame.Size.Width, frame.Size.Height).Save(path);
						break;
					default:
						throw new InvalidOperationException($"Unsupported frame type {frame.Type} in {spriteFile}.");
				}

				Console.WriteLine(path);
			}
		}

		static Color[] BuildGrayscalePalette()
		{
			var palette = new Color[Palette.Size];
			for (var i = 0; i < palette.Length; i++)
				palette[i] = Color.FromArgb(i, i, i);

			return palette;
		}
	}
}
