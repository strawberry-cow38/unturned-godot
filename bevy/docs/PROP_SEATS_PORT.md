# Prop seats and reclined bodies

The native seat renderer follows `game/PropSeat.cs` and the local/remote body
branches in `PlayerController.cs` and `RemotePlayers.cs`. A chair places the rig
origin 0.422m below its cushion and uses `Idle_Sit`. A reclined seat leaves the
feet at the anchor, uses `Idle_Stand`, and rotates the visual body by -90 degrees
around the seat's X axis. It clears local look pitch and lean so neither can
bend the torso through the mattress. The controller remains upright and its
collision stays disabled until the accepted seat is released.

First-person bed eyes use the original seat-local `(0, 0.25, -1.5)` offset. Third
person uses the shared shoulder camera, including its collision sweep and world
seat yaw. Switching perspectives restores the bed eye without moving the body.
Remote recline requires both accepted sitting stance/occupancy and authored
`recline` metadata for that seat ID; late metadata changes the existing rig.
Rotation is rebuilt from the accepted yaw each frame, so it cannot accumulate.

## Which beds exist in the original

`WorldBuilder.LoadSeatCatalog` has only position/facing pairs. The retail
`Bed_*` meshes are ordinary props, not reclined seats. The original `Bed.cs`
creates its own reclined `PropSeat` on a 1m × 0.45m × 2m box. Its only production
caller is the optional no-map/CI fallback furniture path; normal map builds
explicitly disable this furniture. The separate native fallback fixture port
owns registration, claim interaction and materialization. This renderer change
does not add new bed interactions to retail map props.

## Verification

Four regressions check all four seat headings against the original anchor/eye
constants, the real local rig and pitch reset, late accepted remote occupancy
with stable rotation and hull restoration, and the native controller/camera
after an actual managed-authority seat acceptance. The latter exercises first
person, third-person shoulder orbit, return to first person and seat exit.

The opt-in production remote-rig fixture supports `UG_REMOTE_POSE_BEDS=1` and
`UG_REMOTE_POSE_SIDE=1`. It uses the original box mattress geometry and two
orthogonal bed headings; the other two peers retain prone/sitting controls.
These fixed accepted replicas test presentation, not a complete live bed
claim/sit/release journey.

`bevy-reclined-front.png` and `bevy-reclined-side.png` were rendered and opened
for independent source/code and image review. They preserve the source's exact
face-down pose and mattress intersection rather than inventing a corrective
offset. Both renderer runs exited successfully without shader errors. The
combined native suite passes 385 tests (three opt-in render fixtures ignored)
against immutable `profile-config-49`; the four recline regressions also pass
in the frozen `native-reclined-checkpoint` executable.
