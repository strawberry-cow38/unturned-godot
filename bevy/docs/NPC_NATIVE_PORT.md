# Native helicopter flight

`vehicle::npc::NpcFlightPlugin` connects the accepted original `NpcHeliBrain`
controller to the existing `VehicleBody` and `Flight::step`. It applies the four
source axes on the 50 Hz physics schedule. The three-state collective feeds the
existing throttle input; there is no additional aircraft transform integrator.
Human drivers retain control, occupied gunner seats remain the managed host's
responsibility, and a remote observer never runs this controller.

Original NPC startup warms the rotor and bypasses the ignition delay once per
body/session. Subsequent snapshots cannot refill rotor health or restart a
stopped engine. A managed aircraft remains physical outside the player's local
collision window, including an unpowered or exploded airframe. Ordinary parked
vehicles retain their existing streaming behavior.

The native host uploads complete source height grids, original named-node files
and their union bounds through `worldNpcHelis`. Height sampling is independent
of nearby triangles. Grid signatures avoid re-uploading unchanged heightmaps on
every local streaming revision; a map/session change or origin translation
invalidates registration. The Information map uses the same node-file selector.

Outside ordinary terrain collision, supplemental static shapes reuse original
terrain cells around managed aircraft. Cells inside the player's existing
terrain collider and cells shared by multiple aircraft are excluded. These
shapes are retired on removal, session changes and stream replacement. This
does not extend distant prop or tree collision; that remains a separate world
streaming concern. The source controller normally flies 50 m above terrain.

## Validation

Four native integration tests pass against the immutable
`bevy/.local/npc-brain-v66-final/UnturnedBevy.Bridge.dll`:

- An actual managed original controller drives the one Rapier Hind for 2,500
  physics ticks (50 seconds), reaching arrival and orbit while maintaining
  flight outside the player's 100 m fixture window.
- Human driver, pause, remote ownership, malformed axes and one-time rotor
  startup do not create competing control or reset state.
- An unpowered airframe settles on supplemental original terrain and stays
  supported. The assertion uses the offset collider's lowest world corner;
  the source hull root can correctly lie below the terrain surface.
- Complete native height-grid registration supplies the original console spawn
  planner and altitude controller without relying on nearby collision triangles.

The same frozen executable passes all 70 nonvisual vehicle tests (five explicit
render fixtures ignored). `bevy/.local/native-npc-verification-v1` and
`bevy/.local/native-npc-playable-v1` freeze this boundary. The managed controller
contract and its source-level checks are in [NPC_HELICOPTERS.md](NPC_HELICOPTERS.md).
Door crew presentation and damage have separate validation boundaries in
[NPC_CREW_PORT.md](NPC_CREW_PORT.md).

## Full game capture

The complete PEI application was run with the frozen native executable above
and the subsequent immutable `npc-crew-v67-final` bridge. The original console
commands `teleport -1980 102 -70` and `spawnheli hind` selected Confederation
Bridge and spawned the aircraft at the original map edge. No fixture supplied
the aircraft's controls, target or trajectory. The diagnostic camera was fixed
at `UG_INF_CAM=-2014,145,-358,-48.5,-19.5`; player teleport only places the observer.

Both PNGs and their accepted-state sidecars were inspected:

- `bevy/artifacts/bevy-npc-hind-inbound-verified`: tick 234, position
  `[-1983.3438,134.5586,-382]`, engine on, health 12,500, source transit/patrol.
- `bevy/artifacts/bevy-npc-hind-progress-verified`: tick 286, position
  `[-1967.0234,130.48047,-382]`, same living aircraft with changed attitude and
  source controls. The complete terrain sampler supplies a desired height near
  136 m at this part of the route.

These captures establish console spawning and actual rendered autonomous flight.
The longer arrival/orbit path is established by the 50-second physical test, not
these two images. Aircraft combat, crew deaths during flight and a full native
remote observer require their own gameplay captures. The v67 capture's
`seatCount:0` is a separate snapshot metadata bug: the original wire omits the
host-only field; native seating uses the original fleet definition. It does not
mean the host registered a zero-seat Hind.
