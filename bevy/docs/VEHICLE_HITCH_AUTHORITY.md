# Fifth-wheel coupling authority

This checkpoint ports the accepted relationship and paired driver transport from
`game/Vehicle.cs` (`CoupleTo`, `Uncouple`, `UpdateCoupled`) and
`PlayerController.TryToggleHitch`. It does not yet connect native Rapier joints,
landing-leg retraction, approach ghosting, or playable driving. The unfinished
native geometry remains a separate working-tree slice.

## Source rules and ownership

`VehicleHitchServices` uses original catalog fifth-wheel and kingpin positions,
transformed by the accepted full vehicle basis. A live on-foot player must focus
the trailer through the original 2.6 m ray/0.16 m endpoint-sphere/direct authored
box fallback and stand within 3.5 m of its kingpin. A world obstruction wins.
The server picks the first eligible cab in fleet registration order whose pin
is within 1.6 m; the client does not nominate a cab. Attaching aligns the trailer
pin and zeros its velocities. The same focused intent detaches it.

The original detach rules operate on accepted state: either body exceeds 50
degrees of tilt; or cab speed is above 3 m/s and the difference between the two
speed **magnitudes** exceeds 7 m/s for more than 0.15 s. Turning with equal speed
magnitudes does not trigger this rule. Wreck/removal clears the link without
inventing damage to the surviving partner. Cab running/brake lamps feed the
trailer, and the coupled cab suppresses its own tail lamps, following the
existing source lighting state machine.

One driver owns both real bodies. The host validates and adopts their reported
poses together; the service creates no second physics body. The cab retains its
existing motion envelope. A trailer has source `SpeedMax=0`, so it instead uses
the exact accepted relationship, finite bounded wire fields, and the same 1.6 m
transformed pin bound. Missing, foreign or separated pairs reject both poses and
return both last accepted transforms. Recovery acknowledgement freezes the pair
until the owner echoes the current counter. Leaving, dying or disconnecting
returns both accepted bodies to the host without detaching the hitch.

Host publications cannot overwrite a driven trailer. A server-only link
revision prevents a detach/move/relink of the same trailer ID from restoring a
raw pose cached for the previous joint; until the next accepted pair, consumers
hold its new authoritative snapped pose. Rebasing translates both raw poses,
replicas, retained recovery state and undrained recovery facts together.

## Protocol and bridge

Protocol **74** refuses protocol73 peers before they can decode the changed
layouts. Command **83** (`VehicleHitchCommand`) contains trailer ID, eye and unit
direction. System9 appends the cab's `CoupledTrailerNetId`; the inverse is derived.
This is independent of the existing rope relationship (`TowedNetId`, commands
30/31).

Command26 and Event29 append an optional `VehiclePairPose` using existing
position/angle/velocity encoders. Ordinary single-body frames gain only its
absence bit. Both readers reject truncated tails atomically. Relationships are
included in the snapshot hash; only the host relationship writer can change
them. The bridge action is `vehicleHitch` with
`{trailerNetId, origin:[x,y,z], direction:[x,y,z]}`. Existing `vehicleState`
accepts optional `trailer:{netId,pos,linVel,angVel,yawDegrees,pitchDegrees,rollDegrees}`.

Snapshots expose `vehicles[].coupledTrailerNetId` and owner
`vehicleRecoveries[]`, including optional trailer pose, for subsequent native
adoption. Reliable `vehicleRecov` remains a one-shot fact. Saves persist cab and
trailer fleet ordinals plus original type and the source rip timer; load restores
the relationship without snapping a second time or treating ephemeral IDs as
saved identity.

## Validation

- Ten core hitch cases cover full/delta/hash, malformed tails, command identity,
  prior-version refusal, atomic adoption/recovery, exit/death/disconnect,
  same-ID relink and origin translation.
- Eleven managed cases exercise actual UDP source focus/wall/refusal/order,
  initial snap, host transform protection, auto-unhook, late join/save/load,
  real triangle endpoint focus, lighting, all wreck/removal permutations and
  retained/queued recovery rebasing.
- Negative controls reproduce the stale raw trailer after rebase and the old
  pose after same-ID relink. These assertions pass with the lifecycle hooks.
- Parent and independent gameplay source reviews passed; the latter caught the
  same-ID cache edge before this checkpoint.

Final broad counts and immutable candidate are recorded in `PROGRESS.md`.
There is no native accepted-joint, native driving or coupled render claim here.
