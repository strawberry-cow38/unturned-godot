# Wagon revision: continuous sides, joined geometry, sedan hood

Revision of the accepted design at `3bfe4468`, on `astra-wagon`. The body retains **5.800 × 2.520 × 2.440 m** (length × width × height), three side windows per side, the flat roof, the accepted near-vertical tailgate, and the existing wheel/seat rig. This revision removes the wheel openings, replaces overlapping construction pieces with one joined shell, and takes the hood profile from the sedan mesh.

Coordinates are metres, **+Y up, forward −Z**. The body AABB remains exactly **X [−1.26, 1.26], Y [−0.27, 2.17], Z [−2.90, 2.90]**. Wheel-well removal changes none of those extrema. [wagon_dimensions.md](wagon_dimensions.md) records the current construction; [vehicle_style.md](vehicle_style.md) preserves the original fleet/style analysis. Historical wagon values in `vehicle_measurements.md` are superseded here.

## Wheel-area measurements and removal

Both `van_body.txt` and `truck_body.txt` have continuous outer side surfaces at approximately **|X| = 1.23096**, extending down to **Y = −0.125** across both axle positions (**Z = ±1.40**). Their side panels include a subdivision at **Y = 0.125**, with upper levels around **1.000–1.125**. There is no raised arch contour above either wheel.

For example, the van's rear panel spans Z **0.231439..2.120542**, and the truck's spans **0.372843..2.120542**, both crossing the rear axle at **1.40**. Their lower corners remain at Y **−0.125**. Measurements used actual face coordinates, not minimum heights elsewhere on the body. As a second check, both bodies block **48/48** exterior-skin rays at axle Z plus **−0.4, 0, +0.4**, Y **−0.1, 0.2, 0.5, 0.7**, on both sides (rays cross |X| **1.1..1.4**).

The wagon now follows that construction: continuous side panels reach the retained **Y = −0.12 sill**, including over both axles. The former **Y = 0.50 / 0.72** arch contour, recess returns and separate inner wall pieces are removed. There are no replacement wheel-well backings. Wheels remain separate meshes at the unchanged spec anchors **(±1.30, 0.25, −1.56)** and **(±1.30, 0.25, 1.46)**.

## Welded topology and geometry cleanup

Positions are welded by their loaded **float32 XYZ** before counting undirected edges. UV/normal seams do not split the measurement. A boundary edge has exactly one incident triangle.

| body | raw position records | welded verts | tris | boundary edges | % |
| --- | ---: | ---: | ---: | ---: | ---: |
| wagon before (`3bfe4468`) | 648 | 232 | 380 | **60 / 598** | **10.0%** |
| **wagon revised** | **182** | **182** | **388** | **0 / 582** | **0.0%** |
| sedan | 515 | 182 | 366 | 4 / 551 | 0.7% |
| hatchback | 408 | 172 | 346 | 4 / 521 | 0.8% |
| van | 589 | 224 | 422 | 30 / 648 | 4.6% |
| truck | 530 | 208 | 390 | 30 / 600 | 5.0% |

The result is one connected, consistently outward-oriented shell. Every welded edge has **exactly two oppositely directed uses**; there are **zero open or overused edges**. Pillars join the belt and roof without buried mating caps. The inner walls, passenger floor, cargo step/deck, gate and underside share their junctions. There are no separate closed solids hidden inside the body. The grille is a partitioned material patch on the fascia; the projecting rear latch shares an opening in the gate and has no buried back face.

The generator splits shared panel edges at their corners before triangulation, preventing T-junctions. The saved mesh has **zero duplicate or degenerate triangles**, **zero unused positions**, and no positive-area coplanar overlaps or triangle piercings detected by the pairwise audit (**4,819 candidate pairs after bounding-box rejection**). Coplanarity uses a **2 μm** distance tolerance; projected overlap areas must be below **10⁻⁹ m²**. Signed shell volume is **12.704918 m³**. Minimum body triangle area is **0.000699999261 m²**.

Welded positions decrease **232 → 182 (21.6%)**. The complete closures and joined seams bring triangles to **388**, eight above the old open assembly and below the van's **422**. OBJ `v` records are now fully shared; explicit per-corner `vn` and `vt` indices preserve flat shading and palette seams. `ContentProvider.ParseObjUncached` calls `SetNormal`/`SetUV` for each corner before `AddVertex`, so sharing position records does not smooth the mesh. This also avoids padding the asset to meet an exporter-dependent raw-vertex minimum. The loaded triangle budget remains within the measured fleet range.

## Sedan hood measurements and transfer

`tools/build_wagon.py` now reads `sedan_body.txt` for the front stations and the two specs for the overhang scale. It averages bilateral export jitter in longitudinal coordinates and removes approximately 1 μm noise around exact height levels. The independently authored wagon cabin coordinates remain the basis for the rest of the body.

The seven requested front height levels are present in both saved meshes:

| front feature / Y station | sedan Y | wagon Y |
| --- | ---: | ---: |
| bumper bottom | −0.158648 | −0.158648 |
| lower valance | −0.125000 | −0.125000 |
| bumper top | 0.100803 | 0.100803 |
| upper valance | 0.125000 | 0.125000 |
| outer hood nose | 0.874953 | 0.874953 |
| outer hood rear crease | 1.000000 | 1.000000 |
| centre hood rear / cowl | 1.125000 | 1.125000 |

The centre hood nose is **Y = 0.999953** in both bodies: it rounds to the requested **1.000** station but is distinct from the outer rear crease. The actual hood has a centre slope, lower outer shoulders, and a level cowl; the old wagon's single wedge at **0.91 → 1.10** is gone.

| profile measurement | sedan | wagon |
| --- | ---: | ---: |
| foremost body Z | −3.009812 | −2.900000 |
| front axle Z | −1.620000 | −1.560000 |
| front overhang | 1.389812 | 1.340000 |
| hood nose Z | −2.862356 | −2.757828 |
| centre height at nose | 0.999953 | 0.999953 |
| rear crease Z | −1.461164 | −1.406856 |
| centre height at crease | 1.125000 | 1.125000 |
| sloped hood run | 1.401192 | 1.350972 |
| centre rise | 0.125047 | 0.125047 |
| slope angle | 5.0997° | 5.2883° |
| end of flat cowl Z | −1.122904 | −1.250000 |
| flat cowl run | 0.338260 | 0.156856 |

Longitudinal transfer is **Z_w = −1.56 + (Z_s + 1.62) × 0.964159181**, where the factor is **1.34 / 1.389812**. Heights remain unchanged, so the shorter overhang produces the small, expected increase in slope angle. The flat cowl ends early at the wagon's accepted windscreen position, rather than relocating the greenhouse. The centre hood is **1.96 m wide**; its shoulders stay **0.125 m lower** along the slope while their widths blend into the accepted wagon nose/body taper.

To meet the cowl, only the windscreen's two lower vertices and the adjoining A-post front feet rise **25 mm**, from **Y = 1.10 to 1.125**, at unchanged **Z = −1.25**. The windscreen top, all six side panes, rear pane, roof and B/C/D profiles keep their accepted coordinates. The seven other glass assets, both lamps and palette remain byte-identical to `3bfe4468`. Some additional vertices lie along the hood slope at the width break (Z **−2.32**), along the grille divisions (Y **0.36 / 0.54**), and at panel junctions; these subdivide the measured surfaces and do not introduce extra hood profile steps.

## Verification

| check | result |
| --- | --- |
| `dotnet build game/UnturnedGodot.csproj` | **PASS — 0 warnings, 0 errors**, 5.86 s. |
| `./test.sh --l1 --only 'vehicle.wagon*'` | **PASS — 1 test, 13 checks**, 0.90 s; its build also had 0 warnings/errors. |
| `python3 tools/verify_wagon.py` | **PASS** for all 11 OBJ assets: exactly 3 corners per face, explicit position/UV/normal indices in range, finite float32 values, unit normals following winding, valid UVs, no degenerate or duplicate triangles. |
| shell audit | **0/582 boundary edges**, no overused edges, consistent winding, one component, no detected coplanar overlaps or triangle piercings. |
| lower body | **91/91** continuous-side, sill/floor and rear-underside probes hit the shell; 80 of these sample the exterior across the former wheel openings. |
| windows / cargo / lamps | **64** glass-interior probes and **9** cargo probes clear; all **4 outward faces per lamp mesh** visible past the body. |
| integration | Real/replica factories, loaded mesh/AABB, four wheels, four seats, cabin, eight panes, capacities and mass pass. Wagon remains **TypeId 32**, preserving all older IDs. Specs, fitted boxes, shared wheel/seat/steering assets and light anchors are unchanged. |
| reproducibility | A second generator run produces identical bytes for all wagon assets. |
| preview | [wagon_geometry.png](wagon_geometry.png) regenerated and inspected. The inspection renderer now uses a depth buffer so long side triangles cannot incorrectly reveal hidden cabin faces through painter sorting. |

The preview is static geometry inspection with approximate lighting, **not a Godot render**. The in-engine test exercises assembly and loading; the author's visual review is pending. The test generated **7 convex hulls** and took **2 fitted boxes** out of active physics, retaining them for look-focus. Driving, suspension travel and a live multiplayer session were not exercised by this geometry revision.

Reproduce:

```sh
python3 tools/build_wagon.py
python3 tools/verify_wagon.py
dotnet build game/UnturnedGodot.csproj
./test.sh --l1 --only 'vehicle.wagon*'
python3 tools/preview_wagon.py
```

Build/verifier use the Python standard library; preview additionally uses numpy, matplotlib and Pillow. The commit contains the wagon body, windscreen, generator/verifier/preview and these notes. Pre-existing tracked build-artifact edits are excluded. Nothing is pushed.
