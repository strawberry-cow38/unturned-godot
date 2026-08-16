# Minicopter (Rust-style) — implementation plan

VoX 2026-08-15: "fully implement a rust style minicopeter ... modeling to controls to flight physics ...
rust style helicopter controls (w for accelerating up, s for down, a and d for yaw and mouse movements to
control the pitch and roll) ... admin command to spawn ... full multiplayer support ... deploy to the test
server."

## The decision that shapes everything: it is a `Vehicle`, not a new node type

`Vehicle : VehicleBody3D` is spec-driven — one class, a `Spec` struct, a `SpecNames` table and a
`BuildByName` switch. Everything downstream keys off that type:

- `VehicleNetSync.Tick()` walks the `"vehicles"` group and mints a NetId for every `Vehicle`.
- `VehicleReplication` already carries **pitch, yaw AND roll** per entity (`VehicleReplication.cs:483`)
  and in the client-authoritative state command (`:550`).
- Enter/exit, occupancy arbitration, the hold/adopt authority split, damage, fuel, despawn — all of it
  is written against `Vehicle`.

So a helicopter that *is* a `Vehicle` inherits working multiplayer. A sibling `RigidBody3D` would need
every one of those rebuilt. `VehicleBody3D` with no `VehicleWheel3D` children behaves as a plain
`RigidBody3D`, so nothing about the base class fights flight.

**Therefore: add a locomotion mode to `Spec`, branch the physics, and leave the plumbing alone.**

## Multiplayer comes almost free — with one real gap

The driven case is already right: when a remote drives, the server *stops simulating* and adopts the
driver's reported transform (`VehicleNetSync.cs:204-213`), rebuilding a full basis from pitch/yaw/roll.
The client owns the flight sim; observers dead-reckon off the adopted entity. Pitch and roll therefore
replicate without touching the wire format.

The gap is the **anti-cheat envelope** (`VehicleReplication.cs:697-855`), which hardcodes retail CAR
limits for every vehicle:

```
ValidSpeedUpCar   = 12.5 m/s   climb
ValidSpeedDownCar = 25   m/s   fall
horizontal        = SpeedMaxMps * dt * 1.25
```

A minicopter in a dive exceeds 25 m/s trivially, and every violation ships a `recov` rollback — the
pilot would rubber-band out of every descent. Horizontal is fine if the spec's `Speed_Max` is set
honestly.

Fix: per-entity climb/fall caps, defaulted to the car constants so no existing vehicle changes
behaviour, set from the spec at `ServerSpawn` exactly as `SpeedMaxMps` already is. Server-side
validation state derived from the replicated type index — **no wire format change**.

## Controls

Rust mapping, pilot seat only:

| input   | axis                                    |
|---------|-----------------------------------------|
| W / S   | collective up / down                    |
| A / D   | yaw left / right                        |
| mouse X | roll                                    |
| mouse Y | pitch                                   |

`LastDriveInput` is a `Vector2(steer, throttle)` — enough for collective+yaw, not for pitch/roll. Since
the client-auth path carries the resulting *transform*, the extra axes do not need to go over the input
wire for the driven case. `Drive(throttle, steer, handbrake)` stays the single fallback seam
(throttle → collective, steer → yaw) for the pre-predict window.

## Flight model

Rotor thrust along the body up-axis, so tilting the airframe is what translates you — the Rust feel.
Gravity, linear drag, strong angular damping, and a mild self-levelling term so it is flyable without
constant correction. Ground contact via skid colliders; it must rest stably and lift cleanly.

## Spawning

`DevConsole` already has `vehicle <name>` spawning any spec at the look-orb, so registering the spec in
`SpecNames`/`BuildByName` gets the admin command nearly free. Placement raycasts down and seats the skids
on the surface rather than dropping it in.

## Checklist

1. `feat/minicopter` branch
2. model + collision
3. flight physics
4. controls
5. placement + admin command
6. per-type envelope caps (the one core net change)
7. tests, each teeth-checked
8. full sweep, push, deploy to the test server
