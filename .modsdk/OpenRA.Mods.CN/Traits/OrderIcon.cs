#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	/// <summary>
	/// Declares one icon slot in the SC2-style command card for this actor.
	/// Multiple instances can be attached via <c>OrderIcon@Tag</c>.
	/// The OrderPanelWidget reads these at selection time and renders the grid.
	/// </summary>
	public class OrderIconInfo : TraitInfo
	{
		/// <summary>
		/// The order string this icon triggers. May be a standard IOrderTargeter.OrderID
		/// (e.g. "Deploy", "Unload", "Repair") or one of the built-in special commands:
		/// "Move", "Attack", "AttackGround", "Stop", "Scatter", "CycleStance".
		/// </summary>
		[FieldLoader.Require]
		public readonly string Order;

		/// <summary>
		/// Chrome image collection that contains the icon (e.g. "command-icons").
		/// The widget will try a faction-suffixed variant (e.g. "command-icons-gdi")
		/// first and fall back to this base name.
		/// </summary>
		public readonly string ChromeCollection = "command-icons";

		/// <summary>Region name within <see cref="ChromeCollection"/> to use as the icon.</summary>
		[FieldLoader.Require]
		public readonly string ChromeImage;

		/// <summary>Column and row (zero-based) in the command-card grid where this icon is placed.</summary>
		public readonly int2 PanelPosition;

		/// <summary>Named panel this icon belongs to. OrderPanelWidget shows only icons from the active panel.
		/// Use "default" for the normal command card, "cancel" for the targeting-mode subpanel, or any
		/// custom name for future submenus.</summary>
		public readonly string Panel = "default";

		/// <summary>Fluent key for the tooltip title shown on hover.</summary>
		[FluentReference(optional: true)]
		public readonly string TooltipLabel;

		/// <summary>Fluent key for the longer tooltip description shown on hover.</summary>
		[FluentReference(optional: true)]
		public readonly string TooltipDescription;

		/// <summary>Hotkey name registered in hotkeys.yaml that triggers this icon (optional).</summary>
		public readonly string HotkeyName;

		public override object Create(ActorInitializer init) => new OrderIcon(this);
	}

	public class OrderIcon
	{
		public readonly OrderIconInfo Info;

		public OrderIcon(OrderIconInfo info) { Info = info; }
	}
}
