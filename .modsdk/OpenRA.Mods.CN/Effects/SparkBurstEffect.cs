#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.CN.Warheads;
using OpenRA.Primitives;

namespace OpenRA.Mods.CN.Effects
{
	public sealed class SparkBurstEffect : IEffect, IEffectAboveShroud
	{
		sealed class SparkRenderable : IRenderable, IFinalizedRenderable
		{
			readonly WPos end;
			readonly float width;
			readonly Color color;

			public SparkRenderable(WPos start, WPos end, float width, Color color)
			{
				Pos = start;
				this.end = end;
				this.width = width;
				this.color = color;
			}

			public WPos Pos { get; }
			public int ZOffset => 0;
			public bool IsDecoration => true;

			public IRenderable WithZOffset(int newOffset) { return this; }
			public IRenderable OffsetBy(in WVec vec) { return new SparkRenderable(Pos + vec, end + vec, width, color); }
			public IRenderable AsDecoration() { return this; }
			public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }

			public void Render(WorldRenderer wr)
			{
				Game.Renderer.WorldRgbaColorRenderer.DrawLine(
					wr.Screen3DPosition(Pos),
					wr.Screen3DPosition(end),
					width,
					color,
					ignoreWorldTint: true,
					isBloomSource: true);
			}

			public void RenderDebugGeometry(WorldRenderer wr) { }
			public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
		}

		struct Spark
		{
			public WPos Pos;
			public WPos PreviousPos;
			public WVec Velocity;
			public int Age;
			public int RestAge;
			public int Lifetime;
			public int RestLifetime;
			public int Length;
			public int Width;
			public bool Resting;
		}

		readonly SparkBurstWarhead info;
		readonly Spark[] sparks;
		int activeSparks;

		public SparkBurstEffect(World world, WPos pos, SparkBurstWarhead info)
		{
			this.info = info;
			sparks = new Spark[info.Count];
			activeSparks = sparks.Length;

			for (var i = 0; i < sparks.Length; i++)
			{
				var start = pos;
				if (info.Inaccuracy.Length > 0)
					start += WVec.FromPDF(world.LocalRandom, 2) * info.Inaccuracy.Length / 1024;

				var horizontalSpeed = Common.Util.RandomDistance(world.LocalRandom, info.Speed).Length;
				var verticalSpeed = Common.Util.RandomDistance(world.LocalRandom, info.UpwardSpeed).Length;
				var velocity = new WVec(0, -horizontalSpeed, verticalSpeed).Rotate(WRot.FromFacing(world.LocalRandom.Next(256)));

				sparks[i] = new Spark
				{
					Pos = start,
					PreviousPos = start,
					Velocity = velocity,
					Lifetime = Common.Util.RandomInRange(world.LocalRandom, info.Lifetime),
					RestLifetime = Common.Util.RandomInRange(world.LocalRandom, info.RestLifetime),
					Length = Common.Util.RandomInRange(world.LocalRandom, info.Length),
					Width = Common.Util.RandomInRange(world.LocalRandom, info.Width)
				};
			}
		}

		public void Tick(World world)
		{
			var stillAlive = 0;

			for (var i = 0; i < sparks.Length; i++)
			{
				var spark = sparks[i];
				if (spark.Age < 0)
					continue;

				if (spark.Resting)
				{
					if (++spark.RestAge >= spark.RestLifetime)
					{
						spark.Age = -1;
						sparks[i] = spark;
						continue;
					}
				}
				else
				{
					spark.Age++;
					spark.PreviousPos = spark.Pos;
					spark.Velocity += new WVec(0, 0, -info.Gravity.Length);
					spark.Pos += spark.Velocity;

					var distanceAboveTerrain = world.Map.DistanceAboveTerrain(spark.Pos).Length;
					if (distanceAboveTerrain <= 0 || spark.Age >= spark.Lifetime)
					{
						spark.Pos -= new WVec(0, 0, distanceAboveTerrain);
						spark.PreviousPos = spark.Pos + new WVec(0, 0, spark.Length);
						spark.Resting = true;
					}
				}

				stillAlive++;
				sparks[i] = spark;
			}

			activeSparks = stillAlive;
			if (activeSparks == 0)
				world.AddFrameEndTask(w => w.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			foreach (var spark in sparks)
			{
				if (spark.Age < 0 || !spark.Resting || wr.World.FogObscures(spark.Pos))
					continue;

				var color = RestColor(spark.RestAge, spark.RestLifetime);
				if (color.A == 0)
					continue;

				// Lift slightly above terrain surface so depth test passes against ground,
				// while still being occluded by voxels and sprites above.
				var pos = spark.Pos + new WVec(0, 0, 48);
				var end = pos + new WVec(spark.Length / 2, 0, 0);
				yield return new SparkRenderable(pos, end, spark.Width, color);
			}
		}

		public IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr)
		{
			foreach (var spark in sparks)
			{
				if (spark.Age < 0 || spark.Resting || wr.World.FogObscures(spark.Pos))
					continue;

				var color = FlightColor(spark.Age, spark.Lifetime);
				if (color.A == 0)
					continue;

				var end = spark.Pos + (spark.PreviousPos - spark.Pos) * spark.Length / 1024;
				yield return new SparkRenderable(spark.Pos, end, spark.Width, color);
			}
		}

		Color FlightColor(int age, int lifetime)
		{
			var t = lifetime > 0 ? age * 1f / lifetime : 1f;
			return t < 0.35f
				? Exts.ColorLerp(t / 0.35f, info.HotColor, info.MidColor)
				: Exts.ColorLerp((t - 0.35f) / 0.65f, info.MidColor, info.ColdColor);
		}

		Color RestColor(int age, int lifetime)
		{
			var alpha = lifetime > 0 ? 255 - 255 * age / lifetime : 0;
			return Color.FromArgb(alpha.Clamp(0, 255), info.ColdColor);
		}
	}
}
