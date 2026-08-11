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
	/// An <see cref="OrderIconInfo"/> that orders the selected unit(s) to build a structure.
	/// Clicking it opens a <see cref="Orders.BuildStructureOrderGenerator"/> for <see cref="ActorName"/>,
	/// providing the same footprint targeting as normal building placement.
	/// The icon's <c>Order</c> should be <c>"BuildStructure"</c>.
	/// </summary>
	public class BuildOrderIconInfo : OrderIconInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor type (structure) this icon builds.")]
		public readonly string ActorName;

		public override object Create(ActorInitializer init) => new BuildOrderIcon(this);
	}

	public class BuildOrderIcon : OrderIcon
	{
		public new BuildOrderIconInfo Info => (BuildOrderIconInfo)base.Info;

		public BuildOrderIcon(BuildOrderIconInfo info)
			: base(info) { }
	}
}
