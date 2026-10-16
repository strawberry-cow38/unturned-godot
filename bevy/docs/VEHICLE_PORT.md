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
  The anti-roll bar applies a pure torque even with one wheel airborne. Its
  reading is the source `WheelExtension`: a bit0 ray from the wheel node, which
  Godot keeps at the suspended centre. A grounded wheel therefore reads 0, and
  only a lifted wheel's air gap drives the bar. A `Park` latch mirrors source
  `_parked`: set at spawn and on uncouple, cleared by a driver or NPC, coupling,
  a vehicle ram or the end of a remote driver's hold. A driver's exit does not
  park. The bar also skips exploded and settled hulls; see
  `VEHICLE_COUPLING_NATIVE.md`.
- Original voxel buoyancy, mass-scaled water damping, steady hull heave/roll
  damping and boat propulsion/rudder. The flat native sea matches current source:
  `WaveField.Flat = true` forces both the visual field and physical height to zero.
- Authoritative occupancy drives player body/camera through public `Occupancy`.
  Driver ignition/lights stream as owner flags. Door focus joins the shared
  nearest-interaction collector. Exit tests the own-door chest path and standing
  capsule, then submits the trusted host verdict before sending an exit intent.
  Centreline, rolled, bi-fold and helm door selection follows `VehicleExit.cs`.
- Original trunk, glovebox and seat-pocket dimensions/labels are registered with
  the managed `ServerVehicleStorage`. Accepted trunk/cabin grids now render in
  independently scrolling inventory columns and persist with their vehicle;
  see `VEHICLE_STORAGE_PORT.md` for the source lifecycle and remaining layout gap.
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
- Original steering parts turn around the declared pivot and normalized disc axis;
  the same public transform is available to seated hand anchors. Dedicated semi
  steering/seat meshes retain source AABB-centre placement.
- Engine, starter and horn clips use source ranges, RPM/spool curves, rotor-size
  pitch, and master volume. Car drive torque and idle sound wait for the actual
  starter clip; stopping winds pitch/volume down for 1.4 seconds and removes the
  stream instead of leaving inaudible decoders running.
- Engine and main/tail rotor health adopt authoritative persistent pools. Source
  land-vehicle swamp voxels retain trapped air for five seconds, bleed lift over
  four, and keep water drag thereafter. Drowning sends a trusted water-contact
  fact, permanently disables the engine, and switches off drowned electrics.
- Accepted popped-tire masks hide the source outer radial split, leaving real rims
  turning. Radius becomes 78% of stock and grip 35%; replacement restores the
  original per-wheel radius. Moving hit geometry republishes when the mask changes.
- Accepted explosions launch the single native body once, collapse suspension,
  char all mesh descendants and remove rotor/wheel presentation. Detached original
  wheel meshes use 18 kg sphere bodies, source kick ranges, ten-second lifetime and
  independent last-second fade. Native simulation clocks stop during pause, and
  explicit session epochs reset geometry/presentation on every load.
- Native particle meshes render original light/heavy damage smoke, four-frame
  flame atlas, independent rotor smoke/fire and authored exhaust outlets. Rates,
  sizes, lifetimes, rotor thresholds, cold-start/rev steps and the 40–60 second
  wreck fade follow source. Existing particles drift in world space; smoke meshes
  are excluded from combat geometry and charred-hull material replacement.
- Physical rotor cylinder overlaps exclude the own body and stopped blades;
  source 0.22-second strikes send rotor and destructible damage together to the
  managed host. Accepted contacts trigger source spark/debris bursts and metal
  audio. Crash detection preserves horizontal car impacts, full-3D aircraft
  impacts, decaying approach speed, spawn grace and cooldown. High-speed aircraft
  crashes request authoritative immediate explosion, while ordinary damage keeps
  the original four-second fuse. Recovery resets the detector's velocity history.
- Floating-origin changes shift cached spawn/seat positions once and invalidate
  uploaded geometry without touching the already-shifted physical Transform.
  Explicit `--vehicle` captures support original `UG_TIREPOP` through accepted
  host damage; regular map spawns do not read this testing variable.

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

Further native tests exercise drowning, the actual wheel/hand transform agreement,
source tire mesh separation, ignition audio timing, stopped decoder lifetime, and
accepted-explosion propagation into real Rapier velocity and rendered materials.
The explosion test caught a first-frame impulse lost before Rapier creates a body;
the adapter now retains that accepted impulse until its physical handle exists.
All 29 focused vehicle tests passed. `bevy-jeep-tires.png` was captured from the
fresh Vulkan binary and opened: the full Jeep, actual tread/rim split, interior
seats and steering wheel are visible on the original PEI slope, with no asset or
renderer errors.

The subsequent 38-test vehicle suite passes, including real rotor overlaps,
vertical aircraft crash publication, accepted-contact FX, origin cache shifts,
and loaded wreck cooling. `bevy-huey-rotor.png` was captured after entering the
pilot seat and issuing the original authoritative `killmain` command: fire and
smoke appear at the destroyed main hub while the tail and hull remain healthy.
`bevy-jeep-popped-fixed.png` shows the accepted tire mask 9, a bare rear rim and
retained front tread. The first tire capture revealed an unattended vehicle
rolling down the PEI slope; the original all-wheel parking brake was missing.
A real Rapier slope regression failed with 26m of sliding before the fix and
passes with the source mass-scaled brake restored. The final capture keeps the
Jeep parked in frame. Both final Vulkan captures were opened and inspected; no
live renderer or bridge errors occurred. The consolidated native suite passes
all 142 tests at this checkpoint.

## Still required for vehicle parity

Subsequent player checkpoints added the original driver wheel-arm pose, passenger
pose, mesh-bounds chase cameras, aircraft chase rules and accepted first-person
passenger gun actions; see `AVATAR_PORT.md` and `SEATED_ACTION_PORT.md`. The
blowtorch's accepted repair path now restores hull and proportional engine/rotor
pools, and native effects adopt those authoritative values. These do not imply
completion of the remaining vehicle controls below.

This milestone is not the whole 11,264-line vehicle shell. The following remains:
aircraft turbulence; ship
deck/superstructure collider details and riding; suspension surface-material
selection and wheel dust; husk settling;
dynamic headlights/sirens/beacons; towing, kingpin, sling, winch and cargo;
turret weapons/hatches and optics;
doors/hood/trunk animations; refuel/battery/carjack
interaction tools; remaining vehicle console/test entry points; remote-client
puppets; seat switching and turret-specific seated poses/cameras. Jet afterburner
and contrail shader effects are still pending.

The source data for these systems remains in the export, and the original C#
authority is retained. Add each adapter against those seams, with a real physics
or authority round-trip test and rendered inspection as appropriate.
