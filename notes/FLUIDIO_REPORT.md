# Fluid deployable art

The reference barrel is **192 `v` records, 96 unique positions, 156 triangles, 12 radial segments, 1.086522 m diameter × 1.321800 m axial height**. Its three bands are 0.100000 m high and project 0.043261 m from the shell. The generator is 140 triangles with a six-sided, radius-0.25 m motor; the hydrant is 200 triangles with six-sided, radius-0.152490 m fittings. These measurements determined the geometry below.

[FLUID_ART_MEASUREMENTS.md](FLUID_ART_MEASUREMENTS.md) contains the direct OBJ survey, including **one table row for each of 2,010 meshes**. The survey was written before the authoring script created vertices. The additional normal and component UV measurements were made while comparing the first render against the reference meshes; they corrected the initial flat-shaded vessel surfaces.

| Population / measurement | Result |
|---|---|
| 551 catalog primary props, `v` min / median / max | 4 / 196 / 6196 |
| Same props, triangles min / median / max | 2 / 114 / 2973 |
| 63 catalog props with largest dimension 0.75–1.75 m and smallest ≥0.35 m, `v` min / median / max | 40 / 140 / 1114 |
| Same size comparison, triangles min / median / max | 20 / 88 / 552 |
| 566 non-LOD object meshes with paired PNGs, referenced texels min / median / max | 0 / 4 / 387; zero means no referenced UV |
| Most frequent palette dimensions | 2×2: 201; 4×2: 140; 2×1: 83 |
| Barrel normal interpolation | 96 side triangles interpolate radial normals; 60 cap triangles have constant normals |
| Barrel LOD0 → LOD1 | 156 → 20 triangles; 12 → 6 sides; bands removed |
| Standing player capsule / eye | 2.000000 / 1.750000 m |

All **11** device defs were completed: 9110–9117, 9119, 9120, 9121. There is no device def 9118 in this range; `MakeFluid` is a factory. No hose anchor moved. Def-less test containers and municipal fixtures retain their existing rendering paths. The legacy `fluid` console verb constructs those test containers, so it still shows primitives; use the placed item IDs or the new `shot.py` scenes to inspect this art.

## Geometry traced to measurements

Triangle counts below include the moving part once. Preview meshes combine body and moving part for the placement ghost; the placed device loads them separately. `v` counts are welded position records, with separate per-corner normal and UV indices. `ParseObj` expands both old and new meshes to three runtime vertices per triangle, so triangle counts are the comparable rendering budget.

| Device | LOD0 triangles, component accounting | Measurement supporting the allocation and detail |
|---|---|---|
| 9110 Tank | **256** = barrel topology 156 + two socket mouths 80 + lid 20 | Barrel_0's 24-triangle shell and three 44-triangle bands; 12 sides. Each hex mouth adds 36 outer/annulus/inner triangles and four recessed back triangles. Lid uses the generator's six-sided 0.25 m radius. |
| 9111 Water Source | **260** = barrel 156 + shoulder 44 + neck 20 + mouth 40 | Same 12-sided barrel; the 12-sided shoulder uses the measured 0.25 m cap radius. Neck bridges the preserved front port at Z=0.55 to the shell. Green RGBs come from Barrel_1. |
| 9114 Pump | **220** = supports/terminal boards 72 + chamber 28 + mouths 80 + separate motor 40 | Generator_0: 140 total and six-sided motor, radius/length 0.25/0.5 m. Chamber has eight sides, as Propane_0. Added motor end belt is 0.1 m long, taken from barrel band thickness. |
| 9115 Valve | **300** = body/pipe/stem/trigger supports 112 + mouths 80 + separate wheel 108 | Body without wheel is 192 triangles versus hydrant 200. Twelve-sided hollow wheel has four annular/profile strips (96 triangles) plus a 12-triangle spoke. Wheel radius 0.3 m comes from the old functional handle; rim/spoke thickness 0.041160 m comes from the propane stem. |
| 9116 Refinery | **232** = base/heater 24 + retort 44 + projecting band 44 + stack 20 + pipe 20 + mouths 80 | Retort uses barrel's 12 sides and generator/propane radius 0.25; band projects the barrel's 0.043261 m. Stack is the hydrant's six-sided 0.088470 m stem radius. Height 1.75 m preserves the former stack envelope. |
| 9117 Sluice | **188** = base/trestles 36 + floor/walls 36 + three riffles 36 + mouths 80 | 188 triangles versus hydrant 200, including 80 for the fixed mouths. Floor/wall thickness 0.041160 m is the measured stem width; riffle height is barrel projection 0.043261 m. Ends fall from Y=0.7 to 0.5, using the existing Y=0.6 ports ± one 0.1 m band. |
| 9121 Purifier | **268** = base/cabinet 24 + two filter profiles 88 + two clamps 56 + pipe 20 + mouths 80 | Filters use propane's eight sides and the hydrant's 0.152490 m radius. Clamps project the barrel's 0.043261 m. Cabinet reaches the unchanged power anchor (0,1.25,0.42). |
| 9112 Splitter | **224** = base/support 24 + manifold 20 + three branches 60 + three mouths 120 | Hex manifold/pipes/fittings use hydrant's six sides. Removing the three functional mouths leaves 104 triangles, between near-size median 88 and generator 140. Branch positions are exactly the existing port fan at Z=±0.32. |
| 9113 Combiner | **224** = same component counts as splitter | Mirrors the actual input/output fan; same measured hex sections and feature dimensions. |
| 9119 Inlet | **232** = two eight-sided strainer ends 56 + eight ribs 96 + riser/neck 40 + mouth 40 | Eight sides and 0.25 m vessel radius from Propane_0; ribs use 0.041160 m measured stem width. Open gaps are geometry. Riser reaches the unchanged (0,0.7,0.55) outlet. |
| 9120 Drain | **192** = bowl/bottom 76 + three grate bars 36 + riser/neck 40 + mouth 40 | Eight-sided tray follows the small-vessel reference; hollow wall projects 0.043261 m and grate bars use 0.041160 m. Existing Y=0.7/Z=0.55 inlet fixes standpipe height and reach. |

| Shared dimension / rule | Derivation |
|---|---|
| Tank/source outer band radius 0.456739 m, shell radius 0.413478 m | Original 0.5 m footprint radius minus one/two measured 0.043261 m projections. This leaves clearance behind the mouths at the fixed ±0.5 m ports, without moving the anchors or allowing the shell to fill their openings. The narrower drum is deliberate; it is not presented as a copied barrel proportion. |
| Tank/source barrel shell Y=0.05–1.3 | 1.25 m axial span, equal to the measured barrel shell; lower half-band 0.05 m, upper end = 1.4 m old envelope minus 0.1 m lid/shoulder. |
| Tank/source bands Y=0–0.1, 0.6–0.7, 1.2–1.3 | Each 0.1 m thick; middle band ends at the original 0.7 m port elevation; end bands terminate shell/base. |
| Socket outer / bore radius | 0.152490 / 0.088470 m, the hydrant's two measured hex radii; six segments. Recess = barrel projection 0.043261 m; outer depth = band thickness 0.1 m. |
| Skids, pedestals, boards, pipes | Layout follows old 0.9 m fitting envelope, Y=0.6 hose axes, and X=±0.32 / Y=1.2 or 1.25 / Z=±0.42 electrical anchors. These are functional layout constraints, not purported measurements of retail machine internals. |
| Supports/rims/grates | 0.041160 m minimum authored width; 0.1 m bands; no added bolt arrays, text decals, surface scratches, or sub-centimetre bevels. |
| Normal treatment | Radial per-corner normals on eight/twelve-sided curved walls; caps and annuli use explicit face normals. Six-sided fittings, motors, and LOD1 walls use constant face normals. |

## Materials and moving parts

Each device has a **2×2 PNG**, sampled by per-corner UV. The tank/source use the reference barrel assignment: light shell (72 reference corners), dark bands (396 reference corners). All triangles have a single texel-centre UV: OBJ `(0.25,0.75)` becomes `(0.25,0.25)` after `ParseObj` flips V. No `usemtl`, colour-per-vertex, or device-body `AlbedoColor` supplies the art. Materials use nearest filtering, roughness 1, white modulation, and backface culling with clockwise faces and explicit outward normals.

| Device palette | Exact RGB source |
|---|---|
| Tank / Splitter | Barrel_0 (87,104,118), (99,119,135); Generator_0 metal (59,59,59), (138,138,138) |
| Source / Combiner / Inlet | Barrel_1 (87,119,90), (99,135,102); same generator metals |
| Pump | Generator_0 yellow (213,167,44), two metals; Barrel_0 (99,119,135) occupies the unused fourth texel |
| Valve | Barrel_1 green (99,135,102), Fire_Hydrant_0 red (162,32,32), two generator metals |
| Refinery / Sluice / Drain | Barrel_2 (112,68,68), (131,87,87), two generator metals |
| Purifier | Propane_0 white (219,219,219), Barrel_0 (99,119,135), two generator metals |

`PumpDrum` is still `_pumpDrum`, at the original **(0,1.25,0)** pivot. Existing drive-gated vibration and its **0.01 m amplitude** remain intact. Its LOD1 is a child, so both mesh levels inherit the same motion. The collision envelope includes the upper 0.01 m travel; other axes already fit inside the static body bounds.

`ValveHandle` is a separate mesh with a child LOD1. Its pivot is now **(0,1.4,0)**, 0.2 m above the old primitive: two measured band heights clear the unchanged trigger housings at Y=1.2. The previous code only changed colour. Manual toggles and `SetValveOpen` now also rotate the spoke **90° over 0.2 seconds**. The angle follows the flow gate; the duration is a new animation timing choice, not an art measurement. Closing shifts the handle UV by +0.5 U onto the measured red texel; reopening restores green. The per-instance handle material is shared with its own LOD1, without recolouring other valves.

## Placement bounds and LODs

Before this change every def had Size `(1,1.4,1)`, Offset `0.7`, Radius `0.5`, and Upright `false`. The geometry was Y-up but the ghost applied the legacy X stand-up rotation. All authored defs now have **Upright=true, ProcBox=false**. Size and the placed box collider come from the generated catalog. Collider centre = bounds minimum + Size/2; this matters for front-only fittings. Offset is half the collider height. Radius is the minimum of 0.5 m, half-width, half-depth, and Offset−0.041160 m, preserving the clearance-sphere rule while keeping it above the supporting floor.

Hose anchors and the six existing electrical anchors across pump, valve, and purifier did not move. No capacity, rate, head lift, quality conversion, or submerged depth requirement changed. The inlet still requires 0.6–5 m water depth.

The lower meshes use the barrel's **0.20415 screen-height split**, through `LodTable.DistanceForHeight` with source FOV 60° and the existing draw-distance bias. Formula: `max(bounds size)/(2×0.20415×tan(30°))×LodBias`; `LodBias=2+3×clamp(DrawDistance,0,1)`, default 5. Tank split is about 29.695 m at that default. Unlike world props, deployed devices are not culled at the second retail height: LOD1 remains visible, with hose nodes and colliders independent of render distance. Bands, riffles, open strainer ribs and the open wheel are omitted/simplified in LOD1; functional fitting silhouettes remain, explaining the higher totals than a bare 20-triangle barrel LOD.

<!-- generated bounds and asset inventory follow -->

| ID | Welded v (LOD0) | Tris LOD0 / LOD1 | Collider Size XYZ (m) | Bounds minimum XYZ (m) | Offset (m) | Radius (m) | Height / 2 m player |
|---|---:|---:|---|---|---:|---:|---:|
| 9110 | 156 | 256 / 80 | 1.000000, 1.400000, 0.913478 | -0.500000, 0.000000, -0.456739 | 0.700000 | 0.456739 | 0.700000 |
| 9111 | 144 | 260 / 80 | 0.913478, 1.400000, 1.006739 | -0.456739, 0.000000, -0.456739 | 0.700000 | 0.456739 | 0.700000 |
| 9114 | 130 | 220 / 152 | 1.000000, 1.476506, 0.840000 | -0.500000, 0.000000, -0.420000 | 0.738253 | 0.420000 | 0.738253 |
| 9115 | 176 | 300 / 172 | 1.000000, 1.450000, 0.600000 | -0.500000, 0.000000, -0.300000 | 0.725000 | 0.300000 | 0.725000 |
| 9116 | 136 | 232 / 124 | 1.000000, 1.750000, 0.900000 | -0.500000, 0.000000, -0.450000 | 0.875000 | 0.450000 | 0.875000 |
| 9117 | 120 | 188 / 112 | 1.000000, 0.841160, 0.900000 | -0.500000, 0.000000, -0.450000 | 0.420580 | 0.379420 | 0.420580 |
| 9121 | 156 | 268 / 148 | 1.000000, 1.338470, 0.900000 | -0.500000, 0.000000, -0.450000 | 0.669235 | 0.450000 | 0.669235 |
| 9112 | 136 | 224 / 164 | 1.000000, 0.752490, 0.904120 | -0.500000, 0.000000, -0.452060 | 0.376245 | 0.335085 | 0.376245 |
| 9113 | 136 | 224 / 164 | 1.000000, 0.752490, 0.904120 | -0.500000, 0.000000, -0.452060 | 0.376245 | 0.335085 | 0.376245 |
| 9119 | 142 | 232 / 120 | 0.541160, 0.832060, 0.820580 | -0.270580, 0.000000, -0.270580 | 0.416030 | 0.270580 | 0.416030 |
| 9120 | 110 | 192 / 116 | 0.913478, 0.832060, 1.006739 | -0.456739, 0.000000, -0.456739 | 0.416030 | 0.374870 | 0.416030 |

## Every mesh file and its checked AABB

The JSON receipt [FLUID_MESH_BOUNDS.json](FLUID_MESH_BOUNDS.json) records these same bounds and the triangle count for each named component. Part bounds are local to the pivots above. Body/preview bounds are in the placed Y-up frame.

| File under game/content/fluid/ | v | Tris | Min XYZ (m) | Max XYZ (m) |
|---|---:|---:|---|---|
| [9110_body.txt](../game/content/fluid/9110_body.txt) | 156 | 256 | -0.500000, 0.000000, -0.456739 | 0.500000, 1.400000, 0.456739 |
| [9110_body_lod1.txt](../game/content/fluid/9110_body_lod1.txt) | 48 | 80 | -0.500000, 0.000000, -0.395548 | 0.500000, 1.400000, 0.395548 |
| [9111_body.txt](../game/content/fluid/9111_body.txt) | 144 | 260 | -0.456739, 0.000000, -0.456739 | 0.456739, 1.400000, 0.550000 |
| [9111_body_lod1.txt](../game/content/fluid/9111_body_lod1.txt) | 42 | 80 | -0.456739, 0.000000, -0.395548 | 0.456739, 1.400000, 0.550000 |
| [9114_body.txt](../game/content/fluid/9114_body.txt) | 112 | 180 | -0.500000, 0.000000, -0.420000 | 0.500000, 1.338470, 0.420000 |
| [9114_part.txt](../game/content/fluid/9114_part.txt) | 18 | 40 | -0.250000, -0.216506, -0.250000 | 0.250000, 0.216506, 0.250000 |
| [9114_preview.txt](../game/content/fluid/9114_preview.txt) | 130 | 220 | -0.500000, 0.000000, -0.420000 | 0.500000, 1.466506, 0.420000 |
| [9114_body_lod1.txt](../game/content/fluid/9114_body_lod1.txt) | 84 | 132 | -0.500000, 0.000000, -0.420000 | 0.500000, 1.338470, 0.420000 |
| [9114_part_lod1.txt](../game/content/fluid/9114_part_lod1.txt) | 12 | 20 | -0.250000, -0.216506, -0.250000 | 0.250000, 0.216506, 0.250000 |
| [9114_preview_lod1.txt](../game/content/fluid/9114_preview_lod1.txt) | 96 | 152 | -0.500000, 0.000000, -0.420000 | 0.500000, 1.466506, 0.420000 |
| [9115_body.txt](../game/content/fluid/9115_body.txt) | 120 | 192 | -0.500000, 0.000000, -0.250000 | 0.500000, 1.379420, 0.250000 |
| [9115_part.txt](../game/content/fluid/9115_part.txt) | 56 | 108 | -0.300000, -0.050000, -0.300000 | 0.300000, 0.050000, 0.300000 |
| [9115_preview.txt](../game/content/fluid/9115_preview.txt) | 176 | 300 | -0.500000, 0.000000, -0.300000 | 0.500000, 1.450000, 0.300000 |
| [9115_body_lod1.txt](../game/content/fluid/9115_body_lod1.txt) | 96 | 152 | -0.500000, 0.000000, -0.250000 | 0.500000, 1.379420, 0.250000 |
| [9115_part_lod1.txt](../game/content/fluid/9115_part_lod1.txt) | 12 | 20 | -0.300000, -0.050000, -0.259808 | 0.300000, 0.050000, 0.259808 |
| [9115_preview_lod1.txt](../game/content/fluid/9115_preview_lod1.txt) | 108 | 172 | -0.500000, 0.000000, -0.259808 | 0.500000, 1.450000, 0.259808 |
| [9116_body.txt](../game/content/fluid/9116_body.txt) | 136 | 232 | -0.500000, 0.000000, -0.450000 | 0.500000, 1.750000, 0.450000 |
| [9116_body_lod1.txt](../game/content/fluid/9116_body_lod1.txt) | 76 | 124 | -0.500000, 0.000000, -0.450000 | 0.500000, 1.750000, 0.450000 |
| [9117_body.txt](../game/content/fluid/9117_body.txt) | 120 | 188 | -0.500000, 0.000000, -0.450000 | 0.500000, 0.841160, 0.450000 |
| [9117_body_lod1.txt](../game/content/fluid/9117_body_lod1.txt) | 72 | 112 | -0.500000, 0.000000, -0.450000 | 0.500000, 0.841160, 0.450000 |
| [9121_body.txt](../game/content/fluid/9121_body.txt) | 156 | 268 | -0.500000, 0.000000, -0.450000 | 0.500000, 1.338470, 0.450000 |
| [9121_body_lod1.txt](../game/content/fluid/9121_body_lod1.txt) | 88 | 148 | -0.500000, 0.000000, -0.450000 | 0.500000, 1.338470, 0.450000 |
| [9112_body.txt](../game/content/fluid/9112_body.txt) | 136 | 224 | -0.500000, 0.000000, -0.452060 | 0.500000, 0.752490, 0.452060 |
| [9112_body_lod1.txt](../game/content/fluid/9112_body_lod1.txt) | 100 | 164 | -0.500000, 0.000000, -0.452060 | 0.500000, 0.752490, 0.452060 |
| [9113_body.txt](../game/content/fluid/9113_body.txt) | 136 | 224 | -0.500000, 0.000000, -0.452060 | 0.500000, 0.752490, 0.452060 |
| [9113_body_lod1.txt](../game/content/fluid/9113_body_lod1.txt) | 100 | 164 | -0.500000, 0.000000, -0.452060 | 0.500000, 0.752490, 0.452060 |
| [9119_body.txt](../game/content/fluid/9119_body.txt) | 142 | 232 | -0.270580, 0.000000, -0.270580 | 0.270580, 0.832060, 0.550000 |
| [9119_body_lod1.txt](../game/content/fluid/9119_body_lod1.txt) | 58 | 120 | -0.250000, 0.000000, -0.216506 | 0.250000, 0.832060, 0.550000 |
| [9120_body.txt](../game/content/fluid/9120_body.txt) | 110 | 192 | -0.456739, 0.000000, -0.456739 | 0.456739, 0.832060, 0.550000 |
| [9120_body_lod1.txt](../game/content/fluid/9120_body_lod1.txt) | 64 | 116 | -0.456739, 0.000000, -0.395548 | 0.456739, 0.832060, 0.550000 |

## Other files added or changed

| File | Change |
|---|---|
| `game/DeployableDef.cs` | Configure all 11 fluid defs from the authored catalog; shared preview/palette path; upright ghosts. |
| `game/FluidContainer.cs` | Authored branch preserves ports, billboard, `_pumpDrum` drive logic; separate valve handle follows open/closed state. Existing def-less fallback retained. |
| `game/FluidArt.cs` | Runtime catalog, bounds, ParseObj meshes, cached palette materials, separate parts and LOD bindings. |
| `game/Main.cs` | Route `UG_FLUIDART` through the existing `--fluidtest` flag; arm the shared screenshot capture. |
| `game/Main.FluidArt.cs` | Place actual defs in reference gallery, individual close views, lower-LOD view, and wired uphill fluid circuit. |
| `game/testing/tests/FluidArtTests.cs` | Real placement aim/factory, port raycasts, meshes/bounds/materials, every branch flowing, real generator power, pump and valve transform/state checks. |
| `game/content/fluid/catalog.json` | Generated names, colour RGBs, bounds, placement dimensions, pivots and mesh/component counts. |
| `game/content/fluid/9110_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9111_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9114_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9115_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9116_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9117_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9121_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9112_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9113_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9119_palette.png` | 2×2 palette from the RGB sources above. |
| `game/content/fluid/9120_palette.png` | 2×2 palette from the RGB sources above. |
| `tools/measure_fluid_art.py` | Parses old OBJ/TXT files directly; full any-orientation regular-ring edge scan, UV sampling and distribution tables; writes optional raw JSON to `.verify/`. |
| `tools/fluid_art_measurement_details.md` | Numeric component, code-footprint, palette, LOD and normal observations included by the survey script. |
| `tools/author_fluid_art.py` | Measurement-gated deterministic authored mesh/palette/catalog generation. |
| `tools/verify_fluid_art.py` | Reparse every delivered mesh using ParseObj semantics; strict indices/triangles/UV/normal/winding/area/AABB checks and all 21 socket mouths. |
| `tools/shot.py` | Add `fluid`, `fluiddevice`, `fluidlod`, `fluidclosed`, `fluidflow` to SCENES. |
| `notes/FLUID_ART_MEASUREMENTS.md` | Pre-authoring survey and numeric comparison tables; additional normal measurement after first render comparison. |
| `notes/FLUID_MESH_BOUNDS.json` | Per-file bounds/count/component receipt, consumed by validator. |
| `notes/FLUIDIO_REPORT.md` | This report. |
| `notes/fluid_art/9110.png` | Inspected LOD0 render of placed device 9110. |
| `notes/fluid_art/9111.png` | Inspected LOD0 render of placed device 9111. |
| `notes/fluid_art/9114.png` | Inspected LOD0 render of placed device 9114. |
| `notes/fluid_art/9115.png` | Inspected LOD0 render of placed device 9115. |
| `notes/fluid_art/9116.png` | Inspected LOD0 render of placed device 9116. |
| `notes/fluid_art/9117.png` | Inspected LOD0 render of placed device 9117. |
| `notes/fluid_art/9121.png` | Inspected LOD0 render of placed device 9121. |
| `notes/fluid_art/9112.png` | Inspected LOD0 render of placed device 9112. |
| `notes/fluid_art/9113.png` | Inspected LOD0 render of placed device 9113. |
| `notes/fluid_art/9119.png` | Inspected LOD0 render of placed device 9119. |
| `notes/fluid_art/9120.png` | Inspected LOD0 render of placed device 9120. |
| `notes/fluid_art/tank-lod1.png` | Inspected device 9110 beyond its actual LOD split. |
| `notes/fluid_art/9111-lod1.png` | Inspected device 9111 beyond its actual LOD split. |
| `notes/fluid_art/pump-lod1.png` | Inspected device 9114 beyond its actual LOD split. |
| `notes/fluid_art/9115-lod1.png` | Inspected device 9115 beyond its actual LOD split. |
| `notes/fluid_art/9116-lod1.png` | Inspected device 9116 beyond its actual LOD split. |
| `notes/fluid_art/9117-lod1.png` | Inspected device 9117 beyond its actual LOD split. |
| `notes/fluid_art/9121-lod1.png` | Inspected device 9121 beyond its actual LOD split. |
| `notes/fluid_art/9112-lod1.png` | Inspected device 9112 beyond its actual LOD split. |
| `notes/fluid_art/9113-lod1.png` | Inspected device 9113 beyond its actual LOD split. |
| `notes/fluid_art/9119-lod1.png` | Inspected device 9119 beyond its actual LOD split. |
| `notes/fluid_art/9120-lod1.png` | Inspected device 9120 beyond its actual LOD split. |
| `notes/fluid_art/gallery.png` | All 11 placed devices with four measured retail references. |
| `notes/fluid_art/valve-closed.png` | Turned spoke and red handle texel after closing. |
| `notes/fluid_art/flow.png` | Real generator → pump, source → pump → valve → elevated tank. |
| `notes/fluid_art/verification.txt` | Build, mesh validator, in-engine tests, fresh-capture records and asset hashes. |

## Verification and reproduction

| Check | How | Result |
|---|---|---|
| Build | `dotnet build game/UnturnedGodot.csproj` | Final incremental build: success, 0 warnings, 0 errors. Full recompiles emitted 22 warnings in existing code; no new warning diagnostic was introduced. |
| Mesh safety | `python3 tools/verify_fluid_art.py` | 30 files, 4,872 triangles across body/part/preview/LOD files; all indices in range, exactly three corners per face, nonzero triangle area, finite unit normals with outward/clockwise agreement, exact centre UVs after V flip, no cross-cell interpolation, all AABBs match the receipt. |
| Palette role assignment | Validator samples new vessel/band faces through the loader's V flip and compares to the original barrel PNGs | Tank/source shell and band colours match the measured reference assignment. |
| Hose mouths | Validator independently uses the 21 original anchor positions and finds all six bore-rim vertices at every mouth plane | 21 / 21 unchanged anchors have physical fittings. |
| Fluid regression slice | `UG_TEST_RESULTS=.verify/fluid-final ./test.sh --l1 --only 'fluid.*'` | 10 / 10 tests passed, including 147 art-placement/flow checks. |
| Art test after geometry refinement | `UG_TEST_RESULTS=.verify/fluid-art-final ./test.sh --l1 --only 'fluid.art*'` | 147 / 147 passed. Real floor aim and placement for every def, including inlet depth 1 m; yaw 37°; each port ray resolves its own HosePort; every input/output branch moves or consumes fluid. |
| Pump/purifier power | Same art test, real placed generator, Wire and PowerNet; no forced power for those authored devices | Both electrical inlets powered; purifier produces clean water; pump flows, vibrates on successive ticks, and returns to (0,1.25,0) on power loss. |
| Valve | Manual close plus real wired OPEN/CLOSE trigger ports | Intermediate and final wheel angles checked; closed blocks flow, wired reopening resumes flow; green/red UV offsets checked. |
| Inlet and transformations | Same art test | Inlet supplies through a pump; refinery output has gas type; sluice output dirty; purifier output clean; every connected splitter/combiner branch receives fluid. |
| Rendered circuit | `python3 tools/shot.py fluidflow` | `power=True`, `drive=True`, elevated tank received **1250 mL** after twenty 0.1 s FluidNet ticks. Drum observed at **(0.009977427,1.2595209,0.007720116)** versus rest (0,1.25,0). |
| Visual inspection | `shot.py fluid`, every ID through `fluiddevice` and `fluidlod`, plus `fluidclosed` and `fluidflow`; xvfb + Vulkan + movie mode | 25 saved and inspected final frames: gallery 1600×1000; individual views 900×900; circuit 1280×800. Lower views put the camera 10 m beyond the actual configured split; the gallery explicitly forces LOD0 for comparison. |

![Placed fluid devices and measured reference props](fluid_art/gallery.png)

[Pump](fluid_art/9114.png), [open valve](fluid_art/9115.png), [closed valve](fluid_art/valve-closed.png), [powered circuit](fluid_art/flow.png), [tank LOD1](fluid_art/tank-lod1.png). All remaining close views and lower views are listed above.

To regenerate the assets and re-check them, run in this worktree:

```bash
python3 tools/measure_fluid_art.py
python3 tools/author_fluid_art.py
python3 tools/verify_fluid_art.py
dotnet build game/UnturnedGodot.csproj
./test.sh --l1 --only 'fluid.*'
python3 tools/shot.py fluid
DEVICE=9114 python3 tools/shot.py fluiddevice
DEVICE=9114 python3 tools/shot.py fluidlod
python3 tools/shot.py fluidclosed
python3 tools/shot.py fluidflow
```

## Limits of verification

- I did not manually operate the inventory, mouse placement, hose tool, or F key in a live play session. The tests call the real aim/placement factory, physics port rays, manual-toggle method, electrical signal graph and fluid solver. They verify transforms over ticks; the delivered visual evidence is still frames, not an animation video.
- I did not render these in the full island world, underwater, at night, in multiplayer, or on a hardware GPU. Inlet placement depth was tested numerically through the real aim path; its dry studio view exposes the strainer geometry. Captures used the repository's llvmpipe/Vulkan path.
- The renderer produced fresh, nonblank PNGs, then logged the engine's `finalize can only be called from the render thread` shutdown error. One retained capture log also contains `p_image.is_null() || p_image->is_empty()`; its final viewport PNG was saved and inspected. Engine runs emitted ObjectDB/RID cleanup warnings as well. These are recorded in the evidence; I did not establish or fix their underlying cause and do not claim a clean Godot shutdown. The C# build succeeds.
- The ring scan is exhaustive over the listed mesh files for regular closed 5–64-sided edge rings, including tilted rings. It does not classify partial arcs, ellipses, deformed organic sections, or four-sided prisms as rings; `0 detected` in the measurements is not proof that a mesh has no curved surface.
- The complete non-fluid game test suite was not run. No multiplayer deployment or push was performed.
