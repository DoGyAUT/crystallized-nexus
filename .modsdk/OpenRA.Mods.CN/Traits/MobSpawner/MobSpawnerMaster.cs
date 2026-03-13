#region Copyright & License Information
/*
 * Originally written by Boolbada of OP Mod.
 * Ported to Crystallized Nexus by CN contributors.
 * Follows GPLv3 License as the OpenRA engine.
 */
#endregion

using System;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

/*
 * Modes of operation:
 *
 * IncludeMasterInAggregate: false (default / classic nexus)
 *   - Master is an invisible virtual nexus.
 *   - Its position is set to the average of all slave positions every tick.
 *   - Its health bar shows the sum of slave HP scaled to MaxHP.
 *   - Master is disposed when all slaves die.
 *
 * IncludeMasterInAggregate: true (visible squad leader)
 *   - Master is a real, visible unit that moves and animates normally.
 *   - SetNexusPosition is NOT called, so animations/shadows are unaffected.
 *   - HP bar still reflects aggregate slave HP.
 *   - Slaves die one-by-one as HP crosses evenly-spaced thresholds.
 *   - Master fights on alone after all slaves are gone.
 */

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Manages a group of slave actors that form a mob. The master acts as a virtual nexus.")]
	public class MobSpawnerMasterInfo : BaseSpawnerMasterInfo
	{
		[Desc("If true, new slaves exit (bud) from an existing member instead of the nexus spawn point.")]
		public readonly bool ExitByBudding = true;

		[Desc("If true, slaves can act independently (no forced movement/targeting from master).")]
		public readonly bool SlavesHaveFreeWill = false;

		[Desc("Aggregate slave HP into the master's health bar (C&C Generals-style mob).")]
		public readonly bool AggregateHealth = true;

		[Desc("Ticks between health bar refreshes. Visual only, no gameplay effect.")]
		public readonly int AggregateHealthUpdateDelay = 17;

		[Desc("Offset applied to spawn position relative to nexus center.")]
		public readonly WVec Offset = WVec.Zero;

		[Desc("If true, master is a visible unit rather than an invisible nexus. " +
			"Position averaging is disabled so animations and shadows play correctly. " +
			"Slaves die at evenly-spaced HP thresholds; master fights alone when all slaves are gone. " +
			"Convention: master and slaves should have the same Armor type, otherwise damage redirect " +
			"will not account for armor differences correctly.")]
		public readonly bool IncludeMasterInAggregate = false;

		[GrantedConditionReference]
		[Desc("Condition granted to the master while at least one slave is alive. " +
			"Use this with 'Targetable: RequiresCondition: !protected-by-slaves' to make " +
			"the master untargetable while slaves are alive.")]
		public readonly string ProtectedBySlavesCondition = null;

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (InitialActorCount == 0 && AggregateHealth)
				throw new YamlException($"MobSpawnerMaster on {ai.Name}: InitialActorCount cannot be 0 when AggregateHealth is true.");
		}

		public override object Create(ActorInitializer init) { return new MobSpawnerMaster(init, this); }
	}

	public class MobSpawnerMaster : BaseSpawnerMaster, INotifyCreated, INotifyOwnerChanged, ITick, IResolveOrder, INotifyAttack, INotifyDamage, INotifyEnteredCargo, INotifyExitedCargo
	{
		class MobSpawnerSlaveEntry : BaseSpawnerSlaveEntry
		{
			public new MobSpawnerSlave SpawnerSlave;
			public Health Health;
		}

		public new MobSpawnerMasterInfo Info { get; private set; }

		MobSpawnerSlaveEntry[] slaveEntries;

		bool hasSpawnedInitialLoad = false;
		int spawnReplaceTicks = 0;
		int aggregateHealthUpdateTicks = 0;
		bool isDamagePropagating = false;
		int protectedBySlavesToken = Actor.InvalidConditionToken;

		IPositionable position;
		Health health;

		public MobSpawnerMaster(ActorInitializer init, MobSpawnerMasterInfo info) : base(init, info)
		{
			Info = info;
		}

		protected override void Created(Actor self)
		{
			position = self.TraitOrDefault<IPositionable>();
			health = self.Trait<Health>();

			base.Created(self); // Spawns initial slaves (not yet in world)

			if (!IsTraitDisabled)
			{
				SpawnReplenishedSlaves(self);
				hasSpawnedInitialLoad = true;
			}

			UpdateProtectedCondition(self);
		}

		void UpdateProtectedCondition(Actor self)
		{
			if (string.IsNullOrEmpty(Info.ProtectedBySlavesCondition))
				return;

			var anySlaveAlive = slaveEntries.Any(s => s.IsValid && s.Actor.IsInWorld);

			if (anySlaveAlive && protectedBySlavesToken == Actor.InvalidConditionToken)
				protectedBySlavesToken = self.GrantCondition(Info.ProtectedBySlavesCondition);
			else if (!anySlaveAlive && protectedBySlavesToken != Actor.InvalidConditionToken)
				protectedBySlavesToken = self.RevokeCondition(protectedBySlavesToken);
		}

		public override BaseSpawnerSlaveEntry[] CreateSlaveEntries(BaseSpawnerMasterInfo info)
		{
			slaveEntries = new MobSpawnerSlaveEntry[info.Actors.Length];
			for (int i = 0; i < slaveEntries.Length; i++)
				slaveEntries[i] = new MobSpawnerSlaveEntry();

			return slaveEntries;
		}

		public override void InitializeSlaveEntry(Actor slave, BaseSpawnerSlaveEntry entry)
		{
			var se = entry as MobSpawnerSlaveEntry;
			base.InitializeSlaveEntry(slave, se);

			se.SpawnerSlave = slave.Trait<MobSpawnerSlave>();
			se.Health = slave.Trait<Health>();
		}

		// --- IResolveOrder ---

		public void ResolveOrder(Actor self, Order order)
		{
			if (Info.SlavesHaveFreeWill)
				return;

			if (order.OrderString == "Stop")
				StopSlaves();

			// Block EnterTransport if the transport can't fit the master + all alive slaves.
			// This prevents a partial load where only the master enters and slaves are left behind.
			if (order.OrderString == "EnterTransport" && order.Target.Type == TargetType.Actor)
			{
				var transport = order.Target.Actor;
				var cargo = transport.TraitOrDefault<Cargo>();
				if (cargo == null)
					return;

				var aliveSlaves = slaveEntries.Where(s => s.IsValid && s.Actor.IsInWorld).ToArray();

				// Calculate total weight of slaves that would need to enter
				var slaveWeight = aliveSlaves.Sum(se =>
					se.Actor.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1);

				// Check remaining capacity after the master is accounted for
				var masterWeight = self.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1;
				var totalRequired = masterWeight + slaveWeight;

				// HasSpace is internal — use CanLoad sequentially as a read-only probe.
				// We temporarily simulate: does the transport have room for master + all slaves?
				// Since CanLoad checks (totalWeight + reservedWeight + weight <= MaxWeight),
				// and nothing is reserved yet, we can check the sum directly via HasSpace proxy.
				var hasRoom = cargo.HasSpace(totalRequired);
				if (!hasRoom)
				{
					// Cancel the enter order — squad won't fit
					self.CancelActivity();
					return;
				}
			}
		}

		// --- INotifyAttack ---

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (Info.SlavesHaveFreeWill)
				return;

			AssignTargetsToSlaves(self, target);
		}

		// --- INotifyDamage ---
		// When the master takes a hit while slaves are alive, redirect the full
		// damage to a random slave and immediately undo the damage on the master.
		// We cannot use IDamageModifier to zero the hit because that also zeros
		// e.Damage.Value in this callback — leaving us with nothing to redirect.

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (isDamagePropagating)
				return;

			if (!Info.AggregateHealth)
				return;

			// Ignore self-inflicted HP adjustments from RefreshMasterHP
			if (e.Attacker == self)
				return;

			var aliveSlaves = slaveEntries.Where(s => s.IsValid && s.Actor.IsInWorld).ToArray();
			if (aliveSlaves.Length == 0)
				return;

			// Damage value before any modifiers — e.Damage.Value is post-modifier,
			// but since we don't use IDamageModifier the value is the real hit.
			var damageValue = e.Damage.Value;
			if (damageValue <= 0)
				return;

			isDamagePropagating = true;
			try
			{
				// Undo the hit on master
				health.InflictDamage(self, self, new Damage(-damageValue, e.Damage.DamageTypes), true);

				// Redirect to a random alive slave
				var target = aliveSlaves.Random(self.World.SharedRandom);
				target.Health.InflictDamage(target.Actor, e.Attacker ?? self, e.Damage, false);
			}
			finally
			{
				isDamagePropagating = false;
			}
		}
		// --- ITick ---

		void ITick.Tick(Actor self)
		{
			// Respawn timer
			if (spawnReplaceTicks > 0 && !IsTraitDisabled)
			{
				spawnReplaceTicks--;

				if (spawnReplaceTicks <= 0)
				{
					Replenish(self, slaveEntries);
					SpawnReplenishedSlaves(self);

					if (SelectEntryToSpawn(slaveEntries) != null)
						spawnReplaceTicks = Info.RespawnTicks;
				}
			}

			// Health display refresh
			if (Info.AggregateHealth)
			{
				if (!Info.IncludeMasterInAggregate)
					SetNexusPosition(self);

				RefreshMasterHP(self);
			}

			// Keep slaves moving/attacking with master
			if (!Info.SlavesHaveFreeWill)
				AssignSlaveActivity(self);
		}

		// --- Spawning ---

		void SpawnReplenishedSlaves(Actor self)
		{
			WPos centerPosition;
			var isInitialSpawn = !hasSpawnedInitialLoad;

			if (!hasSpawnedInitialLoad || !Info.ExitByBudding)
			{
				centerPosition = self.CenterPosition;
			}
			else
			{
				// Bud from an existing alive member
				var existing = slaveEntries.FirstOrDefault(s => s.IsValid && s.Actor.IsInWorld);
				if (existing == null)
					return;

				centerPosition = existing.Actor.CenterPosition;
			}

			foreach (var se in slaveEntries)
				if (se.IsValid && !se.Actor.IsInWorld)
					SpawnIntoWorld(self, se.Actor, centerPosition + Info.Offset);

			// On initial production spawn: override the default MoveTo(spawnCell) activity
			// so slaves move directly to the master's location instead of scattering to
			// individual exit offsets. They will then follow via AssignSlaveActivity.
			if (isInitialSpawn)
			{
				// Use a nested AddFrameEndTask so this runs after SpawnIntoWorld's own
				// AddFrameEndTask has already placed the slaves into the world.
				self.World.AddFrameEndTask(_ =>
					self.World.AddFrameEndTask(w =>
					{
						foreach (var se in slaveEntries)
						{
							if (!se.IsValid || !se.Actor.IsInWorld)
								continue;

							var mv = se.Actor.TraitOrDefault<IMove>();
							if (mv == null)
								continue;

							// Cancel the scatter-to-exit activity, move to master instead
							se.Actor.CancelActivity();
							se.Actor.QueueActivity(mv.MoveTo(self.Location, 2));
						}
					}));
			}
		}



		public override void OnSlaveKilled(Actor self, Actor slave)
		{
			if (Info.AggregateHealth)
			{
				if (!Info.IncludeMasterInAggregate)
				{
					if (slaveEntries.All(m => !m.IsValid))
						self.Dispose();
				}
				else
				{
					aggregateHealthUpdateTicks = 0;
					RefreshMasterHP(self);
				}
			}

			UpdateProtectedCondition(self);

			if (spawnReplaceTicks <= 0)
				spawnReplaceTicks = Info.RespawnTicks;
		}

		// --- APC Transport ---
		//
		// When the master enters a transport, all alive slaves follow immediately.
		// When the master exits, slaves are unloaded and respawned near the exit point.
		// AssignSlaveActivity already skips slaves that are !IsInWorld (inside a transport),
		// so no extra guard is needed during transit.

		void INotifyEnteredCargo.OnEnteredCargo(Actor self, Actor transport)
		{
			var cargo = transport.TraitOrDefault<Cargo>();
			if (cargo == null)
				return;

			// Space check already passed in ResolveOrder — just load all slaves.
			self.World.AddFrameEndTask(w =>
			{
				foreach (var se in slaveEntries)
				{
					if (!se.IsValid || !se.Actor.IsInWorld)
						continue;

					if (!cargo.CanLoad(se.Actor))
						continue;

					se.Actor.CancelActivity();
					w.Remove(se.Actor);
					cargo.Load(transport, se.Actor);
				}
			});
		}

		void INotifyExitedCargo.OnExitedCargo(Actor self, Actor transport)
		{
			var cargo = transport.TraitOrDefault<Cargo>();
			if (cargo == null)
				return;

			// Unload slaves from cargo — UnloadCargo activity will call World.Add() for each.
			// Do NOT call SpawnReplenishedSlaves or World.Add here — that races with UnloadCargo.
			self.World.AddFrameEndTask(w =>
			{
				foreach (var se in slaveEntries)
				{
					if (!se.IsValid || se.Actor.IsInWorld)
						continue;

					var passenger = se.Actor.TraitOrDefault<Passenger>();
					if (passenger == null || passenger.Transport != transport)
						continue;

					cargo.Unload(transport, se.Actor);
				}
			});
		}

		// --- Position aggregation (nexus mode only) ---

		void SetNexusPosition(Actor self)
		{
			int x = 0, y = 0, cnt = 0;

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				var pos = se.Actor.CenterPosition;
				x += pos.X;
				y += pos.Y;
				cnt++;
			}

			if (cnt == 0)
				return;

			var newPos = new WPos(x / cnt, y / cnt, 0);
			position.SetPosition(self, newPos);
		}

		// --- Health display ---
		//
		// Master HP bar = (master.HP + sum of alive slave HP) / totalMaxHP * master.MaxHP
		// where totalMaxHP = master.MaxHP + sum of all slave MaxHP.
		//
		// This reflects the true health pool of the squad — a squad with wounded slaves
		// shows a lower bar than one with full slaves, even at the same member count.
		// Master HP itself is protected (immortal while slaves alive) via INotifyDamage.

		void RefreshMasterHP(Actor self)
		{
			if (aggregateHealthUpdateTicks > 0)
			{
				aggregateHealthUpdateTicks--;
				return;
			}

			aggregateHealthUpdateTicks = Info.AggregateHealthUpdateDelay;

			var aliveSlaves = slaveEntries.Where(s => s.IsValid && s.Actor.IsInWorld).ToArray();

			// Once all slaves are dead, stop overriding — master fights with its own HP.
			if (aliveSlaves.Length == 0)
				return;

			// Total max HP pool: master + alive slaves only.
			// Dead slaves are excluded from both numerator and denominator so that
			// losing a slave reduces the bar proportionally without causing a heal spike.
			var totalMaxHP = health.MaxHP + aliveSlaves.Sum(s => s.Health.MaxHP);

			// Current pool: master own HP + all alive slave HP
			var currentHP = health.HP + aliveSlaves.Sum(s => s.Health.HP);

			// Scale to master HP range for display
			var targetHP = (int)((long)currentHP * health.MaxHP / totalMaxHP);
			targetHP = Math.Max(targetHP, 1); // never show 0 while master is alive

			var delta = health.HP - targetHP;
			if (delta > 0)
			{
				isDamagePropagating = true;
				try
				{
					health.InflictDamage(self, self, new Damage(delta), true);
				}
				finally
				{
					isDamagePropagating = false;
				}
			}
			else if (delta < 0)
			{
				// Heal master display HP upward when slaves are healed or respawned
				isDamagePropagating = true;
				try
				{
					health.InflictDamage(self, self, new Damage(delta), true); // negative = heal
				}
				finally
				{
					isDamagePropagating = false;
				}
			}
		}

		// --- Slave activity assignment ---

		void AssignTargetsToSlaves(Actor self, in Target target)
		{
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid)
					continue;

				se.SpawnerSlave.Attack(se.Actor, target);
			}
		}

		CPos lastAttackMoveLocation;

		void MoveSlaves(Actor self)
		{
			var targets = self.CurrentActivity?.GetTargets(self);
			if (targets == null || !targets.Any())
				return;

			var location = self.World.Map.CellContaining(targets.First().CenterPosition);

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				if (se.Actor.Location == location)
					continue;

				if (!se.SpawnerSlave.IsMoving)
				{
					se.SpawnerSlave.Stop(se.Actor);
					se.SpawnerSlave.Move(se.Actor, location);
				}
			}
		}

		void AttackMoveSlaves(Actor self)
		{
			var targets = self.CurrentActivity?.GetTargets(self);
			if (targets == null || !targets.Any())
				return;

			var location = self.World.Map.CellContaining(targets.First().CenterPosition);

			if (lastAttackMoveLocation == location)
				return;

			lastAttackMoveLocation = location;

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				se.SpawnerSlave.AttackMove(se.Actor, location);
			}
		}

		void AssignSlaveActivity(Actor self)
		{
			var activity = self.CurrentActivity;

			if (activity is Move)
			{
				MoveSlaves(self);
				return;
			}

			if (activity is AttackMoveActivity)
			{
				AttackMoveSlaves(self);
				return;
			}

			// Master is idle — pull any stray slaves back to the master's cell.
			// This handles post-spawn, post-unload, and any other case where slaves
			// are in the world but haven't received a move order yet.
			if (activity == null)
				GatherStraySlaves(self);
		}

		void GatherStraySlaves(Actor self)
		{
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				// Skip slaves that are already at or moving toward the master
				if (se.Actor.Location == self.Location)
					continue;

				if (se.SpawnerSlave.IsMoving)
					continue;

				se.SpawnerSlave.Move(se.Actor, self.Location);
			}
		}

		// --- Trait enable/disable ---

		protected override void TraitEnabled(Actor self)
		{
			if (!Info.EnabledByDefault && !hasSpawnedInitialLoad)
			{
				Replenish(self, slaveEntries);
				SpawnReplenishedSlaves(self);
				hasSpawnedInitialLoad = true;
			}
		}
	}
}
