# Zombie rewrite — plan

Requested by strawberry 2026-07-26 after the POI fps hunt. This replaces the zombie
simulation entirely. It is written to be built in phases, each one shippable and gated.

## 1. What we are actually fixing, with the numbers

strawberry's control, same spot, 3p in a vehicle inside a POI:

```
zombies OFF   physics  2.4 ms   217 fps    11 active,  28 pairs,  9 islands
zombies ON    physics 26.8 ms    30 fps    74 active, 192 pairs, 73 islands
```

Zombies add **24.4 ms of physics**. Every profiler tag in `ZombieController` summed is
**4.1 ms** of that. The remaining **20.3 ms (83%)** is the physics server simulating their
bodies, not script.

The mechanism is isolated (`--zbody`, committed): 63 bare kinematic capsules, alternating
moving and parked windows in one process. Parked keeps the body *and* its collider; only
`MoveAndSlide` stops.

```
moving 4.11 -> parked 2.10   (+2.01 ms)
moving 5.08 -> parked 3.11   (+1.98)
moving 3.35 -> parked 0.95   (+2.40)
moving 2.09 -> parked 1.04   (+1.05)
broadphase pairs: 63 either way
```

**The swept motion solve is the cost. The collider existing is not.** That is the single
fact the whole design hangs on.

Second cost, separate and state-dependent: in a POI read with zombies investigating a noise,
`z.point` alone was 7.46 ms/tick — 44% of the frame — nearly all of it navmesh pathing inside
`MoveTo`. Idle zombies cost ~0.024 ms/tick. The AI is cheap until it paths, then it is not.

Not established, so nobody plans against it: the obstacle-mode probe did not reproduce the
20.3 ms (first windows read +8.7 and +11.1 ms, paired second windows flipped sign). Direction
confirmed, magnitude unproven on the ARM box.

## 2. What retail does

`ZombieManager.cs` in the SDK is the reference and it already solves this:

- Zombies are partitioned into **regions derived from the navmesh bounds**
  (`_regions = new ZombieRegion[LevelNavigation.bounds.Count]`).
- A `regionsWithPlayers` set is maintained.
- The per-frame loop is profiled as **`TickZombiesInRegionsWithPlayers`** and iterates *only*
  those regions, calling `OnUpdate()`. Everything else gets a cheaper `tick()`.
- `Zombie.apply()` toggles the collider by state (`GetComponent<Collider>().enabled = !isDead`).
- Movement is frequently a direct `transform.position` assignment, not a swept controller move.

Our port simulates every zombie with a live swept body at all times. That is an
**architectural divergence from the source**, not a tuning problem, which is why tuning it
has not worked.

## 3. Architecture

Three layers, replacing one `ZombieController : CharacterBody3D` that does everything.

### 3.1 `ZombieDirector` — one node, owns everything

- Builds the region partition from the navmesh bounds at level load.
- Maintains `regionsWithPlayers` each frame from player positions.
- Drives simulation: regions with players get the full path, others get the cheap one.
- Owns spawning, despawning, and the view pool.
- Is the only place that knows about frame budgets.

### 3.2 `ZombieAgent` — plain data, no Node

Position, velocity, hunt state, target, last-heard point, health, archetype id, LOD tier,
path cursor. **No engine inheritance.** The AI state machine operates on this.

This is the point of the whole layer split: the state machine becomes **engine-free and
therefore L0-testable**, which fits the existing test tiers. Today the AI can only be tested
by booting Godot.

### 3.3 `ZombieView` — pooled presentation

Rig, collider, animation. Attached to an agent on demand and returned to the pool when the
agent drops tier. A pool, never per-zombie allocation.

## 4. Simulation LOD

| tier | condition | body | movement | AI rate | rig |
|---|---|---|---|---|---|
| `ACTIVE` | player region, ≤ ~40 m | `CharacterBody3D`, swept | `MoveAndSlide` | 50 Hz | full |
| `NEARBY` | player region, ≤ collider radius | collider only, **not swept** | transform along nav path | 50 Hz | anim, draw-culled |
| `DISTANT` | player region, beyond that | none | transform, coarse step | ~10 Hz | none |
| `DORMANT` | no player in region | none | none, or coarse objective advance | ~1 Hz | none |

Only `ACTIVE` pays the swept cost, and only zombies that can actually interact with the
player are `ACTIVE`. That is where the 20.3 ms goes.

**Invariant, stated so it does not get lost: anything renderable must be shootable.** Gun rays
mask the body's layer bit, so the collider radius must be `>=` the render cull radius. Today
the render cull is 90 m; retail's `layerCullDistances[ENEMY]` is 256–512 m. Pick the collider
radius from the render cull, not from convenience, and assert it in a test.

Corner clipping is **not** a risk here, and this is worth recording because it looks like one:
`MoveTo` already steers toward `_nav.GetNextPathPosition()`, not at the player, so movement
already rides the navmesh corridor. With `PathDesiredDistance = 0.8` and agent `Radius = 0.4`
against a navmesh baked at that radius, a transform-move follows the same corridor a swept
move did. The requirement is only that the rewrite keeps following waypoints rather than
switching to steer-straight.

Y comes from the nav path position, not from simulating gravity. Gravity plus sweep is what
`MoveAndSlide` was doing for ground contact; the navmesh already encodes the walkable surface.

## 5. Accepted costs

Decided up front rather than discovered later:

- **Zombies overlap each other and can walk into the player** at non-`ACTIVE` tiers. Retail's
  zombies clump too, so this is source-faithful, but it is a visible change.
- **Vehicle-vs-zombie becomes an explicit overlap check**, not a physics response. Needs
  writing; it is currently free because the bodies collide.
- Anything relying on zombie bodies pushing world objects stops working. Audit before Phase 2.

## 6. Corpses

Currently a dead zombie builds 11 `PhysicalBone3D` ragdoll bodies with self-collision, and
**nothing ever removes them** — players get `_corpse.QueueFree()`, zombies get nothing. They
accumulate for a whole session.

Retail: `RagdollTool.ragdollZombie` ends with `GameObject.Destroy(model, GraphicsSettings.effect)`
— 16±2 s on low, 32±4 medium, 48±8 high, and **0 (instant) at the lowest setting**, jittered
per corpse. Implement that lifetime, pooled, in Phase 3.

## 7. Phases

Each phase is independently shippable and independently gated.

**Phase 0 — extract, no behaviour change.** Lift the state machine out of the node into a
plain `ZombieAgent` + engine-free stepper. Node becomes a thin adapter. Success = the existing
`zombie.*` L1 tests pass **unchanged**, plus new L0 tests covering the state machine directly.
No perf change expected or claimed.

**Phase 1 — director and regions.** Build the partition and `regionsWithPlayers`; every zombie
stays `ACTIVE`. Proves the plumbing without changing simulation. Perf should be flat; if it
regresses, the partition is wrong and it is cheap to find now.

**Phase 2 — LOD tiers.** Non-`ACTIVE` tiers stop sweeping. **This is where the 20 ms goes.**
Gate on the metric in §8.

**Phase 3 — view pool and corpse lifetime.** Pooled rigs and ragdolls, retail-timed despawn.

**Phase 4 — archetypes and behaviours.** Zombie types become data records (health, speed,
sight/hearing ranges, abilities) with small composable behaviour modules keyed by archetype,
replacing branches on `ESpeciality`. **This is the expansion surface**: a new zombie type is a
data entry plus at most one module, not a new subclass or another switch arm.

## 8. How we know it worked

The gate is strawberry's own measurement, because it is the one that found the bug:

- **Primary:** physics ms in the same POI spot, zombies on vs off. Target is that zombies-on
  approaches zombies-off plus a small term proportional to `ACTIVE` count, not to total count.
- **Scaling:** physics ms must be roughly flat in total zombie count and linear only in
  `ACTIVE` count. Extend `--zbody` to report per-tier so this is a headless check, not a
  screenshot.
- **Behaviour:** all existing `zombie.*` L1 tests green, unchanged, after every phase.
- **The invariant:** a test asserting collider radius ≥ render cull radius.
- **Corpses:** a test asserting ragdoll bodies are released within the lifetime window.

Do not gate on fps. fps moved from 30 to 217 between two of tonight's screenshots for reasons
that included location; physics ms with a stated zombie count is the honest number.

## 9. Ownership

- **cow tools:** the AI state machine (their `Zombie.cs`/`AlertTool` port — hearing, vision,
  investigate-last-seen). It is the faithful 4.1 ms and should survive the rewrite intact.
  Also the AI→LOD seam: an agent requesting promotion to `ACTIVE`.
- **tinyclaw:** the simulation layer under it — director, regions, tiers, view pool, movement,
  corpse lifetime, and the measurement harness.
