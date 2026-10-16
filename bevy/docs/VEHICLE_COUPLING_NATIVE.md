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

## Full-game rocking: anti-roll extension parity

The handoff's open issue was an actual full-game W-drive of the hitched pair
that oscillated near its start, while CPU witnesses drove. Its own OS captures
showed the cause before anyone drove. After the accepted hitch, the unattended
pair vibrated sideways at about ±2 m/s with only about 0.1 m of displacement
and 6–7° of roll. Every CPU witness had the same signature: one side's wheels
bottomed at suspension length 0 while the other side hung, alternating. The
witnesses' >3 m distance assertion never caught it.

`unattended_coupled_rig_settles_level_like_each_body_alone` isolates it on flat
ground. The semi alone and the trailer alone settle, but the coupled pair rocked
to 24° roll at 3.6 m/s. Disabling the bar (`UG_ANTIROLL=0`) or softening it
(`UG_ANTIROLLK=0.2`) made it stable, which located the cause in the bar's input.
The port read compression (`suspension_length / rest`). Source `WheelExtension`
casts its own bit0 ray from `w.GlobalPosition`, and Godot moves a
`VehicleWheel3D` node to hard point + direction × suspension length every step.
`tools/wheel_extension_reference.py` verifies this on the real Godot
4.6.2/Jolt binary. Its fixture `data/wheel-extension-reference.json` has three
cases and twelve wheels. Grounded wheels read 0 at any compression, a wheel
hanging over a floor 0.3 m lower reads gap/rest = 0.738, and one over a floor
0.8 m lower (none in reach) reads 1. The source bar is therefore a lift-off
restoring couple, not a compression spring. The Rust oracle test replays every
wheel under a non-identity body pose.

The native bar now uses that reading: a ray from the wheel node along body down
for radius + rest + travel, against source bit0 (`player::world_solid`: world
solids but not bit6 props; a vehicle only through an un-ghosted fallback
chassis), excluding the body itself.

The gate is a `dynamics::Park` latch matching every source `_parked` site.
Spawn sets it, and so does Uncouple's `trailer.Park()`. Drive/DriveHeli/
DrivePlane (a driver or NPC at the controls), CoupleTo's trailer `Wake()`,
OnVehicleContact's ram `Wake()` (an active contact with another vehicle body)
and NetEndHold (the host resuming a body after a remote driver's hold) clear it.
A driver's exit does not park: source NetTests asserts `!Parked` after exit, so
a car abandoned while moving keeps its bar until it settles. Source sleep (an
unattended, untowed hull put to sleep once still) is approximated by
instantaneous velocity thresholds in place of the source's low-passed averages.

Known engine differences:
- Godot's grounded wheel node sits about 0.04 m closer to the ground than the
  radius. Its node uses a slightly longer suspension length than its own latest
  ray (0.1777 against 0.1378 in the level oracle), a property of Godot's
  suspension integration rather than the formula. Rapier's equilibrium has no
  such deadband, so the native bar can respond to a few centimetres of rebound
  that Godot ignores.
- The port's contact flag is one controller step older than Godot's.
- A ray starting inside a solid reads 0 natively; Godot skips that shape.

Evidence:
- The oracle test and the parked-rig test both fail under a surgical revert to
  the compression reading (0.711 vs 0, and 24° roll). The latch test fails
  under the old "an undriven car is parked" gate.
- Main-schedule witness distances, before → after: PostUpdate fixture 13.1 →
  22.0 m, fixed schedule 11.9 → 23.7 m, CCD 9.2 → 21.6 m, live entered pose with
  glazing 9.5 → 19.7 m, and actual owner input/publish/tick 8.5 → 15.7 m. The
  PEI road+props witness went 13.2 → 9.0 m from a different settled start pose,
  still accelerating through 4.2 m/s.
- The full native suite passes on the final code (759, 49 GPU-only ignored),
  including 161 vehicle/CLI tests. The managed suite (251) passes.

Full-game journey (actual OS input via xdotool on Xvfb/lavapipe, `--play
--fixed-fps 20`): `--vehicle semi --hitch` with `UG_VEHICLE_AT=-601.5,-200,0`
stages the original Main `--hitch` pose on the PEI road. Coupling stays the
player's accepted F hitch. The journey was console teleport → aim → "[F]
connect trailer" → hitched ("[F] disconnect trailer") → "[F] Enter Semi Truck"
→ hold W → exit while moving. The opened final-code captures are
`bevy/artifacts/bevy-coupling-antiroll-*.png` with JSON sidecars. While parked
and coupled, both bodies stay under about 0.2 m/s at about 0° roll. Driving
accelerates the pair down the road with the trailer tracking. Exiting at
3.6 m/s leaves the coupled pair coasting level (2.4 then 0.7 m/s). An earlier
run of the same journey reached 6.5 m/s over about 35 m. Rigid cargo and
remote/MP towing journeys remain separate work; the latter is planned with the
hybrid dedicated host (ledger row 16).
