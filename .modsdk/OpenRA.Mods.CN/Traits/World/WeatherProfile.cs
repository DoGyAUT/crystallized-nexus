#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	public enum WeatherKind
	{
		Clear,
		Rain,
		Snow,
		Overcast
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Static, match-long weather selected via a lobby dropdown. Grants a",
		"matching world condition (distinct from 'ionstorm') so gated",
		"WeatherOverlay / cloud traits switch on. Visual only; coexists with",
		"the ion-storm system.")]
	public class WeatherProfileInfo : TraitInfo, ILobbyOptions
	{
		[Desc("Descriptive label for the lobby option.")]
		public readonly string WeatherLabel = "Weather";

		[Desc("Tooltip description for the lobby option.")]
		public readonly string WeatherDescription = "Match-long weather: clear, rain, snow or overcast.";

		[Desc("Default lobby value: clear, rain, snow or overcast.")]
		public readonly string WeatherDefault = "clear";

		[Desc("Prevent the option from being changed in the lobby.")]
		public readonly bool WeatherLocked = false;

		[Desc("Display the option in the lobby.")]
		public readonly bool WeatherVisible = true;

		[Desc("Display order for the lobby options panel.")]
		public readonly int WeatherDisplayOrder = 4;

		[Desc("Category for the option in the lobby.")]
		public readonly string WeatherCategory = null;

		[Desc("Condition granted to the world actor when Weather is Rain.")]
		public readonly string RainCondition = "weather-rain";

		[Desc("Condition granted to the world actor when Weather is Snow.")]
		public readonly string SnowCondition = "weather-snow";

		[Desc("Condition granted to the world actor when Weather is Overcast.")]
		public readonly string OvercastCondition = "weather-overcast";

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			var values = new Dictionary<string, string>
			{
				{ "clear", "Clear" },
				{ "rain", "Rain" },
				{ "snow", "Snow" },
				{ "overcast", "Overcast" },
			};

			yield return new LobbyOption(map, "weather",
				WeatherLabel, WeatherDescription, WeatherVisible, WeatherDisplayOrder,
				values, WeatherDefault, WeatherLocked, WeatherCategory);
		}

		public override object Create(ActorInitializer init) { return new WeatherProfile(this); }
	}

	public class WeatherProfile : INotifyCreated, IWorldLoaded
	{
		readonly WeatherProfileInfo info;

		public WeatherKind Weather { get; private set; }

		public WeatherProfile(WeatherProfileInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			var value = self.World.LobbyInfo.GlobalSettings.OptionOrDefault("weather", info.WeatherDefault);
			Weather = value switch
			{
				"rain" => WeatherKind.Rain,
				"snow" => WeatherKind.Snow,
				"overcast" => WeatherKind.Overcast,
				_ => WeatherKind.Clear
			};
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			string condition = Weather switch
			{
				WeatherKind.Rain => info.RainCondition,
				WeatherKind.Snow => info.SnowCondition,
				WeatherKind.Overcast => info.OvercastCondition,
				_ => null
			};

			if (string.IsNullOrEmpty(condition))
				return;

			var worldActor = w.WorldActor;
			var external = worldActor.TraitsImplementing<ExternalCondition>()
				.FirstOrDefault(e => e.Info.Condition == condition);

			external?.GrantCondition(worldActor, this);
		}
	}
}
