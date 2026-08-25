# Zombie AI Redesign — cheap, chunked, flow-field

**Status:** design agreed with master 2026-08-25. Supersedes the old system (removed
`2a4afdd8`, see the zombie-removal notes). Implement phase by phase.

## Why the old one died

Every zombie ran its **own** `NavigationAgent3D` pathfind **and** its own
`CharacterBody3D.MoveAndSlide` sweep **every physics tick**, all N of them at 50 Hz.
In a POI that was ~10 ms/frame at ~46 zombies (the idle MoveAndSlide alone dominated).
Cost scaled with the total zombie count, whether or not a player was near them.

## Core principles

1. **Flow fields, not per-zombie pathfinding.** Many zombies → few players is the
   textbook flow-field case (Supreme Commander 2 / Planetary Annihilation; Elijah
   Emerson, *Crowd Pathfinding and Steering Using Flow Field Tiles*, Game AI Pro).
   Compute **one** field per active region toward a target; every zombie just samples
   its cell → **O(1) per zombie, cost independent of how many there are.** This also
   **replaces the deleted navmesh** — no NavigationAgent3D, no baked pockets.
2. **Chunked LOD sleep.** 64 m chunks over the whole map. A chunk's tier is a function
   of nearest-player distance / visibility. Only chunks near players are simulated; the
   rest are dead data. (Factorio's 32-tile chunks + entity sleeping; RimWorld regions.)
3. **Sight chases, sound lures.** A zombie that **sees** a player chases that player.
   Otherwise it follows the field toward the loudest recent **noise**. Quiet + still
   loses them.

## Chunk grid

- **64 m × 64 m** chunks across the whole map (a POI fits in ~1 chunk, so only a
  handful are ever awake around a player). PEI's playable area → a few hundred chunks;
  nearly all sit FROZEN at any moment.
- Each chunk holds: its zombie store (a plain list; just a **count + last positions**
  when FROZEN), current tier, current field target (player pos or sound point), and an
  alert level + timestamp.

## The four tiers (by nearest-player distance, with hysteresis)

| Tier | Range (approx) | Representation | Tick rate | Cost |
|---|---|---|---|---|
| 🔴 **HOT** | player's chunk, in view/reach (~0–40 m) | full skinned mesh + animation + lightweight collision body | frame/view rate | full — but only the few near a player |
| 🟡 **WARM** | ~1 chunk out (64–128 m) | "ghost": position + velocity only, drifts along the field, **no collision sweep, no animation** | ~6 Hz | cheap |
| 🔵 **COLD** | 2–4 chunks out (128–256 m) | position only; one **coarse step** toward the field target | every ~2–5 s (<1 Hz) | ~free; keeps hordes migrating toward the action |
| ⚫ **FROZEN** | beyond COLD (no player in range) | pure data: count + last positions, **no logic at all** | never | zero |

- **Budget:** ≤ **64 simulated** zombies per player (HOT + WARM), matching retail's
  pocket `maxZombies`. FROZEN is just data → the total map population is unbounded.
- **Hysteresis:** wake radius > sleep radius, so a player pacing a chunk edge doesn't
  thrash tiers. FROZEN → COLD on entry, then COLD → WARM → HOT as they close (and back
  out the other way).

## Flow field (the pathfinding)

- Per active region (the chunks around each player), build an **integration field**: a
  BFS/Dijkstra flood from the target cell outward over a fine grid (~2–4 m cells) where
  cost respects walls — the world collision layer `WorldLayers.World` (the constant that
  outlived `ZombieNav`). Blocked cells are impassable.
- Derive the **flow field**: each cell stores the direction to its lowest-cost neighbour
  (downhill toward the target).
- Zombies sample their cell's direction and move along it. HOT zombies add cheap local
  **separation** (boids-style push-apart) + immediate obstacle avoidance; WARM/COLD just
  take the field direction.
- **Rebuild** only when the target moves enough (crosses a cell) or every N ms —
  amortised across all zombies in the region, never per-zombie.
- Cell size trades cost vs. tightness: coarse enough to be cheap, fine enough to route
  through ~1 m doorways via the flood (tunable; the old navmesh needed 0.2 m cells only
  because Recast erodes in whole cells — a cost-field flood doesn't).

## Targeting: sight + sound

- **Sound field (default, shared).** The field target per region = the loudest recent
  **noise** heard there. Footsteps (quiet, near; scaled by stance — sneaking quieter,
  sprinting louder), gunshots (loud, far; suppressor cuts it). Each noise drops an alert
  (loudness → radius) that **decays over ~seconds** so a horde eventually gives up and
  wanders. Reuses the port's existing footstep/gunshot emit (`SoundBus`) — only the
  zombie-hearing *consumer* was removed, the emit stays.
- **Sight override (HOT only).** Each HOT zombie does a cheap, staggered LoS raycast to
  nearby players. If one is visible within its sight range/cone → it **locks on and
  steers straight at that player**, ignoring the field. Losing LoS → back to the field
  (last sound). Only the few HOT zombies pay for sight.
- **Net:** the zombies you can **see** are chasing you; the ones out of sight are
  converging on your **last noise**; going quiet and still loses them (retail's
  stealth-detection-radius behaviour).
- **Aggro spread (optional, tunable):** a zombie that sees/reaches a player also bumps
  the chunk's alert, pulling nearby WARM/COLD in via the field.

## Wandering

Idle zombies (no target, no live alert) do a cheap per-chunk wander (random drift/mill),
HOT/WARM only. COLD sits between its coarse steps; FROZEN does nothing.

## Cost

- **Old:** N × (pathfind + MoveAndSlide + animation) @ 50 Hz, all N ticking → O(N) with a
  huge constant → ~10 ms.
- **New:** (HOT × full @ view-Hz) + (WARM × cheap @ ~6 Hz) + (COLD × tiny @ <1 Hz) +
  (one field flood per active region per rebuild). HOT + WARM ≤ 64/player; COLD small;
  FROZEN = 0. **Cost is bounded by the near-player simulated count, independent of total
  map population** — a 5,000-zombie map costs the same as a 64-zombie one when only 64
  are near you.

## Build phases

1. **Chunk grid + tiering + cold store.** `ZombieChunkField`: 64 m grid, per-chunk store,
   tier classification by nearest-player distance (+ hysteresis), FROZEN = don't tick. No
   movement yet — spawn/hold/wake only. *Verify:* a tier-count log as a player moves.
2. **Flow field + WARM drift.** Integration/flow flood from a target across the active
   chunks (wall-aware via `WorldLayers.World`); WARM zombies drift along it. *Verify:* a
   ghost horde flows *around* a building toward the target (render: field arrows + points).
3. **HOT promotion.** Near-player zombies get the real skinned mesh + animation + a
   lightweight collision body; sample the field + local separation. The visible, killable
   zombies live here — reuse the ripped character model and the existing melee/gun damage
   paths (the animal-damage code is already the same call). *Verify:* a player is chased
   and can shoot/melee them.
4. **Targeting (sound + sight).** Sound field from footstep/gunshot emits (`SoundBus`)
   with decay; HOT per-zombie LoS override. *Verify:* a gunshot pulls unseen zombies; a
   seen zombie chases; quiet + still loses them.
5. **Spawn budget + population.** Per-chunk spawn from retail's `Spawns/Animals.dat`
   (1,456 real spawn points = the map's actual horde design), capped at 64 simulated per
   player; FROZEN chunks hold potential counts that materialise on wake. *Verify:* the
   whole map populates with ~zero cost while you're away from it.

## Tunables (defaults; settle in playtest)

Chunk 64 m · fine field cell 2–4 m · budget 64/player · WARM ~6 Hz, COLD ~0.3 Hz · sound
decay ~5–10 s · sight range/cone from retail values · hysteresis wake/sleep radii · COLD
coarse-step rate · aggro-spread on/off.

## Non-goals (for now)

- MP replication (server-authoritative horde) — design after SP works; hook the same seam
  the old `ZombieNetSync` used (removed, but the pattern is known).
- Boss / special zombie types — layer on after the base loop is solid.

---
*Refs: Emerson, flow-field crowd pathfinding (Game AI Pro) · Factorio 32-tile chunks +
entity sleeping · Botea et al., HPA\* clusters/portals.*
