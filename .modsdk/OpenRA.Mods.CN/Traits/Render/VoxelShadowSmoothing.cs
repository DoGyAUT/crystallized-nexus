#region Copyright & License Information
/*
 * Crystallized Nexus - VoxelShadowSmoothing
 * Smooths projected voxel shadows over changing terrain height and ramps.
 */
#endregion

using OpenRA.Mods.Cnc.Traits.Render;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Smooths the ground plane used by projected voxel shadows.")]
	public class VoxelShadowSmoothingInfo : ConditionalTraitInfo, Requires<RenderVoxelsInfo>
	{
		[Desc("Distance ahead and behind the actor to sample terrain height. " +
			"0 uses Aircraft.TerrainAltitudeSmoothing when available.")]
		public readonly WDist Range = WDist.Zero;

		[Desc("Number of terrain-height samples used across Range.")]
		public readonly int Samples = 9;

		[Desc("Ticks used to smooth changes in projected shadow height. 1 disables height smoothing.")]
		public readonly int GroundHeightSmoothingTicks = 3;

		[Desc("Ticks used to smooth changes in projected shadow ramp orientation. 1 disables orientation smoothing.")]
		public readonly int OrientationSmoothingTicks = 4;

		[Desc("Project the shadow onto the current terrain ramp orientation. " +
			"Disable for flying or hovering voxels to avoid hard shadow jumps over ramps.")]
		public readonly bool UseTerrainOrientation = true;

		[Desc("Use Aircraft.TerrainAltitudeSmoothing as the fallback Range for aircraft.")]
		public readonly bool UseAircraftTerrainAltitudeSmoothing = true;

		public override object Create(ActorInitializer init) { return new VoxelShadowSmoothing(init.Self, this); }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (Samples < 1)
				throw new YamlException($"{nameof(VoxelShadowSmoothing)}.{nameof(Samples)} must be at least 1.");

			if (GroundHeightSmoothingTicks < 1)
				throw new YamlException($"{nameof(VoxelShadowSmoothing)}.{nameof(GroundHeightSmoothingTicks)} must be at least 1.");

			if (OrientationSmoothingTicks < 1)
				throw new YamlException($"{nameof(VoxelShadowSmoothing)}.{nameof(OrientationSmoothingTicks)} must be at least 1.");

			base.RulesetLoaded(rules, ai);
		}
	}

	public class VoxelShadowSmoothing : ConditionalTrait<VoxelShadowSmoothingInfo>, ITick, IRenderVoxelShadowModifier
	{
		readonly Aircraft aircraft;

		bool initialized;
		WPos lastPos;
		WVec lastMove;
		int smoothedGroundZ;
		WRot smoothedOrientation;

		public VoxelShadowSmoothing(Actor self, VoxelShadowSmoothingInfo info)
			: base(info)
		{
			aircraft = self.TraitOrDefault<Aircraft>();
		}

		void ITick.Tick(Actor self)
		{
			Update(self);
		}

		public int? ShadowGroundZ(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			if (!initialized)
				Update(self);

			return smoothedGroundZ;
		}

		public WRot? ShadowGroundOrientation(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			if (!initialized)
				Update(self);

			return smoothedOrientation;
		}

		void Update(Actor self)
		{
			var pos = self.CenterPosition;
			var move = initialized ? pos - lastPos : WVec.Zero;
			if (move.HorizontalLength > 0)
				lastMove = move;

			lastPos = pos;

			var map = self.World.Map;
			var targetGroundZ = SmoothedTerrainHeight(map, pos, lastMove, ShadowRange());
			var targetOrientation = Info.UseTerrainOrientation
				? map.TerrainOrientation(map.CellContaining(pos))
				: WRot.None;

			if (!initialized)
			{
				smoothedGroundZ = targetGroundZ;
				smoothedOrientation = targetOrientation;
				initialized = true;
				return;
			}

			var heightTicks = Info.GroundHeightSmoothingTicks;
			smoothedGroundZ = heightTicks <= 1
				? targetGroundZ
				: (smoothedGroundZ * (heightTicks - 1) + targetGroundZ) / heightTicks;

			var orientationTicks = Info.OrientationSmoothingTicks;
			smoothedOrientation = orientationTicks <= 1
				? targetOrientation
				: WRot.SLerp(smoothedOrientation, targetOrientation, 1, orientationTicks);
		}

		WDist ShadowRange()
		{
			if (Info.Range.Length > 0)
				return Info.Range;

			if (Info.UseAircraftTerrainAltitudeSmoothing && aircraft != null)
				return aircraft.Info.TerrainAltitudeSmoothing;

			return WDist.Zero;
		}

		int SmoothedTerrainHeight(Map map, in WPos pos, in WVec move, WDist smoothing)
		{
			var range = smoothing.Length;
			var horizontal = move.HorizontalLength;
			if (Info.Samples == 1 || range <= 0 || horizontal == 0)
				return TerrainHeightAt(map, pos);

			var sum = 0;
			for (var i = 0; i < Info.Samples; i++)
			{
				var offset = -range + 2 * range * i / (Info.Samples - 1);
				var sample = pos + new WVec(move.X * offset / horizontal, move.Y * offset / horizontal, 0);
				sum += TerrainHeightAt(map, sample);
			}

			return sum / Info.Samples;
		}

		static int TerrainHeightAt(Map map, in WPos pos)
		{
			return pos.Z - map.DistanceAboveTerrain(pos).Length;
		}
	}
}
