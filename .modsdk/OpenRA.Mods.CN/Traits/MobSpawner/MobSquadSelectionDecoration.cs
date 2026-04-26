#region Copyright & License Information
/*
 * Ported to Crystallized Nexus by CN contributors.
 * Follows GPLv3 License as the OpenRA engine.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Shows a selection box on this slave actor whenever its MobSpawnerMaster is selected. " +
		"Requires SelectionDecorations and Interactable on the same actor. " +
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

	public class MobSquadSelectionDecoration : IRenderAnnotations, INotifyCreated
	{
		readonly MobSquadSelectionDecorationInfo info;

		BaseSpawnerSlave spawnerSlave;
		Interactable interactable;

		public MobSquadSelectionDecoration(ActorInitializer init, MobSquadSelectionDecorationInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			spawnerSlave = self.TraitOrDefault<BaseSpawnerSlave>();
			interactable = self.Trait<Interactable>();
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
			if (!MasterIsSelected(self))
				yield break;

			if (self.World.FogObscures(self))
				yield break;

			var color = info.UsePlayerColor ? self.Owner.Color : info.SelectionBoxColor;
			var bounds = interactable.DecorationBounds(self, wr);
			yield return new SelectionBoxAnnotationRenderable(self, bounds, color);
		}

		bool IRenderAnnotations.SpatiallyPartitionable => true;
	}
}
