#region Copyright & License Information
/*
 * Crystallized Nexus - Scatterer Trait
 * Makes units automatically evade perpendicular to an approaching crusher.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("Makes this unit automatically dodge sideways when an enemy crusher is " +
		"approaching and this unit would be in its path.")]
	public class ScattererInfo : ConditionalTraitInfo, Requires<MobileInfo>
	{
		[Desc("How often (in ticks) to scan for incoming crushers.")]
		public readonly int ScanInterval = 10;

		[Desc("Cell radius within which an approaching crusher triggers evasion.")]
		public readonly int ThreatRadius = 4;

		[Desc("How many cells to move sideways when evading.")]
		public readonly int EvadeDistance = 3;

		public override object Create(ActorInitializer init) { return new Scatterer(init.Self, this); }
	}

	public class Scatterer : ConditionalTrait<ScattererInfo>, ITick
	{
		readonly Mobile mobile;
		bool isEvading;
		int scanTicker;

		public Scatterer(Actor self, ScattererInfo info)
			: base(info)
		{
			mobile = self.Trait<Mobile>();
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			// Wait until the evasion move finishes before scanning again
			if (isEvading)
			{
				if (self.IsIdle)
					isEvading = false;
				return;
			}

			if (--scanTicker > 0)
				return;

			scanTicker = Info.ScanInterval;

			var threat = FindApproachingCrusher(self);
			if (threat == null)
				return;

			var evadeCell = ChooseEvadeCell(self, threat);
			if (evadeCell == null)
				return;

			isEvading = true;
			self.QueueActivity(false, mobile.MoveTo(evadeCell.Value, 0));
		}

		/// <summary>
		/// Returns the nearest moving enemy whose CrushClasses can crush us and
		/// whose facing points roughly toward us.
		/// </summary>
		Actor FindApproachingCrusher(Actor self)
		{
			var selfPos = self.CenterPosition;

			return self.World.FindActorsInCircle(selfPos, WDist.FromCells(Info.ThreatRadius))
				.Where(a =>
				{
					if (a == self || a.IsDead || !a.IsInWorld)
						return false;

					// Only enemies
					if (self.Owner.IsAlliedWith(a.Owner))
						return false;

					// Must be a ground mover with crush capability
					var mobInfo = a.Info.TraitInfoOrDefault<MobileInfo>();
					if (mobInfo == null || mobInfo.LocomotorInfo.Crushes.IsEmpty)
						return false;

					// We must be crushable by this unit
					if (!a.Crushables.Any(c => c.CrushableBy(self, a, mobInfo.LocomotorInfo.Crushes)))
						return false;

					// Crusher must be facing toward us (dot product > 0)
					var facing = a.TraitOrDefault<IFacing>();
					if (facing == null)
						return false;

					var angle = facing.Facing;
					var toUs = selfPos - a.CenterPosition;

					// WAngle.Sin/Cos return values scaled by 1024
					var fwdX = angle.Sin();
					var fwdY = -angle.Cos(); // OpenRA Y-axis is flipped
					var dot = fwdX * toUs.X + fwdY * toUs.Y;

					return dot > 0;
				})
				.MinByOrDefault(a => (a.CenterPosition - selfPos).LengthSquared);
		}

		/// <summary>
		/// Picks the best cell perpendicular to the crusher's heading,
		/// preferring the side with more clearance.
		/// </summary>
		CPos? ChooseEvadeCell(Actor self, Actor crusher)
		{
			var angle = crusher.Trait<IFacing>().Facing;

			// Perpendicular directions: ±90°  (256 units = 90° in WAngle)
			var left = new WAngle(angle.Angle - 256);
			var right = new WAngle(angle.Angle + 256);

			var leftCell = StepInDirection(self.Location, left, Info.EvadeDistance);
			var rightCell = StepInDirection(self.Location, right, Info.EvadeDistance);

			var map = self.World.Map;

			var leftOk = leftCell.HasValue && map.Contains(leftCell.Value) && mobile.CanEnterCell(leftCell.Value);
			var rightOk = rightCell.HasValue && map.Contains(rightCell.Value) && mobile.CanEnterCell(rightCell.Value);

			if (leftOk && rightOk)
			{
				// Prefer the side further from the crusher
				var cp = crusher.CenterPosition;
				var leftDist = (map.CenterOfCell(leftCell.Value) - cp).LengthSquared;
				var rightDist = (map.CenterOfCell(rightCell.Value) - cp).LengthSquared;
				return leftDist >= rightDist ? leftCell : rightCell;
			}

			if (leftOk) return leftCell;
			if (rightOk) return rightCell;
			return null;
		}

		/// <summary>
		/// Returns a CPos <paramref name="cells"/> steps from <paramref name="origin"/>
		/// in the given <paramref name="angle"/> direction.
		/// </summary>
		static CPos? StepInDirection(CPos origin, WAngle angle, int cells)
		{
			// Sin/Cos are ×1024; integer-divide to get cell offsets
			var dx = angle.Sin() * cells / 1024;
			var dy = -angle.Cos() * cells / 1024;
			var result = new CPos(origin.X + dx, origin.Y + dy);
			return result == origin ? null : result;
		}
	}
}
