#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Graphics;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Widgets
{
	/// <summary>
	/// Minimal widget that renders an actor's "icon" sequence sprite,
	/// scaled to fit the widget bounds. Used by SelectionSubgroupLogic.
	/// </summary>
	public class SubgroupIconWidget : Widget
	{
		public Sprite IconSprite;
		public string IconPalette = "chrome";

		readonly WorldRenderer worldRenderer;

		[ObjectCreator.UseCtor]
		public SubgroupIconWidget(WorldRenderer worldRenderer)
		{
			this.worldRenderer = worldRenderer;
		}

		protected SubgroupIconWidget(SubgroupIconWidget other)
			: base(other)
		{
			worldRenderer = other.worldRenderer;
			IconSprite = other.IconSprite;
			IconPalette = other.IconPalette;
		}

		public override Widget Clone() => new SubgroupIconWidget(this);

		public override void Draw()
		{
			if (IconSprite == null)
				return;

			var rb = RenderBounds;
			var pal = worldRenderer.Palette(IconPalette);

			var spriteW = IconSprite.Size.X;
			var spriteH = IconSprite.Size.Y;

			if (spriteW <= 0 || spriteH <= 0)
				return;

			var scale = System.Math.Min(rb.Width / spriteW, rb.Height / spriteH);
			var drawW = spriteW * scale;
			var drawH = spriteH * scale;
			var drawX = rb.X + (rb.Width - drawW) / 2f;
			var drawY = rb.Y + (rb.Height - drawH) / 2f;

			Game.Renderer.SpriteRenderer.DrawSprite(IconSprite, pal, new float3(drawX, drawY, 0f), scale);
		}
	}
}
