# Native cab/trailer coupling

## Detached geometry checkpoint

This slice implements the original `Vehicle.Build` preparation for a parked,
detached trailer. Accepted hitch authority is described in
`VEHICLE_HITCH_AUTHORITY.md`. The subsequent native joint slice is recorded below;
the detached images alone make no claim about driving.

- Body triangles pass through the original landing-leg box first, then the
  baked lens boxes. All three corners must lie inside; no triangle is duplicated
  or lost. The remaining source positions/normals/UVs are retained for the normal
  visible body and its asynchronous collision decomposition.
- The separate leg visual shares the body's original material. `LandingLegScaleY`
  stretches around `LandingLegPivotY`, preserving the exact pivot. No additional
  physical body is created for the leg visual; the existing original landing
  support remains in the chassis collider while detached.
- A towing cab receives the source independent `SleeperHull` box at its original
  roof center. It contacts players and the reserved ghost-trailer group, while
  ordinary chassis ignore it. This preserves the sleeper surface after a later
  cab/trailer body exception without changing the original attached roof box.
- `collision::SourceMesh` supplies the post-partition mesh before the normal
  cache fallback. Its async consumer must not decompose the original leg faces
  into a permanent chassis support that would remain after future retraction.

Five focused native tests cover original transforms and lifecycle, every one of
the seven original trailers' triangle/attribute partition, real Rapier sleeper
contact and its ordinary-chassis negative control, the production builder's
post-partition mesh, and actual asynchronous consumer selection against a
distinct original-cache witness.

The opt-in Vulkan fixture is
`vehicle::coupling::tests::visual::render_original_detached_trailer_and_separate_landing_legs`.
`UG_TRAILER_MODEL=trailer|dinky_trailer` and `UG_TRAILER_SHOT=prefix` select the
original model and export whole/legs PNG+JSON pairs. The corrected whole view
frames original visible mesh bounds rather than the intentionally shorter
collision box. The following captures were rendered and opened:

- `bevy/artifacts/bevy-trailer-detached-{whole,legs}.png`
- `bevy/artifacts/bevy-dinky-detached-{whole,legs}.png`

They demonstrate original detached geometry only. No accepted hitch, motion,
landing-leg retraction or coupled driving is inferred from these images.

## Accepted relationship and physical joint

`coupling::state` derives a trailer's physics owner from the accepted cab row.
The trailer retains driver ID zero and has no independent client publication.
Only the cab's local owner simulates both bodies; the host retains unattended
pairs and uses accepted puppets for a remote driver's pair. The normal cab
publication includes the reciprocal trailer's position, Euler angles and both
velocities in the existing protocol 74 pair tail.

The native joint is a free-angular Rapier spherical joint at the exact source
fifth-wheel/kingpin anchors. Both original masses remain physical. Accepted link
changes retract the separate leg visuals and remove only the authored landing
support; a later asynchronous hull bake reapplies the same accepted state.
Detachment restores both, removes the joint and clears source angular spin.
Ownership transfer and reliable recovery reset current and next raw Rapier poses
through the existing origin/teleport helper before simulation resumes. Recovery
uses delivered reliable facts, including a repeated counter 1 after re-entry;
a recovery for a different current pair is discarded atomically.

Source approach behavior remains local geometry: the nearest eligible trailer
within 6 m receives only the cab/trailer pair exception and wheel exclusion.
It does not create a relationship or retract legs. GROUP16 represents the
source tow layer swap, while GROUP4 retains native vehicle identity. Players and
the independent cab sleeper still collide with the trailer. Wheel rays and
source-world uploads omit it; the original ThrowOrigin bit0|bit6 query still
includes it. Leaving approach range restores the prior masks and exclusions.

The original flattened-yaw stop pivots the trailer around its kingpin and removes
only outward yaw velocity. Spec0 retains the source runtime 90-degree default.
The cab's evaluated wheel engine force is halved, hitched trailers receive no
unattended parking brake, and the chase camera includes 70% of the trailer's
visible mesh extent. Unattended pairs park and wake together at the streamed
collision boundary; driven pairs retain owner simulation. Host crash detection
continues to use accepted remote velocities while those bodies stay kinematic,
so ownership changes cannot suppress authoritative impact damage.

The normal focused vehicle path offers the original hitch intent before ordinary
access, using feet-to-pin 3.5 m and source cab pin 1.6 m admission. The managed source
validator still decides connection, ordered cab selection and automatic tilt/
relative-speed detachment. Native code does not author those shared mutations.

Verification includes real original hull/bake and reciprocal-mass constraints,
kingpin/yaw limits, late-bake retraction, pair residency, real wheel/player/sleeper
contacts with disabled-exception controls, source-world/throw masks, remote host
damage and an actual UDP ownership/publish/recovery/re-entry/exit/unhitch journey.
The final frozen native executable passes all 144 non-ignored vehicle tests
(15 opt-in renders ignored), including 14 coupling cases. The separate player
throw-mask regression also passes. These run against immutable `hitch-v74-final`;
they do not claim a broad combined protocol75/backend validation.
The Vulkan fixture
`vehicle::coupling::tests::coupled_visual::gpu_accepted_hitch_two_mass_drive_and_release`
uses normal UDP intents and the production native wheel controller on a real
ground collider. All four captures were rendered and opened:

- `bevy/artifacts/bevy-coupling-accepted-detached.png`
- `bevy/artifacts/bevy-coupling-accepted-coupled.png`
- `bevy/artifacts/bevy-coupling-accepted-driven.png`
- `bevy/artifacts/bevy-coupling-accepted-released.png`

The driven sidecar records 9.27 m movement, a 4.8e-7 m pin gap, cab driver 2 and trailer
driver 0/effective owner 2, with original 10000/6000 HP retained. Accepted release
removes the joint and restores supports. The camera follows the pair, so movement
is established by the recorded transforms and physics assertions rather than
similar silhouettes. This is a scripted production-controller fixture, not a
full-game OS-input journey. Full-game hitch/drive/exit input and rigid cargo are
separate remaining work.
