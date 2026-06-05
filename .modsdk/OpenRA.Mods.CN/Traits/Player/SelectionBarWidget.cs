#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Widgets
{
	/// <summary>
	/// Horizontal strip showing a portrait icon and HP bar for each selected unit.
	/// Also handles subgroup (unit-type) tab cycling — replaces SELECTION_SUBGROUPS.
	/// Exposes <see cref="GetActiveActors"/> so <see cref="OrderPanelWidget"/> knows
	/// which actor type currently drives the command card.
	/// </summary>
	public class SelectionBarWidget : Widget
	{
		public int SlotWidth = 56;
		public int SlotHeight = 52;
		public int SlotMargin = 2;
		public int HPBarHeight = 4;
		public int HPBarMargin = 2;
		public int MaxSlotsPerRow = 10;
		public int MaxRows = 5;
		public int RowMargin = 2;
		public int PageButtonWidth = 22;
		public int PageButtonGap = 3;

		public string CycleSubgroupKey = "CycleSelectionSubgroup";
		public string CycleSubgroupReverseKey = "CycleSelectionSubgroupReverse";
		public string TooltipContainer;
		public string TooltipTemplate = "SIMPLE_TOOLTIP";

		// HP-bar colours — match SelectionBarsAnnotationRenderable thresholds
		static readonly Color HPColorFull = Color.FromArgb(0, 230, 0);
		static readonly Color HPColorMedium = Color.FromArgb(255, 200, 0);
		static readonly Color HPColorLow = Color.FromArgb(230, 0, 0);
		static readonly Color HPColorBackground = Color.FromArgb(60, 60, 60);

		readonly World world;
		readonly WorldRenderer worldRenderer;
		readonly HotkeyReference cycleKey;
		readonly HotkeyReference cycleReverseKey;

		// Subgroup state
		readonly List<SubgroupEntry> subgroups = [];
		List<Actor> fullSelection = [];
		int activeTypeIndex = -1; // -1 = "All"

		// Per-frame slot cache
		readonly List<SlotData> slots = [];
		int lastSelectionHash;
		int activePage;
		int hoveredSlotIndex = -1;
		bool tooltipVisible;

		readonly Lazy<TooltipContainerWidget> tooltipContainer;

		sealed class SubgroupEntry
		{
			public string ActorType;
			public List<Actor> Actors = [];
			public Actor Representative;
		}

		readonly struct SlotData
		{
			public readonly Actor Actor;
			public readonly Sprite IconSprite;
			public readonly PaletteReference IconPalette;
			public readonly float HPRatio;
			public readonly bool IsActive;

			public SlotData(Actor actor, Sprite iconSprite, PaletteReference iconPalette, float hpRatio, bool isActive)
			{
				Actor = actor;
				IconSprite = iconSprite;
				IconPalette = iconPalette;
				HPRatio = hpRatio;
				IsActive = isActive;
			}
		}

		[ObjectCreator.UseCtor]
		public SelectionBarWidget(World world, WorldRenderer worldRenderer, ModData modData)
		{
			this.world = world;
			this.worldRenderer = worldRenderer;

			cycleKey = modData.Hotkeys[CycleSubgroupKey];
			cycleReverseKey = modData.Hotkeys[CycleSubgroupReverseKey];

			tooltipContainer = Exts.Lazy(() =>
				Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));
		}

		protected SelectionBarWidget(SelectionBarWidget other)
			: base(other)
		{
			world = other.world;
			worldRenderer = other.worldRenderer;
			cycleKey = other.cycleKey;
			cycleReverseKey = other.cycleReverseKey;
		}

		public override Widget Clone() => new SelectionBarWidget(this);

		public override void Tick()
		{
			var hash = ComputeSelectionHash();
			if (hash == lastSelectionHash)
				return;

			lastSelectionHash = hash;
			RebuildSubgroups();
		}

		int ComputeSelectionHash()
		{
			var h = 17;
			foreach (var a in world.Selection.Actors)
				h = h * 31 + a.ActorID.GetHashCode();
			return h;
		}

		void RebuildSubgroups()
		{
			fullSelection = world.Selection.Actors
				.Where(a => a.IsInWorld && !a.IsDead && a.Owner == world.LocalPlayer
					&& a.TraitOrDefault<BaseSpawnerSlave>() == null)
				.OrderBy(a => a.Info.HasTraitInfo<BuildingInfo>() ? 1 : 0) // units before buildings
				.ThenBy(a => a.Info.SelectionPriority(Modifiers.None))
				.ThenBy(a => a.Info.Name)
				.ThenBy(a => a.ActorID)
				.ToList();

			subgroups.Clear();

			var groups = fullSelection
				.GroupBy(a => GetMasterInfo(a).Name)
				.OrderBy(g => g.First().SelectionPriority(Modifiers.None))
				.ThenBy(g => g.Key);

			foreach (var group in groups)
			{
				var rep = group.FirstOrDefault(a => a.TraitOrDefault<BaseSpawnerSlave>() == null)
					?? group.First().Trait<BaseSpawnerSlave>().Master;

				subgroups.Add(new SubgroupEntry
				{
					ActorType = group.Key,
					Actors = group.ToList(),
					Representative = rep
				});
			}

			// Auto-select first subgroup so the order panel is immediately driven
			activeTypeIndex = subgroups.Count > 1 ? 0 : -1;

			RebuildSlots();
		}

		void RebuildSlots()
		{
			slots.Clear();

			// Collect active subgroup actor set for IsActive determination
			HashSet<Actor> activeSet = null;
			if (activeTypeIndex >= 0 && activeTypeIndex < subgroups.Count)
				activeSet = [.. subgroups[activeTypeIndex].Actors];

			foreach (var actor in fullSelection.Where(a => a.IsInWorld && !a.IsDead))
			{
				GetPortraitSprite(actor, out var sprite, out var paletteName);

				PaletteReference palette = null;
				if (sprite != null)
					palette = worldRenderer.Palette(paletteName);

				var health = actor.TraitOrDefault<IHealth>();
				var hpRatio = health != null && health.MaxHP > 0
					? (float)health.HP / health.MaxHP
					: 1f;

				var isActive = activeSet == null || activeSet.Contains(actor);
				slots.Add(new SlotData(actor, sprite, palette, hpRatio, isActive));
			}

			var slotsPerPage = MaxSlotsPerRow * MaxRows;
			var maxPage = slots.Count == 0 ? 0 : (slots.Count - 1) / slotsPerPage;
			activePage = activePage.Clamp(0, maxPage);
		}

		/// <summary>Returns the actors currently displayed in the command card (the active subgroup).</summary>
		public IEnumerable<Actor> GetActiveActors()
		{
			if (activeTypeIndex < 0 || activeTypeIndex >= subgroups.Count)
				return fullSelection.Where(a => a.IsInWorld && !a.IsDead);

			return subgroups[activeTypeIndex].Actors.Where(a => a.IsInWorld && !a.IsDead);
		}

		public override void Draw()
		{
			if (slots.Count == 0)
				return;

			var rb = RenderBounds;
			var rowStride = SlotHeight + RowMargin;
			var iconOffsetX = PageButtonWidth + PageButtonGap;
			var total = MaxSlotsPerRow * MaxRows;

			// Page buttons (1 per row, aligned with grid rows, visible when that page has units)
			var font = Game.Renderer.Fonts["TinyBold"];
			var slotsPerPage = MaxSlotsPerRow * MaxRows;
			for (var page = 0; page < MaxRows; page++)
			{
				if (slots.Count <= page * slotsPerPage)
					break;

				var btnRect = new Rectangle(rb.X, rb.Y + page * rowStride, PageButtonWidth, SlotHeight);
				var isActivePage = page == activePage;
				WidgetUtils.FillRectWithColor(btnRect,
					isActivePage ? Color.FromArgb(200, 80, 130, 80) : Color.FromArgb(140, 50, 50, 50));
				var label = (page + 1).ToString();
				var labelSize = font.Measure(label);
				font.DrawTextWithContrast(
					label,
					new float2(
						btnRect.X + (PageButtonWidth - labelSize.X) / 2f,
						btnRect.Y + (SlotHeight - labelSize.Y) / 2f),
					Color.White, Color.FromArgb(180, 0, 0, 0), 1);
			}

			// Unit portrait slots for active page
			var pageStart = activePage * slotsPerPage;
			for (var i = 0; i < total; i++)
			{
				var slotIndex = pageStart + i;
				if (slotIndex >= slots.Count)
					break;

				var slot = slots[slotIndex];
				var row = i / MaxSlotsPerRow;
				var col = i % MaxSlotsPerRow;
				var slotX = rb.X + iconOffsetX + col * (SlotWidth + SlotMargin);
				var slotY = rb.Y + row * rowStride;

				// Portrait sprite
				if (slot.IconSprite != null && slot.IconPalette != null)
				{
					var iconAreaH = SlotHeight - HPBarHeight - HPBarMargin;
					var sprW = slot.IconSprite.Size.X;
					var sprH = slot.IconSprite.Size.Y;
					var scale = sprW > 0 && sprH > 0
						? Math.Min(SlotWidth / sprW, iconAreaH / sprH)
						: 1f;
					var drawW = sprW * scale;
					var drawH = sprH * scale;
					var drawX = slotX + (SlotWidth - drawW) / 2f;
					var drawY = slotY + (iconAreaH - drawH) / 2f;

					Game.Renderer.SpriteRenderer.DrawSprite(
						slot.IconSprite, slot.IconPalette,
						new float3(drawX, drawY, 0f), scale);
				}

				// HP bar background
				var barY = slotY + SlotHeight - HPBarHeight;
				WidgetUtils.FillRectWithColor(
					new Rectangle(slotX, barY, SlotWidth, HPBarHeight),
					HPColorBackground);

				// HP bar fill
				var barFillWidth = (int)(SlotWidth * slot.HPRatio);
				if (barFillWidth > 0)
				{
					var hpColor = slot.HPRatio > 0.5f ? HPColorFull
						: slot.HPRatio > 0.25f ? HPColorMedium
						: HPColorLow;

					WidgetUtils.FillRectWithColor(
						new Rectangle(slotX, barY, barFillWidth, HPBarHeight),
						hpColor);
				}

				// Dim inactive subgroup slots
				if (!slot.IsActive)
					WidgetUtils.FillRectWithColor(
						new Rectangle(slotX, slotY, SlotWidth, SlotHeight),
						Color.FromArgb(160, 0, 0, 0));
			}
		}

		public override void MouseExited()
		{
			if (TooltipContainer == null || !tooltipVisible)
				return;

			tooltipContainer.Value.RemoveTooltip();
			tooltipVisible = false;
		}

		string GetHoveredActorName()
		{
			if (hoveredSlotIndex < 0 || hoveredSlotIndex >= slots.Count)
				return string.Empty;

			var actor = slots[hoveredSlotIndex].Actor;
			if (actor.IsDead || !actor.IsInWorld)
				return string.Empty;

			var tooltip = actor.TraitsImplementing<Tooltip>().FirstOrDefault(x => !x.IsTraitDisabled);
			return tooltip != null ? FluentProvider.GetMessage(tooltip.Info.Name) : actor.Info.Name;
		}

		int SlotIndexAt(int2 location)
		{
			var localX = location.X - RenderBounds.X - PageButtonWidth - PageButtonGap;
			var localY = location.Y - RenderBounds.Y;
			if (localX < 0)
				return -1;

			var col = localX / (SlotWidth + SlotMargin);
			var row = localY / (SlotHeight + RowMargin);
			if (col < 0 || col >= MaxSlotsPerRow || row < 0 || row >= MaxRows)
				return -1;

			return activePage * MaxSlotsPerRow * MaxRows + row * MaxSlotsPerRow + col;
		}

		public override bool HandleMouseInput(MouseInput mi)
		{
			if (!RenderBounds.Contains(mi.Location))
				return false;

			var localX = mi.Location.X - RenderBounds.X;
			var localY = mi.Location.Y - RenderBounds.Y;

			// Page button strip click
			if (localX < PageButtonWidth && mi.Event == MouseInputEvent.Down && mi.Button == MouseButton.Left)
			{
				var page = localY / (SlotHeight + RowMargin);
				if (page >= 0 && page < MaxRows && slots.Count > page * MaxSlotsPerRow * MaxRows)
					activePage = page;
				return true;
			}

			if (mi.Event == MouseInputEvent.Move)
			{
				hoveredSlotIndex = SlotIndexAt(mi.Location);

				if (TooltipContainer != null)
				{
					var hasContent = !string.IsNullOrEmpty(GetHoveredActorName());
					if (hasContent && !tooltipVisible)
					{
						tooltipContainer.Value.SetTooltip(TooltipTemplate,
							new WidgetArgs { { "getText", (Func<string>)GetHoveredActorName } });
						tooltipVisible = true;
					}
					else if (!hasContent && tooltipVisible)
					{
						tooltipContainer.Value.RemoveTooltip();
						tooltipVisible = false;
					}
				}

				return false;
			}

			if (mi.Event != MouseInputEvent.Down || mi.Button != MouseButton.Left)
				return false;

			var idx = SlotIndexAt(mi.Location);
			if (idx < 0 || idx >= slots.Count)
				return false;

			var clickedActor = slots[idx].Actor;
			if (clickedActor.IsDead || !clickedActor.IsInWorld)
				return false;

			var shift = mi.Modifiers.HasModifier(Modifiers.Shift);
			var ctrl = mi.Modifiers.HasModifier(Modifiers.Ctrl);

			if (ctrl && shift)
			{
				// Remove entire type from selection
				var typeActors = FindSubgroupForActor(clickedActor);
				var remaining = fullSelection.Where(a => !typeActors.Contains(a) && a.IsInWorld && !a.IsDead).ToArray();
				world.Selection.Combine(world, remaining, false, false);
			}
			else if (ctrl)
			{
				// Keep only this type
				var typeActors = FindSubgroupForActor(clickedActor).Where(a => a.IsInWorld && !a.IsDead).ToArray();
				world.Selection.Combine(world, typeActors, false, false);
			}
			else if (shift)
			{
				// Remove this actor
				var remaining = fullSelection.Where(a => a != clickedActor && a.IsInWorld && !a.IsDead).ToArray();
				world.Selection.Combine(world, remaining, false, false);
			}
			else
			{
				world.Selection.Combine(world, [clickedActor], false, false);
			}

			return true;
		}

		public override bool HandleKeyPress(KeyInput e)
		{
			if (e.Event != KeyInputEvent.Down)
				return false;

			if (subgroups.Count < 2)
				return false;

			if (cycleKey.IsActivatedBy(e))
			{
				CycleSubgroup(1);
				return true;
			}

			if (cycleReverseKey.IsActivatedBy(e))
			{
				CycleSubgroup(-1);
				return true;
			}

			return false;
		}

		void CycleSubgroup(int direction)
		{
			if (subgroups.Count < 2)
				return;

			// Wrap strictly within subgroup range — never land on -1 (All)
			activeTypeIndex = activeTypeIndex < 0
				? (direction > 0 ? 0 : subgroups.Count - 1)
				: (activeTypeIndex + direction + subgroups.Count) % subgroups.Count;

			RebuildSlots();
		}

		List<Actor> FindSubgroupForActor(Actor actor)
		{
			var group = subgroups.FirstOrDefault(s => s.Actors.Contains(actor));
			return group?.Actors ?? [actor];
		}

		void GetPortraitSprite(Actor actor, out Sprite sprite, out string paletteName)
		{
			sprite = null;
			paletteName = "chrome";

			string image, iconSequence, palette;
			bool isPlayerPalette;

			var rs = actor.Info.TraitInfoOrDefault<RenderSpritesInfo>();
			var factionImage = rs?.GetImage(actor.Info, actor.Owner.Faction.InternalName) ?? actor.Info.Name;

			var si = actor.Info.TraitInfoOrDefault<SubgroupIconInfo>();
			if (si != null)
			{
				image = si.Image ?? factionImage;
				iconSequence = si.Sequence;
				palette = si.Palette;
				isPlayerPalette = si.IsPlayerPalette;
			}
			else
			{
				var bi = actor.Info.TraitInfos<BuildableInfo>().FirstOrDefault();
				image = factionImage;
				iconSequence = bi?.Icon ?? "icon";
				palette = bi?.IconPalette ?? "chrome";
				isPlayerPalette = bi?.IconPaletteIsPlayerPalette ?? false;
			}

			if (isPlayerPalette)
				palette += actor.Owner.InternalName;

			paletteName = palette;

			var sequences = world.Map.Sequences;
			if (!sequences.Images.Contains(image))
				return;

			if (sequences.HasSequence(image, iconSequence))
				sprite = sequences.GetSequence(image, iconSequence).GetSprite(0);
		}

		static ActorInfo GetMasterInfo(Actor a)
		{
			var slave = a.TraitOrDefault<BaseSpawnerSlave>();
			if (slave != null && slave.Master != null && !slave.Master.IsDead)
				return slave.Master.Info;
			return a.Info;
		}
	}
}
