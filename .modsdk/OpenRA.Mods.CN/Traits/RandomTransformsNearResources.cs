#region Copyright & License Information
/*
 * Crystallized Nexus - RandomTransformsNearResources
 * Replaces an actor with a randomly chosen actor when a resource spawns adjacent.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Manages resource-triggered random tree transformations.")]
	public class RandomTransformsNearResourcesManagerInfo : TraitInfo, Requires<IResourceLayerInfo>, ILobbyOptions
	{
		const string OptionId = "treemutations";

		[Desc("Descriptive label for the tree mutation checkbox in the lobby.")]
		public readonly string CheckboxLabel = "Tree Mutations";

		[Desc("Tooltip description for the tree mutation checkbox in the lobby.")]
		public readonly string CheckboxDescription = "Allow trees near Tiberium to transform into mutated flora.";

		[Desc("Default value of the tree mutation checkbox in the lobby.")]
		public readonly bool CheckboxEnabled = true;

		[Desc("Prevent the tree mutation state from being changed in the lobby.")]
		public readonly bool CheckboxLocked = false;

		[Desc("Whether to display the tree mutation checkbox in the lobby.")]
		public readonly bool CheckboxVisible = true;

		[Desc("Display order for the tree mutation checkbox in the lobby.")]
		public readonly int CheckboxDisplayOrder = 9;

		[Desc("Options category in which to display the tree mutation checkbox in the lobby.")]
		public readonly string CheckboxCategory = null;

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, OptionId,
				CheckboxLabel, CheckboxDescription, CheckboxVisible, CheckboxDisplayOrder, CheckboxEnabled, CheckboxLocked, CheckboxCategory);
		}

		public bool Enabled(World world)
		{
			return world.LobbyInfo.GlobalSettings.OptionOrDefault(OptionId, CheckboxEnabled);
		}

		public override object Create(ActorInitializer init) { return new RandomTransformsNearResourcesManager(init.Self, this); }
	}

	public class RandomTransformsNearResourcesManager : ITick, INotifyCreated, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly RandomTransformsNearResourcesManagerInfo info;
		readonly IResourceLayer resourceLayer;
		readonly Dictionary<CPos, List<RandomTransformsNearResources>> transformsByCell = [];
		readonly List<RandomTransformsNearResources> activeTransforms = [];

		// transform -> its index in activeTransforms, for O(1) swap-removal.
		readonly Dictionary<RandomTransformsNearResources, int> activeIndex = [];

		// Refreshes triggered by resource-cell changes are deferred to the next
		// Tick so a burst of Tiberium growth/decay around the same tree collapses
		// into a single re-evaluation instead of recomputing on every event.
		readonly HashSet<RandomTransformsNearResources> pendingRefresh = [];

		bool enabled;

		public RandomTransformsNearResourcesManager(Actor self, RandomTransformsNearResourcesManagerInfo info)
		{
			this.info = info;
			resourceLayer = self.Trait<IResourceLayer>();
		}

		void INotifyCreated.Created(Actor self)
		{
			enabled = info.Enabled(self.World);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (enabled)
				resourceLayer.CellChanged += ResourceLayerCellChanged;
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			if (enabled)
				resourceLayer.CellChanged -= ResourceLayerCellChanged;
		}

		public void Register(RandomTransformsNearResources transform)
		{
			if (!enabled)
				return;

			var location = transform.Location;
			if (!transformsByCell.TryGetValue(location, out var transforms))
			{
				transforms = [];
				transformsByCell.Add(location, transforms);
			}

			transforms.Add(transform);
			Refresh(transform);
		}

		public void Unregister(RandomTransformsNearResources transform)
		{
			if (!enabled)
				return;

			if (transformsByCell.TryGetValue(transform.Location, out var transforms))
			{
				transforms.Remove(transform);
				if (transforms.Count == 0)
					transformsByCell.Remove(transform.Location);
			}

			pendingRefresh.Remove(transform);
			RemoveActive(transform);
		}

		public void UpdateLocation(RandomTransformsNearResources transform, CPos oldLocation, CPos newLocation)
		{
			if (!enabled)
				return;

			if (oldLocation == newLocation)
				return;

			if (transformsByCell.TryGetValue(oldLocation, out var transforms))
			{
				transforms.Remove(transform);
				if (transforms.Count == 0)
					transformsByCell.Remove(oldLocation);
			}

			if (!transformsByCell.TryGetValue(newLocation, out transforms))
			{
				transforms = [];
				transformsByCell.Add(newLocation, transforms);
			}

			transforms.Add(transform);
			Refresh(transform);
		}

		void ITick.Tick(Actor self)
		{
			if (!enabled)
				return;

			// Drain deferred refreshes from the previous interval's resource
			// changes. Refresh mutates activeTransforms/activeIndex but never
			// pendingRefresh, so iterating it here is safe.
			if (pendingRefresh.Count > 0)
			{
				foreach (var transform in pendingRefresh)
					Refresh(transform);

				pendingRefresh.Clear();
			}

			for (var i = 0; i < activeTransforms.Count;)
			{
				var transform = activeTransforms[i];
				if (transform.Tick())
					RemoveActiveAt(i);
				else
					i++;
			}
		}

		void ResourceLayerCellChanged(CPos cell, string resourceType)
		{
			// Nothing left to mutate — skip the per-event neighbour scan entirely
			// (Tiberium cell changes fire very frequently).
			if (transformsByCell.Count == 0)
				return;

			foreach (var direction in CVec.Directions)
			{
				var location = cell - direction;
				if (!transformsByCell.TryGetValue(location, out var transforms))
					continue;

				for (var i = 0; i < transforms.Count; i++)
					pendingRefresh.Add(transforms[i]);
			}
		}

		void Refresh(RandomTransformsNearResources transform)
		{
			if (transform.IsComplete || !transform.HasRequiredAdjacentResources(resourceLayer))
			{
				RemoveActive(transform);
				return;
			}

			AddActive(transform);
		}

		void AddActive(RandomTransformsNearResources transform)
		{
			if (activeIndex.ContainsKey(transform))
				return;

			activeIndex[transform] = activeTransforms.Count;
			activeTransforms.Add(transform);
		}

		void RemoveActive(RandomTransformsNearResources transform)
		{
			if (activeIndex.TryGetValue(transform, out var index))
				RemoveActiveAt(index);
		}

		// Swap-with-last removal: O(1); order of activeTransforms is irrelevant.
		void RemoveActiveAt(int index)
		{
			var removed = activeTransforms[index];
			var lastIndex = activeTransforms.Count - 1;
			var last = activeTransforms[lastIndex];

			activeTransforms[index] = last;
			activeIndex[last] = index;

			activeTransforms.RemoveAt(lastIndex);
			activeIndex.Remove(removed);
		}
	}

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

	public class RandomTransformsNearResources : INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyCenterPositionChanged
	{
		readonly RandomTransformsNearResourcesInfo info;
		readonly Actor self;
		int delay;
		RandomTransformsNearResourcesManager manager;

		public CPos Location { get; private set; }
		public bool IsComplete => delay < 0;

		public RandomTransformsNearResources(Actor self, RandomTransformsNearResourcesInfo info)
		{
			this.self = self;
			delay = Common.Util.RandomInRange(self.World.SharedRandom, info.Delay);
			Location = self.Location;
			this.info = info;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			manager = self.World.WorldActor.Trait<RandomTransformsNearResourcesManager>();
			manager.Register(this);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			manager.Unregister(this);
			manager = null;
		}

		public bool Tick()
		{
			if (delay < 0)
				return true;

			delay--;
			if (delay < 0)
				Transform(self);

			return delay < 0;
		}

		public bool HasRequiredAdjacentResources(IResourceLayer resourceLayer)
		{
			var adjacent = 0;
			foreach (var direction in CVec.Directions)
			{
				var resource = resourceLayer.GetResource(Location + direction);
				if (resource.Type == null || resource.Type != info.Type)
					continue;

				if (resource.Density < info.Density)
					continue;

				if (++adjacent < info.Adjacency)
					continue;

				return true;
			}

			return false;
		}

		void INotifyCenterPositionChanged.CenterPositionChanged(Actor self, byte oldLayer, byte newLayer)
		{
			var oldLocation = Location;
			Location = self.Location;
			manager?.UpdateLocation(this, oldLocation, Location);
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
