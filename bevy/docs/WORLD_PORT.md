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
  are deliberately not flipped a second time.
- `world.rs`: GUID mappings and PEI/Washington/Yukon object placements; original
  placement orientation `Y(180-ey) * X(ex) * Z(-ez)` and Z-negated positions;
  original map heightmaps and splatmap dominant layers; original resource and
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
  original species radii, height, offset and half-metre visual sink.
- The player starts at an actual non-alternate `Spawns/Players.dat` location,
  excluding the source's origin placeholder rows. The deterministic initial
  choice is the authored spawn nearest the selected town focus. Terrain controls
  its ground height. The town focus is separate from gameplay spawn, so a menu
  camera does not place the player inside the busiest building.
- The legacy ocean uses the map's own Lighting.dat sea level. The 0.99+ sentinel
  suppresses it (Yukon has water volumes instead). Its current Bevy material is a
  static transparent surface; this is not yet the source ocean shader.
- The original `InfiniteTerrain.Generate` output can be rendered through the
  engine-independent C# bridge's JSON export: row-major heights, layers, holes
  and tree placements. No substitute procedural terrain formula is introduced.
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

The initial world build is a bounded region around the town and player spawn.
It retains the full heightfield for sampling, but only meshes and colliders in
that region are materialized. Streaming a moving player across the complete map,
original LOD thresholds, distance fades, floating-origin streaming and generated
road/rail/bridge/tunnel/pylon structures still need Bevy systems.

Terrain currently uses the strongest splat layer per cell with the original
layer texture. Eight-layer shader blending, paint-edge noise, terrain sculpting,
editor overlays, analytic river cuts, terrain-hole sidecars, and shoreline
geometry are not yet reproduced. Road spline geometry is not yet assembled.

The existing custom shaders still require WGSL ports: ocean normal ripples and shoreline
foam, planar reflections (the latest source intentionally keeps geometry flat), underwater rendering, sky/clouds/stars/day-night,
wind/grass displacement, rain shelter/wetness/puddles/glass/impacts, caustics,
vehicle palette painting, character clothing/wear, flags, outlines and effects.
The source water-volume implementation is itself incomplete and should not be
mistaken for an already-solved upstream feature.

Static prop appearance is loaded, but special prop behaviors are not all wired:
doors, light fixtures, signals, destruction/debris, power taps, readable notes,
seats, sinks, shop/container overlays and furniture-specific materials. A raw
prop mesh is not proof that its corresponding interaction works. Exact source
nonuniform world-axis scaling, ladder collider special cases and source layer
masks also need explicit parity checks beyond the current ordinary transforms.

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
