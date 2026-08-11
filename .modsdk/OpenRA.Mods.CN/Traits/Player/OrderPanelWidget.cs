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
using OpenRA.Mods.CN.Orders;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Widgets
{
	/// <summary>
	/// SC2-style command-card widget. Reads <see cref="OrderIconInfo"/> traits from the
	/// currently active subgroup's actor type and renders them in a configurable grid.
	/// Dispatches clicks to the appropriate order generators or immediate orders.
	/// </summary>
	public class OrderPanelWidget : Widget
	{
		public int Columns = 10;
		public int Rows = 5;
		public int2 IconSize = new int2(40, 36);
		public int2 IconMargin = new int2(2, 2);
		public string TooltipContainer;
		public string TooltipTemplate = "SIMPLE_TOOLTIP";

		// Injected by BottomBarLogic after both widgets are created
		public Func<IEnumerable<Actor>> GetActiveActors = () => [];

		readonly World world;
		readonly WorldRenderer worldRenderer;
		readonly ModData modData;
		readonly Lazy<TooltipContainerWidget> tooltipContainer;

		int lastSelectionHash = -1;
		string activePanelName = "default";
		bool wasTargeting;
		readonly List<GridCell> cells = [];
		GridCell hoveredCell;
		bool tooltipVisible;

		sealed class GridCell
		{
			public OrderIconInfo Icon;
			public Sprite Sprite;
			public Rectangle Bounds;
			public HotkeyReference Hotkey;
			public bool Enabled = true;
		}

		[ObjectCreator.UseCtor]
		public OrderPanelWidget(World world, WorldRenderer worldRenderer, ModData modData)
		{
			this.world = world;
			this.worldRenderer = worldRenderer;
			this.modData = modData;
			tooltipContainer = Exts.Lazy(() => Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));
		}

		protected OrderPanelWidget(OrderPanelWidget other)
			: base(other)
		{
			world = other.world;
			worldRenderer = other.worldRenderer;
			modData = other.modData;
			tooltipContainer = other.tooltipContainer;
			Columns = other.Columns;
			Rows = other.Rows;
			IconSize = other.IconSize;
			IconMargin = other.IconMargin;
			TooltipContainer = other.TooltipContainer;
			TooltipTemplate = other.TooltipTemplate;
			GetActiveActors = other.GetActiveActors;
		}

		public override Widget Clone() => new OrderPanelWidget(this);

		public override void Tick()
		{
			var targeting = IsTargetingModeActive();
			if (targeting != wasTargeting)
			{
				wasTargeting = targeting;
				activePanelName = targeting ? "cancel" : "default";
				RebuildCells();
				lastSelectionHash = world.Selection.Hash;
				return;
			}

			var hash = world.Selection.Hash;
			if (hash == lastSelectionHash)
				return;

			lastSelectionHash = hash;
			RebuildCells();
		}

		void RebuildCells()
		{
			cells.Clear();

			var active = GetActiveActors().ToArray();
			if (active.Length == 0)
				return;

			// Collect available OrderIDs from non-disabled IIssueOrder traits on live actors
			var availableOrders = new HashSet<string>(StringComparer.Ordinal);
			foreach (var actor in active)
			{
				foreach (var issueOrder in actor.TraitsImplementing<IIssueOrder>())
					foreach (var targeter in issueOrder.Orders)
						availableOrders.Add(targeter.OrderID);
			}

			// Add built-in special commands based on actor capabilities
			var representative = active[0];
			if (representative.Info.HasTraitInfo<MobileInfo>())
				availableOrders.Add("Move");

			if (representative.Info.HasTraitInfo<AttackBaseInfo>())
			{
				availableOrders.Add("Attack");
				availableOrders.Add("AttackGround");
			}

			// Always-available built-ins
			availableOrders.Add("Stop");
			availableOrders.Add("Scatter");
			availableOrders.Add("Cancel");

			// Build-structure icons are available whenever the actor can build
			if (representative.Info.HasTraitInfo<BuilderInfo>())
				availableOrders.Add("BuildStructure");

			// Individual stance buttons if actors have AutoTarget with stances
			if (representative.Info.HasTraitInfo<AutoTargetInfo>())
			{
				availableOrders.Add("SetStance.AttackAnything");
				availableOrders.Add("SetStance.Defend");
				availableOrders.Add("SetStance.ReturnFire");
				availableOrders.Add("SetStance.HoldFire");
			}

			// Deploy if any actor can currently issue a deploy order
			if (active.Any(a => a.TraitsImplementing<IIssueDeployOrder>()
				.Any(d => d.CanIssueDeployOrder(a, false))))
				availableOrders.Add("Deploy");

			// Warn about available orders that have no icon definition
			var definedIconOrders = new HashSet<string>(
				representative.Info.TraitInfos<OrderIconInfo>().Select(i => i.Order),
				StringComparer.Ordinal);
			foreach (var orderId in availableOrders)
			{
				if (!definedIconOrders.Contains(orderId))
					Log.Write("debug", $"[OrderPanel] {representative.Info.Name}: order '{orderId}' available but has no OrderIconInfo.");
			}

			// Collect OrderIconInfo from the representative actor prototype
			// Use a dictionary keyed by panel position to resolve duplicates (last writer wins)
			var grid = new Dictionary<int2, GridCell>();
			var rb = RenderBounds;

			foreach (var iconInfo in representative.Info.TraitInfos<OrderIconInfo>())
			{
				if (iconInfo.Panel != activePanelName)
					continue;
				if (!availableOrders.Contains(iconInfo.Order))
					continue;

				// Load Chrome sprite; actor is used to determine faction for collection variant
				var sprite = LoadIconSprite(iconInfo, representative);
				var pos = iconInfo.PanelPosition;

				// Clamp to configured grid
				if (pos.X < 0 || pos.X >= Columns || pos.Y < 0 || pos.Y >= Rows)
					continue;

				var cellX = rb.X + pos.X * (IconSize.X + IconMargin.X);
				var cellY = rb.Y + pos.Y * (IconSize.Y + IconMargin.Y);

				HotkeyReference hotkey = null;
				if (!string.IsNullOrEmpty(iconInfo.HotkeyName))
					hotkey = modData.Hotkeys[iconInfo.HotkeyName];

				grid[pos] = new GridCell
				{
					Icon = iconInfo,
					Sprite = sprite,
					Bounds = new Rectangle(cellX, cellY, IconSize.X, IconSize.Y),
					Hotkey = hotkey,
					Enabled = IsOrderIconEnabled(iconInfo, active)
				};
			}

			cells.AddRange(grid.Values);
		}

		static bool IsOrderIconEnabled(OrderIconInfo icon, Actor[] active)
		{
			if (active.Length == 0)
				return false;

			switch (icon.Order)
			{
				case "Move":
					return active.Any(a => a.TraitsImplementing<Mobile>().Any(m => !m.IsTraitDisabled && !m.IsTraitPaused));
				case "Attack":
				case "AttackGround":
					return active.Any(a => a.TraitsImplementing<AttackBase>().Any(ab => !ab.IsTraitDisabled));
				case "BuildStructure":
					return active.Any(a => a.TraitsImplementing<Builder>().Any(b => !b.IsTraitDisabled));
				case "Deploy":
					return active.Any(a => a.TraitsImplementing<IIssueDeployOrder>().Any(d => d.CanIssueDeployOrder(a, false)));
				case "SetStance.AttackAnything":
				case "SetStance.Defend":
				case "SetStance.ReturnFire":
				case "SetStance.HoldFire":
					return active.Any(a => a.TraitsImplementing<AutoTarget>().Any(at => at.Info.EnableStances && !at.IsTraitDisabled));
				case "Stop":
				case "Scatter":
				case "Cancel":
					return true;
				default:
					return active.Any(a => a.TraitsImplementing<IIssueOrder>()
						.Any(issueOrder =>
						{
							if (issueOrder is IDisabledTrait disabledIssue && disabledIssue.IsTraitDisabled)
								return false;

								return issueOrder.Orders.Any(targeter => targeter.OrderID == icon.Order);
						}));
			}
		}

		public override void Draw()
		{
			foreach (var cell in cells)
			{
				if (cell.Sprite == null)
					continue;

				var highlighted = IsIconActive(cell.Icon.Order);

				var sprW = cell.Sprite.Size.X;
				var sprH = cell.Sprite.Size.Y;
				if (sprW <= 0 || sprH <= 0)
					continue;

				var scale = Math.Min(IconSize.X / sprW, IconSize.Y / sprH);
				var drawW = sprW * scale;
				var drawH = sprH * scale;
				var drawX = cell.Bounds.X + (IconSize.X - drawW) / 2f;
				var drawY = cell.Bounds.Y + (IconSize.Y - drawH) / 2f;

				WidgetUtils.DrawSprite(cell.Sprite, new float2(drawX, drawY), new float2(drawW, drawH));

				if (!cell.Enabled)
					WidgetUtils.FillRectWithColor(cell.Bounds, Color.FromArgb(140, 0, 0, 0));

				if (highlighted)
				{
					Game.Renderer.RgbaColorRenderer.DrawRect(
						new float2(cell.Bounds.Left, cell.Bounds.Top),
						new float2(cell.Bounds.Right, cell.Bounds.Bottom),
						2, Color.FromArgb(200, 255, 255, 0));
				}
			}
		}

		public override void MouseExited()
		{
			if (TooltipContainer == null || !tooltipVisible)
				return;

			tooltipContainer.Value.RemoveTooltip();
			tooltipVisible = false;
		}

		string GetHoveredCellLabel()
		{
			if (hoveredCell == null || string.IsNullOrEmpty(hoveredCell.Icon.TooltipLabel))
				return string.Empty;

			return FluentProvider.GetMessage(hoveredCell.Icon.TooltipLabel);
		}

		public override bool HandleKeyPress(KeyInput e)
		{
			if (e.Event != KeyInputEvent.Down)
				return false;

			foreach (var cell in cells)
			{
				if (cell.Hotkey == null || !cell.Hotkey.IsActivatedBy(e))
					continue;

				if (!cell.Enabled)
					return true;

				var queued = e.Modifiers.HasModifier(Modifiers.Shift);
				DispatchOrder(cell.Icon, queued);
				return true;
			}

			return false;
		}

		public override bool HandleMouseInput(MouseInput mi)
		{
			if (mi.Event == MouseInputEvent.Move)
			{
				hoveredCell = cells.FirstOrDefault(c => c.Bounds.Contains(mi.Location));

				if (TooltipContainer != null)
				{
					var hasContent = !string.IsNullOrEmpty(GetHoveredCellLabel());
					if (hasContent && !tooltipVisible)
					{
						tooltipContainer.Value.SetTooltip(TooltipTemplate,
							new WidgetArgs { { "getText", (Func<string>)GetHoveredCellLabel } });
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

			// Consume Left Up to prevent WorldInteractionControllerWidget from calling
			// World.CancelInputMode() (line 164), which would immediately cancel any
			// ConfirmOrder-mode generator set by the preceding Down event.
			if (mi.Event == MouseInputEvent.Up && mi.Button == MouseButton.Left)
				return true;

			if (mi.Event != MouseInputEvent.Down || mi.Button != MouseButton.Left)
				return false;

			var cell = cells.FirstOrDefault(c => c.Bounds.Contains(mi.Location));
			if (cell == null)
				return false;

			if (!cell.Enabled)
				return true;

			var queued = mi.Modifiers.HasModifier(Modifiers.Shift);
			DispatchOrder(cell.Icon, queued);
			return true;
		}

		void DispatchOrder(OrderIconInfo icon, bool queued)
		{
			var active = world.Selection.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner == world.LocalPlayer)
				.ToArray();
			if (active.Length == 0)
				return;

			switch (icon.Order)
			{
				case "Cancel":
					world.CancelInputMode();
					break;

				case "Stop":
					foreach (var a in active)
						world.IssueOrder(new Order("Stop", a, queued));
					break;

				case "Scatter":
					foreach (var a in active)
						world.IssueOrder(new Order("Scatter", a, queued));
					break;

				case "Move":
					if (world.OrderGenerator is UnitOrderGenerator
						and not AttackMoveOrderGenerator
						and not AttackCommandOrderGenerator
						and not ForceAttackGroundOrderGenerator)
						world.CancelInputMode();
					else
						world.OrderGenerator = new UnitOrderGenerator(world);
					break;

				case "Attack":
					if (world.OrderGenerator is AttackCommandOrderGenerator)
						world.CancelInputMode();
					else
						world.OrderGenerator = new AttackCommandOrderGenerator(world, active);
					break;

				case "AttackGround":
					if (world.OrderGenerator is ForceAttackGroundOrderGenerator)
						world.CancelInputMode();
					else
						world.OrderGenerator = new ForceAttackGroundOrderGenerator(world, active);
					break;

				case "Deploy":
					foreach (var a in active)
					{
						foreach (var deployer in a.TraitsImplementing<IIssueDeployOrder>())
						{
							if (!deployer.CanIssueDeployOrder(a, queued))
								continue;

							var deployOrder = deployer.IssueDeployOrder(a, queued);
							if (deployOrder != null)
								world.IssueOrder(deployOrder);
						}
					}

					break;

				case "SetStance.AttackAnything": SetStanceForActors(active, UnitStance.AttackAnything); break;
				case "SetStance.Defend": SetStanceForActors(active, UnitStance.Defend); break;
				case "SetStance.ReturnFire": SetStanceForActors(active, UnitStance.ReturnFire); break;
				case "SetStance.HoldFire": SetStanceForActors(active, UnitStance.HoldFire); break;

				case "BuildStructure":
					if (icon is BuildOrderIconInfo build)
						world.OrderGenerator = new BuildStructureOrderGenerator(world, active, build.ActorName, worldRenderer);
					break;

				default:
					world.OrderGenerator = new OrderIconOrderGenerator(world, active, icon.Order);
					break;
			}
		}

		void SetStanceForActors(Actor[] actors, UnitStance target)
		{
			foreach (var actor in actors)
			{
				foreach (var at in actor.TraitsImplementing<AutoTarget>()
					.Where(t => t.Info.EnableStances && !t.IsTraitDisabled))
				{
					at.PredictedStance = target;
					world.IssueOrder(new Order("SetUnitStance", actor, false) { ExtraData = (uint)target });
					break; // one AutoTarget per actor
				}
			}
		}

		UnitStance? GetUniformStance()
		{
			var stances = world.Selection.Actors
				.Where(a => a.Owner == world.LocalPlayer && !a.IsDead && a.IsInWorld)
				.SelectMany(a => a.TraitsImplementing<AutoTarget>()
					.Where(at => at.Info.EnableStances && !at.IsTraitDisabled))
				.Select(at => at.Stance)
				.Distinct()
				.ToList();
			return stances.Count == 1 ? stances[0] : null;
		}

		bool IsTargetingModeActive() =>
			world.OrderGenerator is AttackCommandOrderGenerator
				or ForceAttackGroundOrderGenerator
				or OrderIconOrderGenerator
				or BuildStructureOrderGenerator;

		bool IsIconActive(string orderID)
		{
			return orderID switch
			{
				"Attack" => false,
				"AttackGround" => false,
				"Move" => false,
				"SetStance.AttackAnything" => GetUniformStance() == UnitStance.AttackAnything,
				"SetStance.Defend" => GetUniformStance() == UnitStance.Defend,
				"SetStance.ReturnFire" => GetUniformStance() == UnitStance.ReturnFire,
				"SetStance.HoldFire" => GetUniformStance() == UnitStance.HoldFire,
				_ => world.OrderGenerator is OrderIconOrderGenerator oiog && oiog.IsForOrder(orderID)
			};
		}

		/// <summary>
		/// Loads the Chrome sprite for the icon. Tries faction-suffixed collection first
		/// (e.g. "command-icons-gdi"), then falls back to the base collection.
		/// </summary>
		static Sprite LoadIconSprite(OrderIconInfo icon, Actor actor)
		{
			if (string.IsNullOrEmpty(icon.ChromeCollection) || string.IsNullOrEmpty(icon.ChromeImage))
				return null;

			var factionCollection = icon.ChromeCollection + "-" + actor.Owner.Faction.InternalName;
			return ChromeProvider.TryGetImage(factionCollection, icon.ChromeImage)
				?? ChromeProvider.TryGetImage(icon.ChromeCollection, icon.ChromeImage);
		}
	}

	/// <summary>
	/// Minimal order generator for the "Attack Ground" button: always applies ForceAttack
	/// modifier, forcing units to target terrain even if no actor is there.
	/// </summary>
	public sealed class ForceAttackGroundOrderGenerator : UnitOrderGenerator
	{
		Actor[] subjects;

		protected override MouseActionType ActionType => MouseActionType.ConfirmOrder;

		public ForceAttackGroundOrderGenerator(World world, IEnumerable<Actor> subjects)
			: base(world) =>
			this.subjects = subjects.Where(a => !a.IsDead && a.IsInWorld).ToArray();

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var queued = mi.Modifiers.HasModifier(Modifiers.Shift);
			if (!queued)
				world.CancelInputMode();

			var target = TargetForInput(world, cell, worldPixel, mi);
			var modifiers = TargetModifiers.ForceAttack;
			if (queued)
				modifiers |= TargetModifiers.ForceQueue;

			foreach (var actor in subjects)
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;

				foreach (var issueOrder in actor.TraitsImplementing<IIssueOrder>())
				{
					foreach (var targeter in issueOrder.Orders.OrderByDescending(o => o.OrderPriority))
					{
						if (targeter.OrderID != "ForceAttack" && targeter.OrderID != "Attack")
							continue;

						var localMods = modifiers;
						string cursor = null;
						if (!targeter.CanTarget(actor, target, ref localMods, ref cursor))
							continue;

						var order = issueOrder.IssueOrder(actor, targeter, target, queued);
						if (order != null)
							yield return order;

						break;
					}
				}
			}
		}

		public override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
			=> "attack";

		public override void SelectionChanged(World world, IEnumerable<Actor> selected)
		{
			subjects = selected.Where(a => !a.IsDead && a.IsInWorld).ToArray();
			if (subjects.Length == 0)
				world.CancelInputMode();
		}

		public override bool InputOverridesSelection(World world, int2 xy, MouseInput mi) => true;
		public override bool ClearSelectionOnLeftClick => false;
	}
}
