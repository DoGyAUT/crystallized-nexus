#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Drives the build-up of a structure placed by a Builder unit: ramps HP from 1 to max ",
		"over the actor's BuildableInfo.BuildDuration while granting a condition (use it to disable ",
		"the structure's traits, e.g. build-incomplete). Returns the builder to the map on completion ",
		"or death. Does nothing if the actor was not created by a Builder.")]
	public class UnderConstructionInfo : TraitInfo, Requires<IHealthInfo>
	{
		[GrantedConditionReference]
		[FieldLoader.Require]
		[Desc("Condition granted while the structure is under construction.")]
		public readonly string Condition = null;

		public override object Create(ActorInitializer init) => new UnderConstruction(init, this);
	}

	public class UnderConstruction : INotifyCreated, ITick, INotifyKilled, INotifyAppliedDamage
	{
		readonly UnderConstructionInfo info;
		readonly Actor builder;

		IHealth health;
		int conditionToken = Actor.InvalidConditionToken;
		int duration;
		int elapsed;
		bool active;
		bool completed;
		int takenDmg;

		/// <summary>True while the structure is building up (granted condition active).</summary>
		public bool IsConstructing => active && !completed;

		/// <summary>Construction progress in the range 0..1.</summary>
		public float Progress => duration <= 0 ? 1f : (elapsed / (float)duration).Clamp(0f, 1f);

		public UnderConstruction(ActorInitializer init, UnderConstructionInfo info)
		{
			this.info = info;
			builder = init.GetOrDefault<BuilderInit>()?.Value;
		}

		void INotifyCreated.Created(Actor self)
		{
			// Not built by a Builder: behave like a normal, fully-built structure.
			if (builder == null)
			{
				completed = true;
				return;
			}

			health = self.Trait<IHealth>();

			var bi = self.Info.TraitInfoOrDefault<BuildableInfo>();
			var baseTime = bi != null && bi.BuildDuration >= 0 ? bi.BuildDuration : GetCost(self);
			var modifier = bi != null ? bi.BuildDurationModifier : 100;
			duration = Util.ApplyPercentageModifiers(baseTime, [modifier]);

			active = true;
			conditionToken = self.GrantCondition(info.Condition);
		}

		void ITick.Tick(Actor self)
		{
			if (!active || completed)
				return;

			elapsed++;

			// Ramp HP towards the construction target, but never overwrite incoming damage
			// (so the structure can still be killed mid-build).
			var target = 1 + (int)((health.MaxHP - 1L - takenDmg) / duration);
			if (target > health.HP && !health.IsDead)
				health.InflictDamage(self, self, new Damage(-target), true);

			if (elapsed >= duration)
				Complete(self);
		}

		void Complete(Actor self)
		{
			if (completed)
				return;

			completed = true;
			active = false;

			if (conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);

			ReturnBuilder(self);
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (completed)
				return;

			completed = true;
			active = false;
			ReturnBuilder(self);
		}

		void ReturnBuilder(Actor self)
		{
			if (builder == null)
				return;

			self.Owner.PlayerActor.Trait<PlayerBuilders>().ReturnBuilder(builder, self.Location);
		}

		static int GetCost(Actor self)
		{
			return self.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
		}

		public void AppliedDamage(Actor self, Actor damaged, AttackInfo e)
		{
			takenDmg += e.Damage.Value;
		}
	}

	public class BuilderInit : ValueActorInit<Actor>, ISuppressInitExport, ISingleInstanceInit
	{
		public BuilderInit(Actor value)
			: base(value) { }
	}
}
