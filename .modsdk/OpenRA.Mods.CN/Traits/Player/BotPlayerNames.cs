#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
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

			var bots = player.World.Players
				.Where(IsNamedBot)
				.OrderBy(p => p.ClientIndex)
				.ToArray();

			var playerIndex = Array.IndexOf(bots, player);
			if (playerIndex < 0)
				return null;

			var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i <= playerIndex; i++)
			{
				var name = PickUniqueName(bots[i], usedNames);
				if (name == null)
					continue;

				usedNames.Add(name);
				if (bots[i] == player)
					return name;
			}

			return null;
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

		string PickUniqueName(Player player, HashSet<string> usedNames)
		{
			if (!TryGetNames(player.Faction.InternalName, out var rawNames) || rawNames.IsDefaultOrEmpty)
				return null;

			var names = rawNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
			if (names.Length == 0)
				return null;

			var botType = player.BotType ?? string.Empty;
			var offset = PositiveModulo(
				StableHash(player.World.LobbyInfo.GlobalSettings.RandomSeed, player.Faction.InternalName, botType),
				names.Length);

			for (var attempt = 0; ; attempt++)
			{
				var baseName = names[(offset + attempt) % names.Length];
				var cycle = attempt / names.Length;
				var name = cycle == 0 ? baseName : $"{baseName} {cycle + 1}";
				var fullName = ApplyBotTypeLabel(name, botType);

				if (!usedNames.Contains(fullName))
					return fullName;
			}
		}

		string ApplyBotTypeLabel(string name, string botType)
		{
			return info.BotTypeLabels.TryGetValue(botType, out var typeLabel)
				? $"{name} ({typeLabel})"
				: name;
		}

		static bool IsNamedBot(Player player)
		{
			return player.IsBot && !player.NonCombatant && player.Playable;
		}

		static int PositiveModulo(int value, int divisor)
		{
			return ((value % divisor) + divisor) % divisor;
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
