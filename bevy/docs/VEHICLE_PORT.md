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

## Still required for vehicle parity

This milestone is not the whole 11,264-line vehicle shell. The following remains:
helicopter/plane forces and all flight controls; rotor, propeller, landing-gear,
airframe damage and crash presentation; source procedural aircraft frames; ship
deck/superstructure collider details and riding; suspension surface-material
selection and wheel dust; swamping/drowning; damage, fire, explosions, husks,
detached wheels and tires; dynamic headlights/sirens/beacons; engine/ignition/horn
audio; towing, kingpin, sling, winch and cargo; turret weapons/hatches and optics;
doors/hood/trunk animations and storage dashboard; repair/refuel/battery/carjack
interaction tools; vehicle console/test entry points; remote-client puppets;
seat switching and fully posed seated character cameras. Far streamed vehicles
also need a parking/freeze boundary so terrain unloading cannot let them fall.

The source data for these systems remains in the export, and the original C#
authority is retained. Add each adapter against those seams, with a real physics
or authority round-trip test and rendered inspection as appropriate.
