# CARS_DRIVE_SPIKE — why a seated MP player can't drive (read-only diagnosis, 2026-07-17)

Live symptom (real PEI, dedicated server): a joined player walks to a replicated car, presses F,
**gets seated** ("i got into the car" — chase cam engages, shell hides), but the engine never audibly
starts and the car **never moves** under WASD. Also: **no outline highlight** when looking at a car.
The C6 L1 test `net.vehicle_drive_sync` passes.

All five requested paths were traced end-to-end. Verdict up front:

> **The client input path is fine and the server command path is fine. The killer is server-side
> physics: with `RemoteAvatars = true` (live only — both vehicle L1 tests run without it), the seated
> player's avatar body — a live CharacterBody3D collision capsule — is teleported INTO the driven
> vehicle's center every tick. The car is permanently in deep penetration with an effectively-static
> kinematic capsule and cannot accelerate.** The missing engine sound is a separate, guaranteed
> presentation gap: `VehiclePuppet` renders no audio and no HUD, so even a working drive would *feel*
> dead. The no-highlight issue is a third, independent gap: puppets have no collision, and the
> look-focus system only recognizes `Vehicle`-typed colliders.

---

## H1 (PRIMARY): the seated avatar's collision capsule rides inside the driven vehicle

**The chain, file:line:**

1. `ServerVehicles.Step` — `core/UnturnedNet/VehicleReplication.cs:539-549` — every tick, each
   driver's **player entity** is teleported onto the vehicle: `_players.ServerTeleport(kv.Key, v.Pos,
   tick)` (`:546`), where `v.Pos` is the vehicle entity's replicated center. Called from inside
   `TickSimulation` at `core/UnturnedNet/NetWorldHost.cs:165` — i.e. before the per-tick sync steps.
2. `PlayerNetSync.Tick`, seated branch — `game/PlayerNetSync.cs:87-96` — for a seated peer
   (`VehicleHost.IsDriver`), the avatar **body follows the entity**: `t.Body.GlobalPosition =
   ToG(e.Pos)` (`:89`), velocity zeroed. So the body is snapped to the **vehicle's center** at 50 Hz.
3. That body is a real `PlayerController { NetAvatar = true }` (`game/PlayerNetSync.cs:72`) — the
   NetAvatar construction **keeps the collision capsule** (`game/PlayerController.cs:1228-1234`), and
   **nothing ever disables it while seated**. Compare the two places that DO know this is fatal:
   - SP `EnterVehicle` disables every `CollisionShape3D` with the comment *"stop the player body
     fighting the vehicle"* — `game/PlayerController.cs:2549-2550`;
   - the MP client shell's `EnterPuppet` does the same — `game/PlayerController.cs:1185-1186`.
   The server avatar is the one seated body in the codebase that keeps its capsule live.
4. Collision is mutual: the vehicle body is on layer bit0|bit5 (`game/Vehicle.cs:918-919`) with the
   default mask (bit0); the avatar capsule is a default-layer CharacterBody3D (bit0 layer, bit0 mask).
   A CharacterBody3D that isn't being moved is a static obstacle to Jolt — a rigid VehicleBody3D in
   deep penetration with it gets depenetration pushback every step, re-established every tick because
   step 1-2 teleport the capsule back onto the car's new center. The avatar's own `_PhysicsProcess`
   also still runs the full on-foot path (gravity + MoveAndSlide from inside the hull), thrashing the
   contact.

**Concrete failure:** engine force (server-side `EngineOn` is genuinely true — set at
`game/VehicleNetSync.cs:95`, and `Drive` only gates throttle on `EngineOn`, `game/Vehicle.cs:1190`)
fights a permanent embedded kinematic obstacle. The car sits pinned or jitters in place; the driver's
streamed WASD arrives, validates, and is applied — and the vehicle still doesn't go anywhere. This
reads exactly as "seated, engine won't start, can't move."

**Why the L1 test passes:** `net.vehicle_drive_sync` boots its `DedicatedServer` **without**
`RemoteAvatars` (`game/testing/tests/NetTests.cs:542` — no `RemoteAvatars = true`), so no avatar
bodies exist at all and the car drives clean; same for the enter/exit churn test (`:440s`). The live
server sets `RemoteAvatars = true` (`game/Main.cs:1933`). Five OTHER net suites do run
`RemoteAvatars = true` (`NetTests.cs:660,779,886,997,1200,1340,1450`) — none of them drives a
vehicle. That is the precise coverage hole: **avatars × vehicles was never tested together.**

**How to confirm:**
- *Test (cheapest, decisive, and per the regression rule this is the test that ships with the fix):*
  clone `NetVehicleDriveSync` with `RemoteAvatars = true` on the `DedicatedServer` line and drive via
  the real fact chain (or even the same raw `SendDriveInput` pump). Expect on current main: the
  `driven > 8f` check (`NetTests.cs:594`) fails with driven ≈ 0, and/or the vehicle visibly jitters.
- *Live (no code):* `UG_NETLOG=1` on the server — the 1 Hz `[NET] 1s:` rollup will show **zero cmd
  rejects** while a rider holds W (proving the input path is healthy) while the vehicle's published
  pos stays put. That log signature separates H1 from any input-path theory.

**Proposed fix:** mirror the SP invariant on the server avatar. In `PlayerNetSync.Tick`'s seated
branch (`game/PlayerNetSync.cs:87-96`), on the unseated→seated transition disable the body's
`CollisionShape3D`s (the exact SP `EnterVehicle` loop, `PlayerController.cs:2549-2550`) and stop its
per-tick physics fighting (velocity already zeroed; consider also skipping the gravity/MoveAndSlide
step while seated); re-enable on the seated→unseated adopt branch (`:97-105` — the `t.Seated` flag
already marks the transition both ways). Alternative (bigger hammer): don't co-locate the body with
the vehicle at all while seated — the entity teleport in `ServerVehicles.Step` already owns the
replicated truth, the body adds nothing while driving. Recommend the collision-disable: minimal,
symmetric with SP/shell, keeps the body positioned for future seated-hit logic.

---

## H2 (SYMPTOM AMPLIFIER): "the engine doesn't start" is guaranteed — the puppet renders no engine state

- `VehiclePuppet` is explicitly *"NO VehicleBody3D, no wheels physics, no collision, **no audio**"* —
  `game/VehiclePuppet.cs:5-8`.
- The engine flag **is** replicated (`FlagEngineOn` packed at `game/VehicleNetSync.cs:120`, decoded
  as `VehicleEntity.EngineOn`, `core/UnturnedNet/VehicleReplication.cs:55`) but
  `VehicleReplicaView._Process` (`game/VehicleReplicaView.cs:36-87`) never reads it — no engine
  loop, no headlight/taillight rendering either.
- Ride mode also has no vehicle HUD: SP `EnterVehicle` sets `Hud.Vehicle = v`
  (`game/PlayerController.cs:2546`); `EnterPuppet` (`:1179-1191`) sets nothing.

**Concrete failure:** even with H1 fixed, a rider gets zero start-up feedback — silent car, no
gauges. Half the report ("engine doesn't start") is this gap talking.

**Confirm:** code-only fact (nothing consumes `e.EngineOn` client-side); or after fixing H1, drive
and note the silence.

**Proposed fix:** give `VehiclePuppet` an engine `AudioStreamPlayer3D` gated on the replicated
`EngineOn`, pitch off replicated forward speed (the `Vehicle` engine-audio recipe,
`game/Vehicle.cs:1630-1663` territory), driven from `VehicleReplicaView._Process`; set a HUD vehicle
box in ride mode (fuel/health/battery all already ride the snapshot). No wire changes — the data is
already there.

---

## Traced and REFUTED (the prompt's other suspects)

### Prompt #1 — "UiInputBlocked zeroes WASD in ride mode": NO

- `UiInputBlocked => Input.MouseMode != Captured` (`game/PlayerController.cs:2577`).
- The MP shell spawns with `CaptureMouse = true` (`game/ClientWorldSession.cs:206`) and captures at
  `_Ready` (`game/PlayerController.cs:1707`). `EnterPuppet` (`:1179-1191`) does **not** touch the
  mouse mode — the mouse stays Captured through the seat, so `RidePuppet` (`:1212-1226`) polls WASD
  normally (`:1220-1221`; identical axes to SP `DriveVehicle` `:2587-2588`). The event-gate at
  `:1723-1728` only filters *event* keys (F/H/L/Ctrl/Esc) and doesn't affect polled input.
- The blocked case exists only when a menu is deliberately open (Tab inventory `:1844`, K craft
  `:1850`, J skills `:1855`, Esc pause, F1 console `game/DevConsole.cs:63`) — modal by design, not
  the systematic live failure.

### Prompt #2 — "EnterPuppet never engages (netId mismatch)": NO — the ids provably match

- The client asks with the puppet's own id: `NetEnterVehicle(p.NetId)`
  (`game/PlayerController.cs:1165`), where `pup.NetId = e.NetIdValue` was stamped by the replica view
  (`game/VehicleReplicaView.cs:52`).
- The server validates + broadcasts the **same** id back: `ServerEnter` → `VehicleEnteredEvent {
  NetId = netId }` (`core/UnturnedNet/VehicleReplication.cs:491-498`).
- The client latches it (`game/ClientWorldSession.cs:88`) and `TryGetPuppet(_ridingNetId, …)`
  (`:168`) looks up the same `NetIdValue`-keyed dictionary the view populated
  (`game/VehicleReplicaView.cs:27,57`). One id space end-to-end; vehicles have no interest filtering
  (`DedicatedServer.cs:88-89` sets Interest for zombies/world-items only), so the puppet always
  exists. The live report confirms engagement: the chase cam + hidden shell the player experienced
  ARE `EnterPuppet`'s effects.
- **Fragility worth noting (not the live bug):** if `TryGetPuppet` ever misses, `ShellStep:164-171`
  freezes the shell silently forever (no walk input, no corrections, no timeout). Fine as designed
  today; a `[CLIENT]`-log after ~2 s of waiting would make any future regression here loud.

### Prompt #3 — "server doesn't drive from the streamed input": NO — that path is healthy (modulo H1)

- Client sends `(netId=_ridingNetId, throttle=LastDriveInput.y, steer=LastDriveInput.x, handbrake)`
  (`game/ClientWorldSession.cs:167`) matching `SendDriveInput(vehicleNetId, throttle, steer,
  handbrake)` (`core/UnturnedNet/NetWorldHost.cs:545-552`). Axis packing is correct
  (`LastDriveInput = (steer, throttle)`, `game/PlayerController.cs:1223`).
- Server validation: `_drivenByPlayer[sender] == cmd.NetId`
  (`core/UnturnedNet/VehicleReplication.cs:472-474`) — populated by the same `ServerEnter` (`:495`)
  that emitted the event the client echoes. Match by construction.
- `VehicleNetSync.Tick` applies it: dedicated has `LocalPlayerId = null` → `localId = 0` → any driver
  is `remote` (`game/VehicleNetSync.cs:58,88`); enter side effects `EngineOn = true; Wake()`
  (`:95-96` — `Wake` clears the spawn-park freeze, `game/Vehicle.cs:204,1179`), then
  `v.Drive(inp.Throttle, inp.Steer, inp.Handbrake)` every tick (`:106-107`). The parked/freeze
  re-engage can't bite a driven car (non-parked `wantHold` requires `_handbraking`,
  `game/Vehicle.cs:1613-1615`, and `Drive` clears `_parked` each call, `:1189`).
- PEI vehicles are first-class: `SpawnPeiVehicles` builds them via `Vehicle.BuildByName`
  (`game/WorldBuilder.cs:336`), which routes through the common builder — `AddToGroup("vehicles")`
  (`game/Vehicle.cs:921`), full fuel + battery (`:929`) — so `VehicleNetSync.Tick` mints them exactly
  like the test's hand-placed jeep (`game/VehicleNetSync.cs:61-73`).
- The `net.vehicle_drive_sync` pass proves this whole chain physically drives a car >8 m on a
  headless server (`game/testing/tests/NetTests.cs:585-596`) — with no avatar bodies present. What it
  never covers: the client half (shell/EnterPuppet/WASD — it streams `SendDriveInput` raw, `:536-540`)
  and `RemoteAvatars = true` (H1).

### Prompt #5 — entry reach: WORKS now (consistent with "i got into the car")

- `CanEnter` gates on the server-side entity pos: `(v.Pos - p.Pos).magnitude <= EnterReach (6 m)`
  (`core/UnturnedNet/VehicleReplication.cs:481-488`). With RemoteAvatars the entity is written back
  from the avatar body every tick (`game/PlayerNetSync.cs:110-112`), which tracks the client's
  MoveInput; after the determinism/rubberband fixes (commits `34645b3`, `85bf0ee`) the server pos
  hugs the shell, so a player standing at the door is well inside 6 m. Live evidence agrees: the
  seat was granted. Pre-fix, a drifted server pos failing this check was very plausibly the old
  "F does nothing" symptom.

---

## The no-highlight UX gap (prompt #4) — confirmed, independent of the drive bug

- SP highlight source: `UpdateLookFocus` (`game/PlayerController.cs:135-202`) finds vehicles ONLY as
  `Vehicle`-typed colliders — ray + sphere against layer bit5 (`:146,154`, hit-cast at `:165`) plus a
  `"vehicles"`-group oriented-hull fallback (`:175-180`) — then `_focusVehicle` (typed `Vehicle`,
  `:120`) gets `SetLookFocused(true)` (`:190-194`) and the F-enter branch (`:1795`).
- MP cars are `VehiclePuppet`: plain `Node3D`, **no collision at all** (`game/VehiclePuppet.cs:5-10`),
  in the `"vehicle_puppets"` group not `"vehicles"` (`game/VehicleReplicaView.cs:53`). Nothing in
  `UpdateLookFocus` can ever see one → no outline, no name/health/fuel billboard, no `[F] Enter`
  prompt.
- Entry only works because the C6 seam is *proximity*-based, not look-based:
  `RequestEnterNearestPuppet` → `NearestPuppet` ≤ 4 m (`game/PlayerController.cs:1144-1167`), wired
  as the F fallthrough (`:1796`).

**Proposed fix:** either (a) give puppets a focus surface — a cheap `StaticBody3D` box hull on layer
bit5 sized from `MeshSize` (`game/VehiclePuppet.cs:31-39`) plus a puppet-side
`SetLookFocused`/outline pass (the OutlineOverlay already exists on the shell,
`game/PlayerController.cs:1672`), and teach the focus/enter path to accept a focused puppet; or
(b) keep zero-collision puppets and add a puppet branch to `UpdateLookFocus` reusing the
`LookRayHitsHull` oriented-box test against the `"vehicle_puppets"` group. Either way, surface the
`[F] Enter` prompt + name/fuel/health (all already replicated) so MP cars stop being invisible to the
interaction system. (a) also buys bullet impacts on cars client-side later; (b) is smaller.

---

## Ranked summary

| # | Hypothesis | Verdict | Blocks driving? |
|---|---|---|---|
| 1 | Seated avatar capsule teleported into the driven vehicle every tick (`PlayerNetSync.cs:89` ← `VehicleReplication.cs:546`), collision never disabled — car pinned by depenetration | **Root cause (high confidence)** | YES |
| 2 | Puppet renders no engine audio/HUD (`VehiclePuppet.cs:5-8`; `EngineOn` replicated but unread) | Confirmed presentation gap | No — but guarantees the "engine doesn't start" report |
| 3 | Look-focus can't see puppets (no collision, wrong group, `Vehicle`-typed focus) | Confirmed UX gap | No — enter is proximity-based |
| 4 | UiInputBlocked zeroing WASD in ride mode | Refuted (mouse stays Captured through `EnterPuppet`) | — |
| 5 | netId mismatch keeping `EnterPuppet`/`TryGetInput` from engaging | Refuted (one id space end-to-end; seat visibly engaged) | — |
| 6 | Server-side entry reach vs client pos | Works post-rubberband-fix (entry succeeded live) | — |

**Fix order:** H1 (collision-disable in `PlayerNetSync`'s seated branch + the `RemoteAvatars = true`
drive regression test), then H2 (engine audio + ride HUD — otherwise the H1 fix will work and still
feel broken), then the highlight gap. All three are independent changes.
