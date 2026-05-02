#region Copyright & License Information
/*
 * Ported to Crystallized Nexus by CN contributors.
 * Follows GPLv3 License as the OpenRA engine.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Shows a selection box on this slave actor whenever its MobSpawnerMaster is selected. " +
		"Also hosts regular actor decorations (rank, heal, etc.) while the vanilla SelectionDecorations are removed. " +
		"Add this trait to the slave actor, not the master.")]
	public class MobSquadSelectionDecorationInfo : TraitInfo, Requires<InteractableInfo>
	{
		[Desc("Color of the selection box. Defaults to the actor owner's player color if not set.")]
		public readonly Color SelectionBoxColor = Color.White;

		[Desc("Use the actor owner's player color instead of SelectionBoxColor.")]
		public readonly bool UsePlayerColor = true;

		public override object Create(ActorInitializer init)
		{
			return new MobSquadSelectionDecoration(init, this);
		}
	}

	public class MobSquadSelectionDecoration : ISelectionDecorations, IRenderAnnotations, INotifyCreated
	{
		readonly MobSquadSelectionDecorationInfo info;

		BaseSpawnerSlave spawnerSlave;
		Interactable interactable;
		IDecoration[] decorations;
		IDecoration[] selectedDecorations;

		public MobSquadSelectionDecoration(ActorInitializer init, MobSquadSelectionDecorationInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			spawnerSlave = self.TraitOrDefault<BaseSpawnerSlave>();
			interactable = self.Trait<Interactable>();
			selectedDecorations = self.TraitsImplementing<IDecoration>().ToArray();
			decorations = selectedDecorations.Where(d => !d.RequiresSelection).ToArray();
		}

		bool MasterIsSelected(Actor self)
		{
			if (spawnerSlave == null)
				return false;

			var master = spawnerSlave.Master;
			if (master == null || master.IsDead || !master.IsInWorld)
				return false;

			return self.World.Selection.Contains(master);
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (self.World.FogObscures(self))
				yield break;

			var masterSelected = MasterIsSelected(self);
			if (masterSelected)
			{
				var color = info.UsePlayerColor ? self.Owner.Color : info.SelectionBoxColor;
				foreach (var r in RenderSelectionBox(self, wr, color))
					yield return r;
			}

			if (wr.Viewport.Zoom < wr.Viewport.MinZoom)
				yield break;

			var renderDecorations = masterSelected ? selectedDecorations : decorations;
			foreach (var decoration in renderDecorations)
				foreach (var renderable in decoration.RenderDecoration(self, wr, this))
					yield return renderable;
		}

		IEnumerable<IRenderable> ISelectionDecorations.RenderSelectionAnnotations(Actor self, WorldRenderer worldRenderer, Color color)
		{
			return RenderSelectionBox(self, worldRenderer, color);
		}

		int2 ISelectionDecorations.GetDecorationOrigin(Actor self, WorldRenderer wr, string pos, int2 margin)
		{
			return wr.Viewport.WorldToViewPx(GetDecorationPosition(self, wr, pos)) + GetDecorationMargin(pos, margin);
		}

		IEnumerable<IRenderable> RenderSelectionBox(Actor self, WorldRenderer wr, Color color)
		{
			var bounds = interactable.DecorationBounds(self, wr);
			yield return new SelectionBoxAnnotationRenderable(self, bounds, color);
		}

		int2 GetDecorationPosition(Actor self, WorldRenderer wr, string pos)
		{
			var bounds = interactable.DecorationBounds(self, wr);
			switch (pos)
			{
				case "TopLeft": return bounds.TopLeft;
				case "TopRight": return bounds.TopRight;
				case "BottomLeft": return bounds.BottomLeft;
				case "BottomRight": return bounds.BottomRight;
				case "Top": return new int2(bounds.Left + bounds.Size.Width / 2, bounds.Top);
				default: return bounds.TopLeft + new int2(bounds.Size.Width / 2, bounds.Size.Height / 2);
			}
		}

		static int2 GetDecorationMargin(string pos, int2 margin)
		{
			switch (pos)
			{
				case "TopLeft": return margin;
				case "TopRight": return new int2(-margin.X, margin.Y);
				case "BottomLeft": return new int2(margin.X, -margin.Y);
				case "BottomRight": return -margin;
				case "Top": return new int2(0, margin.Y);
				default: return int2.Zero;
			}
		}

		bool IRenderAnnotations.SpatiallyPartitionable => true;
	}
}
