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
