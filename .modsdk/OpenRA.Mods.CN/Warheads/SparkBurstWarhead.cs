#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Immutable;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.CN.Effects;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Warheads
{
	[Desc("Spawns small ballistic spark particles at the impact position.")]
	public class SparkBurstWarhead : Warhead
	{
		static readonly BitSet<TargetableType> TargetTypeAir = new("Air");

		[Desc("Number of spark particles to spawn.")]
		public readonly int Count = 10;

		[Desc("Horizontal spark speed range in world units per tick.")]
		public readonly ImmutableArray<WDist> Speed = [new WDist(120), new WDist(360)];

		[Desc("Initial vertical spark speed range in world units per tick.")]
		public readonly ImmutableArray<WDist> UpwardSpeed = [new WDist(90), new WDist(260)];

		[Desc("Downward acceleration applied each tick.")]
		public readonly WDist Gravity = new(24);

		[Desc("Flight lifetime range before a spark is forced to settle.")]
		public readonly ImmutableArray<int> Lifetime = [10, 22];

		[Desc("Ticks that settled sparks remain visible on the ground.")]
		public readonly ImmutableArray<int> RestLifetime = [18, 36];

		[Desc("Spark streak length range.")]
		public readonly ImmutableArray<int> Length = [96, 196];

		[Desc("Spark line width range in screen pixels.")]
		public readonly ImmutableArray<int> Width = [1, 2];

		[Desc("Maximum inaccuracy of the spark spawn position relative to the impact position.")]
		public readonly WDist Inaccuracy = new(192);

		[Desc("Initial hot spark color.")]
		public readonly Color HotColor = Color.FromArgb(255, 214, 64);

		[Desc("Mid-flight spark color.")]
		public readonly Color MidColor = Color.FromArgb(255, 245, 170);

		[Desc("Settled/fading spark color.")]
		public readonly Color ColdColor = Color.FromArgb(255, 255, 255);

		ImpactActorType ActorTypeAtImpact(World world, WPos pos, Actor firedBy)
		{
			var anyInvalidActor = false;

			foreach (var victim in world.FindActorsOnCircle(pos, WDist.Zero))
			{
				if (!AffectsParent && victim == firedBy)
					continue;

				var activeShapes = victim.TraitsImplementing<HitShape>().Where(t => !t.IsTraitDisabled);
				if (!activeShapes.Any(s => s.DistanceFromEdge(victim, pos).Length <= 0))
					continue;

				if (IsValidAgainst(victim, firedBy))
					return ImpactActorType.Valid;

				anyInvalidActor = true;
			}

			return anyInvalidActor ? ImpactActorType.Invalid : ImpactActorType.None;
		}

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			if (target.Type == TargetType.Invalid || Count <= 0)
				return;

			var firedBy = args.SourceActor;
			var world = firedBy.World;
			var pos = target.CenterPosition;

			var actorAtImpact = ActorTypeAtImpact(world, pos, firedBy);
			if (actorAtImpact == ImpactActorType.Invalid)
				return;

			if (actorAtImpact == ImpactActorType.None)
			{
				var cell = world.Map.CellContaining(pos);
				if (!world.Map.Contains(cell))
					return;

				var dat = world.Map.DistanceAboveTerrain(pos);
				var terrainTypes = dat > AirThreshold ? TargetTypeAir : world.Map.GetTerrainInfo(cell).TargetTypes;
				if (!IsValidTarget(terrainTypes))
					return;
			}

			world.AddFrameEndTask(w => w.Add(new SparkBurstEffect(w, pos, this)));
		}
	}
}
