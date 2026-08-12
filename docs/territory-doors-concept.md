# Territory and doors

Status: detection validated visually (`/cntopo`, several iterations) and now drives defense
placement (`EnableDoorDefense`, opt-in, on for the tested profiles in `ai/base-building.yaml`). A
profile-selected kill zone behind each door (`EnableDoorKillZone`: walls for Turtle/Tech, wider
defense spread for everyone else) is on for its first real playtest. A shared region graph
(`CNRegion`) replaces the per-bot territory flood with a one-time, shared shape plus a periodic
ownership tally - visually confirmed, including a fix for ramps not registering as region
boundaries. Its first real consumer, `EnableCoreRegionPlacement`, keeps ordinary building placement
inside the bot's starting region instead of a plain search ring that doesn't know a door is in the
way. Region roles, saturation scoring and front squads are recorded below as future work, not
started.

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

## A shared region graph, and the control system that gave it shape

Shipped (`CNRegion`, on `CNSharedTopology`). Three real in-game observations this session all
traced back to the same cause: a base built defense near itself while its own most important door
sat far outside `MaximumDefenseRadius`; a refinery placed on a naturally defensible peninsula with
no awareness the peninsula was one coherent, narrow-necked pocket; production dropped right next to
a door because nothing placing it had any notion of "this spot belongs to a region, and that region
has a shape." Every bot's territory used to be a full breadth-first race over the whole map,
rebuilt from scratch per bot per refresh — doable for chokepoints because `CNSharedTopology`
already scans them once and shares the result across every bot on the map+locomotor, but territory
never did that: each bot flooded the same ground independently, and with more than two players a
bot's "mine vs. everyone else" race could disagree at the edge between two *other* players, since
neither side of that edge was this bot's own walk.

The fix: a **region graph**, shared exactly like the chokepoint scan — the map cut once, inside the
same `BuildOwnTopology()` pass, into the same bowls a territory claim finds today (an *unseeded*
flood-fill using the same chokepoint-corridor barrier `RebuildTerritory` already uses), stable
because it depends on terrain and doors, not on who owns what.

**Height counts as a barrier too, not just chokepoints.** First `/cntopo` pass showed a region
spanning both sides of a visible cliff — a ramp only resolves to a sealable-corridor gate cell when
it happens to be the narrowest crossing nearby; a wide or gentle one otherwise let the flood walk
straight through, so high ground and low ground read as one region despite looking clearly split.
Fixed by adding every cliff ramp's *full* footprint (not just its two chokepoint endpoint markers)
as an extra barrier for region-building specifically - height is a real separation even where
nothing is narrow enough to wall, which the door/territory model never needed since defence doesn't
care whether a gap is wide, only whether it is narrow enough to hold. This only changes region
computation; `RebuildTerritory`'s own gate (and everything built on it - doors, kill zones) is
untouched. Adjacency was generalised alongside it: two regions are neighbours if they share *any*
barrier cell, not only ones that happen to carry a resolved door corridor.

`CNRegion` carries `Cells`, `BoundaryCells` (for drawing), `DoorCorridorIndices`, `AdjacentRegionIds`
(falls out for free from which regions share a barrier cell), `ResourceCellCount`, and
`BuildableCellCount` - facts every bot
reads via `GetRegions()`/`GetRegionIdAt()`/`GetRegionOwner()`, none of it re-derived per bot.

Ownership is a separate, cheap, periodically-refreshed tally on top of that shape rather than a
per-bot flood: building majority per region, one full-map actor scan per
`RegionOwnershipRefreshInterval`, coordinated so only one bot's tick does it
(`TickRegionOwnershipRefresh`, `NextOwnershipRefreshTick` on the shared object) and every other bot
just reads `RegionOwners` afterward. A region changes hands - is "conquered" - when the tally
flips, which is the hook the doc's own open question about base clusters was pointing at:

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

**Next, not yet built: saturation.** A bot should be able to ask "how used up is my own region" -
enough resources left, enough buildable space left, doors defended well enough - the same three
questions that decide "keep building here" versus "look at an adjacent region instead."
`ResourceCellCount`/`BuildableCellCount` are the static capacity side of that question, already
shipped; the used side (own harvester/refinery count against `ResourceCellCount`, own building
footprint against `BuildableCellCount`, and a richer per-door defense count than the existing
boolean `DoorHasNearbyDefense` - compared against a door's width/importance, not just "any") is
bot-specific dynamic state and deliberately not part of the shared graph. This is the natural
consumer once region roles exist, not before.

## Two things the first region cut got wrong, and how they were closed

**Wide ramps were only sealed at their edges.** `IsCliffRamp` flags a cell only when a cliff is
*immediately* beside it, so on a ramp more than a couple of cells wide only the outermost columns
qualified — the middle of the slope touches nothing impassable and was never barrier at all. The
one-ring dilation added afterwards can close a hole two cells across and no more, so anything wider
still let the flood walk straight up. A ramp is one connected run of walkable height transitions,
bounded by the cliff it cuts through, so the cliff-flagged cells are now grown across that run before
dilation: the seal ends up the ramp's true width, whatever that is, while open bumpy ground stays
untouched because no cliff seeds it in the first place. Region-only, like the ramp barrier itself —
`ScanRampEndpoints` keeps its own scan and its chokepoint markers are unchanged.

**The barrier is now drawn by `/cntopo`** (`RegionBarrier` on `CNSharedTopology`). Every "the regions
are wrong here" so far has really been "the barrier has a hole here", and a hole is invisible from
the region outlines alone — they simply run past it as if nothing were there.

Two corrections followed from watching it, both from the same session:

*`Map.Ramp` as the run to grow along — tried, reverted.* The engine's own slope index looked like the
honest answer to "a wide climb flattens out in the middle, so `IsHeightTransition` tears there". It
carpeted the map: a fifth of all ground carries a nonzero slope index on this tileset, so the run was
connected across most of it. `RampSealMaxSpread` (default 8, still in place) was added to bound the
growth and barely dented it — the growth is seeded from every cell beside higher impassable rock, and
rock outcrops are everywhere.

*Cutting on every walkable step up — built, and rejected in play (`1270d1b`, reverted).* Stated on the
levels rather than on ramp shapes: a passable cell with a higher passable neighbour is barrier, which
guarantees nothing walks between levels without standing on it and makes every region uniform in
height. Clean rule, too much barrier in practice; the cliff-seeded version was preferred as good
enough for now. Kept in history rather than deleted, since the argument for it still holds if
region-per-level ever becomes the thing that is wanted.

*The census is what the next attempt should start from.* `BuildRegions` logs, once per map:

```
terrain: 24254 passable, 5090 slope tiles, 1304 height transitions, 359 cliff-adjacent
         heights 0:13 1:101 2:5706 3:446 4:437 5:596 6:9309 7:766 8:419 9:799 10:5101 11:529 12:32
```

Read on the map above: three real levels (2, 6, 10) with narrow bands between them, a fifth of the
ground carrying a slope index, and 359 cells against a cliff — which was the *entire* seal, because
the growth across height transitions added zero cells to it. Three attempts here were each argued
from a different guess about that shape and each was wrong in a different direction.

**A chain of mini-regions along a coastline.** Regions of 15, 35 and 47 cells strung along one
shoreline, each cut off by its own small `Passage`. Structurally the same problem the doors had
(`DoorMergeRadius`'s target): a chokepoint can be a perfectly genuine bottleneck — it passed
`MinPassageSideCells` to exist at all — and still be far too minor a wrinkle to deserve a region of
its own. `MinRegionSize` (default 60) merges these: the barrier is built as individually droppable
pieces (one per resolved corridor, one per unresolved chokepoint cell, one per physical ramp), and a
piece separating an undersized region is dropped and the whole fill run again, up to ten rounds.
Dropping is monotone, so it terminates by itself; the cap is insurance, and it all happens once
inside `BuildOwnTopology`, never per tick.

A piece goes as soon as *either* side of it is undersized — a 15-cell sliver beside a 5000-cell
region should fold in at once rather than wait for the big one to look too small as well. An
undersized pocket with nothing to merge into keeps its pieces and stays small, the same way
`MinDomainNodes` leaves unreachable pockets alone. Deliberately *not* a post-hoc edit of finished
`CNRegion` records: recomputing boundaries, adjacency and the resource/buildable counts by hand per
merge is the bespoke bookkeeping that re-deriving door geometry by hand already cost this feature
five attempts. Re-running the fill recomputes all of it for free. One `CNBotLog.Debug` line reports
pieces, drops, rounds and the resulting region count.

**Ramp pieces are exempt from the merge**, decided against the first version of it after the log came
back with `82 of 114 pieces dropped -> 12 regions`: the merge was eating the ramp seals it had just
taken two fixes to get right, and the overlay showed a single barrier cell where a wide ramp's band
should be. Dropping a ramp merges high ground into low ground — the one separation the ramp barrier
exists for — and a small plateau is still a place in its own right rather than part of the ground
below it. Corridors and chokepoint cells stay droppable; those are the wrinkles the threshold is
actually aimed at.

Worth knowing about the merge in general: it evaluates against the *current*, still-fragmented fill,
so most drops happen in round one when nearly every piece touches something small. That cascade is
what folds a chain of slivers into one region in three rounds, and it is also why the exemption
above matters — anything that must survive has to be exempt, not merely large-sided.

## Core-region-aware placement: the first real consumer

Shipped (`EnableCoreRegionPlacement`, on in yaml). Ordinary building placement
(`ChooseBuildLocationInBase` → `FindPos`/`TryFindPos`, `CNBaseBuilderQueueManager.cs`) picked
candidates from a plain geometric ring around the base (`Map.FindTilesInAnnulus`) - confirmed by
direct research to consult no region, territory or door concept at all. A ring doesn't know a door
is in the way; it could offer a cell on the *other* side of a chokepoint just as easily as one in
the region the base actually holds.

Deliberately scoped to a first step, not every base: the candidate pool is filtered to the region
containing `BaseOrigin` (the bot's actual starting position, stable regardless of role
reassignment) only when the base being built for *is* the starting one
(`targetBase.AnchorId == PrimaryBase.AnchorId` - `AnchorId`, not object identity, since
`GetBases()`/`PrimaryBase` rebuild fresh instances every call). Filtered once, at the single point
every layout (`Grid`, `BaseGrid`, `Compact`, ...) and every building type (tech, production,
refineries, ordinary) already passes through - one change covers all of them. Falls back to the
unrestricted pool whenever the filter would leave nothing to place on, so a base pushed toward its
region's edge never silently stalls.

What this does *not* yet do: it keeps buildings inside the right region, it does not push them
away from a door within that region. That is the separate, already-diagnosed bug where
`ChooseBuildLocationInBase`'s `isTech` branch is the only one that ever consults
`ScoreTechPlacementSafety`/danger hotspots - `BuildingType.Production` (the observed case: a war
factory built right next to a door) never does, tracked apart from this change. Nor does it apply
to expansions/non-core bases, or reason about how much of the region is already used up
(saturation, above) - both explicitly deferred to later, region-role-aware passes.

## Doors inside your own ground: measured by region, not by a guessed seal

Shipped (`GroundBeyondRegions`, `CNTacticalMapBotModule.cs`). Step 4 of the algorithm measures how
much ground a door opens onto by flooding outward from the cells just *outside* the claim, with the
door sealed. A door standing in the middle of held territory has no such cells — both of its sides
are already ours — so `beyond` came back 0 and fell through to `GroundBeyondPinch`, which builds its
own sideways barrier out to a fixed `PinchBarrierReach = 16` and floods from two seed cells chosen
by the direction from the base. That guesses both the axis and the side. Live `debug.log` on a
plateau with a single approach showed the result: the pinch's own barrier can run across the
plateau it is supposed to be measuring, or seed on the base side, and the door then failed
`MinDoorGroundBeyond` as "too shallow" — a ramp any human reads as *the* way up.

The region graph already knows the answer without flooding anything. A door's cells are a region
barrier by construction: `BuildRegions` cuts the map on exactly the resolved chokepoint corridors
doors are made of, so the regions standing against a door's far side are sitting right there, each
carrying its own `Size`. `GroundBeyondRegions` takes the region holding the base reference as "our
side", collects the region ids within two cells of the door's run (two, not one: a ramp's barrier is
dilated by a ring, so its immediate neighbours belong to no region at all), and sums those regions
plus everything they lead on to in turn with our own region held shut — a door onto a small plateau
that itself opens onto the rest of the map is a way in, not a pocket. Same cap
(`DoorBeyondCellCap`), same unit (cells), so the existing `MinDoorGroundBeyond` threshold and the
`GetDoorHotspots` weighting needed no re-tuning.

Deliberately wired as a *fallback*, not a replacement: outer doors keep the flood measurement that
was already validated by eye, and `GroundBeyondPinch` stays as the last resort for a door that sits
on no region barrier at all (a `FindNarrowestCrossing` candidate in the middle of one region). The
funnel debug line reports how many doors each fallback answered for, for the same reason every other
stage of it is reported — a single number on the overlay cannot say which path produced it.

Known and accepted: where the far side loops back around into our own ground, the region walk runs
around the map and hits the cap, so the door reads "wide open". `GroundBeyondDoor` has always
behaved that way too, and for a door you can genuinely be walked in through it is the right answer.

## A kill-zone behind each door: two modes, on for its first playtest

Shipped, `EnableDoorKillZone` on in yaml for the tested profiles - not yet observed building a wall
in a real match. `GetDoorDefenseAnchors` already pulled placement
toward a position behind a door facing its approach, but that was only ever a scoring bonus on top
of the ordinary build-site search - it never laid out a shape, and never distinguished profiles.

Three rounds of feedback settled on two modes instead of one universal shape, chosen automatically
by the active profile (`DoorKillZoneUsesWalls()`, same Adaptive-resolves-to-its-current-sub-profile
pattern `ShouldSealChokepoints()` already uses):

- **C-mode** (Rush, Steamroller, Expansion, Adaptive resolving to one of those): no wall - the
  half-annulus already built for the earlier single-shape version (`GetDoorKillZoneCells`,
  `Info.DoorKillZoneRadius`, `Map.FindTilesInAnnulus` + a half-plane filter against `Outward`)
  becomes extra anchor points instead, feeding the same `ChokepointDefenseAnchorWeight` falloff
  `GetDoorDefenseAnchors` already uses - defense spreads across the arc as the nearest anchors fill
  up, no new scoring code.
- **L-mode** (Turtle, Tech): actually walls the zone, in two tiers. Tier 1 retreats to a genuinely
  narrower natural pinch behind the door if one exists - found by calling `FindNarrowestCrossing`
  (made public for this) from a few points stepping back along the door's approach axis, exactly as
  `ResolveChokepointCorridors` already does from a scanned chokepoint cell. A found pinch is a real
  `CNSealableCorridor`, so sealing it is a direct reuse of `ChokepointGateFootprint` and
  `ChokepointCorridorIsWorthSealing` (both already generic over any corridor) - wall, gate,
  orientation (3x1/1x3) and the "don't spam gates" cap all come from the same machinery
  `EnableChokepointSealing` already has. This also solves a wall connecting cleanly to real terrain
  for free: `FindNarrowestCrossing`'s own shoulder-walk already guarantees that. Tier 2, only when no
  such pinch exists nearby, walls a simple fixed-radius box (two flanks and a back, door-facing side
  open, reusing `PerimeterCells`) - accepted as a rare, lower-stakes fallback rather than something
  needing its own terrain-seeking geometry.

Both tiers still require `DoorHasNearbyDefense` - a door is only worth fortifying once an own
defence structure already stands within `DoorKillZoneRadius` of it (placed by `EnableDoorDefense`,
which runs independently). Tier-1 gates share the existing `BasePerimeterMaxGateCount` cap with
chokepoint-seal and base-perimeter gates rather than getting a separate budget.

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
