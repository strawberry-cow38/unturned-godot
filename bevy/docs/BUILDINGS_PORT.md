# Original generated buildings

The finite island's existing prefab towns and `BuildingGen` are separate source
paths. This work preserves that distinction. No generated wall replaces a town
prefab. A read-only native stage consumer now materializes the original wall
surfaces, fitted doors, panes and ramps; normal generator/editor entry and
manipulation remain pending.

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
clear pre-existing ramps in an already populated editor instance. Installed-world
file routing, interactive editor history and bake are later consumers.

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

The numerical reference uses Godot's headless mode. Its optional `--render-dir`
instead uses Vulkan/movie mode and actual original wall nodes for a visual
comparison, including the unchanged `EditorBuildings.HideAbove(3)` method.

## Native surface stage

`buildingView` is a read-only pre-session projection of either a source generator
spec or original `WallSave` text. It returns wall indices, full bases, front/back/
trim triangles and colors, separate source collision arrays, ramps and original
room/opening/furniture metadata. It neither registers authority entities nor
writes files. A managed regression confirms the accepted world snapshot is
unchanged by reading it.

`world::buildings::replace` validates the whole projection and prebuilds convex
compounds before retiring the previous stage. It retains the original Y=2000
coordinates, reverses clockwise triangles once for Bevy, derives flat normals,
and uses original uniform colors and roughness. Empty/all-holed replacements
retire old bodies. Original invisible ramp boxes are separate from stair tread
meshes, and a fresh saved-text reopen does not recreate omitted ramps.

Source wall solids use world bit 0. Trim uses source bit 6, represented by native
GROUP4 plus the reserved GROUP17 marker and player filter GROUP2. Actual source
player movement/headroom includes trim, despite an older nearby comment claiming
otherwise; zombie/world/vehicle wheel queries exclude it. Throw clearance uses
the marker to retain source bit 6 while excluding ordinary vehicle HitMesh and
pane geometry. The future authority geometry export must likewise distinguish
source-world bit 0 from bit 6; this preview does not upload host collision.

Verification on `.local/building-view-tests-final` and
`.local/native-building-visual-first`:

- Seven managed building tests pass, including the read-only projection.
- Three native actual-bridge/Rapier tests plus one throw-mask regression pass:
  a real capsule crosses the original door aperture, trim masks distinguish
  player/zombie/vehicle queries, invalid replacement retains the stage, an
  all-hole replacement removes collision, every generated triangle has the
  converted winding/normal, and real ramp bases/save-reopen match source.
- Original Godot and native Vulkan full/cutaway captures both exit 0 and were
  opened. `artifacts/{godot,bevy}-building-{full,cutaway}.png` show the same roof,
  apertures, partitions and stair treads. Cutaway hides 27 of 74 original walls
  while retaining stairs; the stage contains two invisible ramps. The isolated
  native lighting has darker ambient/shadow fill, so these are surface geometry
  comparisons, not a whole-scene lighting parity claim. There are no pixel
  golden assertions. All eight numerical scene outputs remain unchanged after
  adding the optional renderer.

## Remaining runtime work

- Normal generator/editor and map placement entry, authority geometry export,
  save/session/origin lifecycle for an installed building. The validated stage
  consumer is currently exercised by its actual bridge/render fixtures.
- Installed opening authority, endpoint occupancy retry, sound, damage, shards,
  static broken-glass remnants and barricades. The fitted stage preview below
  is separate from those runtime services.
- Original generated furniture and runtime decoration, room services, stairs
  and source metadata bindings; no client-side shared state or invented damage.
- Reachable generator/editor integration, authored files, source bake, editor
  manipulation/history and finite-map menu/remote content handoff.

These remain active work, not completion claims for the building generator.

## Fitted opening preview

`BuildingOpenings` extracts the original `DoorDeploy.SpawnProp`, wooden-door
catalog fallback and `WallSurface.PlaceDoor` into values. It keeps each opening
index, original mesh, fitted mesh scale and pivot, 270-degree stand-up, full wall
basis, measured horizontal/foot alignment, signed angle, duration, easing curves,
sound and collider bounds. Scale is baked into mesh vertices and inverse-scaled
normals; no ancestor above the hinge receives nonuniform scale. Pocket leaves
slide by their fitted width toward the pivot, using the original smoothstep and
minimum collider thickness. Double-acting leaves preserve the reverse direction.

Original source limitations are retained: the unsplit wooden double-door mesh is
refused; `WallSurface.RebuildDoors` never applies the saved `DoorOpen` token.
Newly built leaves therefore start closed. Container leaves use their original
catalog pivots rather than the wooden path's center rebasing.

Glass uses raw opening dimensions, not trim-clear dimensions, with the source
0.04m thickness, default hue/alpha, HP clamp, indestructible flag and interior-wall
coverage convention. Broken or unglazed openings create no intact pane. The
largest-face rain axis includes original `Mesh.GetFaces` coordinate snapping:
0.03999m and 0.04m widths tie after the engine's 0.0001 snap. Native windows and
vehicle glass share the same shader/material registration and transmission copy;
vehicle shelter polling and wreck behavior remain in the vehicle plugin.

The native stage preloads all door meshes before retiring its old root, then
creates fitted leaves and panes under the source wall/opening hierarchy. Both use
the original bit6 marker, distinct from wall bit0. The explicit preview clock
reuses the shared ObjectDoor hinge transform and disables leaf collision while
moving. It creates no gameplay IDs, commands, sounds, damage or shared-state
mutation. Installed authority, occupied-volume endpoint retry, break effects,
static broken-glass remnants and barricade fills are still pending. The stage
clock is not presented as a complete interactive ObjectDoor implementation.

Source evidence: `building_openings_reference.py` executes original DoorDeploy,
ObjectDoor geometry/ApplySwing and WallSurface.PlaceDoor on actual Godot nodes.
Its 19 cases include swing/double/pocket, all hinge/front combinations, tilted
walls at stage altitude, raised openings and one-/two-leaf container props. Eleven
actual Godot BoxMesh/GlassPane cases guard dimensions, tint, HP, snapped
largest-face ties and the source zero-axis fallback for a tiny positive pane.
Eleven managed building tests pass on the private immutable
`.local/building-openings-final-v2` candidate. Six native building tests pass on
`.local/native-building-openings-final`, including actual Rapier leaf transforms,
endpoint/mid-swing collision, glass query masks, saved broken-pane retirement,
and accepting only the exact source zero axis or a unit rain axis. The existing
vehicle RainGlass group (four tests) and ObjectDoor group (nine tests) also pass
after extracting their shared material and pivot helpers.

The paired original Godot and native Vulkan opening rigs both exit 0. All eight
`artifacts/{godot,bevy}-building-openings-{closed,half,open,wet}.png` images were
opened: the two swinging leaves and pocket leaf retain matching positions and
orientations, and the checkerboard-backed pane receives the original rain shader.
The native fixture requires a dry-to-wet pixel change, catching a missing material
pipeline without pixel goldens. Original renderer output preserves all 19 door
and 11 pane numerical oracle rows exactly. Native JSON records actual leaf poses,
moving-collider disable flags and `authorityInstalled:false`. As in the surface
fixture, native isolated shadowed faces/ground have darker ambient fill; these
images verify opening geometry/motion and the shader path, not full lighting
parity or installed gameplay interaction.
