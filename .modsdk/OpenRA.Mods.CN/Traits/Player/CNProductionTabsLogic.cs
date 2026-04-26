#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Linq;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Widgets.Logic
{
	public class CNProductionTabsLogic : ChromeLogic
	{
		readonly ProductionTabsWidget tabs;
		readonly World world;

		void SetupProductionGroupButton(ProductionTypeButtonWidget button)
		{
			if (button == null)
				return;

			void SelectTab(bool reverse)
			{
				if (tabs.QueueGroup == button.ProductionGroup)
					tabs.SelectNextTab(reverse);
				else
					tabs.QueueGroup = button.ProductionGroup;

				tabs.PickUpCompletedBuilding();
			}

			button.IsDisabled = () => !tabs.Groups[button.ProductionGroup].Tabs.Any(t => t.Queue.BuildableItems().Any());
			button.OnMouseUp = mi => SelectTab(mi.Modifiers.HasModifier(Modifiers.Shift));
			button.OnKeyPress = e => SelectTab(e.Modifiers.HasModifier(Modifiers.Shift));
			button.OnClick = () => SelectTab(false);
			button.IsHighlighted = () => tabs.QueueGroup == button.ProductionGroup;

			var chromeName = button.ProductionGroup.ToLowerInvariant();
			var icon = button.Get<ImageWidget>("ICON");
			icon.GetImageName = () => button.IsDisabled() ? chromeName + "-disabled" :
				tabs.Groups[button.ProductionGroup].Alert ? chromeName + "-alert" : chromeName;
		}

		[ObjectCreator.UseCtor]
		public CNProductionTabsLogic(Widget widget, World world)
		{
			this.world = world;
			tabs = widget.Get<ProductionTabsWidget>("PRODUCTION_TABS");
			world.ActorAdded += tabs.ActorChanged;
			world.ActorRemoved += tabs.ActorChanged;
			Game.BeforeGameStart += UnregisterEvents;

			var typesContainer = Ui.Root.Get(tabs.TypesContainer);
			foreach (var i in typesContainer.Children)
				SetupProductionGroupButton(i as ProductionTypeButtonWidget);

			var background = Ui.Root.GetOrNull(tabs.BackgroundContainer);
			if (background != null)
			{
				var palette = tabs.Parent.Get<ProductionPaletteWidget>(tabs.PaletteWidget);
				var rowTemplate = background.Get("ROW_TEMPLATE");
				var bottomCap = background.GetOrNull("BOTTOM_CAP");

				void UpdateBackground(int _, int icons)
				{
					var rows = Math.Max(palette.MinimumRows, (icons + palette.Columns - 1) / palette.Columns);
					rows = Math.Min(rows, palette.MaximumRows);

					background.RemoveChildren();

					var rowHeight = rowTemplate.Bounds.Height;
					for (var i = 0; i < rows; i++)
					{
						var row = rowTemplate.Clone();
						row.Bounds.Y = i * rowHeight;
						background.AddChild(row);
					}

					if (bottomCap == null)
					{
						tabs.Bounds.Y = rows * rowHeight;
						return;
					}

					var cap = bottomCap.Clone();
					cap.Bounds.Y = rows * rowHeight;
					background.AddChild(cap);
					tabs.Bounds.Y = cap.Bounds.Y + Math.Max(0, (cap.Bounds.Height - tabs.Bounds.Height) / 2);
				}

				palette.OnIconCountChanged += UpdateBackground;
				UpdateBackground(0, 0);
			}

			var ticker = widget.GetOrNull<LogicTickerWidget>("PRODUCTION_TICKER");
			if (ticker != null)
			{
				ticker.OnTick = () =>
				{
					if (tabs.CurrentQueue != null && tabs.CurrentQueue.BuildableItems().Any())
						return;

					foreach (var b in typesContainer.Children)
					{
						if (b is not ProductionTypeButtonWidget button || button.IsDisabled())
							continue;

						button.OnClick();
						break;
					}
				};
			}
		}

		void UnregisterEvents()
		{
			Game.BeforeGameStart -= UnregisterEvents;
			world.ActorAdded -= tabs.ActorChanged;
			world.ActorRemoved -= tabs.ActorChanged;
		}
	}
}
