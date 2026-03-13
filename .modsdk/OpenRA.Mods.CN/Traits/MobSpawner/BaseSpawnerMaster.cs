#region Copyright & License Information
/*
 * Originally written by Boolbada of OP Mod.
 * Ported to Crystallized Nexus by CN contributors.
 * Follows GPLv3 License as the OpenRA engine.
 */
#endregion

using System;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	public class BaseSpawnerSlaveEntry
	{
		public string ActorName = null;
		public Actor Actor = null;
		public BaseSpawnerSlave SpawnerSlave = null;

		public bool IsValid { get { return Actor != null && !Actor.IsDead; } }
	}

	[Desc("Base trait for actors that spawn a group of slave actors.")]
	public class BaseSpawnerMasterInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[Desc("Slave actors to spawn. List them all, duplicates allowed for multiple of the same type.")]
		public readonly string[] Actors;

		[Desc("How many slaves to create immediately. -1 = all.")]
		public readonly int InitialActorCount = -1;

		[Desc("Armament name that triggers slaves to attack the master's target.")]
		public readonly string SpawnerArmamentName = "primary";

		[Desc("What happens to slaves when master is killed?")]
		public readonly SpawnerSlaveDisposal SlaveDisposalOnKill = SpawnerSlaveDisposal.KillSlaves;

		[Desc("What happens to slaves when master is mind controlled?")]
		public readonly SpawnerSlaveDisposal SlaveDisposalOnOwnerChange = SpawnerSlaveDisposal.GiveSlavesToAttacker;

		[Desc("Disable slave regeneration entirely.")]
		public readonly bool NoRegeneration = false;

		[Desc("Spawn all dead slaves at once when regenerating, instead of one by one.")]
		public readonly bool SpawnAllAtOnce = false;

		[Desc("Ticks between each respawn attempt.")]
		public readonly int RespawnTicks = 150;

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (Actors == null || Actors.Length == 0)
				throw new YamlException($"Actors is null or empty for a spawner trait in actor type {ai.Name}!");

			if (InitialActorCount > Actors.Length)
				throw new YamlException($"InitialActorCount can't be larger than Actors count! (Actor type = {ai.Name})");

			if (InitialActorCount < -1)
				throw new YamlException($"InitialActorCount must be -1 or non-negative. Actor type = {ai.Name}");
		}

		public override object Create(ActorInitializer init) { return new BaseSpawnerMaster(init, this); }
	}

	public class BaseSpawnerMaster : ConditionalTrait<BaseSpawnerMasterInfo>,
		INotifyCreated, INotifyKilled, INotifyOwnerChanged, INotifyActorDisposing
	{
		protected readonly BaseSpawnerSlaveEntry[] SlaveEntries;

		readonly Actor self;
		IFacing facing;
		ExitInfo[] exits;
		RallyPoint rallyPoint;

		int exitRoundRobin = -1;

		public BaseSpawnerMaster(ActorInitializer init, BaseSpawnerMasterInfo info) : base(info)
		{
			self = init.Self;
			SlaveEntries = CreateSlaveEntries(info);

			for (var i = 0; i < info.Actors.Length; i++)
				SlaveEntries[i].ActorName = info.Actors[i].ToLowerInvariant();
		}

		public virtual BaseSpawnerSlaveEntry[] CreateSlaveEntries(BaseSpawnerMasterInfo info)
		{
			var entries = new BaseSpawnerSlaveEntry[info.Actors.Length];
			for (int i = 0; i < entries.Length; i++)
				entries[i] = new BaseSpawnerSlaveEntry();
			return entries;
		}

		protected override void Created(Actor self)
		{
			base.Created(self);

			facing = self.TraitOrDefault<IFacing>();
			exits = self.Info.TraitInfos<ExitInfo>().ToArray();
			rallyPoint = self.TraitOrDefault<RallyPoint>();

			int burst = Info.InitialActorCount == -1 ? Info.Actors.Length : Info.InitialActorCount;
			if (!IsTraitDisabled)
				for (int i = 0; i < burst; i++)
					Replenish(self, SlaveEntries);
		}

		public void Replenish(Actor self, BaseSpawnerSlaveEntry[] slaveEntries)
		{
			if (Info.SpawnAllAtOnce)
			{
				foreach (var se in slaveEntries)
					if (!se.IsValid)
						Replenish(self, se);
			}
			else
			{
				var entry = SelectEntryToSpawn(slaveEntries);
				if (entry == null)
					return;

				Replenish(self, entry);
			}
		}

		public void Replenish(Actor self, BaseSpawnerSlaveEntry entry)
		{
			if (entry.IsValid)
				throw new InvalidOperationException("Replenish must not be run on a valid entry!");

			var td = new TypeDictionary { new OwnerInit(self.Owner) };

			var slave = self.World.CreateActor(false, entry.ActorName, td);
			InitializeSlaveEntry(slave, entry);
			entry.SpawnerSlave.LinkMaster(slave, self, this);
		}

		public virtual void InitializeSlaveEntry(Actor slave, BaseSpawnerSlaveEntry entry)
		{
			entry.Actor = slave;
			entry.SpawnerSlave = slave.Trait<BaseSpawnerSlave>();
		}

		protected BaseSpawnerSlaveEntry SelectEntryToSpawn(BaseSpawnerSlaveEntry[] slaveEntries)
		{
			var candidates = slaveEntries.Where(m => !m.IsValid);
			if (!candidates.Any())
				return null;

			return candidates.Random(self.World.SharedRandom);
		}

		public virtual void Killed(Actor self, AttackInfo e)
		{
			foreach (var se in SlaveEntries)
				if (se.IsValid)
					se.SpawnerSlave.OnMasterKilled(se.Actor, e.Attacker, Info.SlaveDisposalOnKill);
		}

		public virtual void OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			self.World.AddFrameEndTask(w =>
			{
				foreach (var se in SlaveEntries)
					if (se.IsValid)
						se.SpawnerSlave.OnMasterOwnerChanged(self, oldOwner, newOwner, Info.SlaveDisposalOnOwnerChange);
			});
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			foreach (var se in SlaveEntries)
				if (se.IsValid)
					se.Actor.Dispose();
		}

		public void SpawnIntoWorld(Actor self, Actor slave, WPos centerPosition)
		{
			var exit = ChooseExit(self);
			SetSpawnedFacing(slave, self, exit);

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead)
					return;

				var spawnOffset = exit == null ? WVec.Zero : exit.SpawnOffset;
				var spawnPos = centerPosition + spawnOffset;

				// IPositionable.SetVisualPosition was removed; use Trait<IOccupySpace> or just SetPosition.
				var positionable = slave.Trait<IPositionable>();
				positionable.SetPosition(slave, spawnPos);

				var location = self.World.Map.CellContaining(spawnPos);
				var mv = slave.Trait<IMove>();

				// MoveIntoWorld removed in newer OpenRA; use MoveTo instead.
				slave.QueueActivity(mv.MoveTo(location, 2));

				// RallyPoint.Path is List<CPos> - iterate directly, no CellContaining needed.
				if (rallyPoint != null)
					foreach (var cell in rallyPoint.Path)
						slave.QueueActivity(mv.MoveTo(cell, 2));

				w.Add(slave);
			});
		}

		ExitInfo ChooseExit(Actor self)
		{
			if (exits.Length == 0)
				return null;

			exitRoundRobin = (exitRoundRobin + 1) % exits.Length;
			return exits[exitRoundRobin];
		}

		void SetSpawnedFacing(Actor spawned, Actor spawner, ExitInfo exit)
		{
			var facingOffset = facing?.Facing ?? WAngle.Zero;
			var exitFacing = exit?.Facing ?? WAngle.Zero;
			var spawnFacing = new WAngle(facingOffset.Angle + exitFacing.Angle);

			var spawnIFacing = spawned.TraitOrDefault<IFacing>();
			if (spawnIFacing != null)
				spawnIFacing.Facing = spawnFacing;

			// Turreted has no public facing setter; slaves self-align on attack orders.
		}

		public void StopSlaves()
		{
			foreach (var se in SlaveEntries)
			{
				if (!se.IsValid)
					continue;

				se.SpawnerSlave.Stop(se.Actor);
			}
		}

		public virtual void OnSlaveKilled(Actor self, Actor slave) { }
	}
}
