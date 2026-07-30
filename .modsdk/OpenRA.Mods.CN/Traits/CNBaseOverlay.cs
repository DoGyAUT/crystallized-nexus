#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Commands;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Debug overlay that draws the bases CNBaseBuilderBotModule has clustered: center, build radius,",
		"assigned role and which buildings belong to which base.",
		"Toggle in-game with the \"cnbase\" chat command. Attach to the world actor.")]
	public class CNBaseOverlayInfo : TraitInfo
	{
		public readonly string Font = "TinyBold";

		[Desc("Colors cycled per base of one bot, so neighbouring bases stay distinguishable.")]
		public readonly Color[] BaseColors =
		[
			Color.Cyan,
			Color.LimeGreen,
			Color.Magenta,
			Color.Gold,
			Color.DeepSkyBlue,
			Color.OrangeRed,
		];

		public override object Create(ActorInitializer init) { return new CNBaseOverlay(this); }
	}

	public class CNBaseOverlay : IRenderAnnotations, IWorldLoaded, IChatCommand
	{
		const string CommandName = "cnbase";

		readonly CNBaseOverlayInfo info;
		readonly SpriteFont font;

		public bool Enabled { get; private set; }

		public CNBaseOverlay(CNBaseOverlayInfo info)
		{
			this.info = info;
			font = Game.Renderer.Fonts[info.Font];
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			var console = w.WorldActor.TraitOrDefault<ChatCommands>();
			var help = w.WorldActor.TraitOrDefault<HelpCommand>();
			if (console == null || help == null)
				return;

			console.RegisterCommand(CommandName, this);
			help.RegisterHelp(CommandName, "toggles the CN bot base/role overlay.");
		}

		void IChatCommand.InvokeCommand(string name, string arg)
		{
			if (name == CommandName)
				Enabled ^= true;
		}

		static char RoleTag(CNBaseRole role)
		{
			return role switch
			{
				CNBaseRole.Core => 'C',
				CNBaseRole.Economy => 'E',
				CNBaseRole.Military => 'M',
				_ => 'O',
			};
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (!Enabled)
				yield break;

			var map = self.World.Map;
			var visible = wr.Viewport.AllVisibleCells;

			// Pure reads only - BasesForOverlay hands back the list the sim thread last published and never
			// rebuilds. Recomputing here would both cost render time and desync the bot's own cache tick.
			foreach (var player in self.World.Players)
			{
				var module = player.PlayerActor.TraitsImplementing<CNBaseBuilderBotModule>()
					.FirstOrDefault(m => m.IsTraitEnabled());
				if (module == null)
					continue;

				var bases = module.BasesForOverlay();
				for (var i = 0; i < bases.Count; i++)
				{
					var botBase = bases[i];
					var color = info.BaseColors.Length > 0
						? info.BaseColors[i % info.BaseColors.Length]
						: Color.Cyan;

					var centerVisible = map.Contains(botBase.Center)
						&& visible.Contains((PPos)botBase.Center.ToMPos(map));

					// Membership: one thin spoke from each building back to the center it was assigned to.
					foreach (var building in botBase.Buildings)
					{
						if (building.IsDead || !building.IsInWorld)
							continue;

						if (!visible.Contains((PPos)building.Location.ToMPos(map)) && !centerVisible)
							continue;

						yield return new LineAnnotationRenderable(
							building.CenterPosition, map.CenterOfCell(botBase.Center), 1, color);
					}

					if (!centerVisible)
						continue;

					var center = map.CenterOfCell(botBase.Center);

					// Build radius: the annulus the placement search actually uses for this base.
					yield return new CircleAnnotationRenderable(
						center, WDist.FromCells(module.GetBaseRadiusForOverlay(botBase)), 1, color);

					// Cluster radius: how close another construction yard has to be to join this base.
					yield return new CircleAnnotationRenderable(
						center, WDist.FromCells(module.Info.BaseClusterRadius), 1, Color.DimGray);

					yield return new CircleAnnotationRenderable(center, WDist.FromCells(1), 2, color);
					yield return new TextAnnotationRenderable(font, center, 0, color,
						$"{player.PlayerName} [{RoleTag(botBase.Role)}] {botBase.Role} cy{botBase.ConstructionYards.Count} b{botBase.Buildings.Count}");

					// The raster anchor every BaseGrid placement in this base is aligned to.
					if (map.Contains(botBase.GridAnchor) && visible.Contains((PPos)botBase.GridAnchor.ToMPos(map)))
						yield return new CircleAnnotationRenderable(
							map.CenterOfCell(botBase.GridAnchor), WDist.FromCells(1) / 2, 1, Color.White);
				}
			}
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;
	}
}
