# Territory and doors

Status: detection validated visually (`/cntopo`, several iterations) and now drives defense
placement (`EnableDoorDefense`, opt-in, on by default for the tested profiles in
`ai/base-building.yaml`). Region control, region roles, kill-zone perimeters and front squads are
recorded below as future work, not started.

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

1. ~~Detection only, drawn in `cntopo`.~~ **Done.** Settled visually across several iterations
   (leak fix, merge radius, ground-behind floor) before anything consumed it.
2. ~~Defence placement: cover doors by weight instead of six points per base.~~ **Done.**
   `GetDoorHotspots`/`GetDoorDefenseAnchors` (`CNTacticalMapBotModule.cs`) replace
   `GetTopologyHotspots` at both its call sites when `EnableDoorDefense` is set and a territory
   door exists, with a graceful fallback to the old six-hotspot behaviour otherwise. The budget
   follows the doors — no per-base distance prefilter — so the widest way in gets a real position
   instead of every base getting two turrets.
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

- **Does the enemy race earn its cost**, or is bounding by terrain alone enough? The front
  concept needs it; the doors do not.
- **Do base clusters survive?** They must, as build sites — buildings have to go up within
  reach of existing ones. Their *roles* are what territory should replace: a base behind the
  line needs no defence, one at a door does.

## Resolved: how many doors should a territory have

First `/cntopo` pass answered this one before it needed a debate: dozens, strung along a single
cliff in a ragged chain — a few cells apart, several different widths, tiny `beyond` figures on
most of them. Two separate causes, both visible in the same screenshot.

**The gate only blocked the chokepoint's coarse marker cell**, not the corridor's actual width.
Anything wider than one cell let the claim leak around it during the walk, so a single real gap
fragmented into several boundary cells a cell or two apart instead of closing cleanly — each of
those then resolved to its own nearby-but-distinct `CNSealableCorridor`, which is where the chain
came from. Fixed by resolving every chokepoint to its corridor *before* the walk
(`ResolveChokepointCorridors`) and gating on the corridor's full cell set, not `cp.Cell`. The same
resolved corridors are reused as the door candidates afterwards, so the width that blocked the
walk is exactly the width a defence would be built across.

**Nothing required a door to lead anywhere**, so a pinch that opened onto almost no ground still
counted as one. `MinDoorGroundBeyond` drops these — measured with the door's own corridor sealed,
per the algorithm's step 4. Started at 24 (matching `MinPassageSideCells`); raised to 96 (matching
`MinimumChokepointBeyondCells` — the same "is what's behind this real" question, asked for a
different feature) after a `beyond 25` visibly cleared the first threshold while leading nowhere
worth defending.

Even with the leak fixed, a couple of genuinely separate gaps a handful of cells apart still read
as one door to a human, not two. `DoorMergeRadius` (default 8) folds a candidate into a wider one
found within that radius, kept widest-first so the one that actually leads somewhere survives.

## Future: a control system, and regions that replace `CNBotBase`

Not started. Recorded so the direction is not re-derived later.

Right now every bot's territory is a full breadth-first race over the whole map, rebuilt from
scratch per bot per refresh. That is doable because chokepoints already give the terrain-only
part of the answer for free — `CNSharedTopology` already scans them once and shares the result
across every bot on the map+locomotor. Territory does not do this yet: each bot floods the same
ground independently, and with more than two players a bot's "mine vs. everyone else" race can
disagree at the edge between two *other* players, since neither side of that edge is this bot's
own walk.

The next step this points at: a **region graph**, shared like the chokepoint scan — the map cut
once into the same bowls a territory claim finds today, stable because it depends on terrain and
doors, not on who owns what. Ownership then becomes a small per-region vote (whose buildings
outweigh whose in that region) instead of a per-bot flood over the whole map, and it stays
consistent for every player by construction instead of by two independent walks happening to
agree. A region changes hands — is "conquered" — when the vote flips, which is the hook the doc's
own open question about base clusters was pointing at:

**`CNBotBase` (`CNBaseBuilderBotModule.cs`) is what a region should replace, not sit next to.**
It already does two things a region graph would do better. Clustering: construction yards within
`BaseClusterRadius` form a base, buildings join by raw distance (`MaxBaseRadius` /
`GetBaseMembershipRadius`) — a radius, exactly the thing territory was built to stop using, so the
same "seven bases, seven neighbourhoods" scatter the problem statement opens with can still happen
one level up. Roles: `CNBaseRole` (`Core` / `Economy` / `Military` / `Outpost` / `Secondary`) is
assigned by ad hoc heuristics — nearest-to-origin for Core, chokepoint-radius proximity for
Outpost, distance-to-danger-hotspot for Military — each reinventing, per base, a question a region
already answers for free: an outpost is a region that touches a door, military is a region whose
front is active, a base behind the line needs no defence because its region has no door at all.
Region roles would not be a new idea next to `CNBaseRole` - they are what `CNBaseRole` was
approximating with distance because the terrain-bounded version did not exist yet.

Past that: regions get assigned for secondary base / eco / defense / outpost / production, the
same shape as the existing `CNBaseRole` enum. Whether that stays exactly those five names or
grows is unresolved on purpose - the point recorded here is *replace*, not *add another one*.

## Future: a kill-zone perimeter around each door

Not started. `GetDoorDefenseAnchors` pulls placement toward a position behind a door facing its
approach, but that is still just a scoring bonus on top of the ordinary build-site search - it
does not lay out a shape.

The idea: a perimeter zone around each door - rectangle or half-circle, still undecided - that a
bot can wall off (profile-gated, not every profile should spend on this) with defense placed
behind it. This is a generalisation of the existing `EnableChokepointSealing` /
`ChooseChokepointWallLocation` / `ChooseChokepointGateLocation` feature
(`CNBaseBuilderQueueManager.cs`) from `CNSealableCorridor` to `CNTerritoryDoor` - the same
relationship the doors themselves have to the six old hotspots: a wider, better-founded version of
something that already works, not a new concept sitting next to it.

## Future: region-aware front squads

Not started. A door has terrain doing part of the work, which is what lets a handful of turrets
hold it. A **front** - the part of the boundary touching another player's claim - has none: "held
with an army or not at all," per the model above. Nothing in this project puts an army there;
`GetDefensePlacementThreats` only ever proposes *building* placement, and doors intentionally give
the front zero proactive weight of their own (see the open field question below).

`CNSquadManagerBotModule`/`CNSquadType` already has a `Protection` squad, but it is purely
*reactive* - triggered by attack. A `Front`-aware squad state would instead be fed from
`GetTerritoryFront()` and hold or patrol that boundary proactively, before the first hit lands
rather than after it.

## Open field behaviour, for the record

A fully open map (no real chokepoints anywhere) leaves `GetTerritoryDoors()` empty, so
`GetProactiveThreats` falls back to the old six-hotspot behaviour untouched - no regression, no
improvement, nothing to model there. A *partially* open map is the sharper case: real doors get
covered the way this doc intends, but a wide open flank is classified `Front`, not a door, and
gets zero proactive weight from this system - only the reactive danger-memory path responds there,
after the first attack. That is not a bug in this pass; it is the "front squads" gap above,
recorded rather than silently accepted.
