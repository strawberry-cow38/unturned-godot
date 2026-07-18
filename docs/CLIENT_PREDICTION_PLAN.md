# CLIENT_PREDICTION_PLAN — driving feel, hitmarkers, and the high-RTT inchworm

*Written 2026-07-18 against `main` @ faee89f. Design/implementation plan only — no code shipped with this doc.*
*Ground truth #1: the real Unturned source, open-sourced 2026-07-07, at `~/projects/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/` (cited below as `U3:<path>:<line>`). Ground truth #2: the port's own MP stack (all 8 MP_PLAN phases live, `docs/MP_PLAN.md` §7). All file:line references were read, not guessed.*

## 0. TL;DR — the three calls

| Part | Problem (strawberry, 100+ ms WAN) | Decision | Retail precedent |
|---|---|---|---|
| **C** (first) | residual on-foot lurch at high RTT | WAN test harness first, then: **input redundancy** on the MoveInput datagram, then **client position on the wire + a server ack band** (retail's 2 cm model), rewind+replay reconciliation as a gated spike | `U3:Player/PlayerInput.cs:1820-1838` |
| **A** (largest) | driving is floaty — full-RTT input latency | **Mirror retail: the driver's client OWNS the driven vehicle's physics.** Local real `Vehicle` body for the driver, transform streamed up, server validates a plausibility envelope + `recov` rollback, everyone else keeps today's puppets. NOT predict+reconcile — retail doesn't, and non-deterministic two-body Jolt can't | `U3:Interactable/InteractableVehicle.cs:1490-1519` |
| **B** | hitmarker waits a full RTT | **Client-predicted hitmarker** (immediate, cosmetic, off the local ballistic sim vs replicas) + **server lag compensation** (per-entity position-history ring, fire rewound by the shooter's view delay). Damage stays 100 % server-authoritative | `U3:Useable/UseableGun.cs:1691`, `U3:Player/CapsuleHistory.cs:45-81` |

Ship order **C → A → B**: C is the smallest diff and builds the injected-RTT harness A and B are tested with; A is the biggest felt win but the biggest diff; B is independent of both and lowest-risk.

Every part follows the null-seam SP rule (CLAUDE.md MP recipe #3): new behavior is wired only by `ClientWorldSession` / `DedicatedServer`; SP and `MpLoopback` stay byte-identical.

---

## 1. Ground truth: how shipping Unturned actually does it

The port's netcode should match retail's *decisions*, adapted to the port's 50 Hz tick and three-plane replication. What retail actually does (verified in the source, not folklore):

### 1.1 On-foot movement: predict → reliable input stream → server re-sim → 2 cm ack band → hard snap + replay

- The client simulates locally every input tick and appends to a replayable history: `clientInputHistory` (`U3:Player/PlayerInput.cs:1053`), entry struct `ClientMovementInput` (frameNumber, crouch/prone/sprint, input_x/y, jump, body + aim rotation — `U3:Player/PlayerInput.cs:765-784`), appended per tick at `:1611-1626`.
- **The input packet carries the client's post-move position.** `WalkingPlayerInputPacket` = base packet (`clientSimulationFrameNumber`, `recov`, `keys:u16` bitfield, attack bits, yaw, pitch — `U3:Player/PlayerInput.cs:431-441`) + `analog` (two 4-bit move axes, `:1606`) + `WriteClampedVector3(clientPosition)` (`:867-873`), where `clientPosition` is "Resulting transform.position immediately after movement.simulate was called" (`:854-857`, captured `:1607`).
- **Inputs ride the RELIABLE channel**, one packet per sim tick (`SendInputs.Invoke(..., ENetReliability.Reliable, ...)` `:1713`; `SAMPLES = 4`, `RATE = 0.08f` `:878-879` → retail sims at 12.5 Hz). The server drains `serversidePackets` one per sim tick and **never speculates**: no packet → no simulate. The port already mirrored the queue (PROGRESS.md 2026-07-17, `PlayerReplication.TryConsumeInput`) but kept inputs unreliable-sequenced with a coast — that divergence matters for Part C.
- **Server re-simulates, then acks or corrects with a 2 cm position-only tolerance** (`U3:Player/PlayerInput.cs:1820-1838`):
  ```
  const float errorToleranceDistance = 0.02f; // 2cm
  if ((walkingPacket.clientPosition - serverPosition).sqrMagnitude > sqr...)
      SendSimulateMispredictedInputs(...frame, stance, serverPosition, velocity, stamina, ...)
  else
      SendAckGoodInputs(...frame)
  ```
  Below tolerance: **zero correction** — the client keeps its predicted position, and retail simply tolerates a standing ≤ 2 cm client/server skew. No rotation or velocity tolerance exists.
- **A correction is a hard snap + full replay, no easing.** `ReceiveSimulateMispredictedInputs` latches state (`:1353-1363`); next client tick `ClientResimulate()` (`:1268-1346`) teleports the CharacterController to the server state (`:1317-1319`), restores stance/stamina, then replays **every remaining unacked input** through `stance.simulate` + `movement.simulate` (`:1327-1335`). Order is stance-then-movement on both sides (client `:1582`/`:1590`, server `:1798`/`:1806`).
- Movement itself is `CharacterController.Move` wrapped in an anti-clip sweep (`CheckedMove`, `U3:Utils/CharacterExtension.cs:94,104-182`) with grounding by deterministic `Physics.SphereCast` (`U3:Player/PlayerMovement.cs:726-741`) — timestep-fixed but **not bit-deterministic**; the reconciliation above exists precisely because it isn't. (The port's `DeterministicGround` in `game/PlayerController.cs:1311-1327` is already a copy of that spherecast.)
- Anti-cheat: `serverBoundsHistory` (see §1.3), `fakeLagPenaltyFrames` (input gap > 1 s accrues penalty frames, gun damage ×0.1 while penalized — `U3:Player/PlayerInput.cs:1490-1499,1955,1960`), a rolling input-rate limiter with an ignore cap at 15/s and a kick at 62.5/s (`:1397-1503`), and an out-of-order drop (`:1477-1481`).

### 1.2 Vehicles: **client-authoritative** — the driver's machine runs the physics

This is the load-bearing surprise. Retail does **not** predict-and-reconcile a server-simulated car. The driver *owns* it:

- Authority switch — `InteractableVehicle.updatePhysics()` (`U3:Interactable/InteractableVehicle.cs:1490-1519`): if you are the driver (or the server and the vehicle is *undriven*), `rootRigidbody` is physical; otherwise it is **kinematic**. The server's `FixedUpdate` explicitly bails for driven vehicles: `if (!isPhysical || isDriven || !Provider.isServer) return;` (`:4231`). So: undriven vehicles = server-simulated; the moment a driver sits (`addPlayer` `:2277` / `removePlayer` `:2360` re-run `updatePhysics`), physics authority migrates to that client.
- The driver's client runs full Rigidbody/WheelCollider forces locally (`:3402-3707`) and captures its own transform as truth (`:3703-3706`). The wire load is a `DrivingPlayerInputPacket` on the **same reliable input stream as walking** (`U3:Player/PlayerInput.cs:658-726,1693-1699`, type bit `:1893-1900`): `position, rotation, speed, forwardVelocity, steeringInput, velocityInput` (+ per-wheel suspension nibbles, gear/RPM), at the 12.5 Hz input cadence.
- The server *adopts* the reported transform onto its kinematic body: `rootRigidbody.MovePosition(point); rootRigidbody.MoveRotation(angle);` (`:3171-3182`) — after validation:
  - **Horizontal teleport cap** per packet: `HorizontalDistanceSquared(point, real) > (fuel==0 ? 0.5 : asset.sqrDelta)` → reject (`:3096-3105`); `sqrDelta = (TargetForwardVelocity * 0.1)^2` (`U3:Bundles/VehicleAsset.cs:2319-2329`) — i.e. "one packet may move you ~one packet-interval of top speed, plus slack".
  - **Vertical speed cap**: `validSpeedUp` / `validSpeedDown` (CAR 12.5 / 25 m/s, `U3:Bundles/VehicleAsset.cs:2336-2349`; check `:3138-3152`).
  - Violation → `recov`: server sends the last-good transform, the client teleports back and freezes kinematic until its packets echo the incremented recov counter (`tellRecov` `:2095-2109`, ack wait `:3069-3085`).
  - **Speed, rotation, steering are not validated at all** — stored and rebroadcast as-is (`:3167-3170,3181`). Retail ships this publicly; the envelope above is the entire anti-cheat.
- The server **never echoes vehicle state back to the driver** (`U3:Managers/VehicleManager.cs:2718-2722` "Do not send redundant updates to driver") and the driver ignores network state entirely (`tellState` early-returns for `isDriver`, `U3:Interactable/InteractableVehicle.cs:2113-2116`). Non-drivers smooth toward the latest state with a `BLEND_SPEED = 13` exponential — no snapshot buffer, no extrapolation (`:4427-4446`; state rebroadcast at 12.5 Hz unreliable, `U3:Managers/VehicleManager.cs:2771-2801,2918`, `U3:Provider/Provider.cs:5890`).
- Seating is Unity parenting to the seat transform (`U3:Player/PlayerMovement.cs:574-576`); the movement sim zeroes and skips while DRIVING/SITTING (`:1069-1092`).

**Why retail driving feels instant:** the driver's control loop never touches the network. There is no correction in the steady state at any RTT — the only network artifact a legitimate driver can ever see is a recov teleport.

### 1.3 Combat: favor-the-shooter — client resolves the hit, hitmarks instantly; server validates against a swept-bounds history

- For bullet guns the **client** raycasts locally each ballistic step (`U3:Useable/UseableGun.cs:1675-1677`, `DamageTool.raycast` with `RayMasks.DAMAGE_CLIENT`) and, on a hit, sends the **resolved claim** — not just a ray: hit type, usage, `point`, direction, normal, limb, and the target identity (player SteamID / zombie id / instance id), riding inside the same input packet (`sendRaycast` `:1909-1910`; serialization `U3:Player/PlayerInput.cs:442-543`, up to 16 claims/packet).
- **The hitmarker is client-side and immediate** — `PlayerUI.hitmark(...)` fires inside the client's own `ballistics()` on the local raycast (`U3:Useable/UseableGun.cs:1691` players, `:1706` zombies, `:1717` animals). **No hit-confirm RPC exists in retail**; `SendPlayShoot` replicates muzzle fx to *others* only. A marker the server later rejects is pure cosmetics — the Overwatch "favor the shooter, feedback may lie" trade, shipped.
- **Server validation is a lag-compensated plausibility gate, not a re-trace.** For a claimed player hit, at input-decode time: `enemy.input.serverBoundsHistory.ContainsPoint(controller, info.point)` (`U3:Player/PlayerInput.cs:163-181`) — `BoundsHistory` is a **ring of 50 expanded capsule AABBs, one per 50 Hz FixedUpdate ≈ 1 second of history**, expansion 0.75 m for animation/lean/prone (`U3:Player/CapsuleHistory.cs:141-144`; fed at `U3:Player/PlayerInput.cs:1537`; alloc `:1929-1932`). `ContainsPoint` sweeps consecutive snapshot pairs (`Encapsulate`, `U3:Player/CapsuleHistory.cs:45-81`) — **time-window based, not frame-keyed**: "was the claimed point inside where this victim has been in the last second". This is Source-style lag compensation with the rewind replaced by a window union. Then `ballistics()` adds a bidirectional occlusion trace (`getInput(true, ...)` `U3:Useable/UseableGun.cs:1928`, impl `U3:Player/PlayerInput.cs:965-1044`) and a range gate (`range + 4f` non-ballistic; `ballisticTravel * (steps+1+SAMPLES) + 4` ballistic — `U3:Useable/UseableGun.cs:1941-1958`). The server never re-derives *which* target was hit — it validates the client's claim. Zombies get a coarse ~16 m proximity check instead of a history (`U3:Player/PlayerInput.cs:206`).
- Rocket-type guns (`projectile != null`) bypass all of this with server-authoritative physics projectiles (`:509-658,1326-1332`).

### 1.4 The canon these map to

- **Quake/QuakeWorld** — origin of client prediction + replay of unacked inputs; retail's `ClientResimulate` is exactly the QW loop. Part C's rewind+replay option is this.
- **Source engine lag comp** (Bernier, "Latency Compensating Methods in Client/Server In-game Protocol Design and Optimization") — server rewinds victims to the shooter's view time. Retail's `serverBoundsHistory` is the window-union variant; Part B builds the port's ring.
- **Overwatch** (Tim Ford, GDC 2017 "Netcode") — favor-the-shooter with cosmetic client feedback + server damage authority, and the input-buffer discipline the port already adopted in the mp-inputbuffer fix. Part B's "predicted marker may lie, damage never does" is this framing verbatim.
- **Rocket League** (Cone, GDC 2018 "It IS Rocket Science") — the *other* way to do cars: fully deterministic fixed-tick custom vehicle physics, so the client can rollback-resimulate on every server packet. **This is out of reach here by decision and by fact**: the port's determinism boundary explicitly forswears cross-peer bit-determinism (MP_PLAN §2.5, "nobody should ever 'fix' a desync by chasing float determinism"), and Godot/Jolt's `VehicleBody3D` (suspension raycasts + tire friction solved inside the engine's physics step) cannot be stepped N times mid-tick for replay, nor will two instances of it agree to sub-centimeter over hundreds of ticks. Retail hit the same wall with PhysX WheelColliders and chose client authority — that precedent is the whole reason Part A is safe to build.
- **GGPO** — the general rewind-replay discipline; informs Part C's reconciler upgrade, deliberately *not* Part A.

---

## 2. Ground truth: where the port is today (and the exact gaps)

### 2.1 On-foot (Part C's substrate) — predict + eased-single-delta correction

- Client shell: `ClientWorldSession.ShellStep` (`game/ClientWorldSession.cs:169-221`) — net pump first, then per tick: consume the newest own-entity sample → `Reconciler.OnAuthoritative(e.LastProcessedInputSeq, e.Pos)` → apply `Step(dt)`/`TakeAll()` delta to the node via `PlayerController.ApplyNetCorrection` (`game/PlayerController.cs:1153-1157`, shifts the render-interp samples with the node) → `SendMoveInput` (one input per datagram, UnreliableSequenced — `core/UnturnedNet/NetWorldHost.cs:462-469`) → `Record(seq, TruePhysicsPosition)`.
- Reconciler: `core/UnturnedNet/Prediction.cs` — 256-entry prediction ring, error = `(authoritative − recorded[seq]) − correctionsAppliedSince` (`:69-83`), then **eased**: `1−exp(−8·dt)` per tick (`:26,90-103`), dead-zone `DeadZoneMeters = 0.04` (`:41` — modeled on retail's 2 cm, widened because two distinct physics solves), snap ≥ 2 m (`:23`).
- Server avatar: real headless `PlayerController` per peer (`game/PlayerNetSync.cs:59-155`) — write-back position under the producing input's seq via `ServerDrive` (`:114-119`; `core/UnturnedNet/PlayerReplication.cs:463-474`), then consume ONE queued input in seq order from the jitter buffer (`PlayerReplication.TryConsumeInput`, `core/UnturnedNet/PlayerReplication.cs:345-409`; `PrimeDepth 2`, hole substitution ≤ 2, starvation **coast** on last input up to `MaxCoastTicks 12` then hold, `CoastDebt` repayment — `:298-312`).
- Both bodies use the deterministic spherecast ground (`game/PlayerController.cs:1311-1327` — already retail's `checkGround`), stance/jump ride the wire (`MoveInput.Buttons`, `core/UnturnedNet/PlayerReplication.cs:97-142`).
- History: the LAN inchworm was killed twice (PROGRESS.md `:568-571` dead-zone; `:575-579,586-588` input queue + coast cap). The **documented deferred fast-follow is exactly retail's model**: PROGRESS.md `:570` — "the server re-sims the client's INPUTS and compares against `walkingPacket.clientPosition` … needs the client position ON THE WIRE (we send inputs only), a server-side validation band at the §2.3 choke point".

**Gaps vs retail:** (1) inputs are unreliable one-per-datagram — a lost datagram forces the server to *guess* (coast), retail never guesses; (2) the client position is not on the wire — the server cannot ack-or-adopt, so **every** divergence, however small, can only ever resolve as client-visible correction traffic; (3) corrections ease over ~90–200 ms instead of snap+replay — at 100+ ms the unacked pipe is ~5–8 inputs deep, and fresh error accrues while stale error still glides.

### 2.2 Vehicles (Part A's substrate) — server-authoritative, zero prediction, everyone rides puppets

- Driver client captures intent only (`PlayerController.RidePuppet`, `game/PlayerController.cs:1280-1294`) and streams `DriveInputCommand{Seq,NetId,Throttle,Steer,Handbrake}` @ 50 Hz UnreliableSequenced (`game/ClientWorldSession.cs:186-192`; `core/UnturnedNet/NetWorldHost.cs:585-592`; wire `core/UnturnedNet/VehicleReplication.cs:344-374`). `Seq` is latest-wins stale-drop only (`:143-149`) — **never echoed; no `LastProcessedInputSeq` exists on the vehicle wire**.
- Server: the only real `Vehicle` (`VehicleBody3D`, Jolt) lives on the headless server; `VehicleNetSync.Tick` applies the held input via the same SP seam `v.Drive(throttle, steer, handbrake)` (`game/VehicleNetSync.cs:86-108`; `game/Vehicle.cs:1224`) and publishes state; `ServerVehicles.Step` teleports the driver's player entity onto the vehicle each tick (`core/UnturnedNet/VehicleReplication.cs:554-564`). Tick order: `net.server.sim` → syncs → `net.server.replicate` last (`game/DedicatedServer.cs:117-142`).
- Snapshot @ 25 Hz carries pos, yaw/pitch/roll, **lin+ang velocity**, steer angle, fuel/health/battery, flags (`core/UnturnedNet/VehicleReplication.cs:274-291`).
- Every client — **including the driver** — renders a mesh-only `VehiclePuppet` dead-reckoned from the snapshot (`game/VehicleReplicaView.cs:18-19,24,65-71`: `GlideRate 12/s`, extrapolate on `LinVel` up to 0.5 s, snap at 8 m; "the driver's own client renders the same puppet in v1" `:11-13`). The shell hides/freezes and copies the puppet's position (`EnterPuppet` `game/PlayerController.cs:1240-1253`; `RidePuppet` `:1293`).
- Enter/exit round trip: `RequestEnterNearestPuppet` → `CommandEnterVehicle` → `ServerVehicles.CanEnter` (reach 6 m, seat empty, alive — `core/UnturnedNet/VehicleReplication.cs:493-501`) → `VehicleEnteredEvent` → `EnterPuppet`; exit computes the beside-the-door spot server-side and ships it in `VehicleExitedEvent` (`:516-538`; client `game/ClientWorldSession.cs:273-294`).

**Gap vs retail:** the entire driver control loop crosses the wire twice (input up, transform down through a 25 Hz snapshot + dead-reckoner). At 100 ms RTT the wheel answers ~150–200 ms late and every dead-reckoning correction reads as float. Retail has *zero* of this because the driver simulates locally.

Two facts that make Part A cheap: the client can already construct the full physics vehicle from replicated data (`TypeId → SpecNames` + `Variant`, `game/Vehicle.cs:842-848,861,880`) — nothing in `BuildXxx`/`Drive` needs the server; and the client world already contains the same static terrain/objects the server collides against (content-hash-matched by the join gate). What the client does **not** have is physics bodies for remote players/zombies (visual-only puppets — `game/RemotePlayers.cs`, `game/ZombiePuppets`), so a locally-simulated car collides with the static world only. Retail has the identical limitation for remote pawns vs a client-authoritative car, and ships it.

### 2.3 Combat (Part B's substrate) — server-stepped bullets vs live positions; hitmarker fully round-trip-gated

- MP fire sends **only the intent**: origin + undeviated aim dir (`FireCommand{Seq, Origin, Dir}` — `core/UnturnedNet/CombatReplication.cs:15-36`; comment `:9` "never a hit result"), ReliableOrdered (`core/UnturnedNet/NetWorldHost.cs:474-480`), **no tick/timestamp**. Client fx are immediate; the local bullet goes `Cosmetic = NetFire != null` and despawns on contact with no damage and *no hitmarker* (`game/PlayerController.cs:2159,2183`).
- Server: `ServerCombat.OnFire` validates dead/reload/firerate/ammo/origin-offset (3 m)/dir sanity (`core/UnturnedNet/ServerCombat.cs:178-206`), then **steps** bullets one 0.02 s segment per tick with gravity, up to 20 steps, testing each segment against a head/torso/leg cylinder at the victims' **live, current-tick** positions (`StepBullets` `:265-349`, `SegmentHitsCylinder` `:529-546`), with world occlusion via the Godot ray seam (`game/DedicatedServer.cs:77,147-158`). **No position history exists anywhere.**
- The hitmarker renders **only** on the shooter-unicast `HitConfirmEvent` (`core/UnturnedNet/ServerCombat.cs:513-517`; `game/ClientWorldSession.cs:105-109` "the hitmarker now only ever tells the server's truth") — i.e. one full RTT + up to bullet-flight ticks after the trigger. That is the ~0.5 s strawberry measured.
- All the transforms a history ring needs are already recomputed every tick before `Combat.Step`: `PlayerEntity.Pos` (ServerStep/ServerDrive), `ZombieEntity.Pos` (`game/ZombieNetSync.cs:44` → `ZombieReplication.ServerPublish`), `VehicleEntity.Pos` (`game/VehicleNetSync.cs:53`). Recording them is new memory, not new sim.
- Note: `PvPEnabled = false` on the dedicated server today (`game/DedicatedServer.cs:78`, D1 posture) — lag comp benefits zombie combat immediately and players whenever PvP switches on.

### 2.4 The wire/version substrate all three parts touch

`NetProtocol.Version = 4` (`core/UnturnedNet/NetProtocol.cs:54`); both sides drop mismatched datagrams and the handshake rejects (`core/UnturnedNet/NetSession.cs:155`, `NetServerSession.cs:132-135`). The registries are append-only (`core/UnturnedNet/PlayerReplication.cs:13-87`): next free CommandId = **26**, next free EventId = **29**. Golden byte tests lock every frame; "changing anything here must bump Version and re-golden in the same commit" (`core/UnturnedNet/NetProtocol.cs:8-9`). Deploy reality: one private server + launcher-updated clients, so a version bump per landed part is cheap; each part below states its bump.

---

## 3. Phase 0 (shared prerequisite): the injected-RTT harness

Honest testing of all three parts needs simulated WAN, not LAN loopback. **Most of it already exists:**

- L0/L1 transport: `FaultyLinkConfig{LossProbability, DuplicateProbability, LatencyTicks, ReorderJitterTicks, HoldUntilTick}` per direction (`core/SDG.NetTransport/MemTransport.cs:17-32`), deterministic by seed. L1 rigs already drive it: `net.shell_downhill_reconcile` runs latency 3 / jitter 2 / 2 % loss both ways (`game/testing/tests/NetTests.cs:1199-1200`), `net.shell_sprint_stop_jitter` C2S latency 3 / jitter 2 (`:1277-1278`), `net.shell_stall_burst` uses `HoldUntilTick`.
- What's missing is a **named WAN profile and baseline metrics**, so "does this fix the 100 ms worm" is a number, not a vibe.

**Build (test-side only, no product change):**

1. `WanProfile` helper (in the L1 rig utilities next to the existing per-test configs, and mirrored in `tests/UnturnedNet.Tests` for L0): symmetric `LatencyTicks = 3` + `ReorderJitterTicks = 2` + `Loss = 0.02` per direction ≈ **120–200 ms RTT with jitter** at the 50 Hz tick (1 tick = 20 ms), plus a second harsher profile (`LatencyTicks 5`, loss 0.05) ≈ 200–280 ms. Latency is the knob the existing tests under-use — they were tuned to prove jitter/loss fixes, not high-RTT feel.
2. Metrics already exposed and to assert on: `PredictionReconciler.CorrectionAppliedMeters`, `PendingError`, `Snaps`, `AcksApplied` (`core/UnturnedNet/Prediction.cs:52-57`). Add (client-side observability, no wire change): a per-second NetLog rollup of correction meters + hot-tick count, mirroring the server's `[NET] 1s:` line (`core/UnturnedNet/NetWorldHost.cs:182-210`) — this is also what strawberry runs live with `--netlog` to confirm the fix on the real WAN link.
3. New L1 baselines (they will FAIL before Part C and gate it after, per the regression rule): `net.shell_wan_walk` and `net.shell_wan_sprint_turns` — WAN profile, scripted walk/sprint with direction changes and stops, assert bounded `CorrectionAppliedMeters` per simulated minute + zero snaps + desync-quiet. Record the pre-fix numbers in the test comment the way `net.shell_sprint_stop_jitter` documents its pre-fix 0.21 m yank (PROGRESS.md `:579`).

Scope: small. Risk: none (test-only).

---

## 4. Part C — the residual high-RTT inchworm (ship first)

### 4.1 Diagnosis from the code + the retail model

At LAN the current stack measures ~zero error (proven: `net.shell_sprint_stop_jitter` post-fix "0 m and 0 m across all five cycles", PROGRESS.md `:579`). What is left at 100+ ms, ranked by suspicion — each hypothesis is discriminable on the §3 harness:

- **H1 — coast/hold mispredictions (loss- and jitter-coupled).** Retail's server *never guesses*: inputs are reliable, the server sims only on real packets (§1.1). The port's server, on a seq hole or starved queue, **coasts on the last consumed input** (`core/UnturnedNet/PlayerReplication.cs:373-409`). Whenever the player's actual input differed during the gap (they turned, stopped, started), the server integrated wrong motion the client never predicted — surfacing as a correction of up to `gap × v·dt` on the next acks. At WAN loss/jitter these gaps recur every few seconds; each one is a felt micro-lurch **at input changes** — exactly an "inchworm while maneuvering" signature. *Discriminate: WAN profile with loss = 0, jitter = 0 (pure latency) vs full profile. Pure latency + steady in-order arrivals should measure ≈ 0 correction; if the worm needs loss/jitter to appear, H1 dominates.*
- **H2 — standing two-body drift crossing the dead-zone in cycles.** Shell and avatar are two separate Jolt `CharacterBody3D` solves; healthy per-tick residuals exist (that's why `DeadZoneMeters = 0.04` > retail's 0.02 — `core/UnturnedNet/Prediction.cs:34-41`). Drift accumulates until an ack measures > 4 cm, then the whole accumulated error glides back client-ward — a periodic visible tug. RTT doesn't change the drift *rate* but stretches the measure/correct loop and batches more drift per resolution. Crucially the port has **no server-ward resolution path at all** (no client position on the wire), so 100 % of drift resolves as client-visible motion; retail resolves the sub-2 cm regime by *acking* (client keeps its position, skew tolerated) and can afford to because the server re-sims the exact input stream. *Discriminate: pure-latency runs on slopes/strafe-heavy paths; watch `PendingError` sawtooth frequency/amplitude vs LAN.*
- **H3 — the eased-delta reconciler with RTT-stale acks.** Corrections target the error as of `seq` (RTT ago); the accounting subtracts corrections applied since (`core/UnturnedNet/Prediction.cs:78-79` — no double-count), but *new* divergence accrued in the RTT window is only measured by later acks. Under sustained divergence-producing motion the pipeline never drains: `_pending` is persistently nonzero, `Step` emits a nonzero slice every tick — a constant low-grade rope-tug that scales with RTT depth. Retail has no analogue: corrections are rare, discrete, snap+replay-complete in one tick (§1.1). *Discriminate: correction-meters concentrated in continuous drizzle (H3) vs discrete events at input transitions (H1).*
- **H4 — snap-threshold interaction.** Unlikely: 2 m (`:23`) is far above WAN-scale errors; the L1s count snaps and get zero. Keep the counter asserted, move on.

### 4.2 The fix, phased (each independently landable)

**C1 — MoveInput redundancy: kill the server's need to guess.** MP_PLAN §2.3 already specified it ("MoveInput @50 Hz on UnreliableSeq **carrying the last 3 inputs redundantly** so single loss costs nothing" — `docs/MP_PLAN.md:105`) but the shipped `MoveInput.Write` carries one input (`core/UnturnedNet/PlayerReplication.cs:144-151`). Change the datagram to carry the newest input + the 2 previous (client keeps a tiny ring in `NetWorldClient.SendMoveInput`, `core/UnturnedNet/NetWorldHost.cs:462-469`). Server side: `ServerQueueInput` (`core/UnturnedNet/PlayerReplication.cs:318-327`) iterates the entries oldest-first; the existing strictly-increasing-seq queue guard makes backfill idempotent, and a hole now only forms after **3 consecutive** datagram losses (~0.001 % at 2 % loss vs 2 % today). Coast/hole substitution stays as the deep fallback; it just almost never fires. This is the port-shaped equivalent of retail's reliable input channel — same guarantee (the server integrates the real stream), without retail's freeze-on-loss added latency. Wire: MoveInput layout change → **Version 5**, re-golden `MoveInput` bytes. ~9 bits/input × 2 extra ≈ 7 bytes/datagram — negligible.

**C2 — client position on the wire + the server ack band (the retail model, and the already-documented fast-follow — PROGRESS.md `:570`).** Add the shell's post-move position to MoveInput: quantized through the existing grid (`PlayerReplication.Quantize`, `core/UnturnedNet/PlayerReplication.cs:506-509`), captured exactly where `Record` reads it today (`Shell.TruePhysicsPosition`, `game/ClientWorldSession.cs:218`) — the direct analogue of `walkingPacket.clientPosition` (`U3:Player/PlayerInput.cs:867-873,1607`). Server, in `PlayerNetSync.Tick` after the avatar write-back (`game/PlayerNetSync.cs:114-119`):
  - `|serverPos − claimedPos| ≤ AckBand` → **adopt**: `TeleportTo(claimed)` + `ServerDrive(claimed, …)` — the sub-band skew resolves **server-ward, invisibly** (observers' interpolated puppets absorb centimeters for free; the owner sees nothing). This goes one step past retail (retail acks-but-keeps-its-own-position, tolerating the skew; §1.1) because the port's two-solve drift, unlike retail's re-sim residue, *accumulates* — adoption zeroes it each ack instead of letting it batch into over-band corrections. It is the "favor-client adoption band" PROGRESS.md `:570` scoped.
  - Beyond the band → today's behavior exactly: server position stands, client corrects (the reconciler path, unchanged).
  - **AckBand = 0.08 m** to start (2× the client dead-zone, 4× retail's 2 cm — we carry two-solve noise retail doesn't), tuned by the §3 harness.
  - **Anti-cheat bound (the part retail gets implicitly and we must add explicitly):** retail's server re-simulates the inputs, so a cheater can skew ≤ 2 cm per 80 ms packet ≈ 0.25 m/s. Our adoption must be budgeted the same way: cap cumulative adoption per player at **0.5 m/s** (a counter drained per tick; adoption requests over budget fall through to the correct-client-ward path). Sub-walking-speed exploit ceiling, zero effect on legitimate drift (cm/s). Validation lives at the §2.3 choke point next to the other MoveInput gates (`core/UnturnedNet/NetWorldHost.cs:78-82`). Fits the current TEST-SERVER posture (MP_PLAN §7); note it in the §7 revisit list.
  - Client side: the reconciler dead-zone stays as-is (it's now the mirror of the server band — under it *neither* side corrects, like retail's mutual 2 cm tolerance).
  - Wire: MoveInput +≈55 bits → same **Version 5** if landed with C1, else Version 6. Re-golden.

**C3 (gated spike) — rewind+replay for over-band corrections.** Only if the §3 harness still shows felt lurches from *legitimate* large corrections (real collisions/shoves) after C1+C2: replace the eased glide for errors in `(DeadZone, Snap)` with retail's `ClientResimulate` shape (`U3:Player/PlayerInput.cs:1268-1346`): teleport the shell to the authoritative sample, then replay the unacked `MoveInput` ring (the client already retains per-seq inputs implicitly; make the ring explicit, ~16 entries) by re-invoking the movement step + `MoveAndSlide` N times inside one `_PhysicsProcess`. **Spike first** (this is the honest-uncertainty item): Godot allows repeated `MoveAndSlide` calls per tick (each is synchronous sweeps against the space), and the deterministic-ground path already avoids per-body contact state (`game/PlayerController.cs:1311-1327`), but stance/velocity/floor-snap state must be rewound with the position, and Jolt query cost is ~5–8 sweeps per correction at 100 ms. If the spike fails, keep the eased glide — after C1+C2 it only ever carries genuinely-diverged state, which is also when a visible glide is *correct* feedback. No wire change (MP_PLAN §2.5c: the protocol already carries everything replay needs).

### 4.3 Tests (regression rule — same commit as each fix)

- **C1:** L0 in `tests/UnturnedNet.Tests/InputQueueTests.cs` company: `MoveInputRedundancy_SingleLoss_NoHole_NoCoast` (seeded loss eats one datagram; assert the server integrates the full seq run with zero coast ticks — FAILS pre-C1), golden bytes for the new layout. L1: `net.shell_wan_sprint_turns` (from §3) tightens its correction bound to ~LAN levels.
- **C2:** L0: `AckBand_SubBandDrift_ResolvesServerWard_ZeroClientCorrection` (inject artificial per-tick avatar offset < band; assert entity adopts claimed pos and client `CorrectionAppliedMeters == 0`) + `AckBand_BudgetExceeded_FallsBackToClientCorrection` (a lying client claiming +1 m/s; assert adoption stops at budget and the server position wins — the anti-cheat test). L1: `net.shell_wan_walk` asserts ~zero applied correction over a steady WAN walk (the "worm is dead" bar); the existing shove case in `net.shell_walk_reconcile` still hard-snaps (guard intact).
- **C3 (if built):** L0 replay-parity sim (replay of a recorded input window from a rewound base lands within quantization of the original prediction); L1 `net.shell_wan_shove_replay` (server-side teleport mid-walk; assert one-tick resolution, no multi-tick glide).

### 4.4 Scope/risk

Small-medium. C1 is mechanical (wire + enqueue loop). C2 touches the choke point and needs the budget logic — the riskiest line is the adoption interacting with `ServerTeleport`-style external moves; the existing "entity moved outside this sync → body adopts entity" guard (`game/PlayerNetSync.cs:99-111`) already sequences external teleports above write-backs, and adoption must ride the same guard. C3 is the only genuinely uncertain piece and is explicitly gated. Rollback story per phase: each is a Version-gated behavior — reverting is a revert.

---

## 5. Part A — vehicle driving (largest; the felt-latency killer)

### 5.1 The decision: mirror retail's client authority, not predict+reconcile

The brief's default framing ("run the same `Vehicle.Drive` sim locally, record under input seq, reconcile against vehicle snapshots + `LastProcessedInputSeq`") is the Rocket-League-shaped answer, and it fails honestly here:

- Reconciliation without replay means every client/server divergence — and two independent `VehicleBody3D`s diverge *fast*; suspension contact points, tire slip and chassis attitude compound at highway speed in ways two walking capsules never do — resolves as a visible eased correction of a **fast-moving** body: the floaty feel reborn as rubber-banding. Reconciliation *with* replay requires re-stepping Jolt vehicle physics N times mid-tick, which Godot does not expose (`VehicleWheel3D` solves inside the physics server's step) — and the port's own determinism boundary (MP_PLAN §2.5) forbids betting the design on two Jolt instances agreeing.
- **Retail solved this exact problem by not solving it**: authority migrates to the driver (§1.2). One physics instance, zero steady-state corrections, at any RTT. The server's job collapses to plausibility-bounding a reported transform — cheap, engine-free, already half-shaped like the port's validators.
- Cost: a scoped, explicit carve-out from "the server owns all truth" (CLAUDE.md MP recipe #1) for *the driven vehicle's transform only*, bounded by the retail envelope. On the current private test server (cheats on, deployable ownership deferred — MP_PLAN §7) this is **above** the existing posture floor, and it is the posture retail ships on public servers. Logged as a §7 revisit item for untrusted hosting.

Everyone who is not the driver changes nothing: puppets, dead-reckoning, enter/exit facts all stay.

### 5.2 Design

**A1 — the driver's local vehicle (client).**
- On `VehicleEntered(self)` + puppet present (the existing gate, `game/ClientWorldSession.cs:186-193`): instead of `EnterPuppet(pup)`, build a **real local `Vehicle`** via the spec path (`Vehicle.BuildByName(SpecNames[TypeId], Variant)` — `game/Vehicle.cs:842-848,861`) at the replica transform + velocity, hide that NetId's puppet (a `VehicleReplicaView` suppress-set keyed by NetId; the replica *store* keeps mirroring snapshots verbatim — hash-parity and the C3 "replicas mirror snapshots" bar stay intact, only the *view* switches), and seat the shell on it through the SP direct-drive path (`Shell.EnterVehicle`-equivalent; `ShellStep`'s `if (Shell.IsDriving) return` at `game/ClientWorldSession.cs:180` becomes the live MP branch instead of dead code).
- Per tick while driving: the shell drives the local vehicle exactly as SP does (`DriveVehicle` → `Vehicle.Drive`, `game/PlayerController.cs:2719`, `game/Vehicle.cs:1224`) — **the wheel now answers in 0 ms** — and the session sends `VehicleStateCommand` (below) instead of `DriveInputCommand`.
- Incoming vehicle snapshots for the driven NetId are ignored by the *view* (retail `tellState` early-return, `U3:Interactable/InteractableVehicle.cs:2113-2116`); unlike retail we keep *sending* them (the snapshot system is global + sync-checked; suppressing per-client blocks would complicate the composer for no bandwidth that matters at this scale).

**A2 — the wire.** New `CommandVehicleState = 26`, UnreliableSequenced @ 25 Hz (every 2nd tick — matches the snapshot cadence; 50 Hz is available if feel demands it):
```
Seq:u16, NetId:u32, RecovAck:u8,
Pos (position grid), Yaw/Pitch/Roll (existing degree quantization),
LinVel, AngVel (NetWire.WriteVel), SteerDegrees (SteerBits),
Throttle/Steer/Handbrake axes (the old DriveInput payload — kept for wheel/light dressing + server fallback),
Flags:u8 (braking, lights…)
```
≈ 30 bytes ≈ 750 B/s uplink. Latest-wins by `Seq` server-side (the `DriveInputCommand` guard pattern, `core/UnturnedNet/VehicleReplication.cs:143-149`). `CommandDriveInput = 23` stays registered (append-only registry; it remains the non-predicted fallback and the passenger-era seam). **Version bump.**

**A3 — server adoption + the retail envelope (`ServerVehicles`, engine-free where possible).** On `VehicleStateCommand` from the validated driver (sender identity from the connection, as always):
- **Envelope, straight from retail §1.2:** per-packet horizontal delta cap `sqrDelta = (Spec.SpeedMax × interval × slack)²` with the fuel-empty 0.5 m override (retail `U3:Interactable/InteractableVehicle.cs:3096-3105`, `U3:Bundles/VehicleAsset.cs:2319-2329`); vertical speed caps `validSpeedUp = 12.5 / validSpeedDown = 25` m/s for cars (`U3:Bundles/VehicleAsset.cs:2336-2349`); plus NaN/extent sanity. Like retail, speed/rotation/steer are dressing — not validated in v1.
- **Pass:** write the reported state into the `VehicleReplication` entity (the `ServerPublish` path, `core/UnturnedNet/VehicleReplication.cs:106+`) — observers' puppets now dead-reckon off the driver's truth; keep teleporting the driver's player entity onto it (`Step`, `:554-564` — unchanged).
- **Fail:** **recov**, retail-shaped (`U3:Interactable/InteractableVehicle.cs:2095-2109,3069-3085`): new `EventVehicleRecov = 29` (ReliableOrdered, to the driver): `{NetId, Pos, Yaw/Pitch/Roll, LinVel, RecovCounter:u8}`. Client teleports its local vehicle to the payload, zeroes velocity, freezes it (`RigidBody3D.Freeze`) until its outgoing `RecovAck` echoes the counter; server discards state packets whose `RecovAck` lags the counter. Server keeps publishing its last-good state meanwhile.
- **The server's own `Vehicle` node while driven:** retail flips kinematic (`updatePhysics`); Godot equivalent — `Freeze = true` with `FreezeMode.Kinematic` + per-tick teleport to the adopted state in `VehicleNetSync.Tick` (which stops calling `v.Drive` for predicted drivers). The node stays in the tree so server-side ballistics/occlusion/interaction still see the car at the right place, and kinematic-vs-body contacts still shove server avatars. On exit/disconnect/explode: unfreeze, `Wake()`, seed the body from the last adopted state — physics authority returns to the server **exactly** as retail's `removePlayer → updatePhysics` (`U3:Interactable/InteractableVehicle.cs:2360,1490-1519`); undriven vehicles remain fully server-simulated, unchanged.

**A4 — edges.**
- **Enter:** unchanged round trip; between `VehicleEntered` and the first accepted state packet the server keeps its node live under the old `DriveInput` (held input possibly zero — the car sits still ~1 RTT before the world sees it move; the *driver* feels instant response immediately). No seat handoff race: authority follows `DriverPlayerId`, which only the server writes.
- **Exit:** client sends `ExitVehicle` as today; on `VehicleExited` destroy the local vehicle and restore the shell at the event's authoritative spot (`game/ClientWorldSession.cs:273-294`, unchanged). The server computes the exit spot from *its* node, which now holds the adopted transform — consistent by construction.
- **Mid-drive despawn/explode (server-initiated):** snapshot flag `Exploded` → the driver's session force-exits locally (destroy local vehicle, shell to the exit fact / in-place fallback — the `RidePuppet` despawn-hold shape, `game/PlayerController.cs:1282`).
- **Driver disconnect:** `OnPeerDisconnected` frees the seat (`core/UnturnedNet/NetWorldHost.cs:115`) → unfreeze path above.
- **Collisions:** the local car collides with static world client-side; remote players/zombies are non-physical client-side (§2.2) so the driver passes through them locally — the server (kinematic body + its own entity positions) remains the authority for any run-over gameplay, identical to retail's posture. Flag: vehicle-vs-vehicle between two predicted drivers resolves as envelope-checked interpenetration, as in retail; acceptable at this player count.

### 5.3 Why not `LastProcessedInputSeq` on the vehicle wire

The brief asked to verify/design it. Under client authority it is **unnecessary** — there is no server-side vehicle re-simulation to ack against; the recov counter is the only reconciliation primitive, mirroring retail (which has no vehicle input-seq ack either — the walking ack machinery is explicitly skipped for DRIVING, `U3:Player/PlayerInput.cs:1750-1780` vs `:1817-1839`). If a future pass tightens authority (server re-sim + reconcile), the seq field already exists on both `DriveInputCommand` and `VehicleStateCommand` to build on — the protocol keeps the door open, per the MP_PLAN §5 "decide the fields, defer the algorithm" discipline.

### 5.4 Tests

- **L0** (`tests/UnturnedNet.Tests/VehicleReplicationTests.cs` company, engine-free — the envelope math and adoption are core-side):
  - `VehicleState_DriverAdopted_ObserverSeesDriverTruth` — driver client streams a scripted state track through the MemTransport harness; assert the entity + observer replica converge on it and `StateHash` parity holds (the golden-bytes test re-goldens with the version bump).
  - `VehicleState_TeleportBeyondEnvelope_TriggersRecov_AndFreezeUntilAck` — a 50 m jump: assert reject + recov event + stale-`RecovAck` packets discarded + resume after echo (FAILS without the envelope).
  - `VehicleState_VerticalSpeedCap_Rejected`; `VehicleState_NonDriver_Rejected` (spoofed sender — the choke-point identity rule); `VehicleState_FuelEmpty_TightCap`.
  - `Recov_WireRoundTrip_GoldenBytes` for the new event.
- **L1** (`game/testing/tests/NetTests.cs`, WAN profile from §3):
  - `net.shell_drive_predicted` — the `net.vehicle_drive_sync` rig (`:517+`) under WAN: assert (1) the driver's local vehicle responds to a scripted steer within **1 tick** (the feel bar — FAILS today by ~RTT), (2) the server entity and the observer puppet converge to the driver's track within tolerance, (3) desync-quiet, (4) exit restores the shell at the server spot.
  - `net.shell_drive_recov` — inject an out-of-envelope teleport into the client's state stream mid-drive; assert the rollback lands on the driver's local vehicle and driving resumes.
  - `net.shell_drive_handoff` — enter → drive → exit → server node resumes physics (assert the car keeps rolling/settling server-side post-exit); driver disconnect variant.

### 5.5 Scope/risk (the honest part)

**Largest of the three parts.** New: one command, one event, client vehicle lifecycle (build/destroy/freeze), server adopt+envelope+freeze path, view suppression. Reused wholesale: `Vehicle` build/drive, puppet view, enter/exit flow, snapshot format (unchanged!). Risks:
1. **Authority carve-out** — deliberate, envelope-bounded, retail-precedented; documented in MP_PLAN §7's revisit list. The envelope is *the* anti-cheat; keep its constants spec-derived, not hardcoded.
2. **Godot freeze/unfreeze fidelity** — `RigidBody3D.Freeze` + kinematic teleport on a `VehicleBody3D` (wheels keep raycasting while frozen?) needs a spike; fallback is removing/re-adding the body or zero-gravity + direct state writes via `PhysicsServer3D`. Isolated to `VehicleNetSync`.
3. **Client-side world parity** — the driver now collides against the *client's* static world; the content-hash join gate (`core/UnturnedNet/NetServerSession.cs:192-196`) is what makes that sound. Any future server-only world mutation (destructible objects) must replicate before this assumption breaks — note added to the §7 list.
4. **Two-body determinism is 100 % dodged for the driver** (one body), which is exactly why this design wins; it resurfaces only at the enter/exit boundaries, both of which are event-teleport-anchored already.

---

## 6. Part B — combat: instant hitmarker + server lag compensation

### 6.1 Design

**B1 — the server history ring (core, engine-free).** New `PositionHistory` (in `core/UnturnedNet/`, owned by `ServerCombat`): per player entity and per zombie entity, a ring of quantized `Pos` per tick, **depth 32 ticks (640 ms)** — covers 300 ms RTT + client view delay with slack (retail keeps a full second at the same 50 Hz cadence — `CAPACITY = 50`, `U3:Player/CapsuleHistory.cs:141-144`; we start tighter because our rewind is clamped, below). Recorded once per tick at the top of `TickReplication` (`core/UnturnedNet/NetWorldHost.cs:245`) — after ALL of the tick's mutation, before the snapshot-divisor early-out, so the ring always matches what clients were shown. Positions are already final there (§2.3); this is memory (~32 × 12 B × entities), not sim.

**B2 — rewound validation.** `FireCommand` gains `ViewTick:u32` = the client's `Applier.LastAppliedServerTick` at trigger time (the server tick whose state the shooter's puppets are rendering — the port's replica views chase the newest applied snapshot, `game/VehicleReplicaView.cs:65-71` / `ZombiePuppets`, so LastAppliedServerTick is the honest first-order stamp; refine with the ~1-2-tick glide lag empirically on the §3 harness). Server, in `OnFire` (`core/UnturnedNet/ServerCombat.cs:178-206`):
- `rewind = clamp(receiveTick − ViewTick, 0, MaxRewindTicks = 25)` (500 ms — the anti-cheat bound on how far into the past a shooter can claim to live; beyond it the shot validates against live positions, i.e. degrades, never rejects — a laggy-but-honest peer still hits stationary targets). Stamp `rewind` onto each spawned `Bullet`.
- `StepBullets` (`:265-349`): each segment test against a victim uses `history[victim, currentTick − rewind]` instead of live `Pos` — the Source model ("rewind entities to what the attacker saw") keyed by the client's stamp, which is Source's exact scheme, with retail's window-union (`ContainsPoint`) as the documented simpler fallback if per-tick lookup proves too strict in practice. Cylinder dimensions unchanged; add a fixed **0.15 m expansion** to the cylinder radius during rewound tests (quantization + puppet-vs-entity render offset; retail uses a blunt 0.75 m for animation reach — ours can be tighter because we test against sim positions, not animated bounds).
- Everything else in the server pipeline — firerate/ammo/origin gates, occlusion `WorldRay` (world geometry is static; no rewind needed), damage application, `HitConfirm`/`ImpactFx` events — is unchanged. Melee stays un-compensated in v1 (2 m reach; revisit with PvP). **Version bump** (FireCommand layout + golden re-bake).

**B3 — the client-predicted hitmarker (cosmetic, immediate).** The MP-cosmetic bullets already fly the identical ballistic path client-side (`game/PlayerController.cs:2159`, same constants as `ServerCombat.StepBullets`). Add: each cosmetic step also tests `SegmentHitsCylinder` (the math is engine-free in core — reuse it, `core/UnturnedNet/ServerCombat.cs:529-546`) against the *rendered* replica positions (`RemotePlayers` avatars, `ZombiePuppets`; skip dead ones). On a crossing: `HitmarkerHUD.Instance.Show(headZone)` **immediately** — retail's `PlayerUI.hitmark` timing (`U3:Useable/UseableGun.cs:1691,1706`), Overwatch's favor-the-shooter feedback. Bookkeeping: remember the fire `Seq`s that showed a predicted marker; the `HitConfirmed` handler (`game/ClientWorldSession.cs:105-109`) skips the *marker* for those seqs (no double-flash) while still driving the damage log/kill feed — and still shows the marker for confirms that were *not* predicted (server generosity beats client pessimism). A predicted marker the server rejects simply never gets damage — cosmetic, self-correcting, exactly retail's behavior (which doesn't even have the confirm to reconcile with). Wired only when `NetFire != null` — SP untouched.
- Deliberate divergence from retail, stated: the client's claim does **not** drive target selection — the port keeps "the server steps the authoritative bullet" (`core/UnturnedNet/CombatReplication.cs:9`) and uses the client stamp only to rewind time. That is *stricter* than retail (which trusts the claimed entity+point against a 1 s window) and costs only the ring lookup. If later PvP tuning wants retail's full favor-the-shooter, the claim fields ride the same command — protocol door open, algorithm deferred.

### 6.2 Tests

- **L0** (`tests/UnturnedNet.Tests/ServerCombatTests.cs` company):
  - `Fire_MovingTarget_HitAtRewoundPosition` — victim strafes at sprint speed with a scripted 6-tick view delay; a shot aimed at the *delayed* position hits with rewind and **misses without it** (the teeth: FAILS pre-B2); mirrored `Fire_LiveAimMisses_WhenTargetDisplaced`.
  - `Fire_RewindClamp_BeyondMaxUsesLive` (a `ViewTick` claiming 2 s ago degrades to live positions — the anti-cheat bound); `Fire_FutureViewTick_ClampsToZero`.
  - `PositionHistory_RingWrap_And_EntityChurn` (join/leave mid-ring never dereferences stale entries).
  - Golden bytes for the new `FireCommand`.
- **L1**: `net.shell_fire_hitmarker_wan` — the `net.shell_fire_zombie` rig (`game/testing/tests/NetTests.cs:2131+`) under the §3 WAN profile: assert (1) the predicted marker fires within **1 tick** of the trigger against a replica-visible zombie (FAILS today by ~RTT + flight), (2) the kill still lands server-side with `HitConfirmed`/`ZombieDied` flowing, (3) no double-marker (predicted + confirm counted once), (4) a shot at a *vacated* spot shows a predicted marker but no damage — the documented cosmetic mispredict. `net.shell_fire_strafing_zombie_wan` — the moving-target rewind proven end-to-end in-engine.

### 6.3 Scope/risk

Medium-small, and independent of A/C. The ring + rewind are engine-free core with pure-function tests. Risks: `ViewTick` honesty (bounded by the clamp — worst abuse = a 500 ms-stale world, the same power any actually-laggy client has); marker/confirm dedupe UX (worst case a double flash — trivial); zombie puppet render offset making predicted markers optimistic (bounded by the 0.15 m expansion mirror; tune on the harness). PvP is off on the dedicated server today (`game/DedicatedServer.cs:78`), so the risky half (player-vs-player rewind) ships dark and soaks on zombies first.

---

## 7. Wire & registry summary

| Change | Plane | Id | Version |
|---|---|---|---|
| MoveInput carries last-3 inputs (C1) | command 1 layout | — | bump + re-golden |
| MoveInput carries `clientPosition` (C2) | command 1 layout | — | bump (same bump if landed together) |
| `VehicleStateCommand` (A) | new command | **26** | bump |
| `EventVehicleRecov` (A) | new event | **29** | (same bump as A) |
| `FireCommand` + `ViewTick` (B) | command 2 layout | — | bump |

All registry ids append-only per `core/UnturnedNet/PlayerReplication.cs:9-11`; every bump re-goldens the affected byte tests in the same commit (`core/UnturnedNet/NetProtocol.cs:8-9`). No snapshot-format changes anywhere in this plan — `LastProcessedInputSeq` already rides the player block (`core/UnturnedNet/PlayerReplication.cs:601`) and the vehicle block is untouched.

## 8. SP-byte-identical strategy (per the CLAUDE.md recipe)

- **C:** all server-side changes live in `PlayerReplication`/`PlayerNetSync` (MP-only classes); the client changes live in `ClientWorldSession.ShellStep` + the reconciler (MP-only). `DeterministicGround` and the null seams stay the only PlayerController touchpoints — no new SP-visible state.
- **A:** the local-vehicle path is created only by `ClientWorldSession` (the `NetEnterVehicle` wiring site, `game/ClientWorldSession.cs:242`); SP's direct `EnterVehicle`/`Drive` path is *reused unchanged*, not modified. Server changes live in `VehicleNetSync`/`ServerVehicles`. `MpLoopback` keeps the loopback shape (local shell = authority; no state command sent to yourself).
- **B:** predicted-marker code gates on `NetFire != null` (the existing D1 seam, `game/PlayerController.cs:1177-1182`); the ring/rewind is server-core. SP's direct-hitmarker branch (`:2187`) is untouched.
- The bar, as always: `./test.sh` green including every existing `net.shell_*`, and the SP L2 goldens unmoved.

## 9. Phasing & risk register

**Order: §3 harness → C1 → C2 → A1–A4 → B1–B3 → (C3 if the harness demands it).** C first because it is the smallest diff on the hottest path and produces the measurement rig everything else is judged by; A next because it is the biggest felt complaint and its spike items (freeze fidelity) want the longest soak on the live server; B last because it is independent and lowest-risk, and its PvP half is dark anyway. Each phase lands green, versioned, and revertible on its own.

| Risk | Part | Mitigation |
|---|---|---|
| Two-body Jolt non-determinism (the through-risk) | C | never bet on it: redundancy makes the server integrate the true stream; the ack band absorbs residue server-ward; snap guard for real divergence. It is *structural* — the goal is invisibility, not elimination |
| Adoption band as a speed exploit | C2 | 0.5 m/s cumulative budget at the choke point + L0 lying-client test |
| Rollback-replay infeasible in Godot | C3 | gated spike; the plan is complete without it |
| Authority carve-out for driven vehicles | A | retail-precedented envelope (delta cap, vertical caps, recov); private-server posture; MP_PLAN §7 revisit note before public hosting |
| `VehicleBody3D` freeze/unfreeze fidelity | A | spike first; `PhysicsServer3D` direct-state fallback; isolated to `VehicleNetSync` |
| Client static-world parity assumption | A | content-hash join gate already enforces it; flag on any future destructible-world work |
| `ViewTick` abuse | B | 25-tick clamp; degrade-to-live, never reject |
| Predicted-marker mispredicts | B | cosmetic by design (retail ships worse); dedupe on confirm; expansion tuned on the harness |

The Unturned source made all three calls before us — same tick model, same physics-engine constraints, same trade-offs — and shipped them to millions of players. This plan is those three calls, translated onto the port's three-plane, versioned, golden-tested stack.
