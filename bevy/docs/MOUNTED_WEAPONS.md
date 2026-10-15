# Source mounted weapons

The bridge hosts the original `Vehicle.TurretIndexFor`, `AimTurretAt`, `SetTurretAngles`, and `TryTurretFire` behavior for the Hind, Huey, Orca and tank. The original exported `bevy/data/vehicles.json` is embedded in the managed executable; its accepted vehicle type selects the mounts. Native vehicle registration cannot supply ammunition, weapons, cadence, muzzle positions or damage. Gun profiles load the same loose `.dat` files as `PlayerController.TurretGunDef`. The read-only `catalog.mountedWeapons` map exposes each source gun ID's numeric `blastRadius` for native cannon-flash scaling; it is available in remote sessions without creating local authority.

This is separate from the operator's held rifle. A mount owns its belt and float cooldown, and each accepted round captures that mount's immutable profile. It does not use, refill, reload or change the held inventory item. Player launch uses the source declared ballistic step count (falling back to range/2); the trusted NPC path retains its original range/2 lifetime. Both launch one round with zero gravity from the pitch frame's actual muzzle along its barrel axis.

## Intent and accepted view

`action mountInput` accepts:

```json
{"netId":123,"slot":0,"aimMode":"point","target":[0,2.8,-600],"yaw":0,"pitch":0,"trigger":true,"headOut":false}
```

`slot` is the operator's source slot, not an arbitrary mount index. `aimMode` is `none`, `look`, or `point`. The source look mounts snap to clamped ride-look degrees; crosshair mounts accept point mode and slew at their source rates. `none` retains the pose; native input uses it with `trigger:false` to release. A crosshair `none+trigger` request is refused, preserving `TurretTriggerLive`. Native code resolves the actual camera's source600m aim ray and excludes the ridden vehicle. The host still derives the muzzle and shot direction from the accepted mount pose, not the supplied target.

Every request uses connection identity and a monotonic sequence. The host checks life, accepted vehicle seat, exploded state and live slot access each tick. Own-seat mounts take priority. The driver gets `DriverFallback` mounts only while their actual seat is empty. Delayed requests cannot steal control back after a gunner boards; their owner/seat/access is rechecked when executed. Replayed sequences are ignored. A transport guard expires an unrefreshed held intent after five ticks; this is an adapter loss-of-client safeguard, not an authored fire-rate constant.

Tank `headOut` belongs to seat1 and is independent of selected mount. Departure clears it. Player firing does not require `crewAlive`: the source's `TryTurretFire` has no such gate, even though player-spawned door-gun art may remain hidden until AI crew is installed. The explicit AI path does check crew.

`state.vehicleMounts` comes from the client replication registry in both integrated and UDP sessions:

```json
[{"vehicleNetId":123,"headOut":false,"gunnerRise":0,"mounts":[{"index":0,"yaw":0,"pitch":0,"ammo":59,"cooldownSeconds":2,"crewAlive":true}]}]
```

The accepted `mountShot` fact contains `vehicleNetId`, `index`, `operatorPlayerId`, `npc`, `gun`, `muzzle`, `direction`, `tick`, `remainingAmmo`, and `sequence`. It is delivered once through the reliable event plane. Late joiners receive current angles/belts/cooldowns, with no historical gunfire replay. Save/load preserves per-vehicle source-order mount state and remaining cooldown; held trigger intent is deliberately not persisted. Origin rebasing translates queued point targets and retained muzzle facts while angles, belts, IDs and clocks remain unchanged.

## Wire and host boundary

Protocol64 appends system20 (`VehicleMounts`), command74 (`MountedInput`) and event55 (`MountedShot`). Version63 peers fail the handshake explicitly. Existing IDs and command payloads are unchanged. Mount snapshots apply complete validated row deltas and removals atomically, so a truncated tail cannot partially replace the view. The bounded registry allows256 vehicles with up to16 mounts; idle mounts are omitted from deltas. Removal churn beyond live capacity switches older baselines to a bounded complete replacement rather than an oversized tombstone list. Accepted gun strings use the existing bounded gun-name alphabet. Every aim/fact float and mount state is validated before dispatch or adoption.

`worldMountedInput` is a trusted host adapter for the forthcoming original NPC helicopter controller. It names `netId` plus the source ordered `index`, aim fields, `trigger`, optional `crewAlive` and explicit `infinite` (defaulttrue for this AI-only lane). Remote sessions cannot call it; player `mountInput` rejects authority-owned fields such as `npc`, `infinite`, `ammo`, `gun` and `muzzle`. Human firing always uses finite belts even if an AI previously operated that vehicle.

Trusted NPC bullets carry owner0, may hurt players with PvP disabled, and emit no owner hitmarker. The source player-contact path is flat `PlayerDamage*falloff`, with the original muzzle as the damage direction source, rather than the ordinary player weapon's humanoid zone/armor rules. NPC warheads preserve that PvE permission through the original authoritative blast service. These are optional trusted-host combat seams; ordinary gun, grenade and no-adapter core behavior remains unchanged.

## Accepted occupant anchors

Protocol65 appends the validated `gunnerRise` float to each mounted vehicle row;
version64 peers fail the handshake rather than decoding shifted mount fields.
The source `UpdateHatches` progress advances at3/s exactly once per50Hz host
tick, toward1 only for an occupied seat1 with accepted `headOut`. Native readers
use that accepted progress directly, including late joins; there is no second
rise simulation. Save/load retains progress, then lowers it during the existing
six load-publication ticks when no occupant has been restored.

The host prepares mount aim and hatch progress after command dispatch and before
placing vehicle occupants or stepping combat. Ordinary body origins come from
the embedded original `bodySeats`; the tank gunner uses the cannon yaw pivot plus
`TankGunnerSeatDown + Up * (0.7 * gunnerRise)`. Cannon pitch never rotates the seat.
The complete accepted hull rotation transforms the result. The player wire keeps
the resulting heading and sitting stance. This moves the existing authoritative
player hit cylinder to the rendered body origin; its existing stance geometry
is unchanged. The optional core host provider remains null in bare harnesses.

Dead, exiting and removed-vehicle occupants leave through the existing validated
seat lifecycle. A missing vehicle row now triggers that cleanup even if a host
caller omitted `OnVehicleRemoved`, preventing stranded occupancy after registry
replacement. Source seat switching is a separate subsequent integration.

## Remote flight and blast presentation

Protocol66 appends event56 (`MountedFlight`) and explicitly refuses version65
peers. The existing accepted `MountedShot` starts a remote picture using the same
embedded source gun profile, zero gravity, lifetime, tracer tint/width and rocket
flag. It never creates a remote combat round. The remote view advances original
`BallisticsMath.NextPos` only to observed server snapshot ticks; it does not
guess a local collision or extrapolate beyond the accepted clock. Consequently,
remote positions update with snapshot delivery rather than the host's50Hz body
step. Existing native `state.bullets` presentation consumes these rows.

The new reliable fact has three bounded variants. `Continue` carries the exact
source pierced position, velocity, remaining steps and pierce count. `Retire`
carries the accepted final point and impact/expiry reason. `Blast` carries the
point, source gun/radius and owner of an explosion already applied by the host's
warhead service. Identity is the launch tick plus mounted shot sequence; event
ticks survive unsigned rollover. Every variant is validated and decoded
atomically, including truncated tails. There is no client request, damage or
reward path. The remote picture holds at most1024 active rounds, discarding the
oldest picture if that presentation limit is reached.

Integrated sessions keep their existing local flight/effect events and ignore
the remote echo. Remote readers emit the same retained `bulletRetired` and
`bulletExplosion` facts used by native effects. A late join does not replay a
historical launch, but can see a newly accepted explosion afterward. Disconnect
and session replacement discard all old flight state. Accepted final actor
contacts now retire the matching host cosmetic round too: an NPC hit cannot
leave its tracer passing through a player whose capsule is absent from cosmetic
world geometry. Water continuation is transmitted without giving the remote
client water geometry or authority; detailed remote impact material/splash facts
remain a separate wire integration.

## Firing-vehicle collision correction

A playable tank cannon exposed an adapter fault: moving rendered barrel triangles
could cover the accepted muzzle between publication and the next ballistic step.
The resulting almost-zero-length contact detonated the warhead against its own
tank and killed its seated operator. Mounted authority and cosmetic rounds now
capture the firing vehicle ID at acceptance and exclude only that vehicle from
their world query for the entire flight, including after the operator exits or
switches seats. Other vehicles, solid world bodies, animals, zombies, players and
water keep their existing nearest-contact behavior. The secondary bed/door
identity lookup receives the same exclusion, so it cannot rediscover the skipped
barrel and suppress a farther accepted barricade hit. Handheld bullets, melee and
hosts without the optional mounted callback retain their previous query.

This is an explicit correction for the Bevy host's rendered-triangle collision
adapter. The inspected Godot ballistic query did not itself exclude the firing
vehicle; its muzzle placement normally cleared that body's collider. We do not
claim unchanged query semantics or alter the accepted muzzle, damage, belt,
blast radius or wire protocol.

The regression embeds a triangle from an actual second playable tank capture at
a tilted hull pose, with a documented5mm translation along its accepted barrel
direction. That controlled motion produces a contact about3.9mm from the muzzle.
It is not mislabeled as the original failed frame222: the second unshifted
capture did not reproduce that failure. Tests rigidly align the recorded triangle
to the test's accepted shot. Removing only the mounted query makes that real UDP
shot self-detonate and injure its operator. With the adapter, both authority and
cosmetic rounds pass it, retain the captured identity after exit, and hit another
vehicle80m away with one accepted remote blast. Additional regressions retain
player targets, ordinary-query compatibility, nearer world obstruction and
bed/door damage routing.

## Verification and remaining presentation

The managed tests exercise real UDP driver-to-gunner takeover and return, owner-only controls, exact source belts and muzzles, parent yaw without parent pitch, source rear-deck depression, look-versus-point gates, stale/replayed inputs, crew/player distinctions, persisted state, origin barriers and late joins. Core tests reject every truncated command/event/registry prefix, verify small row deltas among100 idle vehicles, reject old protocol versions, and hit an actual player's head-height band with an NPC round to distinguish the source flat/falloff rule from ordinary2x head damage.

Five managed flight regressions exercise actual UDP cannon flight into a wall,
one accepted blast including a late observer, water continuation against the
host's exact position/velocity history, and NPC player contact without duplicate
hitmarkers or blasts. The bounded read-only view also checks source lifetime,
snapshot-clock pacing, active-round disconnect and session replacement. Three core regressions cover all
variant round trips/truncated tails, malformed values, ordered once-only delivery,
absence of client health/combat mutations, late join and version65 refusal.

Native articulated models were rendered in the preceding vehicle checkpoint.
The subsequent local PEI tank journey now covers actual driver/gunner input,
cannon and sustained HMG fire, accepted belts, optics, head-out/rise and returning
to the driver; see `MOUNTED_NATIVE_PORT.md` for exact captures and remaining
presentation checks. The host occupant anchor and rise are also covered by
managed/real-UDP regressions above. The remote flight/blast adapter feeds the
existing native consumers; a rendered remote mounted journey remains pending.
NPC helicopter navigation, targeting and live damageable crew are subsequent
source-controller work, not implied by this launch seam.
