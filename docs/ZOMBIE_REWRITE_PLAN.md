# Zombies — clean-sheet design

Requested by strawberry 2026-07-26: replace the zombie system entirely. This is designed from
what the game needs, not from what exists. The current implementation is deleted at the end of
Phase 4; nothing here is a refactor of it.

## 1. Requirements

What a zombie system in this game has to do:

1. Populate POIs densely — dozens visible, hundreds alive in a level.
2. Sense the player: sight cone with line of sight, hearing with falloff and salience.
3. Pursue and attack the player, and attack vehicles the player is inside.
4. Route around buildings rather than beelining.
5. Be shootable with per-hit locations, take damage, die, leave a corpse.
6. Support distinct kinds — normal, crawler, sprinter, flanker, burner, acid, mega — that
   differ in stats *and* behaviour.
7. Be server-authoritative and replicate to clients.
8. **Cost approximately nothing when the player is not near them.**

Requirement 8 is the one the old system failed, and it fails structurally, so it is a design
input here rather than an optimisation pass later.

## 2. Design constraints derived from measurement

From the fps investigation, kept because they constrain the design, not because they describe
the old code:

- A swept kinematic move (`MoveAndSlide`) costs roughly **1–2.4 ms per 63 bodies per tick**,
  measured with `--zbody` across four agreeing A/B pairs. A collider merely *existing* costs
  approximately nothing — broadphase pair counts were identical moving vs parked.
- Therefore: **swept movement is a privilege, not the default.** Any design that gives every
  zombie a swept body reproduces the old cost regardless of how clean the code is.
- Navmesh pathing is the other real cost: in a live POI with zombies investigating, pathing
  work alone measured 7.46 ms/tick. Path queries must be budgeted and amortised, not issued
  freely per zombie per tick.

## 3. Core shape

**Zombies are rows in arrays owned by one system. Nodes are transient views granted to the
few that need them.**

```
ZombieSim          the entire simulation. Struct-of-arrays state, one update pass,
                   no per-zombie virtual dispatch, no Node inheritance, engine-free.
ZombieSpatial      uniform grid / hash over zombie positions. Answers "who is near X",
                   "who can hear a shot at X", "what did this bullet hit".
ZombieBodyPool     a SMALL pool of CharacterBody3D. Granted only for melee-range
                   interaction. Scarce by construction.
ZombieViewPool     pooled rigs. Granted by visibility, independent of bodies.
ZombieNet          server-authoritative snapshots per region; clients interpolate views.
ZombieDirector     the Godot-facing node. Owns the sim, drives it, nothing else.
```

The important inversion: today a zombie *is* a physics object that happens to have AI. Here a
zombie is a row of data that may *borrow* a body, a rig, or neither.

## 4. Movement

Position is integrated in the sim and written to a transform. No physics body is involved for
the overwhelming majority of zombies.

- Path: request a navmesh path, cache the corridor, follow waypoint-to-waypoint. Steering is
  toward the next waypoint, never straight at the target, so the corridor keeps them out of
  walls without a swept move enforcing it.
- Y comes from the navmesh surface, not from simulating gravity into a collider.
- Repath is **budgeted globally**: a fixed number of path queries per tick, priority by
  distance to player and staleness. A horde that all hears one gunshot must not issue 60 path
  requests in one tick — they queue and drain.
- Falling, being launched by explosions, ragdolling: those are the exceptions that borrow a
  real body from the pool for the duration.

## 5. Hit detection is decoupled from physics

This is the design decision that removes the old system's worst coupling.

Bullets do **not** rely on every zombie having a collider. The bullet query asks
`ZombieSpatial` for candidates along the ray and tests capsules analytically, then resolves a
hit location against the rig's bone layout for headshots and limb damage.

Consequences:
- "Shootable" no longer implies "has a physics body", so no invariant tying collider radius to
  render distance, and no zombie is ever bulletproof because it dropped a tier.
- Hit detection works identically at 5 m and 500 m.
- Hit detection is **testable without the engine**, because it is a maths query against the sim.

## 6. Simulation tiers

Tiers select *how much thinking* happens, not whether a zombie exists. Every zombie is always
simulated at some rate; none are frozen.

| tier | when | update | movement | body | rig |
|---|---|---|---|---|---|
| `CLOSE` | interaction range of a player | every tick | integrated + borrowed body for contact | pooled | full |
| `NEAR` | player's region, visible range | every tick | integrated, transform only | none | pooled |
| `FAR` | player's region, beyond visible | ~10 Hz | integrated, coarse | none | none |
| `AMBIENT` | no player in region | ~1 Hz | objective-level advance only | none | none |

Regions come from the navmesh bounds. Only regions containing a player run the fast tiers,
which is how a level full of zombies costs nothing while the player is elsewhere.

`AMBIENT` zombies still *exist and move* at coarse granularity, so a horde that wandered
toward a noise is still there when the player arrives. They are not paused.

## 7. Kinds are data

A zombie kind is a record: health, speed, sight range and cone, hearing range, damage, special
ability id, rig/atlas ids, spawn weight. Behaviour beyond stats is a small set of composable
modules — `Pursue`, `Flank`, `Charge`, `SpitProjectile`, `ExplodeOnDeath` — selected by the
record.

Adding a kind is: one data row, and optionally one module. Never a subclass, never a new arm
on a switch. Specialities in the current design are branches scattered through one class; that
is the specific thing this replaces.

## 8. Multiplayer

The sim is server-side and authoritative. Per region, the server sends a compact snapshot of
the zombies a client can perceive; clients hold view-only rows and interpolate. Views, rigs and
audio are client-only concerns that never feed back into the sim.

Designed in from the start rather than retrofitted, because retrofitting authority onto a
system whose state lives in scene nodes is what makes this expensive later.

## 9. Testing

The sim has no engine dependency, so the whole of it is L0-testable — sensing, state
transitions, pathing decisions, damage, death, hit resolution. This is a design goal, not a
by-product: it is why the state lives in arrays rather than nodes.

L1 covers only what genuinely needs the engine: navmesh queries returning real corridors, body
pool grant/release, rig pooling, and the render path.

New harness work: extend `--zbody` into a sim benchmark reporting physics ms and sim ms against
zombie count *per tier*, so the requirement-8 claim is a headless number rather than a
screenshot.

## 10. Success criteria

- **Cost:** with N zombies alive in a level and the player elsewhere, added frame cost is
  approximately flat in N. With the player in a POI, cost is linear in `CLOSE` + `NEAR` count
  only.
- **Fidelity:** sensing, alerting, investigating and attack cadence match the source's
  observable behaviour. Verified against `Zombie.cs` in the SDK, per kind.
- **Correctness:** every zombie is shootable at any range with correct hit locations,
  regardless of tier.
- **Expansion:** adding a new kind touches one data file and at most one behaviour module.

Gate on physics ms and sim ms at a stated zombie count and tier distribution. Not on fps —
fps moved 30 → 217 between two screenshots on the same build for reasons that included where
the camera was standing.

## 11. Phases

Built alongside the current system behind a flag, then the old one is deleted. No phase
attempts to preserve the old implementation.

0. **Sim core.** Arrays, spatial hash, tier assignment, region partition. Engine-free, L0
   tests only. Nothing rendered, nothing playable.
1. **Movement and pathing.** Budgeted path queries, corridor following, tier-rated update.
   Benchmark: N zombies moving with no bodies and no rigs.
2. **Perception and combat.** Sensing, states, damage, analytic hit resolution, death.
3. **Presentation.** View pool, rigs, animation, ragdoll pool with a timed despawn.
4. **Networking, then cutover.** Server authority, client views, flag flip, delete the old
   system.
