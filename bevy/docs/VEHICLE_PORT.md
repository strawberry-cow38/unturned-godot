# Native vehicle port

The native client uses one Rapier body per vehicle. The original managed server
owns the occupancy and inventory registries, validates driver transforms, emits
recovery/enter/exit facts, and retains save data. Host bodies publish unattended
poses through `worldVehicleState`; the local driver uses the existing
`SendVehicleState` command. Normal replicas never snap the host's physical body.

## Implemented

- `tools/export_vehicles.py` evaluates the original `Vehicle.cs` data declarations
  with a small .NET harness. `data/vehicles.json` retains all 39 stable type IDs,
  complete specs, real prefab seats, body offsets, access zones, and 256 original
  deterministic paint variants per type. The source SHA identifies the input.
  The exporter follows `BuildByName`: the source's separate `SpecFor` lookup
  accidentally omits Otter/Jet, and using it would silently produce jeep data.
- Source `Spawns/Vehicles.dat` tiers, weighted high-bit selection, land height,
  water height, yaw conversion and spawn-index paint variants. Registration adds
  original points as collision regions stream in; it does not invent a starter car.
- Native mesh/palette paint, parts, glass when supplied, and wheel visual phase.
  Shipped original convex `.hulls` files are read directly. Missing/stale bakes
  decompose asynchronously with source car concavity/hull count; Parry's per-axis
  voxel resolution uses the cube root of Godot's total voxel budget.
- Mass, centre of mass, load-scaled suspension, actual driven axles, source gear
  construction, torque/RPM/shift/clutch curves, reverse-pedal braking, steering
  fade/stability limit, rear handbrake and tracked differential steering.
  The anti-roll bar applies a pure torque even with one wheel airborne.
- Original voxel buoyancy, mass-scaled water damping, steady hull heave/roll
  damping and boat propulsion/rudder. This currently samples a flat native sea.
- Authoritative occupancy drives player body/camera through public `Occupancy`.
  Driver ignition/lights stream as owner flags. Door focus joins the shared
  nearest-interaction collector. Exit tests the own-door chest path and standing
  capsule, then submits the trusted host verdict before sending an exit intent.
  Centreline, rolled, bi-fold and helm door selection follows `VehicleExit.cs`.
- Original trunk, glovebox and seat-pocket dimensions/labels are registered with
  the managed `ServerVehicleStorage`; rendering those grids is a separate UI task.
- All seven rotary-wing definitions now run the source collective/spool, original
  ignition clip gate, rotor-size pitch timing, hub-height ground effect, ETL,
  anisotropic drag, shaft-aligned descent, envelope backstop and inertial cyclic.
  Fixed-wing flight keeps sticky throttle, airflow AoA/stall lift, dive recovery,
  bank compensation, weathervaning, runway rotation, taxi/reverse and float drag.
  Actual Godot COMBINE damping is preserved, including the otherwise easy-to-miss
  project-default contribution. Mouse/arrow cyclic, independent pitch inversions,
  free look and the manual landing-gear toggle use persisted controls.
- Source main/tail blade meshes spin on their original hubs. The tail's 90-degree
  roll is composed with phase; the propeller rotates about its actual mesh centre.
  Retractable gear folds on the original hinges and removes wheel support halfway
  through retraction. The source scoutcopter pod primitives, jet missiles/control
  stick and original gear meshes are present.
- `--vehicle KEY` explicitly registers any fleet member beside the real player
  spawn through the same authority path. Spawn distance includes the actual rear
  extent, so a large aircraft's tail does not envelop the player. Normal launches
  retain only original map spawns. Unattended bodies freeze outside loaded terrain
  collision and wake when that region returns.
- Moving render triangles upload to the managed dynamic BVH under a stable vehicle
  key, with vehicle/subpart tags. Despawn removes the subtree; terrain replacement
  republishes it. Native pose publication omits untouched vitals, preventing stale
  snapshots from undoing authoritative bullet damage.
- The original passenger validator/event defects are fixed without a packet-format
  change. Occupied driver seats no longer reject a free passenger seat; reliable
  enter/exit facts preserve driver identity; every occupant follows the vehicle,
  while passenger movement, drive input and console teleport remain rejected.

## Verification

The native tests run a real Rapier world: four-wheel contact, forward acceleration,
opposing-pedal braking, correct-sign turning without overturning, and runabout
buoyant equilibrium/recovery after a downward impulse. Additional tests cover
source fleet IDs, tier weighting, bad input, nullable passenger snapshots, existing
hull bakes, flattened decomposition compounds, and blocked-door/standing-person
exit rejection. These exercise the adapters, not only exported counts.

The first actual PEI render caught a Parry panic from nesting a decomposition
compound inside the extras compound, plus a missing parent Visibility component.
The compound composition now flattens leaf hulls and has a regression test;
vehicle roots supply inherited visibility. `bevy-vehicles-smoke.png` was rendered
and inspected after the fix: the shoreline world and HUD are valid, with eight
authoritative vehicles settled by tick 452 in the adjacent snapshot. Close-up
vehicle interaction inspection continues separately. The capture also exposed
per-pane glass files; the loader now follows the source pane labels and single
canopy fallback, with a content-backed regression test.

Flight verification runs every rotary-wing definition through actual Rapier
climb and hands-off descent and checks the authority envelopes. Independent
helicopter/plane traces pin pitch, roll and rudder direction. Tests also decode
the real ignition audio and verify its thrust gate, check cushion-aware idle,
virtual stick decay/inversion and dive/stall lift. The parking test unloads the
terrain under a car and restores it, verifying both retained height and regained
four-wheel support. Forty managed vehicle/player-authority tests pass; all three
new passenger regressions were demonstrated failing before the core fix.

`bevy-huey-three-quarter.png` and `bevy-huey-fixed.png` were rendered and opened:
the complete source Huey, original main/tail blades, cabin and shadows are visible
and grounded on the PEI slope. The first close-up exposed a harness placement
inside the tail; the extent-based clearance is the verified correction.

## Still required for vehicle parity

This milestone is not the whole 11,264-line vehicle shell. The following remains:
aircraft turbulence, rotor strikes, airframe damage and crash presentation; ship
deck/superstructure collider details and riding; suspension surface-material
selection and wheel dust; swamping/drowning; damage, fire, explosions, husks,
detached wheels and tires; dynamic headlights/sirens/beacons; engine/ignition/horn
audio; towing, kingpin, sling, winch and cargo; turret weapons/hatches and optics;
doors/hood/trunk animations and storage dashboard; repair/refuel/battery/carjack
interaction tools; remaining vehicle console/test entry points; remote-client
puppets; seat switching and fully posed seated character cameras. Jet afterburner
and contrail shader effects are still pending.

The source data for these systems remains in the export, and the original C#
authority is retained. Add each adapter against those seams, with a real physics
or authority round-trip test and rendered inspection as appropriate.
