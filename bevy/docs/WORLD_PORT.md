# World and rendering migration

The Bevy host reads the existing content at runtime. It does not duplicate the
546 MB asset tree or depend on Godot's import cache. The formats below are the
original source formats and remain the content replacement seam.

## Implemented readers and renderers

- `content.rs`: Wavefront `.obj` and OBJ-like `.txt`, independent corner indices,
  negative indices, polygon triangulation, vertex RGB, authored normals, flipped
  Wavefront V coordinates, and shared mesh/material caches. Triangle winding is
  normalized to Bevy's counter-clockwise convention using authored normals. Mesh
  coordinates themselves are retained. JSON rig UVs are already converted and
  are deliberately not flipped a second time. Original pebble meshes without a
  UV channel retain the source's zero-UV fallback. Terrain, resource and foliage
  textures receive runtime mip chains and linear filtering; palette assets keep
  their nearest filtering.
- `world.rs`: GUID mappings and PEI/Washington/Yukon object placements; original
  placement orientation `Y(180-ey) * X(ex) * Z(-ez)` and Z-negated positions;
  original map heightmaps and all eight splatmap weights; original resource and
  foliage instance formats. Foliage v1 and the editor's v2 manual-placement flag
  are understood. Real objects, trees, grass, flowers and pebbles use their
  original meshes and textures.
- Retail terrain is 257×257 big-endian u16 samples per tile, 1024 metres per tile,
  4 metres between samples, and `raw/65535*2048-1024` metres in height. The source
  axes are transposed into increasing world X and reflected world Z. Collision
  uses the same generated triangles as rendering. Sampling keeps the source
  bilinear expression. Out-of-range samples return `None`, not zero metres.
- Terrain and solid prop meshes receive fixed Rapier colliders. The original
  explicit pass-through vegetation list is retained. Tree cylinders use the
  original species radii, height, offset and half-metre visual sink. Ladders use
  the original solid bounding box rather than a climb ray slipping through rungs.
  Object source indices retain raw placement line numbers; resource instances
  retain their name and original binary row before distance culling.
- The player starts at an actual non-alternate `Spawns/Players.dat` location,
  excluding the source's origin placeholder rows. The deterministic initial
  choice is the authored spawn nearest the selected town focus. Terrain controls
  its ground height. The town focus is separate from gameplay spawn, so a menu
  camera does not place the player inside the busiest building.
- The legacy ocean uses the map's own Lighting.dat sea level. The 0.99+ sentinel
  suppresses it (Yukon has water volumes instead). Its current Bevy material is a
  static transparent surface; this is not yet the source ocean shader.
- The original `InfiniteTerrain.Generate` output can be rendered through the
  engine-independent C# bridge's JSON export: row-major heights, authored normals,
  layers, holes and all placement metadata. Moving regions request the original
  generator, retain a small warm cache and unload retired meshes/colliders.
  Generated trees, ground cover, boulders, roads, junction paint, poles, pylons,
  bridge kit, rail kit and signs use the source geometry, placement bases and
  textures. Power conductors preserve measured insulator anchors, paired pylon
  endpoints, 3.5% sag, fourteen samples and nine-centimetre tube width. Optional
  retail editor wire saves resolve endpoints against original pole origins.
  No substitute procedural terrain formula is introduced.
- Generated tunnels sweep the original kit's measured arch contours along the
  source centerline frames, including twin highway bores, portal headwalls,
  floors and replacement portal terrain. The bridge exports its source tunnel
  profiles and dimensions, avoiding a second set of hard-coded bore formulas.
  These surfaces supply the same meshes to rendering and fixed collision.
- Generated bridge decks retain the source roadway plane and grade, replace
  their baked markings with the owning road texture, and continue its measured
  dash phase through the span. Rail decks use the source gravel bed. Only the
  old roadway drawing faces are removed; the kit collider remains complete.
- `world/terrain.wesl` selects the winning material per pixel from bilinear splat
  weights, including the source 0.75-texel noise warp and sixteen-metre albedo
  repeat. Retail tiles share a merged weight texture; generated regions sample
  their vertex-centered one-hot weights. The original mixed regional texture
  array, mipmaps, grass-only snow film and base wetness response are retained.
- Ground cover uses the original upward fragment normals, alpha cutoff and
  distance fade in a Bevy material extension. This keeps double-sided grass
  from developing black backfaces. Source wind displacement remains separate.
- Original `UG_INF_AT=x,z[,yaw]` and `UG_INF_CAM=x,y,z[,yaw[,pitch]]` diagnostics
  support repeatable generated-world captures. A fixed camera also selects the
  region streaming focus, including distant tunnels and other source landmarks.
- `rig.rs`: original human and animal skeletons, inverse bind matrices, joint
  weights, body/arms meshes and original keyed animation clips. The human rig has
  17 bones and 711 clips. Animation supports looping, non-looping holds, masked
  overlays and item-relative animated-part bindings. Root ownership includes
  all bones and the rendered skin, allowing ordinary ECS lifecycle cleanup.
- `viewmodel.rs`: original first-person arms, camera-local placement and separate
  60-degree overlay camera; held models follow the source hand hook with its
  90-degree local Z rotation. Guns, default iron sights and magazines resolve
  through `guns_visual.tsv`, `sights.tsv` and `guns_maghook.tsv`. Unknown guns
  are visibly refused rather than silently substituted with another weapon.

## Current boundaries

This is an engine migration in progress. Loading a source asset or preserving an
engine-independent class does not establish gameplay or visual parity.

Retail worlds retain the full heightfield for sampling while rendering a bounded
region around the town and player spawn. Moving 40% of the view radius (capped at
96 metres) rebuilds the region at the player's current position, including roads,
props, resources and foliage, without restarting the session or changing spawn.
Parsed placements and instance arrays are cached; retired render entities and
colliders are removed. A geometry revision informs the authoritative host of
changes. This synchronous rebuild can hitch; async chunk jobs, original LOD
thresholds, non-foliage distance fades and floating origin still need Bevy systems.
Generated regions currently use LOD0; far-landmark rings are not yet reproduced.
Generated buildings come from a separate source
building/editor pipeline and are not produced by the terrain generator alone.

Terrain sculpting, editor overlays, analytic river cuts, terrain-hole sidecars,
authored landscape cut volumes, and shoreline geometry are not yet reproduced.
Replacement tunnel-mouth terrain currently selects its material per triangle;
its own fine splat textures still need the material used by regular terrain.
Retail Paths.dat road splines now retain the
source cubic curves, five-metre sampling, cross-slope banking, per-joint offsets,
material width/depth, texture repeat, end ramps and sealed collision underside.
Source road editor operations and lane AI bindings remain separate work.

The remaining custom shaders require WGSL ports: ocean normal ripples and shoreline
foam, planar reflections (the latest source intentionally keeps geometry flat), underwater rendering,
wind/grass/wire displacement, rain shelter/wetness/puddles/glass/impacts, caustics,
vehicle palette painting, character clothing/wear, flags, outlines and effects.
The source water-volume implementation is itself incomplete and should not be
mistaken for an already-solved upstream feature.

Static prop appearance is loaded, but special prop behaviors are not all wired:
doors, light fixtures, signals, destruction/debris, power taps, readable notes,
seats, sinks, shop/container overlays and furniture-specific materials. A raw
prop mesh is not proof that its corresponding interaction works. Exact source
nonuniform world-axis scaling and source layer masks also need explicit parity
checks beyond the current ordinary transforms. Ladder box collision has an
actual bundled-rung-gap regression test; other special shapes require their
own source comparisons.

Rigs draw and sample source clips, but the full gameplay animation state machine,
ragdoll bodies/joints, clothing composition, faces, wearable attachments and
layered aim/lean behavior remain to be integrated. Viewmodel equip/hold geometry
is present; ADS alignment, recoil springs, weapon-state reload/inspect/sprint
transitions, attachment changes, scopes, casings, audio and steering-wheel hands
are separate remaining behavior checks.

The editor is a substantial product surface: terrain/paint, environment, spawn
sets, objects/gizmos, roads/rails/fences/bridges/tunnels, rivers, building geometry,
openings/roofs/stairs/furniture, NPC authoring, undo/autosave, save overlays, baking
and playtests. It is not ported by the runtime content readers.

## Verification

Unit tests use real bundled static meshes, all three map placement files and all
five bundled rigs, alongside adversarial/truncated formats and isolated winding,
UV, skinning, interpolation, terrain-coordinate and mount-transform checks.
Retail map data is an external optional input, so tests must not silently require
one machine's Steam installation. Rendering tests should use the real local map
when available and state explicitly when only the asset viewer was exercised.
Screenshots must be opened and inspected. The deleted visual-golden gate is not
reintroduced.

The tunnel/foliage milestone passed 74 Rust tests. Inspected captures include
`artifacts/bevy-procedural-foliage.png` (source grass lighting and mip filtering),
`artifacts/bevy-procedural-tunnel.png` (seed 1337 twin highway tunnel near
−12916, −19678), and `artifacts/godot-eaglefire/rig_00.png` (original Godot hip
viewmodel at frame 60 for camera/framing comparison). The capture paths are
evidence from the development environment, not a visual-golden test gate.

The following terrain/bridge increment passed all 30 world tests, including raw
splat orientation, cross-tile filtering, procedural texel-center alignment,
eight-layer mip chains, graded bridge road UV phase, and the actual deck mesh's
two removed roadway triangles. Its inspected render artifacts are
`artifacts/bevy-pei-splat.png` and `artifacts/bevy-procedural-bridge-splat.png`.

## Authored doors and seats

`world/interactables.rs` consumes the original `mapServices` metadata and
replicated door/container/seat state. PEI registers 57 standalone door groups,
345 container door groups and 393 seat anchors. Leaves load the real extracted
OBJ meshes, hinges, axes, durations, sounds and sampled open/close curves,
including overshoot. Containers follow their replicated inventory-open flag;
only source-classified lit barbecues also hold the lid up. Display coolers restore
the measured transparent pane; picnic cool boxes do not acquire glass.

The original prop body owns each animated leaf collider. Leaf boxes retain the
source 0.15 m minimum thickness, disable while swinging, and wait for the local
player capsule to leave an endpoint before becoming solid. Render transforms
and the managed bullet/line-of-sight geometry use the same hinge pose. Geometry
is resent when the world streams or the session reloads. A real Rapier regression
covers closed/open collision, clear-space probes, and the rendered world pose:
Rapier 0.37's child-rigid-body inverse transform expression displaced meshes
under rotated parents while leaving their collision correct, so leaves use child
colliders attached to the existing prop body rather than nested rigid bodies.

Focus resolves source placement indices, distinguishes container and standalone
door ID namespaces, and selects the closest unoccupied authored seat to the
actual hit point. F sends the original validated toggle/storage/sit intent.
Seating adopts server occupancy and uses the source Idle_Sit pelvis offset;
standing uses the existing player collision-safe exit probe. No local toggle or
occupancy mutation bypasses the server.

Six native regressions cover real source curves, hinge transforms, Idle_Sit
pelvis alignment, overlapping ID namespaces, barbecue behavior, and actual
Rapier collision/render transforms. The first live shipping-container capture
also exposed a managed reach-anchor bug: a long prop's root is too far from the
visible leaf. The bridge now uses the transformed source ClosedLeafCentreWorld
anchor, with a guarding server round-trip test, without inflating reach.

Remaining authored-interaction presentation: whole-prop selection outlines,
nonlocal creature overlap gates, pane health/shards, powered appliance interior
lights, toaster/vending/elevator special motion, NPC dialogue, and generated
building fixtures. The cooler pane currently renders the source tint, alpha and
roughness; shared authoritative pane damage remains separate work.
