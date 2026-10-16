# Native cab/trailer coupling

## Detached geometry checkpoint

This slice implements the original `Vehicle.Build` preparation for a parked,
detached trailer. Accepted hitch authority is already described in
`VEHICLE_HITCH_AUTHORITY.md`; native relationship consumption, a physical point
joint, retraction, approach ghosting and playable towing are still pending.

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
