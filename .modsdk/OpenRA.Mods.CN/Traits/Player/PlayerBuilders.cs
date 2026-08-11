#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Attach to the Player actor. Stores Builder units that have been removed from the map ",
		"while they are constructing a structure, and returns them to the map afterwards. ",
		"Works like a Cargo trait with unlimited capacity, but off-map.")]
	public class PlayerBuildersInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) => new PlayerBuilders(init.World);
	}

	public class PlayerBuilders : INotifyActorDisposing
	{
		readonly World world;
		readonly HashSet<Actor> builders = [];

		public PlayerBuilders(World world)
		{
			this.world = world;
		}

		/// <summary>Cache a builder that has already been removed from the world.</summary>
		public void Store(Actor builder)
		{
			builders.Add(builder);
		}

		/// <summary>
		/// Return a previously stored builder to the map on the nearest free cell to <paramref name="near"/>.
		/// No-op if the builder was disposed in the meantime.
		/// </summary>
		public void ReturnBuilder(Actor builder, CPos near)
		{
			if (!builders.Remove(builder) || builder.IsDead || builder.Disposed)
				return;

			world.AddFrameEndTask(w =>
			{
				if (builder.IsDead || builder.Disposed || builder.IsInWorld)
					return;

				var mobile = builder.TraitOrDefault<Mobile>();
				var cell = mobile != null ? mobile.NearestMoveableCell(near) : near;

				builder.Trait<IPositionable>().SetPosition(builder, cell);
				w.Add(builder);
			});
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			foreach (var b in builders)
				if (!b.Disposed)
					b.Dispose();

			builders.Clear();
		}
	}
}
