# Wagon: remove the hood / A-pillar crease

Revision of `13bd2151` on `astra-wagon`. The hood nose is now **Y = 0.999953 (1.000 to three decimals) across the entire 2.520 m width**, with one longitudinal hood plane continuing to one level cowl. The 0.125 m dropped outer top corners and twisted fascia wedges are removed. This supersedes the previous report's incorrect claim that an aspect-ratio scan established that the junction was fixed.

## Geometry change

Carry the sedan's centre hood slope out to **X = ±1.260**: **(Y,Z) = (0.999953,−2.757828) → (1.125000,−1.406856)**. Continue a full-width level cowl at **Y = 1.125** to **Z = −1.250**, the unchanged A-pillar base. There is no transverse shoulder bevel or diagonal cowl corner facet. The existing longitudinal slope-to-level transition remains.

All front fascia triangles now occupy the same plane, including the outer corners. Its profile is **Z(Y) = −2.792392 + 0.034564 × (Y − 0.125) / 0.874953**. The grille keeps its position, outline and palette. The sedan's **Y = 0.874953** and **Y = 1.000000** vertices remain as coplanar side-wall seams below the new hood; they do not form a fold. This is a surface change, not just retriangulation.

**341 triangles retain their coordinates, UVs and normals; 37 are replaced by 37.** All **283 triangles wholly at or behind Z = −1.250** are unchanged, including all pillars and roof faces. All **11 non-body wagon assets** are byte-identical to `13bd2151`.

## Nose top edge

Measured by intersecting the saved float32 fascia triangles with X planes and taking the highest point. All fascia vertex X stations and intervening midpoints are checked, not just these five values.

| X (m) | nose top Y (m) | nose top Z (m) |
| ---: | ---: | ---: |
| −1.26 | **0.999953** | −2.757828 |
| −0.98 | **0.999953** | −2.757828 |
| 0.00 | **0.999953** | −2.757828 |
| +0.98 | **0.999953** | −2.757828 |
| +1.26 | **0.999953** | −2.757828 |

## Every face in the requested boxes

Boxes: **X [−1.35,−0.85] and [+0.85,+1.35], Y [0.80,1.30], Z [−2.90,−2.10]**. IDs are **zero-based**, matching the review's old f137/f138/f140/f141. All three vertices and the stored normal of each saved face are listed below. Selection clips each triangle against all six box planes; it includes crossings even when no vertex or centroid lies inside.

For the review's **centroid selection**, each side contains one upward hood face (**f10 / f66**) and one flat side-wall face (**f8 / f61**). There are **zero forward-facing fascia wedges** in that selection. The upward face has the same normal as the centre hood, so its edge cannot produce a shoulder shading crease. The side-wall face is coplanar with the rest of the wall.

A literal intersection selection also catches ordinary fascia triangles below the hood edge: **32 faces total**, listed without hiding them. Those fascia triangles have **N ≈ (0,0.039474,−0.999221)** and lie on one plane within 1 μm of serialization tolerance. They have no dropped top edge or extra fold. The sedan's quoted single face is likewise a centroid result: its **f158** (left) / **f162** (right); its forward-facing fascia also intersects these boxes. Thus “no −Z faces intersect the box” would not describe either closed nose.

| side | face | centroid in box | normal (X,Y,Z) | all three vertices (X,Y,Z), m |
| --- | ---: | --- | --- | --- |
| left | f0 | no | (-1.000000, 0.000000, -0.000000) | (-1.260000, 0.874953, -2.757828); (-1.260000, 0.999953, -2.757828); (-1.260000, 0.125000, -2.792392) |
| left | f1 | no | (-1.000000, 0.000000, -0.000000) | (-1.260000, 0.874953, -2.757828); (-1.260000, 0.125000, -2.792392); (-1.260000, 0.100803, -2.786276) |
| left | f2 | no | (-1.000000, 0.000000, -0.000000) | (-1.260000, -0.125000, -2.757828); (-1.260000, 0.874953, -2.757828); (-1.260000, 0.100803, -2.786276) |
| left | f5 | no | (-1.000000, 0.000000, -0.000000) | (-1.260000, 0.874953, -2.757828); (-1.260000, -0.125000, -2.757828); (-1.260000, -0.120000, -2.320000) |
| left | f6 | no | (-1.000000, 0.000000, -0.000000) | (-1.260000, 0.874953, -2.757828); (-1.260000, -0.120000, -2.320000); (-1.260000, -0.120000, -1.406856) |
| left | f7 | no | (-1.000000, 0.000000, -0.000000) | (-1.260000, 0.874953, -2.757828); (-1.260000, -0.120000, -1.406856); (-1.260000, 1.000000, -1.406856) |
| left | f8 | yes | (-1.000000, 0.000000, -0.000000) | (-1.260000, 1.000000, -1.406856); (-1.260000, 0.999953, -2.757828); (-1.260000, 0.874953, -2.757828) |
| left | f9 | no | (-1.000000, 0.000000, -0.000000) | (-1.260000, 0.999953, -2.757828); (-1.260000, 1.000000, -1.406856); (-1.260000, 1.125000, -1.406856) |
| left | f10 | yes | (0.000000, 0.995744, -0.092167) | (-0.980000, 0.999953, -2.757828); (-1.260000, 0.999953, -2.757828); (-1.260000, 1.125000, -1.406856) |
| left | f11 | no | (0.000000, 0.995744, -0.092167) | (-0.980000, 0.999953, -2.757828); (-1.260000, 1.125000, -1.406856); (-0.980000, 1.125000, -1.406856) |
| left | f130 | no | (0.000000, 0.039473, -0.999221) | (-0.480000, 0.125000, -2.792392); (-1.260000, 0.125000, -2.792392); (-1.260000, 0.999953, -2.757828) |
| left | f131 | no | (0.000000, 0.039473, -0.999221) | (-0.480000, 0.125000, -2.792392); (-1.260000, 0.999953, -2.757828); (-0.980000, 0.999953, -2.757828) |
| left | f132 | no | (-0.000003, 0.039472, -0.999221) | (-0.480000, 0.360000, -2.783109); (-0.480000, 0.125000, -2.792392); (-0.980000, 0.999953, -2.757828) |
| left | f133 | no | (0.000000, 0.039474, -0.999221) | (-0.980000, 0.999953, -2.757828); (-0.480000, 0.999953, -2.757828); (-0.480000, 0.540000, -2.775998) |
| left | f134 | no | (0.000000, 0.039474, -0.999221) | (-0.980000, 0.999953, -2.757828); (-0.480000, 0.540000, -2.775998); (-0.480000, 0.360000, -2.783109) |
| left | f142 | no | (0.000000, 0.995744, -0.092167) | (-0.480000, 0.999953, -2.757828); (-0.980000, 0.999953, -2.757828); (-0.980000, 1.125000, -1.406856) |
| right | f58 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 0.874953, -2.757828); (1.260000, -0.125000, -2.757828); (1.260000, 0.100803, -2.786276) |
| right | f59 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 0.874953, -2.757828); (1.260000, 0.100803, -2.786276); (1.260000, 0.125000, -2.792392) |
| right | f60 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 0.125000, -2.792392); (1.260000, 0.999953, -2.757828); (1.260000, 0.874953, -2.757828) |
| right | f61 | yes | (1.000000, 0.000000, 0.000000) | (1.260000, 0.874953, -2.757828); (1.260000, 0.999953, -2.757828); (1.260000, 1.000000, -1.406856) |
| right | f62 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 0.874953, -2.757828); (1.260000, 1.000000, -1.406856); (1.260000, -0.120000, -1.406856) |
| right | f63 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 0.874953, -2.757828); (1.260000, -0.120000, -1.406856); (1.260000, -0.120000, -2.320000) |
| right | f64 | no | (1.000000, 0.000000, 0.000000) | (1.260000, -0.120000, -2.320000); (1.260000, -0.125000, -2.757828); (1.260000, 0.874953, -2.757828) |
| right | f65 | no | (1.000000, 0.000000, 0.000000) | (1.260000, 1.125000, -1.406856); (1.260000, 1.000000, -1.406856); (1.260000, 0.999953, -2.757828) |
| right | f66 | yes | (0.000000, 0.995744, -0.092167) | (1.260000, 1.125000, -1.406856); (1.260000, 0.999953, -2.757828); (0.980000, 0.999953, -2.757828) |
| right | f67 | no | (0.000000, 0.995744, -0.092167) | (0.980000, 1.125000, -1.406856); (1.260000, 1.125000, -1.406856); (0.980000, 0.999953, -2.757828) |
| right | f136 | no | (0.000000, 0.039473, -0.999221) | (1.260000, 0.999953, -2.757828); (1.260000, 0.125000, -2.792392); (0.480000, 0.125000, -2.792392) |
| right | f137 | no | (0.000000, 0.039473, -0.999221) | (0.980000, 0.999953, -2.757828); (1.260000, 0.999953, -2.757828); (0.480000, 0.125000, -2.792392) |
| right | f138 | no | (0.000000, 0.039474, -0.999221) | (0.480000, 0.540000, -2.775998); (0.480000, 0.999953, -2.757828); (0.980000, 0.999953, -2.757828) |
| right | f139 | no | (0.000003, 0.039472, -0.999221) | (0.980000, 0.999953, -2.757828); (0.480000, 0.125000, -2.792392); (0.480000, 0.360000, -2.783109) |
| right | f140 | no | (0.000000, 0.039474, -0.999221) | (0.980000, 0.999953, -2.757828); (0.480000, 0.360000, -2.783109); (0.480000, 0.540000, -2.775998) |
| right | f143 | no | (0.000000, 0.995744, -0.092167) | (0.980000, 1.125000, -1.406856); (0.980000, 0.999953, -2.757828); (0.480000, 0.999953, -2.757828) |

## Preserved measurements

Envelope **5.800 × 2.520 × 2.440 m** (length × width × height), AABB **X [−1.26,1.26], Y [−0.27,2.17], Z [−2.90,2.90]**. Width stays **2.520 m** at all **27 review stations** and **53 vertex/mid-span sections**. All **104 exterior side triangles** and all eight pillar exteriors are flush at **X = ±1.260**.

Pillar geometry is unchanged. Its saved front endpoints are **(Y,Z) = (1.125,−1.25) → (1.920,−0.80)**, which measure **60.488501° above horizontal / 29.511499° from vertical**. The request's 60.9° is not the exact angle in `13bd2151`; this revision preserves those endpoints rather than changing the accepted rake. The roof's upper front remains **(Y,Z) = (2.170,−0.658491)**. Tailgate rake and all glass are unchanged.

| requested sedan hood Y station | sedan and saved wagon Y |
| ---: | ---: |
| −0.159 | −0.158648 |
| −0.125 | −0.125000 |
| 0.101 | 0.100803 |
| 0.125 | 0.125000 |
| 0.875 | 0.874953 |
| 1.000 | 1.000000 |
| 1.125 | 1.125000 |

The centre hood's Y/Z stations, longitudinal scale **0.964159181** and slope **5.2883°** remain intact. The two lower shoulder stations now describe flat side seams, as explained above. See [wagon_dimensions.md](wagon_dimensions.md).

## Verification

| check | result |
| --- | --- |
| saved topology | **177 positions, 378 triangles; 0 / 567 boundary edges (0.0%)**; **0 degenerate**, duplicate or overused faces/edges; every face has exactly **3 corners**. |
| surface audit | One connected outward shell; no coplanar overlaps or triangle piercings among **4,748 candidate pairs**; signed volume **12.930715 m³**. |
| `python3 tools/verify_wagon.py` | **PASS**: all 11 OBJ meshes; nose/hood/cowl planes; complete junction listing; dimensions, width, sedan stations, pillar/roof rake, closure, glass, cargo, lamps and registrations. |
| regression sensitivity | **PASS**: the new check rejects `13bd2151` directly for its nose top-edge step at **X = −1.26, Y = 0.874953**, expected **0.999953**. No aspect-ratio criterion is used. |
| `dotnet build game/UnturnedGodot.csproj` | **PASS**, 0 errors, 55.46 s; compilation reports **22 existing C# warnings** in untouched code. |
| `./test.sh --l1 --only 'vehicle.wagon*'` | **PASS — 1 test, 13 checks**, 0 failures, 0.88 s. Its incremental build is clean: **0 warnings / 0 errors**, 5.84 s. |
| reproducibility | **PASS**: regenerating all **12 wagon assets** produces identical bytes. |
| exterior bonnet-height renders | **PASS**: both sides rendered in Godot through the real vehicle harness and inspected; level leading edge and continuous hood/cowl at the pillar foot. See views below. |

## Exterior views at bonnet height

Godot/Vulkan captures of the regenerated asset, frame 48, parked with `UG_VSTATIC=1`. Cameras and look targets are vehicle-local with **eye Y = target Y = 1.125**. Both cameras are outside **X = ±1.260**, aimed toward the hood and pillar base.

| view | eye (X,Y,Z) | target (X,Y,Z) | capture |
| --- | --- | --- | --- |
| left, nose through pillar foot | (−3.5,1.125,−3.8) | (−1.0,1.125,−1.95) | [wagon_hood_left.png](wagon_hood_left.png) |
| right, nose through pillar foot | (+3.5,1.125,−3.8) | (+1.0,1.125,−1.95) | [wagon_hood_right.png](wagon_hood_right.png) |
| left, pillar-foot close-up | (−2.7,1.125,−2.4) | (−1.2,1.125,−1.35) | [wagon_pillar_foot_left.png](wagon_pillar_foot_left.png) |
| right, pillar-foot close-up | (+2.7,1.125,−2.4) | (+1.2,1.125,−1.35) | [wagon_pillar_foot_right.png](wagon_pillar_foot_right.png) |

The full-width nose edge has no dropped corner. The hood-to-cowl transition is a single straight line; the side wall continues into the pillar without the former diagonal hood-corner facet. The render harness exits successfully but logs rain-shader global warnings and a renderer-thread shutdown error after capture; these images verify appearance, not a warning-free render harness.

Reproduce a left-side capture (`UG_VCAM` mirrors X for the right):

```sh
mkdir -p /tmp/wagon-hood-shot
VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.aarch64.json \
UG_QUICK=1 UG_VSTATIC=1 UG_VCAM='-3.5,1.125,-3.8;-1.0,1.125,-1.95' \
xvfb-run -a ~/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64 \
  --path game --rendering-driver vulkan --write-movie /tmp/wagon-hood-shot/mov.avi \
  --fixed-fps 30 -- --vehicle=/tmp/wagon-hood-shot --gun=wagon
```

Reproduce the geometry and runtime checks:

```sh
python3 tools/build_wagon.py
python3 tools/verify_wagon.py
dotnet build game/UnturnedGodot.csproj
./test.sh --l1 --only 'vehicle.wagon*'
```

Pre-existing tracked build-artifact modifications are excluded from the commit. Nothing is pushed.
