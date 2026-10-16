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

Most of the remaining work at this point (fixture ports, triggers, fuel
authority, battery/wind inputs, consumer lights/audio, utility readouts) is
closed by the protocol 78 section at the end; data/radio stays refused for
parity, and the generator ramp authority is still open. The old Godot
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
also rejects data pairs) and the radio channel suffix is missing. Protocol 78
(below) adds the wind turbine and the grid/pump fixture sockets.


## Accepted power inputs, fuel loop, turbine, lamps and fixtures (protocol 78)

The original unified game ran battery charge, turbine wind and fuel burn on each
client's replica `Deployable` nodes. The server never saw them: its solve called
every battery and turbine unpowered (DUPLICATE_AUDIT 2.1/2.4), and the replica
rewrote node fuel each tick from an entity nothing burned, so a generator never
ran dry. The 12.2 health grid also clipped the 150000 mL tank to 2047.75 mL, so
a fresh generator showed a 1.4% fuel bar. The port now owns these inputs on the
server and the native client renders only accepted state.

- `ServerPower` (core, host-stepped before the item movers, like them absent
  from a bare harness) ports `Deployable._PhysicsProcess` in source order:
  TurnOn/TurnOff side triggers fed at least 1 W (a switch takes the trigger's
  state; a generator starts only with fuel and stops unconditionally), the
  generator's `100 × (0.2 + 0.8 × load)` mL/s burn with its dry cut-out (the
  toggle drops and needs a manual restart), battery OUT discharge then IN
  charge at 600 W up to 600 Wh, and turbine wind `min(2, wind × (1 +
  clamp((y − sea)/40, 0, 1)))`. Ports are the source's last Consumer/Output.
  The solve is cached by graph version. Triggers settle to one final state per
  device per tick and replicate through the snapshot, not an event per flip.
  Wind publishes only when it moves more than 0.04 (the source
  `_lastWindDirty` resolution), so calm-air noise does not churn the graph.
  `UG_GENBURN` tunes the burn as in the source, parsed culture-invariant.
- The server wind sample is `SourceWindField`, a transcription of the
  FastNoiseLite OpenSimplex2S/FBm path used by Godot (seed 1337, frequency
  0.0025, 2 octaves) plus the clamped weather floor, on server seconds instead
  of process uptime. It matches three actual Godot samples and 20000 crate
  samples bit for bit. `UG_WIND` remains the source's forced-wind override; the
  client graphics wind option cannot zero authoritative output.
- System6 entities carry Fuel on a signed 19.2 grid and presence-bit battery
  Energy (u22.2) and turbine Wind (u2.8). Values are quantized at store, so
  replicas hash identically; exact server-only accumulators keep sub-grain
  burn/charge. Producing is per family (battery: charged; turbine: wind above
  0.03; others unchanged), and Solve scales turbine output by wind. Battery
  charge survives world saves (save v3). A v1/v2 save stored every fuller tank
  at the old 2047.75 ceiling, so those generators restore full; lower tanks
  are exact. `powerStateHash`/`powerView.stateHash` are now a solver-input key,
  so per-tick fuel or charge does not refetch the view.
- Deliberately unchanged: the generator's 1.3 s/1.1 s spin-up stays client
  presentation. The unified server and its own Godot tests treat a toggled,
  fuelled generator as producing at once; scaling it server-side would change
  the reference. Visible effect: a heavy load connects about 0.1 s earlier and
  a lamp starts fading about 1 s earlier on turn-off than the client-local
  source solve. The generator half of DUPLICATE_AUDIT 2.1 stays open.

The fuel loop, since a generator can now run dry:

- Command 90 `DepositFuel` is `TryDepositFuel`'s generator half. LMB with a held
  fuel container on a focused generator names the exact held instance (address
  plus identity token). The host pours `min(can, free tank)` into a live,
  non-burning generator within 16 m; a replacement can at the same address, a
  full tank or an empty can refuses. The owner echo empties the can.
- Pump extraction uses the existing Command 29. RMB with a held fuel container
  on a focused gas pump sends it; the host fills the sender's first fillable
  can from the shared 8000 L station tank and fans the new percent onto every
  pump of that station. The inventory jar's fuel level widens 12.2 → 19.2 under
  v78, because the 12.2 grid echoed a full 20 L can (50 L industrial) as 2 L.
- Source quirk kept: the pump prompt reads `GasPump.IsPowered` (mains or its
  own wire), but `OnExtractFuel` gates on the wired Consumer port alone. On a
  map with the mains on, an unwired pump offers "[RMB] with a gas can to fill"
  and the host refuses. The prompt's `[RMB]` is the source's literal text.

Native consumers:

- The wind turbine is now an ordinary body with the procedural `BuildTurbine`
  tower, nacelle and three-blade hub, the source hand-written AABB for seat and
  collider, its 2500 W socket, and a hub spinning at `0.25 + 5.5 × wind` rad/s
  from the accepted wind (paused in singleplayer pause).
- Spotlight and Cagelight `DeployLight`s are real Bevy spot/point lights
  (source half-angle, range, energy via `point_lumens`, `SourceFalloff`,
  `dynlight` membership) on the stood-up body, aimed with `LookingAt(Dir,
  Back)`. The `SplitLampLens` bulb is carved by the source predicate and glows
  white-hot at `6 × envelope`. Both follow the accepted consumer `Powered`
  through the source 1.3 s/1.1 s envelope and mid-ramp stutter; a queued wire
  or toggle never lights them. `tools/lamp_lens_reference.py` ran unmodified
  `ObjMesh` on Godot: the runtime carves the 4 amber glass triangles, not the
  "16 head triangles" a source comment claims.
- Generators play the looping engine voice (unit 8 m, range 45 m) with the
  source pitch `0.6 + 0.4 × level − 0.22 × load × level` and volume
  `−26..−6 dB + 3.5 × load × level`, and keep the requested body shake. The
  level is the accepted-state cosmetic ramp; a burning generator cuts out at
  once, as `TakeDamage` does.
- The look-at canvas shows the source suffixes (`· 1h 40m` runtime from fuel or
  charge and the accepted draw, `· 1680w` turbine output) and bars: HP, fuel /
  battery charge / turbine wind (LoadColor), and generator/battery load. The
  runtime suffix reads the client's own `UG_GENBURN`, as the source does.
- GridSource and GasPump world fixtures are port endpoints: a root at the
  `Materialize` frame (yaw, then −90° X) with the source `PortLocal` socket and
  the `AddInteractionCollider` box (pump 1.1×2.6×1.1 at +1.2 m, breaker box
  1.3×2.1×1.3 at +0.95 m, world-axis). The box is solid only to the player and
  look rays, as layer 1<<6 is in the source walking mask; the map object keeps
  its mesh and collision, and no authority geometry is uploaded. Focus shows
  the source panels: "Gas Pump" with the station fill bar and
  `N% station fuel · empty | [RMB] with a gas can to fill | no power`, and
  "Grid Power" with the accepted draw bar and `draw / 10000W` or `mains OFF`.
  Fixtures have no pickup or toggle.

Data/radio: the original unified game also refuses them. Its wire tool sends
every replicated pair through `RequestConnectWire`, and the server's
`CanConnectWire` accepts only Output/Passthrough → Consumer, so a data wire is
never created. The port keeps that behaviour.

Validation (frozen bridge `.local/power-v78-third`): core 754/754, managed
250/250, JSONL 128/128 (its save-version pin now reads 3), native 756 passed /
50 ignored, and the Godot solution builds. New coverage: 8 core power cases
(burn by load, dry cut-out and refuel; battery charge, discharge, replica and
save; turbine wind, height, cap, hysteresis and threshold; trigger port order;
bare harness; exact-instance deposit with token, reach, fire and full-tank
refusals; legacy save migration; 20 L and 50 L cans surviving the owner echo).
Two actual-UDP managed cases: a peer renders host-accepted wind, charge and fuel
burn, and a peer fills its can at a wired host pump (refused unwired) and pours
it into a generator, with nothing changing before acceptance. Ten native power
cases, including the Godot lens oracle, a real-bridge lamp envelope, a grid
fixture wired to a lamp through the server mains, and an actual-UDP owner
filling a held can at a focused pump (RMB) and pouring it into a focused
generator (LMB) with injected mouse edges. 24 surgical controls each fail their
guard. They cover: no burn; the old fuel grid; battery on the toggle rule; an
unscaled turbine; no triggers; first-trigger-wins; wind with no hysteresis;
unsaved charge; an unstepped bridge; a deposit ignoring the token; a deposit
ignoring reach; the old jar grid (core and UDP); no bridge extract action; a hub
ignoring wind; a lamp ignoring the accepted solve; no BulbReach; unrepresented
fixtures; the battery readout fallback; a burning generator winding down; no
fixture look box; RMB or LMB sending nothing; and a pump prompt without the mains.

The actual-UDP Vulkan fixture
`deployables::interaction::power_visual::render_udp_turbine_charges_battery_that_lights_spotlight_and_source_readouts`
(run with `UG_WIND=0.6`, the source turbine override) exits 0. All seven
`bevy-power-accepted-{0..6}.png` were opened:
- 0: an unwired dark lamp beside a spinning turbine.
- 1: accepted wires with the lit spotlight lenses.
- 2: the battery panel (`Vehicle Battery · 2s`, HP, charge, 42% load).
- 3: the turbine panel (`Wind Turbine · 1680w`, HP, wind 0.67).
- 4: a face-on lit Spotlight with only its HP bar.
- 5: the amber `Gas Pump` panel with a full station bar and `100% station fuel
  · [RMB] with a gas can to fil`. The 320 px canvas clips the source prompt.
- 6: the blue `Grid Power` panel with a 7.5% load bar and `750 / 10000W` from
  the accepted solve.

Fixture panels draw their one bar in canvas row 0, which carries the heart
icon. Wiring uses production JSONL intents and focus uses the production ray;
this is not a native wire-tool or OS-input journey. The fixtures' map meshes are
absent in the fixture. It keeps SDR lighting without the production shader
falloff adapter, so beam pools are dim.

Remaining: the generator ramp authority above; LampLight room fixtures (desk,
standing and ceiling lamps) render no light; pouring into or siphoning from
vehicles; extraction fills the sender's first fillable can rather than the held
one (pre-existing `OnExtractFuel`, which names only the pump); the can's pour sound (`GameAudio.FuelPour`) and the fixture outline
glow; the editor grid name on the panel; configurable-device panels; the radio
channel suffix. The Godot hosts (`MpLoopback`, `DedicatedServer`) do not step
`ServerPower`; there batteries and turbines stay unproduced, as before.
