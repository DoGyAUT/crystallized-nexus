#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.Graphics;
using OpenRA.Mods.Cnc.Traits;
using OpenRA.Mods.Cnc.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;
using CncUtil = OpenRA.Mods.Cnc.Util;

namespace OpenRA.Mods.CN.Effects
{
	public sealed class VoxelDebris : IEffect, ISpatiallyPartitionable
	{
		readonly World world;
		readonly ModelRenderer renderer;
		readonly ModelAnimation[] model;
		readonly WRot camera;
		readonly WRot lightSource;
		readonly ImmutableArray<float> lightAmbientColor;
		readonly ImmutableArray<float> lightDiffuseColor;
		readonly string paletteName;
		readonly string normalsPaletteName;
		readonly string shadowPaletteName;
		readonly float scale;
		readonly int fullBrightStartIndex;
		readonly int fullBrightEndIndex;
		readonly int fullBrightStartIndex2;
		readonly int fullBrightEndIndex2;
		readonly float bloomGlowIntensity;
		readonly Size screenMapSize;
		readonly int gravity;
		readonly WeaponInfo explosionWeapon;
		readonly Actor sourceActor;
		readonly int yawRate;
		readonly int pitchRate;
		readonly int rollRate;
		readonly int groundLifetime;

		WPos position;
		WVec velocity;
		WRot rotation;
		int lifetime;
		bool resting;

		public VoxelDebris(
			World world,
			IModel voxel,
			ModelRenderer renderer,
			RenderVoxelsInfo renderInfo,
			WPos position,
			WRot rotation,
			WVec velocity,
			WRot camera,
			WRot lightSource,
			string ownerInternalName,
			int lifetime,
			int groundLifetime,
			int gravity,
			WeaponInfo explosionWeapon,
			Actor sourceActor,
			int yawRate,
			int pitchRate,
			int rollRate,
			Size screenMapSize,
			bool showShadow)
		{
			this.world = world;
			this.renderer = renderer;
			this.position = position;
			this.rotation = rotation;
			this.velocity = velocity;
			this.camera = camera;
			this.lightSource = lightSource;
			this.lifetime = lifetime;
			this.groundLifetime = groundLifetime;
			this.gravity = gravity;
			this.explosionWeapon = explosionWeapon;
			this.sourceActor = sourceActor;
			this.yawRate = yawRate;
			this.pitchRate = pitchRate;
			this.rollRate = rollRate;
			this.screenMapSize = screenMapSize;

			scale = renderInfo.Scale;
			lightAmbientColor = renderInfo.LightAmbientColor;
			lightDiffuseColor = renderInfo.LightDiffuseColor;
			paletteName = renderInfo.Palette ?? renderInfo.PlayerPalette + ownerInternalName;
			normalsPaletteName = renderInfo.NormalsPalette;
			shadowPaletteName = renderInfo.ShadowPalette;
			fullBrightStartIndex = renderInfo.FullBrightStartIndex;
			fullBrightEndIndex = renderInfo.FullBrightEndIndex;
			fullBrightStartIndex2 = renderInfo.FullBrightStartIndex2;
			fullBrightEndIndex2 = renderInfo.FullBrightEndIndex2;
			bloomGlowIntensity = renderInfo.BloomGlowIntensity;

			model =
			[
				new ModelAnimation(voxel, () => WVec.Zero, () => this.rotation, () => false, () => 0, showShadow)
			];

			world.ScreenMap.Add(this, position, screenMapSize);
		}

		void IEffect.Tick(World world)
		{
			if (resting)
			{
				if (--lifetime <= 0)
				{
					ExplodeAndRemove(world);
					return;
				}

				world.ScreenMap.Update(this, position, screenMapSize);
				return;
			}

			position += velocity;
			velocity -= new WVec(WDist.Zero, WDist.Zero, new WDist(gravity));
			rotation = new WRot(
				new WAngle(rotation.Roll.Angle + rollRate),
				new WAngle(rotation.Pitch.Angle + pitchRate),
				new WAngle(rotation.Yaw.Angle + yawRate));

			if (--lifetime <= 0)
			{
				ExplodeAndRemove(world);
				return;
			}

			var distanceAboveTerrain = world.Map.DistanceAboveTerrain(position).Length;
			var terrainZ = position.Z - distanceAboveTerrain;
			var bottomOffset = ModelBottomOffset();
			if (position.Z + bottomOffset <= terrainZ)
			{
				position = new WPos(position.X, position.Y, terrainZ - bottomOffset);
				velocity = WVec.Zero;
				lifetime = groundLifetime;
				resting = true;
			}

			world.ScreenMap.Update(this, position, screenMapSize);
		}

		void ExplodeAndRemove(World world)
		{
			if (explosionWeapon != null && sourceActor != null)
			{
				var target = Target.FromPos(position);
				var args = new WarheadArgs
				{
					Weapon = explosionWeapon,
					Source = position,
					SourceActor = sourceActor,
					ImpactPosition = position,
					WeaponTarget = target,
				};

				explosionWeapon.Impact(target, args);
			}

			world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); });
		}

		int ModelBottomOffset()
		{
			var scaleTransform = CncUtil.ScaleMatrix(scale, scale, scale);
			var rotationTransform = CncUtil.MakeFloatMatrix(rotation.AsMatrix());
			var worldTransform = CncUtil.MatrixMultiply(scaleTransform, rotationTransform);
			var bounds = CncUtil.MatrixAABBMultiply(worldTransform, model[0].Model.Bounds(model[0].FrameFunc()));

			return (int)Math.Floor(Math.Min(bounds[2], bounds[5]));
		}

		IEnumerable<IRenderable> IEffect.Render(WorldRenderer wr)
		{
			if (world.FogObscures(position))
				yield break;

			yield return new ModelRenderable(
				renderer, model, position, 0, camera, scale,
				lightSource, lightAmbientColor, lightDiffuseColor,
				wr.Palette(paletteName), wr.Palette(normalsPaletteName), wr.Palette(shadowPaletteName),
				1f, float3.Ones, TintModifiers.None, ShadowGroundZ,
				fullBrightStartIndex: fullBrightStartIndex, fullBrightEndIndex: fullBrightEndIndex,
				fullBrightStartIndex2: fullBrightStartIndex2, fullBrightEndIndex2: fullBrightEndIndex2,
				bloomGlowIntensity: bloomGlowIntensity);
		}

		int? ShadowGroundZ()
		{
			return position.Z - world.Map.DistanceAboveTerrain(position).Length;
		}
	}
}
