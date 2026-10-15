# Finite islands and generated buildings

This work is distinct from the existing `Procedural`/InfiniteTerrain path.
The source contains three separate systems:

- `InfiniteWorld`/`InfiniteTerrain`: endless 256 m regions, already consumed by
  native streaming and floating origin.
- `Main.BuildEditorNew`/`Terrain.GenerateIsland`/`ProcIsland`: a finite editor map,
  normally three by three 1024 m tiles. Its towns place original prefab buildings.
- Core `BuildingGen`: room, wall, roof, stair and furniture plans, materialized
  through `EditorBuildings`. It does not replace finite-island town prefabs.

The finite path now has a bounded local native CLI entry for original generation
and saved-editor-map reopening. Retail and endless generation remain separate.
The authored generator data, terrain/road/prop/scatter rendering and collision
are linked; editor UI, generated populations and BuildingGen runtime are pending.
The original finite sequence is height/POIs, links and snapped gates, monument
tiles and prefab placement, exact town flattening, routes/junctions, trails/camps,
landmarks, followed by `ProcIslandSpawn` scene/road conformity, ground painting,
player spawns, foliage/resources and generated chart. Road conformity changes
heights after the initial layout; scattering before that step reproduces the
source's previously fixed generation-versus-reload mismatch.

Persistence is the original editor map family, not merely a seed: normalized
heightmap plus splat/holes, objects, road paths, player spawns, optional building
wall plans and a separate `_proc.txt` seed marker. Reopening loads those authored
outputs and must preserve edits. World/session gameplay saves remain separate.

Planned checkpoints, with retail and Infinite coverage retained throughout:

1. Compile unchanged `ProcIsland` and extract the exact `Terrain.GenerateIsland`
   orchestration into an engine-free layout holder. Compare output against the
   original implementation using real GodotSharp math, including repeated seeds,
   rectangular grids and optional lakes. This intermediate stage is not playable.
2. Port the remaining original road conformity/paint/placement and foliage/spawn
   consumers, preserving source object names, axes, materials and identifiers.
   Read/write the existing finite editor-map formats and verify reopen identity.
3. Add native finite-world terrain, ocean, roads and object consumers with
   bounded streaming/collision and source chart. Coordinate bootstrap, world
   configuration and menu/CLI entry only after the data boundary is complete.
4. Materialize the separate existing `BuildingGen`/`WallSave` results using shared
   `WallOpenings`, `RoofShapes` and source wall/furniture runtime geometry. Preserve
   openings, stair collision, original doors/glass and save metadata.
5. Wire generated gameplay fixtures through existing host-owned world services;
   clients consume accepted state. Verify authority/replica/saves and inspect
   actual Vulkan scenes against original source captures before marking parity.

The first adapter compiles `game/ProcIsland.cs` unchanged and extracts
`Terrain.GenerateIsland` with only its final scene rebuild removed. It is an
intermediate layout: no new world mode or UI path is linked, and runtime parity
is not claimed yet. `FiniteIslandTests` checks complete normalized height grids
and all road/building/landmark records against source-derived numerical fixtures,
plus reuse after another seed and bounded dimensions. These are data comparisons,
not pixel goldens.

`verify_island_source.py` builds the same original generator against real
GodotSharp and against an immutable bridge DLL, then compares every serialized
byte. The 1×1 seed1234, rectangular 2×1 seed9876 with lakes, 3×3 seed771177 and
repeated seed1234 cases first matched every height byte but exposed an existing
bridge math error: `180f / MathF.PI` yields a different conversion constant from
Godot's decimal-rounded constant. It turned exact source landmark yaw90 into
89.99999. The Godot adapter now uses the original constant, guarded directly;
Unity's separate compatibility math is unchanged. Source reference:
[Godot4.6 Mathf](https://github.com/godotengine/godot/blob/4.6-stable/modules/mono/glue/GodotSharp/GodotSharp/Core/Mathf.cs).

Verification: the corrected immutable `finite-island-layout-v2` matches all eight
original height/layout files byte for byte. An additional real-Godot differential
matches `IsEqualApprox` at zero, exact epsilon, below/above epsilon and scaled
positive/negative100 boundaries; equal/reversed/negative `SmoothStep` edges; angle
conversion result bits; signed zero and NaN sign behavior. The shared managed run
passed29 cases including finite layout8, Crafting and existing NPC/occupant/light
tests; the added epsilon regression is also run separately against that same DLL.
This stage does not instantiate meshes, so Vulkan verification belongs to the
subsequent native materialization checkpoint.

The next consumer must retain the actual `RoadField` Catmull-Rom tangents,
five-metre curve sampling and source joint-profile smoothing. The generated
curves feed `Terrain.ConformToPolylines`, whose town-pad exclusion, nearest-segment
claim and different-road ceiling affect final height and collision. Existing
native `world::roads` already reads the original saved Paths.dat format and can
reuse these exported joints. Foliage remains after this conformity and all paint.

The second data stage now retains the original RoadField joint/tangent/5m-walk
and Paths.dat writer, Terrain conformity and winner-take-all paint, and original
ProcIslandSpawn object/groundwork/player/zombie/loot/animal consumers.
`FiniteIslandGeneration.Generate` keeps the source static scratch and the whole
dependent sequence under one lock. Its order is towns and boulders; shoreline,
steep and worked-ground paint; player spawns and town furniture; spline conformity;
roadside objects, trails, camps and landmarks; gameplay spawn points. The original
layout remains inspectable separately from the conformed grid and filtered route
list. No authority state is created by this data-stage call.

EditorObjects.Place is replaced by a value sink retaining the original object
name, material variant and full basis; missing original OBJ art remains missing.
The source's standing correction, world/proc Z flip, tilted boulder and fence
bases and world scaling are preserved. The small Basis value adapter retains
Godot4.6's arithmetic order and license; the differential reference uses actual
GodotSharp Basis. Only scene creation, texture upload, editor journaling and
explicitly listed diagnostic reports are removed from extracted methods. Fresh
finite generation has no authored river overlay; saved-map river loading is a
separate remaining requirement.

`verify_island_source.py --paths-environment <PEI/Environment>
--placements-content <game/content>` matches all25 output files byte for byte
against real GodotSharp: complete original layout, final height/splat grids,
original Paths.dat, sampled/debug curves, every placed prop basis/material and
all generated spawn records. Cases are 1×1 seed1234, 2×1 seed9876 with lakes,
3×3 seed771177, then repeated1234. They place759,794,2355,759 objects with zero
missing art. The default3×3 case includes104 prefab buildings,24 player spawns,
1971 zombie points,378 loot points and297 animal points. These are source counts,
not replacement density targets. Foliage/resources, source chart, original
save/reopen, native world entry/streaming and BuildingGen runtime are still pending.

Five consumer regressions pass against immutable `finite-island-consumers-first`:
three complete source-output cases, original basis handedness/world scaling and
missing-art behavior. The tiny `pei-road-materials.dat` fixture is the original
PEI Environment/Roads.dat metadata (172bytes); full retail map installation is
not required for these tests. Runtime-art presence still uses the project content.

The original foliage/resource bakes are now extracted as `FiniteIslandScatter`.
They run after final paint/conform, keep the exact source random streams and
acceptance loops, and write the original 12-float foliage and 9-float resource
formats into a caller-owned output directory. Art and manifest files are copied;
retail instance bins are never copied or modified. Resource manifest row order
is preserved verbatim because it defines the authority registry index space.

The extended `--scatter` differential passes 149 files across the same four cases.
The 1×1 case has 56,117 foliage instances and 501 resources in 31 nonempty type files;
the 3×3 case has 575,438 foliage instances and 5,076 resources. The regression compares
every small-island binary against source-derived count/length/SHA fixtures, checks
unchanged input bins and exact manifest bytes, then paints all ground dirt and
rebakes the same seed: no stale grass or resource instance survives. It passes
against immutable `finite-island-scatter-tests`. These are generated content data
checks; native field materialization/render verification remains pending.

Saved-map data now uses the original EditorObjects prop writer/reader and
RoadField Paths.dat reader, including version 2–6 tangents, modes, offsets,
loop flags and GUID bytes. Props preserve first GUID aliases, source YXZ Euler
decomposition, signed scale and the eleventh material token. The original
three-decimal file quantization remains intentional; reopen consumes those
serialized values rather than reconstructing a seed. A failed road parse leaves
the prior data/version intact before the caller chooses how to report failure.

Terrain persistence now retains the original normalized grid, RLE splat,
bit-packed holes and v2/v3 river-recipe formats. Missing or incompatible hole
sidecars keep the source reset behavior, and saving filled ground deletes stale
hole files. River curves and analytical cuts run unchanged, including v2 chaining,
overlap maximum and repeated-load idempotence. Original SampleHeight now includes
that cut; fresh generation still has no rivers. Scene rebuild, river-water meshes
and editor journals remain native-consumer work, not hidden no-op methods.

`verify_island_source.py --saved-data` with the existing paths/content options
passes all 78 output files using actual GodotSharp. This covers four full map
cases, 144 Basis cases (gimbals, pure rotations, nonuniform and mirrored scale),
all prop text/reopened bases, exact roads, grid/splat/hole/river save/re-save bytes,
and 81 river height/normal samples per map. Eleven focused regressions pass,
including zero-scale/material/alias parsing, all five road versions, truncated
road rollback, the 65,535-cell RLE boundary, stale holes, malformed masks, legacy
river chaining and repeated reload. The earlier 15 finite tests also pass.

This is still a data boundary. The native finite world, chart, authored spawn
files, junction graph and custom-placeable sidecars, generated vehicle chooser,
and BuildingGen runtime remain unfinished. The original source also has a reopen
limit: it restores the seed marker and authored files without reconstructing
IslandTiles/Routes. EditorPlayMode's generated zombie/loot/fauna/vehicle branch
then does not run. That source behavior must be handled explicitly during the
upcoming host-session integration; this adapter does not invent saved entities
or silently regenerate over edited terrain. Existing baked charts must be reused
on reopen, as ProcIslandMap.Rebind requires.

The original chart data stage now extracts ProcIslandMap's complete raster,
road-point and town-pad loops, LayerColor table, and unique seeded town labels.
It returns RGB8 pixels, label TSV, original filenames and the square frame.
Godot's RGB8 conversion multiplies float channels by double `255.0`, clamps and
truncates, without a gamma transform; the bridge retains that exact conversion.
References: [Godot Image](https://github.com/godotengine/godot/blob/4.6-stable/core/io/image.cpp)
and [Color.Lerp](https://github.com/godotengine/godot/blob/4.6-stable/modules/mono/glue/GodotSharp/GodotSharp/Core/Color.cs).

The optional `verify_island_source.py --chart --chart-engine <Godot>` path runs
the source in an actual Vulkan Godot process using native Image.SetPixel. All37
outputs match the immutable `finite-island-chart-tests` bridge, including every
RGB8 byte, label and frame across three distinct layouts and a repeated seed.
Four tests cover those full original outputs, same-seed repaint invalidation and
Rebind's existing-image/optional-label behavior. Rebind derives only the frame
from saved bounds; it never redraws missing roads/towns or overwrites saved art.

Opened original PNGs `godot-finite-chart-1234.png`,
`godot-finite-chart-9876-rectangular.png` and `godot-finite-chart-771177.png` under
`bevy/artifacts/`. Root independently opened all three and passed the coast,
relief, original point-road/town overlays and square framing. These verify the
source raster data; native PNG persistence, Information projection and finite
world rendering remain the following integration stage, not a completed claim.

Original `Terrain.RebuildChunk` array construction is now extracted through its
mesh/collider handoff. It retains 48-cell chunks, source edge normals and UVs,
individual quad holes, and four subdivisions per river quad evaluated from the
analytic surface. The native decoder reverses clockwise triangles once. A fully
dug chunk has an empty index array and must retire its visual and collider.

The extended `--meshes` source differential passes all 94 files, including four
complete chunks per map with edge/river/hole cases. Four focused mesh regressions
pass for shared edges, single-quad removal, complete chunk removal/refill, and
river subdivision/reload. The immutable `finite-island-files-final` candidate
also passes two file-handoff tests: source generation/save/edit/reopen preserves
all authored bytes, while invalid names/dimensions, existing destinations and
escaping scatter paths are rejected.

`FiniteIslandFiles` writes original editor terrain, props, Paths.dat, player
translator and proc seed marker into a new directory, then atomically publishes
that directory. Its small `finite.json` descriptor names the data and shared-art
boundary. Reopening consumes saved files without rebuilding generation-only town
or population lists. Original RGB8 chart bytes remain available for the native
PNG encoder; the chart must be persisted before calling original Rebind.

Disposable `.native` chunks use explicit versioned binary arrays. The surface
transport keeps normalized base heights and original river polylines, rather
than baking river cuts into a coarse interpolation. Each cache file is written
to a unique temporary file and atomically replaced, so concurrent openers never
observe a truncated writer. Native descriptor/decoder/streaming/authority and
rendered finite-world validation are still in progress.


## Native local entry and authored-map reopening

`--new-island DIR --island-name NAME --island-tiles 1x1 --seed 1234`
now runs the original generator through `finiteMap`, persists the original
editor files, and starts the normal native player/local authority session.
`--island DIR` reopens those authored files. The default is the source 3×3 grid;
this entry accepts 1–3 tiles per axis inside the existing shared coordinate
range. Generation requires the original PEI Environment/Roads.dat through the
retail install path. Ambiguous Infinite/finite arguments, remote sessions and
the core-only headless entry fail explicitly. UDP descriptor negotiation and
menu/editor entry remain separate work.

The runtime consumes the extracted source 48-cell chunks with exact quad holes,
river subdivisions, normals and UVs. Coarse metadata supports layer sampling
and shared services; rendering, Rapier and the authority triangle upload use the
exact chunk meshes. The analytic river sampler remains separate from coarse
vertex interpolation. Streaming retires old chunk bodies, including completely
dug chunks, while retaining the original spawn/focus. Source negative-Z splat
rows and source prop bases/materials are preserved. Finite editor props use the
original mesh collision and do not inherit retail holiday or ladder-box rules.
Reopen invalidates only the finite directory's cached placement/scatter data.

The source EditorObjects reader now supplies an atomic disposable props handoff,
so malformed rows, first GUID aliases and zero-scale repair are resolved once
by the original parser. Fresh generation retains its original unquantized live
bases; reopening intentionally uses the source three-decimal saved transforms.
Parsed maps are request-local and are not retained across sessions. Original
RGB chart bytes are encoded into a PNG without a color transform; an existing
PNG is kept unchanged for the source Rebind behavior.

Verification on `native-finite-world-tests-v3`: four focused finite regressions
and all 129 world tests pass (two opt-in render fixtures excluded). The focused
cases compare source fractional river samples, reject malformed/truncated mesh
transports, use actual Rapier rays through original chunks and authored holes,
retire/refill/stream those bodies, reload edited props and preserve chart pixel
bytes. Six CLI cases pass. Six managed file/mesh cases pass after the unused
map-retention removal; source prop transport is checked against every live basis
and the original malformed-row/scale behavior.

Actual Vulkan `native-finite-main-v1` generated a 1×1 seed1234 map, entered the
normal player session and uploaded 84,575 authority triangles. Opened
`bevy-finite-play-initial.png`, `bevy-finite-play-walk.png` and
`bevy-finite-reopened.png` show the original road, terrain, trees and ground
cover, with 215 streamed props, 12,848 foliage instances and zero missing assets.
The real W input moves accepted X299→301.4299 on the road, HP100 and correction
version1 unchanged. Pause→Save world records the accepted owner position;
Pause→Quit exits cleanly. A separate `--island` process reopens the map and exits
cleanly after capture. All 244 authored files remain byte-identical, including
the chart PNG. Root independently opened all three views and reviewed the
source/streaming boundary. These are inspected scenes and source-data checks,
not pixel goldens. The full gameplay save is separate from the authored map;
the reopen capture starts a new owner rather than claiming a Load-world journey.

Remaining finite scope includes source spawn/respawn pool selection (the current
entry chooses a nearby authored spawn and grounds it), generated loot/fauna/
zombie/vehicle population and resource/fixture authority registration, original
editor interactions and custom sidecars, river-water/wet-cell ocean geometry,
Information chart projection and menu entry, and remote descriptor negotiation.
Existing generator town/route metadata is intentionally not reconstructed on
saved-map reopen. Separate BuildingGen plans still need their original
EditorBuildings geometry, openings, stairs, furniture, interaction and saves.
The playable terrain entry is a verified intermediate milestone, not complete
finite-world or building-generator parity.
