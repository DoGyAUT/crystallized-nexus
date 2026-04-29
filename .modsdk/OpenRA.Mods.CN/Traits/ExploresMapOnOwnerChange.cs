#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Explores the whole map for the owning player when this actor enters the world or changes owner.")]
	public class ExploresMapOnOwnerChangeInfo : TraitInfo
	{
		[Desc("Prevent this player's explored shroud from being reset while this actor is owned.")]
		public readonly bool PreventShroudReset = true;

		public override object Create(ActorInitializer init) { return new ExploresMapOnOwnerChange(this); }
	}

	public class ExploresMapOnOwnerChange : INotifyAddedToWorld, INotifyOwnerChanged, IPreventsShroudReset
	{
		readonly ExploresMapOnOwnerChangeInfo info;

		public ExploresMapOnOwnerChange(ExploresMapOnOwnerChangeInfo info)
		{
			this.info = info;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			self.Owner.Shroud.ExploreAll();
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			newOwner.Shroud.ExploreAll();
		}

		bool IPreventsShroudReset.PreventShroudReset(Actor self)
		{
			return info.PreventShroudReset;
		}
	}
}
