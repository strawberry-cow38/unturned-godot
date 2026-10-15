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

## Audio and lightning

`audio.rs` ports `RainAudio`, `RainMaterialAudio` and the upward `ShelterProbe`.
Three original rain beds retain their intensity thresholds, dB curves, random
WAV start offsets and 180 dB/s fades. Four pooled positional emitters follow the
nearest vehicle, tree canopy, authored metal roof and tent. The foliage target
also supplies the precipitation canopy mask; there is one shared nearest-tree
query. Material emitters never enter zombie hearing.

The independent upward cover query includes thin ceilings, rejects tiny props
and lamp fixtures through their parent hierarchy, and follows the original
0.15-second poll. Its 0.35-second shelter ramp drives the small level trim; a
separate 0.07-second spectral ramp follows the logarithmic 6,000–20,500 Hz knee.
Enclosed intact vehicle cabins use the original roof/tracked/plane/Pod metadata.
The current first-person cabin gate compares the camera with the occupied eye
pose; a dedicated third-person camera-mode hook remains to follow that feature.

`audio/filter.rs` adapts Godot 4.6's MIT-licensed `AudioFilterSW` low-pass and
`AudioEffectFilterInstance` routing (license in `licenses/GODOT_AUDIO_FILTER.txt`).
The source sets filter property `db=3`, which actually selects four biquad stages;
the adapter preserves that engine behavior and its resonance 0.25. Stereo
histories are independent. The source WAVs and Ogg are decoded once into bounded
PCM caches. Looping wraps the whole clip after a random initial offset, while
live cutoff changes preserve filter history. Filtering runs at each clip's
sample rate; the original bus may instead resample to its output mix rate.
Positional playback uses Rodio's attenuation and spatial panning, so its exact
falloff is an engine adapter difference from Godot's `AudioStreamPlayer3D`.

`lightning.rs` consumes retained, accepted `weatherStrike` facts and the active
weather's lightning interval metadata. Strike frequency follows authority ticks;
flash decay, repeated return strokes and delayed thunder use render time. Each
strike queues its own delayed clap, and four voices preserve overlapping rumbles.
The source cloud shader receives the warm tint, azimuth and flash envelope,
including the isotropic zenith floor that avoids a pinwheel overhead. A separate
postpass supplies the source's small fullscreen lift after the HUD, converting
to sRGB around the addition to match Godot's non-HDR canvas. A linear GPU blend
would incorrectly turn a 9% black lift into approximately 33% grey. The console
renders afterward, matching its source layer100 above the flash at layer90;
sunlight and ambient light are not changed. Pause and session replacement freeze or clear the
appropriate playback and visual state. `UG_STRIKE_AT` retains the source capture
seam, and `UG_WEATHER=lightning` fires the initial diagnostic strike.

The current source has no independent wind/snow ambience driver. It feeds the
same nonzero weather intensity into rain audio during snowfall, and that
behavior is retained. A snowfall Ogg existing in the content tree does not
supply missing mix rules. General `AmbienceAudio` also has no shelter filter in
the current source; this adapter does not invent one.

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

Five audio tests decode all seven original clips, check full-file wrapping,
measure low/high-frequency filter response, preserve stereo isolation, and drive
actual Rapier thin-cover removal plus playback volume/pause/stop state. Four
lightning tests cover source distance/clap selection, overlapping delayed booms,
the retained event → actual camera uniform/playback lifecycle with pause and
session cleanup, and the console camera route. All20 rain tests pass. This machine has no audio output device: these tests verify
PCM and mixer state, not a listening comparison.

The accepted console strike was rendered on the real PEI map at night in
`artifacts/bevy-lightning-before.png` and `bevy-lightning-strike.png`. Both images
were opened: directional warm cloud illumination and the small foreground lift
are visible while the console stays above the flash. The first capture exposed
an overly bright linear blend; the final sRGB-addition pass fixes it. The final
Vulkan run reported no shader validation errors.

Wet glass, plant/wire wind deformation, broken-window cabin openness and
player-built metal-roof metadata remain separate work. Snowflakes retain the original unshaded
square material, which does not use the rain roof/canopy shader. The rendering
adapter is not evidence that those unfinished systems are ported.
