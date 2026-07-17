# Vehicle Sleep + Idle-CPU Spike (read-only research, 2026-07-17)

Design/analysis only — no code changed. Goal: the headless dedicated server burns ~55% of a core at
0 players even with `Engine.MaxFps = 60`. Measured live (PID 3325534, passive `/proc` sampling only):

| thread (tid)        | name                | %CPU (15 s sample) | identity |
|---------------------|---------------------|--------------------|----------|
| 3325534             | main                | **34.5%**          | Godot main loop: 50 Hz sim spine + 85 × `Vehicle._PhysicsProcess` + `VehicleNetSync` publish |
| 3325542             | (unnamed Godot)     | **22.5%**          | **AudioServer mix thread** (dummy driver) — see Part B, identified with an evidence chain |
| 3325538–41          | `WorkerThread 0–3`  | 0.0%               | Godot WorkerThreadPool (Jolt jobs ride it) — idle, physics is already cheap |
| .NET Finalizer/Sockets/etc. | named       | ≤0.1%              | idle |

Two independent fixes. Part A (vehicle sleep) attacks the 34.5%; Part B (kill server-side audio
voices) attacks the 22.5%. **They do not overlap**: sleeping a vehicle does *not* silence its audio
voice (engine-off only sets volume to −80 dB, the stream keeps decoding — `game/Vehicle.cs:1658`),
so both are needed.

---

## Part A — vehicle sleep/wake design

### A.1 Where the 34.5% goes (cost anatomy)

Every physics tick (50 Hz), for each of the 85 PEI vehicles:

1. **`Vehicle._PhysicsProcess`** (`game/Vehicle.cs:1544-1772`) runs in full. `Vehicle` is a
   `VehicleBody3D` (`Vehicle.cs:8`), but the parked-car physics itself is already cheap — the settle
   latch freezes a stopped car to a static body (`Freeze = true`, `FreezeModeEnum.Static`,
   `Vehicle.cs:1616-1620`), which is why the Jolt workers idle at 0.5%. The cost is the **C# script
   body**: per-wheel `IsInContact()` loop (`:1605`), velocity low-pass (`:1608-1609`), the `wantHold`
   recompute (`:1613-1621`), engine RPM/gear (`:1631-1649`), engine-audio property writes
   (`:1650-1659`), noise emit throttle (`:1662-1668`), fuel/battery (`:1669-1675`), taillight logic
   (`:1714-1718`), crash detection (`:1721-1724`), smoke/dust particle property pokes (`:1725-1758`),
   steering smoothing (`:1769-1771`). Dozens of C#↔Godot interop property accesses per vehicle per
   tick.

   **Found bug — the existing off-screen perf skip never fires on the server.** `Vehicle.cs:1597-1601`
   early-returns for a frozen car only when a camera exists and it's behind/far:
   ```
   var cam = GetViewport().GetCamera3D();
   if (cam != null && (cam.IsPositionBehind(...) || ... > 90000f)) return;
   ```
   The dedicated server is headless — `GetCamera3D()` is null — so the guard is skipped and every
   frozen car runs the **full** settle sim forever. The skip that saves SP was structurally disabled
   on exactly the build that needed it most.

2. **`VehicleNetSync.Tick`** (`game/VehicleNetSync.cs:53-143`), registered as `"net.vehicles.sync"`
   on the SimRoot (`game/DedicatedServer.cs:121-122`), every tick: iterates
   `tree.GetNodesInGroup("vehicles")` (`VehicleNetSync.cs:61` — a marshaled 85-element Godot array
   allocation per tick), and per vehicle does the **publish block** (`:118-128`): reads
   `GlobalTransform`/`GlobalPosition`/`LinearVelocity`/`AngularVelocity`/`SteerAngleDegrees`/
   `Fuel`/`Health`/`Battery` + 6 flag properties (≈12 interop calls), extracts an euler, then calls
   `ServerPublish`.

3. **`VehicleReplication.ServerPublish`** (`core/UnturnedNet/VehicleReplication.cs:106-130`)
   quantizes *every* field (pos, 11-bit yaw/pitch/roll, 6.6-bit velocity components, 9-bit steer,
   fuel/health/battery) and then field-compares against the stored entity, returning early if
   unchanged (`:120-122`). So a parked car already costs **zero wire bytes** — but still pays the
   full quantize+compare, 50 times a second, per vehicle.

What is *already* free (verified — do not re-fix):
- **Wire suppression works.** Two short-circuits: the quantize+compare at publish
  (`VehicleReplication.cs:120-122`, runs once, not per-client) and the `LastChangedTick >
  baselineTick` test at delta compose (`VehicleReplication.cs:193-216`, the gate at `:199`). The
  composer never byte-diffs per client (`core/UnturnedNet/SnapshotComposer.cs:224-287`; no per-client
  world copy, documented at `:35-37`). Compose cadence is 25 Hz (`SnapshotDivisorTicks = 2`,
  `core/UnturnedNet/NetWorldHost.cs:46`, gated at `:240`); publish is 50 Hz.
- **Late joiners are safe if we stop publishing.** The join snapshot is a reliable FULL compose
  (`NetWorldHost.cs:221-238`) whose `WriteFull` (`VehicleReplication.cs:182-191`) reads the **same
  persistent entity registry** `ServerPublish` writes into — entities only leave it via
  `ServerRemove` (`VehicleNetSync.cs:139`). A vehicle we stop publishing still joins at its last
  published pose.

### A.2 Client puppet check — what happens when the server goes quiet

`game/VehicleReplicaView.cs` (client) dead-reckons: target = `e.Pos + e.LinVel * SinceSnap`
(`:60-66`), where `SinceSnap` grows unbounded while `e.Pos` doesn't change (`:25, 60-61`). There is
**no staleness timeout and no despawn-on-silence**: puppets are retired only when the entity leaves
the replica registry (`:77-86`), and the delta applier only removes explicit tombstones
(`VehicleReplication.ReadSnapshot`, `:227-235` — absence from a delta never removes). Conclusion:

- **Silence is safe iff the last published `LinVel` quantizes to exactly zero.** Then the
  extrapolation term is 0 and the puppet holds pose indefinitely. The freeze latch guarantees this:
  `Freeze = true` zeroes `LinearVelocity`/`AngularVelocity` first (`Vehicle.cs:1618`), so any publish
  taken *after* the freeze carries quantized-zero velocity.
- **The hazard**: if publishing ever stopped while the last snapshot carried non-zero velocity, the
  puppet drifts forever (no horizon cap). The sleep design below makes "≥1 publish after Freeze"
  a structural precondition, and the L1 test asserts the stored `LinVel` is zero at sleep time.
- Already regression-covered adjacent: `net.vehicle_drive_sync` asserts the at-rest puppet sits on
  the server transform (`game/testing/tests/NetTests.cs:602-603`).

### A.3 The design

Two orthogonal mechanisms; the first already exists:

- **Freeze** (existing, all modes): the physics settle latch (`Vehicle.cs:1613-1621`) — grounded +
  velocity ~0 ⇒ static body. Untouched.
- **NetSleep** (new, server-only): rides on top of Freeze. Owned by `VehicleNetSync`; a tiny inert
  hook in `Vehicle` for waking.

**State machine** (per tracked vehicle, evaluated in `VehicleNetSync.Tick`):

```
AWAKE ──(eligible for N=25 consecutive ticks)──▶ ASLEEP
ASLEEP ──(any wake trigger)──▶ AWAKE   (re-arms the N-tick counter)
```

**Eligibility (all must hold, checked per tick while awake):**

| condition | why | read |
|---|---|---|
| `v.Freeze` | settled + static; velocities already zeroed (`Vehicle.cs:1618`) → last publish is drift-safe | interop (or mirror a managed flag) |
| `e.DriverPlayerId == 0` and (listen-server) `v != localDriving` | occupied vehicles must sim + publish | managed |
| `!v.Exploded` | wrecks need `_burnTime` ticking for the fire lifecycle + 5-min despawn (`Vehicle.cs:1573-1592`) | managed |
| `!v.Alarmed` (new read-only accessor for `_alarmed`) | alarmed cars (5% of spawns, `Vehicle.cs:1180`) keep their proximity watch (`:1676-1688`), mirroring the existing skip-guard's exclusion (`:1597`) | managed |
| `CoupledTrailer == null && CoupledCab == null` | coupled rigs run `UpdateCoupled`/approach logic (`:1595-1596`); rare, not worth the edge cases | managed |
| `!v.HeadlightsOn` | lights-on drains battery per tick (`:1671-1675`); sleeping would freeze the drain mid-state | managed |

N=25 (0.5 s) is hysteresis against `wantHold` flapping and guarantees ≥1 post-Freeze publish (the
publish runs in the same tick as the eligibility check, so even N=2 would suffice).

**On entering ASLEEP:**
1. `v.NetSleep()` → sets a plain managed `NetAsleep = true` and `SetPhysicsProcess(false)` — the
   entire `_PhysicsProcess` body stops being called (this is the big win; a script callback that
   never fires costs nothing).
2. `Tracked.Asleep = true` in `VehicleNetSync` — the per-tick loop takes a cheap early-`continue`
   for this vehicle: **no transform reads, no euler, no `ServerPublish`**.
3. Nothing changes in core: the entity stays in the registry (join snapshots + `StateHash` sync-check
   blocks keep working — `StateHash` walks stored entities, `VehicleReplication.cs:238-267`, and the
   stored state is identical on both sides, so no desync).

**Wake triggers (exit ASLEEP):**

| trigger | path | already exists? |
|---|---|---|
| driver enters (remote) | `CommandEnterVehicle` → `ServerEnter` sets `e.DriverPlayerId` (`VehicleReplication.cs:491-499`); the sleeping-branch check in `VehicleNetSync` sees `DriverPlayerId != 0` → wake. The awake path's enter side effects already call `v.Wake()` (`VehicleNetSync.cs:91-97`) | check is new; effects exist |
| driver enters (listen-server local) | `v == localDriving` in the sleeping-branch check (`VehicleNetSync.cs:78-84` mirrors occupancy) | check is new |
| rammed by another vehicle | mover's `BodyEntered` → `OnVehicleContact` → `other.Wake()` (`Vehicle.cs:922, 205`). Signals fire regardless of `SetPhysicsProcess(false)` (body-level contact monitor) | exists — add `NetWake()` inside `Wake()` |
| damage (gunfire, explosion chain, bumper) | every damage path funnels through `Vehicle.TakeDamage` (`Vehicle.cs:227`); `ExplodeDamage` (`:265`) hits neighbours through it too. **Today `TakeDamage` does NOT wake** — without a hook, a sleeping car shot to 0 HP would never tick `_deadTimer` and never explode | **add `NetWake()` call** |
| repair/salvage | `Repair` (`Vehicle.cs:1534`) changes replicated Health → add `NetWake()`; `Salvage` (`:1535`) frees the node → the stale-reconciliation sweep (`VehicleNetSync.cs:131-142`, still runs every tick) retires the entity | add to `Repair` |
| coupling | `CoupleTo` already calls `trailer.Wake()`/`Wake()` (`Vehicle.cs:1242, 1255`) | exists via `Wake()` hook |

`Vehicle.NetWake()` is the single exit hook: `if (!NetAsleep) return; NetAsleep = false;
SetPhysicsProcess(true);` — called from `Wake()`, `TakeDamage()`, `Repair()`. `VehicleNetSync`'s
sleeping branch treats `!v.NetAsleep` as "woken from the Vehicle side" and resumes publishing.
All checks in the sleeping branch are **managed reads** (plain C# fields, a dictionary `TryGet`) —
zero interop per sleeping vehicle per tick.

Nothing simulated is lost while asleep: engine + headlights are off after exit (`VehicleNetSync.cs:110-116`),
so fuel/battery drain is already zero (`Vehicle.cs:1669-1675` are gated on them); wrecks and alarmed
cars are excluded; a static body can't be moved by anything that doesn't already route through a
wake trigger.

### A.4 Touch points (exact seams)

| file | change |
|---|---|
| `game/Vehicle.cs` | add `public bool NetAsleep` + `NetSleep()`/`NetWake()` (~8 lines); call `NetWake()` from `Wake()` (`:204`), `TakeDamage()` (`:227`), `Repair()` (`:1534`); add a read-only `public bool Alarmed => _alarmed;` accessor. Optional hygiene: make the frozen-skip guard treat a null camera as "skip" (`:1597-1601`) — covers the awake-frozen minority (alarmed cars, the 0.5 s pre-sleep window) on headless |
| `game/VehicleNetSync.cs` | in `Tick()` (`:53`): per-`Tracked` sleep counter + `Asleep` flag; sleeping branch (cheap wake checks, else `continue`) placed **before** the publish block (`:118`); enter-sleep transition calls `v.NetSleep()`. Add a `public bool IsAsleep(uint netId)` diag accessor for the L1 test. (~30 lines) |
| `core/*` | **no changes** — the delta/dirty/join machinery already behaves correctly when publishes stop |

### A.5 SP / test safety argument

- `VehicleNetSync` is constructed **only** by `DedicatedServer` (`game/DedicatedServer.cs:121`) and
  `MpLoopback` (`game/MpLoopback.cs:65`). SP never instantiates it ⇒ nothing ever sets `NetAsleep`
  ⇒ the `Vehicle.cs` additions are inert no-ops in SP. SP behavior is byte-identical.
- Listen-server/loopback: a locally-driven vehicle is excluded by `v != localDriving`; the L1 suite's
  driven vehicles are never `Freeze`+driverless, so `net.vehicle_drive_sync` (`NetTests.cs:517-618`),
  `net.dropin_dropout` (`:411`), `net.populated_world_quiet` (`:1177`), `net.shell_drive` (`:1425`)
  stay green — and `net.vehicle_drive_sync`'s enter-a-parked-jeep step becomes an incidental
  regression guard for wake-on-enter once its jeep has time to sleep first.
- L0 (`tests/UnturnedNet.Tests/VehicleReplicationTests.cs`) exercises core only — untouched code.
- The camera-guard hygiene fix only widens an existing *skip* for frozen, non-alarmed, non-dying
  cars whose unfreeze path is `Wake()`-driven (a frozen body's `wantHold` can't flip false on its
  own — `_velAvg`/`_angAvg` are zero) — so skipping the recompute is semantics-preserving.

### A.6 Regression test plan (L1, ships with the fix)

`net.vehicle_sleep` in `game/testing/tests/NetTests.cs`, following the `NetVehicleDriveSync`
scaffold (`:517-560`): flat dedicated world + `MemNetwork` + observer client + `DedicatedServer`
(MemTransport ⇒ the `Engine.MaxFps` gate at `DedicatedServer.cs:44` is skipped, per its comment) +
one real jeep.

1. **Falls asleep**: `Until(jeep.Freeze)` then `Until(sync.IsAsleep(id))`; assert
   `!jeep.IsPhysicsProcessing()`, and the stored entity's velocity is exactly zero (the drift
   invariant of §A.2).
2. **Stops publishing / client holds**: record the observer puppet position, run 100 ticks, assert
   the puppet still exists (no despawn), moved < 1 cm, and the server entity's `LastChangedTick`
   did not advance.
3. **Late join while asleep**: connect a second client *after* sleep; assert it materializes the
   puppet at the parked pose (join `WriteFull`-from-registry correctness).
4. **Poke wakes — damage**: `jeep.TakeDamage(10)`; assert `IsPhysicsProcessing()`, `!IsAsleep`, and
   the Health delta reaches the observer (publish resumed).
5. **Poke wakes — enter**: let it re-sleep, then `SendEnterVehicle` + drive; assert the puppet moves
   (reuse the drive-sync tail assertions).

Cheapest-layer argument: requires `VehicleBody3D` physics + the Godot node seam ⇒ L1 (not
expressible in L0).

### A.7 Optional follow-ups (measure first, likely unnecessary after sleep)

- Throttle the `GetNodesInGroup("vehicles")` mint scan (`VehicleNetSync.cs:61`) to ~10 Hz and iterate
  `_tracked` for the per-tick work — kills the per-tick 85-element marshaled array.
- `SortedIds()` allocates + sorts a fresh `List<uint>` per compose per system
  (`VehicleReplication.cs:330-336`) — cache and invalidate on add/remove.
- Settled husks could also `SetPhysicsProcess(false)` after the 60 s fire-out, waking only for the
  360 s despawn via a timer — marginal (wrecks are transient).
- The alarm proximity watch iterates the whole zombies group per alarmed car every 0.3 s
  (`Vehicle.cs:1686`) — server-relevant on a populated map; alarmed cars self-retire after one firing
  (`:1697`), so low priority.

---

## Part B — the mystery 22.5% thread: it's the AudioServer mix thread

### B.1 Identification (evidence chain, all passive/read-only)

1. **Per-thread `/proc` sample** (15 s, table at top): the hot second thread (tid 3325542) is an
   *unnamed native Godot* thread. All .NET runtime threads are named (`.NET Finalizer`, `.NET
   Sockets`, …) and idle; Godot's `WorkerThread 0–3` (the WorkerThreadPool Jolt rides) are idle.
2. **This repo's C# creates zero threads.** Repo-wide audit: no `new Thread`/`Task.Run`/timers/
   `ThreadPool`/`Parallel` anywhere in `game/` or `core/`. The UDP transport is a **non-blocking**
   socket (`Blocking = false`, `core/SDG.NetTransport/UdpNetTransport.cs:49,89`) whose `Receive` is a
   single `ReceiveFrom` returning false on WouldBlock (`:54-67`), drained inline by
   `NetServerSession.Tick`'s `while (Receive(...))` (`core/UnturnedNet/NetServerSession.cs:87-100`) —
   called from `TickSimulation` on the main thread (`NetWorldHost.cs:156` ← `DedicatedServer.cs:105`
   ← `SimDriver._PhysicsProcess`, `game/SimDriver.cs:16-19`). No stdin/console reader exists
   (`DevConsole` is a client `CanvasLayer`, never added on the dedicated path). So the thread is
   engine-side.
3. **Not the render thread**: `game/project.godot` has **no `[rendering]` section** —
   `rendering/driver/threads/thread_model` is absent ⇒ default single-safe (no separate render
   thread), and `--headless` (the systemd `ExecStart`, `deploy/systemd/unturned-server.service:14`)
   uses the dummy renderer regardless. **Not a separate physics thread**:
   `physics/3d/run_on_separate_thread` absent ⇒ false; Jolt steps on the main thread and jobs on the
   (idle) WorkerThreadPool.
4. **Kernel fingerprint**: the hot tid sleeps in `clock_nanosleep`
   (`/proc/<pid>/task/<tid>/wchan` = `hrtimer_nanosleep`; kernel stack confirms) and takes only
   **~8 voluntary wakeups/s** — yet burns 22.5%. That's a timed loop doing ~28 ms of work per
   ~93 ms cycle. This is exactly `AudioDriverDummy::thread_func`: mix `buffer_frames`, then
   `delay_usec(buffer_frames / mix_rate)` — the default 4096 frames @ 44.1 kHz = **92.9 ms**,
   matching the observed period. (`--headless` forces the Dummy *audio* driver, but Dummy still runs
   a real mixer thread and really mixes all playing voices; only the output is discarded.)

### B.2 Root cause: 85 always-on engine-loop voices mixed for nobody

`game/Vehicle.cs:1147-1148` — every vehicle gets
`_engineAudio = new AudioStreamPlayer3D { ..., Autoplay = true }`; the loop starts the moment the
node enters the tree. The dedicated `WorldBuilder` path spawns the same full `Vehicle` nodes as SP
(the shared vehicle-spawn extraction, `game/WorldBuilder.cs:271-273`), so the server starts **85
looping OGG voices**. Engine-off only sets `VolumeDb = -80` (`Vehicle.cs:1658`) — the stream keeps
playing, so each voice still pays Vorbis decode + resample + mix every 93 ms chunk, forever. This is
the only `Autoplay` in the entire game (repo grep), so it's the whole voice set. ~28 ms of decode/mix
per 93 ms chunk for ~85 voices on a Graviton core is the observed 22.5%.

Note the interplay: Part A's sleep does **not** fix this (the voice keeps mixing regardless of
`SetPhysicsProcess`), and muting a bus is not a reliable fix (mute applies at output; sources are
still pulled). The fix is to not have playing voices on the server.

### B.3 The fix

**Primary — no audio nodes on the dedicated server** (matches the existing "dedicated fx hygiene"
precedent: shadows/foliage/visuals off, `WorldBuilder.cs:91-95, 458`):
- Add a static gate, e.g. `Vehicle.ServerNoAudio` (or pass `WorldMode` down), set in
  `Main.BuildDedicated` (`game/Main.cs:1920-1943`) **before** the world builds.
- In `Vehicle.Build`, skip creating the four `AudioStreamPlayer3D`s when set: siren (`:1104-1105`),
  horn (`:1139`), engine (`:1147-1148`), explosion (`:1177`). All uses are already null-guarded
  **except** `Honk()`'s `_hornAudio.Play()` (`Vehicle.cs:1373`) — add the null check.
- Sweep other server-built nodes for `.Play()` voices (zombies, ambience) behind the same gate —
  one-shots only mix transiently, so they're secondary, but free to gate at creation.

**Bonus (helps SP + client too):** on engine-off, set `_engineAudio.StreamPaused = true` instead of
(or with) the −80 dB write (`Vehicle.cs:1650-1658`) — a paused voice is skipped by the mixer, so
parked cars stop paying decode in every mode. Resume on `EngineOn`.

**Do not** globally change `audio/driver/mix_rate` in `project.godot` — it's shared with real
clients.

**Adjacent main-thread knobs** (small, test with the same harness):
- `Engine.MaxFps = 60` → **50** (`game/DedicatedServer.cs:44`): the sim is a 50 Hz accumulator
  (`core/UnturnedSim/SimClock.cs:10`); 60 fps buys nothing and runs 20% more scene-frame iterations.
  Lower (e.g. 30, physics catching up multi-step per frame) also works but batches ticks — try 50
  first.
- `common/physics_interpolation=true` (`game/project.godot:24`) makes moving nodes maintain
  per-frame interpolated transforms for a renderer that doesn't exist on the server. Candidate:
  at dedicated boot set the scene-tree root's `PhysicsInterpolationMode = Off` (inherited
  tree-wide). Low confidence on the win size — measure.
- `OS.LowProcessorUsageMode` is not set anywhere — irrelevant while `MaxFps` caps the loop, noted
  for completeness.

### B.4 How to confirm

The passive sampler used for this spike (no ptrace, safe on the live process):

```bash
PID=$(pgrep -f 'dedicated'); snap(){ for t in /proc/$PID/task/*; do echo "${t##*/}|$(tr -d '\n' <$t/comm)|$(awk '{print $14+$15}' $t/stat)"; done; }
snap >/tmp/a; sleep 15; snap >/tmp/b
join -t'|' /tmp/a /tmp/b | awk -F'|' '{printf "%5.1f%%  %s %s\n",($5-$3)/15,$1,$2}' | sort -rn | head
```

Expected after the audio gate: the 22.5% thread drops to ≈0 (the dummy mixer keeps its ~11 Hz
nanosleep cycle but mixes zero voices). Expected after vehicle sleep: the main thread sheds the
per-vehicle share of its 34.5% (residual = zombie/world publishers, snapshot compose, scene
overhead — measure the split then). Wakeup-rate cross-check: the hot tid's ~8 voluntary
wakeups/s should persist (same loop) while its CPU vanishes — confirming it was per-voice mix cost,
not the loop itself.

---

## Execution order (when implementing)

1. **Part B audio gate** first — smallest diff (a static flag + 4 skips + 1 null guard), biggest
   certain win (~22% of a core), zero interaction with replication. Deploy via
   `tools/deploy-server.sh`, confirm with the B.4 sampler.
2. **Part A sleep**: `Vehicle` hooks (`NetAsleep`/`NetSleep`/`NetWake` + `Wake`/`TakeDamage`/`Repair`
   call sites + `Alarmed` accessor) → `VehicleNetSync` sleep counter/branch + `IsAsleep` diag →
   `net.vehicle_sleep` L1 test (same commit, per the regression rule) → `./test.sh` (L0+L1; no L2 —
   nothing visual changes).
3. Camera-null hygiene fix in the frozen-skip guard (`Vehicle.cs:1597-1601`) — one line, covers the
   awake-frozen minority on headless.
4. `MaxFps` 60→50 (`DedicatedServer.cs:44`); re-measure; only then consider the physics-interpolation
   and scan-throttle follow-ups if the residual justifies them.
5. Re-run the B.4 sampler at 0 players; target: ~55% of a core → low single digits + whatever the
   zombie/world publishers genuinely cost. Log the before/after in PROGRESS.md.
