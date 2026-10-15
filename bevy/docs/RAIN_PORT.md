# Rain, snow and wet surfaces

The native weather renderer consumes the managed authority's `rainIntensity`,
`rainWetness`, `rainPuddle`, `isSnowing` and `windMain`. It does not run another
weather simulation. The original 150-second puddle fill and 420-second drain
remain in the managed adapter, along with snowfall accumulation and melting.

## Source presentation

`world/rain/field.rs` ports `RainSystem3D`: 6,500 world-space rain streaks with
1.4-second lives, the original emitter box, wind gravity, velocity-axis
billboards, lens fade, canopy shadow and sea clipping. The camera trail retains
the original smoothing, lead, jump detection and restart cooldown. Snow uses
the separate 900-flake, nine-second pool with the original square billboard,
rotation, speed and gravity. Particle simulation and shader clocks pause with
the game. Pools and camera history shift once with the floating origin.

Godot's CPU-particle implementation uses two independent spread angles and
semi-implicit gravity integration; the adapter preserves both, including the
30 Hz warm-up steps. Cosmetic random samples use the existing native generator,
so individual drops are not a byte-identical Godot random stream. Emission keeps
the same rate and pool size; the distribution across frames can differ.

The wind field uses the upstream Rust FastNoiseLite implementation with the
original seed, frequency, OpenSimplex2S/FBm defaults, octave count, weather blend,
bearing and integrated sway phase. A regression compares noise values measured
in an actual Godot 4.6.2 process. The original diagnostic strength/frequency/
octave overrides remain available. Render elapsed time replaces Godot's process
uptime, so the field pauses cleanly and repeatable captures start at the same
phase.

`world/rain/roof.rs` ports `RainRoofMap`: a 128-square half-metre camera window,
cached in 16-metre tiles, with a 128-ray-per-tick fill budget. Downward queries
include fixed cover and reject moving bodies, small scenery and lighting poles.
The three-by-three median retains real heights. Shader sampling blends four
height *comparisons*, preventing a blended roof edge from inventing a wall of
rain across a doorway. Cover insertion, removal, movement and disabled-collider
changes invalidate cached samples after the source two-physics-tick delay.
Whole-tile origin shifts rekey the cache; other shifts rebuild it.

`world/rain/wet.rs` retains original StandardMaterial handles while adding the
source wet material to eligible opaque, non-emissive map objects and hard roads.
Door leaves inherit that conversion without losing their original source
material. Puddles retain the original road/block-road/Dock_1 whitelist; ordinary
props get wet sheen and impact crowns. The terrain extension shares the same
roof texture, puddle field and impact library. Grass layers stay dry, fully
snow-covered terrain bypasses wetness, and road layer four receives puddles.

The shared shader ports the original two-scale puddle noise, waterline,
derivative edge fade, two impact grids, jittered rings, short crowns, wind lean,
distance limits, albedo darkening and roughness/specular response. World-noise
coordinates include the floating-origin offset. The current flat source ocean
does not contain rain-ring rendering; no substitute ocean effect is added.

One source control-flow defect is corrected deliberately: `Snowing=true` turns
off the source rain emitter, but a later frame assignment turns it back on for
any nonzero intensity. The native adapter selects one precipitation mode at a
time; existing particles may finish when the mode changes. A regression guards
snow not restarting rain. Dry weather creates no empty meshes or draw warnings.

## Verification and remaining work

Eleven focused tests cover original pool and billboard constants, camera jumps,
origin shifts, exact measured noise values, negative-coordinate roof caching,
height-edge filtering, source scenery exclusions, and live Rapier removal and
restoration of cover. A moving dynamic body is verified not to invalidate or
shade the roof cache. Actual material conversion retains the original handles,
separates puddle eligibility, and skips cutouts/emissive objects. A minimal app
verifies dry weather creates no zero-vertex meshes and snowfall stays exclusive.

Actual Vulkan/lavapipe captures were opened and inspected:
`artifacts/bevy-rain-road.png` shows heavy rain at the original PEI dock/street,
with full puddles, rings, crowns and perspective streaks; the paired JSON records
intensity/wetness/puddle at one. `bevy-rain-shelter.png` shows no rain inside the
closed shipping container, but the dark interior is not a useful visual test of
a doorway edge. `bevy-snow-street.png` shows the separate square flakes without
rain streaks. The source still drives wet-surface crowns from nonzero weather
intensity during snowfall; that separate source behavior is retained. These
captures produced no shader validation or empty-mesh warnings. All 66 world
tests pass after the fixture-parent review fix.

Layered rain/wind/snow audio, shelter low-pass and material emitters, lightning
and delayed thunder, wet glass, plant/wire wind deformation and player-built
roof metadata remain separate work. Snowflakes retain the original unshaded
square material, which does not use the rain roof/canopy shader. The rendering
adapter is not evidence that those unfinished systems are ported.
