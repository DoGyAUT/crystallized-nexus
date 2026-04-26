#region Copyright & License Information
/*
 * Crystallized Nexus - RandomTransformsNearResources
 * Replaces an actor with a randomly chosen actor when a resource spawns adjacent.
 */
#endregion

using System.Collections.Immutable;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Replace with a randomly chosen actor when a resource spawns adjacent.")]
	public class RandomTransformsNearResourcesInfo : TraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("List of actors to randomly pick from when transforming.")]
		public readonly string[] IntoActors = null;

		public readonly CVec Offset = CVec.Zero;

		[Desc("Don't render the make animation.")]
		public readonly bool SkipMakeAnims = false;

		[FieldLoader.Require]
		[Desc("Resource type which triggers the transformation.")]
		public readonly string Type = null;

		[Desc("Resource density threshold which is required.")]
		public readonly byte Density = 1;

		[Desc("This many adjacent resource tiles are required.")]
		public readonly int Adjacency = 1;

		[Desc("The range of time (in ticks) until the transformation starts.")]
		public readonly ImmutableArray<int> Delay = [1000, 3000];

		public override object Create(ActorInitializer init) { return new RandomTransformsNearResources(init.Self, this); }
	}

	public class RandomTransformsNearResources : ITick
	{
		readonly RandomTransformsNearResourcesInfo info;
		readonly IResourceLayer resourceLayer;
		int delay;

		public RandomTransformsNearResources(Actor self, RandomTransformsNearResourcesInfo info)
		{
			resourceLayer = self.World.WorldActor.Trait<IResourceLayer>();
			delay = Common.Util.RandomInRange(self.World.SharedRandom, info.Delay);
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			if (delay < 0)
				return;

			var adjacent = 0;
			foreach (var direction in CVec.Directions)
			{
				var location = self.Location + direction;
				var resource = resourceLayer.GetResource(location);
				if (resource.Type == null || resource.Type != info.Type)
					continue;

				if (resource.Density < info.Density)
					continue;

				if (++adjacent < info.Adjacency)
					continue;

				delay--;
				break;
			}

			if (delay < 0)
				Transform(self);
		}

		void Transform(Actor self)
		{
			var targetActor = info.IntoActors[self.World.SharedRandom.Next(info.IntoActors.Length)];
			var transform = new Transform(targetActor)
			{
				SkipMakeAnims = info.SkipMakeAnims,
				Offset = info.Offset
			};

			var facing = self.TraitOrDefault<IFacing>();
			if (facing != null)
				transform.Facing = facing.Facing;

			self.QueueActivity(false, transform);
		}
	}
}
