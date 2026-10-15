# Remote aircraft presentation

Remote aircraft now advance the original `Vehicle.StepHeli` / `StepPlane`
spool, ignition latch and rotor/propeller phase clock. The Godot
`VehiclePuppet` builder and `VehicleReplicaView` only dressed road wheels;
they did not provide this aircraft presentation path. The native port reuses
the original physical aircraft's cosmetic formulas instead of reproducing that
inherited multiplayer gap.

`Flight::advance_presentation` is called once per 50 Hz step. The existing
physical flight adapter calls it before forces; the remote-body branch calls
only this method and its engine-audio clock, clears force/impulse, and returns.
Changing the accepted local driver therefore transfers the same presentation
state between branches without a second display-frame step. No remote fuel,
collective, AI, transform authority, collision damage or thrust is simulated.
The existing singleplayer pause gate remains in front of both paths; a network
pause menu continues to render the live world.

The retained source constants are helicopter startup length ×0.74 (or 3.2 seconds
for the instant-start/default case), plane startup 3.2 seconds, rundown 5.5 seconds,
main rotor 46 rad/s, tail rotor 120 rad/s, and propeller 150 rad/s. Each helicopter
rotor multiplies its phase rate by `0.30 + 0.70 * healthFraction`. Explosion
freezes all blade phases immediately. Actual original blade/disc meshes still
use their existing hub transforms and plane 0.35 spool visibility threshold.

Aircraft starter audio now follows the source ignition latch: start below 0.05
RPM fraction and rearm only when switched off below 0.01. Turning an engine off
and on while the disc is still spinning no longer invents another startup
clip. Cars retain their separate existing starter clock. The original aircraft
engine loop follows spool pitch/gain and is removed after spin-down; an
accepted explosion removes it immediately. An already-running host NPC starts
warm through the original `SpawnRotorRunning` path without cranking.

## Validation

Source clock tests independently check the three rates and damaged-rotor
factors, compare the physical and presentation paths, and verify cold start,
mid-spool restart, full-stop rearming and warm NPC behavior. Native tests use
real Rapier bodies, authored Huey/Otter mesh hierarchies and actual audio asset
handles. They check stopped/running/rundown/dead transitions, one-step ownership
handoffs, pause behavior and the absence of remote forces or fuel consumption.
An additional real UDP host/peer test feeds the production replica consumer and
checks accepted engine flags, engine-loop creation/removal and vehicle teardown.

The opt-in `render_udp_aircraft_start_run_and_stop` fixture uses a trusted
stationary host to switch the source aircraft's engine. Its separate real UDP
peer feeds `sync_vehicles`, the fixed remote branch and the original mesh/audio
consumers. It is not a claim that two native players have flown together.
Set `UG_REMOTE_AIRCRAFT=huey|otter`, `UG_REMOTE_AIRCRAFT_SHOT=<prefix>` and an
immutable `UG_BRIDGE_DLL`; run under Xvfb/Vulkan with `WAYLAND_DISPLAY` unset.
It captures off/running/stopped PNGs with accepted state, local phases, spool
and actual engine-loop asset/pitch in matching JSON sidecars.

Validation used the immutable `npc-crew-v67-final` bridge and
`native-remote-aircraft-final` executable: fourteen focused flight/audio/remote
tests passed, including six new regressions. Both Vulkan fixture runs passed,
and all six `bevy-remote-{huey,otter}-{off,running,stopped}.png` frames were
opened. The Huey changes its main/tail phases while retaining real blades; the
Otter changes blades to its original blur disc and restores them after rundown.
Their JSON sidecars show accepted flags 0→1→0, spool 0→0.3704/0.6938→0, one
ignition and the original `heli_engine.ogg` / `engine_plane.ogg` loop only in
the running capture. No shader or renderer validation errors were reported. The parent independently
reviewed the shared clock/code and opened all six final images; both reviews
passed.

## Remaining boundaries

The current vehicle wire carries engine/explosion flags and hull health, but
not spool age or blade phase. A newly observed aircraft winds up using its
local source clock; exact late-join phase synchronization is not claimed.
Main/tail health is currently enriched by the host-only managed service, so a
remote peer still defaults those unreplicated pools to healthy. The shared
clock supports the proper per-rotor slowdown once those facts are available.
Remote collective/afterburner and landing-gear state need their own accepted
wire fields; this change does not infer them from velocity. Audio tests verify
the original clip and playback graph, not loudspeaker output in this displayless
environment.
