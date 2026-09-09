# Wagon: remove the hood ledge and restore the nose bevels

Revision of `5044457d` on `astra-wagon`. The hood now meets the windscreen directly, with a flat centre and two planar bevelled outer strips reaching **X = ±1.260 m**. The front panel's top edge follows the bevel continuously. Its entire surface remains coplanar, without filler wedges.

## Hood and A-pillar junction

All six original shelf faces **f16, f17, f72, f73, f146 and f147 are gone**, checked by their original coordinate signatures. There are **zero horizontal hood/cowl faces**. The centre hood runs from **(Y,Z) = (0.999953,−2.757828)** to the existing windscreen bottom **(1.125000,−1.250000)**, giving a **4.740799°** longitudinal tilt. The old slope-to-shelf edge at Z = −1.406856 no longer exists.

The outer strips each contain two triangles, with normals **(±0.405374, 0.911023, −0.075553)** and a **24.352857°** tilt from horizontal. The sedan's approximately 26.9° uses a 0.25 m strip; the wagon's required 0.125 m drop across a **0.28 m** strip produces this shallower angle. The specified width and leading-edge heights take precedence over copying the sedan's exact angle.

Each outer strip ends at **(X,Y,Z) = (±1.260000,0.994273,−1.323997)**, the intersection of its plane with the existing windscreen/A-pillar plane. The outer A-pillar foot extends down that same plane to meet it. This removes the shelf without adding a vertical cap or a second transition facet. The inner foot, windscreen glass, side-window apertures, roof and roof-front rake retain their geometry. The A-pillar/windscreen plane remains **60.488501° above horizontal**, and the roof-front upper edge remains **(Y,Z) = (2.170000,−0.658491)**.

## Front panel top edge

Measurements below intersect the saved float32 fascia triangles with X planes and take the highest point. Both sides agree. The verifier checks all mesh vertex stations and midpoints, plus 101 samples along each bevel: **215 sections total**.

| Absolute X (m) | top Y (m) | top Z (m) |
| ---: | ---: | ---: |
| 0.00 | **0.999953** | −2.757828 |
| 0.48 | **0.999953** | −2.757828 |
| 0.98 | **0.999953** | −2.757828 |
| 1.05 | 0.968703 | −2.759062 |
| 1.12 | 0.937453 | −2.760297 |
| 1.19 | 0.906203 | −2.761531 |
| 1.26 | **0.874953** | −2.762766 |

The centre stays flat across X; from |X| = 0.98 to 1.26 the top edge is a straight sloped line, with no vertical jump. The slight Z change keeps this line on the existing fascia plane:

`Z(Y) = −2.792392 + 0.034564 × (Y − 0.125) / 0.874953`.

All **17 fascia triangles** satisfy this plane within 1 μm. The outer top corner moves forward by approximately **4.938 mm** as it drops, avoiding the twist that occurred when the lowered corner retained the centre nose's Z coordinate. Grille and lamp geometry are retained.

## Every face in the review boxes

Boxes: **X [−1.35,−0.85] / [+0.85,+1.35], Y [0.80,1.30], Z [−2.90,−2.10]**. Face IDs are zero-based. “Forward-facing” means −Z is the normal's dominant direction; the upward hood also has a small negative Z component due to its longitudinal slope.

Using the existing report's **centroid convention**, the boxes contain only **f21 on the left and f64 on the right**, both upward bevel faces. **Forward-facing count: 0 on each side, 0 total.**

Clipping every triangle against all six box planes gives **24 intersecting faces**, fully listed below. **10 of these are ordinary forward-facing fascia faces** with centroids below or inboard of the boxes. They occupy the same plane and contain no filler wedges. A literal requirement of zero forward-facing intersections is not satisfied: the required closed front panel occupies part of these boxes, as it does on the sedan. This report keeps that distinction explicit rather than claiming those faces are absent.

| side | face | centroid in box | normal | all three vertices |
| --- | ---: | --- | --- | --- |
| left | f0 | no | (-1.000000, 0.000000, 0.000000) | (-1.260000, 0.874953, -2.762766); (-1.260000, 0.125000, -2.792392); (-1.260000, 0.100803, -2.786276) |
| left | f1 | no | (-1.000000, 0.000000, 0.000000) | (-1.260000, -0.125000, -2.757828); (-1.260000, 0.874953, -2.762766); (-1.260000, 0.100803, -2.786276) |
| left | f4 | no | (-1.000000, 0.000000, 0.000000) | (-1.260000, 0.874953, -2.762766); (-1.260000, -0.125000, -2.757828); (-1.260000, -0.120000, -2.320000) |
| left | f5 | no | (-1.000000, 0.000000, 0.000000) | (-1.260000, 0.874953, -2.762766); (-1.260000, -0.120000, -2.320000); (-1.260000, -0.120000, -1.323997) |
| left | f6 | no | (-1.000000, 0.000000, 0.000000) | (-1.260000, -0.120000, -1.323997); (-1.260000, 0.994273, -1.323997); (-1.260000, 0.874953, -2.762766) |
| left | f21 | yes | (-0.405374, 0.911023, -0.075553) | (-0.980000, 0.999953, -2.757828); (-1.260000, 0.874953, -2.762766); (-1.260000, 0.994273, -1.323997) |
| left | f22 | no | (-0.405374, 0.911023, -0.075553) | (-0.980000, 0.999953, -2.757828); (-1.260000, 0.994273, -1.323997); (-0.980000, 1.125000, -1.250000) |
| left | f104 | no | (0.000000, 0.039473, -0.999221) | (-0.480000, 0.125000, -2.792392); (-1.260000, 0.125000, -2.792392); (-1.260000, 0.874953, -2.762766) |
| left | f105 | no | (0.000000, 0.039473, -0.999221) | (-0.480000, 0.125000, -2.792392); (-1.260000, 0.874953, -2.762766); (-0.980000, 0.999953, -2.757828) |
| left | f106 | no | (-0.000003, 0.039472, -0.999221) | (-0.480000, 0.360000, -2.783109); (-0.480000, 0.125000, -2.792392); (-0.980000, 0.999953, -2.757828) |
| left | f107 | no | (0.000000, 0.039474, -0.999221) | (-0.980000, 0.999953, -2.757828); (-0.480000, 0.999953, -2.757828); (-0.480000, 0.540000, -2.775998) |
| left | f108 | no | (0.000000, 0.039474, -0.999221) | (-0.980000, 0.999953, -2.757828); (-0.480000, 0.540000, -2.775998); (-0.480000, 0.360000, -2.783109) |
| left | f116 | no | (0.000000, 0.996579, -0.082648) | (-0.480000, 0.999953, -2.757828); (-0.980000, 0.999953, -2.757828); (-0.980000, 1.125000, -1.250000) |
| right | f45 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 0.874953, -2.762766); (1.260000, -0.125000, -2.757828); (1.260000, 0.100803, -2.786276) |
| right | f46 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 0.100803, -2.786276); (1.260000, 0.125000, -2.792392); (1.260000, 0.874953, -2.762766) |
| right | f47 | no | (1.000000, 0.000000, 0.000000) | (1.260000, -0.125000, -2.757828); (1.260000, 0.874953, -2.762766); (1.260000, 0.994273, -1.323997) |
| right | f64 | yes | (0.405374, 0.911023, -0.075553) | (1.260000, 0.994273, -1.323997); (1.260000, 0.874953, -2.762766); (0.980000, 0.999953, -2.757828) |
| right | f65 | no | (0.405374, 0.911023, -0.075553) | (0.980000, 1.125000, -1.250000); (1.260000, 0.994273, -1.323997); (0.980000, 0.999953, -2.757828) |
| right | f110 | no | (0.000000, 0.039473, -0.999221) | (1.260000, 0.874953, -2.762766); (1.260000, 0.125000, -2.792392); (0.480000, 0.125000, -2.792392) |
| right | f111 | no | (0.000000, 0.039473, -0.999221) | (0.980000, 0.999953, -2.757828); (1.260000, 0.874953, -2.762766); (0.480000, 0.125000, -2.792392) |
| right | f112 | no | (0.000000, 0.039474, -0.999221) | (0.480000, 0.540000, -2.775998); (0.480000, 0.999953, -2.757828); (0.980000, 0.999953, -2.757828) |
| right | f113 | no | (0.000003, 0.039472, -0.999221) | (0.980000, 0.999953, -2.757828); (0.480000, 0.125000, -2.792392); (0.480000, 0.360000, -2.783109) |
| right | f114 | no | (0.000000, 0.039474, -0.999221) | (0.980000, 0.999953, -2.757828); (0.480000, 0.360000, -2.783109); (0.480000, 0.540000, -2.775998) |
| right | f117 | no | (0.000000, 0.996579, -0.082648) | (0.980000, 1.125000, -1.250000); (0.980000, 0.999953, -2.757828); (0.480000, 0.999953, -2.757828) |

## Dimensions and topology

Body envelope remains **5.800 × 2.520 × 2.440 m** (length × width × height): **X [−1.26,1.26], Y [−0.27,2.17], Z [−2.90,2.90]**. Body width is 2.520 m at all **27 review stations** and **55 vertex/mid-span sections**. All **82 exterior side triangles**, all eight pillar exteriors and both bumper tips lie at X = ±1.260.

The mesh has **163 positions and 350 triangles**, with **0 / 525 boundary edges (0.0%)**, **0 degenerate triangles**, no duplicate faces, no overused edges, and **exactly 3 corners on every face**. It is one connected outward shell, with no coplanar overlaps or triangle piercings across **4,595 candidate pairs**; signed volume is **12.854203 m³**.

All **11 non-body wagon assets** are byte-identical to `5044457d`. Dimensions, wheels, seats, lights, glass, cargo clearance, palette and registrations pass the existing checks. Construction coordinates are updated in [wagon_dimensions.md](wagon_dimensions.md).

## Verification

| Check | Result |
| --- | --- |
| `python3 tools/verify_wagon.py` | **PASS**: all 11 OBJ meshes, continuous nose trim, planar bevels, shelf removal, complete box listing, width, roof/A-pillar plane, closure, overlaps, glass, cargo, lamps and registrations. |
| Regression sensitivity | **PASS**: rejects `5044457d` for its flattened outer top edge, and `13bd2151` for its dropped outer corner off the fascia plane. All six original shelf face signatures are absent. |
| `dotnet build game/UnturnedGodot.csproj` | **PASS — 0 warnings, 0 errors**, 6.10 s. |
| `./test.sh --l1 --only 'vehicle.wagon*'` | **PASS — 1 test, 13 checks**, 0 failures, 0.84 s. Its build also reports **0 warnings, 0 errors**, 5.82 s. |
| Reproducibility | **PASS**: regenerating all **12 wagon assets** produces identical bytes. |

## Exterior views

Godot/Vulkan captures of this revision, frame 48, parked with `UG_VSTATIC=1`. Cameras and targets are vehicle-local; **eye Y = target Y = 1.125**. The views show the sloped outer nose and the direct hood-to-A-pillar junction.

| View | Eye (X,Y,Z) | Target (X,Y,Z) | Capture |
| --- | --- | --- | --- |
| Left nose and pillar | (−3.5,1.125,−3.8) | (−1.0,1.125,−1.95) | [wagon_hood_left.png](wagon_hood_left.png) |
| Right nose and pillar | (+3.5,1.125,−3.8) | (+1.0,1.125,−1.95) | [wagon_hood_right.png](wagon_hood_right.png) |
| Left pillar foot | (−2.7,1.125,−2.4) | (−1.2,1.125,−1.35) | [wagon_pillar_foot_left.png](wagon_pillar_foot_left.png) |
| Right pillar foot | (+2.7,1.125,−2.4) | (+1.2,1.125,−1.35) | [wagon_pillar_foot_right.png](wagon_pillar_foot_right.png) |

The capture harness logs shader/render synchronization warnings and a renderer-thread shutdown error after saving; these images verify appearance. The build and targeted L1 results above are the clean automated checks.

The [static geometry overview](wagon_geometry.png) is also regenerated from the saved mesh with `python3 tools/preview_wagon.py`.

Reproduce a left-side capture (mirror camera and target X for the right):

```sh
mkdir -p /tmp/wagon-hood-shot
VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.aarch64.json \
UG_QUICK=1 UG_VSTATIC=1 UG_VCAM='-3.5,1.125,-3.8;-1.0,1.125,-1.95' \
xvfb-run -a ~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64 \
  --path game --rendering-driver vulkan --write-movie /tmp/wagon-hood-shot/mov.avi \
  --fixed-fps 30 -- --vehicle=/tmp/wagon-hood-shot --gun=wagon
```

Reproduce the asset and automated checks:

```sh
python3 tools/build_wagon.py
python3 tools/verify_wagon.py
dotnet build game/UnturnedGodot.csproj
./test.sh --l1 --only 'vehicle.wagon*'
```

Pre-existing tracked build-artifact modifications are excluded from the commit. Nothing is pushed.
