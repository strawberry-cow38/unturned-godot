# Wagon revision 3: straight sides, full-width ends, slanted roof front

Revision of `7ad5819c` on `astra-wagon`. The body now has one **2.520 m plan width from Z −2.900 to +2.900**. Nose, doors, fenders, tail, roof sides and all A/B/C/D outer pillar faces share **X = ±1.260**. The roof front continues the existing windscreen/A-pillar plane through the roof thickness.

The envelope remains **5.800 × 2.520 × 2.440 m** (length × width × height), with AABB **X [−1.26, 1.26], Y [−0.27, 2.17], Z [−2.90, 2.90]**. Coordinates are metres, +Y up, forward −Z. Three side windows per side, the flat roof height, near-vertical tailgate angle, closed wheel areas, cargo floor, wheels and seats are retained. [wagon_dimensions.md](wagon_dimensions.md) records the updated construction.

## Width profile and flush walls

`tools/build_wagon.py` uses one outer half-width, **1.260**, for the full body. The nose and bumper widen from ±1.160; the pillar/roof/belt faces move from ±1.230 to the wall plane. The rear taper is removed. Six side glass panes move outward by **0.030 m**, retaining their **0.004 m** inset and every Y/Z coordinate. The roof fitted box widens to **2.520 m**; its Y/Z bounds stay fixed.

The table measures **max X − min X where saved body triangles intersect each Z plane**, using loaded float32 coordinates and the review's displayed stations. This includes panel interiors between authored vertices. All rows are **2.520** to three decimals; there are no plan-width exceptions.

| Z (m) | body X extent (m) |
| ---: | ---: |
| −2.90 | 2.520 |
| −2.68 | 2.520 |
| −2.45 | 2.520 |
| −2.23 | 2.520 |
| −2.01 | 2.520 |
| −1.78 | 2.520 |
| −1.56 | 2.520 |
| −1.34 | 2.520 |
| −1.12 | 2.520 |
| −0.89 | 2.520 |
| −0.67 | 2.520 |
| −0.45 | 2.520 |
| −0.22 | 2.520 |
| 0.00 | 2.520 |
| +0.22 | 2.520 |
| +0.45 | 2.520 |
| +0.67 | 2.520 |
| +0.89 | 2.520 |
| +1.12 | 2.520 |
| +1.34 | 2.520 |
| +1.56 | 2.520 |
| +1.78 | 2.520 |
| +2.01 | 2.520 |
| +2.23 | 2.520 |
| +2.45 | 2.520 |
| +2.68 | 2.520 |
| +2.90 | 2.520 |

The verifier also checks **53 sections** at all vertex Z stations and their intervening midpoints, **98 exterior side triangles** for exact alignment with X = ±1.260, and surface probes on all **eight pillars**. These additional checks prevent a wide roof or bumper from hiding inset doors or pillars. Inner cabin walls at ±0.98 and the underside bevel ending at ±0.99 remain inside the full-width exterior.

## Wagon-specific tail

The lower tailgate is the full-width rear face at **Z = 2.680**, joined to the cargo deck and bumper. Its glass and D-post rake stay at **(Y,Z) = (1.10,2.68) → (1.92,2.56)**, or **8.326° from vertical**. The bumper reaches **Z = 2.900** at the same ±1.260 outer width. The latch retains its small 0.020 m outward projection.

The rear is independently authored wagon geometry. No sedan boot-lid recess or rear body profile is transferred. **21 probes** across the actual gate face, including X = ±1.250 and Y = 1.050, verify that the rear panel itself is present at full width; a wide bumper cannot mask a recessed gate in this check.

## Roof front follows the A-pillars

The windscreen and the forward faces of both A-pillars retain their existing endpoints. The former vertical roof cap is replaced with a slanted face in the same plane:

| landmark | Y | old Z | revised Z |
| --- | ---: | ---: | ---: |
| windscreen / A-pillar base | 1.125 | −1.250000 | −1.250000 |
| windscreen top / roof underside front | 1.920 | −0.800000 | −0.800000 |
| roof upper front edge | 2.170 | −0.800000 | **−0.658491** |

The continuation is **Z(Y) = −1.25 + (Y − 1.125) × 0.45 / 0.795**. Across the 0.250 m roof thickness, the upper leading edge moves aft **0.141509 m**. The roof remains flat at Y = 2.170 and ends at Z = 2.560. All roof-front vertices lie within **1 μm** of the windscreen plane, and its face normals agree with the windscreen normal.

**Measurement correction:** the review's Z = −2.758 point is the hood nose at Y ≈ 1.000, not the glass base. The saved windscreen starts at **Z = −1.250, Y = 1.125**. Its actual rake is **29.5115° from vertical (60.4885° above horizontal)**, unchanged in this revision. Using the hood nose to calculate 59.1° does not measure the A-pillar. Preserving the actual endpoints keeps the requested greenhouse layout and existing pillar rake while removing the blunt roof step.

## Sedan hood preserved

The hood still reads its front profile from `sedan_body.txt`. The centre slope, 0.125 m lower outer shoulders, flat cowl and seven requested height stations remain. Only the outer width and its small cowl transition change; that transition is explicitly split into planar triangles where it joins the straight wall.

| requested Y station | sedan Y | revised wagon Y |
| ---: | ---: | ---: |
| −0.159 | −0.158648 | −0.158648 |
| −0.125 | −0.125000 | −0.125000 |
| 0.101 | 0.100803 | 0.100803 |
| 0.125 | 0.125000 | 0.125000 |
| 0.875 | 0.874953 | 0.874953 |
| 1.000 | 1.000000 | 1.000000 |
| 1.125 | 1.125000 | 1.125000 |

The centre hood nose stays at **(Y,Z) = (0.999953,−2.757828)** and rear crease at **(1.125,−1.406856)**, with the flat cowl ending at **Z = −1.250**. Centre width remains **1.960 m**. Sedan-to-wagon longitudinal transfer remains **Z_w = −1.56 + (Z_s + 1.62) × 0.964159181**; front overhang remains **1.340 m** and centre hood slope **5.2883°**. The windscreen, rear glass, lamps and palette remain byte-identical to `7ad5819c`.

## Topology and verification

Positions are welded by loaded float32 XYZ before counting undirected edges. UV and flat-normal seams do not split the measurement. A boundary edge has exactly one incident triangle.

| body revision | positions / welded | triangles | boundary edges | boundary % |
| --- | ---: | ---: | ---: | ---: |
| revision 2 (`7ad5819c`) | 182 / 182 | 388 | 0 / 582 | 0.0% |
| **revision 3** | **182 / 182** | **388** | **0 / 582** | **0.0%** |

The body is one connected, outward-oriented shell. Every welded edge has exactly two opposite directed uses. There are **zero overused edges, degenerate triangles, duplicate triangles or unused positions**. Every saved face has **exactly three corners**. Minimum body triangle area remains **0.000699999261 m²**. The pairwise surface audit finds no positive-area coplanar overlaps or triangle piercings among **4,806 candidate pairs**; signed shell volume is **12.880944 m³**. Shared panel edges are split before triangulation, preserving welded roof, pillar, sill and gate junctions.

| check | result |
| --- | --- |
| `python3 tools/verify_wagon.py` | **PASS** for all 11 OBJ assets: triangle-only faces, valid explicit indices, finite float32 values, unit normals matching winding, valid UVs, no degenerate or duplicate triangles. |
| width / walls / gate / roof | **PASS**: 27 report stations plus 53 vertex/mid-span sections, 98 flush exterior side triangles, all eight pillars, 21 gate-face probes, six side panes, roof/windscreen coplanarity, unchanged windscreen and rear rake. |
| regression sensitivity | Both new shape checks reject the old `7ad5819c` body: nose width **2.320**, and vertical roof cap outside the A-pillar plane. |
| closed lower body | **91/91** continuous-side, floor/sill and rear-underside probes pass, including 80 across the former wheel openings. No wheel wells reintroduced. |
| glass / cargo / lamps | **64** glass-interior and **9** cargo probes clear; **4 outward faces per lamp mesh** visible past the body. |
| `dotnet build game/UnturnedGodot.csproj` | **PASS — 0 errors**, 51.17 s. The compilation emitted **22 warnings in existing code** (member hiding, obsolete APIs, unused/unassigned members and unreachable code); the roof-width edit introduced no new warning locations. |
| `./test.sh --l1 --only 'vehicle.wagon*'` | **PASS — 1 test, 13 checks**, 0.86 s. Its incremental build reported **0 warnings / 0 errors**, 6.43 s. |
| reproducibility | **PASS**: a second generator run produced byte-identical contents for all **12 wagon assets**, including the palette. |
| preview | [wagon_geometry.png](wagon_geometry.png) regenerated and inspected, including body-only plan and enlarged A-pillar/roof junction views. |

The preview is static geometry inspection with approximate lighting, not a Godot render. The targeted runtime test generated **8 convex hulls** for the revised wagon and exercises assembly/loading, including real and replica bodies, dimensions, cabin, eight panes, four wheels, four seats and capacities. Wagon remains TypeId **32**. Driving, suspension travel and a live multiplayer session are outside this geometry check.

Reproduce:

```sh
python3 tools/build_wagon.py
python3 tools/verify_wagon.py
dotnet build game/UnturnedGodot.csproj
./test.sh --l1 --only 'vehicle.wagon*'
python3 tools/preview_wagon.py
```

Build/verifier use the Python standard library; preview additionally uses numpy, matplotlib and Pillow. Pre-existing tracked build-artifact edits are excluded from the revision commit. Nothing is pushed.
