# Accepted native power-switch foundation

The retained `DeployableReplication` graph already owns wire topology and
health/fuel/toggle state. Both authority and replicas use the original
engine-free `PowerSolver`; native rendering must consume accepted state.

The original fuel-free Power Switch9105 now carries `IsSwitch` through the
shared def table, starts ON as `Deployable._switchOn` does, accepts the existing
validated toggle intent, and maps its accepted bit to solver conduction. An
off switch stops all downstream consumers and their generator draw. Burning
switches and ordinary fuel-free consumers still refuse manual toggles. No
message bytes, wire IDs or save fields are added.

The native source indicator is the original0.12×0.06×0.12m cube, attached to
the deployed body root at `UG_SWLP` or `(0,0.20,-0.14)`. Its original green/red
colors and1.6/0.8 emission follow the accepted bit; sending a request does not
locally change its material, body or inventory. It shares the ordinary source
focus/tap system and info canvas.

`powerView` is a read-only bridge operation available to local and remote
clients. It returns the accepted client graph's wire endpoint identities and
derived per-port live/powered/draw values using the existing solver. It neither
ticks simulation nor changes server topology/scalars. Catalog definitions
remain the source of port positions/kinds/ratings. The view is a foundation for
native wiring and powered-device presentation, not evidence those flows are
already playable.

Validation: immutable `power-switch-v74-first` passes733 core networking tests.
The new actual UDP managed case verifies source schema, downstream off/on,
fire rejection, remote view availability and no authority/tick mutation. All
228 managed cases pass across the broad225 run and the three corrected fixture
reruns: the initial private snapshot omitted two existing native reference
files, without any product-code failures. Removing only switch admission,
conduction, or default-ON behavior makes the new core regression fail in each
case. All128 JSONL integration tests and nine existing native deployable tests
pass on the frozen backend; the initial native candidate is
`native-power-switch-first`.

The opt-in actual-UDP native renderer also passes on
`native-coupling-pair-first` with that same immutable backend. Three opened
`bevy-switch-accepted-{0,1,2}.png` captures show the full source panel and green
Turn Off → accepted red Turn On → green Turn Off, with matching accepted bits
and exact source colors recorded in `bevy-switch-accepted.json`. It exercises
the production focus/release path with injected ordered edges. Exit is0 and
independent visual review passes; this is not an OS-input or full power-network
claim. The fixture uses70° main-game field of view and an independent edge
counter so screenshot callback timing cannot skip its press edge.

Remaining work includes data/radio and specialized-fixture ports, full trigger/data
semantics, generator ramp/fuel authority, battery/wind/output-budget inputs,
consumer lights/audio and complete utility readouts. The old Godot
`Deployable.NetSetPowered` still adopts `_powered` rather than `_switchOn`;
that legacy engine adapter is distinct from the corrected Bevy consumer.


## Accepted wire geometry

The native consumer now draws the original `DeployableReplicaView` two-endpoint
wire between represented ordinary device sockets. It uses `Wire.SetPoints`'s
0.018 m radius, six radial segments, dark `(0.06,0.06,0.07)` material,
metallic0.2/roughness0.6 and local-Y rotation-before-length scaling. Tiny edges
below0.0001 m stay hidden. The endpoint uses the original port's body frame,
outside `MeshEuler`, including original spotlight/generator tuning overrides.
No collision body is added: source wires are non-interactive.

`powerView` now identifies version1/readOnly and the accepted client state hash;
the same hash is exposed in normal snapshots. The renderer fetches the view only
when that accepted hash or the session epoch changes. View failure retires stale
geometry and invalidates the cache key, so A→failed B→A can refetch correctly.
Accepted removal, missing endpoint bodies, disconnect and epoch changes retire
geometry. Sending connect/remove intents does not draw/remove a wire before the
accepted graph arrives.

This is wire presentation only. Ports/arrows, wire-tool routing and management
input, specialized fixture endpoint transforms and data networks still need
native consumers. A graph edge with an unrepresented endpoint is omitted rather
than attached to an invented position. Tool preview routing points are separate
from the actual unified game's accepted two-point wire.

Validation: `wire_reference.py` compiles unmodified original `Wire.cs` on real
Godot and captures10 route/pool cases in `data/wire-source-reference.json`.
The native comparison guards full scaled bases, including exact near-vertical
threshold behavior. Frozen `power-wires-v75-first` passes the actual UDP managed
metadata/authority case and45 bridge JSONL tests. `native-wire-reviewed-final`
passes12 deployable cases, including accepted-only connect/remove and failed-view
cache recovery. Its actual UDP Vulkan fixture passes/exit0 and the three opened
`bevy-wire-accepted-{0,1,2}.png` captures show absent → connected → removed wire.
The fixture uses existing JSONL intents; it does not exercise native wire-tool
input. No existing server command/event/snapshot bytes changed in this slice.


## Native wire tool and source ports

Holding the original wire tool now shows the source `ConnectionPort` cubes on
every represented ordinary device socket, with in/out arrows while the tool owns
captured on-foot input. Cube/arrow geometry, the original texture, all
Focus/Good/Bad material states and the axis billboard are guarded against
`data/port-source-reference.json`, captured by `tools/port_reference.py` from
unmodified `ConnectionPort`/`AxisBillboard` on real Godot.

Input follows `WireLmb`/`WireRmb`/`UpdateWireManage`/`UpdateWireLook` on the
shared ordered binding seam: a 5.5 m port-only ray that ignores ordinary
occluders; start from either end of a usable unwired port; 6 m node placement
with the source bits0|6 world/prop predicate (`player::world_or_prop`, shared with
throw clearance); the 20-node/40 m budget and `-- LIMIT`; RMB undo/cancel while
routing; and RMB tap (≤0.28 s) / hold (1 s) removal on a wired port. Completion
follows `CanCompleteWire` (target usable, other device, unwired, compatible
pair) and orders source→consumer. The client never edits the graph: connect and
remove are `connectWire`/`removeWire` intents, and sockets, highlights and wires
reflect only the accepted `powerView`. The HUD label is a separate layer-40
camera between the dashboard and modal canvases, with the original 26 px font,
600 px minimum box and 1.5 px outline.

Source-visible behaviours ported deliberately, all found by independent review:

- Cubes are sensors on their own layer with filter = layer (source PortLayer
  bit8, `CollisionMask = 0`). They take no contacts and the ordinary look ray
  passes through them; only the port-only ray selects them.
- `UpdateWireManage` writes `clearing wire... N%`, but `UpdateWireLook` runs
  later in the same `ProcessTick` and always replaces it with the info line, so
  a held clear shows the info line plus the RMB hint, not a percentage.
- The route survives a start port retired at 0 HP or on fire
  (`KillPowerHardware` only deactivates the cube). It drops only when the start
  device despawns; the server refuses completion from a retired port.
- No focus-loss cancel: the source has none, and an unfocused window already
  makes the tool inactive.

Validation: the 20 deployable tests pass, including three review regressions.
Each regression fails under a surgical revert: an ALL-filter non-sensor socket,
the old retire-cancels-route rule, and the percentage readout. Rendering ran
`deployables::ports::tests::visual::render_source_ports_routing_accepted_connection_and_hold_clear`
against backend `.local/power-wires-v75-first` under Xvfb/lavapipe, with the
window focused by xdotool (the tool requires a focused window, as production
does). It passes, and all six `bevy-wire-tool-{focus,route,target,accepted,clearing,removed}.png`
were opened.

The first capture showed old labels overprinting. The fixture's SDR world camera
did not share the HDR label camera's main texture, so the never-cleared label
target accumulated. Production `HdrPlugin` makes the player camera `Hdr`, and
the fixture now matches that. The label camera also deactivates when no label is
shown and recovers if a teardown despawns it. Input is injected ordered edges
with real local-server acceptance, not an OS journey. A label wider than 600 px
grows rightward, as the source label does, and is clipped at a 1100 px window.

Remaining: data and radio ports never read live (the server's `CanConnectWire`
also rejects data pairs), the radio channel suffix is missing, and only ordinary
deployables get sockets (no wind turbine or powered fixtures).
