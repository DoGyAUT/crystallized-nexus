#region Copyright & License Information
/*
 * Originally written by Boolbada of OP Mod.
 * Ported to Crystallized Nexus by CN contributors.
 * Follows GPLv3 License as the OpenRA engine.
 */
#endregion

using System;
using System.Linq;
using OpenRA;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
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
	// Runtime-only init: passes live Actor[] through TypeDictionary when spawning a replacement master.
	// ISuppressInitExport prevents ActorReference.Save() from ever calling Save() on this.
	// ISingleInstanceInit enables the parameter-free init.GetOrDefault<T>() overload.
	public class MobSpawnerAdoptionInit : ValueActorInit<Actor[]>, ISuppressInitExport, ISingleInstanceInit
	{
		public MobSpawnerAdoptionInit(Actor[] value) : base(value) { }
	}

	[Desc("Manages a group of slave actors that form a mob. The master acts as a virtual nexus.")]
	public class MobSpawnerMasterInfo : BaseSpawnerMasterInfo
	{
		[Desc("If true, new slaves exit (bud) from an existing member instead of the nexus spawn point.")]
		public readonly bool ExitByBudding = true;

		[Desc("If true, slaves can act independently (no forced movement/targeting from master).")]
		public readonly bool SlavesHaveFreeWill = false;

		[Desc("If true, killed slaves are not replaced after the initial spawn.")]
		public readonly bool DisableRespawning = false;

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

		[Desc("If true, XP earned by any slave is redirected to the master. " +
			"Newly spawned slaves inherit the master's current experience so the whole squad levels together.")]
		public readonly bool ShareSlaveExperience = false;

		[Desc("If true, when the master is killed with surviving slaves, spawn a replacement master " +
			"at a survivor's position and re-link survivors to it instead of killing the whole squad. " +
			"Only meaningful with IncludeMasterInAggregate: true. " +
			"Set to false for 1-master+1-slave pairs like Sniper/Spotter.")]
		public readonly bool TransferMasterOnKill = false;

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (InitialActorCount == 0 && AggregateHealth)
				throw new YamlException($"MobSpawnerMaster on {ai.Name}: InitialActorCount cannot be 0 when AggregateHealth is true.");
		}

		public override object Create(ActorInitializer init) { return new MobSpawnerMaster(init, this); }
	}

	public class MobSpawnerMaster :
		BaseSpawnerMaster,
		INotifyCreated,
		INotifyOwnerChanged,
		ITick,
		IResolveOrder,
		INotifyAttack,
		IDamageModifier,
		INotifyDamage,
		INotifyEnteredCargo,
		INotifyExitedCargo,
		INotifySelected
	{
		class MobSpawnerSlaveEntry : BaseSpawnerSlaveEntry
		{
			public new MobSpawnerSlave SpawnerSlave;
			public IHealth Health;
			public GainsExperience GainsExperience;
			public int LastTrackedExperience;
			public CPos LastMoveDestination;
		}

		public new MobSpawnerMasterInfo Info { get; }

		MobSpawnerSlaveEntry[] slaveEntries;

		bool hasSpawnedInitialLoad = false;
		int spawnReplaceTicks = 0;
		int aggregateHealthUpdateTicks = 0;
		bool isDamagePropagating = false;
		int protectedBySlavesToken = Actor.InvalidConditionToken;

		// Stores the pre-modifier damage value set by IDamageModifier.GetDamageModifier so that
		// INotifyDamage.Damaged can redirect the original amount even though e.Damage.Value == 0
		// when IDamageModifier returns 0%.
		int pendingRedirectDamage = 0;

		// Non-null when this master was created to replace a dead master (TransferMasterOnKill).
		// Set in the constructor so SkipInitialSpawn is true before Created() runs.
		readonly Actor[] slavesToAdopt;

		IPositionable position;
		IHealth health;
		GainsExperience masterXP;

		public MobSpawnerMaster(ActorInitializer init, MobSpawnerMasterInfo info)
			: base(init, info)
		{
			Info = info;

			var adoptionInit = init.GetOrDefault<MobSpawnerAdoptionInit>();
			if (adoptionInit != null)
			{
				slavesToAdopt = adoptionInit.Value;
				SkipInitialSpawn = true;
			}
		}

		protected override void Created(Actor self)
		{
			position = self.TraitOrDefault<IPositionable>();
			health = self.Trait<IHealth>();

			// Cache before base.Created, which calls Replenish → InitializeSlaveEntry.
			if (Info.ShareSlaveExperience)
				masterXP = self.TraitOrDefault<GainsExperience>();

			// Stagger HP refresh across squads so they don't all call InflictDamage on the same tick.
			aggregateHealthUpdateTicks = self.World.SharedRandom.Next(0, Info.AggregateHealthUpdateDelay);

			base.Created(self); // Spawns initial slaves (not yet in world)

			if (!IsTraitDisabled)
			{
				if (slavesToAdopt != null)
					AdoptExistingSlaves(self, slavesToAdopt);
				else
				{
					SpawnReplenishedSlaves(self);
					hasSpawnedInitialLoad = true;
				}
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
			for (var i = 0; i < slaveEntries.Length; i++)
				slaveEntries[i] = new MobSpawnerSlaveEntry();

			return slaveEntries;
		}

		public override void InitializeSlaveEntry(Actor slave, BaseSpawnerSlaveEntry entry)
		{
			var se = entry as MobSpawnerSlaveEntry;
			base.InitializeSlaveEntry(slave, se);

			se.SpawnerSlave = slave.Trait<MobSpawnerSlave>();
			se.Health = slave.Trait<IHealth>();

			if (Info.ShareSlaveExperience)
			{
				se.GainsExperience = slave.TraitOrDefault<GainsExperience>();

				// Newly spawned slave inherits the squad's current rank so it doesn't
				// start at rank 0 when the rest of the squad has already levelled up.
				if (se.GainsExperience != null && masterXP != null && masterXP.Level > 0)
				{
					se.GainsExperience.GiveLevels(masterXP.Level, true);
					se.LastTrackedExperience = se.GainsExperience.Experience;
				}
			}
		}

		// --- IResolveOrder ---
		public void ResolveOrder(Actor self, Order order)
		{
			if (Info.SlavesHaveFreeWill)
				return;

			if (order.OrderString == "Stop")
				StopSlaves();

			if ((order.OrderString == "Attack" || order.OrderString == "ForceAttack") &&
				order.Target.Type != TargetType.Invalid)
				AssignTargetsToSlaves(self, order.Target, forceAttack: order.OrderString == "ForceAttack");

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

				var hasRoom = cargo.HasSpace(totalRequired);
				if (!hasRoom)
				{
					self.CancelActivity();
				}
			}
		}

		// --- INotifyAttack ---
		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (Info.SlavesHaveFreeWill)
				return;

			// If the master fired, slaves follow with forceAttack so they can hit the same target
			// regardless of stance or target type (e.g. ground attack, allied/neutral actors).
			AssignTargetsToSlaves(self, target, forceAttack: true);
		}

		// --- IDamageModifier + INotifyDamage ---
		// When slaves are alive, block all incoming damage on the master (return 0%) and store the
		// original pre-modifier amount in pendingRedirectDamage. INotifyDamage then reads that
		// stored value and redirects it to ONE random slave. This prevents the undo-trick's fatal
		// flaw: if a hit exceeded the master's HP the engine would set IsDead before the undo could
		// run, triggering OnMasterKilled → KillSlaves and wiping the whole squad.
		int IDamageModifier.GetDamageModifier(Actor attacker, Damage damage)
		{
			// Pass-through during internal HP adjustments (RefreshMasterHP sets isDamagePropagating)
			// or when no slaves are alive.
			if (isDamagePropagating || !Info.AggregateHealth)
				return 100;

			var anyAlive = slaveEntries.Any(s => s.IsValid && s.Actor.IsInWorld && !s.Health.IsDead);
			if (!anyAlive)
				return 100;

			// Store original damage for INotifyDamage and suppress the hit on the master entirely.
			pendingRedirectDamage = damage.Value;
			return 0;
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (isDamagePropagating)
				return;

			if (!Info.AggregateHealth)
				return;

			// Ignore self-inflicted HP adjustments from RefreshMasterHP.
			if (e.Attacker == self)
				return;

			// Use the pre-modifier value stored by GetDamageModifier (e.Damage.Value is 0 when
			// the modifier returned 0%).
			var damageValue = pendingRedirectDamage;
			pendingRedirectDamage = 0;

			if (damageValue <= 0)
				return;

			var aliveSlaves = slaveEntries.Where(s => s.IsValid && s.Actor.IsInWorld && !s.Health.IsDead).ToArray();
			if (aliveSlaves.Length == 0)
				return;

			isDamagePropagating = true;
			try
			{
				// Pick one random slave to receive the hit.
				// Overkill is absorbed by the engine — only this one slave can die per hit.
				var target = aliveSlaves.Random(self.World.SharedRandom);
				target.Health.InflictDamage(target.Actor, e.Attacker ?? self, new Damage(damageValue, e.Damage.DamageTypes), false);
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
			if (spawnReplaceTicks > 0 && !IsTraitDisabled && !Info.DisableRespawning)
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

			// Redirect XP earned by any slave to the master.
			if (Info.ShareSlaveExperience && masterXP != null)
				RedirectSlaveExperience();
		}

		void RedirectSlaveExperience()
		{
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld || se.GainsExperience == null)
					continue;

				var gained = se.GainsExperience.Experience - se.LastTrackedExperience;
				if (gained > 0)
					masterXP.GiveExperience(gained);
			}

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld || se.GainsExperience == null)
					continue;

				if (se.GainsExperience.Level < masterXP.Level)
					se.GainsExperience.GiveLevels(masterXP.Level - se.GainsExperience.Level, true);

				se.LastTrackedExperience = se.GainsExperience.Experience;
			}
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
				centerPosition = existing != null ? existing.Actor.CenterPosition : self.CenterPosition;
			}

			foreach (var se in slaveEntries)
				if (se.IsValid && !se.Actor.IsInWorld)
					SpawnIntoWorld(self, se.Actor, centerPosition + Info.Offset);

			// On initial production spawn: override the default MoveTo(spawnCell) activity
			// so slaves move directly to the master's location instead of scattering to
			// individual exit offsets. They will then follow via AssignSlaveActivity.
			if (isInitialSpawn)
			{
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

							se.Actor.CancelActivity();
							se.Actor.QueueActivity(mv.MoveTo(self.Location, 2));
						}
					}));
			}
		}

		public void RestoreSquad(Actor self, Actor repairer)
		{
			if (IsTraitDisabled)
				return;

			var restoredSlaves = false;
			foreach (var se in slaveEntries)
			{
				if (se.IsValid)
					continue;

				Replenish(self, se);
				restoredSlaves = true;
			}

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid)
					continue;

				HealToFull(se.Actor, se.Health, repairer);
			}

			HealToFull(self, health, repairer);
			spawnReplaceTicks = 0;

			var passenger = self.TraitOrDefault<Passenger>();
			if (restoredSlaves && passenger?.Transport == null)
				SpawnReplenishedSlaves(self);
		}

		static void HealToFull(Actor target, IHealth targetHealth, Actor repairer)
		{
			var hpToRepair = targetHealth.MaxHP - targetHealth.HP;
			if (hpToRepair > 0)
				targetHealth.InflictDamage(target, repairer, new Damage(-hpToRepair), false);
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

			if (!Info.DisableRespawning && spawnReplaceTicks <= 0)
				spawnReplaceTicks = Info.RespawnTicks;
		}

		// --- Master transfer on kill ---
		void AdoptExistingSlaves(Actor self, Actor[] slaves)
		{
			for (var i = 0; i < slaveEntries.Length && i < slaves.Length; i++)
			{
				var slave = slaves[i];
				if (slave.IsDead || !slave.IsInWorld)
					continue;

				// InitializeSlaveEntry populates both BaseSpawnerSlaveEntry.SpawnerSlave and
				// MobSpawnerSlaveEntry.SpawnerSlave. Setting only the derived field (as before)
				// left the base field null, causing a NullReferenceException in StopSlaves().
				InitializeSlaveEntry(slave, slaveEntries[i]);

				// Adopted slaves carry pre-existing XP; baseline from the current level
				// so RedirectSlaveExperience does not credit it to the new master.
				if (Info.ShareSlaveExperience && slaveEntries[i].GainsExperience != null)
					slaveEntries[i].LastTrackedExperience = slaveEntries[i].GainsExperience.Experience;

				slaveEntries[i].SpawnerSlave.LinkMaster(slave, self, this);
			}

			hasSpawnedInitialLoad = true;
		}

		public override void Killed(Actor self, AttackInfo e)
		{
			if (!Info.TransferMasterOnKill)
			{
				base.Killed(self, e);
				return;
			}

			var survivors = slaveEntries
				.Where(s => s.IsValid && s.Actor.IsInWorld)
				.Select(s => s.Actor)
				.ToArray();

			if (survivors.Length == 0)
			{
				base.Killed(self, e);
				return;
			}

			// Null out entries for survivors so INotifyActorDisposing.Disposing (BaseSpawnerMaster)
			// does not call Dispose() on them when the old master is cleaned up.
			foreach (var se in slaveEntries)
				if (se.Actor != null && survivors.Contains(se.Actor))
					se.Actor = null;

			// Do NOT call base.Killed() — survivors are not killed.
			var owner = self.Owner;
			var masterType = self.Info.Name;

			self.World.AddFrameEndTask(w =>
			{
				var alive = survivors.Where(a => !a.IsDead && a.IsInWorld).ToArray();
				if (alive.Length == 0)
					return;

				var spawnCell = w.Map.CellContaining(alive[0].CenterPosition);
				var td = new TypeDictionary
				{
					new OwnerInit(owner),
					new LocationInit(spawnCell),
					new MobSpawnerAdoptionInit(alive)
				};

				// CreateActor synchronously triggers Created() → AdoptExistingSlaves() → LinkMaster().
				// SkipInitialSpawn=true prevents Replenish from spawning new slaves.
				w.CreateActor(masterType, td);
			});
		}

		// --- APC Transport ---
		void INotifyEnteredCargo.OnEnteredCargo(Actor self, Actor transport)
		{
			var cargo = transport.TraitOrDefault<Cargo>();
			if (cargo == null)
				return;

			self.World.AddFrameEndTask(w =>
			{
				foreach (var se in slaveEntries)
				{
					if (!se.IsValid)
						continue;

					if (se.Actor.TraitOrDefault<Passenger>()?.Transport == transport)
						continue;

					if (!cargo.CanLoad(se.Actor))
						continue;

					se.Actor.CancelActivity();
					if (se.Actor.IsInWorld)
						w.Remove(se.Actor);

					cargo.Load(transport, se.Actor);
				}
			});
		}

		void INotifyExitedCargo.OnExitedCargo(Actor self, Actor transport)
		{
			// Slaves are loaded into the transport individually (via OnEnteredCargo), so the
			// Cargo activity ejects them on its own — no manual unload needed here.
			// When the transport is killed, Cargo.Killed handles all passengers directly;
			// calling cargo.Unload here too would add each slave to World twice → ArgumentException.
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
		void RefreshMasterHP(Actor self)
		{
			if (aggregateHealthUpdateTicks > 0)
			{
				aggregateHealthUpdateTicks--;
				return;
			}

			aggregateHealthUpdateTicks = Info.AggregateHealthUpdateDelay;

			var spawnedSlaves = slaveEntries.Where(s => s.Actor != null).ToArray();

			if (spawnedSlaves.Length == 0)
				return;

			var livingSlaves = spawnedSlaves.Where(s => !s.Health.IsDead).ToArray();
			var totalMaxHP = health.MaxHP + spawnedSlaves.Sum(s => s.Health.MaxHP);
			var currentHP = health.HP + livingSlaves.Sum(s => s.Health.HP);

			var targetHP = (int)((long)currentHP * health.MaxHP / totalMaxHP);
			targetHP = Math.Max(targetHP, 1);

			var delta = health.HP - targetHP;
			if (delta != 0)
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
		}

		// --- Slave activity assignment ---
		void AssignTargetsToSlaves(Actor _, in Target target, bool forceAttack = false)
		{
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid)
					continue;

				se.SpawnerSlave.Attack(se.Actor, target, forceAttack);
			}
		}

		CPos lastAttackMoveLocation;

		void MoveSlaves(Actor self)
		{
			var targets = self.CurrentActivity?.GetTargets(self);
			if (targets == null || !targets.Any())
				return;

			var location = GetSlaveMoveLocation(self, targets.First());

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				if (se.Actor.Location == location)
					continue;

				if (!se.SpawnerSlave.IsMoving || se.LastMoveDestination != location)
				{
					se.SpawnerSlave.Stop(se.Actor);
					se.SpawnerSlave.Move(se.Actor, location);
					se.LastMoveDestination = location;
				}
			}
		}

		void AttackMoveSlaves(Actor self)
		{
			var targets = self.CurrentActivity?.GetTargets(self);
			if (targets == null || !targets.Any())
				return;

			var location = GetSlaveMoveLocation(self, targets.First());

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

			if (activity == null)
				GatherStraySlaves(self);
		}

		CPos GetSlaveMoveLocation(Actor self, in Target target)
		{
			var mobile = self.TraitOrDefault<Mobile>();
			if (mobile != null)
			{
				if (mobile.ToCell.Layer != 0)
					return mobile.ToCell;

				if (mobile.FromCell.Layer != 0)
					return mobile.FromCell;
			}

			return self.World.Map.CellContaining(target.CenterPosition);
		}

		void GatherStraySlaves(Actor self)
		{
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				if (se.Actor.Location == self.Location)
					continue;

				if (se.SpawnerSlave.IsMoving)
					continue;

				se.SpawnerSlave.Move(se.Actor, self.Location);
				se.LastMoveDestination = self.Location;
			}
		}

		// --- Selection expansion ---
		void INotifySelected.Selected(Actor self)
		{
			// When any squad member is selected, add the remaining alive slaves so the
			// full squad appears in the selection UI regardless of box-select position.
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				if (!self.World.Selection.Contains(se.Actor))
					self.World.Selection.Add(se.Actor);
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
