# Client Scripting Harness — Design Plan

**Status: PLAN (no code yet). Deliverable of the 2026-07-17 "client scripting system" ask.**

The goal: a programmatic **virtual player** that wraps a REAL client (`ClientWorldSession` + its
`PlayerController` shell), exposes high-level actions (walk, look, interact/press-E, drive, fire) and
client-side state reads (shell pos, ride state, ridden-puppet pos, camera transform, focus, health,
inventory), runs inside the existing L1 harness, and serves both as a permanent `net.*` regression
suite and as a few-lines throwaway repro tool — specifically the kind of test that would have caught
"the server drives the car but the DRIVER's own screen shows nothing moving."

Everything below is grounded in the code as of `main` @ `322b233`.

---

## 1. Honest accounting: what already exists, and what the actual hole is

The prompt for this plan says "no automated test sits a real client shell in the seat." That was true
of the Phase-7 test but is **no longer the whole truth** — two newer L1 tests already script a real
client shell end to end, and the plan must build on them rather than reinvent them:

- **`net.vehicle_drive_sync`** (`game/testing/tests/NetTests.cs:517-618`) — the Phase-7 test the
  prompt describes: a headless `NetWorldClient` calls wire methods directly
  (`driver.SendDriveInput(...)` at :539, `driver.SendEnterVehicle(netId)` at :567) and asserts the
  SERVER node drove (:594) and the **observer's** `VehicleReplicaView` puppet tracked it (:595-596).
  No client shell exists. The driver's own view is not represented at all.
- **`net.shell_drive`** (`NetTests.cs:1709-1845`) — the C6 finish-line test. This one DOES sit a real
  shell in the seat: a `ClientWorldSession` joins over MemTransport (:1727-1728), the shell walks to
  the jeep on `ScriptedInput` (:1769-1772), enters through the real interact seam
  `RequestEnterNearestPuppet()` (:1780), drives via `ScriptedDrive` (:1795), and exits via
  `RequestExitPuppet()` (:1811). It asserts the server node drove >8 m (:1798), the **observer's**
  puppet drove with it (:1799-1800), and — importantly — that "the shell rode along":
  `sess.Shell.GlobalPosition.DistanceTo(jeep.GlobalPosition) < 6f` (:1801).
- **`net.shell_fire_zombie`** (`NetTests.cs:1856-1985`) — scripted client combat: `sess.Shell.Fire()`
  (:1925) through the wired `NetFire` seam, kill credit asserted from the server's facts.

So the **coarse** driver-side story is covered: if the driver's own puppet froze entirely,
`RidePuppet()` (which copies the puppet position onto the shell every physics tick,
`game/PlayerController.cs:1241`) would leave the shell at the enter point while the jeep drove >8 m
away, and `net.shell_drive:1801` would fail. The live bug got through anyway, which localizes the
uncovered surface precisely. What NO test reads today:

1. **The camera — what the human actually sees.** In ride mode the camera is torn off the shell
   (`_cam.TopLevel = true`, `PlayerController.cs:1200`) and repositioned only on the FRAME plane by
   `PositionRideCam(_riding.GlobalTransform)` inside `_Process` (`PlayerController.cs:2448-2449`,
   implementation :2623-2643). If that per-frame chain breaks (gating, `_fp` state, TopLevel reset,
   a stale `_riding` reference), the puppet and shell can move perfectly while the rendered view sits
   still — exactly the reported symptom — and every existing assertion stays green. There is a public
   accessor already (`public Camera3D Camera => _cam;`, `PlayerController.cs:1608`); nothing asserts
   on it.
2. **The driver's own puppet, read directly.** `net.shell_drive` reads the observer's view
   (`obsView.TryGetPuppet`, :1791). The driver's own `Session.VehicleView` puppet (the node the
   driver's camera chases, seated via `EnterPuppet` at `game/ClientWorldSession.cs:169`) is only
   tested *indirectly* through the 6 m shell-follow bound — a puppet that stutters, snaps, or lags
   seconds behind still passes.
3. **Look-driven interaction.** `UpdateLookFocus` (`PlayerController.cs:136-218`) is hard-gated on
   `Input.MouseMode == Input.MouseModeEnum.Captured` (:140). Under the headless L1 host
   (`test.sh:91` runs `godot --headless -- --tests`) the headless DisplayServer never reports a
   captured mouse, so `_focusItem`/`_focusVehicle`/`_focusPuppet` are never set: `TryPickup()`
   (:517-528) keys off `_focusItem` and is therefore unreachable, and the full F-interact chain
   (:1805-1818) can only be exercised piecemeal via the two `Request*Puppet` methods.
4. **A unified API.** Each Net test hand-rolls ~30 lines of rig (world build + MemNetwork + pump +
   session + DedicatedServer + teardown). That boilerplate is the reason one-off repros don't get
   written.
5. **The live plane.** L1 shares one process, one physics world, MemTransport, and the flat fallback
   ground. The live client is `WorldMode.Client` on the real PEI map over real UDP with a real window
   (`game/Main.cs:1961` `BuildClient`, :1992-1997 attaching `ClientWorldSession`). If the drive bug
   does not reproduce under the L1 rig (plausible, since `net.shell_drive` presumably passes today),
   it lives in this plane, and only a live-loopback harness can catch it (§9, Phase 3).

The design below closes 1-4 with a thin wrapper plus five small seams, and scopes 5 as an explicit
opt-in phase.

---

## 2. Ground truth: the pieces the harness composes

### 2.1 The L1 coroutine harness

- `game/testing/TestHost.cs` discovers `GameTest` subclasses by reflection (:31-43), gives each a
  per-test `Node3D` sandbox (`World`, :75-77), and advances the test coroutine one **physics tick**
  at a time in `_PhysicsProcess` (:45-67). `yield return Ticks(n)` consumes n fixed 50 Hz ticks;
  `Until(cond, cap)` polls per tick (`game/testing/GameTest.cs:23-24`, `Step` at :28-38). Teardown
  QueueFrees the sandbox and resets known global statics (`ResetGlobals`, `TestHost.cs:114-120`) —
  any static the harness introduces must be registered there.

### 2.2 The real client and its existing control seams

`ClientWorldSession` (`game/ClientWorldSession.cs:26`) is the joined-client composition node — the
same class the live `--connect=` client attaches (`Main.cs:111`, :1992-1997). It owns the
`NetWorldClient`, all replica views (`Remotes`/`Puppets`/`Items`/`VehicleView`, :81-99), and the
shell. Key mechanics the harness rides on:

- **Sim-step registration** (:139-144): `net.client.pump` (Client.Tick — receive, apply snapshots,
  ack) then `client.shell` (`ShellStep`) are added to the world's `SimDriver` in `_Ready`, i.e. at
  `AddChild` time — order relative to other steps is determined by AddChild order (§4).
- **`ShellStep`** (:147-199): spawns the shell on the first authoritative own-entity sample
  (`SpawnShell`, :201-232), streams `SendDriveInput` from `Shell.LastDriveInput` while riding
  (:164-171, engaging `EnterPuppet` when the puppet materializes at :169), else runs the
  reconcile-then-send walk loop (:175-198).
- **Seat facts**: `_ridingNetId` latched by `VehicleEntered(self)` (:88), cleared + shell restored by
  `OnVehicleExited` (:239-258). `RidingVehicle` is public (:45).
- **`SpawnShell`** wires the MP seams: `NetEnterVehicle`/`NetExitVehicle` (:220-221),
  `NetFire`/`NetMelee`/`NetGrenade`/`NetReload` (:224-227), sets `CaptureMouse = true` (:206), and
  seeds `ScriptedInput` if `UG_MPWALK=1` (:229-230) — the precedent for scripted-shell hooks here.
- **Global-static caveat**: `DevConsole.RemoteClient = Client` (:78) — a process-wide static; two
  sessions in one test fight over it (§10, risk 5).

`PlayerController` control seams that already exist (reuse, don't fork):

| Seam | Where | What it drives |
|---|---|---|
| `ScriptedInput` (Vector2 strafe/forward) | `PlayerController.cs:1110`, consumed :2711 | walk axes, bypasses keys and the `UiInputBlocked` gate |
| `ScriptedStance` | :1132, consumed :2707 | stance incl. SPRINT |
| `ScriptedJump` | :1134, consumed :2718 | jump bit |
| `ScriptedDrive` (Vector2 steer/throttle) | :2585, consumed in `RidePuppet` :1232 and SP `DriveVehicle` :2599 | drive intent while riding/driving |
| `Fire()` (public, bool) | :2019 | one trigger pull; aims from `Rotation.Y` + `_pitchDeg` (:2051-2052), NOT the live camera basis; auto guns = call once per tick, `_fireCd` provides the cadence (:2027) |
| `StartReload()` / `EquipHotbar(n)` | :1786 / :1789 (used by `SpawnShell`:212) | reload / equip |
| `RequestEnterNearestPuppet()` / `RequestExitPuppet()` | :1177-1191 | the MP seat request seams (≤4 m, `NearestPuppet` :1160-1171) |
| `TruePhysicsPosition` / `ApplyNetCorrection` | :1125-1130 | true position read / displacement injection |
| `Camera` | :1608 | the view transform read |
| `IsRiding` / `RidingPuppet` / `LastDriveInput` / `LastHandbrakeInput` | :1143-1146 | ride state reads |
| `Health` / `Kills` / `Inventory` / `Stance` / `HasGunOut` / `HeldGunName` | :42 / :36 / :18 / :1097 / :659 / :1638 | vitals/inventory/equipment reads |
| `DriveFP` | :2586 | force first-person vehicle cam |
| `NetAvatar` | :1250 | the server-avatar construction: never reads global Input, but also skips ALL client-side per-frame work (`_Process` early-out :2443) — the wrong tool for a scripted CLIENT |

### 2.3 The driver's-view render chain (the thing the worked example asserts)

Per physics tick: `RidePuppet()` captures drive intent and copies the puppet position onto the shell
(`PlayerController.cs:1228-1242`, entered from `_PhysicsProcess` :2651). Per frame:
`VehicleReplicaView._Process` dead-reckons every replicated vehicle's puppet toward
`e.Pos + e.LinVel * sinceSnap` with an exponential glide and an 8 m snap
(`game/VehicleReplicaView.cs:36-87`, constants :18-19), and `PlayerController._Process` places the
ride camera from the puppet transform (:2448-2449 → `PositionRideCam` :2623 → `PositionVehicleCam`
:2625-2643; 3P chase distance = `clamp(size*1.1, 6.5, 34)` :2637, FP eye = `DriverEyeLocal`,
`game/VehiclePuppet.cs:74`). The seated 3P body is posed on the puppet's `SeatOffset` (:2506-2509).
Headless `_Process` demonstrably runs and moves puppets — `net.shell_drive`'s observer-puppet
assertions (:1799-1800) depend on it and gate CI.

---

## 3. The API surface: `NetRig` + `ScriptedClient`

Two small classes in `game/testing/` (compiled with the game assembly like `TestHost`):

### 3.1 `NetRig` — the one-call world+server+client boot

Encapsulates the exact `net.shell_drive` wiring (NetTests.cs:1718-1735) so a one-off repro is a
few lines:

```csharp
// game/testing/NetRig.cs  (sketch — names final, bodies indicative)
public sealed class NetRig
{
    public WorldBuildResult World;         // the ONE world path, Dedicated mode, fallback ground
    public MemNetwork Net;
    public DedicatedServer Ded;
    public ScriptedClient Driver;          // the first (shell-owning) scripted client
    DelegateSimStep _pump;
    Node3D _sandbox;

    // Boot order (§4 is the argument): world -> netpump -> session(s) -> DedicatedServer LAST.
    public static NetRig Boot(Node3D world, ulong seed, string playerName = "scripted",
                              bool remoteAvatars = true, bool allowCheats = false)
    {
        var r = new NetRig { _sandbox = world };
        r.World = WorldBuilder.BuildFullWorld(world, WorldMode.Dedicated,
            "res://__no_such_map__", "placements.txt",
            noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE").Result;
        r.Net = new MemNetwork(seed);
        r._pump = new DelegateSimStep((t, dt) => r.Net.Tick(), "l1.netpump");
        r.World.Sim.Sim.Add(r._pump);                       // datagram delivery FIRST
        r.Driver = ScriptedClient.Join(world, r.World, r.Net, playerName);
        r.Ded = new DedicatedServer { Driver = r.World.Sim,
            TransportOverride = new MemServerTransport(r.Net),
            RemoteAvatars = remoteAvatars, AllowCheats = allowCheats };
        world.AddChild(r.Ded);                              // server sim + replicate stay LAST (§2.5)
        return r;
    }

    // extra headless observers (raw NetWorldClient + optional VehicleReplicaView), pumped by the rig
    public NetWorldClient AddObserver(string name, out VehicleReplicaView view) { ... }

    // server-side world authoring, mirroring the tests' AddChild pattern (NetTests.cs:1743-1745)
    public Vehicle ServerSpawnVehicle(string spec, Vector3 pos, int variant = 0) { ... }

    public void Teardown()   // Sim.Remove(_pump) + Disconnect() every client -- the tests' closing bars
}
```

`Boot` deliberately keeps the world in `Dedicated` mode (flat fallback, deterministic on any box, no
retail data — the `NetDedicatedBoot` pattern, NetTests.cs:21-24). The **client world in L1 is the
server's world**; that shared-sandbox compromise and its one real hazard are handled in §6.3.

### 3.2 `ScriptedClient` — the virtual player

**Model: held-state actions + momentary method calls**, matching the wire's held-keys input model
and the existing `Scripted*` seams. Explicitly rejected: synthesizing `InputEventKey`/`InputEventMouse`
through `Input.ParseInputEvent` — it reintroduces device-state ambiguity, races the real keyboard on
a windowed host, and would be blocked by the very gating §6 adds. Every action maps onto the same
code path the corresponding key runs.

```csharp
// game/testing/ScriptedClient.cs  (sketch)
public sealed class ScriptedClient
{
    public ClientWorldSession Session { get; private set; }
    public NetWorldClient Net => Session.Client;
    public PlayerController Shell => Session.Shell;      // null until the join snapshot seeds the spawn

    public static ScriptedClient Join(Node3D world, WorldBuildResult built, MemNetwork net, string name)
    {
        var sc = new ScriptedClient();
        sc.Session = new ClientWorldSession {
            Driver = built.Sim, TransportOverride = new MemClientTransport(net),
            PlayerName = name, ScriptedShell = true };    // NEW field, §5 seam S6
        world.AddChild(sc.Session);                       // _Ready connects + registers its sim steps
        return sc;
    }

    // ---- readiness (yield helpers -- they return the harness's own Step) ----
    public bool Spawned => Shell != null && GodotObject.IsInstanceValid(Shell);
    public Step UntilSpawned(double s = 5) => Step.Until(() => Spawned, s);
    public Step UntilRiding(double s = 5)  => Step.Until(() => IsRiding, s);

    // ---- ACTIONS (held-state setters + momentary calls) ----
    public void Walk(float strafe, float forward) => Shell.ScriptedInput = new UnityEngine.Vector2(strafe, forward);
    public void Stop()                        { Shell.ScriptedInput = UnityEngine.Vector2.zero; Drive(0, 0, false); }
    public void Stance(EPlayerStance? s)      => Shell.ScriptedStance = s;
    public void Jump(bool held)               => Shell.ScriptedJump = held ? true : (bool?)null;
    public void LookYawPitch(float yawDeg, float pitchDeg) => Shell.ScriptLook(yawDeg, pitchDeg);   // NEW seam S3
    public void LookAt(Vector3 worldPoint)    { /* yaw/pitch from the eye (TruePhysicsPosition + 1.6 up) toward the point -> ScriptLook */ }
    public bool Interact()                    => Shell.Interact();                                   // NEW seam S4: the F chain
    public void Drive(float throttle, float steer, bool handbrake = false)
    { Shell.ScriptedDrive = new Vector2(steer, throttle); Shell.ScriptedHandbrake = handbrake; }     // NEW seam S2
    public bool EnterNearestVehicle()         => Shell.RequestEnterNearestPuppet();
    public bool ExitVehicle()                 => Shell.RequestExitPuppet();
    public bool Fire()                        => Shell.Fire();       // per-tick calls = holding the trigger
    public void Reload()                      => Shell.StartReload();
    public void EquipHotbar(int n)            => Shell.EquipHotbar(n);
    public bool Console(string cmd)           => Net.SendConsole(cmd);   // NOT via DevConsole (static RemoteClient, §10 risk 5)
    public void Disconnect()                  => Net.Disconnect();

    // ---- STATE READS ----
    public Vector3 Pos                 => Shell.TruePhysicsPosition;
    public bool    IsRiding            => Spawned && Shell.IsRiding;
    public uint    RidingNetId         => Session.RidingVehicle;
    public VehiclePuppet RiddenPuppet  => Shell.RidingPuppet;                        // the driver's OWN puppet
    public bool TryGetVehiclePuppet(uint id, out VehiclePuppet p) => Session.VehicleView.TryGetPuppet(id, out p);
    public Transform3D CameraTransform => Shell.Camera.GlobalTransform;              // what the player SEES
    public WorldItem       FocusItem    => Shell.FocusItem;                          // NEW accessors S5
    public Vehicle         FocusVehicle => Shell.FocusVehicle;
    public IPuppetFocusable FocusPuppet => Shell.FocusPuppet;
    public float Health                => Shell.Health;
    public SDG.Unturned.PlayerInventory Inventory => Shell.Inventory;
    public bool TryGetOwnEntity(out PlayerReplication.PlayerEntity e) => Net.Players.TryGetByOwner(Net.PlayerId, out e);
}
```

Notes on scope decisions:

- **"Press E"** is `Interact()` — the extracted F-key chain (the project moved interact to F,
  `PlayerController.cs:1805`), so one call covers exit-seat / hitch / pickup / SP-enter /
  puppet-enter / generator-toggle / harvest / crate / inspect with the SAME priority order a human
  gets. `EnterNearestVehicle()` stays available for tests that want the narrow seam (what
  `net.shell_drive:1780` uses).
- **Fire-hold** needs no new seam: semi/auto cadence is already enforced by `_fireCd`
  (`PlayerController.cs:2027`); calling `Fire()` every tick IS holding the trigger, and
  `net.shell_fire_zombie:1919-1927` already validates the pattern. The polled auto-fire path
  (:2697) stays untouched.
- **Melee/grenade**: `MeleeAttack()`/`ThrowGrenade()` already route through `NetMelee`/`NetGrenade`
  when wired (:1156-1157); exposing them on `ScriptedClient` is a one-line passthrough each —
  included in Phase 2, not v1.
- **MP item pickup cannot be scripted yet because it does not exist**: the wire command is minted
  (`SendPickupItem`, `core/UnturnedNet/NetWorldHost.cs:506-507`; `CommandPickupItem = 14`,
  `core/UnturnedNet/PlayerReplication.cs:45`) but nothing in `game/` calls it, and the F chain has
  no replicated-item branch (:1805-1818 — `TryPickup` at :1810 only handles the SP `WorldItem`).
  The harness makes this visible: the day `SendPickupItem` is wired into `Interact()`, the Phase-2
  pickup test flips from "expected-absent" to a real regression guard.

---

## 4. L1 wiring: two ends in one process, and why the order is what it is

The harness reuses the exact `net.shell_drive` topology (all under the per-test sandbox `World`):

```
World (per-test sandbox, TestHost.cs:75)
├── [WorldBuilder output]  terrain fallback, SimDriver spine (world.Sim)
├── ClientWorldSession     (the scripted client -- adds net.client.pump + client.shell to world.Sim)
│   ├── PlayerController   (the shell, spawned by ShellStep on the first own-entity sample)
│   ├── VehicleReplicaView / RemotePlayers / ZombiePuppets / ...  (the real replica views)
├── [observer NetWorldClient pumps via DelegateSimStep, optional VehicleReplicaView]
├── DedicatedServer        (added LAST -- its _Ready registers server receive/sim/publish steps)
└── [server-side world nodes the test authors: Vehicle jeep, ZombieController, ...]
```

**Sim-step order per 50 Hz tick** (registration order on `world.Sim.Sim`; every existing Net test
carries the comment "registered BEFORE DedicatedServer → server sim + replicate stay after/LAST
(§2.5)", e.g. NetTests.cs:1726, :1734):

1. `l1.netpump` — `MemNetwork.Tick()`: datagram delivery (added first by `NetRig.Boot`).
2. `net.client.pump` — `Client.Tick()`: receive, apply snapshots, ack (`ClientWorldSession.cs:143`).
3. `client.shell` — `ShellStep`: consume correction, capture/send input, stream drive intent, engage
   the seat (`ClientWorldSession.cs:144`).
4. Observer pumps, in AddChild order.
5. `DedicatedServer`'s steps: receive → dispatch commands (the validation choke point) → player sim →
   vehicle sync (`game/VehicleNetSync.cs:106-107` feeds the held DriveInput into `Vehicle.Drive` —
   the same seam the SP shell uses) → … → replication LAST.

The shell NODE's `_PhysicsProcess` (walk physics, `RidePuppet`) runs on the scene-tree plane the same
tick — `SimDriver` sits before the session in the tree so corrections land before `MoveAndSlide`
(`ClientWorldSession.cs:22-25`, :139-142). The TestHost coroutine advances between ticks, so a test
that samples once per `Ticks(1)` observes a consistent post-tick world.

**Frame plane vs tick plane.** `VehicleReplicaView._Process` (puppet glide) and
`PlayerController._Process` (ride cam, look focus) run on frames, which headless Godot interleaves
with physics ticks at an unfixed ratio. Rules for harness tests, lifted from the passing tests:
sample per tick (the `maxStep` loop shape, NetTests.cs:585-592), assert displacement and bounds —
never exact frame counts or per-frame values. Look-focus reads need ≥1 frame after `LookYawPitch`
before `FocusItem` is meaningful (the camera basis is applied in `_Process`, :2481-2482); in
practice "yield one tick, then read" suffices and the worked example does exactly that.

---

## 5. Seams: reuse vs add

**Reuse (no changes):** everything in the §2.2 table.

**Add — five small seams + one session field.** All default-off, all SP-byte-identical (the
`NetEnterVehicle`-pattern argument: `ClientWorldSession` is the only writer, SP never sets them).

| # | Seam | Exact location | Change |
|---|---|---|---|
| S1 | `PlayerController.ScriptedInputOnly` (bool, default false) | new field beside `NetAvatar` (~:1250); consumed at :1733 and :2593 and :140 | (a) `_UnhandledInput` top guard becomes `if (NetAvatar \|\| ScriptedInputOnly) return;` (:1733) — kills EVERY event-driven key/mouse path, including the F/R/V/1-9 branches that have no `UiInputBlocked` gate (:1783-1835). (b) `UiInputBlocked => ScriptedInputOnly \|\| Input.MouseMode != Captured` (:2593) — kills every POLLED path (auto-fire :2697, stance keys :2704-2706, walk/jump fallbacks :2711-2718, ride keys :1236-1240, SP drive keys :2603-2606); the `Scripted*` seams already take precedence before the `UiInputBlocked` branch in every consumer, so scripted control is unaffected. (c) the look-focus gate (:140) becomes `(ScriptedInputOnly \|\| Input.MouseMode == Captured)` so a scripted client HAS look focus headless — which the MouseMode gate currently makes impossible (§1 point 3). |
| S2 | `PlayerController.ScriptedHandbrake` (bool?, default null) | beside `ScriptedDrive` (:2585); consumed at :1240 and :2606 | `LastHandbrakeInput = ScriptedHandbrake ?? (!UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Space));` — the `ScriptedJump` idiom (:2718). Today handbrake is UNscriptable: `ScriptedDrive` is steer/throttle only and the poll is dead headless (`UiInputBlocked` true ⇒ always false). |
| S3 | `PlayerController.ScriptLook(float yawDeg, float pitchDeg)` | beside `ScriptedStance` (~:1132) | Sets `RotationDegrees.Y` and `_pitchDeg` (clamped ±89, the :2462 clamp). This drives BOTH the fire aim (`Fire()` builds its axis from `Rotation.Y` + `_pitchDeg`, :2051-2052) and, one frame later, the camera (:2481-2482) and thus `UpdateLookFocus`'s eye ray (:143-144). `net.shell_fire_zombie` currently pokes `RotationDegrees` raw (:1924) — this is the same thing with pitch and a name. |
| S4 | `PlayerController.Interact()` (public bool) | extract the F-branch body :1806-1817 verbatim; the key case (:1805) calls it | Pure refactor; returns true if any branch consumed the press (false ⇒ the inspect fallback ran). Plus the MP-shell guard from §6.3: the SP `_focusItem`/`_focusVehicle` branches (:1810-1811) gain `&& NetEnterVehicle == null` so an MP shell can never SP-enter or SP-pickup a server-plane node. |
| S5 | Focus read accessors | beside the fields (:119-122) | `public WorldItem FocusItem => _focusItem;` `public Vehicle FocusVehicle => _focusVehicle;` `public IPuppetFocusable FocusPuppet => _focusPuppet;` (the `WireLookPort` precedent, :267). |
| S6 | `ClientWorldSession.ScriptedShell` (bool, default false) | new field; consumed in `SpawnShell` (:206) | When true, spawn the shell with `CaptureMouse = false` (never grab a real mouse on a windowed dev host — `_Ready` captures at `PlayerController.cs:1723`) and `ScriptedInputOnly = true`. The `UG_MPWALK` hook (:229-230) stays as-is. |

No new seams for: fire (reuse `Fire()`), enter/exit (reuse `Request*Puppet`), reload/equip (public),
console (reuse `SendConsole` — `NetDeployWirePower` pattern, NetTests.cs:212-213), teleport-style
setup (server-side `ServerTeleport`, NetTests.cs:462).

---

## 6. Headless correctness: the input-gating argument

Three properties must hold simultaneously:

1. **SP and every existing test stay byte-identical.** All six seams are default-off fields read at
   already-existing branch points; with defaults, every touched expression evaluates exactly as
   before (`ScriptedInputOnly=false` reduces S1's three edits to the current code; `null`
   `ScriptedHandbrake` falls through to the current poll; `Interact()` is the same statements the key
   handler ran). This is the same contract `NetAvatar` (:1250) and the `Net*` delegates (:1150-1158
   "null default … keeps SP/loopback byte-identical") already rely on.
2. **A dev's real keyboard/mouse can never leak into a scripted client.** Today the scripted shell in
   `net.shell_drive` is only *accidentally* protected: headless, `Input.MouseMode` is never Captured,
   so `UiInputBlocked` is permanently true and the polls are dead — but run `--tests` on a windowed
   machine and (a) the event-driven keys (F, R, 1-9, V, H … :1783-1835) hit the scripted shell with
   no gate at all, and (b) if anything captures the mouse, the polls come alive. `NetAvatar` bodies
   are protected ("never reads global Input", :1244-1249, :2697, :2704) — the scripted CLIENT shell
   is not, because it must keep the whole client-side `_Process` (:2443 early-outs on NetAvatar).
   S1 closes exactly this: event plane at :1733, polled plane at :2593. That is the whole reason
   ScriptedInputOnly is a separate flag rather than `NetAvatar` reuse.
3. **Scripted input still works headless.** Every action either writes a `Scripted*` seam consumed
   before the `UiInputBlocked` branch (`ScriptedDrive` :1232/:2599, `ScriptedInput` :2711,
   `ScriptedJump` :2718, `ScriptedStance` via `_stance.Step` :2707) or calls a method directly
   (`Fire`, `Interact`, `Request*Puppet`) — none of them consult the DisplayServer. The one gate that
   DID consult it — look focus :140 — is exactly what S1(c) fixes.

---

## 7. Worked example: the drive bug, as the test that would have caught it

`game/testing/tests/NetRideViewTests.cs`, Phase-1 deliverable. The scenario VoX described: connect a
scripted client, spawn a car, press E, hold W ~4 s, assert what **the driver sees**.

```csharp
// The driver's-VIEW regression: server-drives-but-driver-sees-nothing must fail here.
// net.shell_drive (NetTests.cs:1709) covers the wire + observer + coarse shell-follow;
// THIS test pins the three reads that make up the driver's actual percept:
//   (1) the driver's OWN VehicleReplicaView puppet (Shell.RidingPuppet),
//   (2) the driver's CAMERA transform (Shell.Camera -- TopLevel'd in ride mode, frame-plane driven),
//   (3) their coupling (camera stays ON the vehicle, puppet moves by interpolation).
public class NetRideDriverView : GameTest
{
    public override string Name => "net.ride_driver_view";
    public override double TimeoutSimSeconds => 60;

    public override IEnumerable<Step> Run()
    {
        var rig = NetRig.Boot(World, seed: 20260717, playerName: "driver");
        T.Check("world ready", rig.World.Ready);
        var jeep = rig.ServerSpawnVehicle("jeep", new Vector3(0f, 1.2f, -10f));   // in front of spawn (shell faces -Z)

        yield return rig.Driver.UntilSpawned();
        yield return Until(() => rig.Driver.Net.Vehicles.Count == 1, 5);
        yield return Ticks(50);                                    // spawn-drop settle (the :1754 bar)

        // walk into interact range (≤4 m, NearestPuppet), then PRESS E -- the full F chain
        rig.Driver.Walk(0f, 1f);
        for (int i = 0; i < 400 && rig.Driver.Pos.DistanceTo(jeep.GlobalPosition) > 3f; i++)
            yield return Ticks(1);
        rig.Driver.Stop();
        yield return Ticks(25);                                    // last walk inputs ack (server reach gate)
        T.Check("press-E requested the seat (interact chain -> puppet branch)", rig.Driver.Interact());
        yield return rig.Driver.UntilRiding();
        T.Check("ride mode engaged on the driver's own puppet",
                rig.Driver.IsRiding && rig.Driver.RiddenPuppet != null);
        rig.Driver.Shell.DriveFP = true;                           // FP ride cam -> tight cam-on-vehicle bound

        // HOLD W for ~4 s of sim
        var pup = rig.Driver.RiddenPuppet;
        var pupStart = pup.GlobalPosition;
        var camStart = rig.Driver.CameraTransform.Origin;
        var jeepStart = jeep.GlobalPosition;
        rig.Driver.Drive(throttle: 1f, steer: 0f);
        float pupMaxStep = 0f, camGap = 0f; var prevPup = pupStart;
        for (int i = 0; i < 200; i++)
        {
            yield return Ticks(1);
            var p = pup.GlobalPosition;
            pupMaxStep = Mathf.Max(pupMaxStep, p.DistanceTo(prevPup));           // interpolation quality
            prevPup = p;
            if (i > 25) camGap = Mathf.Max(camGap, rig.Driver.CameraTransform.Origin.DistanceTo(p));
        }
        float srvDrove = jeep.GlobalPosition.DistanceTo(jeepStart);
        float pupDrove = pup.GlobalPosition.DistanceTo(pupStart);
        float camDrove = rig.Driver.CameraTransform.Origin.DistanceTo(camStart);

        T.Check($"server node drove ({srvDrove:0.0} m)", srvDrove > 8f);          // the precondition, already covered elsewhere
        // ---- THE DECISIVE ASSERTIONS (all three absent from every existing test) ----
        T.Check($"(1) the DRIVER'S OWN puppet advanced ({pupDrove:0.0} m)", pupDrove > 8f);
        T.Check($"(2) the DRIVER'S CAMERA advanced ({camDrove:0.0} m)", camDrove > 8f);
        T.Check($"(3a) the camera stayed on the vehicle (max FP gap {camGap:0.0} m)", camGap < 5f);
        T.Check($"(3b) the puppet moved by interpolation (max per-tick step {pupMaxStep:0.00} m)",
                pupMaxStep > 0f && pupMaxStep < 1.5f);
        T.Check($"the shell rode along (exit anchor) ({rig.Driver.Pos.DistanceTo(jeep.GlobalPosition):0.0} m)",
                rig.Driver.Pos.DistanceTo(jeep.GlobalPosition) < 6f);             // keep the :1801 bar too

        // 3P pass: the orbit cam must ALSO track (chase dist = clamp(size*1.1, 6.5, 34), PlayerController.cs:2637)
        rig.Driver.Shell.DriveFP = false;
        var cam3pStart = rig.Driver.CameraTransform.Origin;
        yield return Ticks(100);
        T.Check($"(2') the 3P chase cam advanced too ({rig.Driver.CameraTransform.Origin.DistanceTo(cam3pStart):0.0} m)",
                rig.Driver.CameraTransform.Origin.DistanceTo(cam3pStart) > 4f);

        // exit view restore: shell visible beside the door, camera back on the shell's head
        rig.Driver.Stop();
        yield return Ticks(100);
        T.Check("exit requested", rig.Driver.ExitVehicle());
        yield return Until(() => !rig.Driver.IsRiding, 5);
        T.Check("shell re-appeared", rig.Driver.Shell.Visible);
        T.Check($"camera re-attached to the shell ({rig.Driver.CameraTransform.Origin.DistanceTo(rig.Driver.Pos + Vector3.Up * 1.6f):0.0} m)",
                rig.Driver.CameraTransform.Origin.DistanceTo(rig.Driver.Pos + Vector3.Up * 1.6f) < 1.5f);

        rig.Teardown();
    }
}
```

**Which read proves it:** assertion (1) reads `Shell.RidingPuppet.GlobalPosition` — the driver's own
`VehicleReplicaView` node, i.e. "the car the driver is sitting in, as their client renders it" — and
(2)/(3a) read `Shell.Camera.GlobalTransform` — the view itself, downstream of the frame-plane
`PositionRideCam` chain that nothing tests today. In the reported bug ("server accelerates to
~9.6 m/s, observer converges, driver sees nothing") at least one of (1)/(2)/(3a) is false whichever
layer broke: puppet frozen ⇒ (1); puppet fine but camera chain broken ⇒ (2)+(3a); puppet snapping
instead of gliding ⇒ (3b).

**Residual gap, stated plainly:** if `net.shell_drive` passes today AND this test passes on landing,
the live bug is in the plane L1 cannot reach — real PEI map (`WorldMode.Client`), real UDP, a real
window rendering `_Process` at display cadence, the launcher build. That is Phase 3's live-loopback
smoke (§9), and it is the honest reason the harness is phased rather than declared sufficient.

---

## 8. Reusable suite vs one-off repros

**(a) The permanent suite** — same discovery, naming, and gate as today (`TestHost` reflection;
`./test.sh` default = L0+L1, CLAUDE.md discipline). Phase-1/2 files under `game/testing/tests/`:

| Test | What it pins (client's-eye view) |
|---|---|
| `net.ride_driver_view` | §7 — enter by press-E, drive, driver's puppet + camera + exit restore |
| `net.ride_passenger_view` (later, when seats >1 replicate) | same reads from a non-driver seat |
| `net.look_focus_puppet` | `ScriptLook` at a replicated car ⇒ `FocusPuppet` set (outline path, `IPuppetFocusable`, `game/IPuppetFocusable.cs:11`); look away ⇒ cleared |
| `net.pickup_looked_item` (Phase 2, blocked) | look at a replicated item, `Interact()` ⇒ server inventory gains it — lands WITH the `SendPickupItem` wiring (§3.2) |
| `net.fire_from_look` | `ScriptLook` + per-tick `Fire()` ⇒ HitConfirmed on the looked-at target (upgrades `net.shell_fire_zombie`'s raw yaw poke :1924) |
| `net.teleport_view` | server `ServerTeleport` ⇒ shell snaps (Reconciler.Snaps>0) and CAMERA follows |

Existing tests are NOT rewritten; where a new test supersedes an ad-hoc rig, the old one keeps
running until the new one has soaked (regression rule: cheapest layer that expresses the bug).

**(b) The one-off shape** — the whole point of `NetRig`. A dev repro is one throwaway file:

```csharp
public class ReproDriveView : GameTest        // drop in game/testing/tests/, delete after
{
    public override string Name => "repro.drive_view";
    public override IEnumerable<Step> Run()
    {
        var rig = NetRig.Boot(World, 1);
        rig.ServerSpawnVehicle("jeep", new Vector3(0, 1.2f, -3f));
        yield return rig.Driver.UntilSpawned();
        yield return Ticks(60);
        rig.Driver.Interact();                                   // press E
        yield return rig.Driver.UntilRiding();
        var cam0 = rig.Driver.CameraTransform.Origin;
        rig.Driver.Drive(1f, 0f);                                 // hold W
        yield return Ticks(200);
        T.Check("driver's view moved", rig.Driver.CameraTransform.Origin.DistanceTo(cam0) > 8f);
        rig.Teardown();
    }
}
// run: ./test.sh --l1 --only repro.drive_view        (~15 s incl. build)
```

Ten lines of body, one command, printout with the repro line on failure (`TestHost.cs:104`).

---

## 9. Scope and phasing

**Phase 1 — the seams + the drive repro (the minimal slice):**
- S1-S6 (§5), `NetRig`, `ScriptedClient` (actions: walk/stop/stance/jump/look/interact/drive/
  enter/exit/fire/reload/console; reads: all of §3.2), `net.ride_driver_view`.
- Definition of done: `./test.sh` green including the new test; the new test demonstrably fails when
  the ride-cam frame chain is artificially broken (e.g. comment out :2448-2449 locally — a
  verify-the-test-can-fail check, not a committed change).

**Phase 2 — interaction + combat breadth:**
- `LookAt`, focus-driven tests, melee/grenade passthroughs, `net.fire_from_look`,
  `net.teleport_view`; wire `SendPickupItem` into `Interact()` (a feature change, reviewed
  separately) and land `net.pickup_looked_item` with it; decide multi-shell support (§10 risk 5) —
  until then: one `ScriptedClient` + N raw observers per test (the `net.shell_drive` shape).

**Phase 3 — the live-plane smoke (opt-in, NOT in the default gate):**
- A scripted director inside `ClientWorldSession` behind an env flag (`UG_MPDRIVE=1`, the `UG_MPWALK`
  precedent at :229-230): on spawn — walk to the nearest replicated vehicle, `Interact()`, hold
  throttle 5 s, log `[ride] tick=N cam=(x,y,z) pup=(x,y,z) veh=NetId` at 1 Hz.
- A `tools/` script boots the real two-process pair (`--dedicated` + xvfb/vulkan `--connect=127.0.0.1`
  per the CLAUDE.md render harness), greps the log for camera displacement, and (optionally) keeps
  the movie frames for eyeballing. This is the only layer that reproduces "what the human sees" on
  the real map/transport/renderer — where this specific live bug most likely lives if L1 stays green.

---

## 10. Risks and open questions

1. **S1 touches live gates.** Folding `ScriptedInputOnly` into `UiInputBlocked` (:2593) and the
   look gate (:140) risks a typo-level SP regression despite the default-off argument. Mitigation:
   the full existing suite is the gate (L0+L1 ~15 s), and the three edits are mechanically reviewable.
2. **Shared-sandbox cross-plane focus (real, found while planning).** In L1 the client's look ray and
   proximity queries run in the SERVER's physics world: `UpdateLookFocus`'s sphere can hit the
   server's real `Vehicle` (layer bit 5, :156-171) and `_focusItem` a server `WorldItem` — and the
   F chain would then SP-enter/SP-pickup a server-plane node (:1810-1811), silently bypassing the
   wire. On the LIVE client this cannot happen (a client world has no `Vehicle`/loot nodes —
   `ClientWorldSession.cs:158`, `net.client_world_mode` NetTests.cs:731-745), so the §5 S4 guard
   (`NetEnterVehicle == null` on the SP branches) both fixes the harness ambiguity and hardens the
   real client for free. Without that guard, `Interact()` in the sandbox is a foot-gun — the guard is
   therefore part of Phase 1, not optional.
3. **Frame-plane nondeterminism.** Ride cam and puppet glide advance per frame; headless frame:tick
   ratio is unfixed. All frame-plane assertions are displacement/bounds sampled per tick (§4); no
   exact-value or frame-count assertions. The existing observer-puppet checks prove the pattern is
   stable in CI.
4. **Ride-mode camera specifics.** 3P orbit depends on mouse-orbit state (`_driveCamYaw`, :26) — the
   worked example uses FP (`DriveFP`, :2586) for the tight bound and only a loose displacement check
   in 3P. Death/exit edge cases reset `TopLevel` in several places (:1220, :1581, :2581) — the exit
   restore assertion covers the ride-exit one; death-while-riding is out of v1 scope.
5. **One shell per test (v1 constraint).** Process-wide singletons make a second full shell
   undefined: `DevConsole.RemoteClient` last-writer-wins (`ClientWorldSession.cs:78`),
   `HitmarkerHUD.Instance` (:106). v1: one `ScriptedClient`, N raw `NetWorldClient` observers.
   Lifting this (per-session HUD/console scoping) is a Phase-2 decision, only if a test needs two
   full driver views at once.
6. **`Interact()` reach vs the real E/F path.** The extracted method covers the key-handler chain,
   but a human also generates the *events* (echo suppression, press/release edges — :1805 `Echo:
   false`). Scripted `Interact()` is one edge per call by construction; tests that need
   hold-repeat semantics (hitch toggle abuse) must call it per intended press. Documented in the
   class doc, not solved structurally.
7. **The harness can only prove the planes it runs.** L1 green + live bug still present ⇒ the bug is
   in map/transport/render (Phase 3's domain). The plan deliberately ships Phase 1 first anyway: the
   seams and the API are prerequisites for the Phase-3 director too, and the suite permanently closes
   the puppet/camera/interact class of regressions.
