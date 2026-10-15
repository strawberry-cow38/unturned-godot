# Engine-independent simulation host

Build: `dotnet build bevy/bridge/UnturnedBevy.Bridge.csproj`.
Run: `dotnet bevy/bridge/bin/Debug/net8.0/UnturnedBevy.Bridge.dll`.
Protocol tests: `python3 bevy/bridge/test_bridge.py` (build first).

This host references the original core projects. It does not load Godot. Standard
input/output carry one JSON object per line. Diagnostics use standard error.
Requests have `{id,op,...}`; replies have `{id,ok,result,state}` or
`{id,ok:false,error}`. A reply acknowledges a queued action, not server approval:
the actual outcome appears in subsequent replicated state.

- `init`: `contentRoot` is an absolute content directory; optional `name`, `mapId`,
  `spawn:[x,y,z]`, `origin:[doubleX,doubleZ]`, `yaw`, `seed:1337`, `starterKit:false`. Returns protocol version, capabilities and
  action names. The explicit starter kit is a development fixture seeded by the
  authority; normal starts have empty inventories.
- `simulate`: `input:{moveX,moveY,jump,grounded,stance,yaw,pitch,velocity?,heldItemId}`.
  Calls the original movement sim once at 0.02 seconds. Stance is `stand`, `sprint`,
  `crouch` or `prone`. Yaw/pitch are degrees. Returns world-space `velocity`, capsule
  `height`, accepted `stance`, `eyeHeight`, and `jumpLocked`. Optional `headroom`
  and current `capsuleHeight` feed the original stance gate; call exactly once per
  fixed step. Run before Bevy collision; it advances no server tick.
- `tick`: `input:{position:[x,y,z],velocity:[x,y,z],grounded,yaw,pitch,stance,heldItemId}`.
  Supply the resolved Bevy body after physics. Sends the existing client state
  command, pumps transport/client/server, then publishes replication. No second
  player body is simulated. Authority recovery increments `correctionVersion`;
  Bevy must teleport/reset its body to `player.position` only when that changes.
- `snapshot`: read current replica state. Inventory has all 22 pages.
- `action`: `name` and `args`, as below. Commands go through `NetWorldClient` and
  the existing server command validators. Most effects appear within 2–4 ticks.
- `save` / `load`: `path`, using original versioned `WorldSave` format and map gate.
  An optional `BevyBridge` extension preserves world seed and retail loot point
  provenance while leaving the original v2 data readable by the old game.
- `terrain`: `seed,x,z,lod`, even before `init`; original `InfiniteTerrain` output.
- `catalog`: original item metadata, existing mesh/texture/icon paths, and full
  deployable definitions. Paths are relative to `contentRoot`; absent files are null.
- `worldGeometry`: host static collision triangles, described below.
- `worldLoot`: `mapRoot` points to the real map containing `Spawns/Items.dat` and
  `Spawns/Jars.dat`. Original parsers and weighted rolls feed server-owned loot;
  spawning follows all players at the original 95/125m thresholds and 600-item cap.
- `worldSpawns`: `mapRoot`; original player spawn parser returns `{players:[{x,z,yaw}]}`.
- `worldSpawn`: `items:[{itemId,amount:1,quality:100,position:[x,y,z]}]`; trusted host
  scene registration, not a player command. Creates real authoritative world items.
- `shutdown`: reply and exit.

State includes replicated inventory, vitals, skills, world-items, deployables,
vehicles, zombies, `worldClock:{day,timeOfDay,dayLengthSeconds}`, and weather
conditions from original `WeatherSim`/`WeatherOutlook`. The initial clock is the
source's 24-minute day at .35. Weather has the source's PEI/variant/snow schedules,
seasonal outlook, snow accumulation and temperature-dependent melt; `UG_WEATHER`,
`UG_WEATHER_FREQ` and `UG_WEATHER_DUR` retain their original meanings. Visual rain,
snow, lighting, wind and audio remain Bevy responsibilities.

`state.events` contains `{seq,kind,data,tick}` and remains available until the consumer
acknowledges it with `ackEvents:<lastConsumedSeq>` on any request. Consumers must
deduplicate and acknowledge only after processing. The tick is the authoritative
bridge tick when the accepted fact was received, so batched fixed updates retain
their cadence for presentation. Events include console, chat, fire, hit, impact,
crafting queue, death and respawn. Result-only operations
(simulate, terrain, catalog, worldGeometry, worldLoot, worldSpawns, worldSpawn,
worldRay) omit state by default; `includeState:true` overrides that. Tick recipes
are emitted only when inventory/skills/knowledge change; snapshot returns them all.

Static geometry accepts `meshes:[{vertices:[[x,y,z],...],indices?:[i0,i1,i2,...],
terrain?:false,destructibleIndex?:-1,resourceIndex?:-1}]`; omitted indices mean sequential triples.
Mark terrain meshes with `terrain:true` so ground-height queries exclude roofs.
Alternatively use `terrainPatches:[{origin:[x,z],cells,spacing,heights,holes?}]`,
with (cells+1)² row-major heights and cells² optional hole flags, or
`terrain:{seed,x,z,lod}` for the original generator. `replace:true` clears previous
geometry; use `replace:false` to append chunks. Defer BVH construction with
`finalize:false`, then finalize the last chunk. Combat rays use this host-owned
BVH; the player never supplies a claimed hit. `worldRay:{from,to}` is a read-only
diagnostic returning `{hit,point,destructibleIndex,resourceIndex}`.

Coordinates use Bevy/Godot right-handed Y-up, camera forward -Z. Terrain normals are
flat XYZ floats; layers are integers rather than JSON's base64 byte-array encoding.

Common actions:

| Name | args |
|---|---|
| `console` | `{text}` |
| `chat` | `{text}` |
| `moveItem` | `{page0,x0,y0,page1,x1,y1,rot1}` |
| `dropItem` | `{page,x,y,point?:[x,y,z]}` |
| `pickupItem` | `{netId}` |
| `equipItem` | `{page,x,y,slot}` |
| `consume` | `{page,x,y}` |
| `craft` | `{index}` |
| `craftCancel` | `{slot}` |
| `wearClothing` | `{page,x,y,slot}` |
| `unwearClothing` | `{slot,page?:255,x?:0,y?:0}` |
| `upgradeSkill` | `{speciality,index}` |
| `fire` | `{origin?:[x,y,z],dir?:[x,y,z]}` |
| `reload` | `{}` |
| `setFiremode` | `{mode:0..3}` (Safety, Semi, Auto, Burst) |
| `melee` | `{strong,yawDegrees}` |
| `grenade` | `{origin:[x,y,z],velocity:[x,y,z],itemId?:254}` |

Other action names match original `NetWorldClient.Send*` names with the `Send`
prefix removed and camel case. Parameters retain their original method names;
`craft.index` and `equipItem.page` are the two aliases listed above. Unknown actions
and missing/invalid arguments fail instead of creating substitute behavior.

Catalog, blueprint registry, gun parser, deployable schema and level-spawn parser
are linked from their original C# source. `extract_data.py` copies only data and
engine-free parsing portions from mixed Godot files at build time, with explicit
source markers that fail on drift. Filesystem/value adapters preserve the small
Godot API surface those sources use; no Godot engine or SDK is loaded.

Current integration limits: an available client command does not imply its world
entity or host service is registered. Animal/zombie sensing and movement,
attachment effects, blowtorch repair and remaining presentation still need host
adapters. Weather timers restart on load as in the original scene lifecycle;
clock/day, retail loot provenance and resource timers persist.

`mapServices {mapRoot,mapName,contentRoot?}` registers the whole extracted retail map
once. PEI currently yields 696 containers, 11 grid/gas fixtures, 1 authored deadzone,
1,733 destructible slots and 1,886 resources. Its `objects` table maps original,
zero-based placement **line numbers** (`sourceIndex`) to `containerNetId`,
`fixtureNetId`, and/or `destructibleIndex`. `resources` gives the original active
manifest + binary instance order (`index`, `name`, `instance`, `position`,
`rewardItemId`). Holiday filtering follows the original source. A repeated call
for the same map is idempotent. Save/load restores registrations before applying
world state and retains the mapping.

`worldServices` is the lower-level host registration seam, supporting
`hasWater`, `seaLevel`, `survivalDrain`, `resourceCount`, `resources`,
`destructibleCount`, `destructibles`, `deadzones`, `taps`, `fixtures`, and
`containers`. This runs the original crop schema, forage validation/regrowth,
rubble damage/regrowth/drop rules, stock generation, cooking/freezing, shared
station tanks and NPC catalogs. `npcCatalog` returns the original character,
dialogue, quest and vendor data. Crops/containers/resource and destructible alive
bits are published in snapshots. A reliable full snapshot follows registration;
this is required because bitmap deltas cannot resize their index space.

`action respawn {}` requests the original server respawn. Death/respawn, melee,
crop, forage, rubble and storage events follow the retained event queue. Owner
respawn clears movement and increments `correctionVersion`; the position comes
from the original authority recovery event. Ray queries skip destroyed rubble
and resume colliding when the authoritative regrowth timer restores it.

Terrain output also includes original road patches/markings, bridges, poles,
pylons, rails, crossings, tunnel mouths, holes and decoration model metadata.
Tree/foliage/boulder positions use region-local X/Z and absolute Y; roads,
bridges, rail spans, poles, pylons and tunnel mouths use world positions.

Tree/ore harvesting uses the original species table and deterministic reward
methods. Tree logs/sticks spawn after the original 11-second debris timer; ore
accepts only pickaxe Resource_Damage. The authoritative alive bit disables the
combat collider until regrowth. Events `resourceHit` and `resourceFelled` carry
index/name/kind, point/position and fall direction/trunkLength/debrisSeconds for
native effects. Save extensions retain partial health and pending timers. Container random rolls preserve the
source's unseeded range/distribution contract through a portable RNG adapter;
the resulting inventories are saved, rather than rerolled on load.


Weapons resolve the actual owned item rather than a global default. Original
magazine compatibility, richest-mag selection, chamber retention, loose-round
consumption and animation timing run on the authority. Reloading requires real
ammunition and returns the spent magazine. Gun ammo/chamber/selector persist on
the item across moves, drop/pickup and save. `weapon` includes ammo, capacity,
chambered, magazineId, shellItemId, firemode, reloading and rechambering. Safety
blocks accepted shots; native input owns Semi/Auto/Burst trigger scheduling.
`setFiremode` validates available modes and uses the original gun-state request.
Catalog entries include full original `gun`/`melee` definitions, reloadSeconds,
consumable effects and fluid metadata. Reload/rechamber events carry source timing.

`worldVehicles` registers `{replace,vehicles:[{typeId,key,variant,position,yaw,
health,fuel,battery,flags,speedMax,climbMax,fallMax,seatCount,hasDoors,storage}]}`;
stable keys append idempotently with `replace:false`. The result lists
`{netId,typeId,key}` in request order. Optional `seatOffsets` determines seat count;
`storage` carries trunkWidth/Height, gloveboxWidth/Height and seat compartment
width/height/label. Source envelopes/storage and fuel authority are retained.
`worldVehicleState {vehicles:[{netId,position,eulerDegrees,linVel,angVel,
steerDegrees,health,fuel,battery,flags}]}` publishes trusted host bodies;
transforms of occupied driver vehicles must arrive via original `vehicleState`
client commands. `worldVehicleExit {netId,seat,clear,position}` publishes a trusted
Rapier door-clearance probe valid for ten simulation ticks. Enter/exit and vehicle
recovery stay original server events. Raw source fuel capacities persist locally
and in saves, while existing remote wire quantization is unchanged.


`holdItem {page,x,y}` validates an owned address, the source `HandRules.KindOf`
and the source holster policy before selecting that exact backing item.
`equipAndHold {page,x,y}` additionally sends the original equip command when a
bag weapon must move to its preferred slot. It selects only when that same item
reference appears in the authorized destination; failure never selects another
same-ID gun. Responses include `queued`, `heldItemId` and (for equipAndHold)
`pending`. `state.weapon.held` gives `{id,page,x,y,kind}` while held. Removing or
moving a selected item into an invalid page clears selection. Original unload
commands update the live ammo counter, so firing cannot restore unloaded rounds.

Catalog `handKind`, `canEquipFromBag`, `preferredSlot`, `tool`, `throwable`,
`gravityMultiplier` and `consumeAnimation` come from the source dispatcher and
registries. `consumeAnimation` includes own equip/use clips, seconds, leftHook,
sound and optional flat color. Catalog `throwMotion` contains the original throw
speed/lift, weak ratio, clip release fraction, wall clearance, fuse and clip time.

`mapServices` also returns `objectDoors`, `seats`, and editor-authored `npcs`.
Object-door assemblies retain one independent net ID per source placement and
all original leaves, hinge pivots/axes, angle, duration, default state, sound and
easing curves. Seats use original catalog anchors transformed into world space.
NPC positions use the source editor coordinate frame and carry their character
definition. Container-converted props keep the source behavior and do not gain a
second door assembly. Real maps do not gain the disabled demo door/bed furniture.
Lower-level `worldServices` accepts explicit `doors`, `beds`, `objectDoors` and
`seats` arrays, with netId and position (doors: owner/locked; beds: yaw;
objectDoors: open/swingSide). `state.interactables` exposes the original replicated
tables. `toggleDoor`, `toggleObjectDoor`, `claimBed` and `sitSeat` run through
existing validators; their events remain in the retained event queue. Prop-door
open/reverse state is preserved by the save extension.

`worldDynamicGeometry {key,meshes}` replaces only the named dynamic body's host
triangles, leaving the static BVH intact. Meshes use world vertices, optional
indices and the same destructible/resource tags. An empty mesh list removes the
key. Native door animation publishes its actual collider pose through this seam.
Weather adds `rainWetness` and `rainPuddle`, preserving the source instantaneous
wetness and gradual 150-second fill / 420-second drain. `UG_PUDDLE` seeds the
original render-test starting level; `snowCover` already carries accumulation.

Chainsaws use the original weak-hit path at the extracted 0.225-second interval
(12 ticks at 50 Hz), without a delayed swing or a strong attack. Catalog exposes
`repeatedHitSeconds`; native input/animation may continuously request weak cuts.
Repair tools are excluded from damaging melee and use `toolUse` below.

Host combat meshes additionally accept `vehicleNetId`, `deployableNetId`, and
`bodyPart` (default `hull`). The ray chooses the nearest actual triangle; a client
never supplies a hit verdict. Removed entities stop blocking rays even before
the native scene removes their meshes. `worldVehicles` accepts `healthMax`,
ordered `hitRegions:[{bodyPart,center,size}]` and `engineRegion:{rear,threshold}`;
the server resolves source rotor boxes and engine half-space in the vehicle's
local frame. Accepted `vehicleDamage` facts contain `netId,amount,engineShare,
bodyPart,point,direction,attacker,health`; direct engine/rotor hits do not subtract
hull HP. Melee and blasts retain source half-damage to the engine.

Vehicle and generator destruction runs on the server's original four-second
fuse, followed by the source blast, 60-second burn/cooling, and 360-second corpse
retirement. Non-generators break immediately. `vehicleExploded` includes source
impulse/torque; `deployableBroken`/`deployableExploded` describe presentation.
Snapshots include `onFire,burnSeconds,wreckSalvageable`. Save extensions retain
remaining fuses and elapsed burns, including when elapsed time exceeds the new
session's tick count. Original network scalar/flag layouts are unchanged.

`action toolUse {active:true}` refreshes a five-tick held-input lease; send false
on release, UI capture, equipment changes or death. The server validates the
exact held repair tool and uses the original eye reach to find the target.
Repairs retain fractional HP across wire quantization and use source
`VehicleDamage*3` per second. A continuous three-second hold salvages a cold
wreck; hot or instant salvage is refused. `vehicleRepaired` gives accepted
`netId,amount,health,healthMax` for proportional native engine/rotor repair.

`consumeAccepted {playerId,itemId,page,x,y,tick}` follows actual source effects
and spending. Native consumption sends the intent at its source clip completion.
`throwAccepted {playerId,itemId,netId,tick}` follows validation and spending;
this host additionally requires an owned throwable. `state.projectiles` exposes
original replicas `{netId,kind,position,lastChangedTick}`. The existing wire has
no velocity/exact item/expiration field: families are grenade0, smoke1, flare2.
Inventory instances include `frozen,cooked,cookStyle`; any nonzero frozen value
blocks eating. Weapon `selectionVersion` changes with the exact held reference,
including same-ID replacements; `broken` and `canFire` expose hard validity
without confusing rate/bolt/reload timers with permanent inability to fire.

`mapServices.containerDoors` supplies the same authored leaf schema as
`objectDoors`, keyed by the container's real `netId` and `sourceIndex`.
These assemblies follow `state.containers.doorsOpen`, plus `cookerOn` only where
`lidRisesWhenLit` is true (the original barbecue rule). PEI currently registers
345 container door groups, 57 standalone doors and 393 seat anchors.

Vehicle subsystem pools are also host-owned: `engineRegion.health` and each
rotor `hitRegions[].health` initialize the source maxima. Snapshots expose
`engineHealth,mainRotorHealth,tailRotorHealth,engineDrowned`; damage, proportional
repair and save/load preserve them. Trusted native water physics may publish
`engineDrowned:true` through `worldVehicleState`; this irreversible source flag
prevents a blowtorch repairing a drowned engine. Tire hits update the original
replicated popped-tire mask, so replacing a tire remains the existing transaction.


Floating origins use `rebase {shift:[sx,0,sz],expectedEpoch:N}`. The shift must be
an integer multiple of the original 256m region size. Positive shift adds to the
absolute origin and subtracts from every local position. `state.origin` contains
`{epoch,x,z}` (double absolute X/Z); initial `spawn` is already local relative to
optional `init.origin`. Terrain generation still takes absolute region indices.
The native caller pauses publication, applies the returned shift to bodies,
interpolation and scene roots, then resumes. Add `originEpoch` to coordinate
requests: stale epochs are rejected before processing. Load and a distant respawn
can also change the origin, so adopt it from every returned state, including
action replies. While dead, suppress native distance-triggered rebases; respawn
moves the origin to the source spawn/claimed bed before its recovery packet.

The barrier drains the real memory transport and its reliable queues without
advancing any simulation clock, translates authority/replicas/pending combat,
then sends a reliable full snapshot at that same tick. IDs, inventory references,
seats, velocity, recovery windows and deadlines stay intact. Retained event
positions and registered static/moving geometry move with the frame. Saving
preserves the double origin and local registration metadata. Distant authoritative
positions use an optional unbounded store; in-range quantization and the existing
bounded wire remain unchanged. Far replicas become useful again when their area
enters the local wire window. This transaction is for this integrated host;
remote UDP peers would require a negotiated epoch barrier before participating.

`worldVehicleDamage {netId,amount?,engineShare?,bodyPart?,point?,direction?,explodeNow?,destructibles?}` is a
trusted host physics operation for source crash/rotor-strike results. It is not a
client game command. Parts are hull, engine, mainRotor, tailRotor or tire:0..7.
Default engine share is .5 for hull and zero for other parts. Accepted damage uses
the same authoritative pools, fuse and event path as weapons; native physics never
writes whole HP snapshots. Writes between simulation ticks are marked for the
next snapshot so a stationary car or newly popped tire cannot miss replication.
`explodeNow:true` applies the original aircraft impact's immediate explosion on
the upcoming authority step, bypassing the normal four-second hull fuse.
`destructibles:[{index,amount}]` applies the same accepted rotor strike to its
contacted props. All fields are validated before mutation. Both dead helicopter
rotors kill hull health and start the normal fuse. Source console diagnostics
`hurtmain`, `killmain`, `hurttail`, `killtail` require a living helicopter driver
and enabled console cheats; they use these same authoritative subsystem pools.

Projectile snapshots keep their original replica `position`, `kind` and `netId`,
and expose host flight `itemId`, `velocity`, `retireTick`, `remainingTicks`,
`elapsedTicks` and `lifetimeTicks`. Flare age includes its flight/fuse interval;
the original flare's full authority lifetime is 3450 ticks. Optional save metadata
restores the remaining flight and its age without spending another inventory item
or replaying a throw fact. A replica awaiting retirement has `remainingTicks:0`
and no flight metadata. Core packet layouts are unchanged.
