# Wagon report

Implemented `wagon` as a command-spawnable road vehicle. In the F1 console, use `vehicle wagon` or `veh wagon`. The implementation follows the sedan's rig and extends its roof to a tailgate. Natural spawning follows the Golf convention: command-only.

**One requested constraint remains unmet:** the wagon is longer than the ambulance. The measured sedan is already **5.952190 m**, versus **5.353246 m** for the ambulance. Extending that sedan cannot also produce a body shorter than the ambulance. I retained the sedan baseline as the governing design assumption, asked for clarification, and documented the conflict instead of changing the measurements. No answer to that clarification was received before completing this work.

## Measurements completed first

[`vehicle_measurements.md`](vehicle_measurements.md) was generated before authoring any wagon assets. It parses the files' `v` records and the actual `Vehicle.cs` initializers, with no external dimensional references.

| Coverage | Result |
| --- | --- |
| Existing road specs | 20, including APC, tank, semi and trailer |
| Boats | Runabout and ship: complete requested measurements |
| Additional aircraft spec | Otter: complete requested measurements; its absent mass resolves to the 900 kg GlobalMass default |
| Original `*_body.txt` inventory | All 29 files have measured geometry and counts; this filename set includes train and the SKS firearm component |
| Road bodies with other filenames | `semi_0.txt`, `trailer_0.txt`, `tank_hull.txt` also measured |
| Explicitly skipped aircraft spec audits | Hind, Orca, Huey, Skycrane, Hummingbird, Minicopter, Fighter Jet; their body geometry/counts are included |
| Train | Body geometry/counts included; it belongs to Train.cs, not Vehicle.Spec, and is excluded from road/boat statistics |
| Statistics | Min/median/max length, width, height, wheelbase, per-axle track, mass, SpeedMax and km/h; separate road and road-plus-boat populations; wagon excluded from both baselines |

The tables include min/max coordinates, wheel anchors, radii and per-axle ride coordinates, main collider sizes/centres and extent ratios, capacities, seats, lights, Parts, wheel assets, palettes and glass references. They use forward −Z and +Y up. Mesh coordinates are already converted from Unity; no additional Z negation was applied.

The sanity checks found:

- **15 baseline vehicles** have wheel anchors outside the body AABB in X/Z: jeep, sedan, hatchback, humvee, roadster, ambulance, firetruck, tractor, ural, police, offroader, truck, van, golf, APC. The measurement table gives every offending wheel, axis and distance. The wagon preserves the sedan's X overrun: 0.038953 m on the left and 0.038951 m on the right. All wagon anchor Z coordinates are inside its body bounds.
- **All 23 fully audited baseline specs** fail main-box enclosure of the complete body mesh at a 0.000010 m tolerance. The wagon does too. This is a test of BoxCenter ± BoxSize/2, not a claim that all runtime collision is absent: cars can add roof shapes, and the ship replaces its main box with other hull shapes. The wagon preserves the sedan's lower box profile, extends its rear face by 0.250000 m, and adds a roof slab matching the new roof.
- Exact seat-table equality: **sedan/police**, **jeep/offroader**, and **huey/hummingbird**. The wagon explicitly inherits sedan seats. Other shared groups include sedan/Golf wheel anchors; jeep/ambulance/humvee/offroader/truck/van wheel anchors; sedan/police/roadster main boxes; jeep/Golf/offroader/truck/van main boxes; and matching jeep/offroader/truck/van body AABBs within exporter rounding. Full numeric groups, including lights and drive/capacity fields, are in the notes. These equalities are reported, not assumed to prove a defect.

## Final wagon and fleet comparison

| Metric | Sedan | Wagon | Measured fleet reference |
| --- | --- | --- | --- |
| Body length Z, m | 5.952190 | **6.202190** | Road median 5.735214; ambulance 5.353246; Ural 6.610358 |
| Body width X, m | 2.522096 | **2.522096** | Road median 2.522099; ambulance 2.561922 |
| Body height Y, m | 2.433513 | **2.433513** | Road median 2.446013; ambulance 2.648431 |
| Wheelbase, m | 3.000000 | **3.000000** | Road median 2.952000 |
| Front/rear track, m | 2.600000 / 2.600000 | **2.600000 / 2.600000** | Per-axle road median 2.600000 |
| WheelRadius, m | 0.600000 | **0.600000** | Sedan wheel mesh and texture retained |
| Anchor Y − radius, m | −0.350000 | **−0.350000** | Excludes suspension deflection and the 0.250000 m rest drop |
| Front/rear overhang, m | 1.389812 / 1.562378 | **1.389812 / 1.812378** | AABB endpoints to front/rear axles; rear includes exhaust |
| Mass, kg | 1500 | **1650** | Midpoint of sedan 1500 and police 1800; road median 2350 |
| Spec.Health | 600 | **600** | Both become HealthMax 6000 through VehicleHealthScale=10 |
| Fuel, mL / L | 50000 / 50 | **50000 / 50** | Sedan capacity retained |
| SpeedMax, m/s / km/h | 16.5 / 59.4 | **16.5 / 59.4** | Road median 14.0 / 50.4; configured values, not a driving measurement |
| Engine | 700 | **700** | Sedan coefficient retained; not horsepower |
| Seat count | 4 | **4** | Same sedan seat positions and visible interior |
| Body OBJ vertices / triangles | 515 / 366 | **238 / 409** | Ambulance 642 / 436; Ural 470 / 288 |

Body AABB is **X [−1.261047, 1.261049], Y [−0.273431, 2.160082], Z [−3.009812, 3.192378] m**. The body has 1.117486 times the sedan's triangle count and 0.462136 times its OBJ vertex-record count. The generator deduplicates position records while keeping UV and normal indices separate. The eight panes add 32 vertices / 16 triangles; the translated taillight mesh has 16 vertices / 20 triangles.

[`wagon_dimensions.md`](wagon_dimensions.md), also embedded before the fleet statistics in the measurement notes, gives every construction dimension and its source. The extension is the sedan roof-edge/eave difference, 2.125000 − 1.875000 = 0.250000 m. The new roof is flat at the sedan's maximum Y=2.160082. Its underside is Y=1.910082. Pillar thickness, glass boundaries, belt rails, handle and tailgate all derive from listed sedan vertex planes, wheel coordinates or that thickness. The generated roof collider is size **(2.461926, 0.250000, 3.967364)** at centre **(0.000000, 2.035082, 1.022514)**.

## Spec field justification

| Fields | Wagon values / origin |
| --- | --- |
| `Body`, `Palette` | `wagon_body.txt`; `wagon_palette.png` is a byte-for-byte copy of `sedan_palette.png`. PaintMat consumes this 4×2 RGBA palette; alpha below 0.5 selects spawn paint. New painted faces use OBJ UV (0.125,0.750), loading as (0.125,0.250), the centre of the paint texel. Handle UV (0.375,0.250) loads as (0.375,0.750), selecting the fixed dark texel. |
| `Wheel`, `WheelTex` | `sedan_wheel.txt`, `jeep_wheel_albedo.png`, exactly the sedan's references; no new wheel asset. |
| `GlassMesh`, `GlassTint` | Prefix `wagon_glass.txt`; eight per-pane files resolved by existing labels. RGBA (0.62,0.73,0.78,0.26), copied from sedan. An aggregate file is unnecessary because AttachGlass loads the pane files first. |
| `Water`, `RandomHueGray` | `WaterMode.Car` explicitly preserves the sedan's default car mode; `true` preserves its spawn-paint selection. |
| `Mass` | 1650 = (sedan 1500 + police 1800)/2. These baseline vehicles share length, width and seat positions. This is an authored mass choice, not a claim to derive physical mass from the mesh volume. |
| `WheelRadius`, `Wheels` | Radius 0.6; anchors (±1.30,0.25,−1.62), (±1.30,0.25,1.38); front steer true, rear false. Exact sedan inheritance. |
| `Engine`, `SteerMax`, `SteerMin`, `SpeedMax`, `SpeedMin`, `Brake` | 700, 28, 14, 16.5, −6, 32; exact sedan values. Their behavior with the changed mass is not road-tested. |
| `BoxSize`, `BoxCenter` | (2.5,0.916,5.906), (0,0.548,0.062): sedan length +0.25 and centre Z +0.125; front box face unchanged. Main-box mesh shortfalls remain listed in the measurements. |
| `ForwardGears`, `ReverseGear`, `ShiftUpRpm` | [14,8.75], 5, 5000; exact sedan values. |
| `Sound`, `Horn` | `engine_medium.ogg`, `carhorn_02.ogg`; sedan assets. |
| `IdlePitch`, `MaxPitch`, `IdleVolume`, `MaxVolume` | 1.0, 2.0, 0.75, 1.0; exact sedan values. |
| `Fuel`, `Health`, `Rarity`, `Name` | 50000 mL, 600, COMMON, `Station Wagon`; capacities and rarity match the sedan (whose omitted rarity defaults to COMMON). |
| `SpotPos`, `OmniPos` | Spots (±0.765,0.708,−2.969); omni (0,0.841,−2.945); exact sedan values and reused front lenses. |
| `TailPos` | (±0.979,0.688,3.091): sedan Z=2.841 +0.250; tail mesh vertices receive the same translation. |
| `SteerPivot`, `SteerAxis` | (−0.464,0.894,−1.416), (0,0.259,0.966); exact sedan values for the unchanged steering mesh. |
| `Parts` | `sedan_seats.txt` RGB (0.25,0.25,0.25); `sedan_steer.txt` (0.28,0.23,0.14); `sedan_headlights.txt` (0.94,0.89,0.73); translated `wagon_taillights.txt` (0.56,0.13,0.13). All colours copied from sedan. |
| Seat table / body pose | Four seats at (±0.5,−0.079,−0.625), (±0.5,−0.079,0.772). SeatOf and HandTunedSeatOf preserve the sedan driver body offset (−0.5,−0.04,−0.566), also used by replicas. |
| Other optional Spec fields | Retain C# defaults, as on the sedan: no per-wheel radius override, aircraft/tank/boat machinery, extra seat override or towing hardware. Shared runtime car settings remain shared. |

## Registration audit

The search included `golf`, `sedan`, `SpecNames`, `BuildByName`, `SpecFor`, `RoofBox`, `SeatTable`, `SeatOf`, `HandTunedSeatOf`, and network `TypeId` consumers.

| Path | Registration / behavior |
| --- | --- |
| `Vehicle._wagon`, `BuildWagon` | Full spec and direct factory; SpecKey=`wagon`. |
| `Vehicle.BuildByName` | `wagon` calls BuildWagon; no jeep fallback for this key. |
| `Vehicle.SpecNames` | Appended `wagon` at TypeId 32; all previous 32 indices retained. |
| `Vehicle.SpecFor` | `wagon` resolves to `_wagon` for MP puppets and GetBodyBox. |
| `SeatTable`, `SeatOf`, `HandTunedSeatOf` | Four sedan seats and sedan body pose, consistently on server and replica. |
| `RoofBox` | Station Wagon roof slab, also enabling HasCabin. |
| `GlassPaneLabels` | Uses existing windshield, rear, l/r_front, l/r_rear and l/r_mid1 labels; no renumbering. |
| `DevConsole` | Its existing SpecNames validation/autocomplete and BuildByName call automatically expose `vehicle wagon` / `veh wagon`; no separate literal vehicle list. |
| `VehicleNetSync`, `VehicleReplicaView` | Existing string-key to SpecNames-index serialization and reverse lookup consume the new catalogue entry. |
| `WorldBuilder` | Golf and wagon remain outside the natural civilian pool; comment and spawn diagnostic now state both names. |

Historical references to Golf's traction tuning, the golf-club item, extracted retail seat data and unrelated map descriptions do not register vehicle types and were not edited.

## Verification and limits

| Check | Method / observed result |
| --- | --- |
| Required C# build | `dotnet build game/*.csproj`: **exit 0, 0 errors, 22 warnings**. Final build includes VehicleWagonTests. |
| Loader-compatible mesh validation | `python3 tools/verify_wagon.py`: **PASS**. Re-parses saved output using float32 coordinates, positive one-based indices, first-three-corner face handling and V→1−V. Separately rejects every face with other than three corners, missing UVs/normals, out-of-range indices, non-finite coordinates and zero-area triangles. Checks unit normals and agreement with winding. |
| Degenerate check result | All 10 authored OBJ assets passed. Minimum body triangle area after float32 loading: **0.000000148463207637 m²**, greater than zero. |
| Claimed dimensions | Float32-loaded body AABB agrees with sedan bounds plus the lamp/rear extension within 0.000001 m on each bound. Decimal source-file bounds are printed in the measurement tables. |
| Rig, parts and paint | Verifier checks exact sedan wheel/texture/seat/drive inheritance, each light delta, the lower collider's fixed front face, roof height, byte-identical palettes, referenced file existence and all eight pane labels. |
| Reproducible output | Re-ran both generators and compared SHA-256 digests: all **13 generated asset/note files** were byte-identical (11 wagon assets plus measurements and dimension notes). |
| Registration / network IDs | Static verifier checks the factory, both lookup paths, catalogue, driver pose, roof and glass labels; compares existing SpecNames order against Git's pre-wagon version. |
| Runtime regression test | **PASS: 1 test, 13 checks, 1.01 s**, executed in Godot 4.6.stable.mono.official.89cea1439 using `--path game --headless -- --tests=vehicle.wagon`. Checks real and puppet builders, exact loaded body resource, cabin/roof, seats, wheels, glass, capacities and dimensions. The engine is absent from PATH but is available at the test runner's default path. Reproduce through `./test.sh --l1 --only 'vehicle.wagon'`. |
| Runtime collision assembly | Godot generated **10 convex hulls** from the wagon's **409 triangles**, then disabled **2 fitted boxes** for physics while keeping them for look-focus. Thus the BoxSize/RoofBox tables describe the configured boxes, not the complete active physics decomposition. Motion/contact quality remains untested. |
| Geometry inspection | `python3 tools/preview_wagon.py`, then opened and inspected [`wagon_geometry.png`](wagon_geometry.png): sedan comparison, front quarter, side and rear quarter. This is a Matplotlib OBJ preview with approximate palette/lighting and painter sorting. It establishes a view of the authored geometry, not in-game appearance. |
| Godot rendering | **Captured and inspected** [`wagon_godot.png`](wagon_godot.png), 1280×720, frame 48 in the existing vehicle harness. Godot 4.6, Vulkan Forward+, llvmpipe, Xvfb, movie mode at 30 FPS; `--gun=wagon`, UG_VSTATIC=1 and camera `−7,3,7;0,1,0`. The PNG was checked for successful decoding and nonuniform pixels, then opened for visual inspection. Body, rear extension, roof, wheels, rear lenses and cabin apertures are present. |
| Render caveats | The temporary shot wrapper initially looked for `wagon.png`, while the vehicle harness writes `rig_00.png`, and therefore reported a missing image despite the engine exiting 0. The actual frame was found, checked and copied without modification. The engine log includes missing `rain_intensity` / `rain_wetness` shader-global warnings, rendering synchronization warnings, and a render-thread `finalize` error plus ObjectDB leak warning at shutdown. The captured frame is useful evidence of assembly/geometry, not proof of correct weather shading or error-free renderer shutdown. |
| Gameplay limits | **Not verified:** I did not interactively execute the console command, drive the wagon, test suspension/collisions over a route, test nighttime beam placement, pane damage, occupants in-game, or a multiplayer session. The stationary harness let the vehicle settle on its ground plane; the assembly test exercised the same factory as console spawning and also constructed the MP puppet locally. These checks do not establish driving behavior or live network parity. |
| Shared MP visual limitation | Source inspection shows the existing BuildPuppetByName path does not attach GlassMesh panes or RoofBox shapes. The wagon uses that existing fleet path. Its body/parts/wheel lookup is registered correctly; glass and roof parity in a live MP client remain unverified and the shared omission is not fixed here. |
| Other existing lookup omissions | The existing SpecFor switch also lacks ship/Otter/Fighter Jet entries despite their BuildByName entries. These predate the wagon; this task adds the wagon entry without changing those vehicles. |
| Ambulance ceiling | **Unmet**, for the measured contradiction explained above. The wagon is 0.848944 m longer than the ambulance and 0.408168 m shorter than the Ural. |

Reproduction order: `python3 tools/measure_vehicles.py`, `python3 tools/build_wagon.py`, `python3 tools/measure_vehicles.py`, `python3 tools/verify_wagon.py`, `dotnet build game/*.csproj`. Preview additionally requires numpy, matplotlib and Pillow. The measuring, building and validating scripts use the Python standard library only.

The Godot frame can be reproduced through the existing shot helper without changing its catalogue:

```python
import sys
from pathlib import Path
sys.path.insert(0, "tools")
import shot
shot.SCENES["wagon"] = (
    ["--vehicle={TMP}", "--gun=wagon"],
    {"UG_QUICK": "1", "UG_VSTATIC": "1", "UG_VCAM": "-7,3,7;0,1,0"},
    False, 180, "station wagon stationary rear quarter",
)
shot.MULTI["wagon"] = "rig_00.png"
shot.take("wagon", str(Path(".shots/wagon-render/wagon.png").resolve()), True)
```

The runs used `XDG_DATA_HOME=$PWD/.shots/wagon-runtime` to keep runtime caches and user data within this worktree. Runtime logs remain under `.shots/` and are not committed.

## Files added or changed

| File | Change |
| --- | --- |
| `game/Vehicle.cs` | Wagon spec, factory, catalogue and both lookups; seat/body-pose/roof registrations; append-only TypeId comment. |
| `game/WorldBuilder.cs` | Natural-spawn comment and diagnostic identify wagon as command-only alongside Golf. |
| `game/content/wagon_body.txt` | New body, 238 vertices / 409 triangles. |
| `game/content/wagon_palette.png` | Copy of sedan's 4×2 palette. |
| `game/content/wagon_taillights.txt` | Sedan rear lamp geometry translated +0.25 m in Z. |
| `game/content/wagon_glass_windshield.txt` | Windshield, 4 vertices / 2 triangles. |
| `game/content/wagon_glass_rear.txt` | Tailgate pane, 4 vertices / 2 triangles. |
| `game/content/wagon_glass_l_front.txt` | Left front pane, 4 vertices / 2 triangles. |
| `game/content/wagon_glass_r_front.txt` | Right front pane, 4 vertices / 2 triangles. |
| `game/content/wagon_glass_l_rear.txt` | Left rear passenger pane, 4 vertices / 2 triangles. |
| `game/content/wagon_glass_r_rear.txt` | Right rear passenger pane, 4 vertices / 2 triangles. |
| `game/content/wagon_glass_l_mid1.txt` | Left cargo pane, 4 vertices / 2 triangles. |
| `game/content/wagon_glass_r_mid1.txt` | Right cargo pane, 4 vertices / 2 triangles. |
| `game/testing/tests/VehicleWagonTests.cs` | Compiled and passed in-engine regression test for server/replica assembly. |
| `tools/measure_vehicles.py` | Reproducible mesh/spec extraction, comparison tables, sanity checks and statistics. |
| `tools/build_wagon.py` | Reproducible sedan-derived geometry and dimension notes. |
| `tools/verify_wagon.py` | Float32 loader-rule validation and static integration checks. |
| `tools/preview_wagon.py` | Static geometry comparison/inspection artifact generator. |
| `notes/vehicle_measurements.md` | Complete measurement deliverable, including wagon row, derivations and baseline fleet statistics. |
| `notes/wagon_dimensions.md` | Generated construction-dimension derivation, embedded in the measurements. |
| `notes/wagon_geometry.png` | Static geometry preview, explicitly labelled as outside Godot. |
| `notes/wagon_godot.png` | Actual Godot vehicle-harness frame; renderer caveats documented above. |
| `notes/WAGON_REPORT.md` | This report. |

Build-generated changes to tracked `core/**/bin` and `core/**/obj` files were restored after validation and are not part of the task change. Work is committed on `astra-wagon`; nothing was pushed.
