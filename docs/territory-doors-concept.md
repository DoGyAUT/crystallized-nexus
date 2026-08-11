# Territory and doors

Status: **concept**. Detection work exists on `ai/territory-doors` and is not wired to anything.

## The problem

Defence is planned against points. `GetTopologyHotspots` returns six chokepoints ranked by
`BaseWeight - DistSq / 16`, where `BaseWeight` is a constant per type — 120 passage, 170 ramp,
220 bridge. Six independent points cannot become a line, and with the weight fixed by type the
six are really just the nearest bridges, then ramps, then passages, regardless of what any of
them controls.

That is then multiplied by the bases a bot holds, each planning its own with its own budget
share. A bot with seven bases scatters defences over seven neighbourhoods and holds none of
them. That is what the "pure chaos" screenshots show.

A human does not defend bases. They defend an area, and the buildings inside it come along.

## The model

Three nouns, of which the codebase already has two and a half.

**Territory** — the ground a player holds. Bounded by terrain and by the chokepoints leading
out of it, *not* by a radius. A bowl with one ramp is one territory whether it contains one
base or four.

**Wall** — the part of that boundary terrain already closes: cliff, water, map edge. Free.
Nothing is built there.

**Door** — a gap in the wall: the span from one side of a passage to the other. Narrow by
definition. This is what a defence is anchored at, and what the sketch this came from marked
with white bars across the ramps.

A fourth follows from the first three but is a separate question: **front**, the part of the
boundary that touches another player's claim. No terrain helps there; it is held with an army
or not at all.

## What already exists

- `CNChokepoint` — passages, ramps and bridges, scanned from terrain, with domains. Working.
- `CNSealableCorridor` — **a door, already computed.** Centre, which axis the wall runs on, and
  the cells wall to wall. It tries both axes, walks to the shoulder on each, requires the
  approach to be open on both sides, and keeps the narrower. Bounded by `MaxPassageWidth`.
- `CNHighGroundEdge` — cliff-edge cells with an outward normal. The wall, cell by cell.
- `HasRealAccessBeyond` — already builds a barrier across a pinch and floods the far side. The
  count it produces is discarded; only the boolean is kept.

The pieces are the front, cell by cell. What is missing is assembling them into a front.

## The algorithm

1. **Claim.** One breadth-first walk seeded with own buildings and known enemy buildings;
   whichever side reaches a cell first keeps it. Chokepoint cells are not enterable, so the
   claim stops at them and doors end up on the boundary by construction rather than having to
   be cut back out of it.
2. **Split the edge.** An edge cell whose outside neighbours are all impassable is wall. One
   whose outside neighbour belongs to the enemy is front. One standing against a chokepoint is
   a door.
3. **Form the door.** *Do not derive the span geometrically.* Use the `CNSealableCorridor` that
   already covers that chokepoint: it has the axis, the shoulders and the width.
4. **Weight it.** Ground behind the door, measured with the corridor's own cells sealed, capped
   — past a point the answer is only "wide open".

## What consumes it, and in what order

1. Nothing. Detection only, drawn in `cntopo`. **Settle whether the lines match the ones a human
   would draw before wiring anything to them.**
2. Defence placement: cover doors by weight instead of six points per base. The budget follows
   the doors, so the widest way in gets a real position instead of every base getting two
   turrets.
3. Expansion siting: does a candidate spot have a defensible boundary at all?
4. Attack: which of the enemy's doors is weakest. `PickApproachCell` gropes at this today.

## What was tried and why it failed

Five attempts, recorded because the design follows from them.

1. **Doors from the claim's edge, "outside neighbour is passable".** With 8-connectivity every
   edge cell has a passable outside neighbour, so everything was a door.
2. **Same, after excluding enemy-owned neighbours.** The two claims tile the whole reachable
   map between them, so "passable and unclaimed" never occurs and *nothing* was a door.
3. **Doors from chokepoints inside the territory.** Zero, because `TerritoryCellCap` counted
   both claims in one walk: at 5000, territories of 2901 and 2161 hit it between them and the
   walk stopped before reaching most of the map.
4. **Chokepoints as walls, doors as gate cells touching the claim.** Correct idea — this is the
   one that made doors land on the boundary — but a door was then a blob of adjacent gate cells
   around the marker, nine wide, rather than a line across the gap.
5. **Span walked outward from the gate cell.** The axis was derived from which side the claim
   lay on, which is noisy: spans ran diagonally, sealed nothing, and `beyond` came back as 0-3.

The common thread is worth stating plainly: each attempt invented geometry that
`CNSealableCorridor` already computes correctly, and each failed on a detail that structure
handles — trying both axes, validating the approach, walking to the shoulders, keeping the
narrower.

## Open questions

- **How many doors should a territory have?** If a map yields dozens, the model is wrong
  somewhere; a handful is the point of it.
- **Does the enemy race earn its cost**, or is bounding by terrain alone enough? The front
  concept needs it; the doors do not.
- **Do base clusters survive?** They must, as build sites — buildings have to go up within
  reach of existing ones. Their *roles* are what territory should replace: a base behind the
  line needs no defence, one at a door does.
