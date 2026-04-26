#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Widgets.Logic
{
	/// <summary>
	/// <para>
	/// Army-selection widget logic inspired by C&amp;C3 / Generals / AoE2.
	/// Groups the current selection by actor type and allows cycling through
	/// subgroups via Tab or by clicking individual type buttons.
	/// </para>
	/// <para>
	/// When a subgroup is active, World.Selection is replaced with only those
	/// actors. The full original selection is stored internally and restored
	/// when the user presses Escape, clicks "All", or the selection changes
	/// externally (e.g. new box-select).
	/// </para>
	/// </summary>
	public class SelectionSubgroupLogic : ChromeLogic
	{
		readonly World world;

		// Widget references
		readonly ContainerWidget container;
		readonly ContainerWidget buttonContainer;

		// Hotkey for cycling
		readonly HotkeyReference cycleSubgroupKey;
		readonly HotkeyReference cycleSubgroupReverseKey;

		// State
		readonly List<SubgroupEntry> subgroups = [];
		List<Actor> fullSelection = [];
		int activeIndex = -1; // -1 = "All" (no subgroup filter active)
		bool suppressSelectionChanged;

		// Cached widget template
		readonly Widget buttonTemplate;

		// Layout config
		readonly int maxButtonsPerRow;

		// Track last known selection hash to detect external changes
		int lastSelectionHash;

		sealed class SubgroupEntry
		{
			public string ActorType;
			public string DisplayName;
			public List<Actor> Actors = [];
			public Actor Representative; // Used for icon/tooltip — master actor if slaves are merged
			public SubgroupButtonWidget Button;
		}

		[ObjectCreator.UseCtor]
		public SelectionSubgroupLogic(Widget widget, World world, WorldRenderer worldRenderer, ModData modData,
			Dictionary<string, MiniYaml> logicArgs)
		{
			this.world = world;
			container = widget.Get<ContainerWidget>("SUBGROUP_CONTAINER");
			buttonContainer = container.Get<ContainerWidget>("SUBGROUP_BUTTONS");

			maxButtonsPerRow = 8;
			if (logicArgs.TryGetValue("MaxButtonsPerRow", out var rowYaml) && !int.TryParse(rowYaml.Value, out maxButtonsPerRow))
				throw new YamlException($"Invalid value for MaxButtonsPerRow: {rowYaml.Value}");
			buttonTemplate = buttonContainer.Get("SUBGROUP_BUTTON_TEMPLATE");

			cycleSubgroupKey = modData.Hotkeys["CycleSelectionSubgroup"];
			cycleSubgroupReverseKey = modData.Hotkeys["CycleSelectionSubgroupReverse"];

			// The "All" button restores the full selection
			var allButton = container.GetOrNull<ButtonWidget>("SELECT_ALL_BUTTON");
			if (allButton != null)
			{
				allButton.OnClick = SelectAll;
				allButton.IsHighlighted = () => activeIndex == -1;
			}

			// Visibility: show whenever at least one unit type is selected
			container.IsVisible = () => subgroups.Count >= 1;

			// Hook into key listener for Tab cycling (inside SUBGROUP_CONTAINER, only active when visible)
			var keyHandler = container.GetOrNull<LogicKeyListenerWidget>("SUBGROUP_KEY_HANDLER");
			keyHandler?.AddHandler(HandleKeyPress);

			// Ticker lives on SELECTION_SUBGROUPS (always visible) so it ticks even when 0–1 types selected
			var ticker = widget.GetOrNull<LogicTickerWidget>("SUBGROUP_TICKER");
			if (ticker != null)
				ticker.OnTick = CheckSelectionChanged;

			// Initial build
			RebuildSubgroups();
		}

		void CheckSelectionChanged()
		{
			if (suppressSelectionChanged)
				return;

			var currentHash = ComputeSelectionHash();
			if (currentHash != lastSelectionHash)
			{
				lastSelectionHash = currentHash;
				RebuildSubgroups();
			}
		}

		int ComputeSelectionHash()
		{
			var hash = 17;
			foreach (var a in world.Selection.Actors)
				hash = hash * 31 + a.ActorID.GetHashCode();
			return hash;
		}

		void RebuildSubgroups()
		{
			// Store the full current selection
			fullSelection = world.Selection.Actors
				.Where(a => a.IsInWorld && !a.IsDead && a.Owner == world.LocalPlayer)
				.OrderBy(a => a.Info.Name)
				.ThenBy(a => a.ActorID)
				.ToList();

			subgroups.Clear();
			activeIndex = -1;

			// Group by actor type — slaves are merged into their master's group
			var groups = fullSelection
				.GroupBy(a => GetMasterInfo(a).Name)
				.OrderBy(g => GetSortPriority(g.First()))
				.ThenBy(g => g.Key);

			foreach (var group in groups)
			{
				// Prefer a master actor already in the selection for icon/tooltip; fall back to slave's Master reference
				var representative = group.FirstOrDefault(a => a.TraitOrDefault<BaseSpawnerSlave>() == null)
					?? group.First().Trait<BaseSpawnerSlave>().Master;

				var tooltip = representative.Info.TraitInfos<TooltipInfo>().FirstOrDefault();
				var displayName = tooltip?.Name ?? group.Key;

				subgroups.Add(new SubgroupEntry
				{
					ActorType = group.Key,
					DisplayName = displayName,
					Actors = group.ToList(),
					Representative = representative
				});
			}

			RebuildButtons();
			lastSelectionHash = ComputeSelectionHash();
		}

		/// <summary>
		/// Returns the ActorInfo to use for grouping — master's info if this is a slave actor.
		/// </summary>
		static ActorInfo GetMasterInfo(Actor a)
		{
			var slave = a.TraitOrDefault<BaseSpawnerSlave>();
			if (slave != null && slave.Master != null && !slave.Master.IsDead)
				return slave.Master.Info;
			return a.Info;
		}

		/// <summary>
		/// Sort priority: combat units first, then support, then economic.
		/// </summary>
		static int GetSortPriority(Actor a)
		{
			var selectable = a.Info.TraitInfoOrDefault<SelectableInfo>();
			if (selectable == null)
				return 50;

			return selectable.Priority switch
			{
				>= 10 => 0, // Combat
				>= 8 => 1,  // Support
				>= 6 => 2,  // Economic
				_ => 3
			};
		}

		void RebuildButtons()
		{
			buttonContainer.RemoveChildren();

			var buttonWidth = buttonTemplate.Bounds.Width;
			var buttonHeight = buttonTemplate.Bounds.Height;
			const int Spacing = 3;

			// Resize buttonContainer height to fit all rows
			var rowCount = (subgroups.Count + maxButtonsPerRow - 1) / maxButtonsPerRow;
			buttonContainer.Bounds = new WidgetBounds(
				buttonContainer.Bounds.X, buttonContainer.Bounds.Y,
				buttonContainer.Bounds.Width,
				rowCount * buttonHeight + (rowCount - 1) * Spacing);

			for (var i = 0; i < subgroups.Count; i++)
			{
				var entry = subgroups[i];
				var index = i;

				var col = i % maxButtonsPerRow;
				var row = i / maxButtonsPerRow;
				var xOffset = col * (buttonWidth + Spacing);
				var yOffset = row * (buttonHeight + Spacing);

				var button = (SubgroupButtonWidget)buttonTemplate.Clone();
				button.IsVisible = () => true;
				button.Bounds = new WidgetBounds(xOffset, yOffset, buttonWidth, buttonHeight);

				// Set up the actor icon sprite (use representative = master for slaves)
				SetupButtonIcon(button, entry.Representative);

				// Count label
				var countLabel = button.GetOrNull<LabelWidget>("COUNT");
				if (countLabel != null)
				{
					var captured = entry;
					countLabel.GetText = () => captured.Actors.Count(a => a.IsInWorld && !a.IsDead).ToString(CultureInfo.InvariantCulture);
				}

				// Click handler
				button.OnClick = () => SelectSubgroup(index);

				// Highlighted when this subgroup is active, OR when all are selected (all buttons lit)
				button.IsHighlighted = () => activeIndex == -1 || activeIndex == index;

				entry.Button = button;
				buttonContainer.AddChild(button);

				xOffset += buttonWidth + Spacing;
			}
		}

		void SetupButtonIcon(SubgroupButtonWidget button, Actor actor)
		{
			var iconWidget = button.GetOrNull<SubgroupIconWidget>("SPRITE_ICON");
			if (iconWidget == null)
				return;

			string image, iconSequence, palette;
			bool isPlayerPalette;

			// SubgroupIcon trait takes priority
			var si = actor.Info.TraitInfoOrDefault<SubgroupIconInfo>();
			if (si != null)
			{
				image = si.Image ?? actor.Info.Name;
				iconSequence = si.Sequence;
				palette = si.Palette;
				isPlayerPalette = si.IsPlayerPalette;
			}
			else
			{
				var bi = actor.Info.TraitInfos<BuildableInfo>().FirstOrDefault();
				image = actor.Info.Name;
				iconSequence = bi?.Icon ?? "icon";
				palette = bi?.IconPalette ?? "chrome";
				isPlayerPalette = bi?.IconPaletteIsPlayerPalette ?? false;
			}

			if (isPlayerPalette)
				palette += actor.Owner.InternalName;

			var sequences = world.Map.Sequences;
			if (!sequences.Images.Contains(image))
				return;

			if (sequences.HasSequence(image, iconSequence))
			{
				iconWidget.IconSprite = sequences.GetSequence(image, iconSequence).GetSprite(0);
				iconWidget.IconPalette = palette;
			}
		}

		void SelectSubgroup(int index)
		{
			if (index < 0 || index >= subgroups.Count)
			{
				SelectAll();
				return;
			}

			activeIndex = index;
			ApplySubgroupSelection();
		}

		void SelectAll()
		{
			activeIndex = -1;

			// Restore the full selection
			var alive = fullSelection.Where(a => a.IsInWorld && !a.IsDead).ToList();
			if (alive.Count == 0)
				return;

			suppressSelectionChanged = true;
			try
			{
				world.Selection.Combine(world, alive, false, false);
			}
			finally
			{
				suppressSelectionChanged = false;
				lastSelectionHash = ComputeSelectionHash();
			}
		}

		void ApplySubgroupSelection()
		{
			if (activeIndex < 0 || activeIndex >= subgroups.Count)
				return;

			var actors = subgroups[activeIndex].Actors
				.Where(a => a.IsInWorld && !a.IsDead)
				.ToList();

			if (actors.Count == 0)
				return;

			suppressSelectionChanged = true;
			try
			{
				world.Selection.Combine(world, actors, false, false);
			}
			finally
			{
				suppressSelectionChanged = false;
				lastSelectionHash = ComputeSelectionHash();
			}
		}

		bool HandleKeyPress(KeyInput e)
		{
			if (e.Event != KeyInputEvent.Down)
				return false;

			if (subgroups.Count < 2)
				return false;

			if (cycleSubgroupKey.IsActivatedBy(e))
			{
				CycleSubgroup(1);
				return true;
			}

			if (cycleSubgroupReverseKey.IsActivatedBy(e))
			{
				CycleSubgroup(-1);
				return true;
			}

			// Escape returns to "All"
			if (e.Key == Keycode.ESCAPE && activeIndex >= 0)
			{
				SelectAll();
				return true;
			}

			return false;
		}

		void CycleSubgroup(int direction)
		{
			if (subgroups.Count < 2)
				return;

			// If currently showing all, start at first (or last for reverse)
			if (activeIndex < 0)
				activeIndex = direction > 0 ? 0 : subgroups.Count - 1;
			else
			{
				activeIndex += direction;

				// Wrap around, with -1 = "All" as an extra stop
				if (activeIndex >= subgroups.Count)
					activeIndex = -1;
				else if (activeIndex < -1)
					activeIndex = subgroups.Count - 1;
			}

			if (activeIndex >= 0)
				ApplySubgroupSelection();
			else
				SelectAll();
		}
	}
}
