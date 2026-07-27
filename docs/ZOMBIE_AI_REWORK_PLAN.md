> **Provenance.** Authored by Fable 5 (xhigh) on 2026-07-27 from a brief by tinyclaw, at
> strawberry_cow's request. Not yet implemented — this is a plan, not a record of work done.
>
> **Independently verified by tinyclaw before this was committed:**
> - §1.3's zero-callers finding is TRUE. Swept `ZombieSim.Hear/.Raycast/.Damage/.Attacks` for
>   non-test callers: there are none. Mechanism confirmed: `SoundBus.Emit`, `PlayerController`'s
>   bullet resolution and `ZombieNetSync` all reach zombies through the `"zombies"` node group,
>   which `ZombieDirector`'s borrowed rigs never join. Under `--newzombies` zombies therefore
>   cannot be shot, cannot hear gunshots, deal no damage and do not replicate.
> - §0's Godot verdict resolves an inference tinyclaw had explicitly flagged as unproven when
>   shipping the per-pocket split (`70aeb742`). Fable read it from Godot 4.6-stable source with
>   citations. The split stands.
>
> Everything else retains Fable's own evidence marks: [MEASURED] / [SOURCE] / [INFERENCE] /
> [ASSUMED]. Treat [INFERENCE] and [ASSUMED] as unproven — several of this project's worst days
> came from acting on confident-sounding claims nobody had checked.

# Zombie AI & Pathfinding Rework — implementation plan

Requested by strawberry_cow, 2026-07-27. Objective function, his words: **"the cheapest pathfinding
that still has a meaningful gameplay effect."** Zombies must find you, come for you, get in the way,
and be outmanoeuvrable. Everything below is argued in those terms. This plan supersedes
`docs/ZOMBIE_REWRITE_PLAN.md` for architecture (its phases 0–2 shipped and are kept; its phases 3–4
are absorbed into the staging here).

Evidence marks used throughout: **[MEASURED]** = read off the live profiler on strawberry's box.
**[SOURCE]** = read from actual source (ours, Godot 4.6-stable, or the PZ B41.78/B42 decompile), with
citation. **[INFERENCE]** = derived, not directly observed — treat as a hypothesis with a stated test.
**[ASSUMED]** = plausible default that a stage must confirm before building on it.

---

## 0. The Godot verdict — the inference is CONFIRMED [SOURCE]

The claim under test: *Godot resolves path endpoints by linearly scanning every polygon in the
navigation map, so a query's cost is O(total map polygons) regardless of path length.*

**Confirmed from Godot 4.6-stable source** (`modules/navigation_3d/`, examined at tag `4.6-stable`;
unchanged in 4.7-stable):

- `NavMeshQueries3D::_query_task_find_start_end_positions`
  (`3d/nav_mesh_queries_3d.cpp:237`) is a triple loop — every region → every polygon → every
  triangle-fan face — calling `Face3::get_closest_point_to` **twice per face** (once for start, once
  for target). No region AABB pre-check, no early-out when the point lies on a polygon, no BVH, no
  grid. A grep of the whole module for spatial acceleration finds nothing query-side.
- There is a **second** O(total polygons) pass per query nobody suspected:
  `_query_task_build_path_corridor` (`3d/nav_mesh_queries_3d.cpp:319`) begins by resetting a
  `path_corridor` array sized to the *entire map's* polygon count
  (`3d/nav_map_builder_3d.cpp:411`).
- The A\* search itself is heap-driven from the start polygon and scales with polygons *expanded*,
  not map size (`3d/nav_mesh_queries_3d.cpp:366`) — with one nasty exception: an **unreachable
  target floods the entire reachable component** before falling back to "furthest reachable"
  (heap-empty branch, line 402). That is the exact worst case our failed-path backoff (commit
  `25ded9d1`, `ZombieSim.cs:599-616`) was added for.
- `map_get_closest_point` is the same full scan (`3d/nav_mesh_queries_3d.cpp:961`) — the
  0.2–0.3 ms/query reported in godot-proposals **#12679** (open, no fix merged through 4.7).
- Three usable extras discovered: **(1)** queries are thread-safe since 4.3 (PR #79577; per-thread
  `PathQuerySlot` pool sized by `navigation/pathfinding/max_threads`), so off-main-thread queries
  are a supported escape hatch. **(2)** `NavigationPathQueryParameters3D` exposes
  `path_search_max_polygons` (default 4096) and `path_search_max_distance` (default off) — they cap
  the A\* flood (incl. the unreachable-target case) but do **nothing** for the endpoint scan.
  **(3)** `NavigationLink3D` *can* bridge disjoint region clusters inside one map — but merging
  pockets back into one map via links would reinstate the O(total) scan, so it is a trap, not a fix.

**What depends on this verdict:** the per-pocket map split (commit `70aeb742`) is the correct and
only real mitigation Godot offers; it must never be undone by "cleanup" that re-merges maps or adds
links across pockets. The cost model in §6 (endpoint scan ∝ polygons in the queried map) is now
source-backed, so the projected post-split query cost (~0.19 ms, §1) is a scaling of a measured
number by a confirmed mechanism — still **[INFERENCE]** until Stage 0 measures it, but a safe one.

---

## 1. Where we actually are

### 1.1 Measured

- Pre-fix: sim tick 352 ms of a 354 ms frame; `s.paths` 426.9 of 428.9 ms — pathfinding was the
  whole cost **[MEASURED]**.
- Removing the two redundant `MapGetClosestPoint` calls (commit `61bfe423`,
  `game/ZombieDirector.cs:359-364`): 428 → 175 ms **[MEASURED]**.
- Remaining: ~3.5 ms per successful `MapGetPath` against the merged 56,876-polygon map, cost
  independent of path length, ~4 queries/tick **[MEASURED]**.
- Per-pocket split (commit `70aeb742`, `game/ZombieNav.cs:78-137`): ~2,993 polygons per queried map
  instead of 56,876. Projected ≈ 3.5 ms × (2993/56876) ≈ **0.19 ms/query**, so the full budget of 8
  (`ZombieSim.cs:101`) ≈ 1.5 ms/tick worst case. **[INFERENCE — Stage 0 measures this first.]**
- Movement, tiering, spatial rebuild, sensing: 1.6 / 0.1 / 0.2 ms — noise **[MEASURED]**.

### 1.2 What the new system is, structurally [SOURCE]

`core/UnturnedSim/ZombieSim.cs` (~865 lines): zombies are rows in parallel arrays; 4 tiers
Close/Near/Far/Ambient with strides 1/1/5/50, slot-phased (`ZombieSim.cs:824-829`); corridor of ≤8
waypoints per row (`MaxWaypoints`, `ZombieSim.cs:107`); one global FIFO path queue drained at 8
queries/tick (`:627-649`); sight = cone + `IZombieLineOfSight` ray per candidate per due tick
(`:529-555`); hearing = spatial-hash sphere query with salience (`:761-781`); analytic ray-capsule
hit detection off the spatial hash (`:693-731`). Regions = the 19 PEI navmesh pocket AABBs
(`game/ZombieDirector.cs:91-96`), hot when a player is within `HotMargin` (forced ≥ `NearRange`
96 m at `ZombieSim.cs:349`).

### 1.3 The honest gap list — the rework is not just pathfinding

Under `--newzombies`, the sim's gameplay surfaces have **zero callers in `game/`**:

- `ZombieSim.Hear` — never called. `SoundBus.Emit` targets the `"zombies"` node group
  (`game/SoundBus.cs:26`), which the director deliberately does not join
  (`game/ZombieDirector.cs:24-28`). **Gunshots are inaudible to new zombies.**
- `ZombieSim.Raycast` / `.Damage` — bullets still resolve against legacy `ZombieController`
  colliders (`game/PlayerController.cs:2054-2055`). **New zombies are unshootable.**
- `ZombieSim.Attacks` / `.Deaths` — never drained. **New zombies deal no damage and leave no
  corpses.**
- `ZombieNetSync` iterates the `"zombies"` group (`game/ZombieNetSync.cs:49-51`). **New zombies
  never replicate.**
- The dedicated server ignores `ZombieDirector.Enabled` entirely and always builds `ZombieField`
  (`game/WorldBuilder.cs:676-683`).
- Only kind "normal" exists (`ZombieDirector.cs:98-99`); legacy has 6 specialities with behaviours
  (`game/ZombieController.cs:18, 90-96, 383-457`).
- No player↔zombie contact of any kind — no body pool was ever built, so zombies cannot body-block.
  Half of "they get in the way" is missing by construction.
- `SnapToSurface` = terrain height (`ZombieDirector.cs:379-380`), so any zombie on an upper floor
  or bridge is pulled to ground level. (Corridor waypoints carry correct navmesh Y; `Advance`
  throws it away — `ZombieSim.cs:675`.)
- `GodotNavQuery.MapFor` picks the map from the **start** position only
  (`ZombieDirector.cs:353-355`); a zombie outside every pocket falls back to the world map, which
  under `--newzombies` has no regions → guaranteed path failure (and before the backoff, a
  guaranteed budget-pinning flood).

So: "rework the AI and pathfinding" has to end in a **cutover** (combat, hearing, MP, dedicated,
specialities, deletion of the legacy system), not just a better path query. The staging in §14
treats those as first-class.

### 1.4 Why zombies "make dumb decisions" today — specific mechanisms [SOURCE]

1. **They stand still while waiting for the path budget** (`ZombieSim.cs:440`: no corridor →
   `continue`). With 8 queries/tick and a woken horde, the back of the queue waits ~a second,
   motionless, in plain sight.
2. **The corridor is truncated at 8 waypoints** (`ZombieSim.cs:107,641`) — a long chase is a
   stutter of walk-8-waypoints → stand → re-query.
3. **No avoidance between waypoints**: nothing steers around other zombies (they stack into one
   column), dropped deployables, vehicles, or props between corridor points.
4. **Any despawn drops the whole path queue** (`ZombieSim.cs:285`, `DropQueue`) — under corpse
   churn, waiting zombies repeatedly lose their place.
5. **The queue is FIFO** — an idle wanderer's repath is served before a zombie actively chasing the
   player.
6. **Upper-floor Y bug** (§1.3) reads as "zombie sank into the floor / walks under the building".
7. Investigation targets are exact — a horde hearing one shot converges on a laser point and forms
   a queue, instead of spreading like a search party.

Fixing this list is Stage 1 and costs nearly nothing per tick. None of it requires new
architecture.

---

## 2. What Project Zomboid actually does (verified against B41.78/B42 decompile + Indie Stone blogs)

The full sourced findings are long; this is the load-bearing subset. **[SOURCE]** throughout except
where marked.

**World model.** Tiles ≈ 1 m. Chunks: 10×10 tiles (B41), 8×8 (B42). Cells: 300×300 tiles (B41),
256×256 (B42). ~19×19 chunks stream around each player. A chunk owns its squares, nav/collision
sidecar, a per-chunk `SoundList`, loot/corpse state.

**Pathfinding (`PolygonalMap2`, 8,290 lines; B42 = same design ported to native C++).**
- The graph is the **tile grid itself** plus visibility graphs built around *vehicle clusters*
  (arbitrary-rotation polygon obstacles), plus explicit stair nodes. It is NOT a navmesh, and NOT
  per-chunk polygon merging.
- Before A\*: a straight **line-clear test**; if clear, the path is `[start, target]`. Most paths
  in open ground never run A\* at all.
- **Async worker thread** ("PolyPathThread") at 20 Hz processing **at most 2 path requests per
  pass** (~40 paths/sec ceiling for the whole game), A\* capped at 5,000 steps.
- **3-tier priority queue**: players first, then zombies *with a target*, then everything else.
  One outstanding request per mover.
- **While waiting**: keep walking toward the previous "next point" if one exists, else stand; a
  watchdog converts "moving but not progressing" into failure; failure falls back to straight-line
  walk + thumping the obstacle. PZ zombies are *allowed to be dumb* — B42 made it canon (some
  zombies "not intelligent enough to go around an obstacle, and choose to slam into a fence").
- Paths cannot exceed the streamed area — the nav data only exists for loaded chunks.
- Doors/windows/player walls are **path-traversable edges flagged thumpable** for zombie requests —
  a zombie paths *through* a barricade and attacks it on arrival at the edge.

**Population (native `ZombiePopulationManager` since B38).**
- Real `IsoZombie`s exist only in loaded chunks. Everything else is a **virtual zombie: an
  individual point record** (x, y, facing, outfit id, state flags, current walk target) advanced by
  the native sim — *not* an aggregate blob. Per-cell (B41) / per-chunk (B42) desired-vs-current
  counts sit on top for respawn bookkeeping.
- Real→virtual on chunk unload (record serialized, walk target preserved); virtual→real in batches
  on stream-in, materialized *moving* toward their preserved target; no spawns in player view; no
  respawn in chunks seen within the last game-hour.
- Migration: intra-cell redistribution every `RedistributeHours` (12); mass migration is driven by
  **metagame sounds** (helicopter: a moving 500-tile-radius sound source; daily random
  gunfire/dog events). Virtual zombies follow the last heard sound up to `FollowSoundDistance`
  (100 tiles).
- Even real zombies are LOD-throttled: full / ½ / ¼ / ⅛ / 1/16 update rates by distance +
  visibility (`MovingObjectUpdateScheduler`). MP hard-caps 500 real zombies per client.

**Sound (`WorldSoundManager`).**
- A sound = `{pos, radius, volume, life=1-2 ticks}`. **Radius = reach, volume = attractiveness**
  (they can differ: a siren reaches far but attracts weakly). No occlusion raycasts — attenuation is
  `volume × (1 − d²/r²)` with a flat ×1.2 different-room / ×1.4 outdoors penalty.
- Persistence lives in the **zombie's memory**, not the sound (attract timeout 60, repath delay
  120). A new sound must beat the current obsession ×2.
- Response is **delayed** by `Rand(0,16)` ticks and the investigate target is **fuzzed by
  ±(distance/2.5) tiles** — a horde spreads out around a noise instead of stacking on the exact
  point.
- Only sounds with radius ≥ 50 reach the virtual-zombie sim.

**What PZ deliberately does NOT simulate** — the actual cost secret: no per-zombie senses or paths
off-screen; no acoustics; population is bookkeeping; pathfinding is budgeted (40/sec!) and failure
is an accepted, visible behaviour, not an error.

### 2.1 Port / don't-port table

| PZ idea | Verdict for us | Why |
|---|---|---|
| Virtual zombies as individual point records + count bookkeeping | **PORT** (§9) | Cheap (PEI cap sum is 359 zombies; even 5k records at 1 Hz is noise), preserves "the horde that wandered is still there", zero wire impact |
| Real/virtual materialization at a radius, batched, out of view | **PORT** (§9) | It is the whole answer to "map-wide AI" outside the 19 pockets |
| 3-tier priority path queue (player-target > aggro > other) | **PORT** (§6.4, Stage 1) | Directly fixes dumb-decision #5; ~30 lines |
| "Keep walking toward last point while pathfind pends" | **PORT, guarded** (Stage 1) | Fixes dumb-decision #1; guard = only when the target is currently *visible* (our LOS bit), so the shamble is usually walkable |
| Sound = transient event + zombie memory + salience beat-by-×2 | Already have ≈ this (`ZombieSim.cs:459-478`) | Keep; wire it up (it currently has no caller) |
| Investigate-target fuzz ∝ distance + reaction delay | **PORT** (§8, Stage 1) | The single cheapest "feels alive" win in the whole plan; ~10 lines |
| Radius ≥ threshold sounds reach the meta layer | **PORT** (§8) | Gunshots/explosions move hordes; footsteps never touch the coarse layer |
| Straight-line "line-clear" test before A\* | **PORT** (§6.3, Stage 3) | Most wilderness/open-lot paths become free |
| Grid/tile world as THE pathfinding graph | **REJECT** | We already own a baked Recast navmesh that solves doorways at CellSize 0.2 (`ZombieNav.cs:29`); rasterizing a second world representation buys nothing it doesn't already give (§3, option b) |
| Vehicle-cluster visibility graphs | **REJECT** (accept dumbness) | PZ needs them because vehicles are persistent path-blocking obstacles on a grid. Our vehicles are few and mobile; local avoidance (§7) + capsule push covers the perceivable part |
| Room-based sound muffling | **REJECT** | We have no room concept; a flat radius model is imperceptibly different in an outdoor-dominated game |
| Thumpable doors/barricades as path edges | **DEFER** (noted §6.6) | The right long-term answer for base raiding; needs the deployable system in the loop, not this rework |
| Native/C++ offload, worker thread | **DEFER** | Godot queries are thread-safe (§0) so the option stays open; determinism and L0 testability argue for exhausting cheap-on-main-thread first |

---

## 3. The three options, compared honestly

**(a) Keep zones + wandering-horde layer.** Keep the 19 pocket regions exactly as the fine-nav +
spawn substrate. Add a horde/virtual layer on top: hordes as coarse moving entities that resolve
into individually-pathing zombies near a player. Cheapest to build; zero wire change; zero risk to
the shipped pocket-split. Weakness: with *only* pockets as the world model, the horde layer has no
answer for "where can a horde walk in the wilderness" (pockets cover towns, not the island) and no
substrate for "hear noise at cell 32,22" — you end up hanging horde state off an ad-hoc point set
and re-inventing half a grid anyway.

**(b) Full PZ-style cell/chunk rework.** Rasterize the world into tiles/chunks, path on the grid
(coarse A\* between chunks, fine A\* within), population per chunk. This is PZ's architecture — and
it is the right architecture *for PZ*, because PZ's world **is already a tile grid**: walkability
per tile is free, the graph is the world. For us it means: a second world representation (rasterize
every prop/building/cliff into tiles at some resolution), kept correct against the real colliders
forever; a doorway-fidelity problem the navmesh already solved (≈1 m doorways need ≤0.5 m tiles
*plus* erosion handling — the exact problem `CellSize = 0.2` was tuned for, `ZombieNav.cs:27-29`);
and a rewrite of fine movement that today demonstrably works. Cost: the largest of the three by a
wide margin. Gameplay delta over (c): approximately zero — nothing a player can perceive
distinguishes "fine path on tiles" from "fine path on the existing navmesh". **Rejected** on the
stated objective function. (If a future map is procedurally generated *as a grid*, revisit.)

**(c) Hybrid — keep zones for fine nav, add a thin coarse cell layer for everything map-wide.**
Pockets remain the fine layer (baked navmesh, per-pocket maps, retail spawn/difficulty data). A
32 m cell grid over the whole island becomes the *coarse* world model: terrain walkability, noise
heat, horde presence. The horde/virtual layer from (a) runs on the cell grid instead of on ad-hoc
points. Cost over (a): one small engine-free class (~64 KB of state, a few hundred lines) plus a
load-time walkability bake from the heightmap we already sample (`Terrain.SampleHeight`,
`game/Terrain.cs:298`). Buys exactly the two things (a) lacks.

**Decision: (c),** which is honestly "(a) plus the minimal grid (a) turns out to need". Explicitly:
strawberry's "keep zones and add a wandering-horde layer" instinct **wins** over the full rework —
the cell grid is adopted only as the horde layer's substrate, not as a pathfinding replacement.
"Ditching the zones entirely" is rejected: the zones ARE retail data (per-pocket caps, difficulty
GUIDs, spawn toggles — `ZombieNav.cs:12-22`) and the walls of the fine-nav cost fix. What changes
is their *role*: zones stop being the AI's whole worldview and become plumbing; the cell layer
becomes the map-wide worldview; hordes become the map-wide population.

Also rejected along the way:
- **Godot `NavigationLink3D` stitching** of pockets into one map — reinstates the O(total) scan
  (§0.3).
- **Per-zombie `NavigationAgent3D`** (the legacy model) — this is the system being deleted; agents
  poll `GetNextPathPosition()` per tick per zombie and path on the world default map
  (`ZombieController.cs:114-118, 197`).
- **RVO/ORCA avoidance** (Godot's `AvoidanceEnabled`) — solves a formation-quality problem nobody
  asked about, at per-agent engine cost; separation steering off our own spatial hash is ~free and
  is all a shambling horde visually needs (§7).
- **Threaded Godot queries as the primary strategy** — viable (§0), but it adds async completion to
  a deterministically-tested sim to rescue a query cost that two cheaper stages (per-pocket maps,
  then owning A\*) reduce by ~1000× total. Kept as a documented fallback.

---

## 4. Recommended architecture, one screen

```
                          ┌──────────────────────────────────────────────┐
 map-wide (coarse)        │  ZombieCells   (core/UnturnedSim, NEW)       │
 1 Hz, engine-free        │  32 m cells over the level bounds:           │
                          │  walkable | pocketId | noiseHeat | counts    │
                          │                                              │
                          │  ZombieWorld   (core/UnturnedSim, NEW)       │
                          │  virtual zombie records + horde groups;      │
                          │  drift toward heat; conservation bookkeeping │
                          └───────────────┬──────────────────────────────┘
                                          │ materialize ≤ ~224 m / dissolve ≥ ~256 m
                          ┌───────────────▼──────────────────────────────┐
 active bubble (fine)     │  ZombieSim     (EXISTS)  rows, tiers,        │
 50 Hz sim, engine-free   │  intent, corridor-follow, combat, hearing    │
                          │  + separation steering (NEW §7)              │
                          │  + 3-tier budgeted path queue (NEW §6.4)     │
                          └───────┬──────────────────────────┬───────────┘
                                  │ IZombieNavQuery          │ IZombieLineOfSight
                          ┌───────▼───────────┐      ┌───────▼──────────┐
                          │ per-pocket paths  │      │ physics ray      │
                          │ NOW: Godot maps   │      │ (staggered §8)   │
                          │ LATER: own A*+    │      └──────────────────┘
                          │ funnel over the   │
                          │ exported bake     │
                          └───────────────────┘
```

The sim boundary changes in exactly one way: **rows stop being the whole population.** A row exists
only inside the active bubble around players; the rest of the population lives as virtual records
in `ZombieWorld`. Everything already built on rows (tiers, combat, replication) is untouched — there
are just fewer rows.

---

## 5. World model

### 5.1 Cells

- **Size: 32 m.** Grid dimensions derived from the loaded terrain bounds at build time (PEI ≈ a few
  1024 m tiles — `game/Terrain.cs:15`; grid lands around 64×64–96×96 cells; even a hypothetical
  8192 m world is 256×256 = 65k cells ≈ 1 MB — irrelevant either way).
- **A cell owns** (struct-of-arrays in `ZombieCells`):
  - `walkable: byte` — bit 0 land-walkable (slope from heightmap ≤ 55° to match
    `AgentMaxSlope`, `ZombieNav.cs:146`, above water level), bit 1 water. Baked once at load
    from `Terrain.SampleHeight` samples (say 4 per cell edge); static thereafter.
  - `pocketId: sbyte` — which of the 19 pockets covers this cell's centre, −1 for wilderness.
    (Load-time assert: pocket AABBs that overlap get logged loudly — `MapFor` returns first-match
    today, `ZombieNav.cs:81-91`, and an overlap would silently pick the wrong map. **[ASSUMED
    disjoint — verify at load.]**)
  - `noiseHeat: float16` — decaying attraction (§8.3).
  - `virtualCount: ushort` — bookkeeping mirror of `ZombieWorld` (for the F3 overlay + respawn
    accounting), not authority.
- **Is "hear noise at cell 32,22" the right granularity? Yes — for this layer only.** At the ranges
  the coarse layer serves (>96 m, beyond `NearRange`), position precision below ~30 m is
  imperceptible: PZ *deliberately fuzzes* investigate targets by ±distance/2.5 — at 80 m that is
  ±32 m, exactly one cell. Fine-grained hearing (the footstep-behind-you case) stays the existing
  exact sphere query (`ZombieSim.Hear`) inside the active bubble. Two scales, each at the fidelity
  its consumers can perceive.

### 5.2 Pockets (kept, demoted)

Pockets keep three jobs: the fine-nav bake unit (per-pocket Godot map / per-pocket exported graph),
the retail spawn data carrier (caps, difficulty, `SpawnZombies`, `HyperAgro` — `ZombieNav.cs:12-22`),
and the hot/cold region partition for tiering (`ZombieRegions`). They lose the job of being the
AI's entire world: a zombie outside every pocket is now a normal, navigable citizen of the cell
layer instead of an orphan (`stats.Orphan`, `ZombieSim.cs:65`).

---

## 6. Pathfinding

### 6.1 The layer split and the one rule

- **Fine paths never cross a pocket boundary.** Inside a pocket: navmesh corridor (Godot per-pocket
  map now; own A\* later). Outside pockets: terrain steering on the cell layer (§6.5). This is how
  constraint 2 (Godot cannot stitch maps) is confronted: **we never ask Godot to** — pockets are
  disjoint islands by construction, and inter-pocket travel is the coarse layer's job at coarse
  fidelity.
- Crossing INTO a pocket: steer by cells to the pocket AABB, snap onto the navmesh at entry, then
  fine-path. Crossing OUT: fine-path to the corridor's last on-mesh waypoint, then cell-steer.
  The stitch point is simply "where the navmesh ends"; no portal precomputation is needed because
  wilderness is open ground — the only obstacles that matter there are terrain slope and water,
  which the cell mask carries. **[ASSUMED: pocket boundaries are open terrain, not walls — PEI
  pocket bounds are town-sized boxes in fields. Stage 4 verifies with the overlay render.]**

### 6.2 Who owns the graph

- **Coarse graph: us.** Built at load from the heightmap; static (deployables and vehicles do not
  affect 32 m walkability). Cost to build: one pass of heightmap samples, tens of ms, once.
- **Fine graph: Godot today, us in Stage 3.** Stage 3 exports each pocket's baked triangles at
  `--bakenav` time to a flat engine-free file next to the `.res`
  (`game/content/navmesh/pei_pocket_N.navbin`: vertices, triangle indices, adjacency, plus a
  16-cell point-location grid). `core/UnturnedSim/PocketNav` loads it and provides:
  `PointToPoly` (grid-accelerated point location — the thing Godot lacks), `SnapToMesh`, A\* over
  triangle adjacency with `path_search_max_polygons`-style caps, and a funnel pass producing the
  same ≤N waypoint corridor shape `IZombieNavQuery` already promises. Kept current: the bake is
  already offline-only (`--bakenav`, `ZombieNav.cs:109`); the export rides the same command, so
  "keep the graph current" costs nothing new.

### 6.3 Cost model and targets

| Query | Today (measured/projected) | After Stage 3 |
|---|---|---|
| Endpoint resolution | O(pocket polys) scan ×2, ≈ 0.19 ms **[INFERENCE]** | O(1) grid lookup, ~0.5 µs |
| Corridor reset | O(pocket polys) memset-ish | none (open list is per-query, sized by expansion) |
| A\* | O(expanded), capped 4096 | O(expanded), capped ~512, expansion counter **asserted at L0** |
| Straight-line pre-test | n/a | segment walk over tri grid, ~2 µs; most open-ground paths end here |
| Total, typical 30 m town path | ~0.2 ms | **10–30 µs target** |

With Godot queries at ~0.2 ms the budget of 8/tick ≈ 1.6 ms — livable but it caps how many zombies
can *actively* chase. At 10–30 µs the budget ceases to be a gameplay constraint (64/tick ≈ 1–2 ms
would be affordable, though unnecessary). **Stage 3 is contingent**: if Stage 0 measures ≤0.2 ms
and Stage 1's queue fixes make 8–16/tick feel fine, Stage 3 defers indefinitely — that is the
objective function working as intended. The plan builds Stage 2 (the read-only tri index) either
way, because avoidance and the Y fix want it (§7), and Stage 3 becomes a ~400-line increment on top
of Stage 2 whenever the measurement says so.

Immediate cheap wins regardless (Stage 0): switch `MapGetPath` → `query_path` with
`path_search_max_polygons ≈ 512` and `path_search_max_distance` set — caps the unreachable-flood
worst case *below* the backoff, per §0.

### 6.4 The queue (Stage 1, engine-free)

Replace the single FIFO ring (`ZombieSim.cs:139-141, 618-649`) with three rings drained in strict
priority, PZ-style: **(1)** rows in `Pursue`/`Attack` with a visible player, **(2)**
`Investigate`, **(3)** everything else. Plus: corridor-continuation requests (spent an 8-waypoint
corridor mid-route) enqueue at the front of their ring; `Despawn` patches queue entries instead of
dropping the whole queue (`DropQueue`, `ZombieSim.cs:285,864` — dumb-decision #4); raise
`MaxWaypoints` 8 → 16 (96 B/row, halves long-chase requery rate). All L0-testable with
`RecordingNav` (`tests/UnturnedSim.Tests/ZombieMovementTests.cs:18-35`).

### 6.5 Off-mesh movement (wilderness)

Virtual zombies/hordes move cell-to-cell (§9) — no per-entity pathfinding at all, greedy step
toward target cell through walkable neighbours, one-step sidestep on block; cells are 32 m so even
"wrong" greedy choices are invisible. A *materialized* (row) zombie outside a pocket steers
straight toward its destination with terrain `SnapToSurface`, deflected by the cell water/slope
mask and separation (§7). No A\* in the wilderness: PEI's wilderness has no structure worth
A\*-ing around, and the coarse layer already routed the horde at cell scale. **[ASSUMED: adequate
for PEI; a future map with walled wilderness compounds would need pocket coverage there — which is
a content decision (add a pocket), not an architecture change.]**

### 6.6 Deferred, on the record

PZ's thumpable-edge trick (path *through* a barricade, attack it on arrival) is the correct future
answer to "zombies vs player bases" and would slot in as a per-edge flag in the Stage 3 graph +
an attack-the-blocker state. It is out of scope here because it depends on deployables entering
nav data; noted so nobody designs it out.

---

## 7. Local obstacle avoidance (currently: none)

Three mechanisms, all engine-free, all Stage 1–2, in cost order:

1. **Separation steering** (zombie↔zombie): for each due Close/Near row, one
   `ZombieSpatial.QuerySphere` (radius ~1.2 m) — the hash is already rebuilt every tick
   (`ZombieSim.cs:356`) — accumulate a capped push away from neighbours, blend into the corridor
   direction. Kills the single-file conga line and the stand-inside-each-other pile-up, which is
   most of what "they don't avoid obstacles" looks like in practice with a horde. Cost: ~1–2 µs per
   due row. L0: two zombies sent through one doorway end up shoulder-to-shoulder, not co-located.
2. **Stay-on-mesh clamp**: separation must not push a zombie through a wall. Until Stage 2, clamp
   the separation displacement to the corridor's lateral margin (±~0.6 m — inside `AgentRadius`
   erosion). After Stage 2, clamp = `PocketNav.SnapToMesh` (exact, µs, and fixes the **upper-floor
   Y bug** by replacing terrain-height snap with mesh snap; interim Stage 1 fix: lerp Y along the
   corridor segment instead of `SnapToSurface` — `ZombieSim.cs:675`).
3. **Vehicle/prop avoidance**: vehicles get a repulsion capsule sampled from the vehicle registry
   (few entries, distance check only for rows within Near); static props are already excluded by
   the navmesh (they were baked as colliders — `ZombieNav.cs:147-148`), so between-waypoint prop
   collisions only occur off-corridor, which the clamp prevents.

**Player body-block** (the other half of "they get in the way"): no bodies. In the player's own
movement code, apply an analytic capsule push against nearby zombie rows (SP/host: read
`ZombieSim` + `ZombieSpatial` directly; remote client: read its zombie *puppet* replicas). Because
on-foot position is client-authoritative inside the envelope (CLAUDE.md, MP §), a client-side push
IS the authoritative result, envelope-legal by construction (push speeds ≪ 8.75 m/s). No wire
change. L1: walk into a wall of 5 zombies → net displacement bounded; walk around them → pass.

**Rejected:** RVO (§3), swept bodies (the design premise this rewrite exists to remove —
`docs/ZOMBIE_REWRITE_PLAN.md` §2), physics-engine anything.

---

## 8. Sensing and noise

### 8.1 Sight

Keep cone + range prefilter + confirming ray (`ZombieSim.cs:529-555`), with three changes:

- **Stagger the rays**: full LOS check at ~10 Hz per zombie (slot-phased like `IsDue`), holding the
  last verdict in between. A zombie noticing you ~50 ms later is imperceptible; ray count drops
  ~5×. The `rays` counter (`ZombieDirector.cs:173`) already exists to verify.
- **Port sneak**: legacy halves `SightRange` for non-sprint stances (`ZombieController.cs:523-524`);
  the new sim ignores stance entirely — a parity regression players will absolutely notice. Add
  stance to the player array the director already fills (`GatherPlayers`,
  `ZombieDirector.cs:179-187`).
- Keep `GodotLineOfSight` as-is (world-geometry mask, 95% ray — `ZombieDirector.cs:328-338`).

### 8.2 Hearing, near (active bubble)

`ZombieSim.Hear` is correct and tested — it just has no caller (§1.3). Wire `SoundBus.Emit` →
`ZombieDirector` → `sim.Hear(pos, loudness)` with the legacy loudness table
(`game/SoundBus.cs:12-18`). Then add the two PZ humanizers, both in `UpdateIntent`
(`ZombieSim.cs:459-478`): **reaction delay** (state flips to Investigate after a per-row 0–0.3 s
slot-phased delay) and **target fuzz** (investigate `_dest` = heard position + deterministic jitter
∝ distance/2.5, seeded from row slot — no RNG in the sim, keep it reproducible). L0: a horde
hearing one shot converges to a *spread* with >N m dispersion, not a point.

### 8.3 Hearing, far (cell layer)

`SoundBus` events with loudness ≥ 32 m (gunshot 48, explosion 64, horn 32 — footsteps never) also
splat `noiseHeat += volume` into cells within a *meta radius* (≈ 4× loudness — the "half the town
heard that" scale), decaying exponentially (half-life ~30 s). Virtual groups drift toward the local
heat gradient, capped at a `FollowSoundDistance`-style limit (~150 m) so one shot doesn't drain the
whole island. No occlusion — flat radius, per the port table. This is the mechanism that makes
"shooting in a town has consequences" true at map scale, which is the meaningful gameplay effect
the coarse layer exists to buy.

---

## 9. Population and migration (the horde layer)

`ZombieWorld` (core, engine-free, stepped at 1 Hz from the director/dedicated server):

- **Virtual zombie record** = `{posX, posZ (cell-resolution + jitter), kind, groupId, homePocket}`
  ≈ 12 B. The whole of PEI (~359, the retail pocket-cap sum) is ~4 KB. PZ-style individual records,
  not blobs — kills persist per-zombie, mixed-kind hordes work, and materialization is trivial.
- **Group** = `{targetCell, mode: Loiter|Drift|Converge, memberCount}`. Groups move; members follow
  with jitter. Modes: Loiter (stay near `homePocket`, retail behaviour), Drift (random-walk through
  walkable cells — the "wandering horde"), Converge (following noiseHeat).
- **Materialize** when a virtual record is within ~224 m of any player: `ZombieSim.Spawn` at the
  record's position (snapped to mesh if in-pocket, terrain otherwise), amortized ≤4 spawns/tick to
  avoid a spike, never inside the player's view frustum when avoidable **[ASSUMED cheap: dot
  product against camera forward; verify it doesn't read as pop-in at 200 m — it won't, NearRange
  is 96 m]**. **Dissolve** beyond ~256 m: row → record (position, kind preserved; corridor
  dropped). Hysteresis band prevents thrash; both radii sit *outside* the MP relevancy ring (192 m,
  `game/DedicatedServer.cs:139`) so a client's puppets appear/vanish strictly inside the
  materialized set — **no wire change and no visible pop-in** (§11).
- **Conservation is the invariant**: rows + records per pocket/area = the population; nothing
  spawns or vanishes except through the spawn/respawn rules (retail respawn semantics stay:
  `RespawnDelay 40 s`, caps per pocket — `ZombieField.cs:24-28,76`). L0-tested as a strict
  bookkeeping property under randomized materialize/dissolve churn.
- **What simulates when nobody is looking**: group drift on cells at 1 Hz, heat decay, respawn
  timers. No senses, no paths, no per-record movement solve — a group is one greedy cell step; its
  members' records only get touched when their group moves a cell (batch add). Cost target:
  <0.05 ms per 1 Hz step at PEI scale, <0.5 ms at 10× PEI. This *replaces* the current design where
  every zombie is always a row and the whole roster is re-classified every tick
  (`ZombieSim.cs:363-382`) — see cut list §13.
- **Tier table impact**: `Ambient` (stride 50) becomes nearly empty — ambient zombies are records
  now. The sim keeps the tier for the transition band (materialized but far), so no behavioral
  change inside the bubble.

This section is strawberry's "wandering horde mechanics as a separate layer" — it is exactly that:
`ZombieSim` does not know hordes exist; it just gains and loses rows.

---

## 10. The fixed-50 Hz / "talk directly, avoid interpolated positions" requirement — audit

Mostly **already satisfied**; stating precisely what is and isn't, so nothing gets "fixed" that
isn't broken:

- Physics ticks at 50 Hz (`game/project.godot:23`) and the zombie sim steps inside
  `_PhysicsProcess` (`ZombieDirector.cs:129-157`). Fixed-rate requirement: **met**.
- The sim reads `Player.GlobalPosition` inside the physics tick (`ZombieDirector.cs:183`) — with
  `physics_interpolation=true` (`project.godot:24`), interpolation affects only what the *renderer*
  draws; `GlobalPosition` during a physics tick IS the tick-authoritative transform. Zombie attacks
  resolve against those same arrays the same tick (`InAttackReach`, `ZombieSim.cs:557`). "Talk
  directly, avoid interpolated positions": **already met** for player→zombie sensing/combat.
- **Genuinely to fix** under this heading: **(1)** legacy `ZombieField` streams in `_Process`
  (render frame, `ZombieField.cs:114`) and legacy puppets interpolate in `_Process`
  (`ZombiePuppets.cs:33`) — both die with the legacy system; the new puppet drive moves to the
  physics tick with render interpolation left to the engine. **(2)** Tick *order* must be pinned
  and asserted: players move → zombie sim reads → replication last (the MP tick-order rule already
  says this; `ZombieNetSync` is registered between `net.server.sim` and `net.server.replicate`,
  `game/MpLoopback.cs:294-295` — the director's step must join that ordering explicitly on both
  dedicated and loopback rather than relying on Godot child order). **(3)** Rig views are driven
  from sim rows during the physics tick and then render-interpolated (`Drive`,
  `ZombieDirector.cs:267-275`) — correct; do not "fix". A one-tick sensing lag (zombie reads the
  player's previous-tick position if node order flips) is acceptable *if constant* — the L1 assert
  is on ordering, not on zero-lag.

---

## 11. Multiplayer survival

- **Wire: zero change.** The zombie block stays `netId + pos + yaw + anim + speciality` ≈ 14 B
  dirty, 12.5 Hz, SystemId 3, relevancy ring 192 m
  (`core/UnturnedNet/ZombieReplication.cs:213-220`, `game/ZombieNetSync.cs:19`,
  `core/UnturnedNet/PlayerReplication.cs:18`). Hordes/cells/virtual records are server-only
  concepts; a client only ever sees materialized rows, which replicate exactly as zombies do today.
  `NetProtocol.Version` stays 14 through the entire rework — the cutover ships a new *publisher*
  (iterate sim rows instead of the `"zombies"` node group), byte-identical block format, provable
  with the existing golden/parity tests (`tests/UnturnedNet.Tests/ZombieReplicationTests.cs`).
- Materialize radius (224 m) > ring (192 m) > `NearRange` (96 m): a joining or approaching client's
  relevancy enter-event always finds the row already alive; `RelevancyTracker`'s ack-safe
  enter/exit (`core/UnturnedNet/Relevancy.cs:68-104`) is untouched.
- **Dedicated server**: `WorldBuilder` Dedicated branch gets the director (guarding the
  camera/viewport-dependent view path — `SyncViews` must no-op headless,
  `ZombieDirector.cs:201-204`), `ZombieWorld` steps there too, `IZombieHost.DamageZombie` routes to
  `sim.Damage`, attack events into the external-damage queue (the same server-authoritative path
  deadzones use). Client puppets: reuse the existing puppet rig path but drop the
  `ZombieController`-as-puppet hack for a thin view (the controller dies in Stage 7).
- Zombie damage to players rides the existing `DamagePlayerExternal` server path; bullets against
  zombies go through `ServerCombat` calling `sim.Raycast` (replacing the hardcoded
  0.4 m/0.82-head mirror at `core/UnturnedNet/ServerCombat.cs:104-105` with the kind table —
  removing a duplicated constant that is already a lurking parity bug).

---

## 12. Legacy system disposition: migrate, then delete

Decision: **delete after cutover** (as the original rewrite plan intended), because running both
forever means every combat/net/trap feature is written twice (§1.3 is the proof it already isn't).
The path:

1. Extract `ZombieField`'s spawn planning (`LoadFromPei` + `DebugPlanSpawns`,
   `ZombieField.cs:42-91,188-206`) into an engine-free `ZombieSpawnPlan` in core — also removes the
   director's construct-and-`Free()`-a-legacy-node hack (`ZombieDirector.cs:104-108`).
2. Re-point the ~15 coupling sites (enumerated by grep in the research pass): bullets
   (`PlayerController.cs:2054`), traps (`Deployable.cs:298-334`), roadkill (`Vehicle.cs:292-353`),
   `SoundBus`, `HordeSpawner` (rewrite as sim-spawner; keep `UG_HORDE` perf repro), `DemoDirector`,
   `ServerCombat`, the L1 tests that build `ZombieController` fixtures.
3. Flip the default (`--newzombies` becomes the only path; keep `--oldzombies` for one release as
   an A/B lever, then delete `ZombieController.cs` (719 lines), `ZombieField.cs` (226),
   `ZombiePuppets`' controller dependency, and the `"zombies"` node group contract.

Cost of keeping it instead: permanent double implementation + the dedicated server stuck on the
system this whole document exists to replace. Not close.

---

## 13. Fidelity currently paid for that nobody can perceive (the cut list)

| What | Where | Cut |
|---|---|---|
| LOS raycast per candidate per due tick for every Near zombie | `ZombieSim.cs:550` | 10 Hz staggered + held verdict (§8.1), ~5× fewer rays |
| Every zombie always a row; whole roster region+tier-classified every tick | `ZombieSim.cs:363-382` | Virtual records (§9); rows only inside the bubble |
| Repath every 1.2 s even vs a stationary target | `RepathInterval`, `ZombieSim.cs:103,612` | `DestMovedTolerance` already covers target motion; raise interval to 3 s for Far tier |
| 8-waypoint corridors forcing requery per ~8 waypoints of a long chase | `ZombieSim.cs:107` | 16 waypoints; continuation priority (§6.4) |
| Corpse rows re-scanned every tick for 24 s | `RecycleCorpses`, `ZombieSim.cs:399-409` | fine as-is at current counts — leave it (honesty: it's already noise) |
| Exact investigate targets (laser-point convergence) | `ZombieSim.cs:472` | fuzz is *cheaper* than exact and reads better (§8.2) |
| Godot's unreachable-target full-component flood | §0 | `path_search_max_polygons` cap (Stage 0) |

---

## 14. Staged migration

Each stage ships alone, is valuable alone, and reverts alone (they are additive behind the existing
`--newzombies` flag until Stage 7). Estimated sizes are relative, not calendar.

**Stage 0 — measure the shipped fix; close the holes; cap the flood. (tiny)**
Re-run the F3 scenario: `s.paths` per window, µs/query (add p50/p99 to the overlay — `Prof` already
counts queries, `ZombieDirector.cs:167`). Expect ~0.2 ms/query [INFERENCE §1.1]; **if not, stop and
find out why before anything else in this plan proceeds.** Switch `GodotNavQuery` to `query_path`
with `path_search_max_polygons=512` + `path_search_max_distance`. Fix `MapFor` so an off-pocket
start returns 0 immediately instead of querying the empty world map (`ZombieDirector.cs:353-355`).
Add the load-time pocket-overlap assert (§5.1). Add the L1 scaling probe (§15).
*Value alone*: today's fix verified, worst case capped, regression-guarded. *Revert*: trivial.

**Stage 1 — make them stop being dumb (engine-free, mostly `ZombieSim`). (small)**
The §1.4 list: 3-tier priority queue + continuation priority + queue patched on despawn +
`MaxWaypoints` 16 (§6.4); separation steering with corridor-lateral clamp (§7.1–2 interim);
corridor-segment Y lerp (kills the upper-floor sink); shamble-toward-visible-target while a path
request pends; sight stagger + sneak port (§8.1); hearing wired (`SoundBus`→director→`sim.Hear`) +
fuzz + reaction delay (§8.2). Every item L0-testable with existing fakes.
*Value alone*: this is most of the *felt* "pathfinding sucks" complaint, at ~zero added tick cost.
*Revert*: per-item, each is a small independent diff.

**Stage 2 — own the nav data, read-only. (small-medium)**
`--bakenav` exports per-pocket `navbin` (verts/tris/adjacency/point-grid); `PocketNav` in core
loads it: `PointToPoly`, `SnapToMesh`, segment-walk line test. Replace terrain-snap with mesh-snap
inside pockets; upgrade the separation clamp to exact on-mesh. **L0 tests now run against the real
PEI pocket geometry as data** — the class of "cost lives in the adapter where L0 is blind"
(constraint 6) shrinks by exactly the amount of logic this pulls out of Godot.
*Value alone*: correct Y everywhere, wall-safe avoidance, real-geometry L0 coverage.
*Revert*: `PocketNav` sits behind the same `IZombieNavQuery`/clamp seams; flag off.

**Stage 3 — own the pathfinder (CONTINGENT on Stage 0/2 measurements). (medium)**
A\* over `navbin` adjacency + funnel + straight-line pre-test (§6.2–6.3), replacing `MapGetPath`
for zombie queries. Expansion-count caps asserted at L0 (§15). NavigationServer leaves the zombie
hot path entirely (legacy/`--navpath` untouched on the world map).
*Trigger to build it*: Stage 0 measures >0.3 ms/query, OR Stage 1's raised activity wants a budget
>16/tick, OR the thumpable-edge future (§6.6) gets scheduled.
*Value alone*: queries drop to µs; the O(whole-map) bug class becomes L0-assertable.
*Revert*: same seam; keep the Godot impl compiled behind `UG_GODOTNAV=1`.

**Stage 4 — the cell layer. (small)**
`ZombieCells` bake from heightmap + pocket ids + heat splat/decay + `SoundBus` meta-splat (§5.1,
§8.3). Debug overlay (F3 page: heat + walkability tint) for eyeball verification via the render
harness. Pure L0 otherwise.
*Value alone*: map-scale noise exists (even before hordes, materialized Far zombies can consume
heat as investigate targets — towns react to gunfire town-wide).
*Revert*: nothing consumes it yet but the overlay; delete-safe.

**Stage 5 — the horde/virtual layer. (medium)**
`ZombieWorld` records + groups + materialize/dissolve + conservation invariant + amortized spawn
(§9); director + dedicated step it at 1 Hz; `ZombieSpawnPlan` extraction (§12.1) feeds initial
population from retail data.
*Value alone*: map-wide AI ships here — wandering hordes, gunfire migration, populations that
persist while unobserved. This is the strawberry-visible headline stage.
*Revert*: flag `UG_HORDES=0` degrades to "all zombies are rows" (the current model).

**Stage 6 — combat + MP + body-block parity. (medium)**
Wire `Raycast`/`Damage`/`Attacks`/`Deaths` (bullets via `ServerCombat`→`sim.Raycast`, melee, traps,
roadkill, external-damage, corpse views); player capsule push (§7); new sim-row publisher
(byte-identical block, §11); dedicated server runs the director; puppet view rework. Specialities:
port the 6 legacy kinds as `ZombieKind` rows + behaviour modules (flanker approach-point port from
`ZombieController.cs:548-559`; acid/burner as modules), difficulty GUIDs from `Flags_Data.dat`.
*Value alone*: `--newzombies` becomes a *playable game* instead of a walking-skeleton demo.
*Revert*: per-seam null-defaults, the codebase's standard pattern.

**Stage 7 — cutover and deletion. (small, mostly deletes)**
§12: flip default, `--oldzombies` grace release, delete controller/field/group contract, re-point
the last references, `PROGRESS.md` the decision trail.

Ordering notes: 0→1 are urgent and independent of everything else; 2 before 3 (data before
algorithm); 4 before 5 (substrate before consumer); 6 can start in parallel with 4–5 (different
files); 7 last. If effort must be cut, the minimum shippable arc with meaningful gameplay effect is
**0 + 1 + 6** — no new world model at all, just the existing sim made non-dumb and actually wired
into the game. Stages 4–5 are what "map-wide" costs; they are also the cheapest per unit of
player-visible novelty in the plan.

---

## 15. Testability

- **L0 (the bulk)**: everything in stages 1, 2 (via exported real geometry as data!), 3, 4, 5 —
  queue priority/starvation, separation invariants (never off-mesh, never through a wall on the
  navbin fixture), funnel correctness vs brute-force shortest path on small meshes, heat
  splat/decay determinism, conservation under churn, materialize hysteresis, fuzz dispersion,
  reaction-delay phasing. The sim stays RNG-free (jitter seeded from slot ids) so every test is a
  fixed-step replay, per the existing convention (`ZombieMovementTests.cs:46-49`).
- **L1**: real-NavigationServer path parity for `GodotNavQuery` (existing `NavSandbox` pattern,
  `game/testing/tests/ZombieDirectorTests.cs:18-31`); director wiring (hearing reaches the sim, F3
  legs live); dedicated headless boot with director + hordes; tick-ordering assert (§10.3);
  body-block; the `net.*` suite extensions for the new publisher (parity + late-join, extending
  `ZombieReplicationTests`).
- **L2**: none required — no rendering change. Horde overlay is eyeball-only via the harness.

**The regression guard for THIS bug** (a cheap-looking call that is O(whole map)):
1. **Structural, now (L1)**: a scaling probe that builds two *procedural* `NavigationMesh`es (set
   vertices/polygons directly — no Recast bake, so it's fast) of ~500 and ~32k polygons, runs 50
   `MapGetPath` each, and asserts the time ratio < 8× (the confirmed linear scan shows ~64×). This
   fails if anyone re-merges the pocket maps, adds cross-pocket links, or a Godot upgrade changes
   the cost shape — and it *documents* the mechanism in executable form. Generous bounds; it
   measures a ratio, not an absolute, so lavapipe/CI slowness cancels out.
2. **Counters as contract**: `IZombieNavQuery` gains `QueriesIssued` + `AccumulatedMicros`
   (already half-exists as `TotalPathQueries`, `ZombieSim.cs:197`); the F3 overlay shows µs/query
   permanently. An L1 asserts the counters move when paths are queried — instrumentation that can't
   silently die again (the lesson of `z.sim = 352 ms` being "true and useless",
   `ZombieDirector.cs:146-166`).
3. **After Stage 3 (L0, the real prize)**: assert `ExpandedNodes < K` for a short path on the
   *largest* pocket fixture — O(whole-map) work becomes a deterministic, engine-free test failure
   with no timing in it at all.

---

## 16. Risks, each with the cheapest killing experiment

| # | Risk | Cheapest experiment, before committing |
|---|---|---|
| 1 | Per-pocket split didn't actually deliver (~0.2 ms/query is projected, not measured) | Stage 0 is literally this measurement. One F3 session. If wrong: the mechanism is now source-confirmed (§0), so the discrepancy would be in *our* adapter — profile `QueryPath` in isolation with 1k synthetic calls |
| 2 | ~~Godot scan hypothesis wrong~~ | Settled — CONFIRMED from source (§0) |
| 3 | Separation steering pushes rows off-mesh / through walls at doorway pinches | 50-line L0 on the exported diner-pocket geometry (the doorway that drove `CellSize=0.2`): 20 zombies through the door, assert all end on-mesh. Build the *test* before the steering |
| 4 | Pocket AABBs overlap → `MapFor` first-match picks the wrong map | Load-time assert + one log line (Stage 0). If they do overlap: pick smallest-containing-box, one-line change |
| 5 | Cell walkability from heightmap misclassifies (cliff edges, shoreline) → hordes walk into the sea | Bake + overlay render (`--navshot`-style, island-wide) and eyeball it — the harness exists (`tools/` screenshot front door). One render session |
| 6 | Materialization pops visibly / spikes the tick | L1: spawn-amortization assert (≤4/tick) + a scripted approach at sprint speed toward a 96-member horde; F3 `z.total` during. If spiky: lower the batch, widen the band |
| 7 | Horde layer starves pockets (everyone drifts away) or dogpiles one noise | L0 conservation + drift-cap tests (`FollowSoundDistance` analog); tune Loiter home-bias constant. Pure data tuning, no architecture risk |
| 8 | Wilderness straight-line steering looks dumb around big rocks/cliff faces outside pockets | Accept small dumbness (PZ ships it, canonically); if a spot is bad, that spot becomes pocket content (add a flag bounds) — content fix, not code |
| 9 | New publisher breaks replication parity | The existing loss/reorder/late-join parity test re-pointed at the sim publisher must pass byte-identically *before* the group publisher is deleted (`ZombieReplicationTests.cs:17`) |
| 10 | Stage 3's funnel has edge-case bugs the navmesh already solved (degenerate tris, welded verts) | Brute-force differential L0: on every pocket fixture, 1k random start/goal pairs, assert own-path length ≤ Godot-path length × 1.05 (run Godot side once, store as golden data) |

---

## 17. Budget targets (the numbers to hold the plan to)

50 Hz tick = 20 ms frame budget; zombies get **≤2.0 ms in the worst live scene** (in-town firefight,
~64 active rows, 8+ chasing), **≤0.3 ms ambient** (player in wilderness), on strawberry's box:

| Leg | Budget | Instrument |
|---|---|---|
| `s.paths` (8/tick worst) | ≤0.5 ms Stage 0–2; ≤0.1 ms after Stage 3 | existing leg + new µs/query |
| `s.move` + separation | ≤0.5 ms | existing leg |
| sight rays | ≤20/tick | existing `rays` counter |
| `z.views` (48 rigs) | ≤0.5 ms | existing leg |
| `ZombieWorld` 1 Hz step | ≤0.05 ms amortized | new leg `s.world` |
| cell heat splat (per shot) | ≤10 µs | new counter |
| Materialization | ≤4 spawns/tick, no tick >+0.3 ms | new counter |

Gate stages on these, not on fps (the fps lesson: `docs/ZOMBIE_REWRITE_PLAN.md` §10 — fps moved
30→217 between two screenshots of the same build).

---

## Appendix: source citations for the external claims

- Godot 4.6-stable `modules/navigation_3d/`: endpoint scan `3d/nav_mesh_queries_3d.cpp:237`
  (`_query_task_find_start_end_positions`); corridor reset `:319` + sizing
  `3d/nav_map_builder_3d.cpp:411`; A\* heap loop `:366`; unreachable flood `:402`; closest-point
  scan `:961`; thread-safe slots `nav_map_3d.cpp:859-871` (PR #79577, 4.3); heap open-list
  PR #85965 (4.4); `path_search_max_polygons` default 4096
  `servers/navigation_3d/navigation_constants_3d.h`; open proposal godot-proposals#12679.
- PZ B41.78 decompile (`zombie/vehicles/PolygonalMap2.java`, `PathFindBehavior2.java`,
  `popman/ZombiePopulationManager.java`, `WorldSoundManager.java`, `IsoZombie.java`,
  `MovingObjectUpdateScheduler.java`; B42 `zombie/pathfind/nativeCode/*`, `IsoChunkMap.java`) +
  Indie Stone blogs: "Bring Out Your Zed" (B32 population), Feb-2017 buildstatus (B38 C++ popman),
  Mar-2024 "Zaumby Thursday" (B42 fences/senses); PZwiki Mapping / Noise / Metagame / Helicopter;
  sandbox-option texts (RespawnHours 72, RespawnUnseenHours 16, RedistributeHours 12,
  FollowSoundDistance 100, RallyGroupSize 20).
- Comparatives: Days Gone hordes 50–500, persistent kills (GDC 2021 "Squad Coordination in Days
  Gone"); State of Decay 2 "probability cloud" ambient population (Jason Hail, Game Developer).
