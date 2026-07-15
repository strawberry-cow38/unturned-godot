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

# Core lib tests
dotnet test tests/UnturnedSim.Tests   # (likewise NetPak / UnturnedDat / UnturnedNet / NetTransport)
```

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

## Deploy / workflow

- Pushes go **direct to `main`** on `github.com/strawberry-cow38/unturned-godot` (private). `gh` is authenticated with write access.
- Contributors: **strawberry_cow** (game dev) and **catboy** (asset rips/handovers).
- Log meaningful progress/decisions in **PROGRESS.md** — it's the project's institutional memory.

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
