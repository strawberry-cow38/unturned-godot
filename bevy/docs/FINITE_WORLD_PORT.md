# Finite islands and generated buildings

This work is distinct from the existing `Procedural`/InfiniteTerrain path.
The source contains three separate systems:

- `InfiniteWorld`/`InfiniteTerrain`: endless 256 m regions, already consumed by
  native streaming and floating origin.
- `Main.BuildEditorNew`/`Terrain.GenerateIsland`/`ProcIsland`: a finite editor map,
  normally three by three 1024 m tiles. Its towns place original prefab buildings.
- Core `BuildingGen`: room, wall, roof, stair and furniture plans, materialized
  through `EditorBuildings`. It does not replace finite-island town prefabs.

The finite path currently has no native generation or saved-editor-map consumer.
The native retail loader and endless generator cannot be treated as substitutes.
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
