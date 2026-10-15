# Camera flinch

`player::flinch` ports the original angular camera spring from
`PlayerController.FlinchFromExplosion`, `ShowHurtCosmetics`, the 4/s recovery,
and the first/third-person camera composition. Third-person ordinary-gun
feedback consumes 35% of the exact accepted vertical impulse from the existing
recoil module before its 18/s aim drain; it does not generate a second random roll. It changes presentation only:
body movement, `PlayerInput` aim, damage and mounted authority stay unchanged.

The accepted owner `hurt` event uses its actual optional source position, a
horizontal direction, and `min(damage,25)*0.5` degrees. Accepted explosive bullet
and frag events reuse the existing blast classifier (smoke/flare excluded), with
radius `max(blastRadius*2,12)` and magnitude 30. Vehicle blasts use 32/45;
deployable blasts use 18/28. Each kick uses the camera-local perpendicular axis,
quadratic radial falloff and ordered quaternion multiplication. Degenerate
vertical/coincident directions and nonfinite payloads cannot poison the spring.

`ExplosionFlinch { point, radius, magnitude }` is the shared local presentation
message. The mounted adapter can emit it for an accepted local cannon shot, using
the original 40m/4degree call site; that adapter is a separate checkpoint. `FlinchUpdate` follows the base camera,
accepted-event publication and mounted effects, before interaction rays and
transform propagation. First person
preserves its eye location. Third person rebuilds the shoulder direction and
runs the existing radius 0.39 collision sweep using the final kicked rotation;
it does not rotate a previously clipped camera through a wall. The base camera
is rebuilt each frame, so repeated presentation does not accumulate the same kick.

An integrated pause freezes the spring. Network menus retain live recovery.
Death, respawn, disconnect and session/load changes clear it, and accepted event
sequences prevent replay. Menu and fixed diagnostic cameras remain unshaken.

## Intentional source correction

The original cannon calls `FlinchFromExplosion`, but its subsequent
`PositionDriveCam`/`PositionVehicleCam` overwrites the camera basis without applying
`_flinch`. Native seated first person applies the kick in the seat's frame;
vehicle orbit applies it in the orbit camera's frame without moving the orbit
anchor. This repairs the visibly ineffective source cannon call, and is not a
claim of identical source seated output.

## Verification and remaining hooks

Ten focused flinch regressions and seven recoil tests pass. They cover
falloff/cap/axis bounds, ordered composition and 4/s recovery, actual ECS camera
reset/pause/session/death lifecycle, accepted-event replay, smoke exclusion,
actual Rapier third-person collision, the seated cannon correction, and reuse
of the same accepted gun impulse. A real ray regression verifies that an event
published in CoreEventsReady rotates the camera before CollectInteractions:
the final visible ray hits an offset target while the baseline ray misses and
PlayerInput aim remains unchanged. This schedule was corrected during peer review.

The production spring was rendered under Vulkan/lavapipe using an explicit
accepted-frag event fixture. All three images were opened:
`bevy-flinch-baseline.png`, `bevy-flinch-kick.png`, `bevy-flinch-settled.png`.
The kick frame records 22.209 degrees with unchanged physical eye position and
zero aim yaw/pitch. The checker wall and target tilt while the HUD stays upright;
the recovered frame returns to the original framing. JSON sidecars preserve the
camera quaternion and fixture scope. The process exited 0 with no renderer errors.
This is camera presentation evidence; playable cannon firing still requires the
separate mounted integration journey. Both native mounted-effect tests also pass
against the immutable occupant-provider-v65-final authority.

SAM missile/site blast call sites await those native subsystems. The separate pain overlay and directional hurt HUD are not
implemented by this camera adapter.
