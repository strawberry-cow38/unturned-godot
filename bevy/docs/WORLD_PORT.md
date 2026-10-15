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
  suppresses it (Yukon has water volumes instead). The current flat source ocean
  shader, planar reflections and submerged-camera optics are described below.
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
thresholds and non-foliage distance fades still need Bevy systems.
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

Remaining custom shaders include wind/grass/wire displacement, wet glass,
character clothing/wear, flags, outlines and remaining effects. Flat ocean,
reflection, underwater optics and source-disabled terrain caustics are described
below. Rain shelter, wetness, puddles and surface impacts are described in
[RAIN_PORT.md](RAIN_PORT.md).
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

The corrected native outside-door journey was rendered and inspected in
`artifacts/bevy-door-closed.png` and `artifacts/bevy-door-open.png`. From player
position (−476.445, 32.5, −111.1), F toggles PEI shipping-container door 18; the
saved authority snapshot records `open:true`, and both original leaves visibly
swing 90 degrees. The regression checks that this visible pose agrees with
Rapier, rather than accepting a moving collider with a displaced mesh.


## Floating origin and the authority barrier

`world/origin.rs` implements the original 256-metre region shift, triggered beyond
`UG_INF_REBASE` (default 1024 metres). Absolute origin and diagnostic camera/start
coordinates use doubles. Generated terrain and all world-coordinate structures
are localized before conversion to floats; region-local trees/ground cover,
normals, directions and model pivots retain their original values. Cached regions
remain absolute so revisiting them after a later shift does not accumulate error.

The native origin pass and the original managed authority shift as one barrier.
The managed host drains pending packets and publishes a full snapshot at the same
tick; native roots, Rapier bodies/colliders, interpolation, terrain samples,
collision exports, seats, vehicle caches and world-space particle data follow.
Rapier collision detection refreshes immediately without integrating physics, so
movement rays before the next normal physics sync see the correct frame. UI and
the separate first-person camera retain their own coordinates. Terrain patterns
use the original modulo-32768 shader origin to remain continuous across ordinary
shifts, retaining the source's phase reset when that 32-km period wraps.

Every native coordinate-bearing request carries the adopted origin epoch. A stale
request is rejected explicitly. Origin adoption runs at frame/fixed boundaries,
between UI intents and movement actions, and immediately after player publication
before projectile replicas. Pending effect messages translate in place, preserving
reader cursors and event deduplication. Load/respawn can change the origin too;
queued authoritative teleports adopt their new correction before the next native
movement solve. The original bounded X/Z wire range is ±4096 metres; distant
storage remains in the original authority with the active play area near zero.

Run `bevy/check.sh` to build the original managed bridge and execute native tests.
Its arguments pass through to Cargo, e.g. `bevy/check.sh world::origin::tests`.
The origin integration test deliberately requires a built bridge and does not
silently skip its native→authority round trip. Five regressions cover source floor
rounding, sub-metre detail at 1e9 metres, structure coordinates versus normals,
real Rapier query/writeback continuity, retained event-reader state, and a queued
teleport drained during a same-tick origin change.

The distant native capture `artifacts/bevy-distant-origin-tunnel.png` was rendered
and inspected together with its authority JSON: the original twin tunnel at
absolute (−12916, −19678) uses origin (−13056, −19712), with the live player near
local (140, 59, 34), healthy and connected rather than clamped to the wire boundary.

A second live journey (`artifacts/bevy-origin-before-crossing` and
`artifacts/bevy-origin-crossing`, PNG plus JSON) teleported the player to local
(1200, 100, 34), then crossed the default threshold. Origin advanced by 1024 m to
(−12032, −19712), epoch 1; the body landed on the streamed source terrain at local
(176, 59.406, 34). Roads, trees and grass remained visible, 25,254 authority
triangles were refreshed, and the player remained connected/alive with source
fall damage applied. The log reported zero missing source assets.

## Terrain layers and water

`world/layers.rs` exports the original dominant terrain grid to the authority and
provides the native surface sampler. Retail rows retain the source negative-Z
direction, four-metre spacing, nearest midpoint-even sampling and map-edge clamp.
Equal raw splat weights keep the first channel, including zero for an empty texel.
Generated regions retain their original layer samples. Wildlife therefore keeps
the source `Terrain.IsWater(layer) == (layer == 5)` sand proxy; it does not infer
this classification from sea level or the renderer's noise-warped material blend.

`world/water.rs` and `water.wesl` port the current flat-water source: original
five-octave Hoskins gradient noise, ripple-distance fade, source depth-based
shore foam, shallow tint, brightness-dependent sky damping and Fresnel alpha.
The executable source Fresnel exponent is 2; nearby source comments describe
older intermediate tuning values and are not used instead. The mirror target is
half the window size from its first frame and resizes with it. It renders the
same sky, terrain, props and trees/resources, with reflection-only copies of
scene lights that omit shadow maps. Grass, particles, UI and the first-person
fill light stay outside the reflection pass. Native sRGB texture views decode
the target on sampling, so the Godot render-target workaround is not applied a
second time.

Reflection lights now follow source/ancestor visibility before native light
culling and retire when their source despawns or leaves the world layer. The
shared HDR startup selects Bevy0.20's CPU cluster assignment, whose per-view
RenderLayers filtering keeps shadowless reflection copies and first-person
mirrors from illuminating other cameras. Real three-camera cluster tests and a
same-process midnight headlamp OFF→ON→OFF capture guard both boundaries; the
final OFF pixels return to the original baseline. See HDR_NIGHTVISION_PORT.md
for the measured capture and optional actual-light sidecars.

The mirror uses a proper camera rotation and a texture-Y flip. A projection
regression compares screen edges and rolled cameras to the mathematically exact
plane reflection, avoiding reversed mesh winding. The sky is excluded from the
depth prepass so it cannot produce false shoreline foam. The ocean's dedicated
render layer is explicitly included in origin shifts. Retail ocean bounds and
the infinite world's 256-metre centering follow the original source; the now-flat
surface uses a quad, with opaque terrain depth hiding land, rather than retaining
the obsolete vertex-displacement grid. `UG_REFLECT=0`, `UG_REFLEVERY`,
`UG_REFLSTR`, `UG_REFLDEBUG` and `UG_NOWATER=1` remain useful diagnostics.

Two actual PEI shoreline renders were inspected: `artifacts/bevy-water-coast.png`
at camera (-450,30,-100), yaw 90/pitch -7, and
`artifacts/bevy-water-coast-offset.png` at (-448,30,-78), yaw 100/pitch -7.
They show the visible shallow bed, shore/wall foam and actual reflected trees and
dock geometry. Reflections remain aligned after translation and yaw. Both runs
reported zero missing source assets and no shader validation errors.

Still separate from this surface milestone: exposing live reflection settings
through the graphics menu. The current flat source water shader contains no rain
impact rings; road/prop puddles are covered in [RAIN_PORT.md](RAIN_PORT.md). Per-volume
water is also absent, but is an existing Godot TODO (Terrain.cs skips Yukon
water volumes), rather than a feature lost in this engine migration. Physical
buoyancy remains correctly flat because the current source sets `WaveField.Flat`.


## Submerged camera optics

`world/underwater.rs` drives the effect from the world camera's eye, with the
original 6 cm enter / 0 cm exit hysteresis. A forced `UG_UNDERWATER=metres` view
and the `UG_WATEROPTICS=0` legacy comparison remain available. The render pass
uses original crossed-sine refraction, per-channel Beer–Lambert extinction,
weather-driven sediment, depth-dependent veil, scattering blur and vignette.
The shallow/deep palette lives in one `water_optics.wesl` library shared with the
surface. No camera-relative effect data requires a floating-origin adjustment.

The source 3D screen texture captures opaque geometry before transparents; the
native pass retains that ordering, then composites the submerged result before
the first-person camera and HUD. Each active view receives six real mip levels,
including LOD interpolation at the source maximum 4.2. The mip chain uses the
original integer 2×2 box footprint, including odd dimensions, as implemented by
[Godot 4.6 screen copying](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/renderer_scene_render_rd.cpp#L348)
and its [mipmap kernel](https://github.com/godotengine/godot/blob/4.6-stable/servers/rendering/renderer_rd/shaders/effects/copy.glsl#L75).
The pass supports sampled or multisampled depth, recreates mip targets on resize,
and skips rendering while dry. Original terrain caustic noise and depth fade are
retained in the terrain material, with the source's current strength **0**; they
remain off by default as requested in the original 2026-10-09 change.

Regressions cover eye hysteresis and teardown, camera-only uniform publication,
weather extinction, reverse-depth reconstruction and actual mip availability.
A render-layer regression also guards water visibility when the underwater
plugin inserts `DepthPrepass` before the reflection setup.

Two fresh Vulkan captures were rendered and inspected: the actual PEI shore at
camera (-450,23.5,-100), 2.1 m below sea level, in
`artifacts/bevy-underwater-shore.png`; and the above-water coast camera forced to
4 m submersion in `artifacts/bevy-underwater-forced.png`. Nearby wall and seabed
detail remain visible, the distant shore scatters into the water veil, and the
HUD/crosshair stay sharp. Both logs report no shader errors and zero missing
source assets. All 54 focused world tests passed after this integration.


The live crossbow front-door check exposed an authority-only leaf offset bug:
the shared box mesh helper centres its vertices, while native compound colliders
already carried the actual authored AABB centre. Door export now translates
those vertices by the same centre before applying placement/hinge transforms.
A real Container_0 mesh/bridge regression reproduces the old front-leaf miss,
then proves the production export hits the visible front, retires the closed
position after opening, and hits the opened leaf. Repeat live arrow evidence is
pending the next combined binary; the physical collider was already correct.
