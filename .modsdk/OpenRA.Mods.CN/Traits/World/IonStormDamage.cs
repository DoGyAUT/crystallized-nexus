#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Fires a weapon at random map positions or actors during ion storms.",
		"All effects (damage, visuals, smudges, sound) are defined by the weapon/warheads.",
		"Requires WeatherController on the world actor.")]
	public class IonStormDamageInfo : TraitInfo
	{
		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Weapon to fire at each strike location.")]
		public readonly string Weapon = null;

		[Desc("Average ticks between strikes during full storm.")]
		public readonly int StrikeInterval = 80;

		[Desc("Maximum number of strikes per interval.")]
		public readonly int MaxStrikes = 1;

		[Desc("Only strike when storm intensity is above this threshold (0.0 - 1.0).")]
		public readonly float IntensityThreshold = 0.8f;

		[Desc("Percentage chance that a strike targets an actor instead of random ground.",
			"0 = always random ground, 100 = always target actors.")]
		public readonly int ActorStrikeChance = 50;

		public override object Create(ActorInitializer init) { return new IonStormDamage(init, this); }
	}

	public class IonStormDamage : ITick
	{
		readonly IonStormDamageInfo info;
		readonly World world;

		WeaponInfo weapon;
		WeatherController weatherController;
		int cooldown;
		bool initialized;

		public IonStormDamage(ActorInitializer init, IonStormDamageInfo info)
		{
			this.info = info;
			world = init.World;
		}

		void ITick.Tick(Actor self)
		{
			if (!initialized)
			{
				weatherController = self.TraitOrDefault<WeatherController>();

				var weaponToLower = info.Weapon.ToLowerInvariant();
				if (!self.World.Map.Rules.Weapons.TryGetValue(weaponToLower, out weapon))
					weapon = null;

				initialized = true;
			}

			if (weatherController == null || weapon == null)
				return;

			if (weatherController.Intensity < info.IntensityThreshold)
				return;

			if (--cooldown > 0)
				return;

			var scaledInterval = (int)(info.StrikeInterval / weatherController.Intensity);
			cooldown = world.SharedRandom.Next(scaledInterval / 2, scaledInterval * 3 / 2);

			for (var i = 0; i < info.MaxStrikes; i++)
			{
				WPos targetPos;
				var strikeActor = world.SharedRandom.Next(100) < info.ActorStrikeChance;

				if (strikeActor)
				{
					var candidates = world.Actors
						.Where(a => a.IsInWorld && !a.IsDead && a.Info.HasTraitInfo<HealthInfo>())
						.ToArray();

					if (candidates.Length > 0)
					{
						var target = candidates[world.SharedRandom.Next(candidates.Length)];
						targetPos = target.CenterPosition;
					}
					else
					{
						targetPos = RandomMapPosition();
						if (targetPos == WPos.Zero)
							continue;
					}
				}
				else
				{
					targetPos = RandomMapPosition();
					if (targetPos == WPos.Zero)
						continue;
				}

				var args = new WarheadArgs
				{
					Weapon = weapon,
					Source = targetPos,
					SourceActor = self,
					WeaponTarget = Target.FromPos(targetPos),
				};

				weapon.Impact(Target.FromPos(targetPos), args);
			}
		}

		WPos RandomMapPosition()
		{
			// Map.Bounds is in projected (u,v) space; on isometric TS maps that is not
			// the same as CPos. ChooseRandomCell unprojects correctly so strikes cover
			// the whole map instead of clustering in one corner.
			var cell = world.Map.ChooseRandomCell(world.SharedRandom);
			return world.Map.CenterOfCell(cell);
		}
	}
}
