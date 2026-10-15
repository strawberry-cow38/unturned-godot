# Vehicle electrical authority

The bridge runs the original `game/Vehicle.cs` light switches, road battery loop,
alarm loop and running-tail decision on the server. `extract_vehicle_lights.py`
copies those source bodies and their constants at build time; scene operations
become accepted state and effects. Bevy still owns the one physical vehicle.

## Original behavior and repaired networking omissions

The old vehicle byte already carried engine, headlights, taillights, siren,
braking, exploded, alarmed and alarming. It did not carry high beams, the selected
lightbar pattern or broken lamps. Driver motion packets could overwrite alarm and
light flags, and the source `NetHeld` branch skipped the road electrical loop.
The new optional authority adapter protects those fields while leaving original
engine-free/no-service flag adoption unchanged.

A running road engine charges 40 units/s, with no simultaneous lamp drain. An
idle engine drains 20/s for low beams, 36/s for high beams and 35/s for the siren.
The full battery is 10,000 and starting requires 400. The host keeps the float
accumulator, so sub-unit 50 Hz drains survive integer replication. Source aircraft
return through `StepHeli`/`StepPlane` before this road loop; they retain that actual
behavior. Navigation lamps and the rotor-driven belly beacon remain native
presentation, not electrical authority.

The source 5% alarm roll happens once per host vehicle (using the session seed),
never per replica. Its original full-3D 7m proximity test checks all registered
players every 0.3s, including the source's absence of an alive filter. Hull damage
or damage to a still-live engine also triggers it. The 30s alarm uses the original
0.5s on/off phase and rising horn edges, then disarms permanently. Flat batteries
prevent headlights but do not suppress the source alarm horn or tail toggle.
Exploded vehicles extinguish all lights and break every configured lamp. Current
host `swamped` extinguishes electrics and disarms the alarm; persistent
`engineDrowned` only prevents starting, so lamps can work again ashore.

Trailer coupling still needs its native/authority adapter. The extracted tail
rule retains the source coupling inputs, but no missing hitch state is invented.
Manual driver horn networking is outside this checkpoint; alarm horn facts use
the new reliable event and the existing source `NoiseRules.Horn` hearing radius.

## Protocol 68

Protocol 67 peers fail the ordinary explicit version handshake. No existing ID
is renumbered.

| Space | Addition |
| --- | --- |
| Vehicle system 9 row | Append `highBeam:1`, `lightbarPattern:2`, `lampMask:7`, `brokenLampMask:7` after the existing tire tail |
| Vehicle battery | Signed integer bits 14 → 15, preserving original 10,000 instead of clipping to 8,191 |
| Command 75 | `VehicleLightingCommand {netId:u32, action:u8, value:u8}` |
| Event 57 | `VehicleLightingEvent {netId:u32, tick:u32, kind:u8, lamp:u8, position:3×finiteFloat32}` |

Action tags are CycleHeadlights=0, ToggleSiren=1, SetPattern=2 and RepairLamp=3.
Cycle/toggle require value 0. Pattern accepts 0..2 or 255=off; repair accepts 0..6.
Controls require the actual living driver. Repair retains the source free fix
(no material consumption), with the existing 8m `ServerVehicles.PaintReach`
envelope added for remote validation; the original mechanics button itself had
no server reach check. Unknown tags/values, invalid identities and truncated
messages are rejected. Vehicle snapshots parse completely before adoption, so a
truncated new tail cannot clear or partially replace a valid accepted view.

Event kinds are AlarmHorn=0 (lamp 255), LampBroken=1 and LampRepaired=2 (lamp 0..6).
They carry effects only, never damage. Integrated and UDP clients consume the
same reliable fact once; late join receives lasting masks without replaying old
horns or break shards. All new lasting fields participate in the state hash.

Motion publications keep engine/braking intent and cannot alter light switches,
alarm flags, battery, exploded state or lens damage. Starting is validated against
battery, engine health, fire, wreck and current swamping; native telemetries do
not refill a battery. A fact-only engineDrowned update applies before normalization and cuts the accepted engine immediately, even when the driver supplies no new pose. Original no-service core harness behavior stays unchanged.

## Bridge/native contract

- `catalog.result.vehicleLighting`: extracted canonical labels, pattern names,
  siren pitch, electrical constants and original low/high range, energy, cone and
  cone attenuation. No estimated lamp positions.
- `action {name:"vehicleLighting",args:{netId,action,value}}`: action names above,
  case-insensitive; SetPattern also accepts JSON value -1 for off.
- `worldVehicleLighting {netId,lamps:[{label,center:[x,y,z]}]}`: trusted host-only
  original mesh AABB centers in hull-local space, preserving original creation
  order for nearest-center ties. Labels map to canonical mask bits. The first
  complete lamp set is immutable; repeated uploads cannot heal broken lamps.
- `worldVehicleState.vehicles[].swamped`: trusted current physics fact, including
  false after leaving water. This is separate from permanent `engineDrowned`.
- `state.vehicles[]`: adds `highBeam`, `lightbarPattern`, `lampMask` and
  `brokenLampMask`. Host battery is exact; UDP battery is integer-quantized.
- Retained events `vehicleAlarmHorn`, `vehicleLampBroken`, `vehicleLampRepaired`:
  `{netId,lamp,label?,position,acceptedTick}`.

A source lamp is the nearest unbroken original center strictly within 0.42m of an
accepted vehicle contact, after any reported glass contact and before tires,
rotors or engine/hull. Full accepted hull yaw/pitch/roll rotates the center.
Breaking it consumes that contact without hull/engine damage, including source rounds whose VehicleDamage is zero. Melee and blasts
retain their original hull path. Native lens visuals are not extra hitboxes.

Save data preserves exact battery, flags, high beam, pattern, alarm timer/phase,
broken masks and local centers by accepted vehicle ordinal/type. It is restored
before join/handshake ticks, avoiding a new roll or a restarted alarm. Local
centers need no origin translation; retained event positions use the existing
origin barrier. Older saves retain their original saved battery even without the
new electrical extension.

## Verification boundary

Managed and core checks cover real UDP driver/passenger/refusal paths, source
fractional drain and alternator priority, full battery framing, all 39 source fleet
entries (including null Parts), swamping/recovery, rotated lamp priority,
streaming damage retention, free repair reach, engine damage/start refusal,
late-join masks without effects replay, one-use alarm/load lifetime, atomic
truncation/unknown tags and explicit old-version rejection. Native lights,
patterns, audio and glass effects require their separate implementation and live
render verification; this authority checkpoint does not claim those visuals.

The immutable `vehicle-lights-v68-final` build passes **121 managed bridge tests,
690 core networking tests and 121 JSONL regressions**. Its source SHA-256 manifest
is saved beside the artifact. Independent source review found and corrected the
null `Parts` fleet crash and fact-only drowned-engine ordering. Four intentional
fault controls fail as expected: restoring the old battery width reads 8,191;
removing lamp routing loses a zero-damage lens hit and sends a normal lamp hit to
the engine; restoring unguarded `Parts` iteration crashes on original null data.

Reproduce the focused authority and framing checks with:

```sh
dotnet test tests/UnturnedBevy.Tests --filter FullyQualifiedName~VehicleLightingTests
dotnet test tests/UnturnedNet.Tests --filter FullyQualifiedName~VehicleLightingProtocolTests
```
