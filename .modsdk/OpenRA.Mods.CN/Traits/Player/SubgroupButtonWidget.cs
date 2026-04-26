#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Mods.Common.Widgets;

namespace OpenRA.Mods.CN.Widgets
{
	public class SubgroupButtonWidget : ButtonWidget
	{
		[ObjectCreator.UseCtor]
		public SubgroupButtonWidget(ModData modData)
			: base(modData) { }

		protected SubgroupButtonWidget(SubgroupButtonWidget other)
			: base(other) { }

		public override SubgroupButtonWidget Clone() => new(this);
	}
}
