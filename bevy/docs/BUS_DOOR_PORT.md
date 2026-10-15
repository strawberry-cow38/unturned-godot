# Original bus folding door

The bus uses the original `Vehicle.BuildBiFoldDoor`, `MeshCut`, `ToggleDoor`,
`CycleDoor`, `SetDoorOpen`, and `UpdateDoor` behavior. The existing original bus
mesh, palette, pane files and `busdoor_open.wav` remain the content source.

## Geometry and contact

`content::cut` retains source vertex normals, UVs and colors while splitting,
subtracting boxes and capping cut faces. Generated face winding is converted to
Bevy's front-face convention. The exclusive source body branch is preserved:
aircraft, landing legs and baked headlight zones take precedence over a door cut.

The bus's front/rear trim, central mullion, bottom cap, floor subtraction, lowered
pocket and three risers use the exact original spec. Hinge A is hull-local;
hinge B is its child, with the original separate `DoorHingeBX` field. A turns
−90° and B +180° under the source quadratic ease. The two source pane meshes
move under those same hinges, including their rain materials and accepted broken
masks. Repaired panes retain their original door collision filter.

Each leaf has the source full `DoorHit` box, including its visible window. The
native player passes through leaves and their glazing; other vehicle bodies and
host combat rays see them. The vehicle body keeps the cut source player mesh,
so the lowered step and open doorway are real geometry. The original comment
claims zombie blocking, but the actual source zombie mask excludes this layer;
the native mask preserves that behavior. There is no extra body simulation.

Authority triangle upload uses each leaf's full box at its current articulated
world pose. A stationary bus therefore still replaces its geometry when folding.
The matching pane upload retains its current centered box, rotation and scale.

## Accepted control and lifetime

Protocol 70 adds command 78 and event 59. A driver request carries only a vehicle
ID and driver intent. An on-foot request includes the current look origin and
direction. The server validates the authenticated alive owner, actual driver's
seat or bounded on-foot origin, source 2.6m look segment and intervening world
geometry, then the original access-zone ordering. Neither request supplies the
wanted state or animation phase.

The managed service executes extracted original switch and timing bodies at
50 Hz: 0.8s motion, no mid-swing reversal, 2.5s boarding/leaving hold, and a 0.2s
auto-close retry if still moving. Accepted enter, same-vehicle seat switch and
exit each call the source cycle. Boarding itself does not wait for the door.
Native key and F-focus consumers only send intents. The native geometry adopts
`hasBiFoldDoor`, `doorOpen` and exact `doorFold` from the vehicle replica.
Driver Ctrl is a discrete action, so it resumes when a closing Map releases its
open state; the outgoing 85ms image still captures continuous look and driving.
A moving door displays the source passive status without advertising an F key.

The vehicle snapshot has a one-bit capability gate; folding vehicles then carry
one wanted-state bit and a finite float phase in [0,1]. Other vehicle publishers
cannot overwrite these fields. Reliable `vehicleDoor` facts carry actual
open/close edges and the original audio position. Late join adopts the phase
without replaying sound. One audio voice per bus uses the source clip, −2dB,
unit size 6 and range 40; a later edge restarts it. A batch of delayed edges still
leaves only one voice. Shared vehicle audio handles pause and parent retirement.

Save data preserves wanted state, phase and remaining hold. The source leaves a
small negative hold after expiry; capture records this inactive value as 0 so a
normal auto-close can reload. Initialization advances the restored clock only
by the actual bootstrap simulation ticks. Rebasing uses the vehicle's current
pose; no separate world-position cache is introduced.

## Verification

Five source geometry/contact tests cover actual bus meshes, all cut faces and
palette attribution, the original lowered floor, source branch precedence,
fully tilted nested hinges, pane break/repair masks and hierarchy cleanup. The
real bridge uploader test proves closed→open→closed ray changes while the bus
is stationary. Removing only the full-leaf marker reproduces the hollow-window
ray failure. Two shared mesh-cut tests and the existing 15 glazing tests pass.

Five managed tests cover actual UDP driver/passenger requests, focused reach,
world obstruction, forged origin, protected state publication, accepted
boarding/switch/exit, late join, mid-swing refusal, auto-close retry and both
active/expired saved holds. Three core tests cover atomic full/delta adoption,
state hash, exact phase, malformed/truncated commands/facts, and authority gates.
The complete combined 144 managed and 710 core suites pass.
The immutable bridge also passes all 128 JSONL protocol tests. Seven native bus
tests include the production key consumer over real UDP, no optimistic phase or
sound, rebound keys, passenger/modal/moving gates, closing-Map input, one voice
per bus and parent retirement. The shared interaction dispatcher additionally
guards passive status against both a misleading key hint and accidental action.

Opened native `bevy-bus-door-{closed,half,open,inside}.png` and original Vulkan
`godot-bus-door-{closed,open}/rig_00.png` agree on the doorway fit, inward fold,
stacked leaves and lowered step. Parent and world independently reviewed them.
These first images use explicit presentation rows; they do not prove accepted
controls. The separate opened `bevy-bus-accepted-{closed,half,open,reclosed}`
PNG/JSON journey runs the production native key consumer against an actual UDP
host and remote client. Its accepted phase is 0→0.45→1→0, with `ownsWorld=false`
and driver seat 0 throughout. The original leaves visibly close, fold inward and
stack beside the clear step; independent review of the accepted half/open views
also passed. This is a scripted key/authority render fixture;
a full playable-map OS-input bus journey remains uncaptured. Audio device
listening is unavailable in this environment.
