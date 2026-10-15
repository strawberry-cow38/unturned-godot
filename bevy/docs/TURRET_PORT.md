# Original mounted weapons: native presentation

Reference: `game/Vehicle.cs` at the migration source commit, especially
`Build`, `BuildTankExtras`, `UpdateHatches`, and the four armed vehicle specs.
This document describes the geometry checkpoint. Mounted input, ammunition
authority, accepted effects and the playable tank journey are now documented in
`MOUNTED_NATIVE_PORT.md` and `MOUNTED_WEAPONS.md`. Crew characters and NPC
helicopter behavior remain separate integration work.

## Source data and frames

`tools/export_vehicles.py` evaluates the original C# declarations and exports all
39 existing fleet definitions unchanged, plus tank mesh zones, hinges, collision
boxes, hatch timing, and seat/camera constants. `VehicleSpec::turrets` consumes the
original ordered `TurretDef` rows for Huey, Hind, Orca, and tank.

Each mount has a yaw node at `Pivot`, then a pitch node at `PitchOffset`.
`ParentMount` attaches to an earlier mount's yaw node. Consequently the tank's
roof HMG inherits cannon traverse but never cannon elevation. Original mesh
Euler rotations use Godot's YXZ order and do not rotate the shot frame. The
Hind chin mesh is present at spawn; the source door guns remain hidden until
their crew is present. The native `Mounts` component has presentation angles in
degrees, crew visibility, and head-out state adopted from accepted replicas.
No native component consumes ammunition or decides that a shot is accepted.

The tank's turret mesh is split into the original main body, HMG housing,
HMG barrel, and top hatch. The driver visor is removed from the original hull
before the separate hinged copy is added. Every triangle must have all three
corners inside one source zone; corners in different zones never qualify.
Partitions preserve normals, UVs, and vertex colors. The driver's missing hinge
face uses the four original cap triangles and nearest original UV/color values.
The source treads use their original solid dark-steel material.

## Hatches and collision

Accepted driver and first-passenger occupancy close the driver/top hatches.
Empty seats open them; an occupied gunner's head-out state opens the top hatch.
The original `Fridge_0` open/close curves drive the 0.8-second motion, including
opening overshoot and clamped closing. Driver lift/slide and the source visor's
1.02 Y/Z scale are preserved. Singleplayer pause freezes hatch time; a networked
pause menu does not. The source's missing-curve fallback is reproduced as written,
including its reversed closing fallback; bundled curves are present and tested.

Turret/barrel collision uses separate kinematic bodies under the corresponding
pivots, with player-only collision filtering. Their moving shapes are never
inserted into the hull's mass/suspension body. Combat uses the actual rendered
triangles, including the peeled parts, rather than those broad movement boxes.
The vehicle geometry upload stamp includes mount angles, crew visibility and
hatch phases, so changing articulation republishes geometry even when the hull
does not move. Removing the vehicle retires all its dynamic triangles.

## Verification

Native tests build the production vehicle renderer using original content and
check triangle conservation, both turret frames, actual hatch curves and seat
transitions, crew-gated helicopter meshes, pause behavior, real Rapier barrel
queries, and managed combat rays before/after cannon traverse and vehicle removal.
The lightweight fixture registers the same Mesh3d visibility requirement as the
production VisibilityPlugin; its managed vehicle is registered through the real
`worldVehicles` path so authority rays exercise a live vehicle ID.

The opt-in `vehicle::turrets::tests::visual::render_original_mounts` fixture uses
the production builder and Vulkan renderer. It accepts `UG_TURRET_VEHICLE`,
`UG_TURRET_OCCUPIED` and `UG_TURRET_SHOT`. Fixture mount angles and occupants are
injected diagnostic state. The separate playable PEI journey uses actual input
and accepted weapon authority, as recorded in `MOUNTED_NATIVE_PORT.md`.
Validation results and reviewed captures are recorded in `PROGRESS.md`.

## Remaining integration

- Rendered multiplayer journeys across the complete armed fleet; accepted
  authority/UDP tests and the local playable tank journey cover part of this.
- Per-view muzzle billboarding for secondary scope and reflection cameras.
- Original door-gunner characters, damage and gun disappearance.
- Original NPC helicopter flight/targeting/burst/flee behavior using the same
  vehicle dynamics and mounted-weapon authority.
