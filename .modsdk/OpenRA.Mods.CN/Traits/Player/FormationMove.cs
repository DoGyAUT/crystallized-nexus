#region Copyright & License Information
/*
 * Crystallized Nexus - FormationMove
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CN.Traits
{
	[Desc("When multiple units with this trait are ordered to move simultaneously, " +
		"they arrange into a directional grid formation around the target cell. " +
		"On large direction changes (> 90°) the most-advanced units in the new " +
		"direction are assigned to front slots, flipping the formation automatically.")]
	public class FormationMoveInfo : TraitInfo
	{
		[Desc("Number of columns in the formation grid. 0 = auto (ceil of square root of group size).")]
		public readonly int Columns = 0;

		[Desc("Cell spacing between units in the grid.")]
		public readonly int Spacing = 1;

		[Desc("When true, the front row is placed at the target cell and subsequent rows trail " +
			"behind relative to the direction of movement (AoE2 style). " +
			"When false, the formation is centred on the target cell.")]
		public readonly bool FrontAtTarget = true;

		[Desc("Order names this trait applies to.")]
		public readonly HashSet<string> ValidOrders = ["Move", "AttackMove"];

		public override object Create(ActorInitializer init) { return new FormationMove(init.Self, this); }
	}

	public class FormationMove : IResolveOrder
	{
		readonly FormationMoveInfo info;

		// Set before issuing our own formation order so ResolveOrder skips it on return,
		// preventing infinite recursion while allowing Mobile to draw the path indicator.
		bool pendingRedirect = false;

		// Persistent slot index (-1 = unassigned). Retained across commands so units
		// keep their position in the formation on small direction changes.
		public int FormationSlot = -1;

		// Last formation direction in cell space. Used to detect turns > 90° so
		// the formation can be flipped rather than shuffled.
		int lastFwdCX = 0;
		int lastFwdCY = 0;

		public FormationMove(Actor _, FormationMoveInfo info)
		{
			this.info = info;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (!info.ValidOrders.Contains(order.OrderString)) return;
			if (!order.Target.IsValidFor(self)) return;

			// Our own formation redirect arriving back — skip formation logic,
			// let Mobile process it normally so the path indicator is drawn.
			if (pendingRedirect) { pendingRedirect = false; return; }

			var targetCell = self.World.Map.CellContaining(order.Target.CenterPosition);

			// Gather all living, mobile, formation-capable friendly actors in the selection.
			var group = self.World.Selection.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Owner == self.Owner
					&& a.TraitOrDefault<FormationMove>() != null
					&& a.TraitOrDefault<Mobile>() != null)
				.ToList();

			if (group.Count <= 1) return;

			// Sort deterministically — every actor in the group computes the same assignment.
			group.Sort((a, b) => a.ActorID.CompareTo(b.ActorID));

			var myIdx = group.IndexOf(self);
			if (myIdx < 0) return;

			// --- Direction (cell space) ---
			var sumCX = 0; var sumCY = 0;
			foreach (var a in group)
			{
				var c = self.World.Map.CellContaining(a.CenterPosition);
				sumCX += c.X; sumCY += c.Y;
			}

			var gcx = (double)sumCX / group.Count;
			var gcy = (double)sumCY / group.Count;

			var dcx = targetCell.X - gcx;
			var dcy = targetCell.Y - gcy;
			var cellLen = Math.Sqrt(dcx * dcx + dcy * dcy);

			// Snap to nearest of 8 cell-space facings; result is always in {-1, 0, 1}.
			int fwdCX, fwdCY;
			if (cellLen < 0.5)
			{
				fwdCX = 0; fwdCY = 1;
			}
			else
			{
				var rawAngle = Math.Atan2(dcy, dcx);
				var octant = (int)Math.Round(rawAngle * 4.0 / Math.PI);
				var snapped = octant * Math.PI / 4.0;
				fwdCX = (int)Math.Round(Math.Cos(snapped), MidpointRounding.AwayFromZero);
				fwdCY = (int)Math.Round(Math.Sin(snapped), MidpointRounding.AwayFromZero);
			}

			// 90° CW in cell space (Y-down): right = (-fwd.Y, fwd.X)
			var rgtCX = -fwdCY;
			var rgtCY = fwdCX;

			// Detect turns > 90°: suppress slot preference and use projection assignment
			// so the most-advanced units in the new direction automatically become the front row.
			var dot = fwdCX * lastFwdCX + fwdCY * lastFwdCY;
			var bigTurn = (lastFwdCX != 0 || lastFwdCY != 0) && dot < 0;

			// --- Raw slot positions ---
			var cols = info.Columns > 0
				? info.Columns
				: Math.Max(2, (int)Math.Ceiling(Math.Sqrt(group.Count)));
			var totalRows = (group.Count + cols - 1) / cols;
			var halfRows = info.FrontAtTarget ? 0.0 : (totalRows - 1) / 2.0;

			var rawSlots = new CPos[group.Count];
			for (var slot = 0; slot < group.Count; slot++)
			{
				var col = slot % cols;
				var row = slot / cols;
				var colsInRow = (row == totalRows - 1) ? (group.Count - row * cols) : cols;
				var halfColsInRow = (colsInRow - 1) / 2.0;
				var colOff = col - halfColsInRow;
				var rowOff = row - halfRows;

				var wx = (int)Math.Round(rgtCX * colOff * info.Spacing - fwdCX * rowOff * info.Spacing,
					MidpointRounding.AwayFromZero);
				var wy = (int)Math.Round(rgtCY * colOff * info.Spacing - fwdCY * rowOff * info.Spacing,
					MidpointRounding.AwayFromZero);
				rawSlots[slot] = targetCell + new CVec(wx, wy);
			}

			// --- Slot assignment ---
			var actorToSlot = new int[group.Count];

			if (bigTurn)
			{
				// Sort units by forward projection DESC (most advanced → front row).
				// Tiebreak by ActorID for determinism.
				var sortedByFwd = group
					.Select((a, ai) =>
					{
						var c = self.World.Map.CellContaining(a.CenterPosition);
						return (ai,
							fwdProj: fwdCX * c.X + fwdCY * c.Y,
							latProj: rgtCX * c.X + rgtCY * c.Y);
					})
					.OrderByDescending(x => x.fwdProj)
					.ThenBy(x => x.ai)
					.ToList();

				// Assign row-by-row: within each row sort by lateral projection separately.
				// This prevents units from crossing sideways — a unit that is on the right
				// stays on the right within its assigned row, regardless of row boundaries.
				var slotCursor = 0;
				for (var row = 0; row < totalRows; row++)
				{
					var colsInRow = (row == totalRows - 1) ? (group.Count - row * cols) : cols;
					var rowUnits = sortedByFwd
						.Skip(row * cols).Take(colsInRow)
						.OrderBy(x => x.latProj).ThenBy(x => x.ai)
						.ToList();
					for (var col = 0; col < colsInRow; col++)
						actorToSlot[rowUnits[col].ai] = slotCursor + col;
					slotCursor += colsInRow;
				}
			}
			else
			{
				// Greedy nearest-slot with slot-preference: units prefer their last slot
				// so the formation stays stable across consecutive small-turn commands.
				var pairs = new List<(int ActorIdx, int SlotIdx, long DistSq)>(group.Count * group.Count);
				for (var ai = 0; ai < group.Count; ai++)
					for (var si = 0; si < rawSlots.Length; si++)
					{
						var diff = group[ai].CenterPosition - self.World.Map.CenterOfCell(rawSlots[si]);
						pairs.Add((ai, si, diff.LengthSquared));
					}

				pairs.Sort((a, b) =>
				{
					var fmA = group[a.ActorIdx].TraitOrDefault<FormationMove>();
					var fmB = group[b.ActorIdx].TraitOrDefault<FormationMove>();
					var prefA = (fmA != null && fmA.FormationSlot == a.SlotIdx) ? 0 : 1;
					var prefB = (fmB != null && fmB.FormationSlot == b.SlotIdx) ? 0 : 1;
					if (prefA != prefB) return prefA.CompareTo(prefB);
					var cmp = a.DistSq.CompareTo(b.DistSq);
					if (cmp != 0) return cmp;
					cmp = a.ActorIdx.CompareTo(b.ActorIdx);
					return cmp != 0 ? cmp : a.SlotIdx.CompareTo(b.SlotIdx);
				});

				var usedActors = new bool[group.Count];
				var usedSlots = new bool[rawSlots.Length];
				var assigned = 0;
				foreach (var (ai, si, _) in pairs)
				{
					if (usedActors[ai] || usedSlots[si]) continue;
					actorToSlot[ai] = si;
					usedActors[ai] = true;
					usedSlots[si] = true;
					if (++assigned == group.Count) break;
				}
			}

			// --- Validate this actor's slot ---
			var myMobile = self.Trait<Mobile>();
			var myCell = rawSlots[actorToSlot[myIdx]];

			if (!IsPassable(self.World, myCell, myMobile))
			{
				var reserved = new HashSet<CPos>();
				for (var ai = 0; ai < group.Count; ai++)
					if (ai != myIdx) reserved.Add(rawSlots[actorToSlot[ai]]);

				var found = false;
				for (var r = 1; r <= 5 && !found; r++)
					foreach (var c in self.World.Map.FindTilesInAnnulus(myCell, r, r))
						if (IsPassable(self.World, c, myMobile) && !reserved.Contains(c))
						{ myCell = c; found = true; break; }

				if (!found) myCell = targetCell;
			}

			// Persist slot and direction.
			FormationSlot = actorToSlot[myIdx];
			lastFwdCX = fwdCX;
			lastFwdCY = fwdCY;

			if (myCell == targetCell) return;

			// Re-issue a formation order for self via the order system so:
			// (a) Mobile draws the green path indicator to the correct formation cell, and
			// (b) no double-queue stop-restart occurs (QueueActivity + IssueOrder for the
			//     same destination was what caused units to briefly stop mid-movement).
			// The 1-frame convergence on the click point is imperceptible at unit speeds
			// (~0.07 cells at 2 cells/s, 30 fps) and far less disruptive than the stop.
			pendingRedirect = true;
			self.World.IssueOrder(new Order(order.OrderString, self,
				Target.FromCell(self.World, myCell), order.Queued));
		}

		static bool IsPassable(World world, CPos cell, Mobile mobile)
		{
			return world.Map.Contains(cell) && mobile.CanEnterCell(cell);
		}
	}
}
