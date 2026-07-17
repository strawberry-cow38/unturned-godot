# CLAUDE.md — Unturned → Godot Port

## What this is

A from-scratch reimplementation of **Unturned** in **Godot 4.6 (mono/C#, net8.0, Jolt physics)**. The approach: port the readable U3-SDK game source faithfully into engine-agnostic C# libraries, and rip the retail Steam assets (meshes/textures/audio/stats) as swappable placeholder content keyed by their **original Unity GUIDs**. Gameplay data comes from the game's real `.dat` files parsed by the ported `UnturnedDat` layer.

Private project. Working log / decision history lives in **PROGRESS.md** — read it for the story behind any subsystem.

## Repo layout

```
game/       Godot 4.6 mono project (the actual game). All gameplay C# + game/content/ assets.
core/       Engine-agnostic ported libraries (build + test standalone, no Godot):
  SDG.Compat        UnityEngine shims (Mathf, Vector2/3/4, Quaternion, Color, Color32)
  SDG.NetPak        Netcode bit-packing (ported source, 46 tests)
  SDG.NetTransport  Real transport interfaces + a standalone UDP implementation (no Steam)
  UnturnedDat       The .dat data/modding layer (ported source, 1039 tests)
  UnturnedNet       NetServer/NetClient/PlayerState — server-authoritative sync over UDP
  UnturnedSim       SimClock/SimRoot 50 Hz tick spine, Layers.cs, PlayerMovementSim
tools/      ~90 Python (UnityPy) rip/extraction scripts — the asset pipeline
tests/      NUnit test projects for the core libs (1085+ green)
launcher/   Differential git-pull launcher (pull → dotnet build → godot --import → run)
```

## Major systems — where things live

- **Entry point / test harness**: `game/Main.cs` (~3300 lines). Parses all the `--` test-entry flags (`--vehicle=DIR`, `--gun=NAME`, `--peiplay`, `--peidrive`, `--demo`, `--shot=PNG`, `--catalog=M`, `--map=NAME`, `--nozombies`, `--night`, `--hitch`, …) and builds the corresponding scene.
- **Vehicles**: `game/Vehicle.cs` (~1700 lines). `VehicleBody3D` + `VehicleWheel3D` port of InteractableVehicle/VehicleAsset. Each vehicle is a static **Spec** struct + a `BuildXxx()` + entries in `BuildByName`/`SpecNames`. Paint via `vehicle_paint.gdshader` (palette-sampled, see Content format below). Damage/smoke/fire/explosion/husk lifecycle, per-wheel surface dust, taillights/headlights are all in here. `game/WheelDebris.cs` = detached wheels.
- **Content loading**: `game/ContentProvider.cs`. Maps original Unity GUIDs → ripped assets (manifest-driven), and parses the OBJ-ish `.txt` mesh format to `ArrayMesh` **at runtime** (`ContentProvider.ParseObj`). This is the swap seam: gameplay references assets by GUID; the provider resolves to whatever we have.
- **Player**: `game/PlayerController.cs` (~2000 lines) — CharacterBody3D driving the ported `PlayerMovementSim` (exact retail constants) on a 50 Hz physics tick; stances, guns (ballistics from real `.dat`s), melee, grenades, fall damage, vitals, vehicle enter/drive. `game/Viewmodel.cs` = first-person arms/gun (additive ADS layer, source-exact aim-hook alignment). `game/OrbitCam.cs` = third-person cam.
- **Sim spine**: `core/UnturnedSim/` (SimClock deterministic 50 Hz accumulator, SimRoot ticking ISimStepped systems, Layers.cs = the retail layer/raymask table) driven from Godot by `game/SimDriver.cs`.
- **Zombies**: `game/ZombieController.cs` (source-accurate sensing/flanking/specialities), `game/ZombieField.cs`, `game/ZombieNav.cs`, `game/HordeSpawner.cs`.
- **Characters/animation**: `game/RiggedCharacter.cs` (skeleton, clips, additive layers), `game/CharacterModel.cs`.
- **World**: `game/Terrain.cs`, `game/RoadField.cs`, `game/FoliageField.cs`, `game/ResourceField.cs`, `game/AnimalField.cs`, `game/DayNightCycle.cs`, `game/RainOverlay.cs`, `game/CropManager.cs`.
- **Networking demo nodes**: `game/ServerNode.cs`, `game/ClientNode.cs`, `game/NetDemoNode.cs` (cross-process `--server`/`--client` over real UDP).
- **UI**: `game/HUD.cs`, `game/inventory/` (ported 9-page grid inventory + dashboard), `game/MapUI.cs`, `game/PauseMenu.cs`, `game/DevConsole.cs`.
- **Gun data**: `game/GunDef.cs` reads real `ItemGunAsset` `.dat`s through the ported UnturnedDat accessors (damage/firerate/recoil/spread/firemodes/pellets).

## Build & test

```bash
# Build the game (C# only — content changes don't need this)
dotnet build game/UnturnedGodot.sln -c Debug

# Run the whole test suite (one command, one grep-able report, exits non-zero on failure)
./test.sh                       # default = L0 (engine-free unit tests); ~1s, ~1100 tests
./test.sh --only 'UnturnedSim*' # run one suite; ./test.sh --failfast; ./test.sh --help
```

`test.sh` prints `[SUITE] <name> | PASS/FAIL | ...` per suite then `[SUMMARY] TOTAL: P passed, F failed | first failure: <name>`; a failing suite lists the failed test names + a copy-pasteable `repro:` line, and names the *first* failure to debug. TRX artifacts land in `.testresults/`. Exit codes: 0 clean, 1 test failure, 2 infra/build failure. It's the layered runner from `docs/TESTING_PROPOSAL.md`: **L0** (engine-free `dotnet test`), **L1** (batched in-engine tests), and **L2** (visual goldens) are all LIVE. Default `./test.sh` runs L0+L1 (~15s); L2 is opt-in (`--visual` or `--all`, ~30s/scene) — it renders each `tests/visual/manifest.json` scene through the xvfb/lavapipe harness and diffs against `tests/visual/golden/<name>.png` (mean-abs-error tolerance per scene; particle scenes carry a looser one). **Do NOT run the L2 golden render tests by default** — they boot Godot fresh per scene (~6+ min for the full set) and only catch a change that alters RENDERING. Run `--visual` ONLY when a user asks for it, when you want a user's feedback on a visual, or when your change directly alters visual output and you want to confirm the goldens didn't move. For netcode / server / logic / test-only changes, `./test.sh` (L0+L1) is the gate — L2 can't regress on them, so running it is wasted time. On a visual failure look at the `<name>.diff.png` it drops in `.testresults/visual/`, and if the new look is INTENDED re-baseline with `python3 tools/visual_tests.py --update <name>` and commit the golden.

**Writing an L1 in-engine test** (`game/testing/tests/*.cs`): subclass `GameTest`, give it a dotted `Name` (e.g. `power.chain_passthrough`) and a `Run()` coroutine that builds nodes into `World`, `yield return Ticks(n)` / `Until(cond)` to advance physics, and `T.Check("desc", cond)`. The host (`game/testing/TestHost.cs`) discovers it by reflection, runs it simplest-first in ONE headless boot (`godot --headless -- --tests[=glob]`), and tears down + resets global statics between tests. Assert deterministic (Recompute-driven / sim-tick) state, not the wall-clock lamp ramp.

**Regression rule (Factorio-style):** every bug that reaches `main` ships a test that reproduces it, in the same commit as the fix, in the cheapest layer that can express it (engine-free logic → an L0 NUnit test under `tests/`; needs nodes/physics → an L1 test once that host exists). A fix without a guarding test is unfinished.

Godot binary on this box:

```
~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64
```

Note: some build outputs under `core/*/bin|obj` and `tests/*/bin` are tracked in git, so a build dirties `git status` — that's normal here.

## Headless render / self-verification — CRITICAL

**This box has no display.** To see anything (and you must verify visually — render, look at the PNG, judge it), use **xvfb + Vulkan (lavapipe) + Movie-Maker mode**:

```bash
GODOT=~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64
mkdir -p /tmp/o
VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.aarch64.json UG_QUICK=1 UG_VSIDE=2 \
  xvfb-run -a "$GODOT" --path game --rendering-driver vulkan \
  --write-movie /tmp/o/mov.avi --fixed-fps 30 -- --vehicle=/tmp/o --gun=eaglefire
```

- **Do NOT use `--headless`** — it's the dummy renderer and hangs silently, producing nothing.
- Frames land as `/tmp/o/rig_NN.png` (the dir you passed via `--vehicle=DIR`).
- `UG_QUICK=1` = capture one frame (frame 48) and quit — ~26 s round trip. Omit it for the full frame strip.
- Camera env flags: `UG_VSIDE` (3/4 view; `=2` starboard side), `UG_SIDE` (side profile), `UG_CAMDIST` (distance — `UG_SIDE` respects it, for framing long rigs like cab+trailer).
- Scene flags after `--`: `--vehicle=DIR --gun=NAME`, `--night` (light glow), `--hitch` (couple trailer), `--peiplay`, `--peidrive`, `--demo`, etc. — all parsed in `Main.cs`; grep there for the current set.

This is the standard loop for **any** visual change: edit → (rebuild if C# changed) → render → open the PNG → verify with your own eyes.

## Source data (not in the repo)

Ripped/retail data lives at `~/unturned-bundles/Bundles/`:

- **`core.masterbundle`** (117 MB Unity AssetBundle) — all 3D content: prefabs, meshes, textures, audio. Read it with **UnityPy** (the `tools/` scripts do). `UG_MASTERBUNDLE` env var overrides the bundle path.
- **`Vehicles/<Name>/<Name>.dat`** — vehicle stats. Files have a BOM: open with `encoding='utf-8-sig'`.
- **`Spawns/**/*.dat`** — spawn tables.
- Drivable vehicle prefab: `vehicles/<name>/vehicle.prefab` inside core.masterbundle.
- Static wreck/prop version: `objects/large/vehicles/<name>_<color>/object.prefab` — **a different asset**; don't confuse the two.

## Rip pipeline & content format

`tools/*.py` (UnityPy-based) rip meshes, textures, audio, and stats out of the bundles. Key scripts: `unity_mesh_to_obj.py` (Unity YAML mesh → OBJ, handles handedness Z-flip + winding reverse, byte-validated vs Unity's localAABB), `batch_convert.py`, `build_texture_map.py`, `extract_vehicle_mesh.py`, `mb_probe.py`/`mb_dump.py`/`mb_export.py` (bundle exploration), `bake_zombie_atlas.py`, `extract_zombie_sounds.py`.

Output lands in **`game/content/`**:

- **Meshes**: `*.txt` in an OBJ-ish format (`v`/`vt`/`vn`/`f`), loaded **at runtime** by `ContentProvider.ParseObj` → `ArrayMesh`. Because loading is runtime, **mesh/texture edits need no rebuild** — just re-render.
- **Palettes**: tiny 4×2 PNGs (or full 256² for curated vehicles), e.g. `ambulance_palette.png`.
- **Paint shader**: `vehicle_paint.gdshader` samples the palette by UV; `ALBEDO = texel.a < 0.5 ? paint_color : texel.rgb`. An **alpha-0 texel marks a paintable region** (gets the vehicle's `_PaintColor` from the .dat); opaque texels are fixed colors.
- Other content: gun `.dat`s + albedos + `.ogg` audio, item textures, etc. — same naming pattern (`<name>_albedo.png`, `<name>_gun.txt`, `<name>_shoot.ogg`).

## Adding a new vehicle, end-to-end

1. **Rip the mesh**: `tools/extract_vehicle_mesh.py` against `vehicles/<name>/vehicle.prefab` in core.masterbundle. The prefab carries a Unity LODGroup: **`Model_0` = LOD0 (full detail — use this)**, `Model_1` = LOD1 low-poly. The port uses LOD0 only, no LOD swapping.
2. **Palette**: produce/copy the palette PNG into `game/content/` (paintable regions = alpha-0 texels).
3. **Stats**: read `~/unturned-bundles/Bundles/Vehicles/<Name>/<Name>.dat` (utf-8-sig!) — `Speed_Max`/`Speed_Min` are directly usable m/s; `Steer_Max`/`Steer_Min` degrees; `Brake`. Engine force is a calibrated feel value (Unity WheelCollider torque does not map 1:1 to Godot), and brake goes through `FootBrakeScale`/`HandbrakeScale` calibration in `Vehicle.cs`.
4. **Spec**: add a static Spec struct in `game/Vehicle.cs` (mesh/palette/wheel positions/lights/parts/stats), a `BuildXxx()`, and register in `BuildByName` + `SpecNames`.
5. **Verify visually**: rebuild (`dotnet build game/UnturnedGodot.sln`), then the headless render harness with `--vehicle=/tmp/o` and eyeball the PNGs (3/4 via `UG_VSIDE`, profile via `UG_SIDE`, `--night` for light glow).

**Vehicle gotchas:**
- `VehicleWheel3D` **auto-rolls its own node** (the mesh child inherits it) — never manually rotate the wheel mesh, or it double-spins.
- Drivable `vehicle.prefab` ≠ static wreck `object.prefab` — rip the right one.
- Content `.txt`/PNG changes are picked up at runtime; only C# changes need a rebuild.

## Multiplayer (MP)

All 8 MP_PLAN phases are live; `docs/MP_PLAN.md` is the architecture doc (§7 = current status + security
posture). Working reference:

- **Architecture, bottom-up:** dumb datagram transports (`core/SDG.NetTransport/` — real UDP + the
  deterministic `MemTransport` test hub) → per-peer reliability engine (`NetSession`: seq/ack header, 3
  channels: Control / ReliableOrdered / UnreliableSequenced, fragmentation, RTO retransmit) → session
  wrappers (`NetServerSession` handshake/keepalive/timeout + flood caps; `NetClientSession`) → the three
  replication planes: **snapshots** (`SnapshotComposer`/`SnapshotApplier` + `IReplicatedSystem`, per-client
  delta baselines, 25 Hz unreliable), **commands** (client→server, `CommandRegistry`, THE validation choke
  point — sender identity always from the connection, never the payload), **events** (server→client facts,
  `EventRegistry`, reliable). `NetWorldServer`/`NetWorldClient` (`NetWorldHost.cs`) wire it all together.
  **The server is authoritative for everything**; clients send intents and render replicas (local player =
  predict + smooth-correct).
- **Tick order (50 Hz):** `TickSimulation` (receive → dispatch commands → player sim → vehicles → combat)
  … game-side publishers (zombies/world items/vehicles/clock/crops/resources) … `TickReplication` LAST
  (join snapshots compose here too — after ALL mutation, never mid-tick).
- **Id registries** (append-only, never renumber): `ReplicationIds` in `core/UnturnedNet/PlayerReplication.cs`
  holds all three spaces — SystemIds 1-12 (players, player-combat, zombies, projectiles, skills,
  deployables, inventory, world-items, vehicles, world-clock, crops, resources) + 255 = sync-check block;
  CommandIds 0 (snapshot ack) + 1-25; EventIds 1-28. New wire ids get the next number and a comment.
- **Run it:** server `godot --headless --path game -- --dedicated` (env: `UG_UNTURNED_DIR` for the retail
  map, `UG_DEDICATED_NOCHEATS=1` to disable console cheats); client `godot --path game -- --connect=<host>`.
  Content hashes must match or the join is rejected. In-process demos: `--netdemo`, `--mploopback`.
- **Net diagnostics (find bugs):** `UG_NETLOG=1` (env) or `--netlog` — routes `NetLog` through
  GD.Print/GD.PrintErr (→ journald on the server). Logs connects/accepts/rejects (with endpoint + reason),
  kicks, per-command rejects (with sender + reason), and a 1 Hz `[NET] 1s:` rollup (pkts/bytes in-out,
  retransmits, cmd rejects, snapshot full/delta/bytes/skips, reassembly drops). OFF by default, zero
  overhead when off.
- **Desync detection:** the server appends a per-system StateHash block to one snapshot per second
  (`NetWorldServer.EnableSyncCheck`, on for `--dedicated`); clients compare after applying. A confirmed
  mismatch (2 consecutive checks) fires `NetWorldClient.DesyncDetected`, prints
  `[CLIENT] DESYNC DETECTED -- desync at server tick T: system N server hash X != client hash Y` and
  banners the player. `system N` = the diverged SystemId above — that's the system whose replication to go
  debug. Only globally-mirrored systems are checked (owner-only + relevancy-filtered ones differ per client
  by design).
- **Security posture (TEST SERVER — private, friendly):** console cheats ON by default
  (`UG_DEDICATED_NOCHEATS=1` to lock); simple flood caps (8 half-open total / 8 sessions per source IP,
  `ServerFull` reject beyond) + reliable-reassembly byte cap with kick; deployable ownership checks
  DEFERRED on purpose (co-op convenience — `TODO(mp-security)` in `ServerTransactions.cs`); no connect
  cookie/auth/encryption. Revisit all four before any public/untrusted hosting (MP_PLAN §7).

## Deploy / workflow

- Pushes go **direct to `main`** on `github.com/strawberry-cow38/unturned-godot` (private). `gh` is authenticated with write access.
- Contributors: **strawberry_cow** (game dev) and **catboy** (asset rips/handovers).
- Log meaningful progress/decisions in **PROGRESS.md** — it's the project's institutional memory.

### Dedicated multiplayer server (claw.bitvox.me)

The `--dedicated` flag boots a **headless** Godot dedicated server (no camera/HUD): the real world via
`WorldBuilder` (dedicated mode) + a `NetServerSession` over `UdpServerTransport` on **UDP 47872**, ticking the
sim at 50 Hz with replication registered last. Clients join with `--connect=<host>` (their content hash must
match the server's build). `docs/MP_PLAN.md` is the multiplayer architecture; phases 1-5 are live on `main`.

- **Map data** (retail, NOT in the repo): the server loads the real PEI map from `$UG_UNTURNED_DIR/Maps/PEI/`.
  On the claw box that's `/home/ec2-user/unturned/Maps/PEI/` (set `UG_UNTURNED_DIR=/home/ec2-user/unturned`).
  Map paths were normalized to forward slashes (valid on Windows too) so the Linux server resolves them —
  don't reintroduce `@"\..."` path literals in the map-loading code.
- **systemd (user units, live on claw)** — versioned in `deploy/systemd/`, installed at `~/.config/systemd/user/`:
  - `unturned-server.service` — runs `--dedicated` headless, `Restart=always`, `TimeoutStopSec=5` (Godot ignores
    SIGTERM, so a restart would otherwise block ~90s). `stdbuf -oL` streams the `[DEDICATED]` heartbeats to journald.
    `loginctl` linger is on, so it starts on boot. Logs: `journalctl --user -u unturned-server`.
  - `unturned-server-reload.path` + `.service` — the `.path` watches the built assembly
    (`game/.godot/mono/temp/bin/Debug/UnturnedGodot.dll`); a rebuild trips it and auto-restarts the server.
- **Deploy a new version**: `tools/deploy-server.sh` (git pull `--ff-only` + `dotnet build`). The rebuild rewrites
  the watched DLL → the `.path` unit bounces the server onto the fresh build. Never restart the unit by hand for a
  deploy — let the watcher own it. Install/refresh the units: `cp deploy/systemd/* ~/.config/systemd/user/ &&
  systemctl --user daemon-reload`.

## Gotchas & lessons (hard-won)

- **`--headless` renders nothing and hangs silently.** Always xvfb + `--rendering-driver vulkan` + `--write-movie` (movie mode forces the frame loop).
- **Unity → Godot handedness**: the converter Z-flips positions and reverses winding. Skinned meshes wind opposite the static convention — they need a double-sided material or they render inside-out.
- **Backface culling eats single-sided quads** facing away from camera (they're just invisible, not broken). When placing decal quads, check facing or set `CullMode.Disabled` while debugging.
- **Physics runs at 50 Hz** to match retail's fixed timestep (0.02 s) — don't change `project.godot` physics tick.
- **.dat files carry a BOM** — always `utf-8-sig` in Python.
- **Textures are shared atlases**; item/character texturing relies on the mesh's original UVs. Use nearest filtering for the low-res style (`game/NearestFilter.cs`).
- **Gun attachments are separate items**, not part of the gun mesh — e.g. the Eaglefire's iron sights are item 5 (`Eaglefire_Iron_Sights`), mounted at the gun's Sight hook. Hooks (Sight/Magazine/Muzzle/Eject/View/Aim) are transforms in the prefab, not geometry.
- **Unturned loot is ground items only** — there are no lootable world containers. Storage is a player-placed barricade.
- **Unturned characters are modular skinned meshes** (body + clothing parts on a shared skeleton), not a single humanoid mesh.
- Runtime asset loading is the shipping model — resist the urge to editor-import content.
- When a value or behavior is in question, **verify against the actual source/bundles** (the ported U3 code, the `.dat`s, UnityPy into core.masterbundle) — eyeballed constants keep getting caught. Render-verify visual results, don't just trust that the code compiled.
