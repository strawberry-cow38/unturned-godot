# PREDICTION_GEOMETRY_DIAGNOSIS — the residual pullback at geometry + jumps

Read-only diagnosis + phased fix plan for the post-Part-C residual: strawberry's real-WAN (100+ ms) report of
pullback/"tweak-out" corrections concentrated at **step-ups, sidewalks, tight doorways, thin colliders
(fire hydrant), and jumps**, while open flat ground is now quiet (the Part C WAN baselines pass at
0.570 / 1.041 m/min — `game/testing/tests/NetTests.cs:2670-2676,2774-2783`).

Everything below is grounded in the current `main` code and the on-box retail source
(`~/projects/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/`, cited as `U3:`). No code was changed.

---

## 0. TL;DR — what it is, and the fix ladder (cheapest first)

The residual is **two-body geometry divergence**, exactly as suspected: the client shell and the server
avatar are two independent `CharacterBody3D` + `MoveAndSlide` solves of the same class
(`game/ClientWorldSession.cs:251`, `game/PlayerNetSync.cs:77`), and their **configuration is already
identical by construction** (§3 table — H1 yields no fix). The static world they collide against is also
**already identical** for terrain/objects/roads/trees (§2 — H0 is refuted for statics, with one small real
hole: the local-clock holiday gate). What diverges is the **dynamics at geometry**: the binary 0.5 m
`StepUp` teleport, knife-edge slide-side selection at thin colliders, the per-body headroom stance gate,
corrections applied *through* geometry, and — worst — **jump-takeoff tick skew** amplified by the
coast/repay machinery re-presenting a held jump bit.

Ranked fix ladder (each independently landable; F0 first because it is the teeth for everything after):

| # | Fix | Targets | Cost | Risk |
|---|-----|---------|------|------|
| F0 | Four new geometry WAN baselines (§8) — obstacle courses under `WanLink.Wan`, asserting the same corr-per-minute bars as the flat baselines. Must FAIL today. | all | small (test-only + a tiny static-geometry helper) | none |
| F1 | Jump bit becomes **takeoff-edge**, never repeated: strip `ButtonJump` from every coast/hole-substituted/repaid input, and send it only on the tick the shell's sim actually consumed it grounded | H3 (the worst felt item) | small | low |
| F2 | Avatar honors the wire jump within a grounded-tolerance window (treat the client's takeoff tick as authoritative-if-plausible, don't re-derive it from the avatar's own `_detGrounded`) | H3 | small | low (validated: recent-grounded gate) |
| F3 | Collision-aware correction application: `ApplyNetCorrection` currently bare-writes the capsule through walls (`game/PlayerController.cs:1153-1157`); route sub-snap slices through a swept test so a correction can never push the shell inside a curb/jamb/hydrant (the reconciler's partial-application accounting already supports this — `core/UnturnedNet/Prediction.cs:114-120`) | H2 amplification at all geometry | small | low |
| F4 | Avatar trusts the wire stance verbatim — skip the avatar-side headroom re-gate (`core/UnturnedSim/PlayerStanceSim.cs:33-35`) for `NetAvatar` bodies | H2 (doorways) | small | low |
| F5 | Holiday-gate parity: the server's `activeHoliday` rides the handshake instead of each machine's wall clock (`game/Main.cs:1435-1442`) | H0 (edge, silent collider-set fork) | small | none |
| F6 | `StepUp` de-binarized: sweep for the *actual* step height instead of the fixed +0.5 m pop (`game/PlayerController.cs:1370-1381`), shrinking the divergence when the two bodies disagree from 0.5 m to the real curb height | H2 (curbs/sidewalks) | medium | medium (touches SP feel — gate to `DeterministicGround` bodies if needed) |
| F7 | H4 instrumentation: a `--netlog` counter for coast ticks whose held stance disagrees with the next real input; fix only if it measures | H4 | small | none |
| F8 | **C3 rewind+replay** (retail's `ClientResimulate`) for whatever the baselines still show after F1-F6 — full design in §7 | H2/H3 residue | large | the honest-uncertainty item; spike first |

Recommendation up front: **F1+F2+F3 are the highest felt-value-per-line in the repo right now** and are
independent of C3. C3 (§7) is now *justified in principle* — real geometry testing found what the flat
baselines couldn't, and this class of divergence is exactly what replay exists for — but it should be
**gated on F0's baselines still failing after F1-F4**, because the jump mechanisms in §5 plausibly account
for the majority of what strawberry felt, at ~1/20th the cost.

---

## 1. Architecture confirmation (verified from the code)

The prompt's architecture summary is confirmed exactly; details with citations, since everything after
builds on them:

- **Client local player** = a real `PlayerController` shell (`game/ClientWorldSession.cs:251`,
  `DeterministicGround = true`). Sim order per 50 Hz tick: net pump → `ShellStep`
  (`game/ClientWorldSession.cs:165-166`), which consumes the newest authoritative own-entity sample and
  applies the reconciler's correction *to the node* before the shell's `_PhysicsProcess` runs
  (`:197-206`; SimDriver sits before the session in the tree, `:161-164`). Then it sends this tick's
  captured input + the position claim and records `rec[seq]` (`:216-221`).
- **Reconciler** = `PredictionReconciler`: eased single delta, `DeadZoneMeters = 0.04`
  (`core/UnturnedNet/Prediction.cs:41`), `SnapThresholdMeters = 2` (`:23`),
  `CorrectionRatePerSecond = 8` (`:26`), correction-since-record accounting (`:78-79`).
- **Server avatar** = one `PlayerController { NetAvatar = true, DeterministicGround = true }` per remote
  peer (`game/PlayerNetSync.cs:77`), fed through the `Scripted*` seams (`:161-164`), MoveAndSliding on the
  server's real world on its own physics tick (`:19`). Write-back publishes last tick's post-physics
  transform under the seq of the input that produced it (`:117-140`), through `ServerDrive`
  (`core/UnturnedNet/PlayerReplication.cs:655-666`), with the C2 ack band
  (`ServerTryAdoptClaim`, `:625-649`, `AckBandMeters = 0.08` `:597`) and the C1.5 no-publish-on-stale-seq
  guard (`game/PlayerNetSync.cs:117,141-150`).
- **`IntegrateFlat` is NOT in this loop.** It serves `ServerStep`'s flat demo path, which skips
  `ExternallyDriven` entities (`core/UnturnedNet/PlayerReplication.cs:544-549`) — and `ServerDrive` marks
  every avatar-driven entity `ExternallyDriven` (`:658`). Production players are pure
  two-`CharacterBody3D`.
- **Shared movement model**: `PlayerMovementSim.Step` — stance speed set directly on the horizontal axes,
  jump = `Velocity.y = JUMP` when grounded, gravity integration otherwise
  (`core/UnturnedSim/PlayerMovementSim.cs:21-48`); constants `JUMP = 7`, `GRAVITY = 29.43`,
  `SPEED_SPRINT/STAND/CROUCH = 7/4.5/2.5` (`core/UnturnedSim/PlayerMovementDef.cs:26-35`). **No stamina in
  the movement sim**; sprint is granted by `PlayerStanceSim.Step` only while `stamina > 0.05`
  (`core/UnturnedSim/PlayerStanceSim.cs:30`).
- **The custom geometry logic, identical code on both bodies** (`game/PlayerController.cs`):
  `StepUp` (+0.5 m binary raise when blocked-at-foot-but-clear-raised, `:1370-1381`), deterministic
  ground spherecast `DetGroundCast` (`:1324-1348`), post-move `DetSnapToGround` (`:1354-1368`),
  `FloorMaxAngle = 55°`, `FloorSnapLength = 0.5` (`:1723-1724`), and the per-tick pipeline
  stance-FSM → `StepUp` → `MoveAndSlide` → det-ground recheck (`:2776,2813-2834`). `_detGrounded` is
  computed post-move and consumed by the *next* tick's `Step` (`:1322`, `:2813`) — a deliberate one-tick
  lag, same on both bodies.

So the two-body-divergence framing holds: on flat ground both solves agree to quantization and the C2 band
absorbs the residue (that's why the flat WAN baselines pass); at geometry the solves *bifurcate*, and the
eased reconciler renders the bifurcation as the felt pullback.

---

## 2. H0 — client-vs-server terrain + static-world collision parity (strawberry's suspect): REFUTED for statics, with two real footnotes

Checked first, as instructed. The world is assembled by **one shared code path** for every mode
(`game/WorldBuilder.cs:5-6` "ONE world assembly for SP, client and dedicated server, or the three modes
drift forever") — and the collision-relevant claims hold up line by line:

| Collision source | Dedicated server | Joined client | Verdict |
|---|---|---|---|
| Terrain | `Terrain.LoadMapMerged(mapRoot + "/Landscape/Heightmaps", withCollider: true)` — `game/WorldBuilder.cs:101`, same call, same mode-independent line | identical (same line — the call precedes the mode branch) | **identical**: one merged visual-mesh trimesh on layer 0 (`game/Terrain.cs:307-312`), full resolution, no server LOD/decimation path exists |
| Placed objects (incl. the fire hydrant) | `colliders = mode != WorldMode.Aerial` → trimesh of the visual mesh per unique prop, shared `shapeCache` (`game/WorldBuilder.cs:118,216-231`) | identical (same loop, same flag) | **identical**: same trimesh data, same layer rule (≥5 m opaque → layer 0, small props/glass → layer 6, `:225-226`); the player collides with both (mask `(1<<0)\|(1<<6)`, `game/PlayerController.cs:1718`) |
| Roads (sidewalk substrate) | `RoadField` built in the Dedicated branch (`game/WorldBuilder.cs:460-464`) | `RoadField` built via `BuildRoadsFoliageTrees` (`:245-253,509`) | **identical**: same flat top-ribbon `ConcavePolygonShape3D` collider, `BackfaceCollision = true`, default layer 0 (`game/RoadField.cs:57-65,243`) |
| Trees/rocks | `ResourceField { VisualInstances = false }` (`:469`) | `ResourceField` default (`:264`) | **identical colliders**: `VisualInstances` only suppresses rendering; the trunk cylinder colliders (layer 0) build regardless (`game/ResourceField.cs:19-24,85-101`) |
| Grass/foliage | skipped on dedicated (`:458`) | built | parity-irrelevant: visual-only, no collider (`:216` comment — leaf meshes carry no collider) |
| Water | same `LoadMapMerged` — layer 9 body (`game/Terrain.cs:301-304`) | identical | parity-irrelevant to the capsule (mask 0\|6) |
| World items | server `WorldItem` bodies on layer 7 (`game/inventory/WorldItem.cs:25,145,200`) | replica visuals | parity-irrelevant (mask excludes layer 7) |
| Zombies | real bodies on layer 1 (`game/ZombieController.cs:82`) | collider-less puppets | parity-irrelevant (player mask excludes layer 1; zombies never block the capsule on either side) |
| Deployables | real `Deployable` `StaticBody3D` + box collider, default layer 0 (`game/Deployable.cs:9,72-77`) | `DeployableReplicaView` spawns the SAME `Deployable.Spawn` node (`game/DeployableReplicaView.cs:43`) | **identical** |
| **Vehicles** | real drivable `Vehicle` nodes, `CollisionLayer = bit0\|bit5` (`game/WorldBuilder.cs:324-341`, `game/Vehicle.cs:956-957`) — the avatar **collides** (mask has bit 0) | **no vehicle bodies at all** — mesh-only `VehicleReplicaView` puppets | **REAL MISMATCH — the one true H0 hit.** Out of scope here: folded into the Part A vehicle branch (client-collider parity). Noted for completeness because it produces exactly strawberry's symptom class near parked cars. |

Also verified: no headless/`--dedicated`/`RemoteAvatars` flag gates any *collider* out.
`DedicatedServer.RemoteAvatars` (`game/DedicatedServer.cs:20`) gates only the avatar bodies; dedicated "fx
hygiene" (`game/WorldBuilder.cs:91-95,458`) touches shadows/visuals/grass only. The flat
`WorldBoundaryShape3D` fallback fires only with no map data (`:105-110`) — not the live server
(`UG_UNTURNED_DIR` is set).

**Footnote 1 — the holiday-gate collider fork (real, cheap, fix F5).** `activeHoliday` gates ~285 placed
props *including their colliders* (`game/WorldBuilder.cs:180-191,216-231`) and is computed from each
machine's **local wall clock** (`System.DateTime.Now`, `game/Main.cs:1435-1442`) with a `UG_HOLIDAY` env
override. A client whose local date sits across a holiday-window boundary from the server (timezones on
Dec 6/7, Oct 19/20, Jan 2/3 …), or with a stray `UG_HOLIDAY`, silently builds a **different static
collision set** — and the content-hash join gate does not catch it (it hashes content identity, not this
local-clock decision). Constant-divergence class, exactly what H0 hypothesized — just seasonal. Fix: the
server's `activeHoliday` string rides the handshake/join and the client builds with it.

**Footnote 2 — parity of data ≠ parity of *loaded* data is otherwise structural.** Both sides read the same
`content/objects/placements.txt` + `guid_mesh.txt` + the same mesh files; a client with a corrupted/missing
mesh file would silently skip that prop *and its collider* (`game/WorldBuilder.cs:190-193` `continue` on
missing mesh) — but the content hash covers exactly these files, so the join gate does its job here.

**Verdict:** H0 as a *constant* inchworm engine is refuted — sidewalk/hydrant/doorway colliders exist,
bit-identical, on both sides. Strawberry's sidewalk/hydrant symptoms are H2 dynamics (§4), not missing or
lower-fidelity server collision. The vehicle mismatch is real but already owned by Part A; the holiday fork
is real, cheap to close, and worth F5.

---

## 3. H1 — client-shell vs server-avatar body config parity: IDENTICAL BY CONSTRUCTION (no fix to land)

Both bodies are the **same class through the same `_Ready`** (`game/PlayerController.cs:1714-1739`), so the
diff table below is short on drama — which is itself the finding worth recording, because it retires H1
from the suspect list:

| Physics-relevant property | Client shell | Server avatar | Source |
|---|---|---|---|
| Class / construction | `PlayerController { CaptureMouse=true, DeterministicGround=true }` | `PlayerController { NetAvatar=true, CaptureMouse=false, DeterministicGround=true }` | `game/ClientWorldSession.cs:251` / `game/PlayerNetSync.cs:77` |
| `CollisionLayer` | `1<<3` | `1<<3` | `game/PlayerController.cs:1717` (shared) |
| `CollisionMask` | `(1<<0)\|(1<<6)` | same | `:1718` (shared) |
| Capsule | H 2.0, R 0.35, stance-resized via `UpdateHitbox` | same, driven by the *wire* stance | `:1720-1721,1304-1309,2776-2777` |
| `FloorMaxAngle` | 55° | 55° | `:1723` |
| `FloorSnapLength` | 0.5 | 0.5 | `:1724` |
| `SafeMargin` | Godot default 0.001 (never set — no assignment exists in the file) | same | grep: no `SafeMargin`/`safe_margin` hit |
| `MotionMode` / `MaxSlides` / `FloorBlockOnWall` / `WallMinSlideAngle` | Godot defaults (never set) | same | grep: no hits |
| `PhysicsInterpolationMode` | `Off` | `Off` | `:1726` |
| `StepHeight` | 0.5 (shared const) | same | `:1370` |
| Det-ground consts (`DetCastRadius/Lift/CheckLength/SnapLength`) | 0.349 / 0.10 / 0.03 / 0.6 | same | `:1324-1327` |
| Grounded source | `_detGrounded` (det spherecast) | same | `:2813` (both `DeterministicGround`) |
| Stance source | keys + local stamina through `PlayerStanceSim` | `ScriptedStance` = the wire stance the shell's sim consumed | `:2773-2776` / `game/PlayerNetSync.cs:164`; wire pack `game/ClientWorldSession.cs:217`, `Stance => _move.Stance` `game/PlayerController.cs:1124` |
| Jump source | Space & `!Broken` | `ScriptedJump` = the wire bit (post-Broken, client-side) | `:2787` / `game/PlayerNetSync.cs:163` |
| Vitals/stamina | live (`UpdateVitals`) | frozen at 1.0 (`if (NetAvatar) return;`) | `:1645-1647` — benign today: avatar stance is wire-driven, avatar stamina is never consulted (§6) |
| Tick order vs SimDriver | SimDriver first in tree, body after → input/correction lands before the body's physics | same relative order | `game/WorldBuilder.cs:50-51`, `game/ClientWorldSession.cs:161-164`, `game/PlayerNetSync.cs:78` |
| Render-interp restore | `_Process` lerps, `_PhysicsProcess` restores from `_interpCurr` | `_Process` skipped (`NetAvatar`), restore is a no-op (position never lerped) | `:2506-2508,2721,2833` — no physics effect either side |
| External-move seam | `ApplyNetCorrection` shifts node + interp samples | `TeleportTo` resets interp + velocity | `:1153-1157` / `:94-101`, `game/PlayerNetSync.cs:103-116` |

Two *asymmetries by design* (not config drift) worth naming because they feed H2:

1. **Only the shell gets displaced by corrections** — a nonzero `ApplyNetCorrection` each tick moves the
   shell's collision inputs relative to the avatar's. Benign in the open; at a contact it is a feedback
   term (§4, fix F3).
2. **Only the avatar can be re-gated on stance headroom** *against the wire stance*: the avatar re-runs
   `PlayerStanceSim.Step`, whose ceiling gate can override the scripted stance from its own position's
   shape query (`core/UnturnedSim/PlayerStanceSim.cs:33-35`, query at `game/PlayerController.cs:1383-1397`).
   The client already resolved that gate at *its* position and sent the result; the avatar's second opinion
   at a slightly different position can disagree in exactly the tight-doorway geometry strawberry named
   (speed 4.5 → 2.5 for the disagreement window). Fix F4: `NetAvatar` bodies take the wire stance verbatim.

**Verdict:** H1 confirmed-clean. No per-property alignment fix exists to land — the cheap win the
hypothesis hoped for isn't here, because it was already built (same class, `DeterministicGround` on both,
the #26 fix). The divergence engine is dynamics (H2/H3), not configuration.

---

## 4. H2 — MoveAndSlide/StepUp determinism across two instances: THE STRUCTURAL ENGINE (mitigable, not fully fixable without replay)

What is deterministic, given identical config + identical static world:

- `DetGroundCast`/`DetSnapToGround` are **pure position→world shape queries** (`:1333-1348,1354-1368`) —
  deterministic *for identical positions*. That was the point of #26, and it holds.
- One `MoveAndSlide` from an identical transform+velocity against identical static trimeshes is, in
  practice, reproducible on the same build (Jolt's solve is deterministic per-process for the same inputs
  on the same architecture — and both processes here run the same binary on the same box class; MP_PLAN
  explicitly refuses to *rely* on cross-instance float determinism, `docs/MP_PLAN.md:127`).

The divergence therefore comes from the inputs to those solves **never being bit-identical in the first
place** — position error is bounded below by the wire grid (1/256 m quantization,
`core/UnturnedNet/Prediction.cs:29-32`) plus the sub-dead-zone skew the C2 band deliberately tolerates
(up to 0.08 m standing, `core/UnturnedNet/PlayerReplication.cs:594-597`). On flat ground a ≤8 cm offset is
invisible: both solves produce parallel trajectories. At geometry it hits **decision cliffs** where a
centimetre picks a different branch:

1. **`StepUp` is a binary 0.5 m teleport** (`game/PlayerController.cs:1373-1381`): blocked-at-foot AND
   clear-raised → `GlobalPosition += Up * 0.5`. At a curb/sidewalk lip, an 8 cm offset (or the one-tick
   `_detGrounded` lag interacting with a jump, §5) makes one body step up this tick and the other not —
   an instant ≥0.3 m divergence (the raise + the horizontal motion the blocked body lost), far over the
   0.04 dead zone and over the 0.08 ack band → the band disengages (`:632-635`), the body's truth
   publishes, the owner eats a visible pullback. This is strawberry's item 1 and 2 verbatim.
2. **Thin colliders bifurcate the slide direction**: against the hydrant's trimesh, slide-left vs
   slide-right is decided by which side of the face centroid the capsule centre lands on — a knife-edge on
   a ≤8 cm offset. The two bodies then diverge *laterally* at full walk speed until the obstacle is
   cleared; the reconciler drags the shell across the hydrant's face ("my server position TWEAKED out",
   item 4). No config change fixes this; only shrinking the standing offset (band/dead-zone tuning trades
   this against flat-ground tug) or replay (§7) does.
3. **Doorways compound (1) + (2) + the headroom re-gate** (§3 asymmetry 2): jamb slides pick sides,
   `FloorSnapLength`/`StepUp` interact with thresholds, and a stance disagreement halves one body's speed
   for a few ticks (item 2, "collider mismatch" feel).
4. **Corrections are applied through geometry**: `ApplyNetCorrection` is a bare `GlobalPosition += delta`
   (`:1153-1157`). Mid-contact, an eased slice can embed the capsule in the curb/jamb it is touching; the
   next `MoveAndSlide` depenetrates along the contact normal — a lateral kick the avatar never had, which
   *creates fresh divergence at exactly the place divergence is already being corrected*. This is a
   positive-feedback term unique to geometry, and the cheapest real H2 fix: sweep the slice
   (`TestMove`-guarded, or a velocity-less `MoveAndCollide`), apply what lands, and report only that via
   `NoteCorrectionApplied` — the reconciler's accounting is already partial-application-exact
   (`core/UnturnedNet/Prediction.cs:114-120` — it exists precisely so callers can persist less than the
   raw delta).

**What the port can do short of replay** (ranked): F3 (collision-aware corrections — removes the feedback
term), F4 (drop the avatar's headroom second opinion), F6 (de-binarize `StepUp`: sweep upward for the
minimal clearing height instead of the fixed 0.5, so a disagreement costs ~the curb height, not half a
metre — note retail has no explicit step-up code at all in `simulate`; it rides Unity's
`CharacterController.stepOffset` + the snap cast, `U3:Player/PlayerMovement.cs:1372`, so F6 also moves the
port *toward* the reference shape). What remains after these — knife-edge slide-side picks — is
structurally unfixable across two independent solves and is exactly the residue C3 replay exists for (§7).

**Honest accounting:** H2 is quiet on the flat baselines *because* the flat world has no decision cliffs.
The §8 baselines exist to measure exactly this class.

---

## 5. H3 — jumps: "server teleports me to the apex" — CONFIRMED, three stacked mechanisms

Jump numbers first (`core/UnturnedSim/PlayerMovementDef.cs:33-34`): `JUMP = 7` m/s, `GRAVITY = 29.43` →
apex ≈ 0.83 m at ~12 ticks (0.24 s); one tick of takeoff rise = 0.14 m; one sprint tick = 0.14 m
horizontal. Every number below the snap threshold (2 m) and above both the dead zone (0.04) and the ack
band (0.08) — i.e. jumps live exactly in the eased-correction regime.

**Mechanism A — takeoff-tick skew (the pullback near step-ups, item 1).** The wire jump bit is the *held
key*, sent every tick (`game/PlayerController.cs:2787,1141-1144`; packed at
`game/ClientWorldSession.cs:217`). The avatar applies it only when **its own** `_detGrounded` is true
(`core/UnturnedSim/PlayerMovementSim.cs:36-40`, grounded fed at `game/PlayerController.cs:2813`). Whenever
the two bodies' grounded flags disagree on the takeoff tick — precisely at curbs/step-ups, where §4's
bifurcation and the one-tick `_detGrounded` lag (`:1322`) live — the avatar misses the client's takeoff
tick and (because the bit stays held while the client is airborne) **launches k ticks late**. The two arcs
are then time-offset: vertical error up to ~0.8 m mid-arc, resolved by the reconciler as a mid-air drag.
On the healthy path (flags agree) both arcs track within the 0.08 band and C2 publishes the client's own
claims → zero correction — which is why flat-ground jumps are mostly fine and *sprint-jumps at geometry*
are the reported symptom.

**Mechanism B — the coast machinery replays the held jump bit (the "instant apex teleport").** Every
non-fresh consume re-presents `e.AppliedInput` **including `Buttons`**: starvation coast
(`core/UnturnedNet/PlayerReplication.cs:489-502`), hole substitution (`:505-518` — copies the held input,
clears only `HasClaim`), and the repay-drain exit (`:481-488`). Only the post-`MaxCoastTicks` hold strips
motion (`StripMotion`, `:493-498,532-538`). So during a WAN jitter gap that overlaps a landing, the avatar
lands and **re-jumps on a coast tick** — a whole arc the client never predicted. C1.5 suppresses the
*write-backs* during the stale window (`game/PlayerNetSync.cs:117,141-150`), which makes it worse to watch:
the published entity **holds at the last exact pairing (on the ground) while the avatar climbs**, and when
the repay-drain exits (`:473-488`) the first fresh write-back publishes the avatar **mid-arc — the
published position steps from ground to ~apex in one snapshot**. That is strawberry's item 3, literally:
server side teleports to the apex; the client then lerps (the 8/s eased glide) toward/away from it.
Observers see the same pop through `RemotePlayers` interpolation (a 1-snapshot discontinuity).

**Mechanism C — vertical error is eased like horizontal error.** The reconciler is isotropic
(`core/UnturnedNet/Prediction.cs:90-103`): a mid-arc vertical error folds in at 8/s while the shell is
ballistic, i.e. the correction fights gravity integration for ~10 ticks — felt as float/suck at the apex.
Minor next to A/B, but it shapes the *feel* of any jump divergence.

**Note on the prompt's interp hypothesis:** headless-server `physics_interpolation` is not a factor — the
player body opts out of Godot interp on both sides anyway (`game/PlayerController.cs:1726`), and the wire
samples post-physics tick positions; the discrete "hops" the client sees are the 25 Hz snapshot cadence
plus the C1.5 write-back gaps of Mechanism B, not missing server-side render interpolation.

**Retail contrast (why retail can't have A or B):** retail's jump is simulated from the *replayed input
stream itself* — server and client run the same `simulate(...)` with the packet's `inputJump`
(`U3:Player/PlayerInput.cs:1806`, `U3:Player/PlayerMovement.cs:1308-1314`), the server **never simulates
without a packet** (no coast — `serversidePackets` dequeue-per-tick, `U3:Player/PlayerInput.cs:1723-1734`),
and a mispredicted takeoff is corrected by rewind+replay, not by easing (§7). Retail also gates the jump on
`stamina ≥ 10` and tires per jump (`U3:Player/PlayerMovement.cs:1310-1313`) — a fidelity gap in the port
(no stamina jump cost at all) that is *currently harmless* for sync because neither side gates on it, but
becomes a §6-class trap if ever added server-side only.

**Fixes (F1+F2, small, land together):**
- **F1 — takeoff-edge semantics + never-repeat.** Client sends `ButtonJump` only on the tick its sim
  actually consumed the jump grounded (in the shell: `jump && grounded` going into `_move.Step`, i.e. the
  tick `Velocity.y` became `JUMP` — one boolean next to `LastJumpInput`, `game/PlayerController.cs:2787-2790`).
  Server side, strip `ButtonJump` from every repeated/substituted/repaid input
  (`core/UnturnedNet/PlayerReplication.cs:470,484,514-516,492` — one mask in each re-present path, mirroring
  what `StripMotion` already does for holds). Kills Mechanism B outright; wire-compatible (same bit,
  stricter semantics), version-gate like the other MoveInput revisions.
- **F2 — avatar honors the claimed takeoff.** On a fresh input with the (now edge-semantic) jump bit, the
  avatar applies the jump impulse if it was grounded *within a small tolerance window* (det-ground within
  `StepHeight`, or grounded in the last 2-3 ticks) instead of requiring exact same-tick groundedness —
  the client picked the takeoff tick, the server validates plausibility (the §2.3 posture: validate,
  don't re-derive). Kills Mechanism A for the curb/step-up case. Anti-cheat note: worst abuse is a jump
  from ≤0.5 m of hover — bounded, same class as the tolerances the ack band already accepts, and the
  fly/teleport ceilings stay with the band + budget (`core/UnturnedNet/PlayerReplication.cs:597-606`).
- Optional polish (with F0 measuring): an anisotropic reconciler — correct Y faster/snappier than XZ while
  airborne (or fold vertical error only on landing) so any residual arc skew doesn't read as apex suck.

---

## 6. H4 — stamina (VoX's hypothesis): MECHANISM REAL BUT NARROW — CONFIRMED only as a coast-window effect, REFUTED as a steady-state inchworm

Trace, from the code: client stance is computed by the shell's FSM **with client stamina**
(`game/PlayerController.cs:2776`, gate `stamina > 0.05` at `core/UnturnedSim/PlayerStanceSim.cs:30`), and
the stance the sim consumed **rides the wire per input** (`game/ClientWorldSession.cs:217`,
`MoveInput.PackStance`); the avatar integrates at exactly that stance per seq
(`game/PlayerNetSync.cs:164`). Avatar-side stamina is frozen at 1.0 and never consulted
(`game/PlayerController.cs:1645-1647`; the sprint-overlay branch needs `sprintKey`, which is always false
on an avatar). Therefore, **on a healthy link the sprint→stand transition at stamina exhaustion is
tick-exact on both sides — zero divergence.** VoX's "stamina isn't part of the control replay" is true but
moot in the current architecture: there *is* no replay, and stance-in-the-input makes stamina replication
unnecessary for parity today.

The real divergence window is **starvation coasting**: `TryConsumeInput` re-presents the last consumed
input — stance included — for up to `MaxCoastTicks = 12`
(`core/UnturnedNet/PlayerReplication.cs:392,489-502`). If the client's stamina crosses 0.05 during a
starve, the avatar integrates stale SPRINT (7 m/s) while the client predicted STAND (4.5 m/s):
2.5 m/s × 0.02 × k ≈ **0.05 m per coast tick, ≤ 0.6 m worst-case** — over the dead zone from the second
tick, resolved as a tug when the stream re-pairs. Requires a ≥2-tick starve (post-C1 redundancy that means
≥3 consecutive datagram losses or a real gap) *coinciding* with the exhaustion crossing — rare, which
matches strawberry's "doesn't inchworm me personally". Rank below H2/H3 as instructed, and the same
mechanism applies to *any* stance flip during a coast (crouch spam under jitter), not just stamina.

**Fix path:** F7 first — instrument, don't guess: a `--netlog` counter of coast ticks whose held stance
differs from the next fresh input's stance (one comparison in the repay path). If it measures: the
surgical fix is *stance demotion on coast repeats* (coast at STAND speed after the first repeated tick —
symmetric with `StripMotion`'s philosophy of never ghost-running stale intent, and it biases the error
toward "server behind" which the ack band absorbs better than "server ahead"). Sending stamina on the wire
or recomputing stance server-side is **not** recommended now: it buys nothing on a healthy link, and the
moment the port grows a server-side stamina sim it must also grow retail's answer (stamina in the
correction + replay — retail restores stamina and the tire/rest clocks in `ClientResimulate`,
`U3:Player/PlayerInput.cs:1232-1239,1322-1325`, and replays `SimulateStaminaFrame` per history frame,
`:1331`, sim at `U3:Player/PlayerLife.cs:1794-1813`). That is C3-scope bookkeeping (§7), not a Part-C.5
patch.

---

## 7. H5 — is C3 (rewind+replay) now justified? YES IN PRINCIPLE, GATED IN PRACTICE — full design

### 7.1 The retail reference (what we'd be porting), cited

- Client records every input tick into a replayable history: `clientInputHistory` append at
  `U3:Player/PlayerInput.cs:1611-1626` (frame, crouch/prone/sprint, axes, jump, body+aim rotation), and
  puts its post-move position on the wire as `clientPosition` (`:857-874`, set at `:1607`).
- Server consumes **one packet per qualifying tick** (`:1723-1734`), re-simulates the full pipeline —
  `life.simulate` → `stance.simulate` → `movement.simulate` (`:1790-1806`) — through the **same single
  CharacterController** the client ran, then: within `errorToleranceDistance = 0.02 m` → `SendAckGoodInputs`
  (client keeps its position, skew tolerated); beyond → `SendSimulateMispredictedInputs` carrying
  **frame, stance, position, velocity, stamina, tire/rest clock offsets** (`:1818-1838`).
- Client, on the next input tick (`:1545-1549`): `ClientResimulate` (`:1268-1346`) — trim history ≤ acked
  frame (`:1241-1263`), **teleport** the controller to the server state (disable/enable trick,
  `:1316-1319`), restore velocity + stance + stamina + clocks (`:1320-1325`), then **replay every remaining
  unacked input through real physics** (`stance.simulate` + `movement.simulate` per history frame,
  `:1327-1335`) — N `CharacterController.Move` solves inside one frame. No easing exists anywhere in
  retail; corrections are rare, discrete, and replay-complete.
- Structural consequence: retail **cannot have two-body geometry divergence** — there is one simulated body
  per player per side, stepped from the same input stream; a geometry disagreement is a *misprediction*,
  corrected once, exactly. The port's §4/§5 residue is the class of error this design erases.

Retail runs this at `RATE = 0.08 s` / `SAMPLES = 4` (`U3:Player/PlayerInput.cs:879-880`) — a 12.5 Hz input
sim, so its replay depth at 200 ms RTT is ~3 frames. The port's 50 Hz tick means ~6-10 unacked inputs at
the same RTT: a deeper but still small replay.

### 7.2 The port's design (what C3 concretely is here)

Scope: **replace the eased glide for errors in (DeadZone, Snap) with teleport + replay**, keeping the dead
zone (below it: no correction at all, retail-style ack) and the snap path (unchanged).

1. **Input ring, explicit.** The shell session already retains per-seq positions
   (`PredictionReconciler.Record`); extend the ring entry (or a parallel ring in `ClientWorldSession`) to
   the full replay record: axes, yaw, jump-edge bit, stance, plus post-move velocity and `_detGrounded`.
   ~16-32 entries covers any sane RTT (the existing ring is 256 — `core/UnturnedNet/Prediction.cs:43`).
2. **The correction payload needs velocity (wire addition).** The player snapshot carries pos + yaw + seq
   today; replay from a position with a wrong velocity re-diverges immediately (retail sends velocity +
   stance + stamina, `:1833-1835`). Options: extend the owner-only player block with velocity + stance
   (quantized, owner-relevancy only), or a dedicated `MispredictionEvent` (EventRegistry, next EventId)
   fired only when the band disengages — the event mirrors retail's shape exactly and costs nothing in the
   healthy case. Recommend the event: append-only id (`ReplicationIds`), no snapshot-format churn.
3. **Re-step the shell N times in one tick.** Extract the movement kernel of `_PhysicsProcess`
   (`game/PlayerController.cs:2776-2834`: stance step → `UpdateHitbox` → `Step` → `StepUp` →
   `MoveAndSlide` → det-ground/snap recheck) into a `StepMovementOnce(in ReplayInput)` the replay loop can
   call. Feasibility, honestly assessed:
   - `MoveAndSlide` is a synchronous sweep against the space and is callable multiple times per physics
     tick (each call consumes `Velocity` and the physics-step delta); `TestMove`, `DetGroundCast`
     (`DirectSpaceState`) are likewise legal inside `_PhysicsProcess`. This is the same operation retail
     performs with `CharacterController.Move` N times in one frame (`U3:Player/PlayerInput.cs:1327-1335`)
     — precedent, not speculation. The one engine caveat to spike: `MoveAndSlide` reads the tick delta
     internally, which is correct here (each replayed input IS one 20 ms tick), and floor flags carried
     between iterations must come from the det-ground pipeline (they already do —
     `game/PlayerController.cs:2813`), not `IsOnFloor`.
   - Restore before replay: `TeleportTo`-semantics position write (interp snapshots + velocity reset —
     `:94-101`), then velocity := payload velocity, stance := payload stance (through `ScriptedStance` for
     the replay window), `_detGrounded` := re-derived by `DetGroundCast` at the restored position
     (deterministic — better than shipping the flag).
   - After replay: recompute `rec[seq]` for every replayed seq (replace-semantics — this is what makes the
     *next* ack measure ~zero), shift `_interpPrev/_interpCurr` to the final position (the
     `ApplyNetCorrection` contract, `:1153-1157`), zero the reconciler's pending error.
   - Cost: a correction event at 120-200 ms RTT replays 6-10 inputs → ~30-60 Jolt sweeps *per correction
     event* (not per tick). Corrections after F1-F4 should be seconds apart even at geometry; this is
     noise next to the per-tick zombie/nav load. Worst case (correction every snapshot at 25 Hz) is the
     spike's kill-criterion.
4. **What C3 does NOT touch:** the server (avatar drive, band, coast machinery unchanged — the band keeps
   healthy skew invisible; replay only replaces *how the client resolves* over-band corrections), vehicles
   (Part A's client authority is the settled answer — `docs/CLIENT_PREDICTION_PLAN.md:70,169-175`), and
   the determinism boundary (`docs/MP_PLAN.md:127`): replay needs *replayability*, not cross-instance
   bit-equality — after a replayed correction the next ack measures whatever residue two solves still
   have, and the dead zone absorbs it exactly as today.

### 7.3 The recommendation

The Part C gate ("only if the harness still shows felt lurches from legitimate large corrections after
C1+C2", `docs/CLIENT_PREDICTION_PLAN.md:153`) has now **fired in the field but not yet in the harness** —
strawberry's geometry lurches are real, but the harness never had geometry to show them. So: build F0
first, confirm the baselines reproduce the pullback, land F1-F4 (cheap, mechanism-targeted), re-run. If
the geometry baselines still fail their bars — which §4 predicts they will for the thin-collider case,
since slide-side bifurcation survives every cheap fix — **C3 is justified and this design is the spike**.
Expected outcome: F1-F4 kill the jump/doorway/curb majority; C3 ships for the residue as the correctness
end-state (and unlocks any future server-side stamina/vitals gating for free, §6).

---

## 8. The teeth — four new geometry WAN baselines (design)

The existing WAN baselines run on the **flat fallback world** (`WorldBuilder.BuildFullWorld(...,
mapRoot: "res://__no_such_map__")` → `WorldBoundaryShape3D` ground — `game/testing/tests/NetTests.cs:2621-2624`,
`game/WorldBuilder.cs:105-110`) — zero decision cliffs, so they structurally cannot reproduce this report.

**Harness fact that makes the add minimal:** in the L1 rig the `ClientWorldSession` shell and the
`DedicatedServer` avatar live in the **same `World` tree = one physics space**
(`NetTests.cs:2626-2631`). Static obstacles added to `World` are therefore seen by both bodies —
no `WorldBuilder` change needed. (Corollary, stated for honesty: this harness canNOT detect H0-class
client/server collision-set mismatches; §2 closed those by inspection, and the holiday fork (F5) gets an
L0/L1 of its own — assert both modes build the same placed-object count for a forced `activeHoliday`.)

Shared helper (test-file-local): `WanGeo.Box(World, pos, size)` → `StaticBody3D { CollisionLayer = 1<<0 }`
+ `BoxShape3D` — mirroring the object-collider layer rule (`game/WorldBuilder.cs:226`). All four tests
reuse the `NetShellWanWalk` skeleton (`:2610-2689`): flat-fallback world + `MemNetwork` + `WanLink.Wan`
(`:2585-2591`; 120-200 ms RTT, 2-tick reorder jitter, 2 % loss), spawn-settle, then the scripted course;
metrics per the flat baselines **plus a worst-single-tick correction** (`corrDelta = CorrectionAppliedMeters`
sampled per tick, max of the per-tick step — the "one hard tug" the per-minute average can hide).

| Test | Geometry (relative to spawn, on the flat fallback ground) | Course | Bars (all: ZERO snaps, desync-quiet, clean-link convergence < 0.05 m — the `:2676-2686` closing pattern) |
|---|---|---|---|
| `net.shell_wan_stepup` | two "curbs": boxes 8×0.15×1 m and 8×0.30×1 m laid across the path at +4 m and +9 m (0.15 = sidewalk lip; 0.30 = under `StepHeight`, over `FloorSnapLength` interplay) | walk fwd 6 s, yaw 180°, back, ×4 (16 curb crossings); then the same sprinting | corrPerMin < 2, maxPending < 0.25, worst single-tick corr < 0.08 |
| `net.shell_wan_doorway` | a wall: two boxes 3×2.5×0.3 m with a 0.9 m gap (capsule Ø 0.7); a lintel box over the gap at y=1.9 m (pokes the headroom gate, §3 asymmetry 2) | approach offset +0.2 m from gap centre (guaranteed jamb slide), through, turn, back, ×6; then ×6 sprinting | same bars |
| `net.shell_wan_thincollider` | the "hydrant": a 0.12×0.6×0.12 m box at +5 m | sprint dead-centre into it, keep holding forward 1 s against it, release, sidestep, repeat with lateral offsets −0.02/0/+0.02 m (knife-edge each side), ×3 each | same bars — this is the one §4 predicts still fails after F1-F4 (the C3 gate) |
| `net.shell_wan_jump` | flat, plus one 0.15 m curb at +6 m | (a) 15 sprint-jumps on flat with the jump key held across landings (bunny-hop cadence — exercises §5-B coast re-jump under the Wan jitter); (b) 8 sprint-jumps timed onto/over the curb (§5-A takeoff skew) | corrPerMin < 2, maxPending < 0.25, worst single-tick corr < 0.08, and **max vertical pending < 0.15** (the apex-teleport signature) |

Expected today (this is what makes them teeth, per the regression rule): stepup and jump fail via §5-A/B +
§4-1 (StepUp bifurcation at the curb, late-jump arcs, coast re-jumps — worst single-tick corr well over
0.08, vertical pending spikes ~0.3-0.8 m); doorway fails via §4-3 (jamb slide-side + stance re-gate);
thincollider fails via §4-2 (lateral bifurcation, the "tweak-out"). Record the measured pre-fix numbers in
each test's comment exactly the way `:2668-2675` documents the 13.951 → 0.570 ladder, so every F-fix
lands against a number.

Sequencing note: land F0 with the bars as `T.Check` + the pre-fix numbers in comments — on today's code
they FAIL, which is correct and intended (they gate the fixes); if CI needs green-before-fix, land them
skip-gated behind an env flag for the one commit between F0 and F1, then un-gate with F1 (the
`net.shell_sprint_stop_jitter` precedent).

---

## 9. Phased landing order (each phase independently shippable, tests in the same commit)

1. **P0 — F0**: the four geometry baselines + the shared `WanGeo` helper. Measure + record pre-fix
   numbers. (Also: F7's coast-stance netlog counter — it's three lines and informs P3.)
2. **P1 — F1+F2 (jump)**: takeoff-edge jump bit + strip-on-repeat + avatar grounded-tolerance.
   Version-gate the semantics like the previous MoveInput revisions. Expect `wan_jump` green,
   `wan_stepup` much improved. L0: a `TryConsumeInput` case proving a repeated/substituted/repaid input
   never carries `ButtonJump`; an L1 assert that a starved landing never double-jumps the avatar.
3. **P2 — F3+F4**: collision-aware `ApplyNetCorrection` (swept slice, partial-apply accounting) + avatar
   wire-stance trust (skip the `NetAvatar` headroom re-gate). Expect `wan_doorway` green, `wan_stepup`
   green or near.
4. **P3 — F5 (+F7 verdict)**: holiday handshake parity; act on the coast-stance counter only if it
   measured in real sessions.
5. **P4 — F6 (only if `wan_stepup` still misses its bar)**: incremental StepUp sweep, gated to
   `DeterministicGround` bodies to keep SP byte-identical.
6. **P5 — F8/C3 spike (gated on `wan_thincollider` still red — §4 predicts it will be)**: the §7.2 design;
   kill-criterion = replay cost per correction event or replay-parity failure in the L0 sim
   (`docs/CLIENT_PREDICTION_PLAN.md:159`'s C3 test shapes). Ship for the residue; keep the eased glide as
   the fallback path for non-replayable states (riding, dead, mid-teleport — retail skips those too,
   `U3:Player/PlayerInput.cs:1287-1301`).

---

## 10. Related, handled elsewhere (do not design here)

- **Vehicle client-collider parity** (walk-through-cars → yank; the one true H0 mismatch, §2 table) —
  folded into the Part A vehicle branch (`mp-predict-a`, concurrent).
- **The >64 KB reliable-full truncation** — folded into Part A's deploy.
- Nothing in this plan changes the wire except: the jump-bit *semantics* (P1, version-gated), the optional
  `MispredictionEvent` (P5, new append-only EventId), and the handshake holiday string (P3) — all
  compatible with the append-only id discipline (`core/UnturnedNet/PlayerReplication.cs` ReplicationIds).
