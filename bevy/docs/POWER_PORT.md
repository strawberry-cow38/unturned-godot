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

Remaining work includes native port geometry and wire/port tools, full trigger/data
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
