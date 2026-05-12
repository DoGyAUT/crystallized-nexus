#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Assigns deterministic faction-specific display names to bots.",
		"Attach this to the Player actor.")]
	public sealed class BotPlayerNamesInfo : TraitInfo
	{
		[Desc("Faction-specific bot name pools. Use `default` as a fallback key.")]
		public readonly FrozenDictionary<string, ImmutableArray<string>> Names =
			FrozenDictionary<string, ImmutableArray<string>>.Empty;

		[Desc("Optional short labels appended as '(label)' per bot type. Key is the bot Type string.")]
		public readonly FrozenDictionary<string, string> BotTypeLabels =
			FrozenDictionary<string, string>.Empty;

		public override object Create(ActorInitializer init) { return new BotPlayerNames(this); }
	}

	public sealed class BotPlayerNames : IResolvePlayerName
	{
		const string DefaultFaction = "default";

		readonly BotPlayerNamesInfo info;

		public BotPlayerNames(BotPlayerNamesInfo info)
		{
			this.info = info;
		}

		string IResolvePlayerName.ResolvePlayerName(Player player)
		{
			if (!player.IsBot)
				return null;

			if (!TryGetNames(player.Faction.InternalName, out var names) || names.IsDefaultOrEmpty)
				return null;

			var hash = StableHash(
				player.World.LobbyInfo.GlobalSettings.RandomSeed,
				player.Faction.InternalName,
				player.BotType ?? string.Empty);
			var seedOffset = ((hash % names.Length) + names.Length) % names.Length;

			var factionBots = player.World.Players
				.Where(p => p.IsBot && !p.NonCombatant && p.Playable && p.Faction.InternalName == player.Faction.InternalName)
				.OrderBy(p => p.ClientIndex)
				.ToArray();

			var botOffset = Array.IndexOf(factionBots, player);
			if (botOffset < 0)
				botOffset = 0;

			var baseName = names[(seedOffset + botOffset) % names.Length];
			var cycle = botOffset / names.Length;
			var fullName = cycle == 0 ? baseName : $"{baseName} {cycle + 1}";

			if (info.BotTypeLabels.TryGetValue(player.BotType ?? string.Empty, out var typeLabel))
				fullName = $"{fullName} ({typeLabel})";

			return fullName;
		}

		bool TryGetNames(string faction, out ImmutableArray<string> names)
		{
			if (info.Names.TryGetValue(faction, out names) && !names.IsDefaultOrEmpty)
				return true;

			if (info.Names.TryGetValue(DefaultFaction, out names) && !names.IsDefaultOrEmpty)
				return true;

			names = default;
			return false;
		}

		static int StableHash(int seed, string faction, string botType)
		{
			unchecked
			{
				var hash = seed;
				foreach (var c in faction) hash = (hash * 16777619) ^ c;
				foreach (var c in botType) hash = (hash * 16777619) ^ c;
				return hash;
			}
		}
	}
}
