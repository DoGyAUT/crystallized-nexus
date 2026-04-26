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
	[TraitLocation(SystemActors.World | SystemActors.Player)]
	[Desc("Plays a faction-specific speech notification and/or displays a text notification",
		"when a condition is granted or revoked. Use RequiresCondition to bind to the condition.")]
	public class AnnounceOnConditionInfo : ConditionalTraitInfo
	{
		[Desc("Speech notification to play for each player (using their faction voice) when the condition is granted.")]
		public readonly string EnabledNotification = null;

		[Desc("Text notification to display when the condition is granted.")]
		public readonly string EnabledTextNotification = null;

		[Desc("Speech notification to play for each player (using their faction voice) when the condition is revoked.")]
		public readonly string DisabledNotification = null;

		[Desc("Text notification to display when the condition is revoked.")]
		public readonly string DisabledTextNotification = null;

		public override object Create(ActorInitializer init) { return new AnnounceOnCondition(this); }
	}

	public class AnnounceOnCondition : ConditionalTrait<AnnounceOnConditionInfo>
	{
		public AnnounceOnCondition(AnnounceOnConditionInfo info)
			: base(info) { }

		protected override void TraitEnabled(Actor self)
		{
			Announce(self, Info.EnabledNotification, Info.EnabledTextNotification);
		}

		protected override void TraitDisabled(Actor self)
		{
			Announce(self, Info.DisabledNotification, Info.DisabledTextNotification);
		}

		static void Announce(Actor self, string notification, string textNotification)
		{
			if (!string.IsNullOrEmpty(notification))
				foreach (var player in self.World.Players)
					Game.Sound.PlayNotification(self.World.Map.Rules, player, "Speech",
						notification, player.Faction.InternalName);

			if (!string.IsNullOrEmpty(textNotification))
				TextNotificationsManager.AddSystemLine(textNotification);
		}
	}
}
