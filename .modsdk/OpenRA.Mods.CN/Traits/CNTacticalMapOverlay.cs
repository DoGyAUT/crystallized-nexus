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
	[Desc("Debug overlay that draws the chokepoints (bridges, ramps, passages) found by CNTacticalMapBotModule.",
		"Toggle in-game with the \"cntopo\" chat command. Attach to the world actor.")]
	public class CNTacticalMapOverlayInfo : TraitInfo
	{
		public readonly string Font = "TinyBold";
		public readonly Color BridgeColor = Color.Cyan;
		public readonly Color RampColor = Color.Yellow;
		public readonly Color PassageColor = Color.Orange;

		public override object Create(ActorInitializer init) { return new CNTacticalMapOverlay(this); }
	}

	public class CNTacticalMapOverlay : IRenderAnnotations, IWorldLoaded, IChatCommand
	{
		const string CommandName = "cntopo";

		readonly CNTacticalMapOverlayInfo info;
		readonly SpriteFont font;

		public bool Enabled { get; private set; }

		public CNTacticalMapOverlay(CNTacticalMapOverlayInfo info)
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
			help.RegisterHelp(CommandName, "toggles the CN tactical-map chokepoint overlay.");
		}

		void IChatCommand.InvokeCommand(string name, string arg)
		{
			if (name == CommandName)
				Enabled ^= true;
		}

		Color ColorFor(CNChokepointType type)
		{
			return type switch
			{
				CNChokepointType.Bridge => info.BridgeColor,
				CNChokepointType.Ramp => info.RampColor,
				_ => info.PassageColor,
			};
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (!Enabled)
				yield break;

			// Pure reads only — never trigger the topology build/recompute from the render thread (that caused
			// periodic render-prepare spikes). The sim thread keeps these up to date.
			var moduleList = self.World.Players
				.Select(p => p.PlayerActor.TraitsImplementing<CNTacticalMapBotModule>().FirstOrDefault(m => m.IsTraitEnabled()))
				.Where(m => m != null && m.TopologyReady)
				.Distinct()
				.ToList();

			// Cells the bots actually act on (reachable + lead somewhere). Everything else is detected-but-filtered.
			var usefulCells = new HashSet<CPos>();
			foreach (var module in moduleList)
				foreach (var cp in module.UsefulForOverlay())
					usefulCells.Add(cp.Cell);

			var visible = wr.Viewport.AllVisibleCells;
			var drawn = new HashSet<CPos>();
			foreach (var module in moduleList)
			{
				foreach (var cp in module.ChokepointsForOverlay())
				{
					if (!visible.Contains((PPos)cp.Cell.ToMPos(self.World.Map)) || !drawn.Add(cp.Cell))
						continue;

					var useful = usefulCells.Contains(cp.Cell);
					var pos = self.World.Map.CenterOfCell(cp.Cell);
					var color = useful ? ColorFor(cp.Type) : Color.DimGray;
					yield return new CircleAnnotationRenderable(pos, WDist.FromCells(1), useful ? 2 : 1, color);
					yield return new TextAnnotationRenderable(font, pos, 0, color,
						$"{cp.Type.ToString()[0]} {cp.BaseWeight} (d{cp.Domain}){(useful ? "" : " x")}");
				}
			}

			// The territory edge, split into what terrain already holds and what has to be held. Drawn per
			// cell rather than as one outline: the point of looking at this is to see where the line runs
			// and where it is open, and a cell is the unit both of those are decided in.
			// Kept paired with their player, unlike moduleList above: a territory only means anything
			// alongside whose it is, and several are drawn at once.
			var territories = self.World.Players
				.Select(p => (Player: p, Module: p.PlayerActor.TraitsImplementing<CNTacticalMapBotModule>().FirstOrDefault(m => m.IsTraitEnabled())))
				.Where(x => x.Module != null && x.Module.TopologyReady);

			foreach (var (owner, module) in territories)
			{
				// In the owning player's colour, because one shared colour would make two territories
				// meeting look like a single one. Wall is that colour faded: it is context, the doors
				// are the point.
				var doorColor = owner.Color;
				var wallColor = Color.FromArgb(110, owner.Color);

				foreach (var cell in module.TerritoryWallForOverlay())
				{
					if (!visible.Contains((PPos)cell.ToMPos(self.World.Map)))
						continue;

					yield return new CircleAnnotationRenderable(
						self.World.Map.CenterOfCell(cell), WDist.FromCells(1) / 3, 1, wallColor);
				}

				foreach (var door in module.DoorsForOverlay())
				{
					foreach (var cell in door.Cells)
					{
						if (!visible.Contains((PPos)cell.ToMPos(self.World.Map)))
							continue;

						yield return new CircleAnnotationRenderable(
							self.World.Map.CenterOfCell(cell), WDist.FromCells(1) / 2, 2, doorColor);
					}

					if (!visible.Contains((PPos)door.Center.ToMPos(self.World.Map)))
						continue;

					yield return new TextAnnotationRenderable(font, self.World.Map.CenterOfCell(door.Center), 0,
						doorColor, $"DOOR w{door.Width} beyond {door.GroundBeyond}");
				}
			}
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;
	}
}
