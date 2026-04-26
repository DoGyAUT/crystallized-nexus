#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Plays a speech notification to all surviving players when an enemy player is defeated.",
		"Attach this to the Player actor.")]
	public class PlayerDefeatedAnnouncerInfo : TraitInfo
	{
		[NotificationReference("Speech")]
		[Desc("Speech notification played for each surviving player when any enemy is defeated.")]
		public readonly string Notification = "PlayerWasDefeated";

		public override object Create(ActorInitializer init) { return new PlayerDefeatedAnnouncer(init.Self, this); }
	}

	public class PlayerDefeatedAnnouncer : INotifyWinStateChanged
	{
		readonly PlayerDefeatedAnnouncerInfo info;
		readonly Player owner;

		public PlayerDefeatedAnnouncer(Actor self, PlayerDefeatedAnnouncerInfo info)
		{
			this.info = info;
			owner = self.Owner;
		}

		void INotifyWinStateChanged.OnPlayerLost(Player player)
		{
			// Only notify the local, surviving, non-spectating player — not the one who just lost.
			if (owner != player && owner == player.World.LocalPlayer
				&& !owner.NonCombatant && !owner.PlayerReference.Spectating
				&& owner.WinState == WinState.Undefined)
			{
				Game.Sound.PlayNotification(player.World.Map.Rules, owner, "Speech",
					info.Notification, owner.Faction.InternalName);
			}
		}

		void INotifyWinStateChanged.OnPlayerWon(Player player) { }
	}
}
