#region Copyright & License Information
/*
 * Crystallized Nexus - FormationMove
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Pathfinder;
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

		[Desc("Manual role bias for slot assignment. Positive values prefer front rows; negative values prefer rear rows.")]
		public readonly int RoleBias = 0;

		[Desc("Prefer durable and currently healthy actors in front rows.")]
		public readonly bool PreferHealthyFrontliners = true;

		[Desc("Prefer actors with longer weapon range in rear rows.")]
		public readonly bool PreferLongRangeBackline = true;

		[Desc("When turning more than 90 degrees, how strongly current forward position should preserve in-place ordering.",
			"Higher values reduce crossing; lower values let role priority dominate more strongly.")]
		public readonly int TurnPositionWeight = 4;

		[Desc("Keep actors that have already arrived at their previous formation slot in place when assigning new slots.")]
		public readonly bool LockSettledSlots = true;

		[Desc("Condition granted while this actor is idle on its assigned formation cell. " +
			"Use this in Mobile.ImmovableCondition to prevent friendly nudge displacement.")]
		public readonly string HoldPositionCondition = "formation-move-locked";

		[Desc("Whether bot-issued orders are put into formation as well. Off, because the formation is",
			"built from World.Selection - the LOCAL player's selection - which a bot never populates: for",
			"a bot's units the group comes back empty, so the work is done for nothing, and reading a",
			"client-side selection while resolving an order a bot issued is not a thing to rely on either.",
			"Bots keep their squads together through the squad states instead.")]
		public readonly bool ApplyToBots = false;

		public override object Create(ActorInitializer init) { return new FormationMove(init.Self, this); }
	}

	public class FormationMove : IIssueOrder, IResolveOrder, INotifyBecomingIdle, INotifyMoving
	{
		const uint FormationOrderMarker = 0x464D4F56;

		readonly FormationMoveInfo info;
		int holdPositionToken = Actor.InvalidConditionToken;
		CPos? lastFormationCell;

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

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				if (info.ValidOrders.Contains("Move"))
					yield return new FormationMoveOrderTargeter();
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order is not FormationMoveOrderTargeter)
				return null;

			if (!info.ApplyToBots && self.Owner.IsBot)
				return null;

			if (!target.IsValidFor(self))
				return null;

			var targetCell = self.World.Map.CellContaining(target.CenterPosition);
			if (!TryGetFormationCell(self, targetCell, false, out var formationCell, out var slot, out var fwdCX, out var fwdCY))
				return new Order("Move", self, target, queued);

			return new Order("Move", self, Target.FromCell(self.World, formationCell), target, queued)
			{
				ExtraData = FormationOrderMarker,
				ExtraLocation = PackFormationState(slot, fwdCX, fwdCY)
			};
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (!info.ValidOrders.Contains(order.OrderString)) return;
			if (!order.Target.IsValidFor(self)) return;

			// This is the path a bot's orders take - IIssueOrder is the player's order generator, while
			// squads issue Move and AttackMove straight through, grouped. Left applying to them, every
			// bot move order went looking for a formation group in the local player's selection.
			if (!info.ApplyToBots && self.Owner.IsBot) return;

			RevokeHoldPosition(self);

			// Direct formation orders already carry their final target cell. Persist the
			// slot state here, then let Mobile / AttackMove process the order normally.
			if (order.ExtraData == FormationOrderMarker)
			{
				UnpackFormationState(order.ExtraLocation, out FormationSlot, out lastFwdCX, out lastFwdCY);
				lastFormationCell = self.World.Map.CellContaining(order.Target.CenterPosition);
				return;
			}

			var targetCell = self.World.Map.CellContaining(order.Target.CenterPosition);
			if (!TryGetFormationCell(self, targetCell, true, out var myCell, out var slot, out var fwdCX, out var fwdCY))
				return;

			if (myCell == targetCell)
				return;

			// Fallback for orders that do not pass through our IIssueOrder path, such as
			// AttackMove's grouped order generator.
			self.World.IssueOrder(new Order(order.OrderString, self,
				Target.FromCell(self.World, myCell), order.Target, order.Queued)
			{
				ExtraData = FormationOrderMarker,
				ExtraLocation = PackFormationState(slot, fwdCX, fwdCY)
			});
		}

		bool TryGetFormationCell(Actor self, CPos targetCell, bool persistState, out CPos formationCell,
			out int formationSlot, out int fwdCX, out int fwdCY)
		{
			// Gather all living, positionable, formation-capable friendly actors in the selection.
			var group = self.World.Selection.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Owner == self.Owner
					&& a.TraitOrDefault<FormationMove>() != null
					&& a.OccupiesSpace is IPositionable)
				.ToList();

			formationCell = targetCell;
			formationSlot = FormationSlot;
			fwdCX = lastFwdCX;
			fwdCY = lastFwdCY;

			if (group.Count <= 1) return false;

			// Sort deterministically — every actor in the group computes the same assignment.
			group.Sort((a, b) => a.ActorID.CompareTo(b.ActorID));

			var myIdx = group.IndexOf(self);
			if (myIdx < 0) return false;

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
			var slotRows = new int[group.Count];
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
				slotRows[slot] = row;
			}

			// --- Slot assignment ---
			var actorToSlot = new int[group.Count];
			Array.Fill(actorToSlot, -1);
			var assignedFwdCX = fwdCX;
			var assignedFwdCY = fwdCY;
			var actorInfos = group
				.Select((a, ai) =>
				{
					var c = self.World.Map.CellContaining(a.CenterPosition);
					var fm = a.Trait<FormationMove>();
					return new FormationActorInfo(ai, fm,
						assignedFwdCX * c.X + assignedFwdCY * c.Y,
						rgtCX * c.X + rgtCY * c.Y,
						GetFrontlineScore(a, fm.info));
				})
				.ToArray();

			var desiredRows = new int[group.Count];
			var rankedActors = actorInfos
				.OrderByDescending(x => x.FrontlineScore)
				.ThenBy(x => x.ActorIdx)
				.ToArray();
			for (var rank = 0; rank < rankedActors.Length; rank++)
				desiredRows[rankedActors[rank].ActorIdx] = rank / cols;

			var usedActors = new bool[group.Count];
			var usedSlots = new bool[rawSlots.Length];
			var assigned = 0;
			if (info.LockSettledSlots)
				for (var ai = 0; ai < group.Count; ai++)
				{
					var previousSlot = actorInfos[ai].Formation.FormationSlot;
					if (previousSlot < 0 || previousSlot >= rawSlots.Length || usedSlots[previousSlot])
						continue;

					if (!IsSettledOnSlot(group[ai], rawSlots[previousSlot]))
						continue;

					actorToSlot[ai] = previousSlot;
					usedActors[ai] = true;
					usedSlots[previousSlot] = true;
					assigned++;
				}

			if (bigTurn)
			{
				// Sort units by role plus current forward projection. This lets durable
				// frontliners lead while still keeping large reversals mostly in-place.
				var sortedByFront = actorInfos
					.OrderByDescending(x => x.FrontlineScore + x.ForwardProjection * info.TurnPositionWeight)
					.ThenByDescending(x => x.ForwardProjection)
					.ThenBy(x => x.ActorIdx)
					.ToList();

				// Assign row-by-row: within each row sort by lateral projection separately.
				// This prevents units from crossing sideways — a unit that is on the right
				// stays on the right within its assigned row, regardless of row boundaries.
				var slotCursor = 0;
				for (var row = 0; row < totalRows; row++)
				{
					var colsInRow = (row == totalRows - 1) ? (group.Count - row * cols) : cols;
					var rowSlots = Enumerable.Range(slotCursor, colsInRow)
						.Where(si => !usedSlots[si])
						.ToList();

					var rowUnits = sortedByFront
						.Where(x => !usedActors[x.ActorIdx])
						.Take(rowSlots.Count)
						.OrderBy(x => x.LateralProjection).ThenBy(x => x.ActorIdx)
						.ToList();

					for (var col = 0; col < rowUnits.Count; col++)
					{
						actorToSlot[rowUnits[col].ActorIdx] = rowSlots[col];
						usedActors[rowUnits[col].ActorIdx] = true;
						usedSlots[rowSlots[col]] = true;
						assigned++;
					}

					slotCursor += colsInRow;
				}
			}
			else
			{
				// Greedy nearest-slot with role-row preference: units settle into sensible
				// rows, then preserve previous slots and proximity within those rows.
				var pairs = new List<(int ActorIdx, int SlotIdx, int RowPenalty, int SlotPenalty, long DistSq)>(group.Count * group.Count);
				for (var ai = 0; ai < group.Count; ai++)
					for (var si = 0; si < rawSlots.Length; si++)
					{
						var diff = group[ai].CenterPosition - self.World.Map.CenterOfCell(rawSlots[si]);
						var rowPenalty = Math.Abs(desiredRows[ai] - slotRows[si]);
						var slotPenalty = actorInfos[ai].Formation.FormationSlot == si ? 0 : 1;
						pairs.Add((ai, si, rowPenalty, slotPenalty, diff.LengthSquared));
					}

				pairs.Sort((a, b) =>
				{
					var cmp = a.RowPenalty.CompareTo(b.RowPenalty);
					if (cmp != 0) return cmp;
					cmp = a.SlotPenalty.CompareTo(b.SlotPenalty);
					if (cmp != 0) return cmp;
					cmp = a.DistSq.CompareTo(b.DistSq);
					if (cmp != 0) return cmp;
					cmp = a.ActorIdx.CompareTo(b.ActorIdx);
					return cmp != 0 ? cmp : a.SlotIdx.CompareTo(b.SlotIdx);
				});

				foreach (var (ai, si, _, _, _) in pairs)
				{
					if (usedActors[ai] || usedSlots[si]) continue;
					actorToSlot[ai] = si;
					usedActors[ai] = true;
					usedSlots[si] = true;
					if (++assigned == group.Count) break;
				}
			}

			// Defensive fallback: unusual locked-slot combinations should not leave an
			// actor without a target, even if the row allocator above runs out early.
			if (assigned < group.Count)
				for (var ai = 0; ai < group.Count; ai++)
				{
					if (actorToSlot[ai] >= 0)
						continue;

					for (var si = 0; si < rawSlots.Length; si++)
					{
						if (usedSlots[si])
							continue;

						actorToSlot[ai] = si;
						usedActors[ai] = true;
						usedSlots[si] = true;
						assigned++;
						break;
					}
				}

			// --- Validate this actor's slot ---
			var myPositionable = (IPositionable)self.OccupiesSpace;
			formationCell = rawSlots[actorToSlot[myIdx]];

			if (!IsPassable(self.World, formationCell, myPositionable))
			{
				var reserved = new HashSet<CPos>();
				for (var ai = 0; ai < group.Count; ai++)
					if (ai != myIdx) reserved.Add(rawSlots[actorToSlot[ai]]);

				var found = false;
				for (var r = 1; r <= 5 && !found; r++)
					foreach (var c in self.World.Map.FindTilesInAnnulus(formationCell, r, r))
						if (IsPassable(self.World, c, myPositionable) && !reserved.Contains(c))
						{ formationCell = c; found = true; break; }

				if (!found) formationCell = targetCell;
			}

			formationSlot = actorToSlot[myIdx];
			if (persistState)
			{
				FormationSlot = formationSlot;
				lastFwdCX = fwdCX;
				lastFwdCY = fwdCY;
			}

			return true;
		}

		void INotifyBecomingIdle.OnBecomingIdle(Actor self)
		{
			UpdateHoldPosition(self);
		}

		void INotifyMoving.MovementTypeChanged(Actor self, MovementType type)
		{
			if (type.HasMovementType(MovementType.Horizontal))
				RevokeHoldPosition(self);
		}

		void UpdateHoldPosition(Actor self)
		{
			if (holdPositionToken != Actor.InvalidConditionToken)
				return;

			if (string.IsNullOrEmpty(info.HoldPositionCondition) || !self.IsIdle || lastFormationCell == null)
				return;

			if (self.World.Map.CellContaining(self.CenterPosition) == lastFormationCell.Value)
				holdPositionToken = self.GrantCondition(info.HoldPositionCondition);
		}

		void RevokeHoldPosition(Actor self)
		{
			if (holdPositionToken != Actor.InvalidConditionToken && self.TokenValid(holdPositionToken))
				holdPositionToken = self.RevokeCondition(holdPositionToken);
			else
				holdPositionToken = Actor.InvalidConditionToken;
		}

		bool IsHoldingFormationPosition => holdPositionToken != Actor.InvalidConditionToken;

		static int GetFrontlineScore(Actor actor, FormationMoveInfo info)
		{
			var score = info.RoleBias;

			if (info.PreferHealthyFrontliners)
			{
				var health = actor.TraitOrDefault<IHealth>();
				if (health != null && health.MaxHP > 0)
				{
					score += health.MaxHP / 10;
					score += health.HP * 60 / health.MaxHP;
				}
			}

			if (info.PreferLongRangeBackline)
			{
				var maxRange = GetRulesMaximumRange(actor);
				score -= maxRange.Length * 12 / 1024;
			}

			return score;
		}

		static WDist GetRulesMaximumRange(Actor actor)
		{
			var maxRange = WDist.Zero;
			var armamentInfos = actor.Info.TraitInfos<ArmamentInfo>().ToArray();

			foreach (var attackInfo in actor.Info.TraitInfos<AttackBaseInfo>())
				foreach (var armamentName in attackInfo.Armaments)
					foreach (var armamentInfo in armamentInfos)
					{
						if (armamentInfo.Name != armamentName)
							continue;

						if (maxRange < armamentInfo.ModifiedRange)
							maxRange = armamentInfo.ModifiedRange;
					}

			return maxRange;
		}

		static bool IsPassable(World world, CPos cell, IPositionable positionable)
		{
			return world.Map.Contains(cell) && positionable.CanEnterCell(cell);
		}

		static bool IsSettledOnSlot(Actor actor, CPos slotCell)
		{
			return actor.IsIdle && actor.World.Map.CellContaining(actor.CenterPosition) == slotCell;
		}

		static CPos PackFormationState(int slot, int fwdCX, int fwdCY)
		{
			return new CPos(slot, (fwdCX + 1) * 3 + fwdCY + 1);
		}

		static void UnpackFormationState(CPos state, out int slot, out int fwdCX, out int fwdCY)
		{
			slot = state.X;
			fwdCX = state.Y / 3 - 1;
			fwdCY = state.Y % 3 - 1;
		}

		readonly struct FormationActorInfo
		{
			public readonly int ActorIdx;
			public readonly FormationMove Formation;
			public readonly int ForwardProjection;
			public readonly int LateralProjection;
			public readonly int FrontlineScore;

			public FormationActorInfo(int actorIdx, FormationMove formation, int forwardProjection,
				int lateralProjection, int frontlineScore)
			{
				ActorIdx = actorIdx;
				Formation = formation;
				ForwardProjection = forwardProjection;
				LateralProjection = lateralProjection;
				FrontlineScore = frontlineScore;
			}
		}

		sealed class FormationMoveOrderTargeter : IOrderTargeter
		{
			public string OrderID => "Move";
			public int OrderPriority => 5;
			public bool IsQueued { get; private set; }

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (!self.AcceptsOrder("Move") || target.Type != TargetType.Terrain)
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				var location = self.World.Map.CellContaining(target.CenterPosition);
				var mobile = self.TraitOrDefault<Mobile>();
				if (mobile != null)
				{
					var formationMove = self.TraitOrDefault<FormationMove>();
					if (mobile.IsTraitPaused || mobile.IsTraitDisabled
						|| (mobile.IsImmovable && formationMove?.IsHoldingFormationPosition != true))
						return false;

					var explored = self.Owner.Shroud.IsExplored(location);
					if (!self.World.Map.Contains(location)
						|| (!explored && !mobile.Info.LocomotorInfo.MoveIntoShroud)
						|| (explored && mobile.Locomotor.MovementCostForCell(location) == PathGraph.MovementCostForUnreachableCell))
						cursor = mobile.Info.BlockedCursor;
					else if (!explored || !mobile.Info.TerrainCursors.TryGetValue(self.World.Map.GetTerrainInfo(location).Type, out cursor))
						cursor = mobile.Info.Cursor;
				}
				else
					cursor = "move";

				return true;
			}

			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers)
			{
				if (target.Type == TargetType.Actor && (target.Actor.Owner != self.Owner || self.World.Selection.Contains(target.Actor)))
					return true;

				return modifiers.HasModifier(TargetModifiers.ForceMove);
			}
		}
	}
}
