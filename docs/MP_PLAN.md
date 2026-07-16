Repo surveyed — I have what I need: the ported net primitives (`core/SDG.NetPak/`, `core/SDG.NetTransport/`, `core/UnturnedNet/NetGame.cs`), the sim spine (`core/UnturnedSim/`), every unnetworked game system in `game/`, the 3-layer test infra (`test.sh`, `game/testing/`, `docs/TESTING_PROPOSAL.md`), and the two existing loopback net tests that are the model to scale up.

---

# PLAN — Multiplayer Support for unturned-godot

*Server-authoritative multiplayer on the ported NetPak/NetTransport base, with a replication model designed for THIS game's systems — not a port of Unturned's.*

## 0. TL;DR

| Piece | Decision (recommended) | Where it lives |
|---|---|---|
| Transport | Keep `IClientTransport`/`IServerTransport`/`ITransportConnection` + `UdpNetTransport` as-is (dumb datagrams). Add `MemTransport` (in-memory, deterministic) for SP-loopback + tests. | `core/SDG.NetTransport/` |
| Reliability | NEW session layer **above** the transport: seq/ack-bitfield, reliable-ordered + unreliable-sequenced channels, fragmentation, connect/timeout lifecycle. Not inside the UDP transport. | `core/UnturnedNet/` |
| Serialization | NetPak bit-packing everywhere; quantized via the existing `WriteClampedFloat`/`WriteDegrees`/etc. Golden byte tests lock the wire format. | `core/SDG.NetPak/` (unchanged) |
| Replication | Three planes: **snapshot** (server→client, per-system delta writers), **command** (client→server, typed structs, validated), **event** (server→client, reliable facts). Explicit registries — no reflection RPC. | `core/UnturnedNet/` + per-system writers in `game/` |
| Authority | Server-authoritative. Dedicated server = **headless Godot** running the real world. SP = same world + in-process server + `MemTransport` loopback. Listen-server falls out for free. | `game/` (WorldBuilder + `--dedicated`) |
| Tick | NetTick = Godot physics tick = **50 Hz** (`game/project.godot:23` already pins it). `SimRoot` finally gets adopted as the in-tick ordering layer. Snapshots at 25 Hz default. | `core/UnturnedSim/` + `game/SimDriver.cs` |
| Ids | `NetId` = session-scoped `uint32` minted by the server, per replicated entity. Nothing in `game/` has instance ids today — minting them is Phase-2 work. | `core/UnturnedNet/` |
| Tests | Every phase ships L0 deterministic multi-peer sims over `MemTransport` (no sockets, no `Thread.Sleep`) + L1 in-engine loopback `GameTest`s, in the existing `[TEST] name | PASS/FAIL` grammar. Packet loss/reorder/join-mid-game are seeded L0 sims. | `tests/UnturnedNet.Tests/`, `game/testing/tests/NetTests.cs` |

Cross-cutting rules:

- **The wire format is versioned from byte one.** Every datagram starts with magic + protocol version; handshake rejects mismatches. Golden byte tests make format drift a test failure, not a live desync.
- **All state mutation goes through commands.** Client input, inventory moves, placement, DevConsole cheats — one server-side validation choke point. No client ever writes authoritative state directly.
- **Per-system wire code lives next to the system**, engine-free where the state already is (inventory, skills, power graph), node-adjacent where it isn't (vehicles).
- **No calendar estimates.** Numbers in this doc are technical (Hz, bits, bytes, MTU).

**The single highest-leverage fact:** `game/project.godot:23` sets `physics_ticks_per_second=50` — identical to `SimClock.FixedDelta = 0.02` (`core/UnturnedSim/SimClock.cs:10`). Every gameplay system already steps on a fixed 50 Hz tick; they're just stepping on Godot's, unordered, instead of `SimRoot`'s, ordered. Adopting the (built, tested, currently **unused**) `SimRoot` spine is a mechanical migration, not a rewrite — and it's the precondition for everything else in this plan.

## 1. Current state (what I verified in the repo)

**Genuinely works and is the base to build on:**

- `core/SDG.NetPak/` — Unturned's real bit-packing wire format, ported. `NetPakWriter`/`NetPakReader` (scratch-buffer bit packing, `NetPakWriter.cs:13`), plus a rich quantization surface in `SystemNetPakWriterEx.cs`: `WriteClampedFloat(intBits, fracBits)`, `WriteUnsignedNormalizedFloat`, `WriteRadians`/`WriteDegrees` (default 8-bit), `WriteGuid`, `WriteEnum<T>`, `WriteList<T>`, `WriteStateArray`, and `AlignToByte()` (load-bearing for skippable snapshot blocks, §2.4). ~46 L0 tests in `tests/SDG.NetPak.Tests/`.
- `core/SDG.NetTransport/` — the real U3 transport seams: `IClientTransport` (`ClientTransport.cs:15`), `IServerTransport` (`ServerTransport.cs:17`), `ITransportConnection` (`TransportConnection.cs:10`, endpoint-equatable). `UdpNetTransport.cs` implements them over raw non-blocking sockets: poll-based `Receive`, `Send` = one `SendTo`. Round-trips verified by `tests/NetTransport.Tests/UdpRoundTripTests.cs`.
- `core/UnturnedNet/NetGame.cs` (292 lines) — a working server-authoritative prototype: `NetServer` assigns `byte` ids, runs a toy zombie sim + hitscan with head/torso/leg zone multipliers, broadcasts the **whole world every tick**; `NetClient` applies it. Proven 2-client sync in `tests/UnturnedNet.Tests/TwoPlayerSyncTests.cs`. Wired to Godot by `game/ServerNode.cs` / `game/ClientNode.cs` / `game/NetDemoNode.cs` (`--server`/`--client`/`--netdemo` build bare arenas — not the PEI world).
- `core/UnturnedSim/` — the deterministic 50 Hz spine (`SimClock`, `SimRoot`, `ISimStepped`) **plus** the engine-free sim seeds already extracted: `PlayerMovementSim` (pure `Step(inputDir, wantJump, grounded, dt)`), `CombatMath` (explosion/fall/stealth), `PowerSolver` (pure graph solve, moved from `game/` in 9ba8b9a). All L0-tested (`tests/UnturnedSim.Tests/`, 45 tests).
- The test infra (`docs/TESTING_PROPOSAL.md`, all phases live): `./test.sh` = 1161 green (L0 1132 + L1 29), `[SUITE]`/`[TEST]`/`[SUMMARY]` grammar, L1 `GameTest` coroutines with per-test sandbox + `ResetGlobals()`, L2 visual goldens.

**Verified stubbed / missing (the answers to the open questions):**

- **Reliability is a no-op.** `ENetReliability` is accepted and ignored — `UdpTransportConnection.Send` (`UdpNetTransport.cs:19-20`) and `UdpClientTransport.Send` (`UdpNetTransport.cs:85-86`) are bare `SendTo` regardless of flag. No acks, no retransmit, no ordering, no dedup, no fragmentation, no connection lifecycle (no handshake, no timeout — `Initialize` just binds a socket; a "connection" springs into being on first datagram, `UdpNetTransport.cs:54-67`). `NetGame.cs:110`'s "reliable" Welcome is reliable in name only: one lost datagram = a client that never learns its id. The U3 SDK itself points at the fix — `SendType.cs:8`: *"Ideally this will become purely 'unreliable', and layer on top of transport will handle message building."* That layered design is exactly what §2.2 builds.
- **The sim spine is 100% unused.** Nothing calls `SimRoot.Add`; `game/SimDriver.cs` is never instantiated (grep-verified). Every gameplay system free-runs on `_PhysicsProcess`/`_Process` directly, in arbitrary order.
- **`NetGame.cs` doesn't scale and isn't meant to.** Full-world broadcast (`NetServer.Broadcast`, `NetGame.cs:203-216`), uncompressed 32-bit floats (`PlayerState.Write`, 25 bytes/player), `byte` ids, a hand-rolled `MsgType` switch, zero contact with the real game systems (`PlayerController`, `Vehicle`, `Deployable`, inventory are never referenced). It's the proof of shape — Phase 3 re-founds it on the real stack and deletes the `MsgType` switch.
- **No entity has an instance id.** Deployables/ports/wires/vehicles/zombies/world items are identified by Godot node reference + group membership only. `DeployableDef.Id`/`Item.id` are *type* ids.
- **No save/persistence system exists** (grep-verified; `BuildTool.cs:8` says "no save" explicitly). MP persistence is greenfield — §5 keeps it decoupled.
- **Single-local-player assumptions are load-bearing.** `PlayerController.Local` (static, `PlayerController.cs:989`) is read by explosions, vehicle prompts, deployables, sound. `HUD` polls the local player every frame; `DevConsole` mutates inventory/skills/vehicles directly (`DevConsole.cs:73-137`) — a free cheat client under MP unless routed through commands.
- **Composition is ad-hoc.** `game/Main.cs` (~2700 lines) builds every mode by inline `AddChild` per flag; `--server`/`--client` assemble a different arena than `--peiplay`/`BuildObjectsTest` (the real world, `Main.cs:1471+`). There is no shared world-root abstraction for server/client/SP to share.
- **Export**: `game/export_presets.cfg` has exactly one preset (Windows Desktop client, `dedicated_server=false`). No headless/server preset, no Linux target. Dev-box testing runs the raw Godot binary, so this only gates *shipping* a server, not building one.

## 2. Target architecture

### 2.1 Authority + process model

**Fork A — where does the authoritative sim run?**

- *(a) Engine-free .NET server process (extend the `NetGame.cs` approach).* Maximum testability, no Godot on the server. **Rejected:** vehicles are `VehicleBody3D`/Jolt physics (`game/Vehicle.cs:6-8`), players collide via `MoveAndSlide`, zombies path on `NavigationServer` — reimplementing all of that engine-free is a second game. The toy zombie sim in `NetGame.cs:174` is the ceiling of this approach.
- *(b) **RECOMMENDED: headless Godot server.*** The dedicated server is the same Godot build run headless (`--dedicated` flag → real world via the shared WorldBuilder, no camera/HUD/viewmodel). Physics, nav, and node-coupled systems work unchanged; the engine-free core (`PowerSolver`, `PlayerMovementSim`, `CombatMath`, inventory model, skills) stays engine-free for L0 tests. Cost: server perf carries some node overhead (fx subtrees can be skipped when dedicated — deferred hygiene, §5).
- *(c) Hybrid now.* (b) is the hybrid: pure-C# subsystems stay pure; the world host is Godot.

**Fork B — dedicated vs listen-server vs SP.**

- **RECOMMENDED: one architecture, three run modes.** The *server world* is the only world. **Dedicated** = headless Godot + `UdpServerTransport`. **Single-player** = the same server world in-process + the local client attached over `MemTransport` (loopback, zero sockets, zero latency — prediction becomes a pass-through). **Listen-server** = SP that also opens the UDP socket. SP-as-loopback-server is the clean answer the constraint asks about, and it's the right one — with the phasing caveat that SP keeps its current direct path until Phase 4, so the game stays shippable throughout (§4). The payoff: after cutover, every SP session exercises the MP code path, which is the cheapest MP soak test that will ever exist.
- *Alternative (SP stays offline forever):* preserves today's code exactly, but every system permanently maintains two mutation paths (direct + command). That's the "bolt-on later" trap the dev lead is worried about. Rejected.

### 2.2 Reliability + session layer (`core/UnturnedNet/`)

New, engine-free, built on the untouched transport interfaces. `NetSession` wraps one `ITransportConnection` (server side, one per client) or `IClientTransport` (client side):

- **Packet header** (every datagram): `magic:8` + `version:8` + `channel:3` + `seq:16` + `ack:16` + `ackBits:32`. Sequence/ack-bitfield per Gaffer-style connection accounting; RTT estimated from ack timing (`IClientTransport.TryGetPing` stays optional/transport-level).
- **Channels:** **Control** (connect/accept/reject/disconnect/keepalive — pre-session), **ReliableOrdered** (commands, events, join snapshot: msgId window, retransmit on RTO ≈ max(100 ms, 1.5×RTT), in-order delivery, fragmentation for messages > MTU budget), **UnreliableSeq** (inputs, snapshots: newer-seq-wins, stale drops on the floor).
- **MTU budget: 1200 bytes** payload per datagram (conservative internet-safe). Reliable messages fragment (`msgId`, `fragIdx:8`, `fragCount:8`); snapshots are *kept under* budget by delta + cadence instead of fragmenting (losing one fragment of an unreliable snapshot wastes the whole thing) — the join-time full snapshot goes over the reliable channel where fragmentation is safe.
- **Connection lifecycle:** client → `Connect{version, mapName, contentHash, name}`; server → `Accept{playerId, serverTick, worldConfig}` or `Reject{reason}`; keepalive at 1 Hz when idle; 5 s silence = disconnect (feeding the existing `ServerTransportConnectionFailureCallback` seam, `ServerTransport.cs:12`). Server keys clients by endpoint (`UdpTransportConnection` equality is already by-endpoint, `UdpNetTransport.cs:35`).

*Fork — reliability inside `UdpNetTransport` instead?* Would honor the `ENetReliability` flag where it's passed today, but buries protocol logic per-transport, and contradicts the SDK's own layering note (`SendType.cs:8`). Keeping transports dumb also means `MemTransport` and any future transport get reliability for free. *Fork — adopt LiteNetLib/ENet?* Mature, but replaces the ported base the constraint says to keep, drags in a dependency, and kills the byte-level golden-test story. **Layer above wins.**

*Web seam (do not build):* a `WebSocketTransport`/`WebRtcTransport` would implement the same `IClientTransport`/`IServerTransport` behind `NetSession`. Since WebSocket is inherently reliable-ordered, `NetSession` takes a `TransportCaps{supportsUnreliable}` flag at construction so the reliable channel can trust the transport instead of double-acking. That one flag is the entire cost of keeping the door open; the only export preset today is Windows Desktop, so nothing more is warranted.

### 2.3 The replication model (the fresh design)

Why not Unturned's: retail replication is `SteamCall`/`NetInvokable` reflection-RPC — hundreds of per-method RPCs discovered by reflection, state scattered across call sites, every system hand-rolling its own late-join resend, all coupled to Steam connections. It works at Unturned's scale but it's unauditable, un-testable at the byte level, AOT-hostile, and this game's systems (an event-driven power graph, a pure-C# grid inventory, a deterministic solver core) have better-fitting shapes. The dev call is explicit: don't port it. Instead, three planes over the §2.2 channels:

**Snapshot plane (server→client, UnreliableSeq, default 25 Hz).** Continuous state. Each netted system implements:

```csharp
public interface IReplicatedSystem
{
    byte SystemId { get; }                                        // append-only registry
    void WriteFull(NetPakWriter w, in ReplicationContext ctx);    // join/late-join/baseline-reset
    void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick);
    void ReadSnapshot(NetPakReader r, bool full);                 // client side
    ulong StateHash();                                            // tests only: sync verification
}
```

`ReplicationContext` carries `{serverTick, clientPlayerId, viewPos}` — the interest-management hook is *in the signature from day one* even while every implementation ignores `viewPos` (§2.6). Dirty tracking is per-entity `lastChangedTick`; `WriteDelta` includes an entity iff `lastChangedTick > baselineTick`. The server keeps each client's last-acked snapshot tick; baseline older than the dirty-ring depth (64 ticks) → send full. No per-client world copies.

**Command plane (client→server).** Typed structs with hand-written `Write/Read` (the `PlayerState.Write` pattern, `NetGame.cs:26`), registered explicitly in a static `CommandRegistry` — greppable, AOT-safe, no reflection. Two delivery classes: `MoveInput` @50 Hz on UnreliableSeq (carrying the last 3 inputs redundantly so single loss costs nothing), everything transactional (`InventoryMove`, `PlaceDeployable`, `ConnectWire`, `EnterVehicle`, `Fire`, `ConsoleCommand`…) on ReliableOrdered. Every command is validated server-side (rate, range, ownership, plausibility); sender identity comes from the connection, never the payload.

**Event plane (server→client, ReliableOrdered; fx-only events may opt into UnreliableSeq).** Discrete facts that don't belong in state: `DeployablePlaced`, `WireConnected`, `EntityDestroyed`, `ImpactFx`, `HitConfirm`, `ItemPickupDenied`, chat. Same registry pattern.

What this buys over Unturned's model, concretely: late-join is free (`WriteFull` is the join snapshot — no per-system resend code); deltas give bandwidth control centrally; every message type is a struct you can golden-test byte-for-byte; the whole protocol is enumerable by reading two registry files; and systems own their wire format next to their state instead of scattering RPCs.

### 2.4 Snapshot framing

Snapshot = `serverTick:32` + `baselineTick:32` (0 = full) + repeated system blocks: `systemId:8` + `byteLen:16` + `AlignToByte()` + payload. Byte-aligned, length-prefixed blocks mean: a client can skip a system it doesn't know (forward compat), a reader bug corrupts one block instead of the stream, and each system's payload golden-tests in isolation. `NetPakWriter.AlignToByte` (`NetPakWriter.cs:132`) exists for exactly this.

Quantization defaults (tunable constants in one file, locked by golden tests): position via `WriteClampedFloat` bounded to the map (PEI fits ±1024 m XZ: 11 int + 8 frac bits/axis; Y 9+8) ≈ 55 bits; yaw/pitch via `WriteDegrees(11)`; velocities normalized. A player snapshot entry lands ≈ 12 bytes vs today's 25 uncompressed. Napkin worst case, 8 players + 60 zombies full-visible at 25 Hz: `8×12 + 60×8 ≈ 576 B/snapshot ≈ 14.4 kB/s ≈ 115 kbps` — inside MTU per snapshot and fine before any relevancy filtering.

### 2.5 Tick, time, prediction

- **NetTick = Godot physics tick = 50 Hz**, shared by server and client (`project.godot:23`). `SimRoot` is adopted as the ordering layer: `SimDriver._PhysicsProcess` → `Sim.Frame(delta)` → systems step in registered order — input-apply → player sim → vehicles (Godot physics acts between ticks; the vehicle *system* step reads results + writes commands) → zombies → combat/projectiles → power/world → **replication send** last. Ordered stepping is what makes "state at tick N" a coherent, snapshot-able statement.
- **Snapshot cadence:** players/vehicles every 2nd tick (25 Hz); zombies/animals every 4th (12.5 Hz); crops/day-night/scalars every 25th (2 Hz). Per-system cadence is a registry property, not protocol.
- **Client interpolation:** remote entities render 100 ms behind newest snapshot (2.5 snapshot intervals at 25 Hz), interpolating between the two bracketing snapshots — the standard Valve-style scheme.
- **Local prediction — fork:**
  - *(a) None (server-driven local player):* unplayable beyond LAN. Rejected as the end state, but it's the correct *first* milestone in Phase 4.
  - *(b) **RECOMMENDED: predict + correct.*** Client applies its own input immediately through the existing `PlayerController` path; server runs the same inputs through the same `PlayerMovementSim` + `MoveAndSlide`; snapshots carry `lastProcessedInputSeq` + authoritative transform; client compares against its stored prediction for that seq and smooths out the error (snap only above a threshold). Cheap, robust, no re-simulation.
  - *(c) Full rollback-replay:* re-run pending inputs on every correction. `PlayerMovementSim` is pure and replayable, but `MoveAndSlide` collision isn't cheaply re-steppable outside the physics tick. **Deferred** — and it's a *client-side-only* upgrade later because the protocol already carries everything (c) needs (input seqs + acked seq). Decide-now: the protocol fields. Defer: the algorithm.
- **Day/night time** derives from `serverTick` (tick × 0.02 s × configured day length) — synced by the handshake + implicitly by every snapshot header; no separate clock stream (today it free-runs per client in `DayNightCycle._Process`).
- **Determinism boundary (decide now):** the server is authoritative; **no lockstep determinism across peers is required, ever.** The only determinism this design leans on is (i) `SimClock`'s fixed step and (ii) `PowerSolver` being a pure function — same replicated graph in, same lamp states out (§3.1). Zombie AI's OS-seeded `RandomNumberGenerator` (`ZombieController.cs:46,99`) is fine as-is because only the server rolls it.

### 2.6 Ownership, ids, interest

- **`NetId` = `uint32`**, server-minted, monotonically increasing, 0 = invalid, **session-scoped** (never persisted — a future save system uses its own stable keys, §5). One flat space; the owning system gives it meaning. Sub-addressing: ports are `(deployableNetId, portIndex:4)` — port order comes from `DeployableDef.Ports`, which is stable. Players get `PlayerId` (`ushort`, connection-scoped) in addition to their avatar's NetId.
- **Ownership:** entities carry `OwnerPlayerId` where it matters (vehicle driver, deployable placer). Commands act on NetIds but authorize by sender connection.
- **Interest management: hook now, policy later.** Snapshot composition is already per-client (per-client baselines force that anyway) and `ReplicationContext.viewPos` is in every `WriteDelta` signature. v1 policy = AllRelevant plus one hard-coded exception: **owner-only blocks** (vitals, full inventory — §3.3/§3.4), which double as the proof the hook works. Distance rings, the 19 zombie nav pockets as relevancy cells (`ZombieField` already buckets by pocket), and priority accumulators under a per-client byte budget are deferred policies that slot into the existing signature without protocol change.

## 3. Per-system replication design

Ordered by retrofit risk, lowest first. "Cmd/Event/Snap" = which plane carries it.

### 3.1 Deployables + power grid — *the showcase for the new model*

Authoritative: server owns the graph (deployables, ports, wires), health/fuel, toggle intent. **Replicate the solver's inputs, never its outputs.** Topology changes are reliable Events (`DeployablePlaced{netId, defId, transform}`, `DeployableRemoved`, `WireConnected{netId, src:(id,port), dst:(id,port)}`, `WireRemoved`, `Toggled{id, on}`); continuous scalars (health, fuel) ride a low-cadence delta Snap block. Every client runs the same pure `PowerSolver.Solve` (`core/UnturnedSim/PowerSolver.cs`) on its replica of the graph — `Live`/`Powered`/`Draw`, lamp ramps, flicker, vibration all derive locally, exactly as they derive from `PowerNet.Recompute` today. The server also solves, authoritatively, for gameplay effects (load-scaled fuel burn). Placement/wiring/toggling/salvage are Cmds validated server-side (range, valid port, one-wire-per-port rule). Contrast with vanilla Unturned, which RPCs each state change per interactable: we ship a handful of graph events + a deterministic solve. Predicted: nothing (placement shows the local ghost until the confirm event — the placer UI already works that way). Retrofit risk: **low-medium** — the system is already event-driven (`PowerNet.MarkDirty` on change), the solver is already pure and L0-tested; the work is minting NetIds and swapping direct mutation for Cmd+Event. `PowerNet`'s static dirty flag stays: `ResetForTests` already handles test isolation (`PowerNet.cs:17`).

### 3.2 Skills — *trivial, do early as the owner-only pilot*

Authoritative: server computes XP awards (kill/harvest/craft hooks) and applies `TryUpgrade`. State = `experience:u32` + ~22 level bytes (`PlayerSkills.cs` is pure C#). Owner-only Snap block (first consumer of the interest hook); `UpgradeSkill` Cmd; `XpAwarded` Event for the HUD ping. Predicted: nothing. Retrofit risk: **minimal**.

### 3.3 Inventory + world items

Authoritative: server owns every `PlayerInventory` and all `WorldItem`s. The model layer (`Items`/`ItemJar`/`Item`, `game/inventory/Items.cs`) is pure C# with change events — the cleanest state in the repo. All mutations become Cmds: `MoveItem{page,from,to,rot}`, `DropItem`, `PickupItem{netId}`, `EquipItem`, `Craft{blueprintId}`, `Consume` — validated against the server grid (the ported `tryFindSpace`/grid logic *is* the validator). Owner gets an owner-only Snap block (full inventory delta keyed on the existing `onStateUpdated` dirtiness); other players see only worn/held items via the player appearance snapshot. `WorldItem`s: server-spawned entities with NetIds — spawn Event with initial throw velocity (clients run the cosmetic tumble locally), a settled-transform Event when the server's physics freezes it (`WorldItem` already settles-and-freezes), removal Event on pickup. Loot: `LootField`'s per-point deterministic rolls move server-side (spawn/despawn must key on *any* player's proximity, not the local player's); spawned loot replicates as world items. Predicted: grid moves apply optimistically client-side and reconcile on the authoritative block (rare mismatch = visible snap-back, acceptable). Retrofit risk: **medium** — wide Cmd surface, but the model layer ports untouched and the UI already listens to events rather than polling.

### 3.4 Player + combat

Authoritative: server steps each player from replicated `MoveInput{seq, moveAxes, look, buttons}` (50 Hz, UnreliableSeq, 3× redundant) through `PlayerMovementSim` + `MoveAndSlide`, plus stance FSM, vitals (`UpdateVitals` rates), fall damage (`FallMath`), cooldown/reload FSMs. Snap: transform/stance/anim-flags/held-item @25 Hz for everyone; vitals (health/food/water/stamina/infection/bleeding/broken) owner-only; other players expose only alive/dead + coarse health. Combat: `Fire` Cmd carries client aim ray; **server steps ballistics** — the `Bullet` list with gravity drop (`PlayerController.cs:1838,2339`) moves into the server's combat step; hits validate server-side (the zone-multiplier logic from `NetServer.Hitscan` generalizes to per-limb checks against server positions). Melee = deferred-hit timer server-side; grenades = server-spawned short-lived entities (Snap while flying, explosion Event). Client fx (muzzle, tracer, casings, `Viewmodel.cs` entirely) stay client-local, triggered by `FireEvent`/`ImpactFx` events — the viewmodel never crosses the wire. Predicted: local movement (§2.5b); firing plays fx immediately, damage waits for `HitConfirm`. **Retrofit risk: highest in the plan** — requires splitting `PlayerController` (2442 lines) into a sim core (movement/vitals/combat state, driven by an input struct — the server runs one per player, headless) and a local shell (input capture, camera, viewmodel, HUD binding, prediction), and breaking the `PlayerController.Local` static (`PlayerController.cs:989`) that explosions/vehicles/deployables read — those call sites become "iterate players" or "nearest player" queries. This split is *the* decide-now refactor; everything player-adjacent waits on it.

### 3.5 Zombies + animals

Authoritative: server runs the real `ZombieController` AI (sensing/flanking/specialities) with `NavigationServer` on the headless world — nav baking is already offline (`pei_pocket_N.res`). Snap: transform + anim-state byte + speciality @12.5 Hz; `ZombieHit`/`ZombieDied`/`AttackSwing` Events. Client: puppet mode — `ZombieController` gets a `IsPuppet` path that skips AI/nav/physics and interpolates (the rig/anim layer renders as-is). Non-determinism of zombie RNG is irrelevant (server-only rolls, §2.5). The toy `NetServer.TickZombies` sim is deleted when this lands. Predicted: nothing. Retrofit risk: **medium** — mostly carving the brain/puppet seam; sensing already targets `Node3D Target`, which generalizes to any player avatar. Zombie counts × 12.5 Hz is the first real bandwidth consumer — the nav-pocket relevancy cells are the ready-made filter when needed.

### 3.6 Vehicles

Authoritative: server owns vehicle physics (`VehicleBody3D` on the headless server), fuel/health/battery, paint variant (replicate the spawn `variant` byte — paint derives deterministically, `Vehicle.cs:329-339`), lights/siren/horn flags. Driver sends `DriveInput{throttle, steer, handbrake}` Cmds @50 Hz feeding `Vehicle.Drive` (`Vehicle.cs:1128`); `EnterVehicle`/`ExitVehicle` Cmds gated server-side. Snap @25 Hz: transform + linear/angular velocity (quantized) + wheel steer/spin summary + scalars. Clients (including the driver, v1) render **puppets** — no `VehicleBody3D` on client replicas, just interpolated meshes with wheel dressing; velocity in the snapshot enables dead-reckoned extrapolation between snapshots. *Fork — driver-side prediction:* real vehicle prediction means client-side physics + reconciliation, the hardest sync problem in the plan; v1 accepts input-latency driving (fine on LAN/loopback, tolerable at moderate ping), revisit after §4 Phase 7 ships. Single-driver only today (no passenger list exists — `PlayerController._driving` is the only link), which conveniently shrinks v1 scope; seats become a small server-side occupancy array when passengers land. Retrofit risk: **high** — physics authority migration + the `Vehicle`/`PlayerController` enter/drive/exit entanglement; isolated though: nothing else depends on vehicle internals.

### 3.7 World: crops, resources, day-night, map

- **Map/static world:** never networked. Clients load the same map from disk; handshake carries map name + content hash and rejects mismatch. There is no world seed to sync — the world is authored data (heightmaps, `Objects.dat`, `Trees.dat`), deterministic from files.
- **Day-night:** server tick-derived (§2.5); `DayNightCycle` reads synced time instead of free-running. Trivial.
- **Crops:** server owns `CropManager`'s clock and the AGRICULTURE second-yield roll (`GD.Randf`, `CropManager.cs:69` — moves server-side). `Plant`/`Harvest` Cmds; growth stage as a tiny low-cadence Snap block or per-stage Events (recommend Snap block: join-consistency for free).
- **Resources (trees):** deterministic index from `Trees.dat` order = implicit id. `ResourceHarvested`/`ResourceRespawned` Events + an alive-bitmap in `WriteFull` for join.
- **Storage crates (`StorageCrate`):** an `Items` page addressed by the crate's NetId — same Cmd surface as inventory, plus open/close arbitration (one opener at a time, server-enforced). Retrofit risk: low, rides entirely on §3.3.

## 4. Phased migration (each phase ships green, SP never breaks)

**Phase 1 — the reliable session layer (core only, zero game/ changes).** `core/UnturnedNet/` gains `NetSession`, the packet header, the three channels (Control/ReliableOrdered/UnreliableSeq), fragmentation, connect/accept/timeout lifecycle — all above the untouched transport interfaces. `core/SDG.NetTransport/` gains `MemTransport` (paired in-memory `IClientTransport`/`IServerTransport` with a deterministic `FaultyLink` — seeded loss/dup/reorder/latency knobs). Tests (all L0, `tests/UnturnedNet.Tests/`): golden byte tests for the header; reliable delivery under 30% seeded loss + reorder; ordering; fragmentation round-trip of a 100 kB payload; connect/reject/timeout; a soak that asserts no reliable-window stall over 10k ticks. The existing `NetGame` demo keeps running untouched beside it. **This phase turns "reliable" from a comment into a property the test suite enforces.**

**Phase 2 — replication framing (core only).** `NetId` minting + `NetEntityRegistry`; `IReplicatedSystem` + `SnapshotComposer`/`SnapshotApplier` with per-client baseline acks and the 64-tick dirty ring; `CommandRegistry`/`EventRegistry` with explicit ids; `ReplicationContext` carrying the interest hook; quantization constants file. Tests: snapshot full/delta round-trip equivalence (`StateHash` compare) under loss; baseline-expiry → full-resend; unknown-systemId skip (forward compat); golden bytes for framing; command validation plumbing. *Phases 1+2 are the "work it out upfront" core — pure C#, fully L0, no Godot, and everything after is consumers.*

**Phase 3 — the server world exists (first game/ changes, behavior-neutral).** Extract `WorldBuilder` from `Main.cs`'s `BuildObjectsTest`/`BuildPeiPlay` so server/client/SP assemble the same world with a mode flag (dedicated skips camera/HUD/viewmodel). Instantiate `SimDriver`/`SimRoot` for real; migrate system stepping into ordered `ISimStepped` registrations (mechanical: same 50 Hz cadence, now ordered — replication steps last). Add `--dedicated` (headless Godot boots the real PEI world + a `NetSession` server). Re-found the 2-player demo on the new stack (players as the first `IReplicatedSystem`, `MoveInput` as the first Cmd) and **delete the `MsgType` switch + full-world broadcast** from `NetGame.cs`. Tests: L0 tick-order regression; L1 `net.dedicated_boot` (world builds headless, ticks advance); L0 two-client join+move over `MemTransport`, deterministic, no `Thread.Sleep` — retiring the sleep-based pump in `TwoPlayerSyncTests.cs`. Unblocks: every per-system phase.

**Phase 4 — players for real.** The `PlayerController` split (sim core vs local shell, §3.4); `PlayerController.Local` call sites converted to player-registry queries; join/handshake flow end-to-end (version + content hash → accept → reliable full snapshot → spawn → deltas); remote avatars via the existing `CharacterModel` puppet path (`ClientNode.cs` already proves it); prediction v1 (predict + smooth-correct). SP-loopback lands **behind a flag** (`--mploopback`): SP still defaults to the direct path until parity is proven. Tests: L0 prediction-correction convergence sim; L0 join-mid-game (client connects at tick 500, `StateHash` parity after sync); L1 `net.loopback_join_move` (two in-process clients on the real world, positions converge per the `[TEST]` grammar). *This is the riskiest phase; everything after it is additive.*

**Phase 5 — combat + zombies.** Server ballistics step + `Fire`/`HitConfirm`/`ImpactFx`; melee + grenades server-side; damage/death/respawn lifecycle; zombie brain/puppet split, real `ZombieController` AI authoritative on the server, toy `TickZombies` deleted. Tests: L0 hit-validation (out-of-range/rate-abuse rejected); L0 two-client kill-credit sync; L1 `net.zombie_chase_sync` (server zombie visible + interpolated on client).

**Phase 6 — the transactional slice: inventory, items, skills, deployables + power.** Inventory Cmd surface + owner-only blocks (§3.3); world items with NetIds; server-side loot; skills (§3.2, the owner-only pilot — can land any time after Phase 4); deployable/wire/toggle Cmd+Event surface with client-side `PowerSolver` recompute (§3.1); `DevConsole` mutations routed through `ConsoleCommand` (server-gated — closes the free-cheat hole). Tests: L0 inventory command validation (illegal moves rejected, grids converge by `StateHash`); L0 power-graph replication (topology events → both sides' `PowerSolver` agree — reusing `PowerSolverTests` fixtures); L1 `net.deploy_wire_power` (client A places generator+spotlight+wire, client B sees the lamp light).

**Phase 7 — vehicles.** Server-authoritative vehicle physics, `DriveInput`/`Enter`/`Exit` Cmds, puppet replicas with dead-reckoning (§3.6). Tests: L0 enter/exit arbitration (two clients race one seat — exactly one wins); L1 `net.vehicle_drive_sync` (driven vehicle's transform converges on the observer within tolerance).

**Phase 8 — world state + MP ops.** Day-night from server tick; crops; resource alive-bitmap; storage crates; relevancy v1 (distance rings + nav-pocket cells) + per-client byte budget with priority accumulators; dedicated-server export preset (`dedicated_server=true`, Linux target) in `export_presets.cfg`; disconnect/rejoin hardening; fx-subtree skipping on dedicated. Tests: L0 relevancy (far entity absent from A's snapshot, present in B's); L0 budget (snapshot ≤ configured bytes under load); L1 `net.dropin_dropout` soak.

Ordering rationale: 1–4 are the architecture-critical decisions running as code (reliability → framing → world/tick → the player split); 5–6 are gameplay value on a proven substrate; 7 is hard-but-isolated; 8 is polish + policy. Phases 1–2 touch zero game code; Phase 3 is behavior-neutral refactoring; SP switches its default path only when Phase 4's parity tests are green.

## 5. Decide now vs defer

**Locked in now (retrofit-hostile if wrong):**

1. **Wire format v1 + version byte** — header layout, channel semantics, fragmentation framing; golden-tested from Phase 1. The version byte is the escape hatch for everything else.
2. **NetId scheme** — u32, server-minted, session-scoped, **never persisted**; `SystemId`/`CommandId`/`EventId` registries append-only.
3. **Snapshot discipline** — full/delta duality on every system from day one (late-join is otherwise a per-system retrofit — the classic bolt-on failure); per-client baseline-ack; byte-aligned length-prefixed skippable blocks.
4. **Everything-is-a-command** — including DevConsole. Retrofitting validation onto direct-mutation paths later means re-auditing every call site.
5. **Tick model** — NetTick = 50 Hz physics tick; `SimRoot` ordering with replication last; per-system snapshot cadence as registry data.
6. **Input protocol fields** — input seq + redundancy + `lastProcessedInputSeq` in snapshots: carries prediction v1 *and* future rollback without wire change.
7. **The `PlayerController` sim/shell split + death of `PlayerController.Local`** — the single biggest code-shape decision; every combat/vehicle/deployable interaction touches it.
8. **`WorldBuilder`** — one world assembly for SP/client/dedicated, or the three modes drift forever.
9. **Interest hook in the compose signature** (`ReplicationContext.viewPos` + owner-only blocks) — hook now, policy later.
10. **Quantization bounds** — map-extent position encoding baked into golden tests (changing bounds = version bump, so choose for the biggest plausible map now).
11. **Determinism boundary** — server-authoritative, no cross-peer lockstep, pure-function solver replication only (§2.5). Nobody should ever "fix" a desync by chasing float determinism.
12. **Save/persistence decoupling** — no save system exists; when one lands it mirrors the per-system `WriteFull` enumeration but uses its own stable keys and format. Wire compactness and save stability must not fight.

**Safely deferred (slots into existing seams):**

- Relevancy *policies* (distance rings, nav-pocket cells, priority accumulators) and bandwidth shaping — the hook ships in Phase 2, policies in Phase 8.
- Driver-side vehicle prediction; player rollback-replay (client-side upgrades, §2.5/§3.6).
- WebSocket/WebRTC transport (seam = `TransportCaps`, §2.2), encryption/auth, master server/browser, NAT punching for listen-servers.
- Passengers/seats beyond single driver; spectator; voice; text chat (an Event type whenever wanted).
- Anti-cheat beyond command validation; per-field encryption; snapshot compression passes (varint/context) beyond NetPak quantization.
- Dedicated-server fx-subtree trimming; server perf hygiene generally.
- Persistence itself.

## 6. Test strategy (how MP stays inside the 3-layer infra)

**The workhorse: deterministic L0 multi-peer sims.** A `NetSimHarness` (test-side, `tests/UnturnedNet.Tests/`) owns a server session + N client sessions over `MemTransport`, and a `Step()` that runs one 50 Hz tick: clients emit, `FaultyLink` delivers per its seeded schedule, server ticks + composes snapshots, clients apply. No sockets, no `Thread.Sleep`, no wall clock — a 1000-tick two-client sim runs in milliseconds and *identically* every run (seed printed in the failure line, matching the L1 `seed` convention). This replaces the sleep-and-poll pump in `TwoPlayerSyncTests.cs:` (today's model, kept until Phase 3 retires it). Adverse-network tests are just `FaultyLink` configs: `loss=0.3, reorder=true, dup=0.05, latencyTicks=5`.

**Wire-format regression = golden byte tests.** Every header/command/event/snapshot-block serializer gets a test asserting exact hex output for a fixed input (the `[TEST] net.wire.player_snapshot_bytes | PASS` style). Any accidental format change fails loudly; intentional changes re-golden alongside a version bump in the same commit — the same re-baseline discipline as the L2 goldens.

**Sync correctness = `StateHash` parity.** Each `IReplicatedSystem` exposes a test-only `StateHash()`. The canonical MP regression test shape: run a scripted scenario through the harness, assert server hash == every client hash at end. Join-mid-game: tick 500, connect late client, pump the join snapshot, assert parity vs a from-the-start client. Every per-system phase ships one.

**L1 = in-engine loopback.** `GameTest`s (`game/testing/tests/NetTests.cs`) boot the real world once via TestHost, run server + clients in-process over `MemTransport`, and assert node-level truth: `net.loopback_join_move`, `net.deploy_wire_power`, `net.vehicle_drive_sync`, `net.zombie_chase_sync`. Same `[TEST] name | PASS/FAIL | detail` grammar, `Ticks(n)`/`Until(cond)` pumping, `ResetGlobals()` teardown (which grows a `Net.ResetForTests()`). `./test.sh` picks all of this up with zero runner changes — L0 suites are auto-discovered by csproj, L1 by reflection.

**Per the regression rule:** every MP bug that reaches `main` ships its repro in the cheapest layer — protocol/framing/sync bugs are L0 harness scripts by construction; only node-integration bugs (puppet rendering, physics handoff) cost an L1. Packet loss, reordering, duplication, late join, disconnect/rejoin, and malicious-command fuzzing (random bytes into `CommandRegistry` must never crash the server) are all L0-expressible from Phase 1 onward.

---

*Written 2026-07-16 against `main` @ 239176b. Review forks: §2.1 (headless-Godot server), §2.2 (session-layer reliability), §2.5 (predict+correct v1), §3.6 (no driver prediction v1), §4 (phase order). Everything else is load-bearing detail that follows from those five calls.*
