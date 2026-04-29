#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Frozen;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Warheads
{
	[Desc("Detonates a weapon on each resource tile of the specified types within the impact area.",
		"Use for explosive tiberium variants (blue, red) that chain-react when hit.")]
	public class ExplodeResourceWarhead : Warhead, IRulesetLoaded<WeaponInfo>
	{
		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Weapon to detonate at each matching resource tile.")]
		public readonly string Weapon = null;

		[Desc("Radius in cells around the impact point to scan for resource tiles.")]
		public readonly int Radius = 1;

		[Desc("Resource type names that trigger the explosion.",
			"Leave empty to match all resource types.")]
		public readonly FrozenSet<string> ResourceTypes = FrozenSet<string>.Empty;

		[Desc("Remove the resource tile after detonating.")]
		public readonly bool RemoveResource = true;

		WeaponInfo weapon;

		public void RulesetLoaded(Ruleset rules, WeaponInfo info)
		{
			if (!rules.Weapons.TryGetValue(Weapon.ToLowerInvariant(), out weapon))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{Weapon}'");
		}

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			if (target.Type == TargetType.Invalid)
				return;

			var firedBy = args.SourceActor;
			var world = firedBy.World;
			var pos = target.CenterPosition;

			if (world.Map.DistanceAboveTerrain(pos) > AirThreshold)
				return;

			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (resourceLayer == null)
				return;

			var targetTile = world.Map.CellContaining(pos);
			var matchAll = ResourceTypes.Count == 0;

			foreach (var cell in world.Map.FindTilesInAnnulus(targetTile, 0, Radius))
			{
				var resource = resourceLayer.GetResource(cell);
				if (resource.Type == null)
					continue;

				if (!matchAll && !ResourceTypes.Contains(resource.Type))
					continue;

				var cellCenter = world.Map.CenterOfCell(cell);
				var cellTarget = Target.FromPos(cellCenter);

				var projectileArgs = new ProjectileArgs
				{
					Weapon = weapon,
					Facing = WAngle.Zero,
					CurrentMuzzleFacing = () => WAngle.Zero,
					DamageModifiers = args.DamageModifiers,
					InaccuracyModifiers = Array.Empty<int>(),
					RangeModifiers = Array.Empty<int>(),
					Source = cellCenter,
					CurrentSource = () => cellCenter,
					SourceActor = firedBy,
					PassiveTarget = cellCenter,
					GuidedTarget = cellTarget,
				};

				if (weapon.Projectile != null)
				{
					var projectile = weapon.Projectile.Create(projectileArgs);
					if (projectile != null)
						world.AddFrameEndTask(w => w.Add(projectile));
				}
				else
				{
					weapon.Impact(cellTarget, firedBy);
				}

				if (RemoveResource)
					resourceLayer.ClearResources(cell);
			}
		}
	}
}
