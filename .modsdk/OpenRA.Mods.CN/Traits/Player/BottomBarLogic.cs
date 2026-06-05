#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.CN.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Widgets.Logic
{
	/// <summary>
	/// Lightweight logic class that connects <see cref="SelectionBarWidget"/> and
	/// <see cref="OrderPanelWidget"/> after both are created by the chrome loader.
	/// Injects <see cref="SelectionBarWidget.GetActiveActors"/> into the panel so that
	/// the command card always reflects the currently focused subgroup.
	/// </summary>
	public class BottomBarLogic : ChromeLogic
	{
		[ObjectCreator.UseCtor]
		public BottomBarLogic(Widget widget, Dictionary<string, MiniYaml> logicArgs)
		{
			var selBar = widget.GetOrNull<SelectionBarWidget>("SELECTION_BAR");
			var orderPanel = widget.GetOrNull<OrderPanelWidget>("ORDER_PANEL");

			if (selBar != null && orderPanel != null)
				orderPanel.GetActiveActors = selBar.GetActiveActors;
		}
	}
}
