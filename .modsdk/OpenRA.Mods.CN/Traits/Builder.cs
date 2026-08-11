#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Mods.CN.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("This unit can be ordered to build structures. It walks onto the chosen footprint, ",
		"is stored off-map in PlayerBuilders while the structure is built up from 1 HP, ",
		"and is returned to the map once the structure is finished or destroyed.")]
	public class BuilderInfo : ConditionalTraitInfo, Requires<MobileInfo>
	{
		[Desc("Color of the build order target line.")]
		public readonly Color TargetLineColor = Color.Yellow;

		[VoiceReference]
		[Desc("Voice to play when ordered to build.")]
		public readonly string Voice = "Action";

		public override object Create(ActorInitializer init) => new Builder(this);
	}

	public class Builder : ConditionalTrait<BuilderInfo>, IResolveOrder, IOrderVoice
	{
		public Builder(BuilderInfo info)
			: base(info) { }

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (IsTraitDisabled || order.OrderString != "BuildStructure")
				return;

			var buildingName = order.TargetString;
			if (string.IsNullOrEmpty(buildingName))
				return;

			if (!self.World.Map.Rules.Actors.TryGetValue(buildingName, out var buildingInfo) ||
				!buildingInfo.HasTraitInfo<BuildingInfo>())
				return;

			var cell = self.World.Map.CellContaining(order.Target.CenterPosition);
			self.QueueActivity(order.Queued, new BuildStructure(self, cell, buildingName, Info.TargetLineColor));
			self.ShowTargetLines();
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			if (IsTraitDisabled || order.OrderString != "BuildStructure" || !self.HasVoice(Info.Voice))
				return null;

			return Info.Voice;
		}
	}
}
