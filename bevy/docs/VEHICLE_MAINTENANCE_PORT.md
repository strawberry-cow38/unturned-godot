# Vehicle maintenance authority

Protocol69 appends command76 (`VehicleMaintenance`) and event58 (accepted
maintenance facts), and extends the vehicle row with18-bit pane-present and
pane-broken masks plus an8-bit tire-present mask. Version68 peers are explicitly
refused. The existing spare-consuming `FitTire` command remains unchanged; the
original mechanics panel's free, indexed repair is a different action.

The source `Vehicle.GlassPaneLabels` and `GlassPaneDisplay` are extracted at build
time. Their17 stable labels occupy canonical bits0..16; bit17 identifies the
source whole-mesh `canopy` fallback. Native registration preserves original
loaded-pane order separately, because `ResolveHitGlass` retains the first tie.
`worldVehicleGlazing` is a trusted host-only mesh adapter:

```
{netId, panes:[{label,center:[x,y,z],size:[x,y,z],rotation:[x,y,z,w],scale:[x,y,z]}], tireMask}
```

`center` is the transformed mesh AABB center in hull space. `size` is the raw
mesh-local AABB size; rotation and scale map the centered raw box into hull space.
Each refresh replaces current pane transforms, including articulated doors, but
cannot change the registered pane/tire set or erase accepted damage. Contacts
use the full accepted hull rotation, inverse pane transform, original0.28m raw
local tolerance, and nearest raw local distance. The original thinnest-axis
normal is transformed and flipped outward. Shard half-size is raw size/2 with
source0.2m component minima. The whole-mesh canopy has no source player collider;
it supplies classification bounds, not an invented authority triangle surface.

Glass consumes a bullet before lamp/tires/hull and does not damage the hull or
engine, even when vehicle damage is zero. Once accepted, shared world-ray traversal
skips the broken pane's `glass:<canonical index>` triangles immediately, before
native geometry refresh. Native presentation must remove its visual and player
collider from the same accepted mask. Ordinary melee retains its original hull
path. Explosion does not invent a glass shatter or repair.

`action vehicleMaintenance` carries `{netId,action,index}` where action is
`RepairGlass`, `RepairTire`, or `Horn`. Repairs require a living requester and a
broken, present index within the port's existing8m vehicle-service reach. The
original direct MechanicsPanel did not itself enforce reach; this is the unified
remote authority boundary, not a claimed original panel check. Repairs do not
consume inventory. Manual horn requires the accepted driver, source horn asset,
positive authoritative battery, no sounding alarm and the source per-spec
cooldown (default0.5s, ship4.1s). One accepted event triggers presentation and the
source32m zombie-hearing alert; clients cannot submit horn counters or sound facts.

`vehicleGlassBroken` includes `netId,label,index,position,normal,halfSize,acceptedTick`.
`vehicleGlassRepaired`, `vehicleTireRepaired`, and `vehicleHorn` are accepted
one-shots with identity, position and tick. Lasting masks reconstruct late joins
without replaying shards or horns. Save data preserves pane transforms, damage,
tire availability and remaining horn cooldown by original vehicle ordinal/type.

The same protocol change repairs two measured scalar budgets. Original exported
fuel reaches300000mL, while signed12.1 clipped it to2047.5mL. It now uses signed20.1
(max524287.5mL). Original ship hull reaches40000HP, while signed16.1 clipped it to
32767.5HP; signed17.1 now represents the full fleet (max65535.5HP). These are wire
capacity corrections; original spec values and authority consumption remain intact.

Verification: the immutable `vehicle-maintenance-v69-final` candidate passes
130 managed adapter tests, 693 core networking tests and all 121 JSONL protocol
journeys. The new coverage includes nine adapter cases and three protocol cases:
full 39-type fleet values, atomic truncated reads, malformed requests/facts,
glass hole/repair geometry, transformed moving panes and source ties, exact tire
indices, horn driver/battery/alarm/cooldown gates, persisted cooldowns, late joins,
malformed saves, and a normal remote shot through an intact wreck pane.

Three isolated negative controls fail when the relevant fixes are removed:
broken-pane geometry keeps blocking, wreck glass cannot break, and fleet fuel
clips to 2047.5mL. A separate health-only control proves the original ship's
40000HP clips to 32767.5HP. Native glazing, repair buttons and accepted manual-horn
presentation are separate integration checkpoints; this authority checkpoint
does not claim those visuals are complete.
