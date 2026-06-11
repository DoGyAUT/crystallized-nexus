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
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;
	}
}
