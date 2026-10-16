# Original generated buildings

The finite island's existing prefab towns and `BuildingGen` are separate source
paths. This work preserves that distinction. No generated wall replaces a town
prefab, and no native building renderer or editor manipulation UI is connected
by the current data checkpoint.

## Source data boundary

`core/UnturnedSim/BuildingGen*`, `WallSave`, `WallOpenings`, `RoofShapes` and
`ConvexHull` already contain the original engine-independent rules. The bridge
uses those classes directly. `extract_buildings.py` retains the original
`game/WallSurface.cs`, `WallMaterials.cs` and selected `EditorBuildings.cs`
methods; it changes resource allocation and UI/history side effects into value
outputs, rather than replacing the generator or mesh algorithm.

`BuildingSurface.Build` returns the original front, back and trim triangle
streams and box/convex collision shapes. Opening partition, overlapping holes,
gables, tapered surfaces, plumb roof edges, palette roles and back-face colors
keep their source arithmetic and order. Each call creates fresh outputs, so a
fully holed surface cannot retain an old collider. The source triangle winding
is clockwise; a native consumer must reverse it once for Bevy. Raw flat normals
are derived before any engine mesh storage compression.
Source wall solids use collision layer bit 0 and trim uses bit 6; the two output
arrays intentionally remain separate so native query masks can preserve that
distinction instead of making every frame a terrain wall.

`BuildingScene.Generate` runs the existing generator, assigns its source
palette, places walls and stair treads, creates invisible ramps, cuts floor
openings, solves corners and translates room/HVAC/electrical/door/furniture
metadata in the original order. Its coordinates retain the source editor stage
origin `(0, 2000, 0)` and `0.25m` clearance. The separate original bake path
subtracts the stage origin; generation must not silently apply that bake offset.

`SaveText` and `LoadText` use unchanged `WallSave` bytes. Empty layouts remain
explicitly writable. Source saves contain visible stair treads but do not record
the invisible generated ramps. Reopening into a fresh scene therefore has no
ramps; this adapter does not invent them. The original load method also does not
clear pre-existing ramps in an already populated editor instance. File routing,
native materialization, interactive editor history and bake are later consumers.

Palette caches are scoped to a content root. The additional Godot value methods
are exact signed modulo, floor, YXZ `Basis.FromEuler` and `Basis.Inverse`; they do
not change the existing Unity compatibility namespace. Retail object Euler
conversion is deliberately separate: it includes a heading conversion that is
not part of an authored wall transform.

## Verification

Two reproducible engine oracles execute original methods with actual Godot
4.6.2 nodes, `SurfaceTool`, materials, shapes and transforms:

- `bevy/tools/building_surface_reference.py`: 12 cases compare every original
  front/back/trim vertex and uncompressed flat normal, material color, collision
  box and convex point. A source `SurfaceTool.CommitToArrays` observation after
  each nonempty mesh commit avoids treating ArrayMesh normal packing as a math
  discrepancy. The oracle omits only independent door/glass/barricade scene
  allocation.
- `bevy/tools/building_scene_reference.py`: eight original editor assemblies
  contain 493 surfaces and eight ramps across residential, commercial,
  industrial, miscellaneous and apartment seeds. Complete `WallSave` text,
  plan fields/openings, palette and ramp bases match. Actual source node
  rotation can report equivalent wrapped Euler angles, so ramps are compared
  through their engine basis, not the spelling of an angle.
- Six focused managed tests pass on the immutable
  `.local/building-scene-tests-final` candidate. They also cover rebuilding an
  all-hole surface, independent palette roots, signed modulo, singular inverse,
  metadata/save/reopen/empty layout, upper-storey exclusion and the source's
  partial-overlap rejection when cutting a stair hole.

The numerical reference uses Godot's headless mode because it does not claim a
rendered image. Actual native Vulkan rendering and peer-opened images are still
required for the runtime presentation slice.

## Remaining runtime work

- Native wall/trim/back materials, exact visible and physics meshes, identity,
  retirement and save/session/origin lifecycle.
- Original opening placement: measured door leaf offsets and action modes,
  glass (including original raw opening dimensions), shards and barricades.
- Original generated furniture and runtime decoration, room services, stairs
  and source metadata bindings; no client-side shared state or invented damage.
- Reachable generator/editor integration, authored files, source bake, editor
  manipulation/history and finite-map menu/remote content handoff.

These remain active work, not completion claims for the building generator.
