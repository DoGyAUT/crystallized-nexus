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
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	sealed class CNProductionQueueFromSelectionInfo : TraitInfo
	{
		public readonly string ProductionPaletteWidget = null;

		public override object Create(ActorInitializer init) { return new CNProductionQueueFromSelection(init.World, this); }
	}

	sealed class CNProductionQueueFromSelection : INotifySelection
	{
		readonly World world;
		readonly Lazy<CNProductionPaletteWidget> paletteWidget;

		public CNProductionQueueFromSelection(World world, CNProductionQueueFromSelectionInfo info)
		{
			this.world = world;
			paletteWidget = Exts.Lazy(() => Ui.Root.GetOrNull(info.ProductionPaletteWidget) as CNProductionPaletteWidget);
		}

		void INotifySelection.SelectionChanged()
		{
			if (world.LocalPlayer == null)
				return;

			var queue = world.Selection.Actors
				.Where(a => a.IsInWorld && a.World.LocalPlayer == a.Owner)
				.SelectMany(a => a.TraitsImplementing<ProductionQueue>())
				.FirstOrDefault(q => q.Enabled);

			if (queue == null)
			{
				var types = world.Selection.Actors.Where(a => a.IsInWorld && a.World.LocalPlayer == a.Owner)
					.SelectMany(a => a.TraitsImplementing<Production>().Where(p => !p.IsTraitDisabled))
					.SelectMany(t => t.Info.Produces);

				queue = world.LocalPlayer.PlayerActor.TraitsImplementing<ProductionQueue>()
					.FirstOrDefault(q => q.Enabled && types.Contains(q.Info.Type));
			}

			if (queue == null || !queue.AnyItemsToBuild())
				return;

			if (paletteWidget.Value != null)
				paletteWidget.Value.CurrentQueue = queue;
		}
	}
}
