# Native resource presentation

`world/foliage/resources.rs` consumes accepted `resourceHit` and `resourceFelled`
facts from the existing authority. It does not apply damage, emit rewards, reset
health, or publish a second resource state. The existing dead bitmap still owns
standing mesh/collider visibility and regrowth.

Trees retain their original `(resource name, .bin instance row)` identity and
resolve the current resource index through `ServiceManifest`. A holiday manifest
can change indices without transferring an old stump to a different tree.
Reliable fall events can arrive before the 25 Hz dead bitmap: the renderer keeps
the accepted direction until that state arrives, including elapsed waiting time
in the existing fall lifetime. Dead state without a fall event creates only the
original stump, which also handles load/join and streamed resources.

The original `ResourceField.cs` presentation is preserved:

- Each hit immediately arms only that canopy's shake at 1; it decays by
  `exp(-5.5*delta)` and ends below 0.01. A private material carries the shader's
  original per-vertex sine jitter; neighboring instances keep the shared material.
- Original `*_stump_N.obj` and `*_debris_N.obj` meshes/textures are loaded at runtime.
  Stumps use the original measured top and -1.5 local base. Their box collider
  applies placement scale to dimensions on an orthonormal world body, with world
  collision membership and wood surface metadata. Regrowth removes both mesh
  and collision; no ghost stump remains.
- Falling trees use the original 1.5-degree kick, `0.669*sin(angle)` torque and
  at-most-1/60-second substeps. The base-center pivot does not drift. The original
  sin-cubed lift clears the planted base and its own trunk radius. The trunk ends
  horizontal, followed by the 0.4-second/two-tap rebound.
- The canopy follows the original 2.5 Hz damped spring and 0.03 offset cap; the
  trunk receives the separate 3 Hz impact kick, with ambient trunk sway disabled.
  Both continue after the rigid settle. `UG_TREELEAF=0` preserves the source
  diagnostic switch. All source offsets also run in depth/shadow passes.
- Debris expires at the accepted 11-second lifetime. Early regrowth retires the
  stump but leaves that independently scheduled debris cleanup intact. Original
  authority alone awards the logs/sticks at its timer.
- The measured original Timber WAV starts at `max(0, predicted landing time minus clip
  duration)`, with original impact-speed volume and pitch variation. Its distance
  gain shares the unchanged source attenuation helper with footsteps. Pause,
  session reset and no-audio-device cleanup are explicit.

The shipped Timber WAV lasts 8.256 seconds, so the source's long-clip backstop
starts it immediately for this 5.9167-second fall. It is not trimmed or retimed.

The original comments still quote the former 84-degree fall's 5.83 seconds and
1.094 rad/s. Running its current **90-degree** integrator yields 5.9167 seconds
and 1.15519 rad/s. The port retains the actual code, including the source 1.094
audio/impact normalization.

Root transforms and physics shift through the existing origin transaction;
cached source transforms and the falling base shift once through the foliage
hook. Session replacement clears hits, pending events, stumps, debris and audio.
Rain roof invalidation follows stump/trunk collider changes.

## Verification

Focused tests exercise instance-isolated hit/rearm, private-material retirement,
actual shipped meshes and box dimensions/layers, torque/springs/lift, dead-state
stumps, delayed event/snapshot ordering, original-index remapping, regrowth,
session cleanup and measured audio timing/pause/retirement. A real bridge shot
drives the native fall; advancing native animation alone cannot award logs, and
advancing the original authority does award them at its retained timer.

The opt-in `render_accepted_original_tree_fall` fixture uses a real bridge shot
and the production materials/animation with original Pine_0 assets. It writes
standing/falling/landed/cleanup PNGs and authoritative JSON into
`UG_RESOURCE_SHOTS`. Run with the repository's X11/Vulkan xvfb setup and an
immutable tested `UG_BRIDGE_DLL`; `--headless` cannot verify the renderer.

Fifteen focused foliage/resource tests pass (two manual render fixtures ignored).
The final Vulkan/lavapipe sequence in `artifacts/bevy-tree-fall/` was rendered and
opened: standing, accepted falling pose, horizontal landed trunk with separate
stump, and stump-only cleanup. Authority JSON records ammo 30→29 and zero drops
before cleanup, then five original wood items. The fixture deliberately renders
resource presentation, not item replicas. The first attempt exposed the event
before snapshot bug; the corrected sequence and a regression now cover it.
Independent source/code and final four-image review found no blocker.

## Remaining boundaries

The current remote wire supplies resource harvested index/dead state, not hit or
fall direction/timing. Remote clients therefore render accepted stumps without
inventing fall directions; a later typed accepted event is needed for exact
remote shake/felling. Persistent dead state restores stumps after load, while
transient in-flight debris is not reconstructed from a save.

Native stump collision is present for body/footstep/shelter queries. The managed
bullet BVH still filters the old dead resource and does not receive a new stump
mesh from this cosmetic adapter. No additional geometry publication is added.
Audio attenuation is ported, but the original distance high-shelf filtering is
still a shared audio gap; this environment has no audio playback device.
