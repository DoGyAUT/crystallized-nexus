#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Linq;
using OpenRA.Mods.CN.Widgets;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Widgets.Logic
{
	public class CNProductionSelectionLogic : ChromeLogic
	{
		readonly CNProductionPaletteWidget palette;
		readonly World world;

		ProductionQueue[] SelectedQueues(string group)
		{
			return world.Selection.Actors
				.Where(a => a.IsInWorld && a.Owner == world.LocalPlayer)
				.SelectMany(a => a.TraitsImplementing<ProductionQueue>())
				.Where(q => q.Enabled && (q.Info.Group ?? q.Info.Type) == group)
				.OrderBy(q => q.Actor.ActorID)
				.Distinct()
				.ToArray();
		}

		ProductionQueue[] AvailableQueues(string group)
		{
			var actorQueues = world.Actors
				.Where(a => a.IsInWorld && a.Owner == world.LocalPlayer)
				.SelectMany(a => a.TraitsImplementing<ProductionQueue>());

			var playerQueues = world.LocalPlayer.PlayerActor.TraitsImplementing<ProductionQueue>();

			return actorQueues.Concat(playerQueues)
				.Where(q => q.Enabled && (q.Info.Group ?? q.Info.Type) == group)
				.OrderBy(q => q.Actor.ActorID)
				.Distinct()
				.ToArray();
		}

		ProductionQueue[] QueuesForInteraction(string group)
		{
			var selectedQueues = SelectedQueues(group);
			return selectedQueues.Length > 0 ? selectedQueues : AvailableQueues(group);
		}

		void SetupProductionGroupButton(ProductionTypeButtonWidget button)
		{
			if (button == null)
				return;

			void SelectGroup(bool reverse)
			{
				var queues = QueuesForInteraction(button.ProductionGroup);
				if (queues.Length == 0)
					return;

				var currentGroup = palette.CurrentQueue?.Info.Group ?? palette.CurrentQueue?.Info.Type;
				if (currentGroup == button.ProductionGroup)
				{
					var currentIndex = Array.IndexOf(queues, palette.CurrentQueue);
					if (currentIndex < 0)
						currentIndex = 0;

					var nextIndex = reverse
						? (currentIndex - 1 + queues.Length) % queues.Length
						: (currentIndex + 1) % queues.Length;
					palette.CurrentQueue = queues[nextIndex];
				}
				else
					palette.CurrentQueue = queues.FirstOrDefault(q => q.BuildableItems().Any()) ?? queues.First();

				palette.ScrollToTop();
				palette.PickUpCompletedBuilding();
			}

			button.IsDisabled = () => !AvailableQueues(button.ProductionGroup).Any();
			button.OnMouseUp = mi => SelectGroup(mi.Modifiers.HasModifier(Modifiers.Shift));
			button.OnKeyPress = e => SelectGroup(e.Modifiers.HasModifier(Modifiers.Shift));
			button.OnClick = () => SelectGroup(false);
			button.IsHighlighted = () => (palette.CurrentQueue?.Info.Group ?? palette.CurrentQueue?.Info.Type) == button.ProductionGroup;

			var chromeName = button.ProductionGroup.ToLowerInvariant();
			var icon = button.Get<ImageWidget>("ICON");
			icon.GetImageName = () => button.IsDisabled() ? chromeName + "-disabled" :
				AvailableQueues(button.ProductionGroup).Any(q => q.AllQueued().Any(i => i.Done)) ? chromeName + "-alert" : chromeName;
		}

		[ObjectCreator.UseCtor]
		public CNProductionSelectionLogic(Widget widget, World world)
		{
			this.world = world;
			palette = widget.Get<CNProductionPaletteWidget>("PRODUCTION_PALETTE");
			SetMaximumVisibleRows(palette);

			var background = widget.GetOrNull("PALETTE_BACKGROUND");
			if (background != null)
			{
				var backgroundTemplate = background.Get("ROW_TEMPLATE");
				var backgroundBottom = background.GetOrNull("BOTTOM_CAP");

				void UpdateBackground(int _, int icons)
				{
					var rows = Math.Max(palette.MinimumRows, (icons + palette.Columns - 1) / palette.Columns);
					rows = Math.Min(rows, palette.MaximumRows);

					background.RemoveChildren();

					var rowHeight = backgroundTemplate.Bounds.Height;
					for (var i = 0; i < rows; i++)
					{
						var row = backgroundTemplate.Clone();
						row.Bounds.Y = i * rowHeight;
						background.AddChild(row);
					}

					if (backgroundBottom == null)
						return;

					var cap = backgroundBottom.Clone();
					cap.Bounds.Y = rows * rowHeight;
					background.AddChild(cap);
				}

				palette.OnIconCountChanged += UpdateBackground;
				UpdateBackground(0, 0);
			}

			var typesContainer = widget.Get("PRODUCTION_TYPES");
			foreach (var i in typesContainer.Children)
				SetupProductionGroupButton(i as ProductionTypeButtonWidget);

			var ticker = widget.GetOrNull<LogicTickerWidget>("PRODUCTION_TICKER");
			if (ticker != null)
			{
				ticker.OnTick = () =>
				{
					if (palette.CurrentQueue != null && palette.DisplayedIconCount > 0)
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

		static void SetMaximumVisibleRows(CNProductionPaletteWidget productionPalette)
		{
			var screenHeight = Game.Renderer.Resolution.Height;
			var containerWidget = Ui.Root.GetOrNull<ContainerWidget>("SIDEBAR_PRODUCTION");
			if (containerWidget == null)
				return;

			var sidebarProductionHeight = containerWidget.Bounds.Y;
			var maxItemsHeight = screenHeight - sidebarProductionHeight;
			var maxIconRowOffset = maxItemsHeight / productionPalette.IconSize.Y - 1;
			productionPalette.MaxIconRowOffset = Math.Max(1, Math.Min(maxIconRowOffset, productionPalette.MaximumRows));
		}
	}
}
