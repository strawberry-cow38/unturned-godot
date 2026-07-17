Repo surveyed against `main` @ b1fe907 — I read the client entry (`Main.cs` `BuildClient`), the one-world assembly (`WorldBuilder.cs`), every replica view (`RemotePlayers`/`ZombiePuppets`/`VehicleReplicaView`/`DeployableReplicaView`), the net stack (`core/UnturnedNet/`), the loopback listen-server (`MpLoopback.cs`), the dedicated host (`DedicatedServer.cs`), and the launcher (`launcher/MainWindow.cs`). Every claim below carries its file:line.

---

# PLAN — A Playable PEI Client for unturned-godot

*Make `--connect=<host>` load the REAL PEI world and put a walking, driving, predicted local player in it — reusing WorldBuilder and the shipped Phase 1–8 net stack, not forking a parallel renderer. Test-server scope: simple > complete, cheats stay on, SP stays byte-identical.*

## 0. TL;DR

| Piece | Decision | Where it lives |
|---|---|---|
| Client world | New `WorldMode.Client` in the ONE assembly path: terrain + objects + colliders + roads/foliage/trees + day-night, **no** local-authority spawns (no ZombieField/LootField/vehicle spawns/jeep). | `game/WorldBuilder.cs` |
| Client composition | New `ClientWorldSession` node (the `MpLoopback.cs` pattern for the remote case): owns `NetWorldClient`, the local shell, and every replica view. `BuildClient` builds world + adds it. | `game/` (new file) + `game/Main.cs:1945` |
| Local player | Real `PlayerController` shell at the server-adopted spawn; input over the wire (`SendMoveInput`), position reconciled via the existing `PredictionReconciler` — corrections applied **to the node** (unlike loopback). | `game/ClientWorldSession.cs` + `core/UnturnedNet/Prediction.cs:20` |
| Server players | Replace the flat-ground demo integration (`PlayerReplication.cs:230` `IntegrateFlat`) with per-peer **server avatar bodies** on the real world, written back through the existing `ServerDrive` seam (`PlayerReplication.cs:261`). Real spawn points from `Spawns/Players.dat`. | `game/PlayerNetSync.cs` (new) + `core/UnturnedNet/NetWorldHost.cs:87` |
| Server content | Dedicated world gains vehicles (move the `Spawns/Vehicles.dat` block out of the Playable-only branch) and zombies (generalize `ZombieField` streaming from `Player` to `PlayerRegistry`, the `LootField` precedent). | `game/WorldBuilder.cs:316,447` + `game/ZombieField.cs:16` |
| Missing views | `ZombiePuppets` attached (exists, unused by the client); NEW: world-item view, clock→`DayNightCycle`, resource alive-bitmap→`ResourceField.SetAlive`. | `game/` |
| Map delivery | Client reads PEI from a **local retail Unturned install** via `UG_UNTURNED_DIR` — the launcher already resolves + passes it (`launcher/MainWindow.cs:234-240`). No map download, no redistribution. Fail-fast error screen if missing. | `launcher/` (no change) + `game/Main.cs` |
| Testing | L1 loopback/MemTransport tests on the fallback world (no retail data, CI-safe) per phase + a two-process scripted headless connect-and-render check with a golden PNG on the claw box. | `game/testing/tests/NetTests.cs`, `tools/` |
| SP guard | Every phase: `./test.sh` (L0+L1) green, `./test.sh --visual` green with **zero re-baselined goldens**; all SP-path edits are extract-and-call refactors. | `test.sh` |

Cross-cutting rules (inherited from `docs/MP_PLAN.md`):

- **One world assembly.** The client reuses `WorldBuilder.BuildFullWorld` with a mode flag (MP_PLAN §5 item 8). No second terrain loader, no client-only object placer.
- **All state mutation goes through commands.** The client shell sends `MoveInput`/`EnterVehicle`/`DriveInput`/…; it never writes authoritative state. DevConsole is already server-gated (`game/DevConsole.cs:85`).
- **No calendar estimates.** Numbers are technical (Hz, bytes, metres).

**The single highest-leverage fact:** everything hard is already built. The wire (sessions, snapshots, commands, events, prediction, interest policy) is Phases 1–8 and live; the real world assembly is `WorldBuilder`; the replica views exist for players/zombies/vehicles/deployables. What's missing is *composition*: `BuildClient` (`game/Main.cs:1945-1972`) still assembles the Phase-3 demo arena around the finished stack, and the server still integrates remote players on a flat demo plane (`core/UnturnedNet/PlayerReplication.cs:222-238`). This plan is mostly wiring, with exactly two real new mechanisms (server avatar bodies; corrections applied to a real client shell).

## 1. Current state — what BuildClient renders vs. what a joined player must see

**What `--connect=<host>` does today** (`game/Main.cs:110` sets `client=true`; dispatch at `Main.cs:346` → `BuildClient()` at `Main.cs:1945`):

1. **Flat demo ground, not PEI**: an 80×80 `PlaneMesh` + `WorldBoundaryShape3D` collider (`Main.cs:1957-1962`) and a hardcoded sky/ambient env (`Main.cs:1947-1956`).
2. **Fixed overhead camera, no player**: a static `Camera3D` at (0, 9, 14) looking at origin (`Main.cs:1963-1966`). There is no local `PlayerController`, no HUD, no first-person anything.
3. **Dead scenery path**: `ScatterScenery()` (`Main.cs:1975-2007`) reads `C:\claude-workspace\ripped-mb\converted\manifest.json` (`Main.cs:1977`) — nonexistent on Linux and on every other dev box, so it returns at `Main.cs:1978` and even the demo props don't load. It is also the only place the client calls `CharacterModel.LoadBundled()` (`Main.cs:1983`), so remote players degrade to `Humanoid` capsules.
4. **Scripted bot input, not real input**: `ClientNode` (`game/ClientNode.cs:45-51`) sends a hardcoded circle-walk `MoveInput` every tick and predicts through `ClientPrediction` (flat-ground `IntegrateFlat`). The user's keyboard/mouse never touch the wire.
5. **Partial replica rendering**: `ClientNode` renders players itself as tinted capsules (`ClientNode.cs:57-74`) and attaches only `DeployableReplicaView` + `VehicleReplicaView` (`ClientNode.cs:33-34`). **Not attached**: `ZombiePuppets` (exists at `game/ZombiePuppets.cs`, used only by L1 tests), `RemotePlayers` (exists at `game/RemotePlayers.cs`, used only by loopback + tests). **Doesn't exist at all**: a world-item view (the `Client.WorldItems` replica has no renderer — grep confirms no `WorldItemReplicaView`), a clock view (nothing feeds `Client.Clock.TimeOfDayAt` into a `DayNightCycle`), a resource view (nothing calls `ResourceField.SetAlive` from `Client.Resources`), a crop view.
6. **Server-side players are demo-grade**: `PlayerReplication.ServerStep` integrates `PlayerMovementSim` **on flat ground at the spawn Y with no collision** — the class says so itself: *"Deliberately demo-grade movement: flat ground, stand stance, no jump/collision"* (`PlayerReplication.cs:129-131`, integration at `PlayerReplication.cs:245-255`). Spawns are a hardcoded line at the origin: `((playerId-1)%8)*2, 0, 0` (`NetWorldHost.cs:125`) — underwater/underground on PEI. `MoveInput` carries no jump/buttons (`PlayerReplication.cs:93-99`).
7. **The dedicated world is underpopulated**: `WorldBuilder` Dedicated mode builds terrain/objects/roads/trees/loot but explicitly **no ZombieField** (*"ZombieField/AnimalField still key spawning on the LOCAL PlayerController and stay out until their streamers generalize"*, `WorldBuilder.cs:449-451`) and **no vehicles** (the `Spawns/Vehicles.dat` block lives only in the Playable branch, `WorldBuilder.cs:316-386`). `ZombieNetSync`/`VehicleNetSync` publish whatever is in the `"zombies"`/`"vehicles"` groups (`ZombieNetSync.cs:49`, `VehicleNetSync.cs:61`) — which on the dedicated server is nothing. A joined client can currently never see a zombie or a vehicle.
8. **Map path resolution is already Linux-safe** — `MapDir` uses forward slashes + the `UG_UNTURNED_DIR` env root (`Main.cs:22-24`); the remaining Windows-hardcoded path in the client path is `ScatterScenery` (item 3), which dies with the demo arena.

**What a joined player must see** (owner's bar): real PEI terrain + objects + colliders, a locally-controlled first-person player who can walk and drive, remote players / zombies / vehicles / deployables rendered *in* that world, day-night matching the server. Everything in the gap list above, closed.

**What already exists to build on** (verified):

- `WorldBuilder.BuildFullWorld(root, mode, mapRoot, mapPlace, …)` assembles the whole real world for Aerial/Playable/Dedicated (`WorldBuilder.cs:34-499`); `--peidrive` and the dedicated server both ride it (`Main.cs:1481`, `Main.cs:1921`). Colliders come for free for any non-Aerial mode (`WorldBuilder.cs:107`).
- The full command surface a playable client needs is shipped and server-validated: `SendMoveInput` (`NetWorldHost.cs:339`), `SendEnterVehicle`/`SendExitVehicle`/`SendDriveInput` (`NetWorldHost.cs:454-469`), combat + inventory + console commands (`NetWorldHost.cs:351-442`).
- Prediction v1 machinery: `PredictionReconciler` (record-per-seq, smooth-correct, snap threshold — `core/UnturnedNet/Prediction.cs:20-107`) and the `ServerDrive`/`ExternallyDriven` seam for a sim that runs outside core (`PlayerReplication.cs:257-272`). The loopback listen-server already exercises the whole loop end-to-end (`MpLoopback.cs:94-117`).
- `PlayerRegistry` replaced the `Local` static (`game/PlayerRegistry.cs`); `LootField` already streams on ANY registered player (`game/inventory/LootField.cs:110-119`) and zombie targeting falls back to `PlayerRegistry.Nearest` (`game/ZombieController.cs:243`).
- Interest policy v1 is live on the dedicated server (distance rings + the 19 nav pockets as cells, `DedicatedServer.cs:52-63`), so a populated PEI won't firehose joiners.
- The launcher already ships the client (git clone → `dotnet build` → godot `--import` → run, `MainWindow.cs:119-226`), resolves the retail Unturned dir, passes it as `UG_UNTURNED_DIR` (`MainWindow.cs:234-240`), and has the "Multiplayer test" checkbox appending `--connect=claw.bitvox.me` (`MainWindow.cs:45,252-259`).

## 2. Target architecture

### 2.1 One world, four modes

`WorldMode` (`WorldBuilder.cs:11`) grows a fourth value:

```
Aerial     = survey cam, no colliders                      (unchanged)
Playable   = full world + local-authority spawns + player  (unchanged)
Dedicated  = headless server world                         (gains vehicles + zombies, Phase C4)
Client     = NEW: terrain + objects + COLLIDERS + roads + foliage + trees + day-night,
             NO ZombieField / LootField / AnimalField / vehicle spawns / jeep / CropManager,
             NO camera and NO player (the session owns those)
```

Client mode is "the world as scenery + physics": everything that is deterministic-from-files loads locally (MP_PLAN §3.7: *"Map/static world: never networked"*); everything that is authoritative state arrives as replicas and is rendered by views. The nav-pocket bake/load at `WorldBuilder.cs:493-496` is skipped in Client mode (puppets don't path; pure load-time savings).

### 2.2 Client boot sequence

```
--connect=<host>  (Main.cs:110 → Main.cs:346 → BuildClient, Main.cs:1945)
  │
  ├─ 1. WorldBuilder.BuildFullWorld(WorldMode.Client, MapDir("PEI"), …)   async, LoadingScreen
  │       terrain+objects+colliders / roads / foliage / trees / DayNightCycle
  │       ── Terr == null? → fail-fast error screen ("map not found — set UG_UNTURNED_DIR"), NOT the demo arena
  │
  ├─ 2. AddChild(new ClientWorldSession { Host, Port, World = res })      the MpLoopback of the remote case
  │       ├─ NetWorldClient(UdpClientTransport(Host,Port), name, NetContent.Hash) + Connect()
  │       ├─ schemas: DeployableNetSchema / CropNetSchema  (as ClientNode.cs:31-32 does today)
  │       ├─ views:  RemotePlayers, ZombiePuppets, VehicleReplicaView, DeployableReplicaView,
  │       │          WorldItemReplicaView (new), WorldClockView (new), ResourceAliveView (new)
  │       ├─ DevConsole.RemoteClient = client                             (server-gated cheats, DevConsole.cs:85)
  │       └─ SimDriver steps (50 Hz, §2.5 order): net pump → shell input/predict → client session tick
  │
  ├─ 3. join: Connect → Accept → reliable FULL join snapshot (NetWorldHost.cs:94-108) → deltas
  │
  └─ 4. first authoritative own-entity sample → spawn the LOCAL SHELL at that position:
          PlayerController + HUD + PauseMenu + AttachmentMenu + MapUI + DevConsole + FpsCounter
          (extracted WorldBuilder.AttachPlayerShell, from the Playable branch WorldBuilder.cs:281-299)
          per tick: SendMoveInput(shell.LastMoveInput, yaw, buttons) → Reconciler.Record(seq, node pos)
                    apply Reconciler.Step(dt) correction delta TO THE NODE (snap via TakeAll past 2 m)
```

### 2.3 Player authority: server avatar bodies + a reconciled real shell

The loopback already proved the shape on the listen-server side: a real `PlayerController` steps sim-core + real collision, `ServerDrive` writes the result into the replication entity, and `ServerStep` skips externally-driven entities (`MpLoopback.cs:100-116`, `PlayerReplication.cs:261-272`). The dedicated server gets the same construction, one avatar per remote peer:

- **`PlayerNetSync`** (new, `game/`, the `VehicleNetSync` pattern): on `PeerConnected`, spawn an avatar body on the server world at the entity's spawn; every tick read that peer's held `MoveInput` (new core accessor `PlayerReplication.TryGetHeldInput` — the field exists as `PlayerEntity.CurrentInput`, `PlayerReplication.cs:146-147`), inject it (yaw + axes + jump), let the body `MoveAndSlide` on the real terrain/objects, then `Players.ServerDrive(peer, bodyPos, yaw, seq, tick)`. On `PeerDisconnected`, free the body. While the peer drives a vehicle the avatar parks under the seat (`ServerClearInput` already fires on enter, `VehicleReplication.cs:490`; the seat teleport owns the entity, `NetWorldHost.cs:73-75`).
- **The avatar is a `PlayerController`** with a new `NetAvatar` construction flag that skips the client-only subtree in `_Ready` (viewmodel, UIs, OutlineOverlay, BuildTool, demo inventory — `PlayerController.cs:1471-1490`) but keeps the capsule/`MoveAndSlide`/`PlayerRegistry` registration (`PlayerController.cs:1436`) and the `ScriptedInput` seam (`PlayerController.cs:1083,2407`). Why not a minimal `CharacterBody3D`: `PlayerRegistry` is typed `List<PlayerController>` (`PlayerRegistry.cs:13`) and zombie targeting / loot streaming / vehicle hitch queries all cast to `PlayerController` (`ZombieController.cs:243,568`, `LootField.cs:119`, `Vehicle.cs:1556`) — reusing the controller makes zombies chase remote players and loot stream around them **for free**.
- **Real spawns**: extract the `Spawns/Players.dat` parse from the Playable branch (`WorldBuilder.cs:235-255`) into a shared helper; `NetWorldServer` gains a `SpawnProvider` func used in `PeerConnected` instead of the static origin line (`NetWorldHost.cs:87,125`); `DedicatedServer` wires it with `Terr.SampleHeight` for the Y.
- **`MoveInput` v2**: add a buttons byte (bit 0 = jump; headroom for sprint/crouch) to `MoveInput.Write/TryRead` (`PlayerReplication.cs:100-118`), bump `NetProtocol.Version` (`core/UnturnedNet/NetProtocol.cs:54` — already at v2, goes to v3) and re-golden the wire tests in the same commit (the MP_PLAN §6 discipline).
- **Client side**: the shell is a normal `PlayerController` (real input capture, camera, viewmodel — all local). Its `LastMoveInput` (`PlayerController.cs:1086`, written at `PlayerController.cs:2416`) + yaw + jump go over the wire; its post-physics `GlobalPosition` is recorded under the sent seq; `PredictionReconciler` corrections are applied back to the node each tick. `ClientPrediction`'s headless `IntegrateFlat` path (`Prediction.cs:118-177`) stays for the demo/tests; the shell uses the `Reconciler` directly — the same objects `MpLoopback` already records into (`MpLoopback.cs:115-116`), except corrections are consumed instead of discarded.

Why this converges: both sides step the **same** `PlayerMovementSim` constants on the **same** world geometry (client world and server world are the same files — §2.1). Residuals are quantization- and timing-sized; the 2 m snap threshold (`Prediction.cs:23`) catches divergence (e.g. a physics-object shove that only one side saw).

### 2.4 Replica views in a real world

All four existing views already parent correctly for this design: `RemotePlayers`/`ZombiePuppets` add puppets as their own children (`RemotePlayers.cs:39`, `ZombiePuppets.cs:34`), `VehicleReplicaView`/`DeployableReplicaView` spawn into `GetParent()` (`VehicleReplicaView.cs:52`, `DeployableReplicaView.cs:43`) — so hanging them under the client session node inside the real world puts every puppet/replica **in** the PEI scene, colliding-free (puppets have no physics bodies by design, `VehicleReplicaView.cs:13,579`). The new views follow the same diff-driven idempotent pattern (`DeployableReplicaView.cs:12-13`):

- **WorldItemReplicaView** (new): mirrors `Client.WorldItems` → frozen `WorldItem` visuals (`game/inventory/WorldItem.cs:15`) at the replicated transform; `WorldItemSettled`/`Removed` events (`NetWorldHost.cs:289-292`) move/retire them. Pickup stays deferred (§6) — items are *visible* in v1.
- **WorldClockView** (new, ~15 lines): each applied snapshot, `dayNight.Time = Client.Clock.TimeOfDayAt(Applier.LastAppliedServerTick)` — the client-side mirror of the server publish at `game/WorldNetSync.cs:61`. The client's `DayNightCycle` keeps rendering visuals exactly as SP does; only its clock is driven.
- **ResourceAliveView** (new, ~20 lines): applies the `Client.Resources` alive-bitmap + `ResourceHarvested`/`Respawned` events onto the client world's `ResourceField.SetAlive(index, alive)` (`game/ResourceField.cs:44`) — index space is deterministic load order on both sides (MP_PLAN §3.7).
- Crops: visual view deferred (§6) — the schema registration ships (`ClientNode.cs:32`) but rendering planted crops is post-milestone.

### 2.5 Camera, HUD, input routing

The shell IS the camera/HUD owner, exactly as in SP Playable (`WorldBuilder.cs:282-299`): first-person `Camera3D` inside `PlayerController`, HUD bound to the shell, ESC pause, F1 console (already remote-gated for `give`/`xp`/`skill`, `DevConsole.cs:85-92`), M map. Driving swaps to the §3.6 v1 puppet-ride: on `VehicleEntered(self)` (`NetWorldHost.cs:297`) the shell hides + freezes, the camera chases the `VehiclePuppet`, WASD becomes `SendDriveInput` @50 Hz; `VehicleExited` restores the shell at the server's exit teleport (`VehicleReplication.cs:505-510`). Vitals shown on the HUD are the shell's local SP vitals in v1 (server-authoritative vitals are deferred, §6).

### 2.6 Content + map resolution

Two data roots, both already Linux-clean:

- **Repo content** (`game/content/` — meshes/palettes/placements/blueprints): ships with the build; the launcher's clone+build delivers it (`MainWindow.cs:119-126,205-226`). Loaded via `res://content/` (`WorldBuilder.cs:108`).
- **Retail map** (`Maps/PEI/` — heightmaps, spawns, paths, foliage): resolved by `MapDir` = `UG_UNTURNED_DIR` env or the Windows Steam default (`Main.cs:22-24`). The dedicated server on claw already runs with `UG_UNTURNED_DIR=/home/ec2-user/unturned` (CLAUDE.md §dedicated). The client uses the identical mechanism — see §4.

## 3. Phased breakdown

Ordering: world first (C1), server correctness second (C2), client feel third (C3), server population fourth (C4), full visibility fifth (C5), driving last (C6). Each phase is independently mergeable, leaves `./test.sh` and `./test.sh --visual` green, and never touches SP behavior (guards listed per phase). C2 and C1 are independent and could land in either order; C3 needs both; C5 needs C4 for anything to look at; C6 needs C3.

### Phase C1 — the client loads real PEI (under the existing overhead cam)

*The smallest real step: a joined client renders the actual island instead of the demo arena.*

- **`game/WorldBuilder.cs:11`** — add `Client` to `WorldMode`. In `BuildFullWorld`: Client takes the same terrain/objects path as Playable/Dedicated (colliders on, `WorldBuilder.cs:107`), plus the Playable-branch roads/foliage/trees blocks (`WorldBuilder.cs:401-422` — extract those three blocks into a small shared local func so Playable and Client call the same code), and **skips** player/zombies/vehicles/loot/animals/jeep/CropManager (`WorldBuilder.cs:230-400`) and the nav bake/load (`WorldBuilder.cs:493-496`, gate `mode != WorldMode.Client`). No camera added for Client (the Aerial else-branch at `WorldBuilder.cs:480-489` must not fire).
- **`game/Main.cs:1945-1972` `BuildClient`** — replace the plane/env/scenery (`Main.cs:1947-1968`) with `await WorldBuilder.BuildFullWorld(this, WorldMode.Client, _mapRoot, _mapPlace, noZombies: true, syncLoad: false, bakeNav: false, activeHoliday: ActiveHoliday())` (async — the LoadingScreen paints, `WorldBuilder.cs:50`). Keep the overhead `Camera3D` for now, hovered over the PEI player-spawn region instead of the origin. **Fail-fast**: `res.Terr == null` → a fullscreen error label ("PEI map not found — set UG_UNTURNED_DIR / install Unturned") + `GD.PrintErr`, never the silent demo arena. Call `CharacterModel.LoadBundled()` here (it lived only inside dead `ScatterScenery`, `Main.cs:1983`).
- **Delete `ScatterScenery`** (`Main.cs:1975-2007`) — hardcoded `C:\claude-workspace\…`, dead on every current box.
- **Keep `ClientNode` attached** as-is (`Main.cs:1970`): its capsule players + vehicle/deployable views now render into the real world. (Replaced in C3.)
- **Verify**: NEW L1 `net.client_world_mode` — `BuildFullWorld(WorldMode.Client, mapRoot: "res://__no_such_map__", …)` on the fallback path returns cleanly with `Terr == null` (the fail-fast contract), and a Client-mode build never creates a `PlayerController`/ZombieField (assert `world.Player == null`, `PlayerRegistry.Count == 0`) — the `NetDedicatedBoot` pattern (`NetTests.cs:15-24`). Scripted check on claw (§5): boot `--dedicated` + `--connect=127.0.0.1` under xvfb, capture PNG → real terrain, roads, trees visible; a second client's capsule visible on it. **SP guard**: `WorldMode.Client` is a new enum arm; the roads/foliage/trees extraction is call-site-identical for Playable (same order, same params); L0+L1+L2 all green, no goldens re-baselined.

### Phase C2 — server players live on the real map (spawns + collision + jump)

*Server correctness: remote avatars stand on PEI, not on an invisible plane at y=0.*

- **Real spawn points**: extract the `Spawns/Players.dat` parse (`WorldBuilder.cs:235-255`) into `LevelSpawns.PlayerSpawns(mapRoot)` (static, `game/`); Playable calls it (behavior-identical, same RNG seed 7 pick at `WorldBuilder.cs:253`). `NetWorldServer` gains `public System.Func<ushort, UnityEngine.Vector3> SpawnProvider` consumed in `PeerConnected` (`NetWorldHost.cs:87`), defaulting to the existing demo line (`NetWorldHost.cs:125`) so every L0/L1 test is untouched. `DedicatedServer._Ready` (`DedicatedServer.cs:35`) wires it: random real spawn, `Terr.SampleHeight(x,z)+1.5f` for Y (`Terr` is already a field, `DedicatedServer.cs:20`).
- **`MoveInput` v2**: buttons byte (bit0 jump) in `Write`/`TryRead` (`PlayerReplication.cs:100-118`); `NetProtocol.Version` 2→3 (`NetProtocol.cs:54`); re-golden wire tests in the same commit.
- **Server avatar bodies**: new `game/PlayerNetSync.cs` (the `VehicleNetSync` shape, `VehicleNetSync.cs:22-52`), registered on the SimRoot by `DedicatedServer` between `net.server.sim` and the syncs: per peer, spawn a `PlayerController { NetAvatar = true, CaptureMouse = false }` at the entity spawn; per tick, read the held input via a new `PlayerReplication.TryGetHeldInput(owner, out MoveInput)` accessor (field exists, `PlayerReplication.cs:146-147`), set `ScriptedInput` + `RotationDegrees.Y` + scripted-jump, and after the body's physics step write back `Players.ServerDrive(...)` (`PlayerReplication.cs:261`). Skip write-back while the peer is seated (`VehicleHost.IsDriver`, `NetWorldHost.cs:75` — the seat teleport owns the entity). Free the body on disconnect.
- **`PlayerController.NetAvatar` flag** (`PlayerController.cs:~1440`): early-outs the client-only subtree — viewmodel, inventory/craft/skills UIs, OutlineOverlay, BuildTool, demo-inventory population (`PlayerController.cs:1471-1490`) — keeps capsule/floor tuning/`PlayerRegistry` registration (`PlayerController.cs:1444-1451,1436`), keeps the (non-Current) camera node since look math reads it. Also: NetAvatar bodies take **no local vitals damage** in v1 (guard in `TakeDamage`) — zombies chase and swing but can't desync an unreplicated death (real damage sync is deferred, §6).
- **Verify**: L0 — MoveInput v2 golden bytes + version-reject test (existing harness). NEW L1 `net.server_avatar_terrain`: fallback world + a `StaticBody3D` ramp in the walker's path; a remote client walks forward; assert its replicated `Pos.y` rises above the ramp base (impossible under `IntegrateFlat`, which never changes Y — `PlayerReplication.cs:250-253`) and that `LastProcessedInputSeq` still acks (the `ServerDrive` path carries it, `PlayerReplication.cs:270`). Existing `net.loopback_join_move`/`net.dedicated_boot` stay green unmodified (SpawnProvider defaults; loopback's `ServerDrive` skips the new sync via `ExternallyDriven`… verify the sync also skips already-driven entities). **SP guard**: `NetAvatar` defaults false; `LevelSpawns` extraction byte-identical for Playable; L2 untouched.

### Phase C3 — the local predicted player walks PEI

*Client feel: WASD + mouse in first person on the real island, reconciled against the server.*

- **`WorldBuilder.AttachPlayerShell(root, player, …)`** — extract the Playable player-shell block (HUD, DevConsole, CropManager-, MapUI, hitmarkers, PauseMenu, Profiler, AttachmentMenu — `WorldBuilder.cs:281-299`) into a helper Playable calls verbatim; the client session reuses it (minus CropManager — server owns growth).
- **New `game/ClientWorldSession.cs`** (the composition node, §2.2): owns `NetWorldClient` + `SimDriver` steps (net pump first, session tick after — the `ClientNode.cs:43-52` ordering), the schema registrations, `DevConsole.RemoteClient`, and the views: `RemotePlayers` (`RemotePlayers.cs:13` — replaces `ClientNode`'s capsules), `VehicleReplicaView`, `DeployableReplicaView`. On the first authoritative own-entity sample (the `ClientPrediction.Reconcile` seed pattern, `Prediction.cs:158-167`): spawn the shell `PlayerController` at that position + `AttachPlayerShell`. Per tick thereafter: `seq = SendMoveInput(shell.LastMoveInput.x, .y, yaw, buttons)`; `Reconciler.Record(seq, shellPos)` (the `MpLoopback.cs:100-116` loop); consume `Reconciler.Step(dt)` / `TakeAll()` by moving the node (+`NoteCorrectionApplied`, `Prediction.cs:104`).
- **`game/Main.cs` `BuildClient`**: swap `ClientNode` for `ClientWorldSession`. `ClientNode` stays in-tree for the bare `--client` demo/tests (no world) — `--connect` implies the world path.
- **Verify**: NEW L1 `net.shell_walk_reconcile` — fallback world; a `ClientWorldSession`-driven shell (ScriptedInput forward) over MemTransport against a `DedicatedServer` + C2 avatar sync; assert (a) the shell moves, (b) the replicated own-entity converges to the node within the wire grid (the loopback parity style, `NetTests.cs:93-116`), (c) an injected 5 m artificial displacement of the *server* avatar snaps the shell (`Reconciler.Snaps` increments — `Prediction.cs:45`). Scripted check on claw: connect, hold forward 100 ticks (a `UG_MPWALK=1` test hook on the session, same spirit as `UG_AUTOFIRE`), two captures → the player moved along real terrain, HUD visible. **SP guard**: `AttachPlayerShell` is an extract-and-call refactor of the Playable branch — same nodes, same order; L2 `--visual` green with zero re-baselines is the proof.

### Phase C4 — the dedicated world is populated (vehicles + zombies)

*Server content: there is something to see.*

- **Vehicles**: extract the `Spawns/Vehicles.dat` block (`WorldBuilder.cs:316-386`) into a shared method; Playable calls it verbatim; Dedicated calls it too (after `ItemCatalog.RegisterAll`, `WorldBuilder.cs:474`). `VehicleNetSync` already mints + publishes every `"vehicles"`-group node (`VehicleNetSync.cs:61-73`) and `VehicleReplicaView` renders them (`VehicleReplicaView.cs:45-73`) — zero net-layer work.
- **Zombies**: generalize `ZombieField` streaming from the single `Player` field (`game/ZombieField.cs:16`, gate at `ZombieField.cs:92-94`) to nearest-any-player via `PlayerRegistry` (the `LootField` precedent, `LootField.cs:110-119`; C2's server avatars register, so streaming keys on real remote players). Enable `ZombieField` in the Dedicated branch of `WorldBuilder` (`WorldBuilder.cs:447-478`) behind the existing `noZombies` param — and flip `BuildDedicated`'s hardcoded `noZombies: true` (`Main.cs:1922`) to respect `--nozombies`/an env toggle (default: zombies ON for the test server). The nav pockets the brains path on are already built in Dedicated mode (`WorldBuilder.cs:493-496`); `ZombieNetSync` publishes the brains (`ZombieNetSync.cs:49-89`); pocket-cell relevancy already bounds the wire cost (`DedicatedServer.cs:55-62`).
- **Verify**: NEW L1 `net.zombiefield_anyplayer` — no map needed: register two `NetAvatar` controllers at different positions, assert the field's streaming picks the nearest per pocket (needs a small test seam on the streaming query — same style as `DebugPlanSpawns`, `Main.cs:2074`). Existing `net.zombie_chase_sync` (`NetTests.cs:284`) guards the publish path. Scripted check on claw: `--dedicated` journal shows `[vehicles] spawned N` (`WorldBuilder.cs:385`) + zombie publishes; the client capture shows parked vehicles + zombie puppets in town. **SP guard**: vehicle-block extraction call-site-identical for Playable; `ZombieField` keeps honoring an explicitly-set `Player` (the SP path) exactly — registry fallback fires only when `Player == null`, mirroring `ZombieController.cs:238-243`.

### Phase C5 — full world-state visibility (items, clock, resources, zombie puppets)

*Everything the server replicates is now rendered.*

- **`ZombiePuppets` attached** in `ClientWorldSession` (exists, `ZombiePuppets.cs:14` — one-line composition).
- **New `game/WorldItemReplicaView.cs`**: diff-driven mirror of `Client.WorldItems` → static `WorldItem` visuals (mesh/texture from the item id — reuse the `WorldItem` build path, `game/inventory/WorldItem.cs:15`; requires `ItemCatalog.RegisterAll` on the client, which the shell's `_Ready` already does, `PlayerController.cs:1479`), driven by the spawn/settle/remove events (`NetWorldHost.cs:287-292`). Visual-only: no `RigidBody3D` physics on replicas, no pickup (deferred §6).
- **New `WorldClockView` + `ResourceAliveView`** (§2.4): `DayNightCycle.Time` from `Clock.TimeOfDayAt(LastAppliedServerTick)` (mirror of `WorldNetSync.cs:61`); alive-bitmap + events → `ResourceField.SetAlive` (`ResourceField.cs:44`).
- **Verify**: NEW L1 `net.client_world_views` — fallback world, MemTransport: server spawns a world item (`Server.Transactions` drop path), fells a resource index, configures the clock; assert the client views materialize the item node, call `SetAlive(i,false)`, and set the `DayNightCycle` time to the tick-derived value (tolerance one snapshot interval). Scripted check on claw: set server time to night via the console (`ServerTransactions.RunConsole`, cheats on) → the client capture is dark with the day-night sky (compare against SP `--night` look). **SP guard**: all three views are new client-session-only nodes; no SP file touched beyond none.

### Phase C6 — drive on PEI (the finish line)

*A joined player walks to a replicated vehicle, gets in, and drives the island; everyone else sees it.*

- **Client-side interaction with puppets**: the shell's interact raycast recognizes `VehiclePuppet` nodes (add them to a `"vehicle_puppets"` group + carry their `NetId` — `VehicleReplicaView` sets it at spawn, `VehicleReplicaView.cs:50-55`) → `SendEnterVehicle(netId)` (`NetWorldHost.cs:454`). Note the server currently seats without a reach check (`VehicleReplication.cs:485-493`) — with C2's real positions, add the range validation there (one `validate:` lambda, ~64 m² like SP's prompt range) since cheap and the choke point exists.
- **Ride mode** in `ClientWorldSession`: on `VehicleEntered(self)` (`NetWorldHost.cs:297`) — hide + freeze the shell (stop sending `MoveInput`; the server drops driver walk-input anyway, `NetWorldHost.cs:73-75`), chase-cam the puppet (reuse the SP drive-cam yaw/pitch orbit, `PlayerController.cs:1516-1520` mouse path), stream `SendDriveInput(netId, throttle, steer, handbrake)` @50 Hz from WASD/space (`NetWorldHost.cs:462-469`). On `VehicleExited(self)`: unhide the shell at the replicated exit position (the server already teleported the entity beside the door, `VehicleReplication.cs:505-510`; adopt it via the snap path). Driver sees their own vehicle dead-reckoned — §3.6 v1 input-latency driving, accepted (fine on the ~sub-100 ms paths this test server serves).
- **Server side**: `VehicleNetSync` already applies remote `DriveInput` through the one drive seam + enter/exit side effects (`VehicleNetSync.cs:86-116`); `PlayerNetSync` (C2) skips seated peers. No changes expected beyond the reach validation.
- **Verify**: extend the existing L1 `net.vehicle_drive_sync` (`NetTests.cs:519-617`) with the shell path: a `ClientWorldSession` driver (not raw commands) enters via the interact seam, drives 4 s, exits; assert the seat handoff (`DriverPlayerId`), the node physically drove >8 m, the shell re-appears at the exit teleport. Scripted check on claw: connect, walk to the nearest replicated vehicle, drive N ticks — capture shows the vehicle displaced along a road with the player camera behind it; a second observer client's capture shows the same vehicle moved. **SP guard**: SP driving untouched (the direct `PlayerController._driving` path is not edited; ride mode is session-only code).

## 4. Content & map delivery

**Decision: clients resolve a local retail Unturned install; nothing map-shaped is downloaded or redistributed.**

- **What the client needs locally**: (a) the repo build incl. `game/content/` — delivered by the launcher's clone → build → import flow (`MainWindow.cs:119-126,205-226`), which is also what makes `NetContent.Hash` match the server (same commit = same constant, `NetContent.cs:11-12`); (b) `Maps/PEI/` from retail Unturned — resolved exactly like SP does today via `MapDir` (`Main.cs:22-24`).
- **Windows** (the actual tester platform): the launcher already resolves the Unturned dir silently (env → saved pick → default Steam path, `MainWindow.cs:114,30`), prompts with a folder picker at Play time if unresolved, persists the choice, and passes it to the game as `UG_UNTURNED_DIR` (`MainWindow.cs:232-240`). The "Multiplayer test" checkbox already appends `--connect=claw.bitvox.me` with the crucial `--` separator (`MainWindow.cs:252-259`). **No launcher change is required for delivery** — this pipeline shipped in launcher v6 (`MainWindow.cs:24`).
- **Linux** (dev boxes / the claw client-under-test): set `UG_UNTURNED_DIR` in the environment (the dedicated server already runs this way, CLAUDE.md §dedicated server). Document in the README next to the render-harness envs.
- **Failure mode**: C1's fail-fast screen replaces today's silent demo arena when the map is missing (the same cleanly-bail contract the dedicated server got in `WorldBuilder.cs:91-102`).
- **Version skew**: a client whose retail map files differ from the server's (different Unturned patch) renders subtly different terrain while the server's physics wins. Accepted for the test server. The seam to close it later is already documented in the code: fold a map manifest hash into `NetContent.Identity` (`NetContent.cs:8-9` says exactly this); bumping the Identity string is today's manual mismatch lever.
- **Rejected alternative**: launcher-downloads-the-map from a GitHub release. It redistributes retail assets (the rip pipeline deliberately keeps those out of the repo), adds a 100+ MB artifact to maintain, and solves a problem no current tester has (they all own Unturned). Revisit only if a mapless tester materializes.

## 5. Testing strategy

Three lanes, matching the house infra (`test.sh` L0/L1/L2 + the MP_PLAN §6 discipline):

1. **L0 (engine-free)** — wire-format goldens for `MoveInput` v2 + the `NetProtocol.Version` bump reject test; `SpawnProvider` plumbing (default keeps every existing L0 sim byte-identical). All in `tests/UnturnedNet.Tests/` on the existing `NetSimHarness`.
2. **L1 (in-engine, MemTransport, fallback world — no retail data, CI-safe)** — the workhorse, one new test per phase as listed in §3: `net.client_world_mode`, `net.server_avatar_terrain`, `net.shell_walk_reconcile`, `net.zombiefield_anyplayer`, `net.client_world_views`, and the extended `net.vehicle_drive_sync`. They follow the shipped pattern exactly: `WorldBuilder.BuildFullWorld(Dedicated/Client, "res://__no_such_map__", syncLoad: true)` for a deterministic fallback world + `MemNetwork` + `DedicatedServer { TransportOverride = MemServerTransport }` (`NetTests.cs:15-62`). The key genuinely-new assertion class: *collision-aware server movement* — replicated Y rising over a ramp is unfakeable under `IntegrateFlat`.
3. **Scripted headless connect-and-render (the real map, the claw box)** — the human-free "does a joined client actually see PEI" check. Two processes, because MemTransport-in-one-process would overlay two copies of the world in one scene tree: (a) `--dedicated` with `UG_UNTURNED_DIR` set (plain headless is fine — the server renders nothing); (b) the client via the standard xvfb + lavapipe + `--write-movie` harness (CLAUDE.md §headless render) with `-- --connect=127.0.0.1` + a per-phase script hook (`UG_MPWALK`, drive hook in C6), capturing a PNG at a late fixed frame. Wrap as `tools/mp_client_check.sh` (boot server → wait for the `[DEDICATED] world up` line → run client capture → kill server → compare against `tests/visual/golden/mp.<name>.png` with the same mean-abs-error diff `tools/visual_tests.py` uses). Wire into `test.sh` as an **opt-in lane** (`--mp`), like `--visual` — it needs retail map data, so it runs on claw, not on mapless CI. These are NEW goldens; the existing L2 set is never re-baselined (the SP-byte-identical guard is precisely `./test.sh --visual` passing untouched every phase).
4. **The live smoke** — the launcher's "Multiplayer test" checkbox against claw stays the final human check; the systemd `.path` watcher redeploys the server on push (CLAUDE.md §deploy), so each merged phase is on the test server within one rebuild.

Regression rule (CLAUDE.md): every bug found on this path ships its repro in the cheapest layer that expresses it — wire/prediction bugs are L0 harness scripts, composition/physics bugs are L1s on the fallback world, "it renders wrong" is a `--mp` golden.

## 6. Explicitly deferred (test-server scope cuts)

- **Combat over the wire for the shell** — `SendFire`/`SendMelee`/`SendGrenade` exist and are server-validated (`NetWorldHost.cs:351-373`), but wiring the shell's fire path + HitConfirm-driven damage/fx is post-milestone. Consequence: shooting from a joined client is local fx only. (Zombies can't hurt you either — C2's NetAvatar invulnerability guard — so the loop is consistent, if pacifist.)
- **Server-authoritative vitals / death / respawn for remote shells** — HUD vitals stay shell-local; `PlayerDied`/`Respawned` events exist when this lands (`NetWorldHost.cs:268-269`).
- **Inventory/pickup over the wire** — the owner-block replication + every command exists (`NetWorldHost.cs:417-433`), but binding the ported inventory UI to the replica (instead of the shell's local demo inventory, `PlayerController.cs:1480-1482`) is its own slice. World items are visible-only in v1.
- **Crop rendering + plant/harvest from the client**; **animals** (no AnimalField replication at all today, `WorldBuilder.cs:450`).
- **Driver-side vehicle prediction** (§3.6 v1 stands: dead-reckoned puppet-riding), **passengers/seats**, **PvP polish**.
- **Auth/encryption/master server/server browser; map auto-download; SP-loopback default-on** (`--mploopback` stays opt-in, `Main.cs:108`); **interest-policy tuning beyond the shipped rings/cells**; **a Linux export preset for the client**.

## 7. Risks / unknowns (with the file to check)

1. **`PlayerController` as a headless server avatar** — the biggest unknown. `_Ready` builds camera/viewmodel/UIs/BuildTool (`PlayerController.cs:1452-1490`); the `NetAvatar` early-outs must not null-deref the ~2000-line `_PhysicsProcess`/`_UnhandledInput` paths (e.g. `_cam` reads in look math, `_viewmodel` calls). Mitigation: keep the cheap nodes (camera non-Current), skip only UI/viewmodel/inventory; if the coupling fights back, fall back to a minimal `CharacterBody3D` avatar + widening `PlayerRegistry` to an interface (costs the zombie/loot-compat freebies — `ZombieController.cs:243,568` casts). **Check: `game/PlayerController.cs` (grep `_cam`, `_viewmodel`, `_invUI` uses outside `_Ready`).**
2. **Multiple `Input.*` readers on one box** — every non-NetAvatar `PlayerController` reads global input (`PlayerController.cs:2407` area); on the server, NetAvatar must never read it (headless gets none — probably safe, but `Input.IsActionPressed` still returns state if a window exists). **Check: the input block in `_PhysicsProcess`, `PlayerController.cs:2400-2420`.**
3. **Join-snapshot size on a populated PEI** — the join full snapshot is capped at half the reliable budget (~150 kB, `NetWorldHost.cs:100`, `NetProtocol.cs:66`) and the composer *truncates by dropping whole system blocks* when over budget (`SnapshotComposer.cs:185`). With C4's zombies+vehicles the join could silently drop a system for the first snapshot round. Relevancy rings should keep it far under, but measure. **Check: `core/UnturnedNet/SnapshotComposer.cs:150-200` truncation semantics + the `[DEDICATED]` composer diagnostics line (`DedicatedServer.cs:106`).**
4. **`ZombieField` internals** — I verified the streaming gate keys on the single `Player` (`ZombieField.cs:92-94`) but not the despawn/re-bucket logic further down; generalizing to N players may expose assumptions (per-player region hysteresis). **Check: `game/ZombieField.cs` past line 92.**
5. **Shell corrections vs. Godot physics interpolation** — the shell opts out of physics interp and does manual position interp (`PlayerController.cs:1451-1452`); applying reconciler deltas to `GlobalPosition` mid-tick must not fight that manual interp (visible micro-jitter). May need to fold the correction into the same place the manual interp samples. **Check: the `_interpReady` handling around `PlayerController.cs:2350`.**
6. **Vehicle exit position on slopes** — the server exit teleport is `v.Pos + right*2.4 + 1.0up` with no ground snap (`VehicleReplication.cs:505-510`); on PEI hillsides the shell may pop above/below ground until the next authoritative sample. Cheap fix if it shows: `Terr.SampleHeight` in a game-side exit hook. **Check: behavior in the C6 scripted run.**
7. **Client `ResourceField`/tree colliders vs. server truth** — the client builds trunk colliders locally (`ResourceField.cs:30-31`); a server-felled tree must also drop its client collider via `SetAlive` (the field zero-scales + disables colliders per its header, `ResourceField.cs:18-19` — verify the collider actually toggles). **Check: `game/ResourceField.cs:44-54`.**
8. **`DayNightCycle.Time` write cadence** — driving `Time` from snapshots while `VisualsEnabled` also advances it per-frame could stutter the sky; the loopback solved this with drift-only re-anchoring (`WorldNetSync.cs:36-77` `driveFromTick:false` mode). The client view may want the same "glide unless drifted" shape. **Check: `game/DayNightCycle.cs` `_Process`.**
9. **Two clients on one dev box** (for the observer-sees-driver check) — window focus + captured mouse on both; the scripted hooks (`UG_MPWALK`, drive hook) exist partly to avoid needing focus at all. Low risk, test-tooling only.

---

*Written 2026-07-17 against `main` @ b1fe907. The load-bearing calls: `WorldMode.Client` reusing `BuildFullWorld` (§2.1), server avatar bodies over the existing `ServerDrive` seam instead of a core rewrite (§2.3), `PlayerController`-as-avatar for the PlayerRegistry freebies (§2.3, risk 1), corrections-to-the-node prediction v1 (§2.3), local-retail-install map delivery (§4). Everything else follows.*
