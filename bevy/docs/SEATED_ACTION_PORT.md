# Seated hand actions

The original `PlayerController.Fire` and `VehicleSeatTests` define an armed
passenger: a gun can fire from a first-person non-driver seat. Drivers cannot
fire their held weapon, and the third-person vehicle orbit cannot supply a shot.
`MeleeAttack` rejects every vehicle seat. Furniture does not block hand actions.

The selected source has conflicting shell wiring: all vehicle LMB presses go to
the horn except turret seats; its physics tick returns before ordinary weapon
and furniture-use timers; its viewmodel visibility hides passengers. The native
port repairs these input/timer gaps to satisfy the tested source feature. It does
not copy a nonfunctional early return as the gameplay rule.

## Authority

`ServerPlayerAuthority.AcceptSeatedActions` is an optional host adapter, false by
default. When enabled, an alive seated sender's existing state packet updates
held-item identity and dispatches its deduplicated combat carry. All claimed
position, rotation, velocity, stance and grounded values remain ignored. The
seat remains the sole position authority. Combat ACKs retire queued shots, and
old falling/sprinting/steadying state does not survive the seated carrier.
Accepted exits use the optional `RepositionExitedOwner` hook into the existing
recovery handshake. Old seat packets cannot overwrite the door position while
its reliable event is in flight; ordinary walking resumes after the owner echoes
the recovery counter. There is no new packet type or protocol version.

The Bevy bridge enables this seam and keeps sending the state stream aboard.
Its original weapon adapter refuses driver gunfire and all vehicle melee.
Passengers consume ammunition and reload actual owned magazines through the
same `ServerCombat` and inventory helpers as walking players.

## Native shell

The movement solve is skipped for seats; controller translation remains empty.
Hand actions and their accepted timers run outside that movement branch.
Passenger guns use the actual seated camera eye and direction, including the
seat's world rotation. Ordinary melee, consumables, throwables and repeated
repair tools remain blocked aboard vehicles. Furniture allows those actions
without moving its seated body; aim/throw/melee directions include the chair's
orientation. Driver horn/flight controls stay in the vehicle controller.

Moving from the driving seat to a passenger restores the visible, ready held rig
immediately. Passenger ADS works; driving hands remain on the wheel. Seated
movement keys cannot produce walking bob or sprint carry. The original scope
steady host gate still excludes seated players.

## Checks

- Two opt-in/default core cases plus the existing player-authority and passenger
  suite: 17 passes; the full network suite passes 638 tests. The carrier test
  sends hostile pose/velocity/recovery fields,
  checks unchanged seat position, one dispatch, current held identity and a
  retired client combat queue. A separate exit test keeps stale seat packets
  flowing until the new recovery is acknowledged.
- Two actual bridge journeys: driver refusal, passenger fire, magazine-conserving
  reload, vehicle melee refusal and accepted furniture melee. Full bridge suite:
  98 passes.
- Two native fixed-step journeys through the actual managed executable check
  passenger camera direction and driver/orbit refusal, chair fire/melee and
  unchanged body/controller transforms.
- The real-rig integration regression checks driver hands, passenger visible hold,
  readiness, ADS and exit. Current full native suite: 265 passes.

Visual passenger/chair action verification remains pending the combined native
weapon/character checkpoint. Seat switching, mounted weapons and further vehicle
specializations retain their separate parity tasks.
