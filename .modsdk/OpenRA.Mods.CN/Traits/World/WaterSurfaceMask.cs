#region Copyright & License Information
/*
 * Crystallized Nexus Mod
 * Palette-index based water surface mask for terrain post-process passes.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Primitives;

namespace OpenRA.Mods.CN.Traits
{
	readonly record struct WaterSurfaceMaskRect(short X, short Y, short Width, short Height);

	sealed class WaterSurfaceMask : IDisposable
	{
		static readonly int[] ChannelOffsets = [2, 1, 0, 3];

		readonly Map map;
		readonly bool[] waterIndices;
		readonly HashSet<int> templateIds;
		readonly DefaultTileCache tileCache;
		readonly Dictionary<TerrainTile, ImmutableArray<WaterSurfaceMaskRect>> rectsByTile = [];
		readonly bool inverted;
		readonly float uvSentinel;

		public readonly bool Enabled;
		public readonly ImmutableArray<CPos> Cells;
		public readonly int MaxVertexCount;

		public WaterSurfaceMask(Map map, int[] paletteIndexRanges, int[] maskedTemplates, bool inverted = false, float uvSentinel = 0f)
		{
			this.map = map;
			this.inverted = inverted;
			this.uvSentinel = uvSentinel;
			if (paletteIndexRanges.Length == 0 || maskedTemplates.Length == 0 || map.Rules.TerrainInfo is not DefaultTerrain terrainInfo)
			{
				Cells = [];
				return;
			}

			Enabled = true;
			waterIndices = BuildIndexMask(paletteIndexRanges);
			templateIds = new HashSet<int>(maskedTemplates);
			tileCache = new DefaultTileCache(terrainInfo);

			var cells = ImmutableArray.CreateBuilder<CPos>();
			var rectCount = 0;
			foreach (var cell in map.AllCells)
			{
				var rects = GetRects(map.Tiles[cell]);
				if (rects.Length == 0)
					continue;

				cells.Add(cell);
				rectCount += rects.Length;
			}

			Cells = cells.ToImmutable();
			MaxVertexCount = rectCount * 6;
		}

		static bool[] BuildIndexMask(int[] ranges)
		{
			var indices = new bool[256];
			for (var i = 0; i < ranges.Length; i += 2)
			{
				var min = ranges[i];
				var max = i + 1 < ranges.Length ? ranges[i + 1] : min;
				if (min > max)
					(min, max) = (max, min);

				min = min.Clamp(0, 255);
				max = max.Clamp(0, 255);
				for (var j = min; j <= max; j++)
					indices[j] = true;
			}

			return indices;
		}

		public bool UsesTemplate(TerrainTile tile)
		{
			return templateIds?.Contains(tile.Type) ?? false;
		}

		ImmutableArray<WaterSurfaceMaskRect> GetRects(TerrainTile tile)
		{
			if (!UsesTemplate(tile))
				return [];

			if (rectsByTile.TryGetValue(tile, out var rects))
				return rects;

			rects = BuildRects(tile);
			rectsByTile.Add(tile, rects);
			return rects;
		}

		ImmutableArray<WaterSurfaceMaskRect> BuildRects(TerrainTile tile)
		{
			var sprite = tileCache.TileSprite(tile, 0);
			if (sprite == tileCache.MissingTile || sprite.Channel == TextureChannel.RGBA)
				return [];

			var channel = ChannelOffsets[(int)sprite.Channel];
			var data = sprite.Sheet.GetData();
			var stride = sprite.Sheet.Size.Width * 4;
			var rects = ImmutableArray.CreateBuilder<WaterSurfaceMaskRect>();
			var open = new Dictionary<(short X, short Width), int>();

			for (var y = 0; y < sprite.Bounds.Height; y++)
			{
				var rowKeys = new HashSet<(short X, short Width)>();
				var row = (sprite.Bounds.Top + y) * stride + sprite.Bounds.Left * 4 + channel;
				var runStart = -1;

				for (var x = 0; x < sprite.Bounds.Width; x++)
				{
					var isWater = inverted ? !waterIndices[data[row + x * 4]] : waterIndices[data[row + x * 4]];
					if (isWater)
					{
						if (runStart < 0)
							runStart = x;
					}
					else if (runStart >= 0)
					{
						rowKeys.Add(((short)runStart, (short)(x - runStart)));
						runStart = -1;
					}
				}

				if (runStart >= 0)
					rowKeys.Add(((short)runStart, (short)(sprite.Bounds.Width - runStart)));

				foreach (var kv in open)
				{
					if (rowKeys.Contains(kv.Key))
						continue;

					var height = y - kv.Value;
					rects.Add(new WaterSurfaceMaskRect(kv.Key.X, (short)kv.Value, kv.Key.Width, (short)height));
				}

				foreach (var key in open.Keys.ToImmutableArray())
					if (!rowKeys.Contains(key))
						open.Remove(key);

				foreach (var key in rowKeys)
					if (!open.ContainsKey(key))
						open.Add(key, y);
			}

			foreach (var kv in open)
			{
				var height = sprite.Bounds.Height - kv.Value;
				rects.Add(new WaterSurfaceMaskRect(kv.Key.X, (short)kv.Value, kv.Key.Width, (short)height));
			}

			return rects.ToImmutable();
		}

		public int AddVertices(WorldRenderer wr, CPos cell, RenderPostProcessPassTexturedVertex[] vertices, int i,
			int2 topLeft, int2 bottomRight, int padding)
		{
			var tile = map.Tiles[cell];
			var rects = GetRects(tile);
			if (rects.Length == 0)
				return i;

			var sprite = tileCache.TileSprite(tile, 0);
			var cellOrigin = map.CenterOfCell(cell) - new WVec(0, 0, map.Grid.Ramps[map.Ramp[cell]].CenterHeightOffset);
			var origin = wr.Screen3DPosition(cellOrigin) + sprite.Offset - 0.5f * sprite.Size;

			if (origin.X + sprite.Size.X + padding < topLeft.X
				|| origin.X - padding > bottomRight.X
				|| origin.Y + sprite.Size.Y + padding < topLeft.Y
				|| origin.Y - padding > bottomRight.Y)
				return i;

			foreach (var rect in rects)
			{
				var left = origin.X + rect.X;
				var right = left + rect.Width;
				var top = origin.Y + rect.Y;
				var bottom = top + rect.Height;

				vertices[i++] = new RenderPostProcessPassTexturedVertex(left, top, uvSentinel, uvSentinel);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(right, top, uvSentinel, uvSentinel);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(right, bottom, uvSentinel, uvSentinel);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(right, bottom, uvSentinel, uvSentinel);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(left, bottom, uvSentinel, uvSentinel);
				vertices[i++] = new RenderPostProcessPassTexturedVertex(left, top, uvSentinel, uvSentinel);
			}

			return i;
		}

		public void Dispose()
		{
			tileCache?.Dispose();
		}
	}
}
