# Fluid art measurements — before authoring

| Measurement | Method |
|---|---|
| Source | Worktree content at 827516d6; `python3 tools/measure_fluid_art.py`; OBJ records parsed directly |
| Units | 1 mesh unit = 1 m; table extents are local XYZ, not guessed world orientation |
| Vertices | Raw `v` includes seam duplicates; unique positions also listed; runtime ParseObj emits 3 vertices per triangle |
| Faces | First 3 corners, matching ParseObj; no triangulation of quads assumed |
| Rings | Exhaustive edge scan of all parsed meshes, any orientation; regular closed 5–64 segment polygons; 0 means none detected, not proof of no curved surface |
| Ring exclusions | Partial arcs, deformed/elliptical sections, fewer than 5 sides, missing perimeter edges; organic curved surfaces do not have a single radial count |
| Palette samples | Referenced corner UVs, repeat addressing, floor(U×W), floor((1−V)×H); PNG row 0 is top; distinct coordinates and RGB values counted separately |
| Texel centres | OBJ UV ((x+0.5)/W, 1−(y+0.5)/H); existing UVs need not be exact centres |

## Feature measurements and scale audit

| Mesh / code source | Measured feature | Dimensions (m) / observed records | Authoring constraint |
|---|---|---|---|
| Barrel_0.obj | Shell | radius 0.500000; axial span 0–1.250000; 12 segments | Tank/source: 12 segments |
| Barrel_0.obj | 3 separate bands | radius 0.543261; spans −0.046043–0.053957, 0.595819–0.695819, 1.175757–1.275757; each height 0.100000 | Model bands; radial projection 0.043261; no texture-only rim |
| Barrel_0.obj | Components | shell 24 triangles; each band 44; 156 total; 4 connected components after welding | Start tank/source budget at 156; account separately for functional fittings |
| Barrel_0.obj | Surface | 96 welded positions, 8 rings, no separate bolt-head component | No bolt arrays or added chamfers on drum bands |
| Generator_0.obj | Motor | 6 segments; radius 0.250000; axis Y from 0.233030 to 0.733030; length 0.500000 | Pump motor: 6 segments, 0.5 m diameter and length |
| Generator_0.obj | Components | 72 welded positions; 140 triangles; 3 welded components | Pump body + motor budget anchored to 140; add sockets separately |
| Propane_0.obj | Vessel | 8 segments; radius 0.250000; height 0.500000 | Small vessel reference: 8 segments |
| Propane_0.obj | Valve stem | component AABB 0.041160 × 0.041160 × 0.129090 (rounded to 0.000010 m) | 0.041160 m is the measured modelled feature floor among these components; use for stems/spokes, not sub-centimetre bolts |
| Propane_0.obj | Handle / guard | component AABB 0.208540 × 0.179290 × 0.183130 (same rounding) | Model handles/guards; no painted fake opening |
| Fire_Hydrant_0.obj | Side hex fitting | radius 0.152490; 6 segments; one transverse component spans 0.537118 | Hose fittings: 6 segments, radius 0.152490 |
| Fire_Hydrant_0.obj | Stem hex | radius 0.088470; span 0.975000–1.146615; 6 segments | Socket bore / valve shaft housing reference |
| Fire_Hydrant_0.obj | Domed body | radii 0.250000, 0.241481, 0.216506, 0.176777, 0.125000, 0.064705; 12 segments | A shoulder may have axial profile rings; radial subdivision stays 12 |
| Pipe_0.obj | Tube wall | outer radius 1.000000; inner 0.800000; length 3.000002; 12 segments | Hollow mouths must have actual inside faces |
| Gas_Pump_0.obj | Display/body | 40 welded positions, 52 tris; 4 sampled texels, 4 colours, 2×2 texture | Flat face colour areas can be UV palette regions; no texture-painted bolts in this palette |
| PlayerMovementDef.cs | Standing body / eye / crouch | 2.000000 / 1.750000 / 1.200000 | A 1.4 m device is 0.700000 standing body height; barrel is 0.660900 |
| DeployablePlacer / BarricadePlacer | Offset / Radius use | overlap sphere centre = surface + normal × Offset; sphere radius = Radius | Offset does not determine mesh ground contact; AABB minimum does |
| FluidDeploy.SpawnFor | Placed basis | Y yaw only; origin = contact point | Author Y-up with minimum Y=0; set Upright for matching ghost |
| MakeFluid (before change) | Size / Offset / Radius / Upright | (1,1.4,1) / 0.7 / 0.5 / false | 1.4 m tank size fits barrel scale; ghost was rotated to 1 m height because Upright was false |

| Device before authoring | ID | Def Size XYZ (m) | Offset / Radius (m) | Actual primitive geometry XYZ (m), Y range | HosePort local centres XYZ (m) | Mismatch |
|---|---:|---|---|---|---|---|
| Fluid Tank | 9110 | 1,1.4,1 | 0.7 / 0.5 | 1,1.4,1; 0–1.4 | (−0.5,0.7,0); (0.5,0.7,0) | Ghost stands up around X; runtime tank already Y-up |
| Water Source | 9111 | 1,1.4,1 | 0.7 / 0.5 | 1,1.4,1; 0–1.4 | (0,0.7,0.55) | Port centre 0.05 m beyond body; ghost orientation |
| Splitter | 9112 | 1,1.4,1 | 0.7 / 0.5 | 0.9,1.05,0.9; 0.025–1.075 | (−0.5,0.6,0); (0.5,0.6,−0.32); (0.5,0.6,0.32) | Size/ghost differ from collider |
| Combiner | 9113 | 1,1.4,1 | 0.7 / 0.5 | 0.9,1.05,0.9; 0.025–1.075 | (−0.5,0.6,−0.32); (−0.5,0.6,0.32); (0.5,0.6,0) | Size/ghost differ from collider |
| Pump | 9114 | 1,1.4,1 | 0.7 / 0.5 | 0.9,1.505,0.9; 0.025–1.53 | (−0.5,0.6,0); (0.5,0.6,0) | Motor reaches Y=1.53 outside both 1.4 m def and 1.075 m collider |
| Valve | 9115 | 1,1.4,1 | 0.7 / 0.5 | 0.9,1.235,0.9; 0.025–1.26 | (−0.5,0.6,0); (0.5,0.6,0) | Handle above collider; existing RefreshValveVisual changes colour only |
| Refinery | 9116 | 1,1.4,1 | 0.7 / 0.5 | 0.9,1.725,0.9; 0.025–1.75 | (−0.5,0.6,0); (0.5,0.6,0) | Stack outside def and collider |
| Sluice | 9117 | 1,1.4,1 | 0.7 / 0.5 | 0.9,1.725,0.9; 0.025–1.75 | (−0.5,0.6,0); (0.5,0.6,0) | Stack outside def and collider |
| Inlet | 9119 | 1,1.4,1 | 0.7 / 0.5 | 1,1.4,1; 0–1.4 | (0,0.7,0.55) | Same source cylinder; submerged placement depth 0.6–5 m |
| Drain | 9120 | 1,1.4,1 | 0.7 / 0.5 | 1,1.4,1; 0–1.4 | (0,0.7,0.55) | Same tank cylinder; ghost orientation |
| Purifier | 9121 | 1,1.4,1 | 0.7 / 0.5 | 0.9,1.725,0.9; 0.025–1.75 | (−0.5,0.6,0); (0.5,0.6,0) | Same transformer stack; power inlet (0,1.25,0.42) |

## Palette and LOD observations

| Mesh / population | Measurement | Consequence |
|---|---|---|
| 566 non-LOD object meshes with paired PNG | sampled texel count min 0 / median 4 / max 387 (0 = no referenced vt) | Use a 4-texel device palette, not one runtime AlbedoColor |
| Same 566 PNGs | 201 are 2×2; 140 are 4×2; 83 are 2×1; 21 are 1×1 | Palette dimensions vary; 2×2 is the most frequent |
| Barrel_0.obj + Barrel_0_tex.png | PNG 2×2, row 0 (87,104,118),(99,119,135); row 1 identical | 4 texels sampled but only 2 distinct RGB values |
| Barrel_0.obj component UV assignment | All 72 shell corners sample (99,119,135); all 396 band corners sample (87,104,118) | Light shell, dark bands; same assignment in the authored tank/source |
| Barrel_1.obj + Barrel_1_tex.png | PNG 2×2, row 0 (87,119,90),(99,135,102); row 1 identical | Water-source green swatches from measured art |
| Generator_0.obj + Generator_0_tex.png | PNG 2×2, rows (59,59,59),(213,167,44) / (59,59,59),(138,138,138) | Pump metal/yellow colours from measured art |
| Fire_Hydrant_0_tex.png | PNG 2×2: (162,32,32),(121,30,30), repeated row | Closed-valve red from measured art |
| Generator_0.obj real UV | (0.119209997,0.731643021) → loaded (0.119209997,0.268356979) → texel (0,0) → RGB (59,59,59) | Existing UV island lies inside texel; it is not an exact centre |
| Generator_0.obj all referenced vt | 0 / 224 exact centres | The repo does not require all legacy UVs to be centres; do not report that assumption as measurement |
| New 2×2 palette centre convention | top left OBJ (0.25,0.75) → loaded (0.25,0.25); top right (0.75,0.75); bottom left (0.25,0.25); bottom right (0.75,0.25) | Exact centre sampling is deterministic under the loader V flip |
| All non-LOD object OBJ regular rings | 12:398; 6:278; 8:182; 16:63; 5:45; 10:9 | Tank 12, small vessel 8, motor/hex fittings 6; no 32-sided ring detected |
| objects/lods.txt parsed records | 338 rows; 201 with 2+ heights; measured median first height 0.202940; last 0.028130 | Existing props do ship LODs; the file also contains cull-only entries |
| Barrel_0.obj → Barrel_0_lod1.obj | 156 → 20 tris; 12 → 6 sides; 3 bands removed | New tank/source need a 6-sided lower mesh with bands omitted |
| Fire_Hydrant_0.obj → Fire_Hydrant_0_lod1.obj | 200 → 42 tris; body 12 → 6 sides | Valve/socket detail can simplify at distance |
| Generator_0.obj → Generator_0_lod1.obj | 140 → 62 tris | Pump also needs a lower mesh; moving drum stays separate |
| Propane_0.obj → Propane_0_lod1.obj | 60 → 12 tris | Small vessels omit stem/guard detail in LOD1 |
| Barrel_0 retail LOD row | size 1.322; screen heights 0.20415,0.02774 | New devices use the barrel split height 0.20415 and their measured bounds |
| LodTable.DistanceForHeight | distance = size / (2 × h × tan(FOV/2)) × LodBias; SourceFov = 60°; LodBias = 2 + 3 × clamp(DrawDistance,0,1); default DrawDistance=1, bias=5 | Runtime distance uses this shared function; retain interaction/colliders independently of render LOD |
| Deployable.BuildMesh / FluidContainer before change | no authored lower meshes or visibility bands for these fluid devices | Adding files alone is insufficient; runtime must bind both mesh levels |

| Authoring decision, before vertices | Numeric basis |
|---|---|
| 1–1.4 m device triangle allocation | Near-size catalog median 88, maximum 552; barrel 156; hydrant 200; generator 140. Device totals must itemize any additions for hose mouths and moving parts. Whole-corpus median 56 is not a device budget. |
| Tank/source shell and bands | 12 radial segments; band thickness 0.100000 and projection 0.043261 copied from Barrel_0. Keep original 1.4 m envelope; reserve 0.043261 m around body for fixed hose anchors. |
| Motor and hose ports | Motor 6 sides, radius 0.25, length 0.5 from Generator_0; socket 6 sides, outer radius 0.152490, bore radius 0.088470 from hydrant. No bolt-head arrays. |
| Functional positions | Existing hose and power anchors determine fitting positions; stems/supports bridge from those anchors to body. These coordinates are engineering constraints, not claimed measurements of retail art. |

| Normal measurement (direct parse during render comparison) | Constant corner normals per triangle | Interpolated corner normals per triangle | Result |
|---|---:|---:|---|
| Barrel_0.obj | 60 | 96 | Interpolate radial normals on 12-sided shell/band sides; caps stay flat |
| Barrel_0_lod1.obj | 20 | 0 | Flat 6-sided LOD1 |
| Propane_0.obj | 44 | 16 | Interpolate 8-sided vessel sides |
| Propane_0_lod1.obj | 12 | 0 | Flat lower mesh |
| Generator_0.obj | 124 | 8 | Motor component's 16 selected triangles have constant normals; retain flat hex motor |
| Fire_Hydrant_0.obj | 56 | 144 | Body curvature uses interpolated corner normals; hex fittings stay flat |


| Population | Meshes | v min / median / max | tris min / median / max |
|---|---:|---|---|
| Catalog primary props | 551 | 4 / 196 / 6196 | 2 / 114 / 2973 |
| Catalog props: max dimension 0.75–1.75 m, min dimension ≥0.35 m | 63 | 40 / 140 / 1114 | 20 / 88 / 552 |
| All parsed OBJ/TXT (includes LODs, parts, vehicles, weapons) | 2010 | 4 / 94 / 11310 | 2 / 56 / 10690 |

## Comparables

| Mesh | v records | Unique positions | Tris | Local AABB size X,Y,Z (m) | Ring segments × rings | PNG W,H | Sampled texels | RGB colours | Centre UVs / referenced UVs |
|---|---:|---:|---:|---|---|---|---:|---:|---|
| objects/Barrel_0.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Barrel_1.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Barrel_2.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Canister_0.obj | 168 | 84 | 164 | 4.000000, 6.500005, 4.000004 | 12×7 | no paired PNG | — | — | — |
| objects/Canister_1.obj | 144 | 96 | 188 | 4.000000, 2.375005, 4.000000 | 12×8 | no paired PNG | — | — | — |
| objects/Fire_Hydrant_0.obj | 218 | 120 | 200 | 0.652202, 0.537118, 2.146615 | 6×4, 12×7 | [2, 2] | 4 | 2 | [0, 218] |
| objects/Gas_Pump_0.obj | 104 | 40 | 52 | 1.327953, 0.734164, 2.386103 | 0 detected | [2, 2] | 4 | 4 | [0, 104] |
| objects/Generator_0.obj | 224 | 72 | 140 | 1.500000, 1.713412, 1.339637 | 6×2 | [2, 2] | 3 | 3 | [0, 224] |
| objects/Pipe_0.obj | 96 | 48 | 96 | 2.000000, 3.000002, 2.000002 | 12×4 | no paired PNG | — | — | — |
| objects/Propane_0.obj | 125 | 40 | 60 | 0.500000, 0.500000, 0.584026 | 8×2 | [2, 1] | 2 | 2 | [0, 125] |
| objects/Tank_Fuel_0.obj | 238 | 92 | 140 | 5.625005, 2.500000, 3.347384 | 6×2, 12×4 | no paired PNG | — | — | — |
| objects/Tower_Water_0.obj | 286 | 210 | 340 | 7.499997, 7.499996, 14.900342 | 6×2, 12×11 | no paired PNG | — | — | — |

## Catalog near-size population

| Mesh | v records | Unique positions | Tris | Local AABB size X,Y,Z (m) | Ring segments × rings | PNG W,H | Sampled texels | RGB colours | Centre UVs / referenced UVs |
|---|---:|---:|---:|---|---|---|---:|---:|---|
| objects/Barbecue_0.obj | 207 | 80 | 102 | 1.624500, 1.624500, 1.222152 | 8×3 | [2, 2] | 3 | 3 | [0, 207] |
| objects/Barbecue_1.obj | 104 | 44 | 51 | 1.210600, 1.210600, 1.402815 | 0 detected | [2, 2] | 3 | 2 | [1, 101] |
| objects/Barrel_0.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Barrel_1.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Barrel_2.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Big_Snowman_06.obj | 140 | 56 | 70 | 1.437980, 0.404738, 1.226217 | 0 detected | [2, 2] | 1 | 1 | [0, 140] |
| objects/Big_Snowman_06_Off.obj | 140 | 56 | 70 | 1.437980, 0.404738, 1.226217 | 0 detected | no paired PNG | — | — | — |
| objects/Booth_1.obj | 94 | 32 | 54 | 1.000000, 1.000000, 0.757156 | 12×2 | [2, 2] | 3 | 2 | [0, 94] |
| objects/Candle_1_HW.obj | 337 | 88 | 136 | 0.381838, 0.780000, 1.400000 | 6×6 | [2, 1] | 2 | 2 | [0, 337] |
| objects/Cardboard_0.obj | 40 | 8 | 20 | 0.866674, 0.555716, 0.433656 | 0 detected | [4, 2] | 0 | 0 | [0, 0] |
| objects/Cardboard_1.obj | 40 | 8 | 20 | 1.137984, 0.857506, 0.569411 | 0 detected | [4, 2] | 0 | 0 | [0, 0] |
| objects/Cardboard_2.obj | 40 | 8 | 20 | 0.866674, 0.555716, 0.433656 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3.obj | 40 | 8 | 20 | 1.137984, 0.857506, 0.569411 | 0 detected | no paired PNG | — | — | — |
| objects/Coffee_0.obj | 164 | 64 | 90 | 1.200000, 0.760336, 0.816996 | 6×4 | [2, 2] | 4 | 4 | [0, 164] |
| objects/Computer_0.obj | 110 | 40 | 58 | 1.000000, 1.026764, 1.142733 | 0 detected | [2, 2] | 4 | 4 | [0, 110] |
| objects/Computer_1.obj | 84 | 32 | 42 | 0.521429, 0.974844, 1.056944 | 0 detected | [2, 2] | 4 | 4 | [0, 84] |
| objects/Computer_2.obj | 81 | 22 | 40 | 0.755340, 0.612736, 0.668520 | 0 detected | [2, 1] | 2 | 2 | [0, 81] |
| objects/Computer_4.obj | 84 | 32 | 42 | 0.521429, 0.974844, 1.056944 | 0 detected | [2, 2] | 4 | 4 | [0, 84] |
| objects/Control_0.obj | 140 | 56 | 70 | 1.000000, 0.983788, 1.697093 | 0 detected | [2, 2] | 3 | 3 | [0, 140] |
| objects/Control_1.obj | 230 | 112 | 166 | 1.000000, 0.700000, 1.250000 | 8×9 | [2, 2] | 4 | 4 | [0, 230] |
| objects/Control_2.obj | 230 | 92 | 114 | 1.000000, 0.700000, 1.250000 | 0 detected | [2, 2] | 4 | 4 | [0, 230] |
| objects/Control_3.obj | 361 | 142 | 179 | 1.000000, 0.700000, 1.257509 | 6×6 | [2, 2] | 4 | 4 | [0, 361] |
| objects/Control_4.obj | 220 | 72 | 94 | 1.000000, 0.700000, 1.592040 | 0 detected | [2, 2] | 3 | 3 | [0, 220] |
| objects/Cooler_1.obj | 105 | 32 | 46 | 0.750000, 0.750000, 1.250000 | 0 detected | [2, 2] | 4 | 4 | [0, 105] |
| objects/Cooler_2.obj | 78 | 36 | 60 | 0.656132, 0.656132, 0.798198 | 6×2, 12×2 | no paired PNG | — | — | — |
| objects/Cooler_Beach_0.obj | 96 | 32 | 48 | 1.000000, 1.635870, 0.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Cooler_Beach_1.obj | 96 | 32 | 48 | 1.000000, 1.635870, 0.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Cooler_Beach_2.obj | 96 | 32 | 48 | 1.000000, 1.635870, 0.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Counter_0.obj | 116 | 52 | 94 | 1.500000, 1.125000, 1.350000 | 0 detected | [2, 2] | 2 | 2 | [0, 116] |
| objects/Counter_2.obj | 116 | 52 | 94 | 1.500000, 1.125000, 1.350000 | 0 detected | [2, 2] | 2 | 2 | [0, 116] |
| objects/Craft_02.obj | 96 | 54 | 48 | 1.233136, 1.067928, 0.885731 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Crate_0.obj | 240 | 88 | 144 | 1.250000, 1.250000, 1.250000 | 0 detected | [2, 2] | 3 | 3 | [0, 240] |
| objects/DL_Garbage.obj | 80 | 28 | 40 | 0.943052, 0.921270, 1.416428 | 0 detected | no paired PNG | — | — | — |
| objects/Disher_0.obj | 70 | 24 | 34 | 1.500000, 1.375000, 1.500000 | 0 detected | [2, 2] | 2 | 1 | [0, 70] |
| objects/Dumpster_3.obj | 194 | 72 | 126 | 1.109044, 1.089526, 1.449326 | 12×4 | [2, 2] | 3 | 3 | [0, 194] |
| objects/Dumpster_4.obj | 194 | 72 | 126 | 1.109044, 1.089526, 1.449326 | 12×4 | [2, 2] | 3 | 3 | [0, 194] |
| objects/Garbage_0.obj | 80 | 28 | 40 | 0.943052, 0.921270, 1.416428 | 0 detected | no paired PNG | — | — | — |
| objects/Garbage_1.obj | 80 | 28 | 40 | 0.943052, 0.921270, 1.416428 | 0 detected | no paired PNG | — | — | — |
| objects/Mailbox_0.obj | 90 | 32 | 50 | 0.541979, 0.870346, 1.705032 | 0 detected | [2, 2] | 3 | 3 | [0, 90] |
| objects/Microwave_0.obj | 168 | 64 | 88 | 1.360062, 0.734373, 0.800000 | 0 detected | [2, 2] | 3 | 3 | [0, 168] |
| objects/Mushroom_Brown_0.obj | 320 | 100 | 180 | 0.974856, 0.600211, 0.909596 | 0 detected | [64, 64] | 11 | 11 | [0, 320] |
| objects/Mushroom_Red_0.obj | 420 | 140 | 230 | 1.012831, 0.579784, 0.956408 | 0 detected | [64, 64] | 20 | 10 | [0, 420] |
| objects/Oven_0.obj | 150 | 56 | 74 | 1.500000, 1.375000, 1.544034 | 0 detected | [2, 2] | 3 | 3 | [0, 150] |
| objects/Present_0_XMAS.obj | 250 | 68 | 124 | 0.750000, 0.750000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_1_XMAS.obj | 250 | 68 | 124 | 0.750000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_2_XMAS.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_3_XMAS.obj | 250 | 68 | 124 | 1.500000, 1.500000, 1.611485 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_4_XMAS.obj | 250 | 68 | 124 | 0.750000, 1.500000, 1.593256 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Pumpkin_0_HW.obj | 124 | 35 | 60 | 0.926667, 0.979456, 1.154210 | 0 detected | [2, 1] | 2 | 2 | [0, 124] |
| objects/Puzzle_Snowman_00.obj | 1114 | 353 | 552 | 1.582788, 0.885539, 1.612619 | 16×16 | [2, 2] | 3 | 3 | [0, 1114] |
| objects/Quest_Present_00.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_01.obj | 250 | 68 | 124 | 0.750000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_02.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_07.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_08.obj | 250 | 68 | 124 | 0.750000, 0.750000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Radio_0.obj | 124 | 48 | 62 | 1.000000, 0.537709, 1.068631 | 0 detected | [2, 2] | 4 | 4 | [0, 124] |
| objects/Radio_1.obj | 178 | 60 | 86 | 1.250000, 0.850000, 0.689856 | 6×2 | [2, 2] | 4 | 4 | [0, 178] |
| objects/Register_0.obj | 578 | 232 | 340 | 0.805676, 0.697631, 0.801505 | 0 detected | [2, 2] | 4 | 4 | [0, 578] |
| objects/Television_1.obj | 108 | 40 | 58 | 1.000000, 1.026764, 1.722421 | 0 detected | [2, 2] | 4 | 4 | [0, 108] |
| objects/Toaster_0.obj | 101 | 34 | 58 | 0.575076, 1.010361, 0.625010 | 0 detected | [2, 2] | 3 | 3 | [0, 101] |
| objects/Washer_0.obj | 94 | 32 | 60 | 1.500000, 1.500001, 1.500000 | 12×2 | [2, 2] | 1 | 1 | [0, 94] |
| objects/Wheel_0.obj | 108 | 48 | 92 | 0.400000, 1.186344, 1.186344 | 12×4 | [2, 1] | 2 | 2 | [0, 108] |
| objects/Wheel_3.obj | 168 | 72 | 140 | 0.500001, 0.782986, 0.782988 | 12×6 | [2, 1] | 2 | 2 | [0, 168] |

## All parsed meshes (one row per mesh)

| Mesh | v records | Unique positions | Tris | Local AABB size X,Y,Z (m) | Ring segments × rings | PNG W,H | Sampled texels | RGB colours | Centre UVs / referenced UVs |
|---|---:|---:|---:|---|---|---|---:|---:|---|
| objects/AC_0.obj | 130 | 64 | 78 | 2.700002, 1.800001, 1.920109 | 12×2 | [2, 2] | 4 | 3 | [0, 130] |
| objects/AC_0_lod1.obj | 118 | 52 | 60 | 2.700002, 1.800001, 1.920109 | 6×2 | no paired PNG | — | — | — |
| objects/AC_1.obj | 130 | 50 | 88 | 7.000000, 5.000000, 1.500000 | 0 detected | [2, 2] | 1 | 1 | [0, 130] |
| objects/AC_1_lod1.obj | 90 | 34 | 56 | 7.000000, 5.000000, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/APC_Desert.obj | 657 | 188 | 322 | 2.771646, 5.825002, 2.553529 | 6×8 | [4, 2] | 7 | 7 | [0, 657] |
| objects/APC_Desert_lod1.obj | 327 | 132 | 214 | 2.733385, 5.825002, 2.462151 | 6×4 | no paired PNG | — | — | — |
| objects/ATM_0.obj | 208 | 84 | 108 | 1.000000, 0.750000, 2.000000 | 0 detected | [2, 2] | 3 | 3 | [0, 208] |
| objects/ATM_0_lod1.obj | 28 | 12 | 18 | 1.000000, 0.750000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Agriculture_0.obj | 361 | 120 | 176 | 7.750000, 8.574019, 1.966792 | 0 detected | [2, 2] | 3 | 3 | [0, 361] |
| objects/Agriculture_0_lod1.obj | 117 | 40 | 56 | 7.750000, 8.574019, 1.966792 | 0 detected | no paired PNG | — | — | — |
| objects/Airport_0.obj | 1414 | 415 | 720 | 18.000006, 14.200001, 10.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1414] |
| objects/Airport_0_lod1.obj | 1216 | 353 | 626 | 18.000006, 14.200001, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Airport_0_lod2.obj | 326 | 89 | 191 | 18.000006, 14.000006, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Ambulance.obj | 737 | 270 | 462 | 2.693116, 5.509786, 2.551550 | 0 detected | [4, 2] | 7 | 7 | [0, 737] |
| objects/Ambulance_lod1.obj | 355 | 154 | 230 | 2.693116, 5.509786, 2.528379 | 0 detected | no paired PNG | — | — | — |
| objects/Apartment_0.obj | 2774 | 667 | 1388 | 21.075523, 20.100003, 25.000000 | 0 detected | [4, 2] | 6 | 6 | [0, 2774] |
| objects/Apartment_0_lod1.obj | 618 | 267 | 616 | 21.000007, 20.000008, 25.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Apartment_1.obj | 2348 | 579 | 1146 | 20.100008, 22.100000, 15.500000 | 0 detected | [4, 2] | 6 | 6 | [0, 2348] |
| objects/Apartment_1_lod1.obj | 807 | 273 | 568 | 20.000000, 22.000000, 15.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Apartment_2.obj | 5246 | 1276 | 2623 | 18.200004, 23.600004, 20.250000 | 0 detected | [4, 2] | 6 | 6 | [0, 5246] |
| objects/Apartment_2_lod1.obj | 1235 | 475 | 1083 | 18.000007, 23.500009, 20.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Apartment_3.obj | 5054 | 1256 | 2632 | 16.699850, 16.099998, 21.250000 | 0 detected | [4, 2] | 7 | 7 | [0, 5054] |
| objects/Apartment_3_lod1.obj | 1129 | 468 | 1072 | 16.501476, 16.020195, 21.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Bank_0.obj | 1201 | 469 | 846 | 22.102276, 22.104822, 11.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1201] |
| objects/Bank_0_lod1.obj | 1103 | 417 | 766 | 22.102276, 22.104822, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Bank_0_lod2.obj | 369 | 135 | 277 | 22.000006, 22.000006, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Barbecue_0.obj | 207 | 80 | 102 | 1.624500, 1.624500, 1.222152 | 8×3 | [2, 2] | 3 | 3 | [0, 207] |
| objects/Barbecue_0_lod1.obj | 111 | 32 | 54 | 1.624500, 1.624500, 1.222152 | 8×3 | no paired PNG | — | — | — |
| objects/Barbecue_1.obj | 104 | 44 | 51 | 1.210600, 1.210600, 1.402815 | 0 detected | [2, 2] | 3 | 2 | [1, 101] |
| objects/Barbecue_1_Ragdoll_0.obj | 140 | 52 | 70 | 1.210510, 1.210510, 1.764985 | 0 detected | no paired PNG | — | — | — |
| objects/Barbecue_1_lid.obj | 44 | 20 | 21 | 1.210600, 1.210600, 0.362191 | 0 detected | no paired PNG | — | — | — |
| objects/Barbecue_1_lod1.obj | 88 | 44 | 43 | 1.210600, 1.210600, 1.402815 | 0 detected | no paired PNG | — | — | — |
| objects/Barbedwire_0.obj | 48 | 12 | 24 | 0.682528, 0.213291, 0.184714 | 6×2 | [32, 16] | 6 | 1 | [0, 48] |
| objects/Barn_0.obj | 683 | 275 | 468 | 15.000007, 21.048854, 15.928185 | 0 detected | [4, 2] | 8 | 8 | [0, 683] |
| objects/Barn_0_lod1.obj | 444 | 173 | 314 | 15.000007, 21.048854, 15.928185 | 0 detected | no paired PNG | — | — | — |
| objects/Barn_0_lod2.obj | 216 | 70 | 146 | 15.000007, 20.000010, 15.928185 | 0 detected | no paired PNG | — | — | — |
| objects/Barn_1.obj | 419 | 130 | 209 | 22.365718, 17.000007, 24.500000 | 0 detected | [4, 2] | 8 | 8 | [0, 419] |
| objects/Barn_1_lod1.obj | 255 | 84 | 141 | 21.865709, 16.000006, 24.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Barracks_0.obj | 352 | 112 | 230 | 10.000002, 17.000003, 11.000001 | 0 detected | [4, 2] | 5 | 5 | [0, 352] |
| objects/Barracks_0_lod1.obj | 203 | 62 | 138 | 10.000002, 16.000008, 11.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Barracks_1.obj | 1311 | 330 | 681 | 12.200006, 18.200002, 11.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1311] |
| objects/Barracks_1_lod1.obj | 522 | 140 | 308 | 12.000005, 18.000014, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Barracks_2.obj | 352 | 112 | 230 | 10.000002, 17.000003, 11.000001 | 0 detected | [4, 2] | 5 | 5 | [0, 352] |
| objects/Barracks_2_lod1.obj | 135 | 47 | 108 | 10.000002, 17.000003, 10.500001 | 0 detected | no paired PNG | — | — | — |
| objects/Barracks_3.obj | 1311 | 330 | 681 | 12.200006, 18.200002, 11.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1311] |
| objects/Barracks_3_lod1.obj | 324 | 119 | 267 | 12.000005, 18.000014, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Barrel_0.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Barrel_0_lod1.obj | 36 | 12 | 20 | 0.866026, 1.000000, 1.250000 | 6×2 | no paired PNG | — | — | — |
| objects/Barrel_1.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Barrel_1_lod1.obj | 36 | 12 | 20 | 0.866026, 1.000000, 1.250000 | 6×2 | no paired PNG | — | — | — |
| objects/Barrel_2.obj | 192 | 96 | 156 | 1.086522, 1.086522, 1.321800 | 12×8 | [2, 2] | 4 | 2 | [0, 192] |
| objects/Barrel_2_lod1.obj | 36 | 12 | 20 | 0.866026, 1.000000, 1.250000 | 6×2 | no paired PNG | — | — | — |
| objects/Baseball_0.obj | 127 | 98 | 145 | 44.277669, 44.277680, 0.300000 | 12×1 | [64, 64] | 101 | 64 | [0, 127] |
| objects/Baseball_0_lod1.obj | 49 | 49 | 47 | 44.277669, 44.277680, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Battery_0.obj | 84 | 28 | 38 | 0.565000, 0.350000, 0.460000 | 5×4 | [2, 2] | 3 | 3 | [0, 84] |
| objects/Battery_00.obj | 196 | 76 | 104 | 0.317861, 0.367034, 0.736031 | 6×10 | [2, 2] | 4 | 2 | [0, 196] |
| objects/Battery_00_Off.obj | 36 | 12 | 20 | 0.272851, 0.315062, 0.676333 | 6×2 | [1, 1] | 1 | 1 | [0, 36] |
| objects/Battery_00_Off_lod1.obj | 24 | 8 | 12 | 0.272851, 0.157530, 0.676333 | 0 detected | no paired PNG | — | — | — |
| objects/Battery_00_lod1.obj | 12 | 12 | 12 | 0.272851, 0.315062, 0.577345 | 6×2 | no paired PNG | — | — | — |
| objects/Battery_01.obj | 196 | 76 | 104 | 0.317861, 0.367034, 0.736031 | 6×10 | [2, 2] | 4 | 2 | [0, 196] |
| objects/Battery_01_lod1.obj | 12 | 12 | 12 | 0.272851, 0.315062, 0.577345 | 6×2 | no paired PNG | — | — | — |
| objects/Battery_0_lod1.obj | 30 | 18 | 18 | 0.565000, 0.350000, 0.385000 | 5×2 | no paired PNG | — | — | — |
| objects/Bed_0.obj | 206 | 80 | 108 | 3.037042, 3.888310, 1.665334 | 0 detected | [4, 2] | 5 | 5 | [0, 206] |
| objects/Bed_0_lod1.obj | 134 | 52 | 68 | 2.916232, 3.888310, 1.665334 | 0 detected | no paired PNG | — | — | — |
| objects/Bed_1.obj | 186 | 72 | 98 | 2.004448, 3.888310, 1.665334 | 0 detected | [4, 2] | 5 | 5 | [0, 186] |
| objects/Bed_1_lod1.obj | 134 | 52 | 68 | 1.924714, 3.888310, 1.665334 | 0 detected | no paired PNG | — | — | — |
| objects/Bed_2.obj | 256 | 96 | 128 | 1.805514, 3.745402, 2.915334 | 0 detected | [4, 2] | 6 | 5 | [0, 256] |
| objects/Bed_2_lod1.obj | 168 | 64 | 84 | 1.805514, 3.745402, 2.915334 | 0 detected | no paired PNG | — | — | — |
| objects/Bed_3_Empty.obj | 124 | 48 | 62 | 1.805514, 3.745402, 0.918221 | 0 detected | [4, 2] | 6 | 5 | [0, 124] |
| objects/Bed_3_Empty_lod1.obj | 80 | 32 | 40 | 1.805514, 3.745402, 0.883081 | 0 detected | no paired PNG | — | — | — |
| objects/Bench_Wood_0.obj | 204 | 72 | 128 | 2.500001, 1.330198, 1.765023 | 0 detected | [2, 2] | 4 | 2 | [0, 204] |
| objects/Bench_Wood_0_Debris.obj | 40 | 16 | 20 | 2.025000, 0.158798, 0.366060 | 0 detected | no paired PNG | — | — | — |
| objects/Bench_Wood_0_Ragdoll_0.obj | 204 | 72 | 128 | 2.500001, 1.330198, 1.765023 | 0 detected | no paired PNG | — | — | — |
| objects/Bench_Wood_1.obj | 184 | 72 | 92 | 2.725853, 3.125001, 1.187500 | 0 detected | [2, 2] | 2 | 2 | [0, 184] |
| objects/Bench_Wood_1_lod1.obj | 72 | 24 | 36 | 2.725853, 3.125001, 0.604642 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_00.obj | 192 | 58 | 98 | 3.982724, 4.030643, 2.461634 | 16×2 | [2, 2] | 1 | 1 | [0, 192] |
| objects/Big_Snowman_00_Off.obj | 192 | 58 | 98 | 3.982724, 4.030643, 2.461634 | 16×2 | no paired PNG | — | — | — |
| objects/Big_Snowman_00_Off_lod1.obj | 127 | 43 | 68 | 3.877235, 3.877236, 2.196277 | 16×2 | no paired PNG | — | — | — |
| objects/Big_Snowman_00_lod1.obj | 127 | 43 | 68 | 3.877235, 3.877236, 2.196277 | 16×2 | no paired PNG | — | — | — |
| objects/Big_Snowman_01.obj | 475 | 143 | 236 | 4.030642, 4.030643, 4.787138 | 16×6 | [2, 2] | 1 | 1 | [0, 475] |
| objects/Big_Snowman_01_Off.obj | 324 | 105 | 160 | 3.734346, 3.877235, 3.362091 | 16×2 | no paired PNG | — | — | — |
| objects/Big_Snowman_02.obj | 864 | 257 | 432 | 4.030642, 4.030643, 7.340040 | 16×16 | [2, 2] | 1 | 1 | [0, 864] |
| objects/Big_Snowman_02_Off.obj | 437 | 132 | 216 | 3.319473, 3.417955, 3.826413 | 16×7 | no paired PNG | — | — | — |
| objects/Big_Snowman_03.obj | 50 | 16 | 20 | 7.204258, 0.341372, 1.099160 | 0 detected | [2, 2] | 1 | 1 | [0, 50] |
| objects/Big_Snowman_03_Off.obj | 50 | 16 | 20 | 7.204258, 0.341372, 1.099160 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_03_Off_lod1.obj | 11 | 11 | 10 | 7.204258, 0.341372, 1.099160 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_03_lod1.obj | 11 | 11 | 10 | 7.204258, 0.341372, 1.099160 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_04.obj | 20 | 8 | 10 | 0.693399, 2.009676, 0.693399 | 0 detected | [2, 2] | 1 | 1 | [0, 20] |
| objects/Big_Snowman_04_Off.obj | 20 | 8 | 10 | 0.693399, 2.009676, 0.693399 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_05.obj | 60 | 24 | 30 | 0.250340, 0.386011, 1.435915 | 0 detected | [2, 2] | 1 | 1 | [0, 60] |
| objects/Big_Snowman_05_Off.obj | 60 | 24 | 30 | 0.250340, 0.386011, 1.435915 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_06.obj | 140 | 56 | 70 | 1.437980, 0.404738, 1.226217 | 0 detected | [2, 2] | 1 | 1 | [0, 140] |
| objects/Big_Snowman_06_Off.obj | 140 | 56 | 70 | 1.437980, 0.404738, 1.226217 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_06_Off_lod1.obj | 36 | 16 | 20 | 1.066698, 0.382899, 0.349251 | 0 detected | no paired PNG | — | — | — |
| objects/Big_Snowman_06_lod1.obj | 36 | 16 | 20 | 1.066698, 0.382899, 0.349251 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_0.obj | 1944 | 440 | 648 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_0_Debris.obj | 42 | 24 | 34 | 0.750000, 0.750000, 1.500000 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_0_Ragdoll_0.obj | 1020 | 440 | 648 | 8.815319, 1.232745, 12.285286 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_0_lod1.obj | 812 | 324 | 464 | 8.815319, 1.232745, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_0_lod2.obj | 64 | 24 | 32 | 8.815319, 1.232745, 12.285287 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_1.obj | 1357 | 610 | 893 | 8.815319, 1.232745, 12.285286 | 12×2 | [2, 2] | 4 | 4 | [0, 1357] |
| objects/Billboard_10.obj | 3762 | 860 | 1254 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_10_lod1.obj | 16 | 16 | 14 | 8.000000, 0.350000, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_11.obj | 5097 | 1149 | 1699 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_11_lod1.obj | 8 | 8 | 6 | 8.000000, 0.670244, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_12.obj | 2418 | 544 | 806 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_12_lod1.obj | 9 | 9 | 8 | 8.815319, 1.000001, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_13.obj | 2706 | 613 | 902 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_13_lod1.obj | 17 | 17 | 16 | 8.000000, 0.250000, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_14.obj | 3078 | 712 | 1026 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_14_lod1.obj | 18 | 18 | 16 | 8.000000, 0.857744, 12.285286 | 12×1 | no paired PNG | — | — | — |
| objects/Billboard_15.obj | 1818 | 426 | 606 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_15_lod1.obj | 39 | 39 | 36 | 8.000000, 0.907744, 12.285286 | 12×1 | no paired PNG | — | — | — |
| objects/Billboard_16.obj | 2358 | 548 | 786 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_16_lod1.obj | 18 | 18 | 16 | 8.000000, 0.857744, 12.285286 | 12×1 | no paired PNG | — | — | — |
| objects/Billboard_1_lod1.obj | 122 | 50 | 68 | 8.815319, 1.182505, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_2.obj | 1541 | 678 | 1001 | 8.815319, 1.232745, 12.285286 | 12×2 | [2, 2] | 4 | 4 | [0, 1541] |
| objects/Billboard_2_lod1.obj | 5 | 3 | 2 | 0.137259, 0.649519, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_3.obj | 1914 | 432 | 638 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_3_Debris.obj | 42 | 24 | 34 | 0.750000, 0.750000, 1.500000 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_3_Ragdoll_0.obj | 942 | 432 | 638 | 8.815319, 1.232745, 12.285286 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_3_lod1.obj | 714 | 292 | 418 | 8.815319, 1.232745, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_3_lod2.obj | 64 | 24 | 32 | 8.815319, 1.232745, 12.285287 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_4.obj | 2271 | 514 | 757 | 8.815319, 1.232745, 12.285286 | 12×5 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_4_Debris.obj | 42 | 24 | 34 | 0.750000, 0.750000, 1.500000 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_4_Ragdoll_0.obj | 1139 | 514 | 757 | 8.815319, 1.232745, 12.285286 | 12×5 | no paired PNG | — | — | — |
| objects/Billboard_4_lod1.obj | 876 | 364 | 522 | 8.815319, 1.232745, 12.285286 | 12×3 | no paired PNG | — | — | — |
| objects/Billboard_4_lod2.obj | 64 | 24 | 32 | 8.815319, 1.232745, 12.285287 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_5.obj | 2796 | 620 | 932 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_5_Debris.obj | 42 | 24 | 34 | 0.750000, 0.750000, 1.500000 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_5_Ragdoll_0.obj | 1388 | 620 | 932 | 8.815319, 1.232745, 12.285286 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_5_lod1.obj | 1067 | 412 | 610 | 8.815319, 1.232745, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_5_lod2.obj | 64 | 24 | 32 | 8.815319, 1.232745, 12.285287 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_6.obj | 1272 | 280 | 424 | 8.815319, 1.232745, 12.285286 | 12×2 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_6_Debris.obj | 42 | 24 | 34 | 0.750000, 0.750000, 1.500000 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_6_Ragdoll_0.obj | 670 | 280 | 424 | 8.815319, 1.232745, 12.285286 | 12×2 | no paired PNG | — | — | — |
| objects/Billboard_6_lod1.obj | 545 | 196 | 288 | 8.815319, 1.232745, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_6_lod2.obj | 64 | 24 | 32 | 8.815319, 1.232745, 12.285287 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_7.obj | 1276 | 560 | 832 | 8.815319, 1.232745, 12.285286 | 12×2 | [2, 2] | 4 | 4 | [0, 1276] |
| objects/Billboard_7_lod1.obj | 10 | 4 | 4 | 0.512259, 0.649519, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_8.obj | 1743 | 760 | 1120 | 8.815319, 1.232745, 12.285286 | 12×2 | [2, 2] | 4 | 4 | [0, 1743] |
| objects/Billboard_8_lod1.obj | 65 | 30 | 36 | 8.815319, 1.182505, 12.285286 | 0 detected | no paired PNG | — | — | — |
| objects/Billboard_9.obj | 4809 | 1094 | 1603 | 8.815319, 1.232745, 12.285286 | 12×6 | [1, 1] | 0 | 0 | [0, 0] |
| objects/Billboard_9_lod1.obj | 5 | 5 | 4 | 8.000000, 0.250000, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Biodome_0.obj | 855 | 324 | 778 | 59.999994, 57.063383, 30.000005 | 5×10 | no paired PNG | — | — | — |
| objects/Biodome_0_glass.obj | 66 | 36 | 96 | 59.499998, 56.587858, 29.750006 | 5×5 | no paired PNG | — | — | — |
| objects/Birch_0.obj | 286 | 152 | 230 | 1.635845, 18.464679, 4.553280 | 6×3, 12×2 | [128, 32] | 187 | 2 | [0, 286] |
| objects/Birch_0_foliage.obj | 446 | 380 | 304 | 18.098717, 16.015324, 18.271272 | 0 detected | [128, 64] | 6 | 6 | [0, 446] |
| objects/Birch_0_foliage_lod1.obj | 184 | 184 | 110 | 18.098717, 16.015325, 18.271272 | 0 detected | no paired PNG | — | — | — |
| objects/Birch_0_lod1.obj | 106 | 40 | 64 | 1.136701, 18.464678, 4.181394 | 12×1 | no paired PNG | — | — | — |
| objects/Birch_1.obj | 315 | 171 | 268 | 4.314701, 17.865680, 5.251627 | 6×4, 12×2 | [128, 32] | 209 | 2 | [0, 315] |
| objects/Birch_1_foliage.obj | 471 | 405 | 324 | 17.609816, 15.449578, 18.170804 | 0 detected | [128, 64] | 6 | 4 | [0, 471] |
| objects/Birch_1_foliage_lod1.obj | 125 | 125 | 76 | 16.813504, 14.454494, 18.170804 | 0 detected | no paired PNG | — | — | — |
| objects/Birch_1_lod1.obj | 76 | 35 | 56 | 3.515853, 13.058983, 4.173109 | 12×1 | no paired PNG | — | — | — |
| objects/Blade_0.obj | 168 | 60 | 120 | 3.482964, 3.482963, 0.100000 | 12×4 | no paired PNG | — | — | — |
| objects/Blade_0_lod1.obj | 72 | 24 | 48 | 2.598076, 3.000000, 0.100000 | 6×4 | no paired PNG | — | — | — |
| objects/Bleacher_0.obj | 634 | 198 | 348 | 16.000000, 9.000000, 3.812500 | 0 detected | [2, 2] | 3 | 3 | [0, 634] |
| objects/Bleacher_0_lod1.obj | 174 | 72 | 120 | 16.000000, 9.000000, 3.375000 | 0 detected | no paired PNG | — | — | — |
| objects/Block_Parking_0.obj | 32 | 12 | 20 | 3.000001, 0.500000, 0.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Block_Parking_0_lod1.obj | 24 | 8 | 12 | 3.000001, 0.500000, 0.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Block_Road_0.obj | 24 | 8 | 12 | 0.500000, 3.000001, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Block_Road_1.obj | 24 | 8 | 12 | 0.500000, 16.000006, 1.250000 | 0 detected | [2, 1] | 2 | 2 | [0, 24] |
| objects/Board_0.obj | 70 | 20 | 36 | 3.500002, 0.400000, 2.500000 | 0 detected | [2, 1] | 2 | 2 | [0, 70] |
| objects/Board_0_Ragdoll_0.obj | 70 | 20 | 36 | 3.500002, 0.400000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Board_0_lod1.obj | 24 | 11 | 18 | 3.500002, 0.400000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Boat_0.obj | 241 | 104 | 168 | 2.840002, 6.704341, 1.752758 | 0 detected | [2, 2] | 3 | 2 | [0, 241] |
| objects/Boat_0_lod1.obj | 161 | 72 | 104 | 2.640002, 6.502546, 1.652766 | 0 detected | no paired PNG | — | — | — |
| objects/Boat_1.obj | 192 | 70 | 120 | 1.840002, 8.703805, 1.610713 | 0 detected | [2, 2] | 3 | 2 | [0, 192] |
| objects/Boat_1_lod1.obj | 119 | 46 | 72 | 1.640002, 8.502546, 1.510717 | 0 detected | no paired PNG | — | — | — |
| objects/Boat_2.obj | 209 | 58 | 112 | 1.813308, 8.002546, 1.534551 | 0 detected | [2, 2] | 3 | 2 | [0, 209] |
| objects/Boat_2_lod1.obj | 81 | 26 | 48 | 1.813308, 8.002546, 1.510717 | 0 detected | no paired PNG | — | — | — |
| objects/Bollard_0.obj | 28 | 12 | 18 | 0.312500, 0.312500, 2.375000 | 0 detected | no paired PNG | — | — | — |
| objects/Bollard_0_lod1.obj | 7 | 7 | 8 | 0.312500, 0.312500, 2.296750 | 0 detected | no paired PNG | — | — | — |
| objects/Book_Black.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Book_Blue.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Book_Green.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Book_Orange.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Book_Purple.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Book_Red.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Book_White.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Book_Yellow.obj | 40 | 12 | 20 | 0.525592, 0.200000, 0.627420 | 0 detected | [2, 1] | 2 | 2 | [0, 40] |
| objects/Booth_0.obj | 116 | 48 | 68 | 4.000007, 4.000004, 1.507156 | 0 detected | [2, 2] | 4 | 3 | [0, 116] |
| objects/Booth_1.obj | 94 | 32 | 54 | 1.000000, 1.000000, 0.757156 | 12×2 | [2, 2] | 3 | 2 | [0, 94] |
| objects/Booth_1_Ragdoll_0.obj | 94 | 32 | 54 | 1.000000, 1.000000, 0.757156 | 12×2 | no paired PNG | — | — | — |
| objects/Booth_1_lod1.obj | 52 | 20 | 28 | 0.866026, 1.000000, 0.757156 | 6×2 | no paired PNG | — | — | — |
| objects/Botanist_0.obj | 1462 | 447 | 717 | 14.000005, 14.100002, 10.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1462] |
| objects/Botanist_0_lod1.obj | 1111 | 333 | 546 | 14.000005, 14.100002, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Botanist_0_lod2.obj | 213 | 61 | 124 | 14.000005, 14.000006, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_00.obj | 131 | 35 | 66 | 19.250378, 18.984709, 17.283164 | 0 detected | [64, 64] | 72 | 8 | [0, 131] |
| objects/Boulder_00_lod1.obj | 28 | 10 | 16 | 18.434426, 18.984709, 14.885006 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_01.obj | 88 | 81 | 128 | 33.809317, 33.763241, 5.827096 | 0 detected | [64, 64] | 76 | 11 | [0, 88] |
| objects/Boulder_01_lod1.obj | 33 | 33 | 32 | 33.809317, 33.763241, 4.958410 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_02.obj | 348 | 83 | 154 | 15.344977, 17.404661, 8.997639 | 0 detected | [64, 64] | 172 | 10 | [0, 348] |
| objects/Boulder_02_lod1.obj | 28 | 10 | 16 | 7.637110, 8.262667, 7.215577 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_03.obj | 69 | 28 | 52 | 20.000000, 30.000000, 20.000000 | 0 detected | [64, 64] | 57 | 12 | [0, 69] |
| objects/Boulder_03_lod1.obj | 24 | 8 | 12 | 20.000000, 30.000000, 17.758717 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_04.obj | 62 | 24 | 44 | 31.064967, 48.000000, 32.000000 | 0 detected | [64, 64] | 53 | 11 | [0, 62] |
| objects/Boulder_04_lod1.obj | 24 | 8 | 12 | 31.064967, 48.000000, 22.209543 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_06.obj | 131 | 35 | 66 | 19.250378, 18.984709, 17.283164 | 0 detected | [64, 64] | 72 | 11 | [0, 131] |
| objects/Boulder_06_lod1.obj | 28 | 10 | 16 | 18.434426, 18.984709, 14.885006 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_08.obj | 46 | 18 | 32 | 8.860615, 10.175721, 4.500000 | 0 detected | [64, 64] | 44 | 12 | [0, 46] |
| objects/Boulder_08_lod1.obj | 28 | 10 | 16 | 7.977168, 10.175721, 4.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_09.obj | 55 | 22 | 40 | 7.340264, 11.752380, 7.340264 | 0 detected | [64, 64] | 48 | 11 | [0, 55] |
| objects/Boulder_09_lod1.obj | 24 | 8 | 12 | 7.340263, 9.825155, 7.340263 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_10.obj | 66 | 34 | 64 | 34.000002, 23.270672, 23.663011 | 0 detected | [64, 64] | 64 | 10 | [0, 66] |
| objects/Boulder_10_lod1.obj | 28 | 10 | 16 | 33.000002, 22.000000, 22.290482 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_11.obj | 131 | 35 | 66 | 19.250378, 18.984709, 17.283164 | 0 detected | [64, 64] | 72 | 15 | [0, 131] |
| objects/Boulder_11_lod1.obj | 28 | 10 | 16 | 18.434426, 18.984709, 14.885006 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_12.obj | 88 | 81 | 128 | 33.809317, 33.763241, 5.827096 | 0 detected | [64, 64] | 76 | 15 | [0, 88] |
| objects/Boulder_12_lod1.obj | 33 | 33 | 32 | 33.809317, 33.763241, 4.958410 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_13.obj | 348 | 83 | 154 | 15.344977, 17.404661, 8.997639 | 0 detected | [64, 64] | 172 | 20 | [0, 348] |
| objects/Boulder_13_lod1.obj | 28 | 10 | 16 | 7.637110, 8.262667, 7.215577 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_22.obj | 52 | 24 | 44 | 18.773947, 31.134150, 9.170863 | 0 detected | [64, 64] | 51 | 9 | [0, 52] |
| objects/Boulder_22_PEI.obj | 52 | 24 | 44 | 18.773947, 31.134150, 9.170863 | 0 detected | [64, 64] | 51 | 12 | [0, 52] |
| objects/Boulder_22_PEI_lod1.obj | 24 | 8 | 12 | 16.597060, 31.134150, 6.512296 | 0 detected | no paired PNG | — | — | — |
| objects/Boulder_22_Russia.obj | 52 | 24 | 44 | 18.773947, 31.134150, 9.170863 | 0 detected | [64, 64] | 51 | 12 | [0, 52] |
| objects/Boulder_22_Russia_lod1.obj | 24 | 8 | 12 | 16.597060, 31.134150, 6.512296 | 0 detected | no paired PNG | — | — | — |
| objects/Bricks_0.obj | 160 | 64 | 80 | 2.638450, 2.798571, 0.409506 | 0 detected | [2, 2] | 4 | 4 | [0, 160] |
| objects/Bridge_Line_0.obj | 60 | 28 | 34 | 17.000000, 48.000008, 48.250000 | 0 detected | [256, 512] | 5 | 2 | [0, 60] |
| objects/Bridge_Line_1.obj | 76 | 40 | 40 | 17.000000, 48.000008, 54.229439 | 0 detected | [256, 512] | 4 | 2 | [0, 76] |
| objects/Bridge_Line_2.obj | 491 | 125 | 254 | 17.000010, 32.000008, 33.749996 | 0 detected | [2, 1] | 2 | 2 | [0, 491] |
| objects/Bridge_Line_Broken_0.obj | 219 | 100 | 160 | 17.776189, 48.000008, 48.250000 | 0 detected | [256, 512] | 32 | 2 | [0, 219] |
| objects/Bridge_Line_Broken_0_lod1.obj | 18 | 10 | 12 | 14.609310, 7.527694, 36.573521 | 0 detected | no paired PNG | — | — | — |
| objects/Bridge_Line_Broken_1.obj | 363 | 151 | 258 | 17.930390, 48.000008, 54.229439 | 0 detected | [256, 512] | 43 | 2 | [0, 363] |
| objects/Bridge_Line_Broken_1_lod1.obj | 5 | 5 | 4 | 3.750000, 3.750000, 50.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Bridge_Line_Cap_0.obj | 84 | 32 | 46 | 17.000000, 50.000008, 32.250000 | 0 detected | [256, 512] | 12 | 2 | [0, 84] |
| objects/Bridge_Line_Cap_1.obj | 156 | 72 | 92 | 17.000001, 98.000004, 54.229439 | 0 detected | [256, 512] | 9 | 2 | [0, 156] |
| objects/Bridge_Line_Cap_1_lod1.obj | 72 | 30 | 40 | 17.000001, 98.000004, 21.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Bridge_Line_Cap_2.obj | 500 | 125 | 258 | 17.000010, 32.000008, 33.749996 | 0 detected | [2, 1] | 2 | 2 | [0, 500] |
| objects/Bridge_Turn_1.obj | 182 | 100 | 160 | 44.500026, 44.500015, 54.229439 | 0 detected | [256, 128] | 18 | 2 | [0, 182] |
| objects/Bridge_Turn_1_lod1.obj | 84 | 60 | 80 | 44.500026, 44.500015, 54.229440 | 0 detected | no paired PNG | — | — | — |
| objects/Bunker_0.obj | 1206 | 329 | 604 | 44.500000, 45.000000, 16.216096 | 0 detected | [2, 2] | 4 | 4 | [0, 1206] |
| objects/Bunker_0_lod1.obj | 55 | 16 | 26 | 44.500000, 45.000000, 8.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Bus.obj | 2149 | 573 | 1088 | 3.533216, 8.358102, 3.577684 | 0 detected | [4, 2] | 8 | 8 | [0, 2149] |
| objects/Bush_0.obj | 44 | 44 | 22 | 5.997267, 5.873599, 5.380211 | 0 detected | [32, 32] | 3 | 1 | [0, 44] |
| objects/Bush_1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 4 | 1 | [0, 36] |
| objects/Bush_Amber.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Amber_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Bush_Hanu.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Hanu_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Bush_Indigo.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Indigo_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Bush_Jade.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Jade_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Bush_Mauve.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Mauve_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Bush_Russet.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Russet_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Bush_Teal.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Teal_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Bush_Vermillion.obj | 204 | 92 | 102 | 5.352729, 4.667708, 5.627419 | 0 detected | [32, 32] | 16 | 1 | [0, 204] |
| objects/Bush_Vermillion_lod1.obj | 36 | 36 | 18 | 5.352729, 4.667708, 5.627419 | 0 detected | no paired PNG | — | — | — |
| objects/Cafe_0.obj | 1286 | 513 | 802 | 17.500008, 14.100006, 10.750000 | 0 detected | [4, 2] | 7 | 7 | [0, 1286] |
| objects/Cafe_0_lod1.obj | 1114 | 385 | 610 | 17.500008, 14.100006, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Cafe_0_lod2.obj | 236 | 73 | 132 | 17.500008, 14.000009, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Cafe_1.obj | 790 | 337 | 543 | 16.000007, 16.100004, 11.750000 | 0 detected | [4, 2] | 7 | 7 | [0, 790] |
| objects/Cafe_1_lod1.obj | 692 | 268 | 439 | 16.000007, 16.100004, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Cafe_1_lod2.obj | 200 | 77 | 138 | 16.000007, 16.000008, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Cagelight_0.obj | 332 | 96 | 142 | 0.536864, 0.414534, 0.671080 | 0 detected | [2, 2] | 3 | 2 | [0, 332] |
| objects/Camera_0.obj | 82 | 28 | 44 | 0.582215, 0.631015, 0.504321 | 0 detected | [2, 1] | 2 | 2 | [0, 82] |
| objects/Camera_0_Debris.obj | 82 | 28 | 44 | 0.516700, 0.641867, 0.586244 | 0 detected | no paired PNG | — | — | — |
| objects/Camera_0_lod1.obj | 34 | 13 | 22 | 0.582215, 0.582215, 0.353867 | 0 detected | no paired PNG | — | — | — |
| objects/Campfire_0.obj | 312 | 104 | 156 | 2.243955, 2.354402, 1.443312 | 0 detected | [2, 1] | 2 | 2 | [0, 312] |
| objects/Candle_0_HW.obj | 36 | 12 | 20 | 0.162380, 0.187500, 0.750000 | 6×2 | no paired PNG | — | — | — |
| objects/Candle_0_HW_lod1.obj | 4 | 4 | 4 | 0.162380, 0.093750, 0.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Candle_1_HW.obj | 337 | 88 | 136 | 0.381838, 0.780000, 1.400000 | 6×6 | [2, 1] | 2 | 2 | [0, 337] |
| objects/Candle_1_HW_lod1.obj | 98 | 54 | 68 | 0.381838, 0.750000, 1.400000 | 6×3 | no paired PNG | — | — | — |
| objects/Cane_00.obj | 115 | 42 | 80 | 1.575936, 3.495960, 0.468191 | 6×7 | [1, 4] | 4 | 2 | [0, 115] |
| objects/Cane_00_lod1.obj | 4 | 4 | 4 | 0.405464, 3.265170, 0.234095 | 0 detected | no paired PNG | — | — | — |
| objects/Canister_0.obj | 168 | 84 | 164 | 4.000000, 6.500005, 4.000004 | 12×7 | no paired PNG | — | — | — |
| objects/Canister_0_lod1.obj | 72 | 24 | 44 | 4.000004, 6.500004, 3.464103 | 6×4 | no paired PNG | — | — | — |
| objects/Canister_1.obj | 144 | 96 | 188 | 4.000000, 2.375005, 4.000000 | 12×8 | no paired PNG | — | — | — |
| objects/Canister_1_lod1.obj | 90 | 36 | 68 | 4.000001, 2.375002, 3.464103 | 6×6 | no paired PNG | — | — | — |
| objects/Car_Lift_0.obj | 152 | 56 | 92 | 4.429909, 3.769763, 4.496467 | 0 detected | [4, 2] | 5 | 4 | [0, 152] |
| objects/Car_Lift_0_lod1.obj | 88 | 32 | 60 | 4.250003, 3.769763, 4.496467 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_0.obj | 40 | 8 | 20 | 0.866674, 0.555716, 0.433656 | 0 detected | [4, 2] | 0 | 0 | [0, 0] |
| objects/Cardboard_0_Debris.obj | 72 | 16 | 36 | 1.279920, 0.892656, 0.657767 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_0_lod1.obj | 20 | 8 | 20 | 0.866674, 0.555716, 0.433656 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_0_xhi_door.obj | 8 | 4 | 4 | 0.220038, 0.555715, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_0_xlo_door.obj | 8 | 4 | 4 | 0.208362, 0.555716, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_0_yhi_door.obj | 8 | 4 | 4 | 0.866673, 0.245804, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_0_ylo_door.obj | 8 | 4 | 4 | 0.866673, 0.251802, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_1.obj | 40 | 8 | 20 | 1.137984, 0.857506, 0.569411 | 0 detected | [4, 2] | 0 | 0 | [0, 0] |
| objects/Cardboard_1_Debris.obj | 72 | 16 | 36 | 1.568428, 1.486448, 0.798810 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_1_lod1.obj | 20 | 8 | 20 | 1.137984, 0.857506, 0.569411 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_1_xhi_door.obj | 8 | 4 | 4 | 0.277618, 0.857506, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_1_xlo_door.obj | 8 | 4 | 4 | 0.289867, 0.857505, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_1_yhi_door.obj | 8 | 4 | 4 | 1.137984, 0.376264, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_1_ylo_door.obj | 8 | 4 | 4 | 1.137984, 0.351216, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_2.obj | 40 | 8 | 20 | 0.866674, 0.555716, 0.433656 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_2_Debris.obj | 72 | 16 | 36 | 1.279920, 0.892656, 0.657767 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_2_lod1.obj | 20 | 8 | 20 | 0.866674, 0.555716, 0.433656 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_2_xhi_door.obj | 8 | 4 | 4 | 0.220038, 0.555715, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_2_xlo_door.obj | 8 | 4 | 4 | 0.208362, 0.555716, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_2_yhi_door.obj | 8 | 4 | 4 | 0.866673, 0.245804, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_2_ylo_door.obj | 8 | 4 | 4 | 0.866673, 0.251802, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3.obj | 40 | 8 | 20 | 1.137984, 0.857506, 0.569411 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3_Debris.obj | 72 | 16 | 36 | 1.568428, 1.486448, 0.798810 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3_lod1.obj | 20 | 8 | 20 | 1.137984, 0.857506, 0.569411 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3_xhi_door.obj | 8 | 4 | 4 | 0.277618, 0.857506, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3_xlo_door.obj | 8 | 4 | 4 | 0.289867, 0.857505, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3_yhi_door.obj | 8 | 4 | 4 | 1.137984, 0.376264, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Cardboard_3_ylo_door.obj | 8 | 4 | 4 | 1.137984, 0.351216, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Castle_0.obj | 3188 | 1100 | 2101 | 26.000062, 26.000049, 18.500000 | 12×2 | [4, 2] | 7 | 5 | [0, 3188] |
| objects/Castle_0_lod1.obj | 544 | 192 | 270 | 25.727492, 25.727476, 18.500000 | 6×16 | no paired PNG | — | — | — |
| objects/Cave_0_PEI.obj | 514 | 281 | 580 | 33.533934, 38.413184, 18.466656 | 0 detected | [64, 64] | 387 | 23 | [0, 514] |
| objects/Cave_0_PEI_lod1.obj | 168 | 65 | 144 | 31.997630, 38.011006, 18.158071 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Beach_0.obj | 96 | 44 | 52 | 1.500001, 3.000001, 0.724999 | 0 detected | [2, 2] | 3 | 3 | [0, 96] |
| objects/Chair_Beach_0_Debris.obj | 80 | 32 | 40 | 1.300000, 2.800000, 0.330811 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Beach_0_lod1.obj | 24 | 8 | 12 | 1.500001, 3.000001, 0.349999 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Beach_1.obj | 96 | 44 | 52 | 1.500001, 3.000001, 0.724999 | 0 detected | [2, 2] | 3 | 3 | [0, 96] |
| objects/Chair_Beach_1_Debris.obj | 80 | 32 | 40 | 1.300000, 2.800000, 0.330811 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Beach_1_lod1.obj | 24 | 8 | 12 | 1.500001, 3.000001, 0.349999 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Beach_2.obj | 96 | 44 | 52 | 1.500001, 3.000001, 0.724999 | 0 detected | [2, 2] | 3 | 3 | [0, 96] |
| objects/Chair_Beach_2_Debris.obj | 80 | 32 | 40 | 1.300000, 2.800000, 0.330811 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Beach_2_lod1.obj | 24 | 8 | 12 | 1.500001, 3.000001, 0.349999 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Metal_0.obj | 160 | 56 | 84 | 0.937500, 1.018218, 1.756036 | 0 detected | [2, 2] | 4 | 2 | [0, 160] |
| objects/Chair_Metal_0_Ragdoll_0.obj | 160 | 56 | 84 | 0.937500, 1.018218, 1.756036 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Metal_0_lod1.obj | 44 | 16 | 24 | 0.883642, 0.256250, 1.709888 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Wood_0.obj | 160 | 64 | 80 | 0.937500, 0.937500, 1.806521 | 0 detected | [2, 2] | 4 | 2 | [0, 160] |
| objects/Chair_Wood_0_Ragdoll_0.obj | 160 | 64 | 80 | 0.937500, 0.937500, 1.806521 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Wood_0_lod1.obj | 40 | 16 | 20 | 0.883642, 0.156250, 1.709888 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Wood_1.obj | 160 | 56 | 84 | 0.937500, 1.018218, 1.756036 | 0 detected | [2, 2] | 4 | 2 | [0, 160] |
| objects/Chair_Wood_1_Ragdoll_0.obj | 160 | 56 | 84 | 0.937500, 1.018218, 1.756036 | 0 detected | no paired PNG | — | — | — |
| objects/Chair_Wood_1_lod1.obj | 44 | 16 | 24 | 0.883642, 0.256250, 1.709888 | 0 detected | no paired PNG | — | — | — |
| objects/Charge_0.obj | 240 | 108 | 194 | 0.413428, 0.148028, 0.458174 | 0 detected | [4, 2] | 6 | 6 | [0, 240] |
| objects/Charge_0_lod1.obj | 24 | 8 | 12 | 0.393428, 0.118028, 0.458174 | 0 detected | no paired PNG | — | — | — |
| objects/Checkout_0.obj | 56 | 24 | 32 | 3.000001, 2.000000, 1.274312 | 0 detected | [2, 2] | 4 | 2 | [0, 56] |
| objects/Checkout_0_lod1.obj | 36 | 16 | 22 | 3.000001, 2.000000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Chemicals_00.obj | 151 | 48 | 92 | 0.318174, 0.318175, 0.584059 | 12×4 | [2, 2] | 3 | 2 | [0, 151] |
| objects/Chemicals_00_Off.obj | 151 | 48 | 92 | 0.318174, 0.318175, 0.584059 | 12×4 | [1, 1] | 1 | 1 | [0, 151] |
| objects/Chemicals_00_Off_lod1.obj | 62 | 19 | 34 | 0.275548, 0.275548, 0.584059 | 0 detected | no paired PNG | — | — | — |
| objects/Chemicals_00_lod1.obj | 62 | 19 | 34 | 0.275548, 0.275548, 0.584059 | 0 detected | no paired PNG | — | — | — |
| objects/ChemistryLab_0.obj | 888 | 412 | 597 | 2.000001, 1.243839, 1.927139 | 5×2, 6×22 | [64, 64] | 34 | 28 | [0, 888] |
| objects/Chess_0.obj | 692 | 272 | 348 | 1.000000, 1.000000, 0.240069 | 0 detected | [16, 8] | 5 | 3 | [0, 692] |
| objects/Chess_0_Debris.obj | 52 | 16 | 28 | 1.000000, 1.000000, 0.100000 | 0 detected | no paired PNG | — | — | — |
| objects/Chess_0_lod1.obj | 38 | 14 | 24 | 1.000000, 1.000000, 0.100000 | 0 detected | no paired PNG | — | — | — |
| objects/Chute_Wood_0.obj | 88 | 24 | 44 | 2.500000, 12.000000, 2.000000 | 0 detected | [2, 2] | 3 | 2 | [0, 88] |
| objects/Circle_00.obj | 32 | 32 | 32 | 12.252012, 12.252014, 0.000000 | 16×2 | [2, 2] | 1 | 1 | [0, 32] |
| objects/Circle_01.obj | 60 | 60 | 46 | 12.018936, 13.044194, 0.020000 | 16×2 | [2, 2] | 1 | 1 | [0, 60] |
| objects/Circle_01_lod1.obj | 28 | 28 | 14 | 12.018936, 12.044194, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Circle_02.obj | 32 | 32 | 32 | 2.000000, 2.000000, 0.000000 | 16×2 | [2, 2] | 1 | 1 | [0, 32] |
| objects/Circuit_0.obj | 184 | 88 | 92 | 0.999658, 0.581130, 1.865803 | 0 detected | [4, 2] | 6 | 5 | [0, 184] |
| objects/Circuit_0_Debris.obj | 184 | 88 | 92 | 0.999658, 0.581130, 1.865803 | 0 detected | no paired PNG | — | — | — |
| objects/Circuit_0_lod1.obj | 49 | 22 | 24 | 0.999658, 0.509248, 1.865803 | 0 detected | no paired PNG | — | — | — |
| objects/Clay_0.obj | 125 | 50 | 96 | 3.642531, 3.722193, 3.589734 | 0 detected | [64, 64] | 107 | 39 | [0, 125] |
| objects/Clay_0_lod1.obj | 64 | 19 | 34 | 2.459870, 2.551192, 2.595200 | 0 detected | no paired PNG | — | — | — |
| objects/Clay_1.obj | 77 | 39 | 70 | 3.237393, 1.845114, 3.633945 | 0 detected | [64, 64] | 73 | 31 | [0, 77] |
| objects/Clay_1_lod1.obj | 16 | 6 | 8 | 2.053423, 1.355324, 2.534461 | 0 detected | no paired PNG | — | — | — |
| objects/Clay_2.obj | 164 | 70 | 132 | 4.041149, 2.888599, 4.111197 | 0 detected | [64, 64] | 124 | 47 | [0, 164] |
| objects/Clay_2_lod1.obj | 38 | 15 | 22 | 3.503777, 2.391349, 3.496600 | 0 detected | no paired PNG | — | — | — |
| objects/Clay_3.obj | 120 | 55 | 98 | 3.395213, 2.265116, 3.383798 | 0 detected | [64, 64] | 99 | 36 | [0, 120] |
| objects/Clay_3_lod1.obj | 28 | 11 | 14 | 2.831368, 1.677300, 3.043235 | 0 detected | no paired PNG | — | — | — |
| objects/Clay_4.obj | 180 | 69 | 122 | 3.592244, 1.837701, 3.250860 | 0 detected | [64, 64] | 131 | 41 | [0, 180] |
| objects/Clay_4_lod1.obj | 24 | 8 | 12 | 2.985830, 0.976589, 2.663835 | 0 detected | no paired PNG | — | — | — |
| objects/Clock_0.obj | 176 | 56 | 96 | 1.000000, 0.125002, 1.000000 | 12×4 | [2, 1] | 2 | 2 | [0, 176] |
| objects/Clock_0_lod1.obj | 68 | 26 | 36 | 0.866026, 0.136763, 1.000000 | 6×3 | no paired PNG | — | — | — |
| objects/Clothes_0.obj | 978 | 409 | 692 | 24.000022, 16.111088, 11.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 978] |
| objects/Clothes_0_lod1.obj | 847 | 322 | 561 | 24.000022, 16.111088, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Clothes_0_lod2.obj | 218 | 82 | 168 | 24.000022, 16.000023, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Clothes_1.obj | 1610 | 498 | 772 | 12.500000, 17.200002, 10.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1610] |
| objects/Clothes_1_lod1.obj | 1147 | 366 | 574 | 12.500000, 17.200002, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Clothes_1_lod2.obj | 268 | 64 | 125 | 12.500000, 17.000001, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Coat_0.obj | 86 | 32 | 42 | 0.582571, 0.582570, 1.989102 | 0 detected | no paired PNG | — | — | — |
| objects/Coat_0_Ragdoll_0.obj | 86 | 32 | 42 | 0.582571, 0.582570, 1.989102 | 0 detected | no paired PNG | — | — | — |
| objects/Coat_0_lod1.obj | 20 | 8 | 12 | 0.120000, 0.120000, 1.900000 | 0 detected | no paired PNG | — | — | — |
| objects/Coffee_0.obj | 164 | 64 | 90 | 1.200000, 0.760336, 0.816996 | 6×4 | [2, 2] | 4 | 4 | [0, 164] |
| objects/Coffee_0_Ragdoll_0.obj | 164 | 64 | 90 | 1.200000, 0.760336, 0.816996 | 6×4 | no paired PNG | — | — | — |
| objects/Coffee_0_lod1.obj | 52 | 20 | 30 | 1.200000, 0.663196, 0.816996 | 0 detected | no paired PNG | — | — | — |
| objects/Coil_0.obj | 288 | 96 | 160 | 1.700003, 2.000001, 2.000000 | 12×8 | [2, 2] | 4 | 2 | [0, 288] |
| objects/Coil_0_lod1.obj | 96 | 36 | 52 | 1.700002, 2.000001, 1.732052 | 6×6 | no paired PNG | — | — | — |
| objects/Computer_0.obj | 110 | 40 | 58 | 1.000000, 1.026764, 1.142733 | 0 detected | [2, 2] | 4 | 4 | [0, 110] |
| objects/Computer_0_Ragdoll_0.obj | 110 | 40 | 58 | 1.000000, 1.026764, 1.142733 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_0_lod1.obj | 48 | 16 | 28 | 1.000000, 1.000000, 1.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_1.obj | 84 | 32 | 42 | 0.521429, 0.974844, 1.056944 | 0 detected | [2, 2] | 4 | 4 | [0, 84] |
| objects/Computer_1_Ragdoll_0.obj | 84 | 32 | 42 | 0.521429, 0.974844, 1.056944 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_1_lod1.obj | 24 | 8 | 12 | 0.521429, 0.952019, 1.056944 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_2.obj | 81 | 22 | 40 | 0.755340, 0.612736, 0.668520 | 0 detected | [2, 1] | 2 | 2 | [0, 81] |
| objects/Computer_2_Ragdoll_0.obj | 81 | 22 | 40 | 0.755340, 0.612736, 0.668520 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_3.obj | 110 | 40 | 58 | 1.300000, 0.340545, 1.192733 | 0 detected | [2, 2] | 4 | 4 | [0, 110] |
| objects/Computer_3_Ragdoll_0.obj | 110 | 40 | 58 | 1.300000, 0.340545, 1.192733 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_3_lod1.obj | 48 | 16 | 28 | 1.300000, 0.200000, 1.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_4.obj | 84 | 32 | 42 | 0.521429, 0.974844, 1.056944 | 0 detected | [2, 2] | 4 | 4 | [0, 84] |
| objects/Computer_4_Ragdoll_0.obj | 84 | 32 | 42 | 0.521429, 0.974844, 1.056944 | 0 detected | no paired PNG | — | — | — |
| objects/Computer_4_lod1.obj | 24 | 8 | 12 | 0.521429, 0.952019, 1.056944 | 0 detected | no paired PNG | — | — | — |
| objects/Construction_0.obj | 694 | 242 | 462 | 20.000008, 20.000010, 24.500000 | 0 detected | [2, 2] | 4 | 4 | [0, 694] |
| objects/Construction_0_lod1.obj | 495 | 164 | 365 | 20.000008, 20.000010, 24.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Container_0.obj | 347 | 116 | 226 | 2.877038, 7.497734, 3.242792 | 0 detected | [2, 2] | 4 | 2 | [0, 347] |
| objects/Container_0_Left_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374998, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_0_Left_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_0_Left_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_0_Right_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_0_Right_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_0_Right_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_0_lod1.obj | 52 | 18 | 32 | 2.877038, 7.497734, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_1.obj | 347 | 116 | 226 | 2.877038, 7.497734, 3.242792 | 0 detected | [2, 2] | 4 | 2 | [0, 347] |
| objects/Container_1_Left_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374998, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_1_Left_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_1_Left_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_1_Right_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_1_Right_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_1_Right_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_1_lod1.obj | 52 | 18 | 32 | 2.877038, 7.497734, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_2.obj | 347 | 116 | 226 | 2.877038, 7.497734, 3.242792 | 0 detected | [2, 2] | 4 | 2 | [0, 347] |
| objects/Container_2_Left_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374998, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_2_Left_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_2_Left_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_2_Right_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_2_Right_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_2_Right_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_2_lod1.obj | 52 | 18 | 32 | 2.877038, 7.497734, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_3.obj | 321 | 107 | 210 | 2.877038, 7.497734, 3.242793 | 0 detected | [2, 2] | 2 | 1 | [0, 321] |
| objects/Container_3_lod1.obj | 24 | 8 | 12 | 2.877038, 7.497734, 3.242793 | 0 detected | no paired PNG | — | — | — |
| objects/Container_4.obj | 321 | 107 | 210 | 2.877038, 7.497734, 3.242793 | 0 detected | [2, 2] | 2 | 1 | [0, 321] |
| objects/Container_4_lod1.obj | 24 | 8 | 12 | 2.877038, 7.497734, 3.242793 | 0 detected | no paired PNG | — | — | — |
| objects/Container_5.obj | 321 | 107 | 210 | 2.877038, 7.497734, 3.242793 | 0 detected | [2, 2] | 2 | 1 | [0, 321] |
| objects/Container_5_lod1.obj | 24 | 8 | 12 | 2.877038, 7.497734, 3.242793 | 0 detected | no paired PNG | — | — | — |
| objects/Container_6_Left_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374998, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_6_Left_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_6_Left_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_6_Right_Hinge_0_door.obj | 128 | 56 | 64 | 1.438519, 0.374999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_6_Right_Hinge_0_door_lod1.obj | 30 | 16 | 16 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Container_6_Right_Hinge_1_door.obj | 24 | 8 | 12 | 1.438519, 0.249999, 3.242792 | 0 detected | no paired PNG | — | — | — |
| objects/Control_0.obj | 140 | 56 | 70 | 1.000000, 0.983788, 1.697093 | 0 detected | [2, 2] | 3 | 3 | [0, 140] |
| objects/Control_0_lod1.obj | 20 | 8 | 10 | 1.000000, 0.700000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Control_1.obj | 230 | 112 | 166 | 1.000000, 0.700000, 1.250000 | 8×9 | [2, 2] | 4 | 4 | [0, 230] |
| objects/Control_1_lod1.obj | 20 | 8 | 10 | 1.000000, 0.700000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Control_2.obj | 230 | 92 | 114 | 1.000000, 0.700000, 1.250000 | 0 detected | [2, 2] | 4 | 4 | [0, 230] |
| objects/Control_2_lod1.obj | 20 | 8 | 10 | 1.000000, 0.700000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Control_3.obj | 361 | 142 | 179 | 1.000000, 0.700000, 1.257509 | 6×6 | [2, 2] | 4 | 4 | [0, 361] |
| objects/Control_3_lod1.obj | 20 | 8 | 10 | 1.000000, 0.700000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Control_4.obj | 220 | 72 | 94 | 1.000000, 0.700000, 1.592040 | 0 detected | [2, 2] | 3 | 3 | [0, 220] |
| objects/Control_4_lod1.obj | 20 | 8 | 10 | 1.000000, 0.700000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_0.obj | 84 | 40 | 46 | 1.500000, 1.000000, 2.500000 | 0 detected | [2, 2] | 4 | 3 | [0, 84] |
| objects/Cooler_0_Glass_0_door.obj | 8 | 4 | 4 | 1.300000, 0.000001, 2.300000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_0_Hinge_0_door.obj | 90 | 24 | 42 | 1.500000, 0.200000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_1.obj | 105 | 32 | 46 | 0.750000, 0.750000, 1.250000 | 0 detected | [2, 2] | 4 | 4 | [0, 105] |
| objects/Cooler_1_lod1.obj | 6 | 6 | 6 | 0.750000, 0.750000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_2.obj | 78 | 36 | 60 | 0.656132, 0.656132, 0.798198 | 6×2, 12×2 | no paired PNG | — | — | — |
| objects/Cooler_2_lod1.obj | 4 | 4 | 4 | 0.164033, 0.612180, 0.748198 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_Beach_0.obj | 96 | 32 | 48 | 1.000000, 1.635870, 0.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Cooler_Beach_0_Ragdoll_0.obj | 24 | 8 | 12 | 1.000000, 1.500000, 0.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_Beach_0_lod1.obj | 28 | 13 | 22 | 1.000000, 1.500000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_Beach_1.obj | 96 | 32 | 48 | 1.000000, 1.635870, 0.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Cooler_Beach_1_Ragdoll_0.obj | 24 | 8 | 12 | 1.000000, 1.500000, 0.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_Beach_1_lod1.obj | 28 | 13 | 22 | 1.000000, 1.500000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_Beach_2.obj | 96 | 32 | 48 | 1.000000, 1.635870, 0.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Cooler_Beach_2_Ragdoll_0.obj | 24 | 8 | 12 | 1.000000, 1.500000, 0.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Cooler_Beach_2_lod1.obj | 28 | 13 | 22 | 1.000000, 1.500000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Couch_0.obj | 158 | 70 | 116 | 3.000002, 1.529923, 1.500000 | 8×4 | [2, 2] | 3 | 2 | [0, 158] |
| objects/Couch_0_lod1.obj | 82 | 36 | 68 | 3.000002, 1.500002, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Couch_1.obj | 158 | 70 | 116 | 2.200002, 1.529923, 1.500000 | 8×4 | [2, 2] | 3 | 2 | [0, 158] |
| objects/Couch_1_lod1.obj | 82 | 36 | 68 | 2.200002, 1.500002, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_0.obj | 116 | 52 | 94 | 1.500000, 1.125000, 1.350000 | 0 detected | [2, 2] | 2 | 2 | [0, 116] |
| objects/Counter_0_Left_Hinge_0_door.obj | 44 | 16 | 22 | 0.750000, 0.167596, 1.225000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_0_Right_Hinge_0_door.obj | 44 | 16 | 22 | 0.750000, 0.167597, 1.225000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_0_lod1.obj | 47 | 19 | 28 | 1.500000, 1.125000, 1.350000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_1.obj | 180 | 64 | 104 | 1.500000, 1.125000, 1.834327 | 0 detected | [2, 2] | 4 | 4 | [0, 180] |
| objects/Counter_1_lod1.obj | 46 | 16 | 26 | 1.500000, 1.125000, 1.350000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_2.obj | 116 | 52 | 94 | 1.500000, 1.125000, 1.350000 | 0 detected | [2, 2] | 2 | 2 | [0, 116] |
| objects/Counter_2_Left_Hinge_0_door.obj | 44 | 16 | 22 | 0.750000, 0.167596, 1.225000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_2_Right_Hinge_0_door.obj | 44 | 16 | 22 | 0.750000, 0.167597, 1.225000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_2_lod1.obj | 47 | 19 | 28 | 1.500000, 1.125000, 1.350000 | 0 detected | no paired PNG | — | — | — |
| objects/Counter_3.obj | 180 | 64 | 104 | 1.500000, 1.125000, 1.834327 | 0 detected | [2, 2] | 4 | 4 | [0, 180] |
| objects/Counter_3_lod1.obj | 46 | 16 | 26 | 1.500000, 1.125000, 1.350000 | 0 detected | no paired PNG | — | — | — |
| objects/Craft_00.obj | 201 | 81 | 107 | 1.458312, 1.300610, 2.171462 | 6×2 | [2, 2] | 4 | 4 | [0, 201] |
| objects/Craft_00_lod1.obj | 24 | 24 | 24 | 1.312436, 1.136603, 2.071462 | 0 detected | no paired PNG | — | — | — |
| objects/Craft_01.obj | 232 | 108 | 122 | 1.746767, 1.303308, 2.068290 | 0 detected | [2, 2] | 4 | 4 | [0, 232] |
| objects/Craft_01_lod1.obj | 57 | 57 | 44 | 1.746767, 1.303308, 2.068290 | 0 detected | no paired PNG | — | — | — |
| objects/Craft_02.obj | 96 | 54 | 48 | 1.233136, 1.067928, 0.885731 | 0 detected | [2, 2] | 2 | 2 | [0, 96] |
| objects/Craft_02_lod1.obj | 18 | 18 | 12 | 1.233136, 1.067928, 0.885731 | 0 detected | no paired PNG | — | — | — |
| objects/Crane_0.obj | 1950 | 555 | 936 | 10.976280, 85.000051, 48.996859 | 0 detected | [2, 2] | 4 | 4 | [0, 1950] |
| objects/Crane_0_lod1.obj | 48 | 16 | 24 | 6.000002, 85.000051, 5.000050 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_0.obj | 240 | 88 | 144 | 1.250000, 1.250000, 1.250000 | 0 detected | [2, 2] | 3 | 3 | [0, 240] |
| objects/Crate_0_lod1.obj | 24 | 8 | 12 | 1.250000, 1.250000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_1.obj | 54 | 16 | 28 | 2.000000, 0.750000, 0.200000 | 0 detected | [2, 1] | 2 | 2 | [0, 54] |
| objects/Crate_1_door.obj | 54 | 16 | 28 | 2.000000, 0.750001, 0.201407 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_1_lod1.obj | 24 | 8 | 12 | 2.000000, 0.750000, 0.200000 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_2.obj | 24 | 8 | 12 | 2.000000, 0.750000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_2_lod1.obj | 5 | 5 | 6 | 2.000000, 0.750000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_3.obj | 264 | 104 | 156 | 1.875000, 1.875001, 1.875000 | 0 detected | [2, 2] | 3 | 3 | [0, 264] |
| objects/Crate_3_lod1.obj | 24 | 8 | 12 | 1.874998, 1.875000, 1.875000 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_4.obj | 54 | 16 | 28 | 2.000000, 0.750000, 0.200000 | 0 detected | [2, 1] | 2 | 2 | [0, 54] |
| objects/Crate_4_door.obj | 54 | 16 | 28 | 2.000000, 0.750001, 0.201407 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_5.obj | 24 | 8 | 12 | 2.000000, 0.750000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| objects/Crate_5_lod1.obj | 5 | 5 | 6 | 2.000000, 0.750000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| objects/Crop_0.obj | 144 | 144 | 96 | 18.325856, 19.642291, 2.993200 | 0 detected | [32, 32] | 4 | 1 | [0, 144] |
| objects/Crop_0_lod1.obj | 48 | 48 | 24 | 17.190001, 18.558847, 2.826702 | 0 detected | no paired PNG | — | — | — |
| objects/Crop_1.obj | 420 | 420 | 238 | 24.397386, 12.833215, 2.500000 | 0 detected | [32, 32] | 20 | 1 | [0, 420] |
| objects/Crop_1_lod1.obj | 84 | 84 | 70 | 24.397386, 12.000001, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Crossing_0.obj | 122 | 40 | 60 | 0.865153, 0.316790, 2.725909 | 0 detected | [4, 2] | 3 | 3 | [0, 122] |
| objects/Crossing_1.obj | 20 | 8 | 10 | 0.304840, 0.304840, 1.500000 | 0 detected | [4, 2] | 2 | 1 | [0, 20] |
| objects/Crossing_1_Hinge_0_door.obj | 44 | 16 | 22 | 10.619216, 0.234182, 0.435733 | 0 detected | no paired PNG | — | — | — |
| objects/Crossing_2.obj | 20 | 8 | 10 | 0.304840, 0.304840, 1.500000 | 0 detected | [4, 2] | 2 | 1 | [0, 20] |
| objects/Crossing_2_Hinge_0_door.obj | 44 | 16 | 22 | 10.619216, 0.234182, 0.435733 | 0 detected | no paired PNG | — | — | — |
| objects/Crystal_00.obj | 20 | 8 | 8 | 0.153500, 0.306998, 0.642118 | 0 detected | [2, 2] | 1 | 1 | [0, 20] |
| objects/Crystal_00_Off.obj | 20 | 8 | 8 | 0.153500, 0.306998, 0.642118 | 0 detected | [1, 1] | 1 | 1 | [0, 20] |
| objects/DL_Garbage.obj | 80 | 28 | 40 | 0.943052, 0.921270, 1.416428 | 0 detected | no paired PNG | — | — | — |
| objects/DL_Garbage_lod1.obj | 35 | 18 | 20 | 0.943052, 0.921270, 1.364297 | 0 detected | no paired PNG | — | — | — |
| objects/Debris_0.obj | 197 | 82 | 110 | 5.884793, 5.999998, 2.441153 | 0 detected | [2, 2] | 4 | 4 | [0, 197] |
| objects/Debris_00_XMAS.obj | 197 | 82 | 110 | 5.884793, 5.999998, 2.441153 | 0 detected | [2, 2] | 4 | 4 | [0, 197] |
| objects/Debris_0_lod1.obj | 57 | 26 | 40 | 5.706347, 5.999998, 1.240420 | 0 detected | no paired PNG | — | — | — |
| objects/Debris_1.obj | 304 | 106 | 140 | 5.864496, 6.132834, 1.767682 | 0 detected | [2, 2] | 4 | 4 | [0, 304] |
| objects/Debris_1_lod1.obj | 10 | 10 | 8 | 5.864496, 6.132834, 0.252094 | 0 detected | no paired PNG | — | — | — |
| objects/Demo_House.obj | 1458 | 384 | 712 | 12.770000, 9.871020, 12.250120 | 0 detected | [4, 2] | 2 | 2 | [1458, 1458] |
| objects/Demo_House_lod1.obj | 200 | 200 | 344 | 12.735000, 9.871020, 12.250120 | 0 detected | no paired PNG | — | — | — |
| objects/Diner_0.obj | 1240 | 472 | 833 | 23.500014, 20.607079, 11.750000 | 0 detected | [4, 2] | 8 | 8 | [0, 1240] |
| objects/Diner_0_lod1.obj | 1164 | 430 | 774 | 23.500014, 20.607079, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Diner_0_lod2.obj | 449 | 161 | 326 | 23.000014, 20.000001, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Diner_1.obj | 825 | 326 | 559 | 14.000007, 18.100003, 12.883025 | 0 detected | [4, 2] | 7 | 7 | [0, 825] |
| objects/Diner_1_lod1.obj | 746 | 282 | 492 | 14.000007, 18.100003, 12.883025 | 0 detected | no paired PNG | — | — | — |
| objects/Diner_1_lod2.obj | 247 | 90 | 185 | 14.000007, 18.000008, 12.883025 | 0 detected | no paired PNG | — | — | — |
| objects/Diner_2.obj | 1523 | 587 | 1004 | 21.700026, 18.199996, 11.750001 | 0 detected | [4, 2] | 8 | 8 | [0, 1523] |
| objects/Diner_2_lod1.obj | 330 | 118 | 242 | 21.700026, 18.199980, 11.750001 | 0 detected | no paired PNG | — | — | — |
| objects/Diner_Sign_0.obj | 1226 | 522 | 777 | 4.101336, 0.666918, 10.000000 | 0 detected | [2, 2] | 4 | 4 | [0, 1226] |
| objects/Diner_Sign_0_lod1.obj | 60 | 20 | 32 | 4.000000, 0.500000, 10.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Directions_0.obj | 78 | 29 | 42 | 1.067919, 1.067919, 2.250000 | 0 detected | [2, 2] | 2 | 2 | [0, 78] |
| objects/Directions_0_Ragdoll_0.obj | 78 | 29 | 42 | 1.067919, 1.067919, 2.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Directions_0_lod1.obj | 68 | 24 | 34 | 1.067919, 1.067919, 2.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Disher_0.obj | 70 | 24 | 34 | 1.500000, 1.375000, 1.500000 | 0 detected | [2, 2] | 2 | 1 | [0, 70] |
| objects/Disher_0_door.obj | 46 | 16 | 22 | 1.500000, 0.210138, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Divider_0.obj | 62 | 28 | 42 | 3.250000, 0.250002, 2.000000 | 0 detected | [2, 1] | 2 | 2 | [0, 62] |
| objects/Divider_0_lod1.obj | 21 | 17 | 16 | 3.250000, 0.250002, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Divider_1.obj | 245 | 67 | 126 | 3.125000, 0.125000, 2.500000 | 0 detected | [2, 1] | 2 | 2 | [0, 245] |
| objects/Divider_1_Debris.obj | 108 | 32 | 40 | 3.125000, 0.125000, 0.291562 | 0 detected | no paired PNG | — | — | — |
| objects/Divider_1_lod1.obj | 104 | 23 | 42 | 3.125000, 0.125000, 2.375000 | 0 detected | no paired PNG | — | — | — |
| objects/Dock_0.obj | 288 | 128 | 184 | 2.500000, 6.350000, 19.000000 | 8×8 | [2, 2] | 4 | 2 | [0, 288] |
| objects/Dock_0_lod1.obj | 104 | 40 | 52 | 2.500000, 6.250002, 19.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Dock_1.obj | 60 | 24 | 30 | 37.500000, 36.750011, 41.000000 | 0 detected | [2, 1] | 2 | 2 | [0, 60] |
| objects/Dock_1_lod1.obj | 20 | 8 | 10 | 36.000014, 36.000017, 40.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Aircraft_Carrier.obj | 452 | 164 | 246 | 2.527007, 2.800000, 0.560004 | 8×8 | [2, 2] | 3 | 3 | [0, 452] |
| objects/Door_Aircraft_Carrier_lod1.obj | 24 | 8 | 12 | 1.950000, 2.800000, 0.250004 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Birch.obj | 48 | 16 | 24 | 2.450000, 0.505976, 2.800000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Door_Birch_lod1.obj | 20 | 8 | 12 | 2.450000, 0.200000, 2.800000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Blast.obj | 379 | 90 | 192 | 12.500000, 6.485000, 1.000001 | 6×9 | [2, 2] | 4 | 3 | [0, 379] |
| objects/Door_Chamber.obj | 28 | 12 | 14 | 6.000000, 6.000000, 5.250003 | 0 detected | [64, 64] | 6 | 4 | [0, 28] |
| objects/Door_Jail.obj | 267 | 96 | 136 | 2.450000, 0.575531, 2.800000 | 0 detected | [8, 8] | 7 | 2 | [0, 267] |
| objects/Door_Jail_lod1.obj | 80 | 32 | 48 | 2.450000, 0.243953, 2.800000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Maple.obj | 48 | 16 | 24 | 2.450000, 0.505976, 2.800000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Door_Maple_lod1.obj | 20 | 8 | 12 | 2.450000, 0.200000, 2.800000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Metal.obj | 48 | 16 | 24 | 2.450000, 0.505976, 2.800000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Door_Metal_lod1.obj | 20 | 8 | 12 | 2.450000, 0.200000, 2.800000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Pine.obj | 48 | 16 | 24 | 2.450000, 0.505976, 2.800000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Door_Pine_lod1.obj | 20 | 8 | 12 | 2.450000, 0.200000, 2.800000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Prison_0.obj | 461 | 95 | 232 | 1.600000, 0.300000, 2.750000 | 0 detected | [4, 2] | 1 | 1 | [0, 461] |
| objects/Door_Prison_0_lod1.obj | 68 | 28 | 80 | 1.600000, 0.200000, 2.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Prison_1.obj | 363 | 96 | 156 | 5.200000, 0.200000, 2.950000 | 0 detected | [4, 2] | 1 | 1 | [0, 363] |
| objects/Door_Prison_1_lod1.obj | 60 | 60 | 70 | 5.200000, 0.200000, 2.950000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Prison_2.obj | 463 | 120 | 216 | 4.200000, 0.200000, 3.950000 | 0 detected | [4, 2] | 1 | 1 | [0, 463] |
| objects/Door_Prison_2_lod1.obj | 61 | 61 | 60 | 4.200000, 0.200000, 3.950000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Prison_Slide.obj | 459 | 95 | 232 | 1.600000, 2.750000, 0.300000 | 0 detected | [4, 2] | 1 | 1 | [0, 459] |
| objects/Door_Prison_Slide_lod1.obj | 69 | 29 | 80 | 1.600000, 2.750000, 0.200000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Prison_Swing.obj | 459 | 95 | 232 | 1.600000, 2.750000, 0.300000 | 0 detected | [4, 2] | 1 | 1 | [0, 459] |
| objects/Door_Prison_Swing_lod1.obj | 69 | 29 | 80 | 1.600000, 2.750000, 0.200000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Research.obj | 93 | 24 | 44 | 2.600000, 2.850000, 0.300000 | 0 detected | [4, 2] | 2 | 2 | [0, 93] |
| objects/Door_Research_lod1.obj | 48 | 16 | 28 | 2.600000, 2.850000, 0.300000 | 0 detected | no paired PNG | — | — | — |
| objects/Door_Stable_Bottom.obj | 110 | 22 | 40 | 2.450000, 0.250000, 1.500000 | 0 detected | [2, 1] | 2 | 2 | [0, 110] |
| objects/Door_Stable_Top.obj | 101 | 21 | 38 | 2.450000, 0.250000, 1.300000 | 0 detected | [2, 1] | 2 | 2 | [0, 101] |
| objects/Door_Vault.obj | 238 | 98 | 154 | 2.547664, 0.668234, 2.887055 | 0 detected | [2, 2] | 4 | 4 | [0, 238] |
| objects/Door_Vault_lod1.obj | 24 | 8 | 12 | 2.450978, 0.422451, 2.887055 | 0 detected | no paired PNG | — | — | — |
| objects/Doubledoor_Birch.obj | 220 | 60 | 120 | 4.020051, 0.200000, 3.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 220] |
| objects/Doubledoor_Birch_lod1.obj | 52 | 15 | 30 | 4.020051, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Doubledoor_Maple.obj | 220 | 60 | 120 | 4.020051, 0.200000, 3.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 220] |
| objects/Doubledoor_Maple_lod1.obj | 52 | 15 | 30 | 4.020051, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Doubledoor_Metal.obj | 220 | 60 | 120 | 4.020051, 0.200000, 3.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 220] |
| objects/Doubledoor_Metal_lod1.obj | 52 | 15 | 30 | 4.020051, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Doubledoor_Pine.obj | 220 | 60 | 120 | 4.020051, 0.200000, 3.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 220] |
| objects/Doubledoor_Pine_lod1.obj | 52 | 15 | 30 | 4.020051, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Drill.obj | 1438 | 435 | 708 | 25.612986, 66.678921, 20.333749 | 6×4, 12×2 | [4, 2] | 7 | 7 | [0, 1438] |
| objects/Drill_lod1.obj | 66 | 32 | 38 | 5.999996, 58.077522, 6.977632 | 0 detected | no paired PNG | — | — | — |
| objects/Dryer_0.obj | 139 | 52 | 88 | 1.500000, 1.500001, 1.782579 | 12×2 | [2, 2] | 4 | 4 | [0, 139] |
| objects/Dryer_0_door.obj | 62 | 24 | 44 | 1.100803, 1.100804, 0.125001 | 12×2 | no paired PNG | — | — | — |
| objects/Dryer_0_door_lod1.obj | 14 | 7 | 10 | 1.027063, 1.100803, 0.125001 | 0 detected | no paired PNG | — | — | — |
| objects/Dryer_0_lod1.obj | 48 | 16 | 28 | 1.500000, 1.500001, 1.782579 | 0 detected | no paired PNG | — | — | — |
| objects/Dumpster_0.obj | 96 | 36 | 56 | 2.837136, 1.500000, 1.715000 | 0 detected | [2, 2] | 3 | 3 | [0, 96] |
| objects/Dumpster_0_lod1.obj | 56 | 20 | 36 | 2.500001, 1.500000, 1.715000 | 0 detected | no paired PNG | — | — | — |
| objects/Dumpster_1.obj | 96 | 36 | 56 | 2.837136, 1.500000, 1.715000 | 0 detected | [2, 2] | 3 | 3 | [0, 96] |
| objects/Dumpster_1_lod1.obj | 56 | 20 | 36 | 2.500001, 1.500000, 1.715000 | 0 detected | no paired PNG | — | — | — |
| objects/Dumpster_2.obj | 125 | 42 | 68 | 6.311437, 3.999999, 2.250000 | 0 detected | [2, 2] | 3 | 3 | [0, 125] |
| objects/Dumpster_2_lod1.obj | 85 | 26 | 48 | 6.000002, 3.999999, 2.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Dumpster_3.obj | 194 | 72 | 126 | 1.109044, 1.089526, 1.449326 | 12×4 | [2, 2] | 3 | 3 | [0, 194] |
| objects/Dumpster_3_Ragdoll_0.obj | 24 | 8 | 12 | 1.000000, 1.000000, 0.100000 | 0 detected | no paired PNG | — | — | — |
| objects/Dumpster_3_lod1.obj | 96 | 32 | 52 | 1.109044, 1.057586, 1.449317 | 6×4 | no paired PNG | — | — | — |
| objects/Dumpster_4.obj | 194 | 72 | 126 | 1.109044, 1.089526, 1.449326 | 12×4 | [2, 2] | 3 | 3 | [0, 194] |
| objects/Dumpster_4_Ragdoll_0.obj | 24 | 8 | 12 | 1.000000, 1.000000, 0.100000 | 0 detected | no paired PNG | — | — | — |
| objects/Dumpster_4_lod1.obj | 96 | 32 | 52 | 1.109044, 1.057586, 1.449317 | 6×4 | no paired PNG | — | — | — |
| objects/Elevator_0.obj | 401 | 150 | 204 | 5.000001, 4.400002, 3.894076 | 6×2 | [4, 2] | 8 | 8 | [0, 401] |
| objects/Elevator_0_lod1.obj | 40 | 14 | 24 | 4.900002, 4.400002, 3.100000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_0.obj | 48 | 24 | 24 | 4.200000, 0.200003, 2.305000 | 0 detected | [8, 8] | 5 | 1 | [0, 48] |
| objects/Fence_Metal_0_Debris.obj | 40 | 16 | 20 | 4.200000, 0.200003, 0.348741 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_0_lod1.obj | 8 | 8 | 4 | 4.001036, 0.000013, 2.141169 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_1.obj | 72 | 30 | 44 | 6.200000, 0.670713, 3.875712 | 0 detected | [8, 8] | 5 | 1 | [0, 72] |
| objects/Fence_Metal_1_lod1.obj | 11 | 11 | 10 | 6.200000, 0.200003, 3.305000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_Broken_0.obj | 64 | 28 | 36 | 4.267927, 1.397042, 2.450510 | 0 detected | [8, 8] | 13 | 1 | [0, 64] |
| objects/Fence_Metal_Broken_0_Debris.obj | 40 | 16 | 20 | 4.257611, 0.286110, 0.299753 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_Broken_0_Ragdoll_0.obj | 32 | 14 | 18 | 1.569250, 1.074750, 2.449857 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_Broken_0_Ragdoll_1.obj | 32 | 14 | 18 | 1.577419, 1.397042, 2.406056 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_Broken_0_lod1.obj | 10 | 10 | 8 | 4.267927, 0.306093, 2.313047 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Metal_Broken_1.obj | 112 | 42 | 68 | 6.200000, 1.818070, 4.065749 | 0 detected | [8, 8] | 13 | 1 | [0, 112] |
| objects/Fence_Metal_Broken_1_lod1.obj | 10 | 10 | 8 | 6.200000, 0.200003, 3.305000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_0.obj | 228 | 65 | 114 | 0.555362, 16.250000, 2.275833 | 0 detected | [2, 2] | 3 | 2 | [0, 228] |
| objects/Fence_Road_0_Debris.obj | 100 | 40 | 50 | 0.500000, 16.250000, 1.433410 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_0_Ragdoll_0.obj | 40 | 14 | 20 | 0.047659, 4.000000, 0.780001 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_0_Ragdoll_1.obj | 40 | 14 | 20 | 0.047659, 4.000000, 0.780001 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_0_Ragdoll_2.obj | 40 | 14 | 20 | 0.047659, 4.000000, 0.780001 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_0_Ragdoll_3.obj | 40 | 14 | 20 | 0.047659, 4.000000, 0.780001 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_0_lod1.obj | 132 | 50 | 66 | 0.555362, 16.250000, 2.275833 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_Broken_0.obj | 232 | 70 | 116 | 2.310875, 16.250000, 2.379453 | 0 detected | [2, 2] | 3 | 2 | [0, 232] |
| objects/Fence_Road_Broken_0_Debris.obj | 80 | 32 | 40 | 1.124277, 16.250000, 1.582212 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_Broken_0_Ragdoll_0.obj | 116 | 33 | 60 | 1.712356, 6.068677, 2.343209 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_Broken_0_Ragdoll_1.obj | 120 | 33 | 60 | 1.830877, 6.218607, 2.379453 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Road_Broken_0_lod1.obj | 108 | 44 | 56 | 1.775787, 16.250000, 2.379104 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Stone_0.obj | 68 | 24 | 26 | 4.700000, 0.700004, 2.250000 | 0 detected | [2, 2] | 2 | 2 | [0, 68] |
| objects/Fence_Stone_0_lod1.obj | 8 | 8 | 6 | 4.000000, 0.500000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Stone_1.obj | 130 | 60 | 66 | 6.700000, 1.685879, 4.460877 | 0 detected | [2, 2] | 3 | 3 | [0, 130] |
| objects/Fence_Stone_1_lod1.obj | 106 | 36 | 46 | 6.700000, 1.685879, 4.460877 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_0.obj | 300 | 112 | 150 | 4.200000, 0.200000, 2.310000 | 0 detected | [4, 2] | 6 | 3 | [0, 300] |
| objects/Fence_Wood_0_Debris.obj | 236 | 88 | 118 | 4.200000, 0.200000, 0.424330 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_0_Ragdoll_0.obj | 180 | 64 | 90 | 2.100001, 0.200000, 2.310000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_0_Ragdoll_1.obj | 180 | 64 | 90 | 2.100000, 0.200000, 2.310000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_0_lod1.obj | 100 | 48 | 50 | 4.200000, 0.200000, 2.310000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_1.obj | 156 | 60 | 78 | 4.250000, 0.250000, 1.250000 | 0 detected | [2, 2] | 3 | 2 | [0, 156] |
| objects/Fence_Wood_1_Debris.obj | 84 | 38 | 42 | 4.250000, 0.250000, 0.400177 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_1_Ragdoll_0.obj | 68 | 32 | 34 | 2.125000, 0.250000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_1_Ragdoll_1.obj | 68 | 32 | 34 | 2.125000, 0.250000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_1_lod1.obj | 48 | 24 | 24 | 3.836356, 0.176130, 1.106543 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_2.obj | 302 | 132 | 182 | 4.113250, 0.241494, 1.500000 | 0 detected | [2, 2] | 4 | 2 | [0, 302] |
| objects/Fence_Wood_2_lod1.obj | 32 | 16 | 16 | 3.673782, 0.066564, 0.753034 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_3.obj | 60 | 24 | 26 | 6.700000, 0.700004, 3.750000 | 0 detected | [2, 2] | 2 | 2 | [0, 60] |
| objects/Fence_Wood_3_lod1.obj | 8 | 8 | 6 | 6.000000, 0.500000, 3.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_Broken_0.obj | 328 | 120 | 164 | 4.200000, 2.327074, 2.552594 | 0 detected | [4, 2] | 6 | 3 | [0, 328] |
| objects/Fence_Wood_Broken_0_Debris.obj | 176 | 64 | 88 | 4.200000, 0.549068, 0.514777 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_Broken_0_Ragdoll_0.obj | 152 | 56 | 76 | 1.503243, 0.921799, 2.443923 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_Broken_0_Ragdoll_1.obj | 152 | 56 | 76 | 1.576399, 0.868624, 2.381510 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_Broken_0_lod1.obj | 40 | 16 | 20 | 4.200000, 0.200000, 2.310000 | 0 detected | no paired PNG | — | — | — |
| objects/Fence_Wood_Broken_3.obj | 108 | 40 | 48 | 6.700000, 1.238012, 3.962818 | 0 detected | [2, 2] | 2 | 2 | [0, 108] |
| objects/Fence_Wood_Broken_3_lod1.obj | 16 | 10 | 10 | 2.350163, 0.977089, 3.712818 | 0 detected | no paired PNG | — | — | — |
| objects/Files_0.obj | 144 | 56 | 72 | 0.781251, 1.029436, 2.140978 | 0 detected | [2, 2] | 4 | 3 | [0, 144] |
| objects/Files_0_Debris.obj | 144 | 56 | 72 | 0.781251, 1.212003, 2.140978 | 0 detected | no paired PNG | — | — | — |
| objects/Files_0_lod1.obj | 24 | 8 | 12 | 0.781251, 0.937500, 2.140978 | 0 detected | no paired PNG | — | — | — |
| objects/Files_1.obj | 184 | 72 | 92 | 0.781251, 1.002616, 2.140978 | 0 detected | [2, 2] | 4 | 3 | [0, 184] |
| objects/Files_1_Debris.obj | 184 | 72 | 92 | 0.781251, 1.067243, 2.140978 | 0 detected | no paired PNG | — | — | — |
| objects/Files_1_lod1.obj | 24 | 8 | 12 | 0.781251, 0.937500, 2.140978 | 0 detected | no paired PNG | — | — | — |
| objects/Fire_0.obj | 873 | 324 | 572 | 16.094684, 24.504135, 11.750000 | 0 detected | [4, 2] | 8 | 7 | [0, 873] |
| objects/Fire_0_lod1.obj | 811 | 292 | 524 | 16.094684, 24.504135, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Fire_0_lod2.obj | 311 | 108 | 220 | 16.000002, 24.400008, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Fire_Hydrant_0.obj | 218 | 120 | 200 | 0.652202, 0.537118, 2.146615 | 6×4, 12×7 | [2, 2] | 4 | 2 | [0, 218] |
| objects/Fire_Hydrant_0_lod1.obj | 59 | 25 | 42 | 0.433012, 0.500000, 2.100000 | 6×4 | no paired PNG | — | — | — |
| objects/Firetruck.obj | 676 | 254 | 432 | 2.902001, 7.699010, 2.967035 | 6×4 | [4, 2] | 7 | 7 | [0, 676] |
| objects/Firetruck_lod1.obj | 380 | 142 | 274 | 2.902001, 7.520614, 2.757921 | 0 detected | no paired PNG | — | — | — |
| objects/Flag_0.obj | 27 | 11 | 13 | 0.100000, 1.050000, 2.750000 | 0 detected | [2, 1] | 2 | 2 | [0, 27] |
| objects/Flag_American.obj | 42 | 18 | 20 | 0.200000, 4.100000, 12.000000 | 0 detected | [32, 16] | 11 | 3 | [0, 42] |
| objects/Flag_American_lod1.obj | 13 | 13 | 10 | 0.200000, 4.100000, 12.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Flag_Canadian.obj | 42 | 18 | 20 | 0.200000, 4.100000, 12.000000 | 0 detected | [32, 16] | 11 | 3 | [0, 42] |
| objects/Flag_Canadian_Ragdoll_0.obj | 42 | 18 | 20 | 0.200000, 4.100000, 12.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Flag_Canadian_lod1.obj | 13 | 13 | 10 | 0.200000, 4.100000, 12.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_0.obj | 94 | 40 | 46 | 1.326062, 0.795886, 2.500000 | 0 detected | [2, 2] | 2 | 2 | [0, 94] |
| objects/Fridge_0_door.obj | 52 | 16 | 22 | 1.326062, 0.150000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_0_door_lod1.obj | 24 | 12 | 14 | 1.326062, 0.100000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_0_lod1.obj | 36 | 12 | 20 | 1.326062, 0.795886, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_1.obj | 94 | 40 | 46 | 1.326062, 0.795886, 2.500000 | 0 detected | [2, 2] | 2 | 2 | [0, 94] |
| objects/Fridge_1_door.obj | 52 | 16 | 22 | 1.326062, 0.150000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_1_door_lod1.obj | 24 | 12 | 14 | 1.326062, 0.100000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_1_lod1.obj | 36 | 12 | 20 | 1.326062, 0.795886, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_Random_door.obj | 52 | 16 | 22 | 1.326062, 0.150000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Fridge_Random_door_lod1.obj | 24 | 12 | 14 | 1.326062, 0.100000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Garage_Birch.obj | 516 | 252 | 362 | 5.600002, 0.750005, 4.264650 | 8×26 | [2, 2] | 3 | 3 | [0, 516] |
| objects/Garage_Birch_lod1.obj | 87 | 34 | 52 | 5.600000, 0.750005, 4.160914 | 0 detected | no paired PNG | — | — | — |
| objects/Garage_Brick.obj | 128 | 48 | 88 | 5.500002, 0.750005, 4.264650 | 0 detected | [2, 2] | 3 | 2 | [0, 128] |
| objects/Garage_Brick_lod1.obj | 95 | 32 | 56 | 5.500002, 0.750005, 4.264650 | 0 detected | no paired PNG | — | — | — |
| objects/Garage_Maple.obj | 516 | 252 | 362 | 5.600002, 0.750005, 4.264650 | 8×26 | [2, 2] | 3 | 3 | [0, 516] |
| objects/Garage_Maple_lod1.obj | 87 | 34 | 52 | 5.600000, 0.750005, 4.160914 | 0 detected | no paired PNG | — | — | — |
| objects/Garage_Metal.obj | 128 | 48 | 88 | 5.500002, 0.750005, 4.264650 | 0 detected | [2, 2] | 3 | 3 | [0, 128] |
| objects/Garage_Metal_lod1.obj | 95 | 32 | 56 | 5.500002, 0.750005, 4.264650 | 0 detected | no paired PNG | — | — | — |
| objects/Garage_Pine.obj | 516 | 252 | 362 | 5.600002, 0.750005, 4.264650 | 8×26 | [2, 2] | 3 | 3 | [0, 516] |
| objects/Garage_Pine_lod1.obj | 87 | 34 | 52 | 5.600000, 0.750005, 4.160914 | 0 detected | no paired PNG | — | — | — |
| objects/Garbage_0.obj | 80 | 28 | 40 | 0.943052, 0.921270, 1.416428 | 0 detected | no paired PNG | — | — | — |
| objects/Garbage_0_lod1.obj | 35 | 18 | 20 | 0.943052, 0.921270, 1.364297 | 0 detected | no paired PNG | — | — | — |
| objects/Garbage_1.obj | 80 | 28 | 40 | 0.943052, 0.921270, 1.416428 | 0 detected | no paired PNG | — | — | — |
| objects/Garbage_1_lod1.obj | 35 | 18 | 20 | 0.943052, 0.921270, 1.364297 | 0 detected | no paired PNG | — | — | — |
| objects/Gas_0.obj | 780 | 282 | 473 | 12.217226, 20.190496, 11.750000 | 0 detected | [4, 2] | 8 | 8 | [0, 780] |
| objects/Gas_0_lod1.obj | 160 | 58 | 118 | 12.000005, 20.000008, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Gas_Cover_0.obj | 188 | 80 | 100 | 16.200000, 24.000008, 8.792372 | 0 detected | [2, 2] | 4 | 4 | [0, 188] |
| objects/Gas_Cover_0_lod1.obj | 68 | 28 | 38 | 16.200000, 24.000008, 8.792372 | 0 detected | no paired PNG | — | — | — |
| objects/Gas_Pump_0.obj | 104 | 40 | 52 | 1.327953, 0.734164, 2.386103 | 0 detected | [2, 2] | 4 | 4 | [0, 104] |
| objects/Gas_Pump_0_lod1.obj | 40 | 16 | 20 | 1.193110, 0.692838, 2.386103 | 0 detected | no paired PNG | — | — | — |
| objects/Gas_Sign_0.obj | 940 | 414 | 611 | 4.101336, 0.666918, 10.000000 | 0 detected | [2, 2] | 4 | 4 | [0, 940] |
| objects/Gas_Sign_0_lod1.obj | 665 | 272 | 398 | 4.000000, 0.603720, 10.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Gas_Sign_0_lod2.obj | 60 | 20 | 32 | 4.000000, 0.500000, 10.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Gate_Birch.obj | 48 | 16 | 24 | 4.000000, 0.505976, 3.750000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Gate_Birch_lod1.obj | 20 | 8 | 12 | 4.000000, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Gate_Maple.obj | 48 | 16 | 24 | 4.000000, 0.505976, 3.750000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Gate_Maple_lod1.obj | 20 | 8 | 12 | 4.000000, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Gate_Metal.obj | 48 | 16 | 24 | 4.000000, 0.505976, 3.750000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Gate_Metal_lod1.obj | 20 | 8 | 12 | 4.000000, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Gate_Pine.obj | 48 | 16 | 24 | 4.000000, 0.505976, 3.750000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Gate_Pine_lod1.obj | 20 | 8 | 12 | 4.000000, 0.200000, 3.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Generator_0.obj | 224 | 72 | 140 | 1.500000, 1.713412, 1.339637 | 6×2 | [2, 2] | 3 | 3 | [0, 224] |
| objects/Generator_0_lod1.obj | 53 | 37 | 62 | 1.500000, 1.713412, 1.339637 | 6×1 | no paired PNG | — | — | — |
| objects/Glass_0.obj | 30 | 12 | 16 | 2.000000, 0.100000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Glass_1.obj | 40 | 14 | 22 | 2.000000, 0.100000, 1.336628 | 0 detected | no paired PNG | — | — | — |
| objects/Glass_1_lod1.obj | 20 | 8 | 10 | 2.000000, 0.100000, 1.336628 | 0 detected | no paired PNG | — | — | — |
| objects/Grass_0.obj | 88 | 81 | 128 | 33.809317, 33.763241, 5.827096 | 0 detected | [64, 64] | 76 | 51 | [0, 88] |
| objects/Grass_0_lod1.obj | 33 | 33 | 32 | 33.809317, 33.763241, 4.958410 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_0.obj | 230 | 100 | 150 | 1.500000, 0.546294, 2.000000 | 0 detected | [2, 1] | 2 | 2 | [0, 230] |
| objects/Grave_0_Debris.obj | 32 | 16 | 22 | 1.500000, 0.500000, 0.296923 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_0_HW.obj | 230 | 100 | 150 | 1.500000, 0.546294, 2.000000 | 0 detected | [2, 1] | 2 | 2 | [0, 230] |
| objects/Grave_0_HW_lod1.obj | 24 | 8 | 12 | 1.500000, 0.500000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_0_Ragdoll_0.obj | 230 | 100 | 150 | 1.500000, 0.546294, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_0_lod1.obj | 189 | 78 | 117 | 1.500000, 0.546294, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_0_lod2.obj | 36 | 12 | 18 | 1.500000, 0.500000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_1_HW.obj | 316 | 108 | 152 | 1.274886, 3.000000, 0.750000 | 0 detected | [2, 1] | 2 | 2 | [0, 316] |
| objects/Grave_1_HW_lod1.obj | 32 | 12 | 20 | 1.250000, 3.000000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_2_HW.obj | 338 | 114 | 164 | 1.274886, 3.000000, 0.750000 | 0 detected | [2, 1] | 2 | 2 | [0, 338] |
| objects/Grave_2_HW_lod1.obj | 32 | 12 | 20 | 1.250000, 3.000000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_Keig.obj | 64 | 28 | 44 | 2.000000, 1.000000, 3.000000 | 0 detected | [2, 1] | 2 | 2 | [0, 64] |
| objects/Grave_Keig_lod1.obj | 11 | 8 | 10 | 1.500000, 0.500000, 1.975000 | 0 detected | no paired PNG | — | — | — |
| objects/Grave_Swansong.obj | 64 | 28 | 44 | 2.000000, 1.000000, 3.000000 | 0 detected | [2, 1] | 2 | 2 | [0, 64] |
| objects/Grave_Swansong_lod1.obj | 11 | 8 | 10 | 1.500000, 0.500000, 1.975000 | 0 detected | no paired PNG | — | — | — |
| objects/Grocer_0.obj | 1876 | 715 | 1345 | 20.238600, 24.614062, 12.863635 | 0 detected | [4, 2] | 6 | 6 | [0, 1876] |
| objects/Grocer_0_lod1.obj | 1755 | 642 | 1239 | 20.238600, 24.614062, 12.863635 | 0 detected | no paired PNG | — | — | — |
| objects/Grocer_0_lod2.obj | 570 | 207 | 458 | 20.000007, 24.500015, 12.863635 | 0 detected | no paired PNG | — | — | — |
| objects/Guns_0.obj | 811 | 272 | 423 | 18.000007, 11.100009, 10.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 811] |
| objects/Guns_0_lod1.obj | 681 | 218 | 342 | 18.000007, 11.100009, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Guns_0_lod2.obj | 193 | 62 | 106 | 18.000007, 11.000006, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Hangar_0.obj | 1119 | 412 | 612 | 40.000008, 38.000016, 22.266508 | 0 detected | [2, 2] | 4 | 4 | [0, 1119] |
| objects/Hangar_0_lod1.obj | 793 | 315 | 442 | 40.000008, 38.000016, 22.266508 | 0 detected | no paired PNG | — | — | — |
| objects/Hangar_1.obj | 898 | 292 | 438 | 24.000000, 28.000000, 13.405695 | 0 detected | [2, 2] | 4 | 4 | [0, 898] |
| objects/Hangar_1_lod1.obj | 735 | 245 | 356 | 24.000000, 28.000000, 13.405695 | 0 detected | no paired PNG | — | — | — |
| objects/Harbor_0.obj | 729 | 199 | 380 | 100.500000, 18.000017, 20.750008 | 6×4 | [2, 2] | 4 | 4 | [0, 729] |
| objects/Harbor_0_gantry.obj | 729 | 199 | 306 | 100.500000, 18.000017, 20.750008 | 0 detected | no paired PNG | — | — | — |
| objects/Harbor_0_hoistblk.obj | 729 | 199 | 10 | 100.500000, 18.000017, 5.187502 | 0 detected | no paired PNG | — | — | — |
| objects/Harbor_0_lod1.obj | 282 | 138 | 262 | 100.500000, 18.000013, 19.250001 | 6×2 | no paired PNG | — | — | — |
| objects/Harbor_0_trolley.obj | 729 | 199 | 74 | 100.500000, 18.000017, 20.750008 | 6×4 | no paired PNG | — | — | — |
| objects/Hardware_0.obj | 1489 | 524 | 843 | 16.100002, 23.100003, 11.750000 | 0 detected | [4, 2] | 8 | 6 | [0, 1489] |
| objects/Hardware_0_lod1.obj | 1291 | 424 | 693 | 16.100002, 23.100003, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Hardware_0_lod2.obj | 311 | 100 | 187 | 16.000007, 23.000008, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Hatch_Birch.obj | 48 | 16 | 24 | 1.800000, 1.800000, 0.400000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Hatch_Birch_lod1.obj | 20 | 8 | 12 | 1.800000, 1.800000, 0.200000 | 0 detected | no paired PNG | — | — | — |
| objects/Hatch_Maple.obj | 48 | 16 | 24 | 1.800000, 1.800000, 0.400000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Hatch_Maple_lod1.obj | 20 | 8 | 12 | 1.800000, 1.800000, 0.200000 | 0 detected | no paired PNG | — | — | — |
| objects/Hatch_Metal.obj | 48 | 16 | 24 | 1.800000, 1.800000, 0.400000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Hatch_Metal_lod1.obj | 20 | 8 | 12 | 1.800000, 1.800000, 0.200000 | 0 detected | no paired PNG | — | — | — |
| objects/Hatch_Pine.obj | 48 | 16 | 24 | 1.800000, 1.800000, 0.400000 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Hatch_Pine_lod1.obj | 20 | 8 | 12 | 1.800000, 1.800000, 0.200000 | 0 detected | no paired PNG | — | — | — |
| objects/Hatchback_Blue.obj | 567 | 204 | 378 | 2.824347, 5.522967, 2.453282 | 0 detected | [4, 2] | 7 | 7 | [0, 567] |
| objects/Hatchback_Blue_lod1.obj | 371 | 132 | 272 | 2.813497, 5.303303, 2.337905 | 0 detected | no paired PNG | — | — | — |
| objects/Hatchback_Green.obj | 567 | 204 | 378 | 2.824347, 5.522967, 2.453282 | 0 detected | [4, 2] | 7 | 7 | [0, 567] |
| objects/Hatchback_Green_lod1.obj | 371 | 132 | 272 | 2.813497, 5.303303, 2.337905 | 0 detected | no paired PNG | — | — | — |
| objects/HayBale_0.obj | 48 | 24 | 44 | 2.500002, 2.414816, 2.414818 | 12×2 | no paired PNG | — | — | — |
| objects/HayBale_0_lod1.obj | 36 | 12 | 20 | 2.500002, 2.500000, 2.165065 | 6×2 | no paired PNG | — | — | — |
| objects/HayBale_1.obj | 24 | 8 | 12 | 2.000000, 1.220726, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Hedge_0.obj | 60 | 48 | 30 | 4.566294, 1.959993, 2.134186 | 0 detected | [64, 64] | 59 | 5 | [0, 60] |
| objects/Hedge_0_lod1.obj | 36 | 24 | 18 | 4.395344, 1.959993, 2.134186 | 0 detected | no paired PNG | — | — | — |
| objects/Hedge_0_lod2.obj | 20 | 8 | 10 | 4.310000, 1.200000, 1.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Heli_1.obj | 627 | 198 | 330 | 11.514004, 11.389740, 5.246122 | 6×2 | [4, 2] | 6 | 6 | [0, 627] |
| objects/Heli_1_lod1.obj | 79 | 28 | 56 | 3.000001, 10.403975, 2.750002 | 0 detected | no paired PNG | — | — | — |
| objects/Heli_2.obj | 971 | 297 | 476 | 11.514004, 15.804661, 5.547867 | 6×2 | [4, 2] | 6 | 6 | [0, 971] |
| objects/Helipad_0.obj | 120 | 44 | 64 | 15.000006, 15.000007, 0.600000 | 0 detected | [2, 1] | 2 | 2 | [0, 120] |
| objects/Helipad_0_lod1.obj | 20 | 8 | 10 | 15.000006, 15.000007, 0.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Hesco_0.obj | 31 | 9 | 14 | 2.500001, 2.500001, 2.595893 | 0 detected | [16, 16] | 8 | 2 | [0, 31] |
| objects/Hockey_0.obj | 436 | 247 | 285 | 21.000000, 41.000000, 3.000000 | 12×10 | [2, 2] | 4 | 4 | [0, 436] |
| objects/Hockey_0_lod1.obj | 232 | 88 | 132 | 21.000000, 41.000000, 3.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Hockey_1.obj | 215 | 61 | 118 | 6.750005, 2.752971, 3.000004 | 0 detected | [8, 8] | 24 | 1 | [0, 215] |
| objects/House_00.obj | 1022 | 380 | 732 | 16.499994, 22.500003, 12.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1022] |
| objects/House_00_lod1.obj | 428 | 154 | 320 | 16.000257, 22.000008, 12.750000 | 0 detected | no paired PNG | — | — | — |
| objects/House_00_ported.obj | 1816 | 452 | 868 | 16.500000, 22.500000, 12.965940 | 0 detected | [4, 2] | 3 | 3 | [1816, 1816] |
| objects/House_00_ported_lod1.obj | 159 | 159 | 292 | 16.126080, 22.500000, 12.965940 | 0 detected | no paired PNG | — | — | — |
| objects/House_01.obj | 749 | 263 | 508 | 12.999998, 25.000000, 11.750000 | 0 detected | [4, 2] | 8 | 8 | [0, 749] |
| objects/House_01_lod1.obj | 335 | 117 | 244 | 12.000005, 24.000010, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/House_02.obj | 887 | 311 | 601 | 21.000008, 17.000000, 13.734800 | 0 detected | [4, 2] | 8 | 8 | [0, 887] |
| objects/House_02_lod1.obj | 423 | 145 | 298 | 20.000008, 16.000008, 13.834800 | 0 detected | no paired PNG | — | — | — |
| objects/House_03.obj | 2759 | 977 | 1948 | 35.000002, 14.999998, 17.500000 | 0 detected | [4, 2] | 6 | 6 | [0, 2759] |
| objects/House_03_lod1.obj | 1176 | 420 | 889 | 34.000014, 14.000006, 17.500000 | 0 detected | no paired PNG | — | — | — |
| objects/House_04.obj | 796 | 282 | 559 | 20.999996, 16.999993, 12.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 796] |
| objects/House_04_lod1.obj | 364 | 128 | 272 | 20.000008, 16.006767, 12.749954 | 0 detected | no paired PNG | — | — | — |
| objects/House_05.obj | 1706 | 617 | 1206 | 27.000002, 19.000001, 17.500000 | 0 detected | [4, 2] | 6 | 6 | [0, 1706] |
| objects/House_05_lod1.obj | 765 | 278 | 573 | 26.000006, 18.000006, 17.500000 | 0 detected | no paired PNG | — | — | — |
| objects/House_06.obj | 939 | 359 | 675 | 20.500000, 20.999998, 13.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 939] |
| objects/House_06_lod1.obj | 430 | 157 | 316 | 20.000002, 20.000004, 13.750000 | 0 detected | no paired PNG | — | — | — |
| objects/House_07.obj | 1159 | 414 | 837 | 17.000007, 20.987703, 13.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1159] |
| objects/House_07_lod1.obj | 571 | 205 | 434 | 16.000007, 20.000008, 13.750004 | 0 detected | no paired PNG | — | — | — |
| objects/House_08.obj | 1070 | 389 | 774 | 24.999996, 16.999993, 13.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1070] |
| objects/House_08_lod1.obj | 448 | 164 | 346 | 24.000010, 16.000008, 13.750000 | 0 detected | no paired PNG | — | — | — |
| objects/House_09.obj | 748 | 267 | 516 | 17.000003, 23.499993, 13.750000 | 0 detected | [4, 2] | 7 | 7 | [0, 748] |
| objects/House_09_lod1.obj | 350 | 127 | 252 | 16.000007, 22.500004, 13.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Humvee_Desert.obj | 681 | 216 | 380 | 2.632835, 5.361199, 2.514174 | 6×4 | [4, 2] | 7 | 7 | [0, 681] |
| objects/Humvee_Desert_lod1.obj | 300 | 96 | 182 | 2.632835, 5.295084, 2.514174 | 0 detected | no paired PNG | — | — | — |
| objects/Ice_0.obj | 4 | 4 | 2 | 20.000000, 20.000000, 0.000000 | 0 detected | [256, 256] | 4 | 1 | [0, 4] |
| objects/Ice_1.obj | 9 | 9 | 8 | 320.000000, 320.000000, 0.000000 | 0 detected | [256, 256] | 4 | 1 | [0, 9] |
| objects/Ice_Box_0.obj | 221 | 100 | 145 | 2.000000, 1.000000, 2.473373 | 0 detected | [2, 2] | 3 | 3 | [0, 221] |
| objects/Ice_Box_0_lod1.obj | 177 | 70 | 101 | 2.000000, 1.000000, 2.473373 | 0 detected | no paired PNG | — | — | — |
| objects/Ice_Box_0_lod2.obj | 52 | 20 | 30 | 2.000000, 1.000000, 2.473373 | 0 detected | no paired PNG | — | — | — |
| objects/Icicle_0.obj | 36 | 15 | 12 | 0.694716, 0.252506, 1.346039 | 0 detected | no paired PNG | — | — | — |
| objects/Icicle_0_XMAS.obj | 36 | 15 | 12 | 0.694716, 0.252506, 1.346039 | 0 detected | no paired PNG | — | — | — |
| objects/Icicle_0_XMAS_lod1.obj | 5 | 5 | 4 | 0.252506, 0.252506, 1.346039 | 0 detected | no paired PNG | — | — | — |
| objects/Icicle_1.obj | 36 | 15 | 12 | 0.720832, 0.250580, 1.052319 | 0 detected | no paired PNG | — | — | — |
| objects/Icicle_1_XMAS.obj | 36 | 15 | 12 | 0.720832, 0.250580, 1.052319 | 0 detected | no paired PNG | — | — | — |
| objects/Igloo_0.obj | 182 | 70 | 126 | 6.507181, 5.196153, 9.000000 | 6×2 | [64, 64] | 145 | 10 | [0, 182] |
| objects/Inukshuk.obj | 124 | 48 | 62 | 0.527360, 1.536949, 1.878077 | 0 detected | [2, 2] | 4 | 4 | [0, 124] |
| objects/Inukshuk_Ragdoll_0.obj | 124 | 48 | 62 | 0.527360, 1.536949, 1.878077 | 0 detected | no paired PNG | — | — | — |
| objects/Inukshuk_lod1.obj | 20 | 8 | 12 | 0.500000, 1.536949, 0.266887 | 0 detected | no paired PNG | — | — | — |
| objects/JasperMemorial.obj | 96 | 32 | 56 | 0.065721, 1.000000, 1.250947 | 0 detected | [512, 512] | 11 | 9 | [0, 96] |
| objects/JasperMemorial_lod1.obj | 13 | 9 | 14 | 0.050000, 1.000000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Jeep_Desert.obj | 540 | 200 | 344 | 2.659993, 5.160452, 2.431816 | 6×4 | [4, 2] | 7 | 7 | [0, 540] |
| objects/Jeep_Desert_lod1.obj | 199 | 66 | 118 | 2.659993, 5.048070, 2.375631 | 0 detected | no paired PNG | — | — | — |
| objects/Junglegym_0.obj | 212 | 86 | 116 | 1.750000, 5.250000, 3.250000 | 0 detected | [4, 2] | 2 | 2 | [0, 212] |
| objects/Kiln_0.obj | 681 | 317 | 543 | 4.861702, 4.290720, 2.174933 | 8×4, 10×9 | [64, 64] | 34 | 30 | [0, 681] |
| objects/Ladder_Metal_0.obj | 192 | 88 | 96 | 1.150002, 0.150001, 6.750002 | 0 detected | [2, 1] | 2 | 2 | [0, 192] |
| objects/Ladder_Metal_0_lod1.obj | 48 | 16 | 24 | 1.150002, 0.150001, 6.750002 | 0 detected | no paired PNG | — | — | — |
| objects/Ladder_Wood_0.obj | 192 | 88 | 96 | 1.150002, 0.150001, 6.750002 | 0 detected | [2, 1] | 2 | 2 | [0, 192] |
| objects/Ladder_Wood_0_lod1.obj | 48 | 16 | 24 | 1.150002, 0.150001, 6.750002 | 0 detected | no paired PNG | — | — | — |
| objects/Lamp_0.obj | 80 | 32 | 48 | 0.405000, 0.300438, 0.881908 | 0 detected | [2, 2] | 2 | 2 | [0, 80] |
| objects/Lamp_0_Ragdoll_0.obj | 80 | 32 | 48 | 0.405000, 0.300438, 0.881908 | 0 detected | no paired PNG | — | — | — |
| objects/Lamp_0_lod1.obj | 40 | 12 | 20 | 0.354885, 0.156587, 0.808773 | 0 detected | no paired PNG | — | — | — |
| objects/Lamp_1.obj | 88 | 24 | 40 | 0.741766, 0.741766, 2.300000 | 0 detected | [2, 2] | 3 | 3 | [0, 88] |
| objects/Lamp_1_Ragdoll_0.obj | 68 | 16 | 28 | 0.741766, 0.741766, 2.225000 | 0 detected | no paired PNG | — | — | — |
| objects/Lamp_1_lod1.obj | 34 | 16 | 16 | 0.741766, 0.741766, 2.225000 | 0 detected | no paired PNG | — | — | — |
| objects/Landmine_0.obj | 51 | 16 | 22 | 0.500000, 0.500000, 0.150000 | 0 detected | [2, 1] | 2 | 2 | [0, 51] |
| objects/Landmine_0_lod1.obj | 24 | 12 | 14 | 0.500000, 0.500000, 0.125000 | 0 detected | no paired PNG | — | — | — |
| objects/Library_0.obj | 1077 | 349 | 564 | 22.000010, 20.116699, 10.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1077] |
| objects/Library_0_lod1.obj | 902 | 281 | 462 | 22.000010, 20.116699, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Library_0_lod2.obj | 232 | 67 | 130 | 22.000010, 20.000011, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Lifeguard_0.obj | 334 | 115 | 210 | 1.302298, 1.281148, 3.275000 | 0 detected | [2, 1] | 2 | 2 | [0, 334] |
| objects/Lifeguard_0_Ragdoll_0.obj | 334 | 115 | 210 | 1.302298, 1.281148, 3.275000 | 0 detected | no paired PNG | — | — | — |
| objects/Lifeguard_0_lod1.obj | 4 | 4 | 4 | 0.875000, 1.000000, 0.650000 | 0 detected | no paired PNG | — | — | — |
| objects/Lifeguard_1.obj | 258 | 64 | 128 | 1.250000, 1.250000, 0.250000 | 8×16 | [2, 1] | 2 | 2 | [0, 258] |
| objects/Lifeguard_1_Ragdoll_0.obj | 258 | 64 | 128 | 1.250000, 1.250000, 0.250000 | 8×16 | no paired PNG | — | — | — |
| objects/Lifeguard_1_lod1.obj | 116 | 24 | 48 | 1.250000, 1.250000, 0.216506 | 8×3 | no paired PNG | — | — | — |
| objects/Light_0.obj | 118 | 48 | 60 | 1.000000, 4.000002, 1.000000 | 0 detected | [2, 2] | 3 | 3 | [0, 118] |
| objects/Light_0_Ragdoll_0.obj | 78 | 32 | 40 | 1.000000, 4.000002, 0.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Light_0_lod1.obj | 24 | 8 | 12 | 1.000000, 3.999998, 0.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Lighthouse_0.obj | 1210 | 386 | 654 | 10.900943, 10.900942, 50.966599 | 8×16 | [4, 2] | 7 | 7 | [0, 1210] |
| objects/Lighthouse_0_lod1.obj | 495 | 177 | 280 | 10.586556, 10.586554, 50.966599 | 0 detected | no paired PNG | — | — | — |
| objects/Line_Parking_0.obj | 16 | 16 | 8 | 12.500000, 5.000000, 0.000000 | 0 detected | [2, 2] | 1 | 1 | [0, 16] |
| objects/Lodge_0.obj | 786 | 195 | 369 | 27.693808, 27.696823, 14.250000 | 0 detected | [4, 2] | 6 | 6 | [0, 786] |
| objects/Lodge_0_lod1.obj | 438 | 108 | 215 | 27.071068, 27.071068, 14.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Lodge_1.obj | 979 | 244 | 510 | 16.501138, 13.018819, 12.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 979] |
| objects/Lodge_1_lod1.obj | 571 | 143 | 324 | 15.500005, 12.000006, 12.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Lodge_2.obj | 815 | 282 | 429 | 13.000572, 13.018819, 12.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 815] |
| objects/Lodge_2_lod1.obj | 118 | 47 | 87 | 12.000000, 12.000000, 12.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Lodge_3.obj | 2163 | 666 | 1328 | 33.000000, 17.000000, 15.250000 | 0 detected | [4, 2] | 6 | 6 | [0, 2163] |
| objects/Lodge_3_lod1.obj | 657 | 295 | 598 | 33.000000, 17.000000, 15.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Logs_0.obj | 432 | 144 | 264 | 9.911975, 16.321847, 4.258051 | 12×12 | [4, 2] | 8 | 8 | [0, 432] |
| objects/Logs_0_lod1.obj | 216 | 72 | 120 | 9.839003, 16.309091, 4.213761 | 6×12 | no paired PNG | — | — | — |
| objects/Logs_0_lod2.obj | 144 | 48 | 72 | 10.164019, 16.365005, 4.676025 | 0 detected | no paired PNG | — | — | — |
| objects/Loom_0.obj | 1460 | 616 | 976 | 1.738223, 1.922347, 1.886203 | 6×15, 8×4 | [64, 64] | 43 | 38 | [0, 1460] |
| objects/Mailbox_0.obj | 90 | 32 | 50 | 0.541979, 0.870346, 1.705032 | 0 detected | [2, 2] | 3 | 3 | [0, 90] |
| objects/Mailbox_0_Ragdoll_0.obj | 90 | 32 | 50 | 0.541979, 0.870346, 1.705032 | 0 detected | no paired PNG | — | — | — |
| objects/Mailbox_0_lod1.obj | 48 | 20 | 28 | 0.500000, 0.870346, 1.551651 | 0 detected | no paired PNG | — | — | — |
| objects/Mall_0.obj | 5387 | 1409 | 2817 | 42.000018, 38.120785, 15.500000 | 0 detected | [4, 2] | 8 | 7 | [0, 5387] |
| objects/Mall_0_lod1.obj | 1298 | 523 | 1144 | 42.000018, 38.000012, 15.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Manhole_0.obj | 202 | 88 | 114 | 2.000000, 2.000000, 1.132727 | 12×2 | [2, 2] | 4 | 2 | [0, 202] |
| objects/Manhole_0_lod1.obj | 30 | 12 | 16 | 1.732050, 2.000000, 1.100000 | 6×2 | no paired PNG | — | — | — |
| objects/Mannequin_0.obj | 192 | 66 | 122 | 1.559040, 2.093471, 0.684063 | 0 detected | no paired PNG | — | — | — |
| objects/Mannequin_0_Ragdoll_0.obj | 192 | 66 | 122 | 1.559040, 2.093471, 0.684063 | 0 detected | no paired PNG | — | — | — |
| objects/Mannequin_0_lod1.obj | 102 | 30 | 56 | 1.271050, 2.093471, 0.646170 | 0 detected | no paired PNG | — | — | — |
| objects/Mannequin_1.obj | 284 | 134 | 214 | 2.944127, 2.944126, 4.749999 | 12×6 | [128, 128] | 188 | 1 | [140, 284] |
| objects/Mannequin_1_lod1.obj | 53 | 53 | 54 | 2.944127, 2.944126, 4.749999 | 12×4 | no paired PNG | — | — | — |
| objects/Mannequin_2.obj | 236 | 144 | 212 | 2.944127, 2.944126, 4.749999 | 12×8 | no paired PNG | — | — | — |
| objects/Mannequin_2_lod1.obj | 76 | 76 | 72 | 2.944127, 2.944126, 4.749999 | 12×6 | no paired PNG | — | — | — |
| objects/Maple_0.obj | 218 | 208 | 296 | 10.454273, 20.490758, 10.399714 | 5×4, 6×10, 12×1 | [8, 2] | 2 | 2 | [0, 218] |
| objects/Maple_0_foliage.obj | 452 | 395 | 316 | 18.338562, 17.236622, 20.345343 | 0 detected | [128, 64] | 6 | 5 | [0, 452] |
| objects/Maple_0_foliage_lod1.obj | 96 | 96 | 54 | 18.338562, 16.214176, 20.345343 | 0 detected | no paired PNG | — | — | — |
| objects/Maple_0_lod1.obj | 13 | 13 | 12 | 1.800000, 16.460183, 1.800001 | 12×1 | no paired PNG | — | — | — |
| objects/Maple_1.obj | 233 | 218 | 309 | 9.977044, 21.338117, 9.931742 | 5×6, 6×10, 12×1 | [8, 2] | 2 | 2 | [0, 233] |
| objects/Maple_1_foliage.obj | 452 | 395 | 316 | 20.347748, 17.924878, 18.262225 | 0 detected | [128, 64] | 6 | 6 | [0, 452] |
| objects/Maple_1_foliage_lod1.obj | 72 | 72 | 42 | 20.347748, 16.096163, 18.262225 | 0 detected | no paired PNG | — | — | — |
| objects/Maple_1_lod1.obj | 13 | 13 | 12 | 1.800000, 12.835140, 1.800001 | 12×1 | no paired PNG | — | — | — |
| objects/Mechanic_0.obj | 1101 | 461 | 743 | 25.000010, 13.000004, 13.750000 | 0 detected | [4, 2] | 6 | 5 | [0, 1101] |
| objects/Mechanic_0_lod1.obj | 944 | 360 | 591 | 25.000010, 13.000004, 13.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Mechanic_0_lod2.obj | 206 | 70 | 149 | 24.000010, 12.000006, 13.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Medic_0.obj | 1275 | 522 | 857 | 20.099256, 20.101021, 11.750000 | 0 detected | [4, 2] | 7 | 7 | [0, 1275] |
| objects/Medic_0_lod1.obj | 1068 | 408 | 684 | 20.099256, 20.101021, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Medic_0_lod2.obj | 273 | 100 | 202 | 20.000000, 20.000001, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Medic_1.obj | 4828 | 1706 | 2694 | 39.000006, 20.100003, 11.750002 | 0 detected | [4, 2] | 7 | 6 | [0, 4828] |
| objects/Medic_1_lod1.obj | 871 | 248 | 492 | 39.000006, 20.000008, 11.750002 | 0 detected | no paired PNG | — | — | — |
| objects/Medic_2.obj | 2603 | 775 | 1310 | 20.700008, 22.100006, 10.750000 | 0 detected | [4, 2] | 7 | 7 | [0, 2603] |
| objects/Medic_2_lod1.obj | 2177 | 635 | 1100 | 20.700008, 22.100006, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Medic_2_lod2.obj | 650 | 179 | 376 | 20.500008, 22.000010, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Medic_Cover_0.obj | 986 | 332 | 472 | 10.000000, 14.133635, 6.792367 | 0 detected | [2, 2] | 4 | 4 | [0, 986] |
| objects/Medic_Cover_0_lod1.obj | 697 | 238 | 331 | 10.000000, 14.133635, 6.792367 | 0 detected | no paired PNG | — | — | — |
| objects/Medic_Cover_0_lod2.obj | 140 | 56 | 70 | 10.000000, 14.000000, 6.792367 | 0 detected | no paired PNG | — | — | — |
| objects/Metal_2.obj | 204 | 82 | 118 | 3.923656, 4.038805, 3.280455 | 0 detected | [2, 1] | 2 | 2 | [0, 204] |
| objects/Metal_2_lod1.obj | 24 | 8 | 12 | 3.181790, 2.845041, 3.008589 | 0 detected | no paired PNG | — | — | — |
| objects/Microwave_0.obj | 168 | 64 | 88 | 1.360062, 0.734373, 0.800000 | 0 detected | [2, 2] | 3 | 3 | [0, 168] |
| objects/Microwave_0_Ragdoll_0.obj | 168 | 64 | 88 | 1.360062, 0.734373, 0.800000 | 0 detected | no paired PNG | — | — | — |
| objects/Microwave_0_lod1.obj | 48 | 16 | 28 | 1.360062, 0.677750, 0.800000 | 0 detected | no paired PNG | — | — | — |
| objects/Military_Sign_0.obj | 2124 | 926 | 1367 | 5.000000, 0.600001, 4.000000 | 0 detected | [2, 2] | 4 | 4 | [0, 2124] |
| objects/Military_Sign_0_lod1.obj | 94 | 34 | 51 | 5.000000, 0.600000, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Minecart.obj | 372 | 122 | 218 | 3.852478, 4.024000, 2.012245 | 12×8 | [2, 2] | 3 | 3 | [0, 372] |
| objects/Minehole.obj | 64 | 24 | 26 | 8.375001, 11.250000, 5.000000 | 0 detected | [2, 2] | 2 | 2 | [0, 64] |
| objects/Mobile_0.obj | 533 | 172 | 316 | 6.100002, 12.000006, 5.750000 | 0 detected | [4, 2] | 7 | 6 | [0, 533] |
| objects/Mobile_0_lod1.obj | 323 | 104 | 208 | 6.000002, 12.000006, 5.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Monument_0.obj | 1128 | 374 | 692 | 18.562500, 18.562500, 70.500003 | 12×6 | [2, 2] | 3 | 3 | [0, 1128] |
| objects/Monument_0_lod1.obj | 184 | 75 | 134 | 18.562500, 18.054830, 59.250002 | 12×1 | no paired PNG | — | — | — |
| objects/Monument_Decorations_00.obj | 328 | 205 | 252 | 18.520565, 18.520569, 73.383700 | 12×6 | [16, 32] | 75 | 2 | [0, 328] |
| objects/Monument_Decorations_00_lod1.obj | 104 | 96 | 90 | 12.858448, 11.719637, 54.000003 | 12×2 | no paired PNG | — | — | — |
| objects/Monument_Decorations_01.obj | 328 | 205 | 252 | 18.520565, 18.520569, 73.383700 | 12×6 | [16, 32] | 75 | 10 | [0, 328] |
| objects/Monument_Decorations_01_lod1.obj | 104 | 96 | 90 | 12.858448, 11.719637, 54.000003 | 12×2 | no paired PNG | — | — | — |
| objects/Mushroom_Brown_0.obj | 320 | 100 | 180 | 0.974856, 0.600211, 0.909596 | 0 detected | [64, 64] | 11 | 11 | [0, 320] |
| objects/Mushroom_Brown_0_lod1.obj | 7 | 4 | 4 | 0.428089, 0.144509, 0.242381 | 0 detected | no paired PNG | — | — | — |
| objects/Mushroom_Red_0.obj | 420 | 140 | 230 | 1.012831, 0.579784, 0.956408 | 0 detected | [64, 64] | 20 | 10 | [0, 420] |
| objects/Mushroom_Red_0_lod1.obj | 30 | 15 | 12 | 1.012831, 0.288986, 0.918492 | 0 detected | no paired PNG | — | — | — |
| objects/Newspaper.obj | 77 | 20 | 36 | 1.025468, 1.025468, 0.042102 | 0 detected | [2, 1] | 2 | 2 | [0, 77] |
| objects/Note_0.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_10.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_11.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_12.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_14.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_2.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_4.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_6.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_7.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_8.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Note_Present.obj | 24 | 8 | 12 | 0.500000, 0.666666, 0.050000 | 0 detected | [16, 16] | 5 | 1 | [0, 24] |
| objects/Off_Roader_Blue.obj | 700 | 264 | 482 | 2.773504, 5.129060, 2.534574 | 6×4 | [4, 2] | 8 | 8 | [0, 700] |
| objects/Off_Roader_Blue_lod1.obj | 309 | 89 | 180 | 2.539429, 5.071479, 2.472471 | 0 detected | no paired PNG | — | — | — |
| objects/Off_Roader_Orange.obj | 700 | 264 | 482 | 2.773504, 5.129060, 2.534574 | 6×4 | [4, 2] | 8 | 8 | [0, 700] |
| objects/Off_Roader_Orange_lod1.obj | 366 | 132 | 280 | 2.773504, 4.855406, 2.412130 | 0 detected | no paired PNG | — | — | — |
| objects/Off_Roader_Purple.obj | 700 | 264 | 482 | 2.773504, 5.129060, 2.534574 | 6×4 | [4, 2] | 8 | 8 | [0, 700] |
| objects/Off_Roader_Purple_lod1.obj | 366 | 132 | 280 | 2.773504, 4.855406, 2.412130 | 0 detected | no paired PNG | — | — | — |
| objects/Off_Roader_White.obj | 700 | 264 | 482 | 2.773504, 5.129060, 2.534574 | 6×4 | [4, 2] | 8 | 8 | [0, 700] |
| objects/Off_Roader_White_lod1.obj | 309 | 89 | 180 | 2.539429, 5.071479, 2.472471 | 0 detected | no paired PNG | — | — | — |
| objects/Office_0.obj | 3215 | 764 | 1543 | 28.200000, 18.100000, 16.500000 | 0 detected | [4, 2] | 6 | 6 | [0, 3215] |
| objects/Office_0_lod1.obj | 1024 | 338 | 725 | 28.000000, 18.000000, 16.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Office_1.obj | 3900 | 1023 | 2135 | 25.700008, 25.100000, 26.000000 | 0 detected | [4, 2] | 6 | 6 | [0, 3900] |
| objects/Office_1_lod1.obj | 835 | 384 | 873 | 25.500000, 25.000000, 26.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Office_2.obj | 6196 | 1435 | 2973 | 18.200010, 18.100000, 34.500000 | 0 detected | [4, 2] | 8 | 8 | [0, 6196] |
| objects/Office_2_lod1.obj | 1881 | 619 | 1369 | 18.000000, 18.100000, 34.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Office_3.obj | 5641 | 1334 | 2724 | 21.600004, 16.100000, 44.000000 | 0 detected | [4, 2] | 8 | 8 | [0, 5641] |
| objects/Office_3_lod1.obj | 1785 | 608 | 1308 | 21.500000, 16.000000, 44.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Ornament_0_XMAS.obj | 8 | 4 | 4 | 0.750000, 3.000000, 0.000000 | 0 detected | [64, 16] | 4 | 1 | [0, 8] |
| objects/Oven_0.obj | 150 | 56 | 74 | 1.500000, 1.375000, 1.544034 | 0 detected | [2, 2] | 3 | 3 | [0, 150] |
| objects/Oven_0_door.obj | 112 | 40 | 68 | 1.500000, 0.335726, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Oven_0_door_lod1.obj | 50 | 18 | 32 | 1.500000, 0.125001, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Oven_0_lod1.obj | 45 | 24 | 34 | 1.500000, 1.375000, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Oven_1.obj | 180 | 65 | 112 | 1.500000, 1.000000, 6.425000 | 12×2 | no paired PNG | — | — | — |
| objects/Oven_Brick_0.obj | 430 | 156 | 262 | 1.583544, 1.566185, 3.750001 | 0 detected | [64, 64] | 38 | 34 | [0, 430] |
| objects/Oven_Electric_0.obj | 168 | 64 | 92 | 1.500000, 1.710725, 1.544034 | 0 detected | [2, 2] | 4 | 4 | [0, 168] |
| objects/Pallet_Wood_0.obj | 168 | 56 | 84 | 2.954511, 3.000002, 0.283720 | 0 detected | [2, 2] | 4 | 2 | [0, 168] |
| objects/Pallet_Wood_0_lod1.obj | 64 | 24 | 32 | 2.954511, 3.000002, 0.283720 | 0 detected | no paired PNG | — | — | — |
| objects/Phone_0.obj | 140 | 52 | 80 | 0.506436, 0.603470, 0.592924 | 12×2 | [2, 2] | 4 | 3 | [0, 140] |
| objects/Phone_0_lod1.obj | 84 | 28 | 40 | 0.500000, 0.614216, 0.592924 | 6×2 | no paired PNG | — | — | — |
| objects/Pine_0.obj | 169 | 154 | 213 | 2.338732, 22.367782, 2.463975 | 5×6, 6×2, 12×2 | [8, 2] | 2 | 2 | [0, 169] |
| objects/Pine_0_foliage.obj | 328 | 328 | 216 | 17.943322, 21.717410, 19.883320 | 0 detected | [128, 64] | 8 | 5 | [0, 328] |
| objects/Pine_0_foliage_lod1.obj | 6 | 6 | 4 | 11.243760, 5.772403, 13.125305 | 0 detected | no paired PNG | — | — | — |
| objects/Pine_0_lod1.obj | 13 | 13 | 12 | 1.800000, 19.540291, 1.800001 | 12×1 | no paired PNG | — | — | — |
| objects/Pine_1.obj | 184 | 164 | 226 | 2.407137, 20.367782, 2.463975 | 5×8, 6×2, 12×2 | [8, 2] | 2 | 2 | [0, 184] |
| objects/Pine_1_foliage.obj | 328 | 328 | 216 | 15.616656, 19.690827, 16.864666 | 0 detected | [128, 64] | 8 | 7 | [0, 328] |
| objects/Pine_1_foliage_lod1.obj | 48 | 48 | 32 | 13.943442, 8.786732, 16.199900 | 0 detected | no paired PNG | — | — | — |
| objects/Pine_1_lod1.obj | 13 | 13 | 12 | 1.800000, 17.540291, 1.800001 | 12×1 | no paired PNG | — | — | — |
| objects/Pipe_0.obj | 96 | 48 | 96 | 2.000000, 3.000002, 2.000002 | 12×4 | no paired PNG | — | — | — |
| objects/Pipe_0_lod1.obj | 72 | 24 | 48 | 2.000002, 3.000002, 1.732052 | 6×4 | no paired PNG | — | — | — |
| objects/Plane_1.obj | 776 | 224 | 379 | 26.000006, 19.000012, 7.908345 | 12×2 | [2, 2] | 4 | 4 | [0, 776] |
| objects/Plane_2.obj | 1188 | 332 | 603 | 26.000015, 23.000012, 7.408345 | 12×2 | [2, 2] | 4 | 4 | [0, 1188] |
| objects/Plane_2_lod1.obj | 201 | 60 | 95 | 26.000015, 23.000005, 6.876746 | 6×2 | no paired PNG | — | — | — |
| objects/Plane_3.obj | 1188 | 332 | 603 | 26.000015, 23.000012, 7.408345 | 12×2 | [2, 2] | 4 | 4 | [0, 1188] |
| objects/Plane_3_lod1.obj | 201 | 60 | 95 | 26.000015, 23.000005, 6.876746 | 6×2 | no paired PNG | — | — | — |
| objects/Police.obj | 614 | 214 | 398 | 2.526923, 5.978681, 2.656898 | 0 detected | [4, 2] | 7 | 7 | [0, 614] |
| objects/Police_0.obj | 1085 | 431 | 750 | 16.196034, 16.098724, 11.750000 | 0 detected | [4, 2] | 7 | 7 | [0, 1085] |
| objects/Police_0_lod1.obj | 977 | 367 | 654 | 16.196034, 16.098724, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Police_0_lod2.obj | 353 | 125 | 263 | 16.000007, 16.000008, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Police_1.obj | 4128 | 1329 | 2448 | 24.700006, 36.200012, 15.500000 | 0 detected | [4, 2] | 7 | 7 | [0, 4128] |
| objects/Police_1_lod1.obj | 1936 | 513 | 1084 | 24.500010, 36.000017, 15.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Police_lod1.obj | 418 | 142 | 292 | 2.492485, 5.664652, 2.392212 | 0 detected | no paired PNG | — | — | — |
| objects/Post_0.obj | 887 | 353 | 592 | 16.000007, 20.177621, 12.250000 | 0 detected | [4, 2] | 8 | 7 | [0, 887] |
| objects/Post_0_lod1.obj | 804 | 299 | 511 | 16.000007, 20.177621, 12.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Post_0_lod2.obj | 276 | 101 | 190 | 16.000007, 20.000008, 12.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Poster_0.obj | 20 | 8 | 10 | 3.000001, 0.200000, 1.500000 | 0 detected | [16, 8] | 7 | 1 | [0, 20] |
| objects/Power_Line_0.obj | 166 | 64 | 100 | 4.000000, 0.833516, 9.000000 | 12×2 | [2, 2] | 4 | 4 | [0, 166] |
| objects/Power_Line_0_Debris.obj | 42 | 24 | 34 | 0.500000, 0.500000, 1.500000 | 12×2 | no paired PNG | — | — | — |
| objects/Power_Line_0_Ragdoll_0.obj | 166 | 64 | 100 | 4.000000, 0.833516, 9.000000 | 12×2 | no paired PNG | — | — | — |
| objects/Power_Line_0_lod1.obj | 44 | 16 | 22 | 4.000000, 0.833516, 9.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Present_0_XMAS.obj | 250 | 68 | 124 | 0.750000, 0.750000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_1_XMAS.obj | 250 | 68 | 124 | 0.750000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_2_XMAS.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_3_XMAS.obj | 250 | 68 | 124 | 1.500000, 1.500000, 1.611485 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Present_4_XMAS.obj | 250 | 68 | 124 | 0.750000, 1.500000, 1.593256 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Propane_0.obj | 125 | 40 | 60 | 0.500000, 0.500000, 0.584026 | 8×2 | [2, 1] | 2 | 2 | [0, 125] |
| objects/Propane_0_lod1.obj | 24 | 8 | 12 | 0.353554, 0.353554, 0.584026 | 0 detected | no paired PNG | — | — | — |
| objects/Pumpkin_0_HW.obj | 124 | 35 | 60 | 0.926667, 0.979456, 1.154210 | 0 detected | [2, 1] | 2 | 2 | [0, 124] |
| objects/Pumpkin_0_HW_lod1.obj | 24 | 8 | 12 | 0.733281, 0.931331, 0.899833 | 0 detected | no paired PNG | — | — | — |
| objects/Puzzle_Snowman_00.obj | 1114 | 353 | 552 | 1.582788, 0.885539, 1.612619 | 16×16 | [2, 2] | 3 | 3 | [0, 1114] |
| objects/Puzzle_Snowman_00_lod1.obj | 180 | 36 | 60 | 0.954332, 0.865976, 1.742257 | 5×36 | no paired PNG | — | — | — |
| objects/Quest_Present_00.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_00_Debris.obj | 328 | 64 | 180 | 2.909538, 2.977212, 0.256515 | 0 detected | no paired PNG | — | — | — |
| objects/Quest_Present_01.obj | 250 | 68 | 124 | 0.750000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_01_Debris.obj | 344 | 64 | 180 | 2.227212, 2.909538, 0.256515 | 0 detected | no paired PNG | — | — | — |
| objects/Quest_Present_02.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_03.obj | 200 | 60 | 108 | 0.101668, 0.446707, 0.353397 | 0 detected | [2, 1] | 2 | 2 | [0, 200] |
| objects/Quest_Present_03_Off.obj | 200 | 60 | 108 | 0.101668, 0.446707, 0.353397 | 0 detected | [1, 1] | 1 | 1 | [0, 200] |
| objects/Quest_Present_03_Placed.obj | 200 | 60 | 108 | 0.101668, 0.446707, 0.353397 | 0 detected | [2, 1] | 2 | 2 | [0, 200] |
| objects/Quest_Present_04.obj | 312 | 68 | 148 | 0.418666, 0.418666, 0.171588 | 8×1 | [2, 1] | 2 | 2 | [0, 312] |
| objects/Quest_Present_04_Off.obj | 136 | 32 | 64 | 0.418666, 0.418666, 0.151054 | 8×4 | [1, 1] | 1 | 1 | [0, 136] |
| objects/Quest_Present_04_Placed.obj | 312 | 68 | 148 | 0.418666, 0.418666, 0.171588 | 8×1 | [2, 1] | 2 | 2 | [0, 312] |
| objects/Quest_Present_05.obj | 196 | 64 | 110 | 0.524758, 1.065230, 0.113782 | 0 detected | [2, 1] | 2 | 2 | [0, 196] |
| objects/Quest_Present_05_Off.obj | 46 | 18 | 26 | 0.524758, 1.065230, 0.086745 | 0 detected | [1, 1] | 1 | 1 | [0, 46] |
| objects/Quest_Present_05_Placed.obj | 196 | 64 | 110 | 0.524758, 1.065230, 0.113782 | 0 detected | [2, 1] | 2 | 2 | [0, 196] |
| objects/Quest_Present_06.obj | 202 | 66 | 120 | 0.369930, 1.095449, 0.098931 | 0 detected | [2, 1] | 2 | 2 | [0, 202] |
| objects/Quest_Present_06_Off.obj | 54 | 20 | 36 | 0.369930, 1.095449, 0.070946 | 0 detected | [1, 1] | 1 | 1 | [0, 54] |
| objects/Quest_Present_06_Placed.obj | 202 | 66 | 120 | 0.369930, 1.095449, 0.098931 | 0 detected | [2, 1] | 2 | 2 | [0, 202] |
| objects/Quest_Present_07.obj | 250 | 68 | 124 | 1.500000, 1.500000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Quest_Present_08.obj | 250 | 68 | 124 | 0.750000, 0.750000, 0.833087 | 0 detected | [2, 1] | 2 | 2 | [0, 250] |
| objects/Racecar_Purple.obj | 557 | 152 | 254 | 3.056182, 7.355685, 1.971576 | 0 detected | [4, 2] | 6 | 6 | [0, 557] |
| objects/Radar_0.obj | 332 | 98 | 158 | 6.998980, 6.998977, 7.886139 | 12×4 | [2, 2] | 4 | 4 | [0, 332] |
| objects/Radar_0_lod1.obj | 100 | 30 | 44 | 6.998977, 6.998977, 7.886139 | 6×2 | no paired PNG | — | — | — |
| objects/Radar_1.obj | 436 | 148 | 214 | 7.845671, 6.794552, 28.250000 | 0 detected | [2, 2] | 4 | 4 | [0, 436] |
| objects/Radar_1_lod1.obj | 320 | 116 | 156 | 7.412658, 6.419552, 24.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Radio_0.obj | 124 | 48 | 62 | 1.000000, 0.537709, 1.068631 | 0 detected | [2, 2] | 4 | 4 | [0, 124] |
| objects/Radio_0_Ragdoll_0.obj | 124 | 48 | 62 | 1.000000, 0.537709, 1.068631 | 0 detected | no paired PNG | — | — | — |
| objects/Radio_0_lod1.obj | 24 | 8 | 12 | 1.000000, 0.500000, 0.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Radio_1.obj | 178 | 60 | 86 | 1.250000, 0.850000, 0.689856 | 6×2 | [2, 2] | 4 | 4 | [0, 178] |
| objects/Radio_1_lod1.obj | 66 | 32 | 42 | 1.250000, 0.800000, 0.689856 | 6×1 | no paired PNG | — | — | — |
| objects/Rebar_0.obj | 180 | 76 | 110 | 2.789822, 2.766884, 1.482869 | 0 detected | [2, 2] | 4 | 4 | [0, 180] |
| objects/Rebar_0_lod1.obj | 100 | 40 | 50 | 2.741510, 2.429267, 1.482869 | 0 detected | no paired PNG | — | — | — |
| objects/Register_0.obj | 578 | 232 | 340 | 0.805676, 0.697631, 0.801505 | 0 detected | [2, 2] | 4 | 4 | [0, 578] |
| objects/Register_0_Ragdoll_0.obj | 578 | 232 | 340 | 0.805676, 0.697631, 0.801505 | 0 detected | no paired PNG | — | — | — |
| objects/Register_0_lod1.obj | 64 | 24 | 38 | 0.805676, 0.697631, 0.801505 | 0 detected | no paired PNG | — | — | — |
| objects/Research_0.obj | 1015 | 313 | 588 | 38.000014, 34.100006, 15.500000 | 0 detected | [4, 2] | 6 | 5 | [0, 1015] |
| objects/Research_0_lod1.obj | 361 | 150 | 294 | 38.000014, 34.000017, 15.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Research_1.obj | 1448 | 422 | 836 | 10.200007, 14.200002, 7.000000 | 0 detected | [4, 2] | 6 | 5 | [0, 1448] |
| objects/Research_Sign_0.obj | 2878 | 940 | 1366 | 5.088490, 0.637868, 4.000000 | 0 detected | [2, 2] | 4 | 4 | [0, 2878] |
| objects/Research_Sign_0_lod1.obj | 1928 | 630 | 901 | 5.000000, 0.600001, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Research_Sign_0_lod2.obj | 60 | 20 | 32 | 5.000000, 0.500000, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Research_Sign_1.obj | 2878 | 940 | 1366 | 5.088490, 0.637868, 4.000000 | 0 detected | [2, 2] | 4 | 4 | [0, 2878] |
| objects/Road_Line_0.obj | 28 | 20 | 14 | 24.000000, 24.000004, 1.400000 | 0 detected | [256, 256] | 3 | 2 | [0, 28] |
| objects/Road_Line_Cap_0.obj | 46 | 24 | 22 | 24.000000, 26.000004, 1.400000 | 0 detected | [256, 256] | 9 | 2 | [0, 46] |
| objects/Road_Line_Cap_0_lod1.obj | 12 | 8 | 6 | 18.000001, 26.000004, 1.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Road_Quad_0.obj | 84 | 52 | 54 | 24.000000, 24.000001, 1.400000 | 0 detected | [256, 64] | 25 | 2 | [0, 84] |
| objects/Road_Quad_0_lod1.obj | 32 | 16 | 18 | 24.000000, 24.000000, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Road_Quad_Cap_0.obj | 102 | 58 | 62 | 24.000000, 26.000001, 1.400000 | 0 detected | [256, 64] | 28 | 2 | [0, 102] |
| objects/Road_Quad_Cap_0_lod1.obj | 40 | 20 | 22 | 24.000000, 26.000000, 1.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Road_Tee_0.obj | 60 | 38 | 36 | 24.000001, 24.000001, 1.400000 | 0 detected | [256, 64] | 19 | 2 | [0, 60] |
| objects/Road_Tee_0_lod1.obj | 30 | 20 | 18 | 24.000001, 24.000001, 1.400000 | 0 detected | no paired PNG | — | — | — |
| objects/Road_Tee_Cap_1.obj | 74 | 44 | 44 | 24.000001, 26.000001, 1.400000 | 0 detected | [256, 64] | 20 | 2 | [0, 84] |
| objects/Road_Tee_Cap_1_lod1.obj | 34 | 24 | 22 | 24.000001, 26.000001, 1.400000 | 0 detected | no paired PNG | — | — | — |
| objects/Road_Turn_0.obj | 53 | 31 | 31 | 24.000001, 24.000000, 1.400000 | 0 detected | [256, 128] | 13 | 2 | [0, 53] |
| objects/Road_Turn_0_lod1.obj | 24 | 14 | 14 | 24.000001, 24.000000, 1.400000 | 0 detected | no paired PNG | — | — | — |
| objects/Roadster_Black.obj | 542 | 184 | 330 | 2.763216, 5.956593, 2.554371 | 0 detected | [4, 2] | 7 | 7 | [0, 542] |
| objects/Roadster_Black_lod1.obj | 346 | 112 | 224 | 2.763216, 5.676268, 2.456403 | 0 detected | no paired PNG | — | — | — |
| objects/Roadster_Blue.obj | 542 | 184 | 330 | 2.763216, 5.956593, 2.554371 | 0 detected | [4, 2] | 7 | 7 | [0, 542] |
| objects/Roadster_Blue_lod1.obj | 250 | 72 | 144 | 2.763216, 5.641157, 2.447488 | 0 detected | no paired PNG | — | — | — |
| objects/Roadster_Green.obj | 542 | 184 | 330 | 2.763216, 5.956593, 2.554371 | 0 detected | [4, 2] | 7 | 7 | [0, 542] |
| objects/Roadster_Green_lod1.obj | 250 | 72 | 144 | 2.763216, 5.641157, 2.447488 | 0 detected | no paired PNG | — | — | — |
| objects/Roadster_Orange.obj | 542 | 184 | 330 | 2.763216, 5.956593, 2.554371 | 0 detected | [4, 2] | 7 | 7 | [0, 542] |
| objects/Roadster_Orange_lod1.obj | 250 | 72 | 144 | 2.763216, 5.641157, 2.447488 | 0 detected | no paired PNG | — | — | — |
| objects/Roadster_Yellow.obj | 542 | 184 | 330 | 2.763216, 5.956593, 2.554371 | 0 detected | [4, 2] | 7 | 7 | [0, 542] |
| objects/Roadster_Yellow_lod1.obj | 346 | 112 | 224 | 2.763216, 5.676268, 2.456403 | 0 detected | no paired PNG | — | — | — |
| objects/Roundabout_0.obj | 230 | 96 | 148 | 4.500000, 4.500000, 1.500000 | 12×2 | [4, 2] | 4 | 3 | [0, 230] |
| objects/Roundabout_0_lod1.obj | 19 | 7 | 10 | 3.897114, 3.897114, 0.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Sandcastle_0.obj | 294 | 90 | 162 | 0.500000, 0.500000, 0.732765 | 0 detected | [2, 2] | 3 | 3 | [0, 294] |
| objects/Sandcastle_0_lod1.obj | 47 | 26 | 40 | 0.500000, 0.500000, 0.732765 | 0 detected | no paired PNG | — | — | — |
| objects/Scaffhold_0.obj | 248 | 86 | 120 | 5.400000, 8.400002, 5.250000 | 0 detected | [2, 2] | 3 | 2 | [0, 248] |
| objects/Scaffhold_0_lod1.obj | 45 | 45 | 54 | 5.200006, 8.327470, 5.015718 | 0 detected | no paired PNG | — | — | — |
| objects/Scaffhold_1.obj | 283 | 81 | 154 | 4.500001, 8.500000, 6.250000 | 0 detected | [2, 2] | 2 | 2 | [0, 283] |
| objects/Scaffhold_1_lod1.obj | 40 | 36 | 48 | 4.250001, 8.500000, 6.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Scaffhold_2.obj | 272 | 77 | 148 | 4.500001, 8.500000, 3.000000 | 0 detected | [2, 2] | 2 | 2 | [0, 272] |
| objects/Scaffhold_2_lod1.obj | 30 | 20 | 36 | 4.250000, 8.500000, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/School_0.obj | 1538 | 446 | 782 | 28.700006, 15.000000, 10.750000 | 0 detected | [4, 2] | 6 | 5 | [0, 1538] |
| objects/School_0_lod1.obj | 1394 | 370 | 668 | 28.700006, 15.000000, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/School_0_lod2.obj | 552 | 132 | 267 | 28.500000, 15.000000, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/School_1.obj | 1292 | 329 | 646 | 13.000006, 19.000002, 17.044258 | 0 detected | [4, 2] | 6 | 6 | [0, 1292] |
| objects/School_1_lod1.obj | 500 | 139 | 290 | 12.000005, 18.000009, 17.044258 | 0 detected | no paired PNG | — | — | — |
| objects/Science_0.obj | 384 | 116 | 204 | 0.934914, 0.311638, 0.658708 | 0 detected | [8, 2] | 13 | 8 | [0, 384] |
| objects/Science_0_lod1.obj | 25 | 13 | 18 | 0.934914, 0.251228, 0.603880 | 0 detected | no paired PNG | — | — | — |
| objects/Science_1.obj | 171 | 52 | 78 | 0.344077, 0.416858, 0.729339 | 0 detected | [2, 2] | 4 | 4 | [0, 171] |
| objects/Science_1_lod1.obj | 6 | 6 | 6 | 0.082846, 0.201266, 0.395501 | 0 detected | no paired PNG | — | — | — |
| objects/Science_2.obj | 64 | 20 | 32 | 0.366640, 0.466640, 0.228199 | 0 detected | [2, 1] | 2 | 2 | [0, 64] |
| objects/Science_3.obj | 115 | 44 | 60 | 0.750001, 0.450001, 1.863691 | 0 detected | [2, 2] | 4 | 4 | [0, 115] |
| objects/Science_3_Ragdoll_0.obj | 115 | 44 | 60 | 0.750001, 0.450001, 1.863691 | 0 detected | no paired PNG | — | — | — |
| objects/Science_3_lod1.obj | 64 | 24 | 36 | 0.750001, 0.450001, 1.863691 | 0 detected | no paired PNG | — | — | — |
| objects/Scorpion.obj | 172 | 77 | 89 | 1.492308, 1.893346, 0.000000 | 0 detected | [2, 2] | 2 | 2 | [0, 172] |
| objects/Scorpion_lod1.obj | 67 | 65 | 65 | 1.492308, 1.893346, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Scrap_00.obj | 48 | 16 | 24 | 0.703565, 0.686081, 0.080414 | 0 detected | [2, 1] | 2 | 2 | [0, 48] |
| objects/Scrap_00_Off.obj | 48 | 16 | 24 | 0.703565, 0.686081, 0.080414 | 0 detected | [1, 1] | 1 | 1 | [0, 48] |
| objects/Security_0.obj | 777 | 179 | 390 | 7.000000, 9.000000, 11.250000 | 0 detected | [4, 2] | 6 | 6 | [0, 777] |
| objects/Security_0_lod1.obj | 166 | 77 | 179 | 6.500000, 8.500000, 10.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Sedan_Black.obj | 602 | 214 | 398 | 2.526923, 5.978681, 2.656898 | 0 detected | [4, 2] | 7 | 7 | [0, 602] |
| objects/Sedan_Black_lod1.obj | 329 | 101 | 196 | 2.526923, 5.911778, 2.656898 | 0 detected | no paired PNG | — | — | — |
| objects/Sedan_Purple.obj | 602 | 214 | 398 | 2.526923, 5.978681, 2.656898 | 0 detected | [4, 2] | 7 | 7 | [0, 602] |
| objects/Sedan_Purple_lod1.obj | 406 | 142 | 292 | 2.492485, 5.664652, 2.392212 | 0 detected | no paired PNG | — | — | — |
| objects/Sedan_Red.obj | 602 | 214 | 398 | 2.526923, 5.978681, 2.656898 | 0 detected | [4, 2] | 7 | 7 | [0, 602] |
| objects/Sedan_Red_lod1.obj | 406 | 142 | 292 | 2.492485, 5.664652, 2.392212 | 0 detected | no paired PNG | — | — | — |
| objects/Sedan_Yellow.obj | 602 | 214 | 398 | 2.526923, 5.978681, 2.656898 | 0 detected | [4, 2] | 7 | 7 | [0, 602] |
| objects/Sedan_Yellow_lod1.obj | 406 | 142 | 292 | 2.492485, 5.664652, 2.392212 | 0 detected | no paired PNG | — | — | — |
| objects/SewingTable_0.obj | 920 | 410 | 654 | 2.506863, 1.000001, 2.130164 | 6×27 | [64, 64] | 38 | 38 | [0, 920] |
| objects/Shed_0.obj | 177 | 54 | 90 | 9.000011, 9.000003, 11.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 177] |
| objects/Shed_0_lod1.obj | 106 | 32 | 58 | 8.000003, 8.000004, 11.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Shelf_0.obj | 152 | 56 | 76 | 1.875000, 2.499998, 2.000000 | 0 detected | [2, 2] | 4 | 2 | [0, 152] |
| objects/Shelf_0_lod1.obj | 72 | 24 | 36 | 1.875000, 2.499998, 1.625000 | 0 detected | no paired PNG | — | — | — |
| objects/Shelf_1.obj | 96 | 32 | 48 | 5.000002, 1.476980, 2.500000 | 0 detected | [2, 2] | 4 | 2 | [0, 96] |
| objects/Shelf_2.obj | 121 | 34 | 64 | 2.755002, 0.875000, 2.600000 | 0 detected | [2, 1] | 2 | 2 | [0, 121] |
| objects/Shelf_2_lod1.obj | 42 | 18 | 32 | 2.755002, 0.875000, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Shelf_3.obj | 513 | 104 | 226 | 1.510783, 2.144700, 1.806150 | 0 detected | [2, 1] | 2 | 2 | [0, 513] |
| objects/Shelf_3_lod1.obj | 388 | 64 | 164 | 1.500000, 2.000000, 1.575000 | 0 detected | no paired PNG | — | — | — |
| objects/Ship_0.obj | 1444 | 395 | 780 | 67.500000, 24.000002, 22.000011 | 0 detected | [4, 2] | 8 | 8 | [0, 1444] |
| objects/Ship_0_lod1.obj | 728 | 221 | 448 | 67.500000, 24.000002, 22.000008 | 0 detected | no paired PNG | — | — | — |
| objects/Ship_1.obj | 1444 | 395 | 780 | 67.500000, 24.000002, 22.000011 | 0 detected | [4, 2] | 8 | 8 | [0, 1444] |
| objects/Ship_1_lod1.obj | 728 | 221 | 448 | 67.500000, 24.000002, 22.000008 | 0 detected | no paired PNG | — | — | — |
| objects/Ship_2.obj | 1444 | 395 | 780 | 67.500000, 24.000002, 22.000011 | 0 detected | [4, 2] | 8 | 8 | [0, 1444] |
| objects/Silo_Grain_0.obj | 85 | 85 | 156 | 4.000000, 4.000000, 14.000000 | 12×7 | no paired PNG | — | — | — |
| objects/Silo_Grain_0_lod1.obj | 55 | 25 | 42 | 3.464102, 4.000000, 14.000000 | 6×4 | no paired PNG | — | — | — |
| objects/Silo_Grain_1.obj | 37 | 25 | 36 | 12.000000, 11.999999, 9.869712 | 12×2 | no paired PNG | — | — | — |
| objects/Silo_Grain_1_lod1.obj | 13 | 13 | 12 | 12.000000, 11.999999, 7.869712 | 12×1 | no paired PNG | — | — | — |
| objects/Ski_Lift_0.obj | 281 | 88 | 132 | 7.021278, 2.454162, 11.500000 | 0 detected | [2, 2] | 4 | 4 | [0, 281] |
| objects/Ski_Lift_1.obj | 170 | 52 | 86 | 3.000000, 1.470541, 4.500000 | 0 detected | [2, 2] | 3 | 3 | [0, 170] |
| objects/Ski_Lift_2.obj | 334 | 146 | 238 | 8.000000, 10.077982, 10.500000 | 12×6 | [2, 2] | 4 | 4 | [0, 334] |
| objects/Skirt_00.obj | 96 | 72 | 92 | 9.662578, 9.662579, 0.450000 | 12×2 | [1, 2] | 2 | 2 | [0, 96] |
| objects/Skirt_00_lod1.obj | 48 | 24 | 44 | 9.662578, 9.662579, 0.400000 | 12×2 | no paired PNG | — | — | — |
| objects/Sleigh_00.obj | 652 | 235 | 398 | 6.795012, 3.366715, 2.772238 | 0 detected | [4, 2] | 8 | 8 | [0, 652] |
| objects/Slide_0.obj | 260 | 102 | 204 | 2.489542, 6.000000, 3.708000 | 0 detected | [4, 2] | 2 | 2 | [0, 260] |
| objects/Slide_0_lod1.obj | 164 | 54 | 108 | 2.489542, 6.000000, 3.708000 | 0 detected | no paired PNG | — | — | — |
| objects/Snow_0.obj | 25 | 25 | 32 | 5.000000, 5.000000, 0.443957 | 0 detected | [64, 64] | 25 | 7 | [0, 25] |
| objects/Snow_1.obj | 48 | 20 | 36 | 5.070408, 0.807323, 0.918893 | 0 detected | [64, 64] | 43 | 8 | [0, 48] |
| objects/Snow_2.obj | 88 | 81 | 128 | 33.809317, 33.763241, 5.827096 | 0 detected | [64, 64] | 76 | 9 | [0, 88] |
| objects/Snow_Pile_00.obj | 55 | 32 | 53 | 4.972546, 1.413923, 5.634455 | 0 detected | [64, 64] | 1 | 1 | [0, 55] |
| objects/Snow_Pile_00_lod1.obj | 9 | 9 | 7 | 4.972546, 0.000001, 5.634455 | 0 detected | no paired PNG | — | — | — |
| objects/Snowman_Carrot_00.obj | 20 | 8 | 10 | 0.190426, 0.551912, 0.190426 | 0 detected | [2, 2] | 1 | 1 | [0, 20] |
| objects/Snowman_Carrot_01.obj | 20 | 8 | 10 | 0.190426, 0.551912, 0.190426 | 0 detected | [2, 2] | 1 | 1 | [0, 20] |
| objects/Snowman_Scaffhold_00.obj | 1944 | 960 | 1746 | 6.911114, 4.336922, 8.151559 | 12×36, 16×6 | [4, 2] | 5 | 5 | [0, 1944] |
| objects/Snowman_Scaffhold_00_lod1.obj | 1256 | 632 | 954 | 6.911114, 4.266095, 8.151558 | 6×36, 16×2 | no paired PNG | — | — | — |
| objects/Spikes_0.obj | 96 | 27 | 42 | 0.180680, 0.140564, 0.557820 | 0 detected | [2, 2] | 4 | 2 | [0, 96] |
| objects/SpinningWheel_0.obj | 640 | 274 | 460 | 1.906544, 0.954834, 1.761591 | 6×7, 8×2, 12×4 | [64, 64] | 21 | 21 | [0, 640] |
| objects/Spotlight_deploy.obj | 744 | 216 | 336 | 1.623955, 1.121828, 2.687579 | 0 detected | [2, 2] | 4 | 3 | [0, 744] |
| objects/Spotlight_deploy_lod1.obj | 107 | 39 | 60 | 1.623955, 0.440769, 2.454840 | 0 detected | no paired PNG | — | — | — |
| objects/Stop_0.obj | 337 | 148 | 222 | 1.200000, 0.204490, 2.850000 | 0 detected | [4, 2] | 6 | 3 | [0, 337] |
| objects/Stop_0_Ragdoll_0.obj | 337 | 148 | 222 | 1.200000, 0.204490, 2.850000 | 0 detected | no paired PNG | — | — | — |
| objects/Stop_0_lod1.obj | 264 | 104 | 154 | 1.200000, 0.204490, 2.850000 | 0 detected | no paired PNG | — | — | — |
| objects/Stop_0_lod2.obj | 60 | 24 | 38 | 1.200000, 0.176148, 2.850000 | 0 detected | no paired PNG | — | — | — |
| objects/Street_Light_0.obj | 116 | 44 | 66 | 0.600000, 2.598543, 7.735192 | 0 detected | [2, 2] | 4 | 4 | [0, 116] |
| objects/Street_Light_0_Ragdoll_0.obj | 96 | 36 | 56 | 0.600000, 2.473543, 6.735192 | 0 detected | no paired PNG | — | — | — |
| objects/Street_Light_0_lod1.obj | 100 | 32 | 46 | 0.600000, 2.598543, 7.735192 | 0 detected | no paired PNG | — | — | — |
| objects/Sub_0.obj | 594 | 173 | 306 | 14.175905, 41.500103, 16.210263 | 12×4 | [2, 2] | 4 | 4 | [0, 594] |
| objects/Swings_0.obj | 240 | 92 | 140 | 6.250000, 2.163854, 4.822214 | 0 detected | [4, 2] | 5 | 4 | [0, 240] |
| objects/Swings_0_lod1.obj | 88 | 32 | 52 | 6.250000, 2.163854, 4.822214 | 0 detected | no paired PNG | — | — | — |
| objects/Table_Metal_0.obj | 104 | 40 | 52 | 1.875000, 3.000001, 1.187500 | 0 detected | [2, 2] | 3 | 2 | [0, 104] |
| objects/Table_Metal_0_lod1.obj | 24 | 8 | 12 | 1.875000, 3.000001, 0.125000 | 0 detected | no paired PNG | — | — | — |
| objects/Table_Wood_0.obj | 104 | 40 | 52 | 1.875000, 2.500001, 1.187500 | 0 detected | [2, 2] | 3 | 2 | [0, 104] |
| objects/Table_Wood_0_lod1.obj | 24 | 8 | 12 | 1.875000, 2.500001, 0.125000 | 0 detected | no paired PNG | — | — | — |
| objects/Table_Wood_1.obj | 134 | 56 | 84 | 3.125000, 3.125000, 1.187500 | 12×2 | [2, 2] | 3 | 2 | [0, 134] |
| objects/Table_Wood_1_lod1.obj | 116 | 44 | 60 | 2.703218, 3.121408, 1.187500 | 6×2 | no paired PNG | — | — | — |
| objects/Table_Wood_2.obj | 104 | 40 | 52 | 1.758899, 1.679954, 1.187500 | 0 detected | [2, 2] | 3 | 2 | [0, 104] |
| objects/Table_Wood_2_lod1.obj | 24 | 8 | 12 | 1.758899, 1.679954, 0.125000 | 0 detected | no paired PNG | — | — | — |
| objects/Table_Wood_3.obj | 104 | 40 | 52 | 1.758899, 1.679954, 1.187500 | 0 detected | [2, 2] | 3 | 2 | [0, 104] |
| objects/Table_Wood_3_lod1.obj | 24 | 8 | 12 | 1.758899, 1.679954, 0.125000 | 0 detected | no paired PNG | — | — | — |
| objects/Tank_Forest_Body.obj | 275 | 96 | 156 | 6.522868, 9.453127, 2.654261 | 6×6 | [4, 2] | 6 | 6 | [0, 275] |
| objects/Tank_Forest_Body_lod1.obj | 122 | 40 | 68 | 6.522868, 9.453127, 2.006571 | 6×2 | no paired PNG | — | — | — |
| objects/Tank_Fuel_0.obj | 238 | 92 | 140 | 5.625005, 2.500000, 3.347384 | 6×2, 12×4 | no paired PNG | — | — | — |
| objects/Tank_Fuel_0_lod1.obj | 136 | 56 | 76 | 5.625004, 2.500000, 2.957533 | 6×4 | no paired PNG | — | — | — |
| objects/Target.obj | 56 | 16 | 28 | 1.000000, 0.100000, 2.000000 | 0 detected | [8, 8] | 4 | 1 | [0, 56] |
| objects/Target_Ragdoll_0.obj | 56 | 16 | 28 | 1.000000, 0.100000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Target_lod1.obj | 30 | 12 | 20 | 1.000000, 0.100000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Taxi.obj | 796 | 282 | 536 | 2.565123, 6.112611, 2.889244 | 0 detected | [16, 16] | 37 | 9 | [0, 796] |
| objects/Taxi_lod1.obj | 443 | 148 | 268 | 2.555957, 6.053640, 2.875788 | 0 detected | no paired PNG | — | — | — |
| objects/Television_0.obj | 108 | 40 | 58 | 3.750001, 0.225980, 2.000000 | 0 detected | [4, 2] | 5 | 5 | [0, 108] |
| objects/Television_0_Ragdoll_0.obj | 108 | 40 | 58 | 3.750001, 0.225980, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Television_0_lod1.obj | 48 | 16 | 28 | 3.750001, 0.200004, 2.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Television_1.obj | 108 | 40 | 58 | 1.000000, 1.026764, 1.722421 | 0 detected | [2, 2] | 4 | 4 | [0, 108] |
| objects/Television_1_Ragdoll_0.obj | 108 | 40 | 58 | 1.000000, 1.026764, 1.722421 | 0 detected | no paired PNG | — | — | — |
| objects/Television_1_lod1.obj | 48 | 16 | 28 | 1.000000, 1.000000, 1.150000 | 0 detected | no paired PNG | — | — | — |
| objects/Tent_0.obj | 414 | 136 | 214 | 15.870483, 12.200000, 6.000001 | 0 detected | [8, 8] | 12 | 5 | [0, 414] |
| objects/Tent_0_lod1.obj | 142 | 40 | 78 | 8.000002, 12.000005, 6.000001 | 0 detected | no paired PNG | — | — | — |
| objects/Tent_1.obj | 414 | 136 | 214 | 15.870483, 12.200000, 6.000001 | 0 detected | [8, 8] | 11 | 3 | [0, 414] |
| objects/Tent_1_lod1.obj | 142 | 40 | 78 | 8.000002, 12.000005, 6.000001 | 0 detected | no paired PNG | — | — | — |
| objects/Tent_2.obj | 163 | 64 | 90 | 12.631540, 8.840458, 5.724210 | 0 detected | [2, 2] | 4 | 4 | [0, 163] |
| objects/Tent_2_lod1.obj | 99 | 32 | 58 | 6.000011, 8.000009, 5.643262 | 0 detected | no paired PNG | — | — | — |
| objects/Tent_3.obj | 163 | 64 | 90 | 12.631540, 8.840458, 5.724210 | 0 detected | [2, 2] | 4 | 4 | [0, 163] |
| objects/Tent_3_lod1.obj | 99 | 32 | 58 | 6.000011, 8.000009, 5.643262 | 0 detected | no paired PNG | — | — | — |
| objects/Tent_4.obj | 414 | 136 | 214 | 15.870483, 12.200000, 6.000001 | 0 detected | [8, 8] | 12 | 5 | [0, 414] |
| objects/Tent_4_lod1.obj | 102 | 36 | 70 | 8.000002, 12.000005, 6.000001 | 0 detected | no paired PNG | — | — | — |
| objects/Toaster_0.obj | 101 | 34 | 58 | 0.575076, 1.010361, 0.625010 | 0 detected | [2, 2] | 3 | 3 | [0, 101] |
| objects/Toaster_0_Ragdoll_0.obj | 101 | 34 | 58 | 0.575076, 1.010361, 0.625010 | 0 detected | no paired PNG | — | — | — |
| objects/Toaster_0_lod1.obj | 24 | 8 | 12 | 0.575076, 0.948942, 0.625010 | 0 detected | no paired PNG | — | — | — |
| objects/Tower_Airport_0.obj | 1609 | 426 | 843 | 18.200005, 18.199999, 30.750000 | 0 detected | [4, 2] | 6 | 6 | [0, 1609] |
| objects/Tower_Airport_0_lod1.obj | 841 | 242 | 487 | 18.000004, 18.000005, 30.750000 | 0 detected | no paired PNG | — | — | — |
| objects/Tower_Military_0.obj | 1148 | 276 | 540 | 9.156868, 9.149732, 16.473577 | 12×8 | [2, 2] | 4 | 4 | [0, 1148] |
| objects/Tower_Military_0_lod1.obj | 352 | 104 | 180 | 7.794230, 9.000000, 16.473577 | 6×12 | no paired PNG | — | — | — |
| objects/Tower_Military_1.obj | 623 | 162 | 320 | 6.200004, 6.200001, 16.930000 | 0 detected | [4, 2] | 5 | 5 | [0, 623] |
| objects/Tower_Military_1_lod1.obj | 312 | 85 | 170 | 6.000002, 6.000003, 16.930000 | 0 detected | no paired PNG | — | — | — |
| objects/Tower_Military_2.obj | 906 | 356 | 614 | 15.199996, 15.199999, 21.792278 | 0 detected | [2, 2] | 4 | 4 | [0, 906] |
| objects/Tower_Military_2_lod1.obj | 408 | 139 | 284 | 15.199996, 15.199999, 21.000065 | 0 detected | no paired PNG | — | — | — |
| objects/Tower_Water_0.obj | 286 | 210 | 340 | 7.499997, 7.499996, 14.900342 | 6×2, 12×11 | no paired PNG | — | — | — |
| objects/Tower_Water_0_lod1.obj | 148 | 72 | 100 | 8.000000, 7.401217, 14.900342 | 6×5 | no paired PNG | — | — | — |
| objects/Tractor_0.obj | 824 | 235 | 436 | 2.823357, 5.280807, 3.013180 | 0 detected | [4, 2] | 8 | 8 | [0, 824] |
| objects/Tractor_0_lod1.obj | 67 | 25 | 50 | 2.731911, 4.876742, 2.925116 | 0 detected | no paired PNG | — | — | — |
| objects/Traffic_Light_0.obj | 258 | 96 | 138 | 0.773461, 9.159859, 7.910541 | 0 detected | [4, 2] | 6 | 6 | [0, 258] |
| objects/Traffic_Light_0_Ragdoll_0.obj | 238 | 88 | 128 | 0.648461, 9.034859, 6.910541 | 0 detected | no paired PNG | — | — | — |
| objects/Traffic_Light_0_lod1.obj | 120 | 40 | 60 | 0.522601, 9.159859, 7.910541 | 0 detected | no paired PNG | — | — | — |
| objects/Train_Car_0.obj | 354 | 112 | 176 | 3.377038, 10.684742, 1.760416 | 0 detected | [2, 1] | 2 | 2 | [0, 354] |
| objects/Train_Car_1.obj | 477 | 124 | 240 | 3.577041, 10.504641, 4.742792 | 0 detected | [2, 2] | 4 | 4 | [0, 477] |
| objects/Train_Car_2.obj | 598 | 200 | 308 | 3.377038, 10.684742, 4.078402 | 6×2, 12×2 | [2, 2] | 4 | 4 | [0, 598] |
| objects/Train_Engine_0.obj | 947 | 296 | 478 | 3.377038, 10.774425, 4.042893 | 6×6 | [4, 2] | 8 | 8 | [0, 947] |
| objects/Transit_0.obj | 172 | 38 | 72 | 2.499998, 1.331531, 1.751787 | 0 detected | [2, 2] | 3 | 2 | [0, 172] |
| objects/Transit_0_lod1.obj | 6 | 6 | 8 | 2.499998, 1.179407, 0.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Transit_1.obj | 216 | 62 | 104 | 5.750000, 3.250000, 3.500000 | 0 detected | [2, 2] | 4 | 2 | [0, 216] |
| objects/Truck_Blue.obj | 655 | 248 | 438 | 2.544690, 5.156025, 2.436154 | 6×4 | [4, 2] | 7 | 7 | [0, 655] |
| objects/Truck_Blue_lod1.obj | 321 | 116 | 236 | 2.487351, 4.785883, 2.328479 | 0 detected | no paired PNG | — | — | — |
| objects/Truck_Red.obj | 655 | 248 | 438 | 2.544690, 5.156025, 2.436154 | 6×4 | [4, 2] | 7 | 7 | [0, 655] |
| objects/Truck_Red_lod1.obj | 306 | 90 | 170 | 2.544690, 5.118130, 2.434040 | 0 detected | no paired PNG | — | — | — |
| objects/Truck_White.obj | 655 | 248 | 438 | 2.544690, 5.156025, 2.436154 | 6×4 | [4, 2] | 7 | 7 | [0, 655] |
| objects/Truck_White_lod1.obj | 321 | 116 | 236 | 2.487351, 4.785883, 2.328479 | 0 detected | no paired PNG | — | — | — |
| objects/Tub_0.obj | 269 | 82 | 116 | 1.700000, 3.268795, 1.727744 | 0 detected | [2, 2] | 3 | 2 | [0, 269] |
| objects/Tub_0_lod1.obj | 57 | 18 | 32 | 1.700000, 3.200001, 1.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Tunnel_Line_0.obj | 36 | 36 | 32 | 24.000001, 24.000021, 17.399999 | 0 detected | no paired PNG | — | — | — |
| objects/Tunnel_Line_Cap_0.obj | 54 | 36 | 48 | 24.000001, 24.000021, 17.399999 | 0 detected | no paired PNG | — | — | — |
| objects/Tunnel_Line_Cap_0_lod1.obj | 25 | 25 | 26 | 24.000001, 24.000021, 17.399999 | 0 detected | no paired PNG | — | — | — |
| objects/UFO.obj | 542 | 193 | 286 | 16.000000, 16.000001, 5.000002 | 12×7 | [2, 1] | 2 | 2 | [0, 542] |
| objects/UFO_lod1.obj | 18 | 18 | 22 | 16.000000, 13.856407, 1.500006 | 6×1, 12×1 | no paired PNG | — | — | — |
| objects/Umbrella_0.obj | 142 | 34 | 58 | 3.750000, 3.750000, 3.050000 | 12×2 | [2, 2] | 2 | 2 | [0, 142] |
| objects/Umbrella_0_Ragdoll_0.obj | 142 | 34 | 58 | 3.750000, 3.750000, 3.050000 | 12×2 | no paired PNG | — | — | — |
| objects/Umbrella_0_lod1.obj | 76 | 22 | 32 | 3.247596, 3.750000, 3.050000 | 6×2 | no paired PNG | — | — | — |
| objects/Umbrella_1.obj | 142 | 34 | 58 | 3.750000, 3.750000, 3.050000 | 12×2 | [2, 2] | 2 | 2 | [0, 142] |
| objects/Umbrella_1_Ragdoll_0.obj | 142 | 34 | 58 | 3.750000, 3.750000, 3.050000 | 12×2 | no paired PNG | — | — | — |
| objects/Umbrella_1_lod1.obj | 76 | 22 | 32 | 3.247596, 3.750000, 3.050000 | 6×2 | no paired PNG | — | — | — |
| objects/Umbrella_2.obj | 142 | 34 | 58 | 3.750000, 3.750000, 3.050000 | 12×2 | [2, 2] | 2 | 2 | [0, 142] |
| objects/Umbrella_2_Ragdoll_0.obj | 142 | 34 | 58 | 3.750000, 3.750000, 3.050000 | 12×2 | no paired PNG | — | — | — |
| objects/Umbrella_2_lod1.obj | 76 | 22 | 32 | 3.247596, 3.750000, 3.050000 | 6×2 | no paired PNG | — | — | — |
| objects/Ural_Desert.obj | 635 | 216 | 368 | 2.850418, 6.595173, 2.461147 | 6×4 | [4, 2] | 8 | 8 | [0, 635] |
| objects/Ural_Desert_lod1.obj | 255 | 84 | 150 | 2.850418, 6.532127, 2.461147 | 0 detected | no paired PNG | — | — | — |
| objects/Van_Black.obj | 717 | 264 | 470 | 3.114689, 5.124451, 2.544453 | 6×4 | [4, 2] | 7 | 7 | [0, 717] |
| objects/Van_Black_lod1.obj | 383 | 132 | 268 | 3.114689, 4.890335, 2.424193 | 0 detected | no paired PNG | — | — | — |
| objects/Van_Green.obj | 717 | 264 | 470 | 3.114689, 5.124451, 2.544453 | 6×4 | [4, 2] | 7 | 7 | [0, 717] |
| objects/Van_Green_lod1.obj | 383 | 132 | 268 | 3.114689, 4.890335, 2.424193 | 0 detected | no paired PNG | — | — | — |
| objects/Veh_Ambulance.obj | 737 | 270 | 462 | 2.693116, 2.551550, 5.509786 | 0 detected | [4, 2] | 7 | 7 | [0, 737] |
| objects/Veh_Ambulance_lod1.obj | 355 | 154 | 230 | 2.693116, 2.528379, 5.509786 | 0 detected | no paired PNG | — | — | — |
| objects/Veh_Firetruck.obj | 676 | 254 | 432 | 2.902001, 2.967035, 7.699010 | 6×4 | [4, 2] | 7 | 7 | [0, 676] |
| objects/Veh_Firetruck_lod1.obj | 353 | 140 | 216 | 2.902001, 2.754034, 7.699010 | 6×2 | no paired PNG | — | — | — |
| objects/Veh_Police.obj | 614 | 214 | 398 | 2.526923, 2.656898, 5.978681 | 0 detected | [4, 2] | 7 | 7 | [0, 614] |
| objects/Veh_Police_lod1.obj | 284 | 81 | 170 | 2.492485, 2.392213, 5.629878 | 0 detected | no paired PNG | — | — | — |
| objects/Vendor_0.obj | 212 | 88 | 112 | 1.600002, 1.144680, 2.500000 | 0 detected | [2, 2] | 4 | 4 | [0, 212] |
| objects/Vendor_0_lod1.obj | 20 | 8 | 10 | 1.600000, 1.000000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Vendor_1.obj | 212 | 88 | 112 | 1.600002, 1.144680, 2.500000 | 0 detected | [2, 2] | 4 | 4 | [0, 212] |
| objects/Vendor_1_lod1.obj | 20 | 8 | 10 | 1.600000, 1.000000, 2.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Vines_0.obj | 20 | 18 | 10 | 1.961629, 2.094624, 5.000000 | 0 detected | [64, 64] | 18 | 4 | [0, 20] |
| objects/Vines_0_lod1.obj | 8 | 6 | 4 | 1.600000, 1.600002, 5.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Vines_1.obj | 24 | 24 | 12 | 7.999998, 0.419057, 8.759165 | 0 detected | [64, 64] | 23 | 3 | [0, 24] |
| objects/Vines_1_lod1.obj | 4 | 4 | 2 | 7.999998, 0.200004, 8.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Vines_2.obj | 16 | 16 | 8 | 3.999998, 0.333011, 4.000000 | 0 detected | [32, 32] | 15 | 3 | [0, 16] |
| objects/Vines_2_lod1.obj | 4 | 4 | 2 | 3.999998, 0.100002, 4.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Vines_3.obj | 16 | 11 | 8 | 1.960941, 1.938212, 1.786825 | 0 detected | [32, 32] | 16 | 4 | [0, 16] |
| objects/Vines_3_lod1.obj | 12 | 7 | 6 | 1.628846, 1.616807, 1.700001 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_0.obj | 84 | 32 | 42 | 2.069998, 0.875000, 2.600000 | 0 detected | [2, 1] | 2 | 2 | [0, 84] |
| objects/Wardrobe_0_Left_Hinge_0_door.obj | 44 | 16 | 22 | 1.034998, 0.215752, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_0_Left_Hinge_0_door_lod1.obj | 24 | 12 | 14 | 1.034998, 0.125000, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_0_Right_Hinge_0_door.obj | 44 | 16 | 22 | 1.034998, 0.215752, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_0_Right_Hinge_0_door_lod1.obj | 24 | 12 | 14 | 1.034998, 0.125000, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_0_lod1.obj | 36 | 12 | 20 | 2.069998, 0.875000, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_1.obj | 84 | 32 | 42 | 2.069998, 0.875000, 2.600000 | 0 detected | [2, 1] | 2 | 2 | [0, 84] |
| objects/Wardrobe_1_Left_Hinge_0_door.obj | 44 | 16 | 22 | 1.034998, 0.215752, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_1_Right_Hinge_0_door.obj | 44 | 16 | 22 | 1.034998, 0.215752, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Wardrobe_1_lod1.obj | 36 | 12 | 20 | 2.069998, 0.875000, 2.600000 | 0 detected | no paired PNG | — | — | — |
| objects/Warehouse_0.obj | 884 | 258 | 456 | 15.000027, 25.000006, 12.250000 | 0 detected | [4, 2] | 6 | 6 | [0, 884] |
| objects/Warehouse_0_lod1.obj | 313 | 89 | 192 | 14.000007, 24.000009, 12.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Warehouse_1.obj | 234 | 108 | 116 | 15.000027, 25.000006, 7.250000 | 0 detected | [4, 2] | 2 | 2 | [0, 234] |
| objects/Warehouse_1_lod1.obj | 64 | 28 | 36 | 15.000027, 25.000006, 7.250000 | 0 detected | no paired PNG | — | — | — |
| objects/Washer_0.obj | 94 | 32 | 60 | 1.500000, 1.500001, 1.500000 | 12×2 | [2, 2] | 1 | 1 | [0, 94] |
| objects/Washer_0_door.obj | 63 | 24 | 44 | 1.100804, 0.125000, 1.100804 | 12×2 | no paired PNG | — | — | — |
| objects/Washer_0_door_lod1.obj | 15 | 7 | 10 | 1.100804, 0.125000, 1.027064 | 0 detected | no paired PNG | — | — | — |
| objects/Washer_0_lod1.obj | 42 | 17 | 30 | 1.500000, 1.500001, 1.500000 | 0 detected | no paired PNG | — | — | — |
| objects/Web_0.obj | 8 | 4 | 4 | 3.000000, 3.000000, 0.000000 | 0 detected | [64, 64] | 4 | 1 | [0, 8] |
| objects/Web_0_Debris.obj | 8 | 4 | 4 | 3.000000, 3.000000, 0.000000 | 0 detected | no paired PNG | — | — | — |
| objects/Web_0_HW.obj | 8 | 4 | 4 | 3.000000, 3.000000, 0.000000 | 0 detected | [64, 64] | 4 | 1 | [0, 8] |
| objects/Well_0.obj | 216 | 92 | 124 | 2.000002, 1.970322, 2.625000 | 12×4 | [2, 2] | 3 | 3 | [0, 216] |
| objects/Well_0_lod1.obj | 208 | 68 | 88 | 2.000002, 1.970322, 2.625000 | 6×4 | no paired PNG | — | — | — |
| objects/Wheel_0.obj | 108 | 48 | 92 | 0.400000, 1.186344, 1.186344 | 12×4 | [2, 1] | 2 | 2 | [0, 108] |
| objects/Wheel_0_lod1.obj | 72 | 24 | 44 | 0.400000, 1.186342, 1.027404 | 6×4 | no paired PNG | — | — | — |
| objects/Wheel_1.obj | 348 | 116 | 206 | 3.352478, 3.081334, 1.197031 | 12×8 | [2, 1] | 2 | 2 | [0, 348] |
| objects/Wheel_3.obj | 168 | 72 | 140 | 0.500001, 0.782986, 0.782988 | 12×6 | [2, 1] | 2 | 2 | [0, 168] |
| objects/Wheel_3_lod1.obj | 114 | 36 | 68 | 0.500000, 0.782986, 0.678086 | 6×6 | no paired PNG | — | — | — |
| objects/Wheel_6.obj | 108 | 48 | 92 | 0.600000, 1.779516, 1.779516 | 12×4 | [2, 1] | 2 | 2 | [0, 108] |
| objects/Wheel_6_lod1.obj | 45 | 17 | 30 | 0.600000, 1.779516, 1.541107 | 6×1 | no paired PNG | — | — | — |
| objects/Workbench_0.obj | 646 | 241 | 364 | 2.000001, 1.450966, 2.200002 | 8×2 | [64, 64] | 32 | 26 | [0, 646] |
| objects/Wreck_0.obj | 373 | 111 | 216 | 10.017446, 10.884164, 6.585977 | 0 detected | [2, 2] | 3 | 2 | [0, 373] |
| objects/Wreck_0_lod1.obj | 20 | 9 | 12 | 4.946828, 3.262595, 2.374240 | 0 detected | no paired PNG | — | — | — |
| objects/Yield_0.obj | 48 | 17 | 26 | 1.200000, 0.176148, 2.679862 | 0 detected | [4, 2] | 5 | 3 | [0, 48] |
| objects/Yield_0_Ragdoll_0.obj | 48 | 17 | 26 | 1.200000, 0.176148, 2.679862 | 0 detected | no paired PNG | — | — | — |
| objects/Yield_0_lod1.obj | 20 | 8 | 12 | 0.100000, 0.100000, 2.585512 | 0 detected | no paired PNG | — | — | — |
| ace_gun.txt | 228 | 102 | 156 | 0.137646, 0.575465, 0.341693 | 12×2 | no paired PNG | — | — | — |
| adrenaline.txt | 108 | 40 | 54 | 0.515022, 0.265440, 0.106588 | 0 detected | no paired PNG | — | — | — |
| ambulance_body.txt | 642 | 246 | 436 | 2.561922, 2.648431, 5.353246 | 0 detected | no paired PNG | — | — | — |
| ambulance_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.860000, 1.550000 | 0 detected | no paired PNG | — | — | — |
| ambulance_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.860000, 1.550000 | 0 detected | no paired PNG | — | — | — |
| ambulance_glass_rear.txt | 4 | 4 | 2 | 2.060000, 0.950000, 0.000000 | 0 detected | no paired PNG | — | — | — |
| ambulance_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.898866, 0.196060 | 0 detected | no paired PNG | — | — | — |
| ambulance_headlights.txt | 40 | 16 | 20 | 2.048476, 0.254716, 0.125399 | 0 detected | no paired PNG | — | — | — |
| ambulance_seats.txt | 218 | 98 | 170 | 1.787029, 1.572051, 1.003499 | 12×4 | no paired PNG | — | — | — |
| ambulance_siren0.txt | 16 | 8 | 8 | 0.456344, 0.322977, 0.672525 | 0 detected | no paired PNG | — | — | — |
| ambulance_siren1.txt | 16 | 8 | 8 | 0.456345, 0.322977, 0.672525 | 0 detected | no paired PNG | — | — | — |
| ambulance_steer.txt | 142 | 66 | 122 | 0.767780, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| ambulance_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329079, 0.189916 | 0 detected | no paired PNG | — | — | — |
| animal_trailer_body.txt | 96 | 96 | 152 | 3.100000, 3.250001, 7.973885 | 0 detected | no paired PNG | — | — | — |
| animal_trailer_taillights.txt | 56 | 16 | 20 | 2.804810, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| antibiotics.txt | 130 | 48 | 78 | 0.205344, 0.205344, 0.249832 | 12×4 | no paired PNG | — | — | — |
| apc_body.txt | 372 | 110 | 182 | 3.661945, 2.586414, 7.713795 | 6×4 | no paired PNG | — | — | — |
| apc_headlights.txt | 120 | 48 | 64 | 2.909050, 0.404513, 0.171785 | 6×8 | no paired PNG | — | — | — |
| apc_taillights.txt | 56 | 16 | 20 | 3.426933, 0.329078, 0.189922 | 0 detected | no paired PNG | — | — | — |
| apc_wheel.txt | 381 | 226 | 365 | 0.550004, 1.478206, 1.489064 | 5×18, 8×4, 13×8 | no paired PNG | — | — | — |
| augewehr_gun.txt | 260 | 104 | 184 | 0.132557, 1.175335, 0.318116 | 0 detected | no paired PNG | — | — | — |
| augewehr_sight.txt | 132 | 64 | 112 | 0.104458, 0.367187, 0.168780 | 12×4 | no paired PNG | — | — | — |
| avenger_gun.txt | 226 | 90 | 134 | 0.101668, 0.493736, 0.332714 | 0 detected | no paired PNG | — | — | — |
| axe_camp.txt | 58 | 22 | 36 | 0.415635, 1.081835, 0.086744 | 0 detected | no paired PNG | — | — | — |
| axe_fire.txt | 54 | 22 | 34 | 0.524758, 1.065230, 0.086744 | 0 detected | no paired PNG | — | — | — |
| axe_pick.txt | 92 | 36 | 64 | 0.810116, 1.081835, 0.113160 | 0 detected | no paired PNG | — | — | — |
| bag_chips.txt | 88 | 28 | 52 | 0.304098, 0.578892, 0.127826 | 0 detected | no paired PNG | — | — | — |
| bag_mre.txt | 80 | 28 | 52 | 0.357762, 0.681050, 0.100256 | 0 detected | no paired PNG | — | — | — |
| bandage.txt | 96 | 40 | 64 | 0.436468, 0.297694, 0.308698 | 12×2 | no paired PNG | — | — | — |
| bane_gun.txt | 296 | 112 | 211 | 0.100000, 1.049317, 0.348640 | 0 detected | no paired PNG | — | — | — |
| bane_sight.txt | 212 | 92 | 154 | 0.075000, 0.526329, 0.113286 | 12×2 | no paired PNG | — | — | — |
| bar_candy.txt | 83 | 28 | 52 | 0.152048, 0.528892, 0.075618 | 0 detected | no paired PNG | — | — | — |
| bar_chocolate.txt | 83 | 28 | 52 | 0.152048, 0.528892, 0.075618 | 0 detected | no paired PNG | — | — | — |
| bar_energy.txt | 83 | 28 | 52 | 0.190061, 0.528892, 0.075618 | 0 detected | no paired PNG | — | — | — |
| bar_granola.txt | 83 | 28 | 52 | 0.190061, 0.528892, 0.075618 | 0 detected | no paired PNG | — | — | — |
| bass_cooked.txt | 40 | 16 | 28 | 0.212967, 0.793713, 0.029924 | 0 detected | no paired PNG | — | — | — |
| bass_raw.txt | 64 | 30 | 40 | 0.328769, 1.026987, 0.130087 | 0 detected | no paired PNG | — | — | — |
| bat.txt | 48 | 16 | 28 | 0.134666, 1.052137, 0.134666 | 0 detected | no paired PNG | — | — | — |
| baton.txt | 56 | 20 | 36 | 0.285002, 1.052137, 0.085002 | 0 detected | no paired PNG | — | — | — |
| beef_cooked.txt | 64 | 32 | 40 | 0.756254, 0.732798, 0.101838 | 0 detected | no paired PNG | — | — | — |
| beef_raw.txt | 64 | 32 | 40 | 0.756254, 0.732798, 0.101838 | 0 detected | no paired PNG | — | — | — |
| berry_amber.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| berry_hanu.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| berry_indigo.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| berry_jade.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| berry_mauve.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| berry_russet.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| berry_teal.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| berry_vermillion.txt | 168 | 56 | 84 | 0.193190, 0.186970, 0.550431 | 0 detected | no paired PNG | — | — | — |
| bloodbag.txt | 80 | 24 | 38 | 0.510904, 0.260212, 0.060860 | 0 detected | no paired PNG | — | — | — |
| blowtorch.txt | 102 | 44 | 74 | 0.236110, 0.619745, 0.236110 | 12×2 | no paired PNG | — | — | — |
| bluntforce_gun.txt | 212 | 82 | 124 | 0.114568, 1.886622, 0.357181 | 6×6 | no paired PNG | — | — | — |
| bluntforce_sight.txt | 106 | 54 | 104 | 0.049328, 0.007721, 0.102957 | 12×4 | no paired PNG | — | — | — |
| bottle_birch.txt | 66 | 20 | 28 | 0.246120, 0.166874, 0.444591 | 6×2 | no paired PNG | — | — | — |
| bottle_maple.txt | 66 | 20 | 28 | 0.246120, 0.166874, 0.444591 | 6×2 | no paired PNG | — | — | — |
| bottle_pine.txt | 66 | 20 | 28 | 0.246120, 0.166874, 0.444591 | 6×2 | no paired PNG | — | — | — |
| bottled_coconut.txt | 120 | 48 | 92 | 0.254540, 0.254540, 0.584059 | 12×4 | no paired PNG | — | — | — |
| bottled_cola.txt | 120 | 48 | 92 | 0.254540, 0.254540, 0.584059 | 12×4 | no paired PNG | — | — | — |
| bottled_energy.txt | 120 | 48 | 92 | 0.254540, 0.254540, 0.584059 | 12×4 | no paired PNG | — | — | — |
| bottled_soda.txt | 120 | 48 | 92 | 0.254540, 0.254540, 0.584059 | 12×4 | no paired PNG | — | — | — |
| bottled_water.txt | 120 | 48 | 92 | 0.254540, 0.254540, 0.584059 | 12×4 | no paired PNG | — | — | — |
| bow_birch_gun.txt | 104 | 40 | 76 | 0.262722, 1.509478, 0.049454 | 0 detected | no paired PNG | — | — | — |
| bow_compound_gun.txt | 200 | 72 | 128 | 0.355010, 1.509478, 0.076686 | 6×4 | no paired PNG | — | — | — |
| bow_compound_sight.txt | 162 | 74 | 128 | 0.059392, 0.023892, 0.099843 | 12×4 | no paired PNG | — | — | — |
| bow_maple_gun.txt | 104 | 40 | 76 | 0.262722, 1.509478, 0.049454 | 0 detected | no paired PNG | — | — | — |
| bow_pine_gun.txt | 104 | 40 | 76 | 0.262722, 1.509478, 0.049454 | 0 detected | no paired PNG | — | — | — |
| box_milk.txt | 111 | 29 | 47 | 0.234772, 0.234772, 0.588980 | 0 detected | no paired PNG | — | — | — |
| box_orange.txt | 111 | 29 | 47 | 0.234772, 0.234772, 0.588980 | 0 detected | no paired PNG | — | — | — |
| bread.txt | 38 | 12 | 20 | 0.334320, 0.406132, 0.062200 | 0 detected | no paired PNG | — | — | — |
| bulldog_gun.txt | 350 | 144 | 212 | 0.100000, 0.912945, 0.365971 | 6×2 | no paired PNG | — | — | — |
| bulldog_sight.txt | 72 | 26 | 42 | 0.059392, 0.023892, 0.061323 | 0 detected | no paired PNG | — | — | — |
| bus_body.txt | 1936 | 505 | 986 | 3.110405, 3.170867, 8.276292 | 0 detected | no paired PNG | — | — | — |
| bus_glass_l_front.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.230000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_l_mid2.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.400000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_l_mid3.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_l_mid4.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_l_rear.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.460000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_r_front.txt | 4 | 4 | 2 | 0.000000, 0.890000, 0.440000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_r_mid1.txt | 4 | 4 | 2 | 0.000000, 0.890000, 0.440000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_r_mid2.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.400000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_r_mid3.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_r_mid4.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_r_rear.txt | 4 | 4 | 2 | 0.000000, 1.070000, 1.460000 | 0 detected | no paired PNG | — | — | — |
| bus_glass_windshield.txt | 4 | 4 | 2 | 2.540000, 1.091234, 0.138592 | 0 detected | no paired PNG | — | — | — |
| bus_headlights.txt | 40 | 16 | 20 | 2.761920, 0.254715, 0.125402 | 0 detected | no paired PNG | — | — | — |
| bus_seats.txt | 380 | 160 | 240 | 2.387030, 1.526791, 7.019176 | 0 detected | no paired PNG | — | — | — |
| bus_steer.txt | 142 | 66 | 122 | 0.767780, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| bus_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329079, 0.189917 | 0 detected | no paired PNG | — | — | — |
| bus_wheel.txt | 381 | 226 | 365 | 0.440004, 1.182565, 1.191252 | 5×18, 8×4, 13×8 | no paired PNG | — | — | — |
| cake.txt | 386 | 120 | 164 | 0.757920, 0.757920, 0.343409 | 12×2 | no paired PNG | — | — | — |
| cake_upgraded.txt | 386 | 120 | 164 | 0.757920, 0.757920, 0.343409 | 12×2 | no paired PNG | — | — | — |
| canned_bacon.txt | 54 | 24 | 44 | 0.318416, 0.318416, 0.129678 | 12×2 | no paired PNG | — | — | — |
| canned_beans.txt | 50 | 24 | 44 | 0.318416, 0.318416, 0.392964 | 12×2 | no paired PNG | — | — | — |
| canned_beef.txt | 54 | 24 | 44 | 0.318416, 0.318416, 0.129678 | 12×2 | no paired PNG | — | — | — |
| canned_cola.txt | 54 | 24 | 44 | 0.254734, 0.254733, 0.314372 | 12×2 | no paired PNG | — | — | — |
| canned_ham.txt | 54 | 24 | 44 | 0.318416, 0.318416, 0.129678 | 12×2 | no paired PNG | — | — | — |
| canned_pasta.txt | 50 | 24 | 44 | 0.318416, 0.318416, 0.392964 | 12×2 | no paired PNG | — | — | — |
| canned_sardines.txt | 54 | 24 | 44 | 0.318416, 0.318416, 0.129678 | 12×2 | no paired PNG | — | — | — |
| canned_soda.txt | 54 | 24 | 44 | 0.254734, 0.254733, 0.314372 | 12×2 | no paired PNG | — | — | — |
| canned_soup_chicken.txt | 50 | 24 | 44 | 0.318416, 0.318416, 0.392964 | 12×2 | no paired PNG | — | — | — |
| canned_soup_tomato.txt | 50 | 24 | 44 | 0.318416, 0.318416, 0.392964 | 12×2 | no paired PNG | — | — | — |
| canned_tuna.txt | 54 | 24 | 44 | 0.318416, 0.318416, 0.129678 | 12×2 | no paired PNG | — | — | — |
| canteen.txt | 66 | 20 | 28 | 0.246120, 0.166874, 0.444591 | 6×2 | no paired PNG | — | — | — |
| car_trailer_preview_alone.txt | 926 | 508 | 886 | 3.025006, 1.576570, 5.906090 | 5×2, 6×20, 8×4, 13×16 | no paired PNG | — | — | — |
| car_trailer_preview_beside.txt | 3456 | 1770 | 3172 | 6.637512, 2.785082, 6.680960 | 5×6, 6×64, 8×14, 12×4, 13×48 | no paired PNG | — | — | — |
| car_trailer_preview_coupled.txt | 3456 | 1770 | 3160 | 3.025006, 2.785082, 11.184643 | 5×6, 6×64, 8×14, 12×4, 13×48 | no paired PNG | — | — | — |
| car_trailer_preview_family.txt | 11310 | 6022 | 10690 | 45.687420, 3.250001, 7.997511 | 5×24, 6×244, 8×50, 12×4, 13×192 | no paired PNG | — | — | — |
| card_gun.txt | 256 | 94 | 186 | 0.123500, 1.240832, 0.359441 | 0 detected | no paired PNG | — | — | — |
| card_sight.txt | 182 | 76 | 130 | 0.049128, 0.560009, 0.055197 | 0 detected | no paired PNG | — | — | — |
| carrot.txt | 44 | 16 | 22 | 0.107722, 0.481365, 0.107722 | 0 detected | no paired PNG | — | — | — |
| chainsaw.txt | 136 | 56 | 94 | 1.274403, 0.576823, 0.505626 | 0 detected | no paired PNG | — | — | — |
| character.txt | 464 | 239 | 488 | 2.246718, 1.956578, 0.398844 | 0 detected | no paired PNG | — | — | — |
| cheese.txt | 18 | 6 | 8 | 0.315102, 0.315102, 0.188522 | 0 detected | no paired PNG | — | — | — |
| chemicals.txt | 151 | 48 | 92 | 0.318174, 0.318175, 0.584059 | 12×4 | no paired PNG | — | — | — |
| chevron_scope_sight.txt | 230 | 100 | 166 | 0.118478, 0.252683, 0.165003 | 6×2 | no paired PNG | — | — | — |
| chewing_gum.txt | 83 | 28 | 52 | 0.152048, 0.528892, 0.075618 | 0 detected | no paired PNG | — | — | — |
| cobra_gun.txt | 196 | 82 | 118 | 0.101668, 0.466652, 0.332714 | 0 detected | no paired PNG | — | — | — |
| coconut_half.txt | 90 | 22 | 40 | 0.293490, 0.328133, 0.186518 | 0 detected | no paired PNG | — | — | — |
| colt_gun.txt | 253 | 98 | 144 | 0.110278, 0.446810, 0.333729 | 0 detected | no paired PNG | — | — | — |
| corn.txt | 77 | 16 | 30 | 0.156331, 0.552562, 0.156332 | 0 detected | no paired PNG | — | — | — |
| crate.txt | 2353 | 922 | 1320 | 1.557155, 1.287500, 1.521544 | 0 detected | no paired PNG | — | — | — |
| crate2.txt | 24 | 8 | 12 | 4.000000, 4.000000, 2.000000 | 0 detected | no paired PNG | — | — | — |
| crop_amber_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_amber_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_amber_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_carrot_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_carrot_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_carrot_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_corn_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_corn_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_corn_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_hanu_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_hanu_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_hanu_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_indigo_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_indigo_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_indigo_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_jade_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_jade_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_jade_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_lettuce_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_lettuce_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_lettuce_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_mauve_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_mauve_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_mauve_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_mushroom_brown_Foliage_1.txt | 12 | 12 | 8 | 0.336636, 0.380189, 0.300920 | 0 detected | no paired PNG | — | — | — |
| crop_mushroom_brown_Model_0.txt | 64 | 26 | 36 | 0.400000, 0.303341, 0.450000 | 0 detected | no paired PNG | — | — | — |
| crop_mushroom_red_Foliage_1.txt | 12 | 12 | 8 | 0.343834, 0.343834, 0.320176 | 0 detected | no paired PNG | — | — | — |
| crop_mushroom_red_Model_0.txt | 64 | 26 | 36 | 0.400000, 0.303341, 0.450000 | 0 detected | no paired PNG | — | — | — |
| crop_potato_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_potato_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_potato_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_pumpkin_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_pumpkin_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_pumpkin_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_rice_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_rice_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_rice_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_russet_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_russet_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_russet_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_sugarcane_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_sugarcane_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_sugarcane_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_taro_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_taro_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_taro_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_teal_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_teal_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_teal_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_tomato_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_tomato_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_tomato_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_vermillion_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_vermillion_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_vermillion_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 0.750000 | 0 detected | no paired PNG | — | — | — |
| crop_wheat_Foliage_0.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_wheat_Foliage_1.txt | 8 | 8 | 4 | 0.530332, 0.530330, 1.500000 | 0 detected | no paired PNG | — | — | — |
| crop_wheat_Model_0.txt | 40 | 16 | 20 | 0.400000, 0.050000, 1.500000 | 0 detected | no paired PNG | — | — | — |
| cross_scope_sight.txt | 196 | 84 | 148 | 0.095380, 0.206339, 0.147227 | 6×2, 12×2 | no paired PNG | — | — | — |
| crossbow_gun.txt | 204 | 80 | 132 | 0.804824, 1.457375, 0.365165 | 0 detected | no paired PNG | — | — | — |
| crossbow_sight.txt | 68 | 24 | 42 | 0.044748, 0.023892, 0.070147 | 0 detected | no paired PNG | — | — | — |
| crowbar.txt | 74 | 28 | 52 | 0.328092, 0.976984, 0.081812 | 0 detected | no paired PNG | — | — | — |
| crushed_amber.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| crushed_hanu.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| crushed_indigo.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| crushed_jade.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| crushed_mauve.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| crushed_russet.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| crushed_teal.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| crushed_vermillion.txt | 46 | 12 | 20 | 0.364250, 0.346675, 0.345513 | 0 detected | no paired PNG | — | — | — |
| cue.txt | 40 | 12 | 20 | 0.078124, 1.375000, 0.078124 | 0 detected | no paired PNG | — | — | — |
| desert_falcon_gun.txt | 216 | 88 | 124 | 0.101668, 0.516299, 0.343833 | 0 detected | no paired PNG | — | — | — |
| determinator_gun.txt | 320 | 149 | 235 | 0.159378, 0.798728, 0.340340 | 8×2, 12×2 | no paired PNG | — | — | — |
| dinky_trailer_body.txt | 72 | 72 | 108 | 2.099994, 1.576570, 5.098181 | 0 detected | no paired PNG | — | — | — |
| dinky_trailer_taillights.txt | 56 | 16 | 20 | 1.804804, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| dolphinfish_cooked.txt | 24 | 16 | 28 | 0.207855, 0.672369, 0.036497 | 0 detected | no paired PNG | — | — | — |
| dolphinfish_raw.txt | 60 | 28 | 40 | 0.293352, 0.966446, 0.096082 | 0 detected | no paired PNG | — | — | — |
| dough.txt | 40 | 16 | 28 | 0.249204, 0.359908, 0.249204 | 0 detected | no paired PNG | — | — | — |
| doughnut.txt | 80 | 32 | 64 | 0.450000, 0.450000, 0.150000 | 8×4 | no paired PNG | — | — | — |
| dragonfang_gun.txt | 196 | 79 | 126 | 0.220512, 1.141047, 0.366279 | 8×2 | no paired PNG | — | — | — |
| dragonfang_sight.txt | 304 | 138 | 244 | 0.106866, 0.549276, 0.099441 | 6×2, 12×6 | no paired PNG | — | — | — |
| dressing.txt | 96 | 40 | 64 | 0.436468, 0.297694, 0.308698 | 12×2 | no paired PNG | — | — | — |
| eaglefire_gun.txt | 430 | 168 | 270 | 0.184521, 1.347017, 0.339979 | 6×2, 8×6 | no paired PNG | — | — | — |
| eaglefire_gun_icon.txt | 430 | 168 | 270 | 0.184521, 1.347017, 0.339979 | 6×2, 8×6 | no paired PNG | — | — | — |
| eaglefire_gun_new.txt | 430 | 168 | 270 | 0.184521, 1.347017, 0.339979 | 6×2, 8×6 | no paired PNG | — | — | — |
| eaglefire_iron_sights.txt | 295 | 119 | 213 | 0.066750, 0.731404, 0.124138 | 12×2 | no paired PNG | — | — | — |
| eaglefire_mag.txt | 32 | 12 | 20 | 0.087114, 0.177352, 0.350961 | 0 detected | no paired PNG | — | — | — |
| eaglefire_sight_real.txt | 295 | 119 | 213 | 0.066750, 0.731404, 0.124138 | 12×2 | no paired PNG | — | — | — |
| eggs.txt | 32 | 12 | 20 | 0.240420, 0.536454, 0.184852 | 0 detected | no paired PNG | — | — | — |
| ekho_gun.txt | 374 | 164 | 252 | 0.214557, 1.622945, 0.307572 | 6×2, 8×6 | no paired PNG | — | — | — |
| ekho_sight.txt | 148 | 56 | 106 | 0.051530, 0.723816, 0.115696 | 6×4 | no paired PNG | — | — | — |
| empire_gun.txt | 480 | 201 | 316 | 0.150000, 0.948207, 0.363189 | 0 detected | no paired PNG | — | — | — |
| empire_sight.txt | 228 | 96 | 164 | 0.055270, 0.470503, 0.085116 | 12×4 | no paired PNG | — | — | — |
| fighterjet_body.txt | 810 | 256 | 302 | 9.000000, 3.300000, 11.750000 | 6×8 | no paired PNG | — | — | — |
| fighterjet_body_1.txt | 333 | 111 | 172 | 9.000000, 3.000000, 11.750000 | 6×4 | no paired PNG | — | — | — |
| fighterjet_canopy.txt | 9 | 9 | 8 | 1.420000, 0.530000, 2.140000 | 0 detected | no paired PNG | — | — | — |
| fighterjet_gear_mainL.txt | 810 | 256 | 10 | 9.000000, 3.300000, 11.750000 | 0 detected | no paired PNG | — | — | — |
| fighterjet_gear_mainR.txt | 810 | 256 | 10 | 9.000000, 3.300000, 11.750000 | 0 detected | no paired PNG | — | — | — |
| fighterjet_gear_nose.txt | 810 | 256 | 10 | 9.000000, 3.300000, 11.750000 | 0 detected | no paired PNG | — | — | — |
| fighterjet_joystick.txt | 120 | 32 | 60 | 0.715812, 0.354807, 0.180505 | 0 detected | no paired PNG | — | — | — |
| fighterjet_missiles.txt | 810 | 256 | 56 | 9.000000, 3.300000, 11.750000 | 0 detected | no paired PNG | — | — | — |
| fighterjet_wheel.txt | 168 | 72 | 140 | 0.400004, 0.711806, 0.711808 | 12×6 | no paired PNG | — | — | — |
| firetruck_body.txt | 570 | 230 | 406 | 2.522090, 2.906351, 7.266399 | 6×4 | no paired PNG | — | — | — |
| firetruck_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.890000, 1.550000 | 0 detected | no paired PNG | — | — | — |
| firetruck_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.890000, 1.550000 | 0 detected | no paired PNG | — | — | — |
| firetruck_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.899224, 0.194414 | 0 detected | no paired PNG | — | — | — |
| firetruck_headlights.txt | 40 | 16 | 20 | 2.048476, 0.254715, 0.125399 | 0 detected | no paired PNG | — | — | — |
| firetruck_seats.txt | 76 | 32 | 48 | 1.787030, 1.526791, 0.995190 | 0 detected | no paired PNG | — | — | — |
| firetruck_siren0.txt | 16 | 8 | 8 | 0.456344, 0.322977, 0.672525 | 0 detected | no paired PNG | — | — | — |
| firetruck_siren1.txt | 16 | 8 | 8 | 0.456345, 0.322977, 0.672525 | 0 detected | no paired PNG | — | — | — |
| firetruck_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| firetruck_taillights.txt | 56 | 16 | 20 | 2.226930, 0.329078, 0.189917 | 0 detected | no paired PNG | — | — | — |
| flashlight.txt | 88 | 32 | 48 | 0.150154, 0.451649, 0.150154 | 0 detected | no paired PNG | — | — | — |
| fury_gun.txt | 228 | 88 | 160 | 0.500000, 0.601701, 0.649505 | 8×2 | no paired PNG | — | — | — |
| fusilaut_gun.txt | 258 | 104 | 192 | 0.104536, 1.219674, 0.435035 | 8×2 | no paired PNG | — | — | — |
| fusilaut_sight.txt | 104 | 38 | 70 | 0.043358, 0.446931, 0.086144 | 0 detected | no paired PNG | — | — | — |
| gascan.txt | 59 | 20 | 30 | 0.837440, 0.825000, 0.275000 | 0 detected | no paired PNG | — | — | — |
| gianttrevally_cooked.txt | 24 | 16 | 28 | 0.342266, 0.617455, 0.046434 | 0 detected | no paired PNG | — | — | — |
| gianttrevally_raw.txt | 60 | 29 | 40 | 0.438455, 0.941171, 0.173870 | 0 detected | no paired PNG | — | — | — |
| glue.txt | 74 | 24 | 32 | 0.141368, 0.319812, 0.615644 | 0 detected | no paired PNG | — | — | — |
| goatfish_cooked.txt | 24 | 16 | 28 | 0.120356, 0.323217, 0.023487 | 0 detected | no paired PNG | — | — | — |
| goatfish_raw.txt | 76 | 36 | 48 | 0.205935, 0.431515, 0.085296 | 0 detected | no paired PNG | — | — | — |
| goldfish_cooked.txt | 40 | 16 | 28 | 0.135287, 0.195870, 0.023487 | 0 detected | no paired PNG | — | — | — |
| goldfish_raw.txt | 60 | 30 | 40 | 0.190178, 0.349002, 0.086724 | 0 detected | no paired PNG | — | — | — |
| golf.txt | 72 | 28 | 40 | 0.311313, 1.178420, 0.091564 | 0 detected | no paired PNG | — | — | — |
| golf_body.txt | 504 | 228 | 452 | 2.522099, 2.458513, 5.228553 | 0 detected | no paired PNG | — | — | — |
| golf_glass_l_front.txt | 7 | 7 | 5 | 0.000000, 0.890000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| golf_glass_l_rear.txt | 8 | 8 | 6 | 0.000000, 0.890000, 0.950000 | 0 detected | no paired PNG | — | — | — |
| golf_glass_r_front.txt | 7 | 7 | 5 | 0.000000, 0.890000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| golf_glass_r_rear.txt | 8 | 8 | 6 | 0.000000, 0.890000, 0.950000 | 0 detected | no paired PNG | — | — | — |
| golf_glass_rear.txt | 4 | 4 | 2 | 2.030000, 0.737422, 0.221605 | 0 detected | no paired PNG | — | — | — |
| golf_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.944900, 0.502059 | 0 detected | no paired PNG | — | — | — |
| golf_headlights.txt | 72 | 24 | 40 | 2.111922, 0.413821, 0.125399 | 6×4 | no paired PNG | — | — | — |
| golf_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.326137 | 8×2 | no paired PNG | — | — | — |
| golf_seats.txt | 152 | 64 | 96 | 1.787046, 1.526792, 2.155282 | 0 detected | no paired PNG | — | — | — |
| golf_steer.txt | 142 | 66 | 122 | 0.767781, 0.771826, 0.311454 | 12×4 | no paired PNG | — | — | — |
| golf_taillights.txt | 40 | 16 | 20 | 2.048478, 0.254716, 0.125398 | 0 detected | no paired PNG | — | — | — |
| grenade.txt | 74 | 28 | 46 | 0.338263, 0.275308, 0.388758 | 0 detected | no paired PNG | — | — | — |
| grizzly_gun.txt | 260 | 118 | 194 | 0.263333, 1.816609, 0.338754 | 8×2 | no paired PNG | — | — | — |
| grizzly_sight.txt | 156 | 64 | 100 | 0.066576, 0.695607, 0.099507 | 0 detected | no paired PNG | — | — | — |
| hammer.txt | 112 | 44 | 66 | 0.343921, 0.817640, 0.116254 | 0 detected | no paired PNG | — | — | — |
| hatchback_body.txt | 408 | 172 | 346 | 2.522097, 2.399293, 5.518237 | 0 detected | no paired PNG | — | — | — |
| hatchback_glass_l_front.txt | 9 | 9 | 7 | 0.000000, 0.920000, 1.790000 | 0 detected | no paired PNG | — | — | — |
| hatchback_glass_l_rear.txt | 9 | 9 | 7 | 0.000000, 0.920000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| hatchback_glass_r_front.txt | 9 | 9 | 7 | 0.000000, 0.920000, 1.790000 | 0 detected | no paired PNG | — | — | — |
| hatchback_glass_r_rear.txt | 9 | 9 | 7 | 0.000000, 0.920000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| hatchback_glass_rear.txt | 4 | 4 | 2 | 2.030000, 0.782503, 0.424016 | 0 detected | no paired PNG | — | — | — |
| hatchback_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.826131, 0.469049 | 0 detected | no paired PNG | — | — | — |
| hatchback_headlights.txt | 40 | 16 | 20 | 2.095224, 0.284461, 0.115463 | 0 detected | no paired PNG | — | — | — |
| hatchback_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.326130 | 8×2 | no paired PNG | — | — | — |
| hatchback_seats.txt | 152 | 64 | 96 | 1.787046, 1.526796, 2.531895 | 0 detected | no paired PNG | — | — | — |
| hatchback_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| hatchback_taillights.txt | 56 | 16 | 20 | 2.226930, 0.329079, 0.189916 | 0 detected | no paired PNG | — | — | — |
| hatchback_wheel.txt | 398 | 208 | 370 | 0.400006, 1.182565, 1.191252 | 6×8, 8×2, 10×1, 13×8 | no paired PNG | — | — | — |
| hawkhound_gun.txt | 184 | 80 | 120 | 0.235253, 1.802004, 0.396399 | 6×2, 8×2 | no paired PNG | — | — | — |
| hawkhound_sight.txt | 302 | 120 | 208 | 0.119286, 0.730167, 0.119505 | 6×4, 12×2 | no paired PNG | — | — | — |
| heartbreaker_gun.txt | 262 | 110 | 182 | 0.177842, 1.319899, 0.307285 | 0 detected | no paired PNG | — | — | — |
| heartbreaker_sight.txt | 233 | 103 | 175 | 0.049128, 0.678419, 0.109317 | 12×2 | no paired PNG | — | — | — |
| heatstim.txt | 40 | 12 | 20 | 0.483784, 0.602896, 0.034556 | 0 detected | no paired PNG | — | — | — |
| hind_body.txt | 1055 | 424 | 804 | 6.376203, 5.490133, 15.227984 | 8×12 | no paired PNG | — | — | — |
| hind_body_1.txt | 779 | 308 | 606 | 6.376203, 4.973959, 15.227984 | 0 detected | no paired PNG | — | — | — |
| hind_rotor_main_blades.txt | 154 | 45 | 64 | 11.803309, 0.100001, 10.553901 | 0 detected | no paired PNG | — | — | — |
| hind_rotor_main_disc.txt | 64 | 32 | 60 | 11.752982, 0.000004, 11.752983 | 32×1 | no paired PNG | — | — | — |
| hind_rotor_tail_blades.txt | 12 | 6 | 8 | 2.500000, 0.000000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| hind_rotor_tail_disc.txt | 32 | 19 | 28 | 2.564000, 0.000000, 2.564000 | 16×1 | no paired PNG | — | — | — |
| hind_seats.txt | 228 | 96 | 144 | 1.787030, 2.234791, 6.152175 | 0 detected | no paired PNG | — | — | — |
| hind_steer.txt | 120 | 32 | 60 | 0.715812, 0.354806, 0.180505 | 0 detected | no paired PNG | — | — | — |
| hind_taillights.txt | 48 | 8 | 24 | 0.255568, 0.338060, 0.287939 | 0 detected | no paired PNG | — | — | — |
| hind_turret.txt | 93 | 53 | 80 | 1.200000, 0.600000, 3.200000 | 12×3 | no paired PNG | — | — | — |
| hind_turret_pitch.txt | 56 | 16 | 20 | 0.700000, 0.250000, 2.600000 | 0 detected | no paired PNG | — | — | — |
| hind_turret_yaw.txt | 37 | 37 | 60 | 1.200000, 0.600000, 1.200000 | 12×3 | no paired PNG | — | — | — |
| hind_wheels.txt | 672 | 288 | 560 | 3.200004, 0.873806, 5.681807 | 12×24 | no paired PNG | — | — | — |
| hockey.txt | 54 | 20 | 36 | 0.369930, 1.095449, 0.070946 | 0 detected | no paired PNG | — | — | — |
| honeybadger_gun.txt | 568 | 227 | 366 | 0.145052, 1.079859, 0.335815 | 6×7, 8×2 | no paired PNG | — | — | — |
| honeybadger_sight.txt | 269 | 107 | 181 | 0.066750, 0.601744, 0.107875 | 0 detected | no paired PNG | — | — | — |
| horsebox_trailer_body.txt | 80 | 80 | 120 | 3.100000, 3.250001, 7.973885 | 0 detected | no paired PNG | — | — | — |
| horsebox_trailer_taillights.txt | 56 | 16 | 20 | 2.804810, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| huey_body.txt | 639 | 184 | 332 | 3.500002, 4.776756, 11.200545 | 6×2 | no paired PNG | — | — | — |
| huey_body_1.txt | 360 | 100 | 192 | 3.500002, 4.276756, 11.200545 | 0 detected | no paired PNG | — | — | — |
| huey_rotor_main_blades.txt | 60 | 14 | 24 | 11.138019, 0.100000, 0.861081 | 0 detected | no paired PNG | — | — | — |
| huey_rotor_main_disc.txt | 64 | 32 | 60 | 11.138020, 0.000002, 11.138022 | 32×1 | no paired PNG | — | — | — |
| huey_rotor_tail_blades.txt | 12 | 6 | 8 | 2.500000, 0.000000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| huey_rotor_tail_disc.txt | 32 | 19 | 28 | 2.564000, 0.000000, 2.564000 | 16×1 | no paired PNG | — | — | — |
| huey_seats.txt | 76 | 32 | 48 | 2.037030, 1.526793, 0.995203 | 0 detected | no paired PNG | — | — | — |
| huey_steer.txt | 120 | 32 | 60 | 0.715812, 0.354806, 0.180505 | 0 detected | no paired PNG | — | — | — |
| huey_taillights.txt | 40 | 16 | 20 | 0.160731, 0.368619, 0.370285 | 0 detected | no paired PNG | — | — | — |
| hummingbird_body.txt | 801 | 225 | 432 | 3.000001, 4.901756, 10.816996 | 6×2 | no paired PNG | — | — | — |
| hummingbird_body_1.txt | 521 | 141 | 292 | 3.000001, 4.401756, 10.816996 | 0 detected | no paired PNG | — | — | — |
| hummingbird_rotor_main_blades.txt | 60 | 14 | 24 | 11.138019, 0.100000, 0.861081 | 0 detected | no paired PNG | — | — | — |
| hummingbird_rotor_main_disc.txt | 64 | 32 | 60 | 11.138020, 0.000002, 11.138022 | 32×1 | no paired PNG | — | — | — |
| hummingbird_rotor_tail_blades.txt | 12 | 6 | 8 | 2.500000, 0.000000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| hummingbird_rotor_tail_disc.txt | 32 | 19 | 28 | 2.564000, 0.000000, 2.564000 | 16×1 | no paired PNG | — | — | — |
| hummingbird_seats.txt | 76 | 32 | 48 | 2.037030, 1.526793, 0.995203 | 0 detected | no paired PNG | — | — | — |
| hummingbird_steer.txt | 120 | 32 | 60 | 0.715812, 0.354806, 0.180505 | 0 detected | no paired PNG | — | — | — |
| hummingbird_taillights.txt | 40 | 16 | 20 | 0.160731, 0.368369, 0.368376 | 0 detected | no paired PNG | — | — | — |
| humvee_body.txt | 481 | 147 | 278 | 2.522099, 2.398432, 5.360358 | 0 detected | no paired PNG | — | — | — |
| humvee_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| humvee_glass_l_rear.txt | 4 | 4 | 2 | 0.000000, 0.830000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| humvee_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| humvee_glass_r_rear.txt | 4 | 4 | 2 | 0.000000, 0.830000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| humvee_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.810966, 0.176730 | 0 detected | no paired PNG | — | — | — |
| humvee_headlights.txt | 60 | 24 | 32 | 2.309049, 0.404513, 0.171783 | 6×4 | no paired PNG | — | — | — |
| humvee_seats.txt | 152 | 64 | 96 | 1.787046, 1.526792, 2.331197 | 0 detected | no paired PNG | — | — | — |
| humvee_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311453 | 12×4 | no paired PNG | — | — | — |
| humvee_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329079, 0.189916 | 0 detected | no paired PNG | — | — | — |
| humvee_wheel.txt | 399 | 210 | 379 | 0.400006, 1.182565, 1.191252 | 5×1, 6×10, 8×2, 13×8 | no paired PNG | — | — | — |
| jackhammer.txt | 100 | 41 | 60 | 1.280304, 0.225000, 0.717232 | 0 detected | no paired PNG | — | — | — |
| jeep_body.txt | 364 | 130 | 232 | 2.522099, 2.398432, 5.110358 | 0 detected | no paired PNG | — | — | — |
| jeep_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.824399, 0.244880 | 0 detected | no paired PNG | — | — | — |
| jeep_headlights.txt | 60 | 24 | 32 | 2.309048, 0.404513, 0.171783 | 6×4 | no paired PNG | — | — | — |
| jeep_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.326130 | 8×2 | no paired PNG | — | — | — |
| jeep_seats.txt | 152 | 64 | 96 | 1.787046, 1.526796, 2.483869 | 0 detected | no paired PNG | — | — | — |
| jeep_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| jeep_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329079, 0.189916 | 0 detected | no paired PNG | — | — | — |
| jeep_wheel.txt | 399 | 210 | 379 | 0.400006, 1.182565, 1.191252 | 5×1, 6×10, 8×2, 13×8 | no paired PNG | — | — | — |
| juice_apple.txt | 40 | 12 | 20 | 0.252659, 0.110353, 0.316678 | 0 detected | no paired PNG | — | — | — |
| juice_grape.txt | 40 | 12 | 20 | 0.252659, 0.110353, 0.316678 | 0 detected | no paired PNG | — | — | — |
| katana.txt | 71 | 29 | 43 | 0.180814, 1.230423, 0.180812 | 0 detected | no paired PNG | — | — | — |
| knife_butcher.txt | 72 | 25 | 46 | 0.243143, 0.922845, 0.055104 | 0 detected | no paired PNG | — | — | — |
| knife_butterfly.txt | 84 | 31 | 44 | 0.115708, 0.816048, 0.055102 | 0 detected | no paired PNG | — | — | — |
| knife_kitchen.txt | 79 | 31 | 49 | 0.141664, 0.980989, 0.067528 | 0 detected | no paired PNG | — | — | — |
| knife_military.txt | 113 | 50 | 85 | 0.181101, 0.921528, 0.068844 | 0 detected | no paired PNG | — | — | — |
| knife_swiss.txt | 36 | 15 | 20 | 0.103603, 0.828181, 0.055102 | 0 detected | no paired PNG | — | — | — |
| kryzkarek_gun.txt | 184 | 68 | 102 | 0.101668, 0.489682, 0.346435 | 0 detected | no paired PNG | — | — | — |
| large_trailer_body.txt | 72 | 72 | 108 | 3.100000, 1.576570, 7.973885 | 0 detected | no paired PNG | — | — | — |
| large_trailer_taillights.txt | 56 | 16 | 20 | 2.804810, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| launcher_rocket_gun.txt | 102 | 40 | 70 | 0.330086, 1.103951, 0.417430 | 8×4 | no paired PNG | — | — | — |
| launcher_rocket_sight.txt | 172 | 72 | 130 | 0.045092, 0.476454, 0.125085 | 0 detected | no paired PNG | — | — | — |
| lettuce.txt | 72 | 25 | 40 | 0.328242, 0.574042, 0.328242 | 0 detected | no paired PNG | — | — | — |
| lobster_cooked.txt | 65 | 24 | 44 | 0.224896, 0.375003, 0.086608 | 0 detected | no paired PNG | — | — | — |
| lobster_raw.txt | 102 | 48 | 76 | 0.552456, 0.924177, 0.129334 | 0 detected | no paired PNG | — | — | — |
| luger_gun.txt | 276 | 120 | 190 | 0.110974, 0.530347, 0.344368 | 6×4, 8×4 | no paired PNG | — | — | — |
| machete.txt | 65 | 23 | 41 | 0.149512, 1.237950, 0.055103 | 0 detected | no paired PNG | — | — | — |
| mag_ace_6.txt | 234 | 96 | 140 | 0.113762, 0.140077, 0.113762 | 6×4, 12×2 | no paired PNG | — | — | — |
| mag_arrow_1.txt | 44 | 13 | 16 | 0.031250, 0.623036, 0.031250 | 0 detected | no paired PNG | — | — | — |
| mag_arrow_birch_1.txt | 44 | 13 | 16 | 0.031250, 0.623036, 0.031250 | 0 detected | no paired PNG | — | — | — |
| mag_arrow_maple_1.txt | 44 | 13 | 16 | 0.031250, 0.623036, 0.031250 | 0 detected | no paired PNG | — | — | — |
| mag_arrow_pine_1.txt | 44 | 13 | 16 | 0.031250, 0.623036, 0.031250 | 0 detected | no paired PNG | — | — | — |
| mag_avenger_13.txt | 24 | 8 | 12 | 0.064132, 0.091924, 0.296605 | 0 detected | no paired PNG | — | — | — |
| mag_bane_21.txt | 48 | 16 | 28 | 0.150000, 0.090000, 0.275000 | 0 detected | no paired PNG | — | — | — |
| mag_bulldog_45.txt | 24 | 8 | 12 | 0.064132, 0.098020, 0.386966 | 0 detected | no paired PNG | — | — | — |
| mag_card_71.txt | 76 | 32 | 56 | 0.271666, 0.105562, 0.271666 | 12×2 | no paired PNG | — | — | — |
| mag_cobra_20.txt | 24 | 8 | 12 | 0.064132, 0.098020, 0.284892 | 0 detected | no paired PNG | — | — | — |
| mag_colt_7.txt | 24 | 8 | 12 | 0.064132, 0.091924, 0.237284 | 0 detected | no paired PNG | — | — | — |
| mag_desert_falcon_7.txt | 24 | 8 | 12 | 0.071930, 0.097122, 0.267010 | 0 detected | no paired PNG | — | — | — |
| mag_dragonfang_150.txt | 80 | 28 | 44 | 0.322229, 0.198936, 0.272317 | 0 detected | no paired PNG | — | — | — |
| mag_ekho_7.txt | 24 | 8 | 12 | 0.211898, 0.167172, 0.054720 | 0 detected | no paired PNG | — | — | — |
| mag_empire_28.txt | 24 | 8 | 12 | 0.064000, 0.080000, 0.312500 | 0 detected | no paired PNG | — | — | — |
| mag_fury_250.txt | 54 | 24 | 38 | 0.241875, 0.280072, 0.241876 | 8×2 | no paired PNG | — | — | — |
| mag_grizzly_5.txt | 24 | 8 | 12 | 0.087124, 0.211570, 0.227159 | 0 detected | no paired PNG | — | — | — |
| mag_hawkhound_8.txt | 24 | 8 | 12 | 0.081546, 0.211570, 0.248217 | 0 detected | no paired PNG | — | — | — |
| mag_kryzkarek_12.txt | 28 | 8 | 12 | 0.064132, 0.091924, 0.237284 | 0 detected | no paired PNG | — | — | — |
| mag_luger_9.txt | 24 | 8 | 12 | 0.064132, 0.091924, 0.237284 | 0 detected | no paired PNG | — | — | — |
| mag_matamorez_17.txt | 24 | 8 | 12 | 0.081546, 0.211570, 0.227187 | 0 detected | no paired PNG | — | — | — |
| mag_military_30.txt | 32 | 12 | 20 | 0.087114, 0.177352, 0.350961 | 0 detected | no paired PNG | — | — | — |
| mag_mp40_32.txt | 24 | 8 | 12 | 0.051305, 0.078020, 0.286966 | 0 detected | no paired PNG | — | — | — |
| mag_nailgun_20.txt | 48 | 16 | 32 | 0.042352, 0.195485, 0.414310 | 0 detected | no paired PNG | — | — | — |
| mag_nykorev_200.txt | 72 | 24 | 36 | 0.322229, 0.198936, 0.272317 | 0 detected | no paired PNG | — | — | — |
| mag_peacemaker_50.txt | 32 | 12 | 20 | 0.088104, 0.371995, 0.039950 | 0 detected | no paired PNG | — | — | — |
| mag_ranger_35.txt | 40 | 16 | 28 | 0.092700, 0.275705, 0.327892 | 0 detected | no paired PNG | — | — | — |
| mag_rocket_1.txt | 52 | 32 | 60 | 0.226528, 0.720251, 0.226528 | 8×4 | no paired PNG | — | — | — |
| mag_sabertooth_10.txt | 24 | 8 | 12 | 0.081546, 0.175244, 0.209007 | 0 detected | no paired PNG | — | — | — |
| mag_scalar_30.txt | 24 | 8 | 12 | 0.064132, 0.184020, 0.386966 | 0 detected | no paired PNG | — | — | — |
| mag_shadowstalkermk2_20.txt | 296 | 88 | 148 | 0.181868, 0.250908, 0.144000 | 12×2 | no paired PNG | — | — | — |
| mag_snayperskya_7.txt | 32 | 12 | 20 | 0.081546, 0.222304, 0.227187 | 0 detected | no paired PNG | — | — | — |
| mag_sportshot_10.txt | 24 | 8 | 12 | 0.081546, 0.211570, 0.248217 | 0 detected | no paired PNG | — | — | — |
| mag_swissgewehr_20.txt | 272 | 67 | 200 | 0.087114, 0.177352, 0.350961 | 0 detected | no paired PNG | — | — | — |
| mag_teklowvka_15.txt | 48 | 16 | 28 | 0.101667, 0.140449, 0.369123 | 0 detected | no paired PNG | — | — | — |
| mag_timberwolf_6.txt | 24 | 8 | 12 | 0.081546, 0.211570, 0.227159 | 0 detected | no paired PNG | — | — | — |
| mag_viper_25.txt | 48 | 20 | 36 | 0.074180, 0.153927, 0.279161 | 0 detected | no paired PNG | — | — | — |
| mag_vonya_7.txt | 24 | 8 | 12 | 0.076518, 0.189108, 0.231278 | 0 detected | no paired PNG | — | — | — |
| mag_yuri_64.txt | 74 | 32 | 56 | 0.093750, 0.392567, 0.103750 | 12×2 | no paired PNG | — | — | — |
| makeshift_bat.txt | 216 | 86 | 84 | 0.309541, 1.152408, 0.320821 | 0 detected | no paired PNG | — | — | — |
| makeshift_scope_sight.txt | 297 | 96 | 152 | 0.153309, 0.501980, 0.299709 | 6×8 | no paired PNG | — | — | — |
| maple_syrup.txt | 80 | 24 | 44 | 0.334712, 0.157964, 0.520697 | 0 detected | no paired PNG | — | — | — |
| maplestrike_gun.txt | 340 | 140 | 224 | 0.184521, 1.347017, 0.341691 | 6×2, 8×6 | no paired PNG | — | — | — |
| maplestrike_iron_sights.txt | 309 | 127 | 237 | 0.072299, 0.731404, 0.138048 | 12×2 | no paired PNG | — | — | — |
| masterkey_gun.txt | 212 | 74 | 118 | 0.183632, 1.572012, 0.342399 | 6×4 | no paired PNG | — | — | — |
| matamorez_gun.txt | 262 | 106 | 176 | 0.126658, 1.541679, 0.324617 | 8×2 | no paired PNG | — | — | — |
| matamorez_sight.txt | 232 | 88 | 132 | 0.060538, 0.551536, 0.060110 | 6×4 | no paired PNG | — | — | — |
| medium_trailer_body.txt | 72 | 72 | 108 | 2.850000, 1.576570, 6.928175 | 0 detected | no paired PNG | — | — | — |
| medium_trailer_taillights.txt | 56 | 16 | 20 | 2.554810, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| medkit.txt | 80 | 32 | 52 | 0.485780, 0.616706, 0.200562 | 0 detected | no paired PNG | — | — | — |
| military_100_mag.txt | 96 | 32 | 52 | 0.410724, 0.174013, 0.298643 | 6×4 | no paired PNG | — | — | — |
| military_30_mag.txt | 32 | 12 | 20 | 0.087114, 0.177352, 0.350961 | 0 detected | no paired PNG | — | — | — |
| minicopter_body.txt | 435 | 431 | 569 | 2.312500, 2.312500, 4.750000 | 6×47 | no paired PNG | — | — | — |
| minnow_cooked.txt | 43 | 16 | 28 | 0.109582, 0.293806, 0.028457 | 0 detected | no paired PNG | — | — | — |
| minnow_raw.txt | 60 | 30 | 40 | 0.134361, 0.382822, 0.065044 | 0 detected | no paired PNG | — | — | — |
| morphine.txt | 108 | 40 | 54 | 0.515022, 0.265440, 0.106588 | 0 detected | no paired PNG | — | — | — |
| mp40_gun.txt | 315 | 121 | 202 | 0.121867, 1.007279, 0.316766 | 8×2 | no paired PNG | — | — | — |
| mp40_sight.txt | 90 | 34 | 54 | 0.057532, 0.560009, 0.050998 | 0 detected | no paired PNG | — | — | — |
| mushroom_brown.txt | 56 | 16 | 28 | 0.200994, 0.200994, 0.226747 | 0 detected | no paired PNG | — | — | — |
| mushroom_brown_roast.txt | 104 | 32 | 56 | 0.274642, 0.309354, 0.072390 | 0 detected | no paired PNG | — | — | — |
| mushroom_red.txt | 76 | 24 | 38 | 0.223878, 0.223878, 0.227300 | 0 detected | no paired PNG | — | — | — |
| mushroom_red_roast.txt | 136 | 48 | 72 | 0.277809, 0.340482, 0.072390 | 0 detected | no paired PNG | — | — | — |
| nailgun_gun.txt | 110 | 38 | 66 | 0.118398, 0.523570, 0.434588 | 0 detected | no paired PNG | — | — | — |
| nightraider_gun.txt | 280 | 108 | 202 | 0.154540, 1.189821, 0.375431 | 8×2 | no paired PNG | — | — | — |
| nightraider_sight.txt | 240 | 98 | 190 | 0.064300, 0.446931, 0.085807 | 12×6 | no paired PNG | — | — | — |
| nightvision_scope_sight.txt | 250 | 102 | 140 | 0.209882, 0.521339, 0.197230 | 6×8 | no paired PNG | — | — | — |
| northernpike_cooked.txt | 24 | 16 | 28 | 0.151416, 0.799111, 0.033188 | 0 detected | no paired PNG | — | — | — |
| northernpike_raw.txt | 68 | 32 | 44 | 0.319578, 1.093271, 0.099262 | 0 detected | no paired PNG | — | — | — |
| nykorev_gun.txt | 298 | 116 | 188 | 0.258039, 1.195139, 0.334630 | 8×2 | no paired PNG | — | — | — |
| nykorev_sight.txt | 272 | 112 | 184 | 0.094002, 0.339328, 0.069170 | 6×4 | no paired PNG | — | — | — |
| offroad_body.txt | 574 | 224 | 434 | 2.522099, 2.398433, 5.110358 | 0 detected | no paired PNG | — | — | — |
| offroad_headlights.txt | 60 | 24 | 32 | 2.309046, 0.404513, 0.171783 | 6×4 | no paired PNG | — | — | — |
| offroad_seats.txt | 152 | 64 | 96 | 1.787046, 1.526796, 2.483869 | 0 detected | no paired PNG | — | — | — |
| offroad_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| offroad_taillights.txt | 56 | 16 | 20 | 2.226923, 0.329079, 0.189916 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_l_rear.txt | 7 | 7 | 5 | 0.000000, 0.830000, 1.370000 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_r_rear.txt | 7 | 7 | 5 | 0.000000, 0.830000, 1.370000 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_rear.txt | 4 | 4 | 2 | 2.030000, 1.060525, 0.142085 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_roof_front.txt | 4 | 4 | 2 | 1.920000, 0.000000, 0.600000 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_roof_rear.txt | 4 | 4 | 2 | 1.920000, 0.000001, 1.320000 | 0 detected | no paired PNG | — | — | — |
| offroader_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.893667, 0.322273 | 0 detected | no paired PNG | — | — | — |
| offroader_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.326130 | 8×2 | no paired PNG | — | — | — |
| orca_body.txt | 810 | 366 | 686 | 3.500002, 5.149232, 13.433900 | 6×2, 8×8 | no paired PNG | — | — | — |
| orca_body_1.txt | 564 | 274 | 526 | 3.500002, 4.626671, 13.433900 | 8×6 | no paired PNG | — | — | — |
| orca_rotor_main_blades.txt | 154 | 45 | 64 | 11.803309, 0.100001, 10.553901 | 0 detected | no paired PNG | — | — | — |
| orca_rotor_main_disc.txt | 64 | 32 | 60 | 11.752982, 0.000004, 11.752983 | 32×1 | no paired PNG | — | — | — |
| orca_rotor_tail_blades.txt | 12 | 6 | 8 | 2.500000, 0.000000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| orca_rotor_tail_disc.txt | 32 | 19 | 28 | 2.564000, 0.000000, 2.564000 | 16×1 | no paired PNG | — | — | — |
| orca_seats.txt | 152 | 64 | 96 | 1.998030, 1.534792, 2.142949 | 0 detected | no paired PNG | — | — | — |
| orca_steer.txt | 120 | 32 | 60 | 0.715812, 0.354806, 0.180505 | 0 detected | no paired PNG | — | — | — |
| orca_taillights.txt | 40 | 16 | 20 | 0.960730, 0.368370, 0.368369 | 0 detected | no paired PNG | — | — | — |
| orca_wheels.txt | 672 | 288 | 560 | 3.620003, 0.821806, 5.711808 | 12×24 | no paired PNG | — | — | — |
| otter_body.txt | 505 | 192 | 290 | 9.488381, 3.587886, 7.942156 | 6×2 | [2, 2] | 4 | 4 | [0, 505] |
| otter_body_1.txt | 364 | 124 | 210 | 9.488381, 3.587886, 7.674357 | 0 detected | no paired PNG | — | — | — |
| otter_prop.txt | 12 | 6 | 8 | 2.050610, 2.050610, 0.000000 | 0 detected | no paired PNG | — | — | — |
| otter_prop_disc.txt | 32 | 18 | 28 | 2.564000, 2.564000, 0.000000 | 16×1 | no paired PNG | — | — | — |
| paddle.txt | 88 | 32 | 54 | 0.279648, 1.298022, 0.057868 | 0 detected | no paired PNG | — | — | — |
| painkillers.txt | 130 | 48 | 78 | 0.205344, 0.205344, 0.249832 | 12×4 | no paired PNG | — | — | — |
| paintballgun_gun.txt | 180 | 64 | 98 | 0.178940, 1.043190, 0.421714 | 6×2, 8×2 | no paired PNG | — | — | — |
| pan.txt | 128 | 60 | 110 | 0.524972, 0.987815, 0.116878 | 12×4 | no paired PNG | — | — | — |
| pancake.txt | 32 | 16 | 28 | 0.386224, 0.386224, 0.064916 | 8×2 | no paired PNG | — | — | — |
| peacemaker_gun.txt | 228 | 84 | 168 | 0.148717, 0.792624, 0.347832 | 0 detected | no paired PNG | — | — | — |
| peacemaker_sight.txt | 175 | 75 | 125 | 0.064418, 0.231933, 0.089826 | 0 detected | no paired PNG | — | — | — |
| pie.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_amber.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_fish.txt | 293 | 192 | 254 | 0.682128, 0.683312, 0.355296 | 12×6 | no paired PNG | — | — | — |
| pie_hanu.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_indigo.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_jade.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_mauve.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_russet.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_teal.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pie_vermillion.txt | 144 | 37 | 70 | 0.682128, 0.682128, 0.207044 | 12×3 | no paired PNG | — | — | — |
| pike.txt | 64 | 25 | 36 | 0.082894, 2.309886, 0.264708 | 0 detected | no paired PNG | — | — | — |
| pitchfork.txt | 156 | 64 | 94 | 0.361156, 1.230524, 0.077837 | 0 detected | no paired PNG | — | — | — |
| pizza.txt | 144 | 60 | 80 | 0.757920, 0.757920, 0.094334 | 12×3 | no paired PNG | — | — | — |
| pizza_mushroom.txt | 288 | 132 | 200 | 0.757920, 0.757920, 0.092006 | 12×3 | no paired PNG | — | — | — |
| poke.txt | 700 | 284 | 450 | 0.614888, 0.614888, 0.392275 | 5×12, 8×8 | no paired PNG | — | — | — |
| police_body.txt | 537 | 190 | 372 | 2.522096, 2.634502, 5.952190 | 0 detected | no paired PNG | — | — | — |
| police_glass_l_front.txt | 10 | 10 | 8 | 0.000000, 0.860000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| police_glass_l_rear.txt | 7 | 7 | 5 | 0.000000, 0.860000, 1.190000 | 0 detected | no paired PNG | — | — | — |
| police_glass_r_front.txt | 10 | 10 | 8 | 0.000000, 0.860000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| police_glass_r_rear.txt | 7 | 7 | 5 | 0.000000, 0.860000, 1.190000 | 0 detected | no paired PNG | — | — | — |
| police_glass_rear.txt | 4 | 4 | 2 | 2.030000, 0.796197, 0.397706 | 0 detected | no paired PNG | — | — | — |
| police_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.865827, 0.390953 | 0 detected | no paired PNG | — | — | — |
| police_headlights.txt | 40 | 16 | 20 | 2.048475, 0.254715, 0.125399 | 0 detected | no paired PNG | — | — | — |
| police_siren0.txt | 16 | 8 | 8 | 0.456344, 0.322977, 0.672525 | 0 detected | no paired PNG | — | — | — |
| police_siren1.txt | 16 | 8 | 8 | 0.456345, 0.322978, 0.672525 | 0 detected | no paired PNG | — | — | — |
| police_steer.txt | 142 | 66 | 122 | 0.767781, 0.771826, 0.311454 | 12×4 | no paired PNG | — | — | — |
| police_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| pork_cooked.txt | 64 | 32 | 40 | 0.756254, 0.732798, 0.101838 | 0 detected | no paired PNG | — | — | — |
| pork_raw.txt | 64 | 32 | 40 | 0.756254, 0.732798, 0.101838 | 0 detected | no paired PNG | — | — | — |
| potato.txt | 40 | 16 | 28 | 0.249204, 0.359908, 0.249204 | 0 detected | no paired PNG | — | — | — |
| potato_baked.txt | 60 | 52 | 80 | 0.349825, 0.431044, 0.224929 | 0 detected | no paired PNG | — | — | — |
| pumpkin.txt | 120 | 35 | 60 | 0.342641, 0.426777, 0.362161 | 0 detected | no paired PNG | — | — | — |
| quad_body.txt | 730 | 182 | 370 | 1.567724, 0.994374, 3.047921 | 0 detected | no paired PNG | — | — | — |
| quad_headlights.txt | 42 | 16 | 20 | 1.394017, 0.146610, 0.269861 | 0 detected | no paired PNG | — | — | — |
| quad_steer.txt | 84 | 36 | 66 | 0.929367, 0.462120, 0.262137 | 0 detected | no paired PNG | — | — | — |
| quad_taillights.txt | 48 | 16 | 20 | 1.394019, 0.158994, 0.189913 | 0 detected | no paired PNG | — | — | — |
| quad_wheel.txt | 508 | 216 | 380 | 0.400004, 0.886924, 0.893438 | 6×8, 8×2, 12×1, 13×8 | no paired PNG | — | — | — |
| quadbarrel_gun.txt | 349 | 106 | 178 | 0.333088, 1.304299, 0.342328 | 6×4 | no paired PNG | — | — | — |
| rag.txt | 56 | 24 | 44 | 0.436468, 0.297694, 0.297694 | 12×2 | no paired PNG | — | — | — |
| rake.txt | 114 | 46 | 52 | 0.280171, 1.053567, 0.426366 | 0 detected | no paired PNG | — | — | — |
| red_dot_sight.txt | 278 | 80 | 212 | 0.116070, 0.178130, 0.136149 | 6×2 | no paired PNG | — | — | — |
| red_halo_sight.txt | 228 | 72 | 130 | 0.083230, 0.146663, 0.129625 | 0 detected | no paired PNG | — | — | — |
| red_kobra_sight.txt | 276 | 80 | 212 | 0.108622, 0.178130, 0.144530 | 6×2 | no paired PNG | — | — | — |
| rice.txt | 128 | 32 | 64 | 0.184415, 0.574151, 0.085930 | 0 detected | no paired PNG | — | — | — |
| rice_cooked.txt | 108 | 84 | 124 | 0.614888, 0.614888, 0.320190 | 8×8 | no paired PNG | — | — | — |
| rifle_birch_gun.txt | 345 | 108 | 182 | 0.187029, 1.631516, 0.392361 | 0 detected | no paired PNG | — | — | — |
| rifle_birch_sight.txt | 142 | 48 | 74 | 0.029877, 0.719709, 0.097177 | 0 detected | no paired PNG | — | — | — |
| rifle_maple_gun.txt | 345 | 108 | 182 | 0.187029, 1.631516, 0.392361 | 0 detected | no paired PNG | — | — | — |
| rifle_maple_sight.txt | 142 | 48 | 74 | 0.029877, 0.719709, 0.097177 | 0 detected | no paired PNG | — | — | — |
| rifle_pine_gun.txt | 345 | 108 | 182 | 0.187029, 1.631516, 0.392361 | 0 detected | no paired PNG | — | — | — |
| rifle_pine_sight.txt | 142 | 48 | 74 | 0.029877, 0.719709, 0.097177 | 0 detected | no paired PNG | — | — | — |
| roadster_body.txt | 457 | 152 | 294 | 2.522096, 2.424579, 5.952190 | 0 detected | no paired PNG | — | — | — |
| roadster_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.800000, 1.700000 | 0 detected | no paired PNG | — | — | — |
| roadster_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.800000, 1.700000 | 0 detected | no paired PNG | — | — | — |
| roadster_glass_rear.txt | 4 | 4 | 2 | 2.030000, 0.790165, 0.339469 | 0 detected | no paired PNG | — | — | — |
| roadster_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.850453, 0.423355 | 0 detected | no paired PNG | — | — | — |
| roadster_headlights.txt | 40 | 16 | 20 | 2.048475, 0.254715, 0.125399 | 0 detected | no paired PNG | — | — | — |
| roadster_seats.txt | 76 | 32 | 48 | 1.787030, 1.526791, 0.995191 | 0 detected | no paired PNG | — | — | — |
| roadster_steer.txt | 142 | 66 | 122 | 0.767781, 0.771826, 0.311454 | 12×4 | no paired PNG | — | — | — |
| roadster_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| roadster_wheel.txt | 508 | 216 | 380 | 0.400006, 1.182565, 1.191252 | 6×8, 8×2, 12×1, 13×8 | no paired PNG | — | — | — |
| rocket_projectile.txt | 52 | 32 | 60 | 0.226528, 0.720251, 0.226528 | 8×4 | no paired PNG | — | — | — |
| roll_lobster.txt | 335 | 159 | 232 | 0.347937, 0.538573, 0.224463 | 0 detected | no paired PNG | — | — | — |
| runabout_body.txt | 226 | 88 | 172 | 3.000000, 2.954033, 9.310365 | 0 detected | no paired PNG | — | — | — |
| runabout_seats.txt | 152 | 64 | 96 | 1.787030, 1.526792, 2.651176 | 0 detected | no paired PNG | — | — | — |
| runabout_steer.txt | 142 | 66 | 122 | 0.767780, 0.771826, 0.311454 | 12×4 | no paired PNG | — | — | — |
| sabertooth_gun.txt | 244 | 104 | 164 | 0.152661, 1.587215, 0.323710 | 8×2 | no paired PNG | — | — | — |
| sabertooth_sight.txt | 330 | 130 | 228 | 0.105280, 0.719552, 0.089992 | 6×4, 12×2 | no paired PNG | — | — | — |
| salmon_cooked.txt | 40 | 16 | 28 | 0.215657, 0.573829, 0.029924 | 0 detected | no paired PNG | — | — | — |
| salmon_meuniere.txt | 192 | 92 | 148 | 0.689582, 0.689582, 0.150796 | 5×4, 8×3 | no paired PNG | — | — | — |
| salmon_meuniere2.txt | 192 | 92 | 148 | 0.689582, 0.689582, 0.150796 | 5×4, 8×3 | no paired PNG | — | — | — |
| salmon_raw.txt | 64 | 30 | 40 | 0.322837, 0.765644, 0.130087 | 0 detected | no paired PNG | — | — | — |
| sandwich_bass.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_beef.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_beef2.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_blt.txt | 94 | 28 | 48 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_blt2.txt | 94 | 28 | 48 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_cheese.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_cheesesteak.txt | 94 | 28 | 48 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_dolphinfish.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_gianttrevally.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_goatfish.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_goldfish.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_ham.txt | 80 | 24 | 40 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_lobster.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_minnow.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_northernpike.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_salmon.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_shrimp.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_trout.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_tuna.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| sandwich_venison.txt | 62 | 20 | 32 | 0.366914, 0.441642, 0.093300 | 0 detected | no paired PNG | — | — | — |
| saw.txt | 88 | 36 | 68 | 0.850854, 0.348750, 0.068016 | 0 detected | no paired PNG | — | — | — |
| sawed_off_gun.txt | 177 | 58 | 98 | 0.183632, 1.287103, 0.334250 | 6×2 | no paired PNG | — | — | — |
| scalar_gun.txt | 234 | 88 | 144 | 0.137809, 0.924336, 0.306956 | 0 detected | no paired PNG | — | — | — |
| scalar_sight.txt | 88 | 30 | 50 | 0.059454, 0.473816, 0.063579 | 0 detected | no paired PNG | — | — | — |
| schofield_gun.txt | 150 | 68 | 100 | 0.222247, 1.670762, 0.319167 | 6×2, 8×2 | no paired PNG | — | — | — |
| schofield_sight.txt | 198 | 88 | 152 | 0.048230, 0.882372, 0.075531 | 0 detected | no paired PNG | — | — | — |
| scope_16x_sight.txt | 451 | 264 | 432 | 0.152094, 0.521339, 0.189283 | 6×5, 12×9 | no paired PNG | — | — | — |
| scope_7x_sight.txt | 290 | 140 | 240 | 0.140162, 0.521339, 0.205716 | 6×3, 12×6 | no paired PNG | — | — | — |
| scope_8x_sight.txt | 234 | 120 | 200 | 0.170945, 0.521339, 0.194330 | 6×4, 12×6 | no paired PNG | — | — | — |
| scythe.txt | 86 | 33 | 58 | 0.852284, 1.531503, 0.337186 | 0 detected | no paired PNG | — | — | — |
| seaweed.txt | 42 | 21 | 32 | 0.457095, 0.371373, 0.036309 | 0 detected | no paired PNG | — | — | — |
| sedan_body.txt | 515 | 182 | 366 | 2.522096, 2.433513, 5.952190 | 0 detected | no paired PNG | — | — | — |
| sedan_glass_l_front.txt | 10 | 10 | 8 | 0.000000, 0.860000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| sedan_glass_l_rear.txt | 7 | 7 | 5 | 0.000000, 0.860000, 1.190000 | 0 detected | no paired PNG | — | — | — |
| sedan_glass_r_front.txt | 10 | 10 | 8 | 0.000000, 0.860000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| sedan_glass_r_rear.txt | 7 | 7 | 5 | 0.000000, 0.860000, 1.190000 | 0 detected | no paired PNG | — | — | — |
| sedan_glass_rear.txt | 4 | 4 | 2 | 2.030000, 0.790165, 0.446615 | 0 detected | no paired PNG | — | — | — |
| sedan_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.750000, 0.499833 | 0 detected | no paired PNG | — | — | — |
| sedan_headlights.txt | 40 | 16 | 20 | 2.048475, 0.254715, 0.125399 | 0 detected | no paired PNG | — | — | — |
| sedan_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.326131 | 8×2 | no paired PNG | — | — | — |
| sedan_seats.txt | 152 | 64 | 96 | 1.787046, 1.526792, 2.391222 | 0 detected | no paired PNG | — | — | — |
| sedan_steer.txt | 142 | 66 | 122 | 0.767781, 0.771826, 0.311454 | 12×4 | no paired PNG | — | — | — |
| sedan_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| sedan_wheel.txt | 398 | 208 | 370 | 0.400006, 1.182565, 1.191252 | 6×8, 8×2, 10×1, 13×8 | no paired PNG | — | — | — |
| semi_0.txt | 817 | 231 | 404 | 3.184128, 3.690607, 7.075001 | 5×6, 8×4 | no paired PNG | — | — | — |
| shadowstalker_gun.txt | 394 | 134 | 244 | 0.126266, 1.447017, 0.341931 | 6×11 | no paired PNG | — | — | — |
| shadowstalker_scope_sight.txt | 128 | 48 | 72 | 0.122070, 0.521339, 0.168644 | 0 detected | no paired PNG | — | — | — |
| shadowstalker_sight.txt | 128 | 48 | 72 | 0.122070, 0.521339, 0.168644 | 0 detected | no paired PNG | — | — | — |
| shadowstalkermk2_gun.txt | 706 | 265 | 438 | 0.148316, 1.496576, 0.356219 | 6×1 | no paired PNG | — | — | — |
| shadowstalkermk2_scope_sight.txt | 142 | 64 | 96 | 0.120000, 0.180000, 0.175220 | 6×2, 12×1 | no paired PNG | — | — | — |
| shadowstalkermk2_sight.txt | 126 | 48 | 84 | 0.120000, 0.180000, 0.175219 | 6×2 | no paired PNG | — | — | — |
| ship_body.txt | 1444 | 395 | 780 | 24.000002, 22.000011, 67.500000 | 0 detected | [4, 2] | 8 | 8 | [0, 1444] |
| ship_glass_l_front.txt | 4 | 4 | 2 | 0.000000, 2.560000, 2.810000 | 0 detected | no paired PNG | — | — | — |
| ship_glass_l_rear.txt | 4 | 4 | 2 | 0.000000, 2.560000, 2.810000 | 0 detected | no paired PNG | — | — | — |
| ship_glass_r_front.txt | 4 | 4 | 2 | 0.000000, 2.560000, 2.810000 | 0 detected | no paired PNG | — | — | — |
| ship_glass_r_rear.txt | 4 | 4 | 2 | 0.000000, 2.560000, 2.810000 | 0 detected | no paired PNG | — | — | — |
| ship_glass_windshield.txt | 4 | 4 | 2 | 14.760000, 2.560000, 0.500000 | 0 detected | no paired PNG | — | — | — |
| shovel.txt | 74 | 33 | 56 | 0.334030, 1.230524, 0.074236 | 0 detected | no paired PNG | — | — | — |
| shrimp_cooked.txt | 64 | 23 | 42 | 0.220713, 0.211002, 0.049262 | 0 detected | no paired PNG | — | — | — |
| shrimp_raw.txt | 58 | 32 | 48 | 0.220712, 0.297055, 0.049262 | 0 detected | no paired PNG | — | — | — |
| sks_action_body.txt | 979 | 288 | 420 | 0.151000, 1.820000, 0.432529 | 6×4, 8×6, 12×2 | no paired PNG | — | — | — |
| sks_action_bolt.txt | 979 | 288 | 76 | 0.151000, 1.820000, 0.432529 | 0 detected | no paired PNG | — | — | — |
| sks_gun.txt | 979 | 288 | 496 | 0.151000, 1.820000, 0.432529 | 6×4, 8×6, 12×2 | no paired PNG | — | — | — |
| skycrane_body.txt | 831 | 238 | 410 | 6.999999, 5.088304, 12.585141 | 6×2 | no paired PNG | — | — | — |
| skycrane_body_1.txt | 358 | 98 | 176 | 6.999999, 4.588303, 12.335141 | 0 detected | no paired PNG | — | — | — |
| skycrane_rotor_main_blades.txt | 154 | 45 | 64 | 11.803309, 0.100001, 10.553901 | 0 detected | no paired PNG | — | — | — |
| skycrane_rotor_main_disc.txt | 64 | 32 | 60 | 11.752982, 0.000004, 11.752983 | 32×1 | no paired PNG | — | — | — |
| skycrane_rotor_tail_blades.txt | 12 | 6 | 8 | 2.500000, 0.000000, 0.400000 | 0 detected | no paired PNG | — | — | — |
| skycrane_rotor_tail_disc.txt | 32 | 19 | 28 | 2.564000, 0.000000, 2.564000 | 16×1 | no paired PNG | — | — | — |
| skycrane_seats.txt | 38 | 16 | 24 | 0.787030, 1.526791, 0.995176 | 0 detected | no paired PNG | — | — | — |
| skycrane_steer.txt | 120 | 32 | 60 | 0.715812, 0.354806, 0.180505 | 0 detected | no paired PNG | — | — | — |
| skycrane_taillights.txt | 40 | 8 | 20 | 0.174106, 0.500000, 0.500000 | 0 detected | no paired PNG | — | — | — |
| sledgehammer.txt | 92 | 32 | 54 | 0.458342, 1.043521, 0.244398 | 0 detected | no paired PNG | — | — | — |
| small_trailer_body.txt | 72 | 72 | 108 | 2.224994, 1.576570, 5.882464 | 0 detected | no paired PNG | — | — | — |
| small_trailer_taillights.txt | 56 | 16 | 20 | 1.929804, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| snayperskya_gun.txt | 252 | 102 | 176 | 0.126658, 1.630097, 0.324617 | 8×2 | no paired PNG | — | — | — |
| snayperskya_sight.txt | 196 | 80 | 116 | 0.066576, 0.712110, 0.070358 | 0 detected | no paired PNG | — | — | — |
| splint.txt | 192 | 80 | 132 | 0.653012, 0.372296, 0.250146 | 0 detected | no paired PNG | — | — | — |
| sportshot_gun.txt | 118 | 46 | 84 | 0.105917, 1.802004, 0.392765 | 8×2 | no paired PNG | — | — | — |
| sportshot_sight.txt | 206 | 86 | 160 | 0.062234, 0.718385, 0.119505 | 12×2 | no paired PNG | — | — | — |
| squid_cooked.txt | 32 | 12 | 20 | 0.424264, 0.550000, 0.424264 | 0 detected | no paired PNG | — | — | — |
| squid_raw.txt | 150 | 57 | 78 | 0.615910, 1.442324, 0.615910 | 0 detected | no paired PNG | — | — | — |
| stew_mushroom.txt | 212 | 104 | 182 | 0.614888, 0.614888, 0.258497 | 8×7 | no paired PNG | — | — | — |
| stew_seaweed.txt | 96 | 72 | 110 | 0.614888, 0.614888, 0.258497 | 8×9 | no paired PNG | — | — | — |
| stew_vegetables.txt | 224 | 104 | 170 | 0.614888, 0.614888, 0.275196 | 8×7 | no paired PNG | — | — | — |
| sugarcane.txt | 112 | 32 | 56 | 0.171475, 0.606179, 0.080067 | 0 detected | no paired PNG | — | — | — |
| suppressor.txt | 34 | 16 | 28 | 0.086080, 0.367271, 0.086080 | 8×2 | no paired PNG | — | — | — |
| suturekit.txt | 133 | 54 | 63 | 0.483518, 0.616706, 0.177369 | 0 detected | no paired PNG | — | — | — |
| swissgewehr_gun.txt | 305 | 116 | 220 | 0.110000, 1.283845, 0.312285 | 8×2 | no paired PNG | — | — | — |
| swissgewehr_sight.txt | 244 | 88 | 148 | 0.066750, 0.731404, 0.098117 | 0 detected | no paired PNG | — | — | — |
| syrup_cough.txt | 102 | 48 | 78 | 0.212690, 0.212690, 0.288353 | 12×4 | no paired PNG | — | — | — |
| tablets.txt | 102 | 48 | 78 | 0.212690, 0.212690, 0.288353 | 12×4 | no paired PNG | — | — | — |
| tank_gun.txt | 24 | 8 | 12 | 0.500001, 0.500001, 5.656250 | 0 detected | no paired PNG | — | — | — |
| tank_hull.txt | 229 | 70 | 118 | 6.523053, 2.379321, 9.453153 | 0 detected | no paired PNG | — | — | — |
| tank_seat_driver.txt | 38 | 16 | 24 | 0.787030, 1.526791, 0.995176 | 0 detected | no paired PNG | — | — | — |
| tank_seat_gunner.txt | 38 | 16 | 24 | 0.787030, 1.526792, 0.995175 | 0 detected | no paired PNG | — | — | — |
| tank_steer.txt | 238 | 70 | 170 | 0.767780, 0.767780, 0.116719 | 12×1 | no paired PNG | — | — | — |
| tank_treads.txt | 128 | 64 | 112 | 5.922868, 1.239735, 8.698366 | 0 detected | no paired PNG | — | — | — |
| tank_turret.txt | 138 | 52 | 80 | 3.725587, 2.698839, 5.562827 | 6×4 | no paired PNG | — | — | — |
| tank_turret_1.txt | 26 | 12 | 16 | 3.725587, 1.389064, 4.269166 | 0 detected | no paired PNG | — | — | — |
| tank_wheel.txt | 354 | 144 | 280 | 1.300006, 1.485172, 1.485178 | 8×2, 16×6 | no paired PNG | — | — | — |
| taro.txt | 48 | 18 | 32 | 0.261205, 0.292026, 0.233539 | 0 detected | no paired PNG | — | — | — |
| taro_mooncake.txt | 38 | 26 | 48 | 0.380346, 0.383822, 0.259808 | 0 detected | no paired PNG | — | — | — |
| taro_poi.txt | 156 | 87 | 136 | 0.614888, 0.614888, 0.338056 | 8×8 | no paired PNG | — | — | — |
| taro_ricecake.txt | 192 | 74 | 120 | 0.327160, 0.685131, 0.265691 | 0 detected | no paired PNG | — | — | — |
| taro_steamed.txt | 312 | 128 | 212 | 0.614888, 0.614888, 0.336291 | 8×6 | no paired PNG | — | — | — |
| teklowvka_gun.txt | 206 | 82 | 124 | 0.150873, 0.587868, 0.334106 | 8×2 | no paired PNG | — | — | — |
| timberwolf_gun.txt | 156 | 66 | 100 | 0.244902, 1.886620, 0.392765 | 6×2, 8×2 | no paired PNG | — | — | — |
| timberwolf_sight.txt | 256 | 122 | 210 | 0.062234, 0.748142, 0.101421 | 12×4 | no paired PNG | — | — | — |
| tomato.txt | 40 | 17 | 20 | 0.250000, 0.280443, 0.250000 | 0 detected | no paired PNG | — | — | — |
| tractor_body.txt | 628 | 172 | 342 | 2.607874, 2.909479, 5.250006 | 0 detected | no paired PNG | — | — | — |
| tractor_glass_l_front.txt | 4 | 4 | 2 | 0.000000, 0.830000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| tractor_glass_r_front.txt | 4 | 4 | 2 | 0.000000, 0.830000, 1.310000 | 0 detected | no paired PNG | — | — | — |
| tractor_glass_rear.txt | 4 | 4 | 2 | 2.060000, 0.830000, 0.000000 | 0 detected | no paired PNG | — | — | — |
| tractor_glass_windshield.txt | 4 | 4 | 2 | 2.090000, 0.830000, 0.000000 | 0 detected | no paired PNG | — | — | — |
| tractor_headlights.txt | 40 | 16 | 20 | 1.300003, 0.254715, 0.125396 | 0 detected | no paired PNG | — | — | — |
| tractor_steer.txt | 142 | 66 | 122 | 0.767780, 0.771826, 0.311454 | 12×4 | no paired PNG | — | — | — |
| tractor_taillights.txt | 56 | 16 | 20 | 1.726928, 0.329078, 0.189914 | 0 detected | no paired PNG | — | — | — |
| tractor_wheel_front.txt | 108 | 48 | 92 | 0.600008, 1.779516, 1.779518 | 12×4 | no paired PNG | — | — | — |
| tractor_wheel_rear.txt | 108 | 48 | 92 | 0.800012, 2.076102, 2.076106 | 12×4 | no paired PNG | — | — | — |
| trailer_0.txt | 268 | 74 | 111 | 3.000000, 2.499998, 16.100001 | 5×2 | no paired PNG | — | — | — |
| train_body.txt | 929 | 288 | 468 | 3.377044, 4.042894, 10.684744 | 6×6 | [4, 2] | 7 | 7 | [0, 929] |
| train_bogie.txt | 348 | 116 | 206 | 3.352478, 1.197033, 3.081334 | 12×8 | [2, 1] | 2 | 2 | [0, 348] |
| train_bogie_frame.txt | 60 | 20 | 30 | 3.352478, 0.834388, 2.250000 | 0 detected | no paired PNG | — | — | — |
| train_boxcar.txt | 477 | 124 | 240 | 3.577041, 4.742792, 10.504641 | 0 detected | [2, 2] | 4 | 4 | [0, 477] |
| train_car.txt | 354 | 112 | 176 | 3.377038, 1.760417, 10.684744 | 0 detected | [2, 1] | 2 | 2 | [0, 354] |
| train_headlights.txt | 48 | 16 | 24 | 1.974019, 0.467936, 0.192126 | 0 detected | no paired PNG | — | — | — |
| train_seat.txt | 38 | 16 | 24 | 0.787030, 1.526792, 0.995176 | 0 detected | no paired PNG | — | — | — |
| train_steer.txt | 120 | 32 | 60 | 0.715812, 0.354806, 0.180505 | 0 detected | no paired PNG | — | — | — |
| train_tanker.txt | 598 | 200 | 308 | 3.377038, 4.078402, 10.684742 | 6×2, 12×2 | [2, 2] | 4 | 4 | [0, 598] |
| train_wheel.txt | 72 | 24 | 44 | 0.215301, 1.197031, 1.197031 | 12×2 | no paired PNG | — | — | — |
| trout_cooked.txt | 40 | 16 | 28 | 0.135287, 0.326450, 0.023487 | 0 detected | no paired PNG | — | — | — |
| trout_raw.txt | 60 | 30 | 40 | 0.193589, 0.510429, 0.086724 | 0 detected | no paired PNG | — | — | — |
| truck_body.txt | 530 | 208 | 390 | 2.522099, 2.398432, 5.110358 | 0 detected | no paired PNG | — | — | — |
| truck_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| truck_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| truck_glass_rear.txt | 4 | 4 | 2 | 2.030000, 0.830000, 0.000000 | 0 detected | no paired PNG | — | — | — |
| truck_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.871269, 0.378671 | 0 detected | no paired PNG | — | — | — |
| truck_headlights.txt | 60 | 24 | 32 | 2.309045, 0.404513, 0.171773 | 6×4 | no paired PNG | — | — | — |
| truck_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.326130 | 8×2 | no paired PNG | — | — | — |
| truck_seats.txt | 76 | 32 | 48 | 1.787030, 1.526791, 0.995191 | 0 detected | no paired PNG | — | — | — |
| truck_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| truck_taillights.txt | 56 | 16 | 20 | 2.226923, 0.329078, 0.189908 | 0 detected | no paired PNG | — | — | — |
| tuna_cooked.txt | 24 | 16 | 28 | 0.330333, 0.876874, 0.056036 | 0 detected | no paired PNG | — | — | — |
| tuna_raw.txt | 72 | 38 | 56 | 0.497808, 1.253118, 0.188006 | 0 detected | no paired PNG | — | — | — |
| ural_body.txt | 470 | 154 | 288 | 2.522093, 2.648432, 6.610358 | 0 detected | no paired PNG | — | — | — |
| ural_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.710000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| ural_glass_l_rear.txt | 4 | 4 | 2 | 0.000000, 0.710000, 0.470000 | 0 detected | no paired PNG | — | — | — |
| ural_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.710000, 1.250000 | 0 detected | no paired PNG | — | — | — |
| ural_glass_r_rear.txt | 4 | 4 | 2 | 0.000000, 0.710000, 0.470000 | 0 detected | no paired PNG | — | — | — |
| ural_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.777733, 0.289882 | 0 detected | no paired PNG | — | — | — |
| ural_headlights.txt | 60 | 24 | 32 | 2.309049, 0.404513, 0.171783 | 6×4 | no paired PNG | — | — | — |
| ural_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| ural_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329079, 0.189916 | 0 detected | no paired PNG | — | — | — |
| vaccine.txt | 108 | 40 | 54 | 0.515022, 0.265440, 0.106588 | 0 detected | no paired PNG | — | — | — |
| van_body.txt | 589 | 224 | 422 | 2.522099, 2.398433, 5.110358 | 0 detected | no paired PNG | — | — | — |
| van_glass_l_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| van_glass_r_front.txt | 6 | 6 | 4 | 0.000000, 0.830000, 1.280000 | 0 detected | no paired PNG | — | — | — |
| van_glass_rear.txt | 4 | 4 | 2 | 2.030000, 0.830000, 0.000000 | 0 detected | no paired PNG | — | — | — |
| van_glass_windshield.txt | 4 | 4 | 2 | 2.030000, 0.893667, 0.322273 | 0 detected | no paired PNG | — | — | — |
| van_headlights.txt | 60 | 24 | 32 | 2.309048, 0.404513, 0.171783 | 6×4 | no paired PNG | — | — | — |
| van_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.326130 | 8×2 | no paired PNG | — | — | — |
| van_seats.txt | 76 | 32 | 48 | 1.787030, 1.526791, 0.995191 | 0 detected | no paired PNG | — | — | — |
| van_steer.txt | 142 | 66 | 122 | 0.767781, 0.771825, 0.311454 | 12×4 | no paired PNG | — | — | — |
| van_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| venison_cooked.txt | 64 | 32 | 40 | 0.756254, 0.732798, 0.101838 | 0 detected | no paired PNG | — | — | — |
| venison_raw.txt | 64 | 32 | 40 | 0.756254, 0.732798, 0.101838 | 0 detected | no paired PNG | — | — | — |
| viper_gun.txt | 282 | 112 | 192 | 0.143920, 0.993731, 0.323189 | 0 detected | no paired PNG | — | — | — |
| viper_sight.txt | 256 | 112 | 192 | 0.075270, 0.560009, 0.085116 | 12×4 | no paired PNG | — | — | — |
| vitamins.txt | 102 | 48 | 78 | 0.212690, 0.212690, 0.288353 | 12×4 | no paired PNG | — | — | — |
| vonya_gun.txt | 280 | 112 | 164 | 0.126065, 1.110546, 0.317361 | 8×2 | no paired PNG | — | — | — |
| vonya_sight.txt | 132 | 48 | 74 | 0.041536, 0.220000, 0.075904 | 0 detected | no paired PNG | — | — | — |
| waffle.txt | 24 | 8 | 12 | 0.369286, 0.369286, 0.064916 | 0 detected | no paired PNG | — | — | — |
| wagon_body.txt | 101 | 101 | 226 | 2.520000, 2.329000, 5.483611 | 0 detected | no paired PNG | — | — | — |
| wagon_bumper_front.txt | 24 | 24 | 44 | 2.520000, 0.259451, 0.157184 | 0 detected | no paired PNG | — | — | — |
| wagon_bumper_rear.txt | 24 | 24 | 44 | 2.520000, 0.259450, 0.146938 | 0 detected | no paired PNG | — | — | — |
| wagon_exhaust.txt | 8 | 8 | 10 | 0.197415, 0.364491, 2.000001 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_l_front.txt | 4 | 4 | 2 | 0.000000, 0.820000, 1.120000 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_l_mid1.txt | 4 | 4 | 2 | 0.000000, 0.820000, 0.930000 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_l_rear.txt | 4 | 4 | 2 | 0.000000, 0.820000, 0.980000 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_r_front.txt | 4 | 4 | 2 | 0.000000, 0.820000, 1.120000 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_r_mid1.txt | 4 | 4 | 2 | 0.000000, 0.820000, 0.930000 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_r_rear.txt | 4 | 4 | 2 | 0.000000, 0.820000, 0.980000 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_rear.txt | 4 | 4 | 2 | 2.029928, 0.820000, 0.120000 | 0 detected | no paired PNG | — | — | — |
| wagon_glass_windshield.txt | 4 | 4 | 2 | 2.029928, 0.795000, 0.450000 | 0 detected | no paired PNG | — | — | — |
| wagon_headlights.txt | 40 | 16 | 20 | 2.048475, 0.254715, 0.125399 | 0 detected | no paired PNG | — | — | — |
| wagon_hitch.txt | 24 | 24 | 40 | 0.100000, 0.150000, 0.250003 | 8×2 | no paired PNG | — | — | — |
| wagon_seats.txt | 152 | 64 | 96 | 1.787046, 1.526792, 2.186222 | 0 detected | no paired PNG | — | — | — |
| wagon_steer.txt | 142 | 66 | 122 | 0.767781, 0.771826, 0.311454 | 12×4 | no paired PNG | — | — | — |
| wagon_taillights.txt | 56 | 16 | 20 | 2.226929, 0.329078, 0.189916 | 0 detected | no paired PNG | — | — | — |
| wheat.txt | 72 | 24 | 36 | 0.104909, 0.573056, 0.106072 | 0 detected | no paired PNG | — | — | — |
| xmas_chicken.txt | 92 | 32 | 54 | 0.185114, 0.615820, 0.185114 | 0 detected | no paired PNG | — | — | — |
| xmas_gingerbread.txt | 110 | 38 | 72 | 0.300374, 0.423360, 0.041542 | 0 detected | no paired PNG | — | — | — |
| xmas_saw.txt | 350 | 130 | 206 | 1.213607, 0.614820, 0.499918 | 6×4 | no paired PNG | — | — | — |
| xmas_snow_shovel.txt | 124 | 40 | 74 | 0.525238, 1.340655, 0.080946 | 0 detected | no paired PNG | — | — | — |
| yuri_gun.txt | 212 | 77 | 130 | 0.135621, 0.993731, 0.317991 | 0 detected | no paired PNG | — | — | — |
| yuri_sight.txt | 176 | 64 | 92 | 0.097992, 0.563245, 0.053972 | 6×4 | no paired PNG | — | — | — |
| zubeknakov_gun.txt | 238 | 96 | 152 | 0.126065, 1.240082, 0.317361 | 8×2 | no paired PNG | — | — | — |
| zubeknakov_sight.txt | 224 | 88 | 124 | 0.094002, 0.487014, 0.051079 | 6×4 | no paired PNG | — | — | — |
| zweihander.txt | 132 | 51 | 80 | 0.362940, 1.472028, 0.100000 | 0 detected | no paired PNG | — | — | — |
