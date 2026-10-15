# Footsteps, landings and swim strokes

`footsteps::FootstepPlugin` ports the cosmetic paths in `PlayerController`,
`RemotePlayers` and `GameAudio`. Gameplay noise for zombie hearing remains in
its existing authority path; these voices cannot apply damage or hearing events.

Local ground steps observe resolved motion before the next movement simulation.
Swim strokes observe the current swim velocity after simulation. Landing audio
runs after Rapier contact resolution and before `PublishPlayer` clears downward
velocity, so its threshold and volume use the actual precontact fall speed.
Remote players reuse the existing 14/s position glide and 8/s speed smoothing;
their displayed motion drives the same stride rules. Ground probes distinguish
air from floor, and seating, death and teleports reset remote stride/landing state.
No additional network grounded bit is needed for these cosmetic sounds.

The source strides are 1.5 m standing, 2 m sprinting, 1 m crouching and 0.9 m
prone. Stance volumes retain the source -4 dB trim. Missing run audio falls back
to walking on the same material before falling back to concrete; retail dirt
ships walking clips only. Metal footsteps use `metallow`, metal landings use
`metal`, and rock footsteps use concrete. The port does not restore the gear
rustle that the source deliberately removed. Landings require downward speed
greater than 2.5 m/s; swim strokes use the three original wading banks.

The shared impact surface resolver provides terrain layers, prop metadata and
explicit generated materials. Foot probes use the original world collision
layer and span 0.3 m above to 0.6 m below the feet. Source `WaveField.Flat=true`
makes the water probe's height equal to sea level. Puddles affect audio only,
never physics grip: they require accumulated water >=0.35, concrete or metal,
a wet sample in the shader's two-octave puddle field, and open sky. CPU samples
use the same local position plus origin uniform as the rendered field.

Rain and footsteps now share `sound::shelter::Shelter`. Review caught an inherited
rain adapter mismatch: the original up-ray tests world-layer bodies, including
dynamic bodies, rather than all fixed bodies. The shared query now follows that
mask, retaining lamp-ancestor exclusion and the footprint-only small-prop test.
A thin broad ceiling shelters audio; a tiny overhead object does not.

The original WAV/OGG banks feed Bevy spatial voices with source pitch variation,
inverse-distance gain, the default +3 dB cap and linear maximum-distance fade.
The spatial scale keeps Rodio's own inverse-square attenuation out of the audible
range, avoiding a second distance falloff. Stereo panning remains Bevy/Rodio's
adapter; Godot's default distance high-shelf filtering is not yet reproduced.
Voices follow master volume, pause, disconnect and session epoch changes. They
also retire without an output device instead of accumulating queued entities.

Seven focused tests exercise original clip decoding and no-repeat gait fallback,
real Rapier ground/water/layer probes, puddle wet/dry and cover cases, dynamic
roof filtering, local steps/landing/swim, remote airborne/seat/death/teleport
suppression, and actual playback component lifecycle. All 349 native tests pass
against the immutable `probe-tests-26` bridge (one manual render fixture ignored).
The machine has no audio output device, so this is PCM and playback-state
verification, not an audible comparison.

Engine attenuation reference: [Godot 4.6 AudioStreamPlayer3D](https://github.com/godotengine/godot/blob/4.6-stable/scene/3d/audio_stream_player_3d.cpp).
