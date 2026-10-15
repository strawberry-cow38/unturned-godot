# Vehicle seat switching and tank anchors

The server now accepts a change to a free, explicit seat in the vehicle the
requester already occupies. This is the source `PlayerController.TrySwitchSeat`
contract carried through the existing `EnterVehicle` command, without a new
wire number. A current-seat request, occupied seat, missing seat, `AnySeat`, or
request naming another vehicle is rejected. External entry retains its original
reach and fallback rules. For an already seated player, the server's seat binding
authorizes movement within the cabin; an original long vehicle's body anchor may
be farther from the hull origin than the external entry radius.

Leaving seat zero clears that owner's driver authority window and held driving
input. It does not stop vehicle velocity, brake, or change ignition. Taking seat
zero establishes the usual fresh driver authority window. Reliable enter facts
clear only that owner's previous slot before assigning the accepted slot, so an
observer never temporarily renders one player as both driver and passenger while
waiting for a delta snapshot. Seat poses continue through the committed trusted
source body-anchor provider; no client position assertion authorizes a seat.

Three core wire regressions cover driver/passenger/driver, stale driving commands,
all switch rejection cases, a long-cabin anchor, and immediate reliable observer
state. The real UDP bridge regression additionally exercises JSON host requests,
a remote claimant, occupied-driver refusal and both peers' resulting slots.
It passes against the isolated `seat-switch-v66` candidate and fails against the
otherwise identical preceding `mounted-flight-v66-final` candidate at the first
driver-to-passenger switch. All675 networking tests and11 bridge mounted/switch
regressions pass. Native key dispatch, source gaze/weapon-slot preservation and
tank body/optic anchors are being validated together in the native mounted
checkpoint; this authority checkpoint does not claim their rendered integration.

Sources: `game/PlayerController.cs` seat-key handler and `TrySwitchSeat`,
`core/UnturnedNet/VehicleReplication.cs` validated entry/driver authority and
reliable `ApplyEntered` path. Persistent seat ownership and save extensions beyond
the existing source lifecycle are not added here.

## Native source anchors and controls

`vehicle::turrets::Mounts::{seat_body,seat_eye}` implements the original
`SeatBodyLocal`, `SeatBodyBasis` and `SeatEyeOverride`. A tank gunner's body and
eye position use the first turret's yaw frame, excluding cannon elevation. The
body moves from original `(0.44,2,0.60)` by `0.7*gunnerRise` along turret Y. The
camera selects original sight `(0,4.3,-0.8)` or cupola `(0.44,4.2,0.60)` when the
accepted hatch state changes. Its orientation remains hull-relative so the
player's ride-look does not apply turret yaw twice. The full hull transform,
including pitch/roll, transforms all anchors.

The source driver periscope override is `(0,1.98,-3.92)`. This is exported
explicitly as `tank.periscopeEye`: `TankSpec` is initialized before the source's
static readonly constant, so its exported `DriverEye` field is legitimately zero
even though runtime `SeatEyeOverride` reads the final nonzero constant. The native
renderer must use the override rather than that field's generic fallback.

Occupancy adopts after the current accepted mount pose. Protocol65's
`gunnerRise` is used directly without a second interpolation; fixtures/older
adapters without it retain original3/s easing and SP-pause behavior. The ordinary
vehicle body-seat path is preserved. Ordinary first-person eyes now consume the
original animated Skull anchor; the generic seat-pan+1.05m remains its source
fallback, as described below.

Five native tests cover actual tank meshes/hatches, all yaw directions with
independent cannon pitch, a tilted hull, accepted partial rise, SP/MP pause,
ordinary seat preservation and a real bridge-to-native accepted player/body
comparison. Five Vulkan fixture frames were rendered and opened:
`bevy-tank-gunner-up`, `bevy-tank-gunner-down`, `bevy-tank-driver-eye`,
`bevy-tank-gunner-sight`, and `bevy-tank-gunner-cupola` under `bevy/artifacts`.
The original rig rises through the open hatch and is hidden below the closed
lid; the driver clears the visor. These controlled fixtures supply accepted
mount rows and do not replace the separate playable mounted input/optics journey.
Parent and managed-agent source reviews passed; parent independently opened the
raised/lowered body frames.

Native literal F1–F12 dispatch sends the same accepted enter intent. It refuses
source no-op/occupied/missing seats locally, with authority validating again.
The body, gaze and selected mount never change optimistically. F1's console
alias is reserved for on-foot use; Backquote remains available in a vehicle.
Two input tests and one actual UI keyboard test pass. The playable PEI journey
also accepted F2 from driver to gunner, switched to the original 8x/12x gunsight,
raised the gunner to the cupola, selected/fired the HMG, and accepted F1 back to
driver while preserving HMG slot1 and accepted ammo395. Source-anchor and input
evidence is in `bevy-mounted-*-verified` artifacts; the separate HUD ordering fix
and final integrated validation are tracked in `MOUNTED_NATIVE_PORT.md`.

## Ordinary seated eyes and driver window peek

`avatar::camera::VehicleCameras::seated_eye` reads the previously completed local
player rig pose, matching the original `PositionDriveCam` before `UpdateBody`.
The Skull translation is converted back into rig space, then placed using the
current accepted body seat and full current hull transform. This prevents a
previous-frame hull position or world-origin shift from dragging the camera.
The source `(0,.16,-.10)` eye offset and additional `.15m` rise remain in vehicle
axes; looking up or down does not rotate that offset around the skull. Missing
rig/bone returns the original fallback unchanged; a near-zero unposed skull
returns fallback plus `.15m`. A new session never consumes the old avatar rig.

The first-person driver slides toward the own-side window only beyond 40° of
ride-look yaw. Source division 240 on the own side and 360 across the cab produce
settled offsets of −.5m and +.3333m at ±160°. The offset lerps at 4/s on the render
clock, freezes with singleplayer pause, and eases to zero for a passenger or
third-person view. Entering another vehicle resets it; a same-vehicle seat swap
preserves the easing state. Tank periscope/sight/cupola overrides replace both
skull tracking and peek. Existing third-person collision/eye-plane aim remain
unchanged, and no accepted capsule or body position is moved by this camera code.

Focused tests use actual Idle_Drive/Idle_Sit rigs on a tilted and moving hull,
missing/skipped skeleton data, session replacement, source peek transitions and
actual tank optic meshes. Four Vulkan source-mesh fixtures were rendered and
opened: `bevy-seated-driver`, `bevy-seated-passenger`, `bevy-seated-driver-left`,
and `bevy-seated-driver-right` under `artifacts/`. Their sidecars show opposite
seat eyes at X −.5/+.5 and Y 1.4172; after 70 frames, the driver's left peek reaches
X −.9984 while the across-cab peek reaches only X −.1677. The own-side frame clears
the window; the across-cab frame remains inside behind the seats. These fixtures
validate camera placement with original art, not full environmental/glass parity.

Five ordinary-camera regressions pass (four skull/peek cases and the actual tank
optic override), alongside the existing aircraft/chase and tank-seat checks.
The prior-pose test samples the completed bones before the camera frame; the
optics test supplies a real animated skull plus 160° driver peek and proves both
are overridden. Source/code and all four cabin/window images received independent
parent review. The source `game/testing/tests/NetTests.cs::NetRideFreelook` also
documents the move from hand-authored DriverEyeLocal to the seated model's head.
