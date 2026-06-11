#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.CN.Traits.Movement;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Activities
{
	/// <summary>
	/// <para>Wrapper activity created by <see cref="CNSteeredMobile"/>.</para>
	/// <para>
	/// For turns beyond SteeringAngle:
	///   1. Compute an arc path: ForwardCommitCells straight + SteeringCone curved cells.
	///   2. Hand the entire arc as one Move so IsTurn fires between consecutive cells.
	///   3. Queue the original moveInner after the arc completes.
	/// </para>
	/// <para>
	/// The lambda-based Move constructor lets us return a fresh path copy on re-path
	/// while EvalPath's TakeWhile(mobile.ToCell) correctly prunes already-visited cells.
	/// </para>
	/// </summary>
	public class CNSteeredMoveActivity : Activity
	{
		readonly Activity moveInner;
		readonly CNSteeredMobileInfo info;
		readonly Mobile mobile;

		public CNSteeredMoveActivity(Actor self, Activity moveInner, CNSteeredMobileInfo info)
		{
			this.moveInner = moveInner;
			this.info = info;
			mobile = (Mobile)self.OccupiesSpace;
		}

		protected override void OnFirstRun(Actor self)
		{
			// Get the rough destination from the wrapped activity (available before pathfinding).
			var target = moveInner.GetTargets(self).LastOrDefault();
			if (target.Type == TargetType.Invalid)
			{
				QueueChild(moveInner);
				return;
			}

			var map = self.World.Map;
			var destCell = map.CellContaining(target.CenterPosition);

			if (destCell == mobile.ToCell)
			{
				QueueChild(moveInner);
				return;
			}

			// Absolute angle between current facing and destination direction.
			var desiredFacing = map.FacingBetween(mobile.ToCell, destCell, mobile.Facing);
			var rawDelta = (desiredFacing - mobile.Facing).Angle;
			var absDelta = rawDelta > 512 ? 1024 - rawDelta : rawDelta;

			// Within threshold: the engine's own arc movement handles this without a commit.
			if (absDelta <= info.SteeringAngle.Angle)
			{
				QueueChild(moveInner);
				return;
			}

			// Skip the arc for very close targets - the arc would overshoot the destination.
			var arcLength = info.ForwardCommitCells + info.SteeringCone;
			if ((destCell - mobile.ToCell).LengthSquared <= arcLength * arcLength)
			{
				QueueChild(moveInner);
				return;
			}

			// Build the arc waypoint list and hand it to a single Move as a custom path.
			var arcCells = BuildArcPath(map, rawDelta, absDelta, destCell);
			if (arcCells == null || arcCells.Count == 0)
			{
				QueueChild(moveInner);
				return;
			}

			// Move processes path from the end (next step = last element), so reverse travel order.
			var reversedArc = new List<CPos>(arcCells);
			reversedArc.Reverse();

			// The lambda returns a fresh copy each re-path call;
			// EvalPath's TakeWhile(mobile.ToCell) prunes already-visited cells automatically.
			QueueChild(new Move(self, _ => (false, new List<CPos>(reversedArc))));
			QueueChild(moveInner);
		}

		// Default Tick() returns true - activity completes when all queued children finish.

		/// <summary>
		/// Builds the arc waypoint list in travel order (immediate next cell first).
		/// ForwardCommitCells cells go straight, then SteeringCone cells curve toward
		/// the target facing by distributing the total turn evenly across the arc steps.
		/// </summary>
		List<CPos> BuildArcPath(Map map, int rawDelta, int absDelta, CPos destCell)
		{
			var cells = new List<CPos>();
			var current = mobile.ToCell;
			var clockwise = rawDelta <= 512;

			var totalSteps = info.ForwardCommitCells + info.SteeringCone;
			for (var i = 0; i < totalSteps; i++)
			{
				WAngle stepFacing;
				if (i < info.ForwardCommitCells)
				{
					// Straight ahead: keep current heading.
					stepFacing = mobile.Facing;
				}
				else
				{
					// Arc: linearly interpolate toward desiredFacing.
					var steeringIdx = i - info.ForwardCommitCells + 1;
					var turnAmount = absDelta * steeringIdx / info.SteeringCone;
					stepFacing = clockwise
						? mobile.Facing + new WAngle(turnAmount)
						: mobile.Facing - new WAngle(turnAmount);
				}

				// Pick the adjacent cell whose bearing is closest to stepFacing.
				var best = current;
				var bestDelta = 1024;
				foreach (var dir in CVec.Directions)
				{
					var candidate = current + dir;
					if (!map.Contains(candidate) || !mobile.CanEnterCell(candidate))
						continue;

					var candidateFacing = map.FacingBetween(current, candidate, stepFacing);
					var d = (candidateFacing - stepFacing).Angle;
					if (d > 512)
						d = 1024 - d;

					if (d < bestDelta)
					{
						bestDelta = d;
						best = candidate;
					}
				}

				if (best == current)
					break; // All directions blocked - use whatever arc we have so far.

				cells.Add(best);

				if (best == destCell)
					break; // Arc naturally reached the destination.

				current = best;
			}

			return cells.Count > 0 ? cells : null;
		}

		public override IEnumerable<Target> GetTargets(Actor self) =>
			moveInner.GetTargets(self);

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self) =>
			moveInner.TargetLineNodes(self);
	}
}
