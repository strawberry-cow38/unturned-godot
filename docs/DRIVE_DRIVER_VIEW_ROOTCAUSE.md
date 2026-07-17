# Root cause: driven vehicle frozen on the DRIVER's own client (MP)

Read-only root-cause analysis, code as of `main` @ `6a84813`. Every claim below is grounded in
file:line from the actual source; the PEI numbers come from parsing the live map data the dedicated
server loads (`/home/ec2-user/unturned/Maps/PEI/Spawns/Vehicles.dat`).

## TL;DR

The driver's client is not being filtered by seat, ownership, or ride state at all. It is stuck in a
**per-client "permanent full snapshot" wedge** in `SnapshotComposer`: after any ≥64-tick (1.28 s) gap
in that client's snapshot acks, the composer flips that client to FULL snapshots on the **unreliable**
channel (1187-byte budget) — and on PEI the full vehicles block is **~2.9 KB (85 vehicles × ~35 B)**,
so it can never fit, is budget-skipped on *every* compose, its per-system baseline can never advance
(advancing requires acking a snapshot that *contained* the block), which keeps `WillSendFull` latched
true forever. Result: for that one client, the vehicle system is silently frozen at its
wedge-time state — invisible while every car is parked, catastrophic the moment anyone (including that
client) drives one. Small/filtered systems (players, zombies, clock) still fit the full budget and keep
flowing, which is why the rest of the world looks alive. The observer client never hit an ack gap, so
it stayed in delta mode, where the moving car's delta (~35 B) always fits.

The driver/observer asymmetry is the **link + rendering profile of the client**, not the seat: VoX's
graphical client (WAN + real rendering, with multi-second first-render/world-stream hitches — e.g. the
one frame in which `VehicleReplicaView` materializes all 85 PEI puppets) is exactly the client that
suffers a >1.28 s ack gap; the headless LAN net-observer is exactly the client that never does.

---

## 1. The winning hypothesis, mechanism step by step

### 1.1 The latch

1. **Snapshot acks drive per-client baselines.** The client acks its newest applied tick every 50 Hz
   tick (`NetWorldHost.cs:409-411`), the composer records it (`SnapshotComposer.SetClientBaseline`,
   `SnapshotComposer.cs:184-202`).
2. **A ≥64-tick ack gap flips the client to full snapshots.** `WillSendFull` condition 1:
   `(serverTick - cs.AckedTick) > DirtyRingDepthTicks` (`SnapshotComposer.cs:213`;
   `DirtyRingDepthTicks = 64` = 1.28 s, `NetQuantization.cs:32`). These fulls go out on the
   **unreliable** stream with the default budget (`NetWorldHost.cs:245` → `Compose` with
   `maxBytes = BudgetBytes = NetProtocol.MaxUnreliablePayload = 1187`, `SnapshotComposer.cs:59`,
   `NetProtocol.cs:71`). Only the *join* path uses the big reliable budget
   (`maxBytes: MaxReliableMessageBytes / 2`, `NetWorldHost.cs:227`).
3. **The full vehicles block physically cannot fit.** `VehicleReplication.WriteFull` writes **every**
   vehicle, unfiltered (`VehicleReplication.cs:182-191`). One vehicle entity is 279 bits ≈ 34.9 B
   (`WriteEntity`, `VehicleReplication.cs:274-291`: 32+8+8+16 id/type/variant/driver, 55 pos, 33
   yaw/pitch/roll, 72 lin+ang vel, 9 steer, 13+11+14 fuel/health/battery, 8 flags). The PEI dedicated
   server spawns a drivable vehicle at every retail spawn point with type ≤ 5
   (`WorldBuilder.cs:300-342`, skip at `:306`): parsing the live `Spawns/Vehicles.dat` gives **95
   points, 85 drivable** (52 civilian / 7 police / 6 fire / 5 military / 2 medic / 13 farm). Full
   block ≈ 85 × 34.9 + 2 ≈ **2 970 bytes** vs a 1187-byte datagram budget — ~2.5× over. Even composed
   FIRST at maximum starvation priority it can never be included.
4. **Skipping pins; pinned baselines only advance on inclusion.** An over-budget block is emitted
   empty and the system's per-client baseline is pinned at the stale `AckedTick`
   (`SnapshotComposer.cs:255-263`, pin at `:262`). A pinned baseline advances **only** when the client
   acks a tick whose recorded `IncludedMask` contains that system (`SnapshotComposer.cs:196-201`).
   Vehicles is never included → its baseline never advances → `WillSendFull` condition 2
   (`SnapshotComposer.cs:214-215`) stays true even after the client's acks resume and condition 1
   clears. **The wedge is permanent for that client** — every subsequent snapshot is a full, and the
   vehicles block is skipped in every one of them (`Diag.OversizedBlocksSkipped` increments forever,
   `SnapshotComposer.cs:258`). Nothing ever resets `SystemBaseline[i]` to −1.
5. **The client sees "frozen", not "gone", and no desync alarm.** An empty block is still dispatched
   to `ReadSnapshot` (`SnapshotApplier.cs:103-107` with a zero-length buffer), where the count read
   fails **before** the full-clear (`VehicleReplication.cs:220-221`) — a harmless no-op, so the
   replica keeps its last state instead of vanishing. And the desync detector is blind by
   construction: the sync-check block is withheld whenever any checked system was budget-skipped in
   the same compose (`SnapshotComposer.cs:146-147`) — vehicles is a checked system
   (`NetWorldHost.EnableSyncCheck`, `NetWorldHost.cs:134-140`, on for `--dedicated`,
   `DedicatedServer.cs:55`) and is *always* skipped, so a wedged client **never receives a single
   sync check** for the rest of its session. Maximum desync, zero banner.

The design comment at `SnapshotComposer.cs:13-17` ("a skipped block LOSES NOTHING — the next included
delta carries everything the skips withheld") is the precise flaw: it is only true for a block that
can *eventually* be included. A system whose lone full block exceeds `BudgetBytes` has no such next
inclusion, and the full-fallback path — the loss-*recovery* mechanism — is the very thing that makes
its inclusion impossible.

### 1.2 Why it froze exactly the vehicle system and nothing visible else

In wedged full-mode, each 25 Hz full snapshot greedily packs blocks in starvation-priority order.
Which systems survive:

| System | Full-block size on the live server | Fate when wedged |
|---|---|---|
| Players, PlayerCombat, Clock, Skills/Inventory (owner-only, `SkillsReplication.cs:123`, `InventoryReplication.cs:311`), Projectiles | tens of bytes | fits → keeps flowing |
| Zombies | relevancy-filtered even in `WriteFull` (`ZombieReplication.cs:118-127` via `ctx.ViewPos`; ring 192 m, `DedicatedServer.cs:88`) | small → keeps flowing |
| World items | relevancy-filtered (`WorldItemReplication.cs:200-209`; ring 128 m, `DedicatedServer.cs:89`) | small → keeps flowing |
| Resources (alive-bitmap, `WorldReplication.cs:443,494`), Crops, Deployables | hundreds of bytes today | fits alone → priority rotation recovers them |
| **Vehicles** | **~2.9 KB, unfiltered, ~2.5× the budget** | **never fits → frozen forever** |

So the *only* permanently-latched system at current world scale is vehicles — precisely matching a
world that looks completely normal until someone drives. (Deployables will join this failure class
the day a base outgrows ~1.1 KB of wire; nothing will warn about it.)

### 1.3 Why the driver and not the observer (the trigger)

The wedge needs one ≥1.28 s gap in the server *receiving* that client's acks. Nothing about driving
causes it — the ride loop keeps acking every tick (`NetWorldClient.Tick`, `NetWorldHost.cs:399-415`,
runs as `net.client.pump` before the shell step, `ClientWorldSession.cs:143-144`). The gap comes from
the client's environment:

- **Graphical client (VoX):** the Godot main loop is single-threaded — any long frame stalls the
  SimDriver, so no acks go out for the entire hitch while the server keeps ticking at 50 Hz wall
  time. This client has guaranteed multi-second hitches: the frame in which
  `VehicleReplicaView._Process` materializes puppets for **all 85** replicated vehicles at once
  (`VehicleReplicaView.cs:48-57` — no distance culling; each `Vehicle.BuildPuppetByName` parses
  meshes at runtime), first-render shader compiles, PEI world stream-in, plus WAN loss/jitter on top.
  Any single such hitch > 1.28 s after the join ack latches the wedge for the rest of the session.
  A hitch > 5 s instead times the session out (`NetProtocol.cs:76`) — and the rejoin can wedge again.
- **Headless LAN observer (netobs):** no rendering, no shader compiles, no world visuals
  (branch commit `6cebcfe`: "headless net-observer — no render world"), same-box RTT. It never gaps,
  stays in delta mode, and a moving car's ~35 B delta always fits — which is exactly what the probe
  saw (`epos` −108 → −147 tracked to centimetres).

The "frozen at the ENTRY position" detail confirms the wedge predates the drive: the replica froze at
wedge time, when every car was parked — a frozen parked car renders identically to a live one, so the
freeze is invisible right up until the drive starts.

## 2. The winning hypothesis vs ALL five required observations

1. **Server node moves** — untouched by the wedge; DriveInput is client→server unreliable and flows
   fine (`ClientWorldSession.cs:167` → `NetWorldHost.cs:545-552` → `VehicleNetSync.Tick` drive at
   `VehicleNetSync.cs:107-109`). ✓
2. **Server entity matches the node** — `ServerPublish` stamps it every tick
   (`VehicleReplication.cs:106-130`); the composer-side starvation is per-client, downstream of the
   entity. ✓
3. **Observer receives the motion** — unwedged client, delta mode, 35 B delta always included
   (`WriteDelta` includes anything with `LastChangedTick > baseline`, `VehicleReplication.cs:193-199`). ✓
4. **Driver's own client frozen** — wedged client: full-mode forever, vehicles block skipped every
   compose (mechanism §1.1). The puppet is driven solely by the replica
   (`VehicleReplicaView.cs:60-74`), the chase-cammed shell follows the puppet
   (`PlayerController.cs:1241`), so a frozen replica freezes car + seated view together. ✓
5. **Zombies chase the real position, visibly, on the driver's screen** — the server teleports the
   driver's player entity onto the real car every tick (`ServerVehicles.Step`,
   `VehicleReplication.cs:539-549`), that entity is the client's `viewPos`
   (`NetWorldHost.cs:244`), zombie relevancy rings around it (`Relevancy.cs:25-38`) — so the wedged
   driver keeps receiving the (small, always-fitting) zombie block for the zombies around the REAL
   car, and watches the horde converge on empty ground 40 m away: "chasing the ghost". ✓
6. **Exit drops him at the entry spot** — `OnVehicleExited` computes the exit spot from the frozen
   replica (`ClientWorldSession.cs:245-249`), i.e. the entry position. (Server-side he was correctly
   teleported beside the real car, `VehicleReplication.cs:513-518`; his player entity block still
   flows, so once his first post-exit MoveInputs are processed the reconciler should measure a ~40 m
   error and snap him to the true spot — `Prediction.cs:69-83`, threshold 2 m at `:23`. If that
   delayed 40 m yank was observed a moment after exit, it is *additional confirmation* of this
   hypothesis.) ✓
7. **L1 `net.vehicle_drive_sync` passes** — triply blind: (a) the only `VehicleReplicaView` is wired
   to the **observer** (`NetTests.cs:544-545`) and every tracking assert reads that view
   (`NetTests.cs:578-596`) — the driver's replica/puppet is never checked; (b) MemTransport is
   loss-free and hitch-free, so no ack gap can ever latch the wedge; (c) the world has **one**
   vehicle (`NetTests.cs:550-552`) — a full vehicles block trivially fits, so even a forced full-mode
   would not freeze anything. ✓
8. **Restart cleared it / freeze once reached a fresh observer pre-restart** — the wedge lives in
   server-side per-client `ClientState` (`SnapshotComposer.cs:67-89`); a server restart destroys all
   of it (a client reconnect clears its own too, `Composer.ForgetClient`, `NetWorldHost.cs:119`) —
   but re-latching only needs the next ≥1.28 s gap, so restarting "fixes" it only until then. The
   probability of the gap grows with uptime: accumulated world state makes the join snapshot transfer
   longer and join-time hitches bigger, so on the long-uptime pre-restart server even a fresh
   observer could gap during its own join (first ack lands > 64 ticks after the join snapshot's
   compose tick → condition 1 latches on the very first unreliable compose) and wedge immediately;
   on the freshly-restarted lean server the observer joined clean. Secondary clue, but consistent. ✓

## 3. Ranked suspects (as posed), proven or refuted

1. **Baseline/ack starvation specific to this client (suspect 4) — CONFIRMED, with the mechanism
   generalized.** Not "driving changes ack cadence" (it doesn't: acks are sent in
   `NetWorldClient.Tick` every tick regardless of ride state, `NetWorldHost.cs:409-411`), but the
   full-snapshot wedge of §1.1: one historical ≥64-tick ack gap + a full vehicles block ~2.5× the
   unreliable budget = that client's vehicle system frozen permanently. The starvation the suspect
   guessed at ("`LastChangedTick > baseline` never true") is real but inverted: the baseline pins
   *stale*, the block is always *eligible* — it just never physically fits the datagram again.
2. **The restart clue (suspect 5) — CONFIRMED as the same bug's fingerprint.** Accumulated
   server-side per-client composer state (`AckedTick`/`SystemBaseline`/`Priority`,
   `SnapshotComposer.cs:67-89`) is exactly the "state that degrades over uptime"; see §2.8.
3. **Owner/own-entity filtering in the composer (suspect 1) — REFUTED.** There is no per-entity or
   per-owner exclusion anywhere in the vehicle path: `WriteFull`/`WriteDelta` iterate all vehicles
   with no driver/owner test (`VehicleReplication.cs:182-216`); the composer treats blocks as opaque
   and has no entity-level policy (`SnapshotComposer.cs:244-274`); `ctx.ClientPlayerId` is consulted
   only by the owner-only skills/inventory blocks (`SkillsReplication.cs:123`,
   `InventoryReplication.cs:311`) and the relevancy systems — never by vehicles. The driver's player
   entity being teleported onto the car (`VehicleReplication.cs:546`) affects only `viewPos`
   (zombie/item relevancy), not any own-entity exclusion (none exists).
4. **Driver-side applier skips the own driven vehicle (suspect 2) — REFUTED.**
   `SnapshotApplier.Apply` routes purely by systemId (`SnapshotApplier.cs:98-112`);
   `VehicleReplication.ReadSnapshot` has no identity logic (`VehicleReplication.cs:218-236`);
   `NetEntityRegistry.Add` is replace-on-duplicate (`NetId.cs:58`); nothing in `ClientWorldSession`
   or `NetWorldClient` suppresses the vehicles registry for `_ridingNetId` (the ride branch only
   swaps MoveInput for DriveInput, `ClientWorldSession.cs:164-171`).
5. **`VehicleReplicaView` / ride-mode node writes (suspect 3) — REFUTED as cause.** `_Process`
   dead-reckons every replicated vehicle with no ride special-case (`VehicleReplicaView.cs:44-75`);
   `RidePuppet` copies puppet→shell, never shell→puppet (`PlayerController.cs:1241`); `EnterPuppet`
   never touches the puppet transform (`PlayerController.cs:1195-1207`). (The one real bug that ever
   lived here — the puppet inheriting Godot physics interpolation — was already fixed in `4bcb297`,
   which is why the observer now tracks perfectly; the driver's freeze survived that fix because it
   was never a node problem.)

## 4. How to confirm (cheap, in order of strength)

1. **Server journald during a frozen repro** (`UG_NETLOG=1` is enough — the counters already exist):
   the 1 Hz `[NET] 1s:` rollup (`NetWorldHost.cs:196-207`) will show `snaps full ~25/s, delta 0` and
   `skips` climbing steadily once the driver's client wedges — full-mode + per-compose skip is the
   wedge's exact signature. A healthy epoch shows delta-dominant and `skips 0`.
2. **Driver-client counters:** `Client.Applier.Diag.FullSnapshotsApplied` climbing continuously
   (should be ~1 after join) and `Diag.SyncChecksPassed+Failed` frozen at their wedge-time values
   (no sync-check blocks arrive at a wedged client, §1.1.5).
3. **Deterministic L0 repro (engine-free):** a `SnapshotComposer` + `VehicleReplication` with 85
   entities publishing every tick; ack normally for a while, withhold acks for 65+ ticks, resume
   acking every composed tick. Assert: every subsequent `Compose` is full,
   `OversizedBlocksSkipped` increments each time, and a client-side applier's vehicle replica never
   advances again — while an identical never-gapped client tracks fine.
4. **Live A/B:** run the same headless netobs but pause its process (SIGSTOP) for 2 s mid-session,
   resume — its vehicle replica freezes exactly like the driver's while a second unpaused observer
   keeps tracking. Proves the seat is irrelevant.

## 5. Minimal proposed fix (not applied — diagnosis only)

**Primary (correctness): starvation-recovery fulls must ride the reliable channel.** The join path
already does exactly this (`NetWorldHost.cs:226-235`: compose with `MaxReliableMessageBytes / 2`,
send via `peer.SendReliable`, hold unreliable snapshots until acked, `NetWorldHost.cs:243`). In
`TickReplication`, when `Composer.WillSendFull(peer)` is true, reuse that path for the resync full
(send once, keep holding the unreliable stream until the client acks past it) instead of composing a
1187-byte full that cannot carry the world. This removes the latch for every system, present and
future — a full snapshot is the recovery mechanism and must never be smaller than the world it
recovers.

Cheap hardening alongside (each optional):
- `NetLog.Warn` (once per system) when a system's lone block exceeds `maxBytes` in a full compose —
  the "this block can never be delivered" condition is currently silent (`SnapshotComposer.cs:255-263`).
- Emit the sync-check block even when a checked system was skipped, with a "stale" marker (or simply
  exclude only the skipped system) — today the desync detector goes blind exactly when it is needed
  (`SnapshotComposer.cs:146-147`).
- Relevancy-filter vehicles like world items (ring on `ctx.ViewPos`) to shrink blocks generally —
  useful, but NOT a substitute: any unfiltered system (deployables) re-creates the wedge as it grows.

**Test coverage (the regression-rule shipment for this bug):**
- The L0 wedge repro of §4.3 (fails today, passes with the reliable-resync fix).
- Extend L1 `net.vehicle_drive_sync`: attach a second `VehicleReplicaView { Client = driver }` and
  assert the DRIVER's puppet tracks the car too (closes the exact coverage hole at
  `NetTests.cs:544-596`), plus a variant that withholds the driver's acks for 65+ ticks mid-drive
  and asserts recovery.
