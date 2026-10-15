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
