# Engine-independent simulation host

Build: `dotnet build bevy/bridge/UnturnedBevy.Bridge.csproj`.
Run: `dotnet bevy/bridge/bin/Debug/net8.0/UnturnedBevy.Bridge.dll`.
Protocol tests: `python3 bevy/bridge/test_bridge.py` (build first).
Wildlife authority tests: `python3 bevy/bridge/test_wildlife.py`.

This host references the original core projects. It does not load Godot. Standard
input/output carry one JSON object per line. Diagnostics use standard error.
Requests have `{id,op,...}`; replies have `{id,ok,result,state}` or
`{id,ok:false,error}`. A reply acknowledges a queued action, not server approval:
the actual outcome appears in subsequent replicated state.

- `init`: `contentRoot` is an absolute content directory; optional `name`, `mapId`,
  `spawn:[x,y,z]`, `origin:[doubleX,doubleZ]`, `yaw`, `seed:1337`, `starterKit:false`, `allowCheats:true`. `UG_DEDICATED_NOCHEATS=1` changes the cheats default. Returns protocol version, capabilities and
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
  Seated packets still carry held selection and deduplicated combat; the opted-in
  host ignores their movement fields. Vehicle seats remain the pose authority.
- `snapshot`: read current replica state. Inventory has all 22 pages.
  Recovered arrows/bolts are ordinary settled `worldItems` with optional
  `stuckDirection`/`stuckNormal`/`stuckEuler` frozen-pose metadata. Accepted gun
  warheads emit `bulletExplosion` (`position`, `radius`, original `gun`/`itemId`);
  cosmetic bullet retirement never authorizes damage. See
  [WARHEAD_PORT](../docs/WARHEAD_PORT.md).
- `diagnostics`: read-only live authoritative bullet and retained stuck-pose counts.
- `worldCosmeticGeometry`: trusted rendered `waterBoxes` (`center`,`size`), shared
  by authoritative and cosmetic bullet rays only; excludes movement/blast shelter.
  The historical operation name remains compatible.
- `action`: `name` and `args`, as below. Commands go through `NetWorldClient` and
  the existing server command validators. Most effects appear within 2–4 ticks.
- `save` / `load`: `path`, using original versioned `WorldSave` format and map gate.
  An optional `BevyBridge` extension preserves world seed and retail loot point
  provenance while leaving the original v2 data readable by the old game.
- `terrain`: `seed,x,z,lod`, even before `init`; original `InfiniteTerrain` output.
- `catalog`: original item metadata, existing mesh/texture/icon paths, and full
  deployable definitions. Paths are relative to `contentRoot`; absent files are null.
  `gestures` enumerates the original `GestureRules` table as `id,name,clip,loops,
  stops,requestable,broadcast,requiredStance`. Optional null fields are omitted.
  `requestGesture` returns `queued:false,reason` and a retained `gestureRejected`
  fact with `text` when the original local admission check refuses the request.
  `queued:true` only sends the original command; accepted state/events still come
  from the server publisher, with no optimistic native pose.
- `worldGeometry`: host static collision triangles, described below.
- `worldLoot`: `mapRoot` points to the real map containing `Spawns/Items.dat` and
  `Spawns/Jars.dat`. Original parsers and weighted rolls feed server-owned loot;
  spawning follows all players at the original 95/125m thresholds and 600-item cap.
- `worldSpawns`: `mapRoot`; original player spawn parser returns `{players:[{x,z,yaw}]}`.
- `worldWildlife`: `mapRoot`; loads original `Spawns/Fauna.dat` tables and points.
  Optional `points:[[x,y,z],...]` selects the original generated-point loader; these
  positions are already local world coordinates, while retail file positions are
  converted from Unity coordinates and the active absolute origin.
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

`state.player.gesture` is the original accepted `EPlayerGesture` byte;
`state.player.arrested` is true only for `ARREST_START` (11), matching the source
controller. The original `PlayerAppearanceNetSync` publishes clothing, held
attachments and gestures at its 5Hz cadence before replication. Use action
`requestGesture` with `{gesture:6}`/`{gesture:7}` for surrender start/stop,
`{gesture:9}` for a one-shot wave, and `struggle` with `{side:0}` or `{side:1}`
for an arrested player's lean change. Core validates held-item/stance/request
rules and alternating struggle sides. One-shot gestures use retained `gesture`
events instead of latching into the state. Captor identity and remaining restraint
strength remain server-private; native must not invent either.

The publisher is the original source class with its attachment lookup redirected
to the existing extracted helper. Its current input protocol supplies only the
four on-foot stances; native seated gesture admission and multiplayer restraint
presentation remain separate integration work. Session teardown discards that
owner's pending static gesture request so a fresh world cannot inherit it.

`state.events` contains `{seq,kind,data,tick}` and remains available until the consumer
acknowledges it with `ackEvents:<lastConsumedSeq>` on any request. Consumers must
deduplicate and acknowledge only after processing. The tick is the authoritative
bridge tick when the accepted fact was received, so batched fixed updates retain
their cadence for presentation. Events include console, chat, fire, hit, impact,
crafting queue, death and respawn. Result-only operations
(simulate, terrain, catalog, worldGeometry, worldLoot, worldSpawns, worldSpawn,
worldRay) omit state by default; `includeState:true` overrides that. Tick recipes
are emitted only when inventory/skills/knowledge change; snapshot returns them all.
Accepted `fire.data` additionally carries `itemId`, `quality`, `handlingPenalty`,
`recoilMultiplier`, `selectionVersion` and `acceptedTick`, captured before weapon
wear at the actual shot. `recoilMultiplier` is the original sharpshooter skill
multiplier times condition handling penalty; native stance settling remains
separate. These optional bridge fields survive holstering and retained-event
delivery without changing the original `PlayerFiredEvent` wire format.
Accepted `impact.data` keeps the original `pos` and `surface` (`0` flesh, `1` world)
and adds optional `normal:[x,y,z]`, `water`, `thin`, `playerId`, `sequence`, `pellet`
and `acceptedTick` from the exact authoritative contact. One original reliable
impact produces one retained event; `bulletPierced` must not produce a second
splash. Unknown analytic normals remain absent. Native water effects should use
`water:true`, since the splash slab intentionally has no solid physics collider.

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

`terrainLayers:[{origin:[x,z],width,height,spacing:[dx,dz],layers:[byte,...]}]`
supplies the original dominant-layer field. Rows are Z, columns X; PEI has negative
Z spacing. Sampling rounds to the nearest grid point with midpoint-even ties and
clamps at the edge. Layer 5 remains the source's sand/water proxy, including dry
sand; missing metadata returns 255 rather than inferring water from sea height.

Wildlife retains the original `AnimalAgent` brain, species stats, fauna-table
selection, 130/165m stream distances and 36-animal cap. `state.animals` contains
actual `AnimalReplication` position/yaw/species/anim fields plus source clip,
health, bodyHeight/radius, rig/texture, rigYawFix, dead, corpseRemainingTicks and
deathImpulse. The local host renders `hostPosition`/`hostYaw` from the brain's
50Hz pose; remote replicas retain their original 12.5Hz publication cadence.
There is one terrain-followed body, with no second native AI or movement solve.
`animalPanic`, `animalHit` and `animalDied` are retained accepted facts. Bullet,
melee and grenade damage use the existing validated combat commands; bullet
damage captures shell selection, condition and distance falloff at fire time.
The original animal melee proximity cone follows typed tree/prop/body targets;
untyped walls do not add a new melee gate. Vision, bullets and blasts use source
world occlusion. Source corpses last 12 seconds, then fauna points can respawn;
the current source has no animal death loot or XP. Save/load preserves brain
RNG, targets, timers, health and corpse lifetime, and rebasing preserves all IDs.
Original `spawnanimal`/`animal` console commands accept species name/prefix and
count 1–12, use the persistent golden-angle placement sequence, and respect the
server cheat gate. No animals are invented for `InfiniteTerrain`: its source
world does not register wildlife; the separate finite-island fauna generator is
an outstanding native world path.

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
entity or host service is registered. Zombie sensing and movement,
attachment effects and remaining presentation still need host
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


`worldZombies {mapRoot,points?}` hosts the **current** `ZombieChunkField`,
`ZombieFlowField`, `ZombieBody` damage rules, and `ZombieTables` wardrobe parser.
Register after finalized terrain geometry. Omit points for real
`Spawns/Animals.dat`; supplied generated points already use local X/Y/Z.
Layer-5 filtering, deterministic outfit seeds, 64m chunks, tier hysteresis,
budgets, nearest-first 48-body cap, sight, sound, separation and flow all come
from the original source. Dormant legacy `ZombieSim` AI is not enabled.

Only promoted physical bodies and eight-second corpses appear in `state.zombies`.
Rows contain the existing replica fields plus `hasBody`, `hotBody`, exact
`hostPosition`, `hostYaw` (degrees), `velocity`, `desiredVelocity:[x,z]`, `health`,
`dead`, `table`, `outfit`, `variant`, `shirtId/pantsId/hatId/gearId`,
`corpseRemainingTicks`, `deathImpulse`, and optional native `motion`.
`state.zombieField` reports chunk/population/body and navigation diagnostics.
WARM/COLD records remain managed data without a native collider or puppet.

Native performs the single source body movement solve, then sends trusted host
`worldZombieState {bodies:[{netId,position,velocity,yaw,motion?}]}` before the next
bridge tick. Optional motion preserves the unsticking window across save/load:
`{windowPosition:[x,y,z],windowTime,escapeTime,positionInitialized}`. Origin shifts
translate its position with the body; the remaining scalar state is unchanged.
Unknown/dead IDs are ignored and all accepted updates are validated before mutation.
The bridge never integrates another HOT body's position.

Accepted gun/melee and owner footstep sounds use original source radii, stance,
skills, suppression, cadence and rain masking. Native vehicle horn/engine facts
can use `worldZombieNoise {position:[x,y,z],radius}`; this is a host sound emission,
not a client damage request. Zero radius is silent. Zombie bullets use the current
source canonical gun damage, exact capsule and original falloff, rather than the
legacy cylinder/speciality profile. Melee retains original typed-world priority
and one-zombie plus one-animal cones; blasts retain linear falloff and world occlusion.
`zombieHit`/`animalHit` expose `source:"bullet"|"melee"|"blast"`,
`impactEmitted` (bullet only), and `hitConfirmedEmitted:false` so native blood and
hitmarkers are not duplicated. Current zombie kills increment the real replicated
kill count; the current source implements no attack loop, death loot, or XP award.

Save metadata retains full chunk population/killed-slot timers, deterministic
outfits, native motion, alert/flow state and remaining corpse lifetime. Immediate
HOT→FROZEN teleport unloads live bodies before dropping the chunk's live list;
this closes the original node-orphan leak without reviving killed slots.
The exact source walkability box queries exported triangle surfaces via a BVH;
fully contained convex volumes still require native solid-shape metadata because
the current host geometry contract carries triangles rather than convex shapes.


Accepted `fire.data` also captures `barrelId`, `silenced`, `shootClip`,
`shotVolume` (linear), `muzzleVelocity`, and `barrelBroken` before an attachment
breaks or the gun is holstered. Sound filenames follow original barrel audio and
Viewmodel fallback rules. `weapon.attachments` exposes `sightId`, `barrelId`,
`gripId`, `tacticalId`, `magazineId`, `mask`, and `seeded`; `weapon.silenced` is the
current state for the next shot. Factory irons seed once, with saved custom
attachments retained. The original source seeds no factory barrel.

Suppressed bullets use the source .80 velocity multiplier in actual authority
flight. A water-bottle silencer remains effective for its one accepted shot, then
clears its installed ID and mask bit and emits `barrelBroken` with the original
notice. Rejected triggers never break it. The retained shot facts remain silenced
afterward so delayed native muzzle/audio consumers cannot turn that shot loud.
The optional core condition lookup uses the bridge's exact selected backing item,
including two same-ID pistols in different weapon slots.

`attachmentOptions` reads the selected gun's actual capabilities and owned item
instances. Its result contains `gun` (address/ID), `gunName`, `gunToken`,
`selectionVersion`, `busy`, `canRack`, and `slots`. Each slot contains `name`,
`visible`, `available`, `installedId`, `detachable`, source RGBA `color`, `kind`
(`attachment`, `magazine`, or `shell`), `choices`, and optional `shells`.
Each physical choice is `{page,x,y,id,amount,quality,instanceToken}`; shell choices
are `{id,count,selected}`. No item is invented for a missing bag choice. Capabilities
come from the original resolved visual tables: the current source has no Grip
node, and AUG's integral Sight is hidden. A loose-shell Magazine can remain gray
in the source icon recipe while still offering its ammo-type picker.

New listen-host actions require `gunToken` and `selectionVersion` from those
options: `installAttachment {slot,page,x,y,instanceToken}`, `removeAttachment
{slot}`, `chooseShellType {itemId}`, `unloadShells {}`, and `rackGun {}`.
Requests capture exact owned references and are revalidated during the authority
tick. `queued:false` includes a refusal reason; accepted queued mutations emit
`attachmentChanged`, and later refusals emit `attachmentRejected` with `reason`.
Native must wait for the actual snapshot and accepted event before changing its
held model. Tokens are opaque process-local identities and change after load.

Non-magazine fitting extracts the original `AttachmentMenu.FitAttachmentTo`.
A cloned-grid preflight runs the same placement algorithm before touching the
real objects, closing its full-bag rollback hole without replacing Item references.
Factory irons never mint inventory items and return after removing a scope.
Specific-magazine actions extract the original `LoadMagInstance`,
`RemoveMagazine`, and `RackGun`, keeping exact selected rounds, chamber contents,
reservoir capacity, animation gates and auto-rack timing. An outgoing magazine
keeps its rounds; full-bag removal drops that magazine, while a swap that cannot
return its predecessor is refused atomically. Rack retains the source's current
FMJ5004 ejection mapping even for another chamber type (an existing source gap).
Loose-shell choices eject the old type before loading only the new type actually
carried; animated unload refuses to discard rounds if the bag has no room.
Per-player caliber choices persist in bridge save metadata so reload cannot
reinterpret a saved slug tube as buckshot.

Accepted `fire.data.barrelSilenced` is distinct from `silenced`: the former is the
original BarrelDef flash gate; the latter additionally includes integral gun
suppression for tracers/hearing/velocity. This intentionally preserves the current
source's bare-barrel flash on intrinsically suppressed guns. Accepted `melee.data`
adds `itemId`, `repeated`, and `acceptedTick`, captured before wear or switching,
so delayed native animation/audio consumers never identify an old swing by the
currently selected weapon. Neither addition changes the network packet format.


`state.bullets` is the original client-only cosmetic flight, separate from the
server's damage flight. Accepted `fire` intents can include `args.visual` with
clamped `aimAlpha` and optional world-space `muzzleAnchor`. The
bridge joins that context to the actual accepted combat sequence, captures the
shot's original gun/round/condition before wear or bottle break, and extracts the
source cone/pattern scatter. Rejected or replayed requests never launch visuals.
One source sampling pass supplies both cosmetic and authoritative pellet
directions. The server profile retains count/speed/damage and the validated
command supplies the authoritative origin. Burst count advances only on accepted
shots and resets at the original0.35s gap; legacy `visual.patternShot` hints are
tolerated but ignored. Native never supplies sampled directions or hit claims.
The source per-axis pattern branch overwrites the initial cone direction; this
preserves the executed source despite its additive wording.

Each bullet row contains `id`, `shotId`, `playerId`, `sequence`, `pellet`, `itemId`,
`gun`, `origin`, `position`, `velocity`, `gravity`, `remainingSteps`, `launchTick`,
`silenced`, `tracerWidth`, HDR `tracerColor`, `rocket`, `pierced`, and optional
`muzzleAnchor`. Retained `bulletLaunched` records the accepted launch identity,
context and per-pellet velocities; `bulletRetired` records impact/expiry. Neither
event creates damage, blood, surface impacts, hitmarkers, or recoverable items.
Only the existing combat authority owns those facts. `catalog.bulletVisuals`
provides source tracer texture/shape constants and the original rocket model,
orientation, color and flight sound. Native should follow these flight rows
rather than extrapolate a second projectile simulation.

`worldCosmeticGeometry {waterBoxes:[{center:[x,y,z],size:[x,y,z]}]}` replaces the
cosmetic-only ocean splash shapes. The original ocean has a 0.2m box; these boxes
never enter solid-world combat geometry. Optional `thin`/`water` tags on existing
world meshes affect only cosmetic piercing. Original thin/water velocity loss,
two-pierce maximum, exit offset, warhead stop and hit-from-inside behavior remain.
Geometry and flights shift atomically with origin; water metadata survives load
while temporary bullets clear, matching the source. Per-bone corpse colliders
are not exported yet. Authoritative rocket splash/recoverable arrow behavior is
still limited by the existing server combat implementation; cosmetic retirement
does not bypass that authority boundary.

Weather snapshots also expose the active original asset's `hasLightning`,
`minLightningInterval`, and `maxLightningInterval`. Native cosmetic scheduling
uses these values and existing blend alpha; it does not create weather authority.
`UG_SNOWCOVER` seeds the authoritative coverage once, clamped to0..1, then the
original accumulation/temperature-driven melt rules continue. This preserves the
source render diagnostic without an independent native snow override.

The console `weather` command now runs the original extracted
`WeatherManager.ApplyCommand` switch in the managed host. `weather lightning`
emits one retained cosmetic `weatherStrike` fact, while clear/rain/variants use
the original WeatherSim transitions. The existing server cheat gate applies;
refused commands produce neither a strike nor a weather mutation.

Cosmetic launch context also retains the original local origin/direction before
wire quantization. It becomes visible only after the matching combat sequence is
accepted; the authoritative command still uses the unchanged packed values.
Pending local origins and anchors cross the same atomic origin barrier.

Local appearance reads `player.profile {name,face,avatarHash}` from the actual
client profile replica, and `inventory.wornQuality` alongside the seven existing
worn item IDs. Empty slots have ID0 and quality0. `catalog.items[].hasCondition`
uses the original durability classification, so presentation can ignore quality
for items without condition. Profile changes use the existing `setProfile`
action (`name`, `avatarPng` byte array, `face`); server name sanitization, face
validation and the 100-tick rate gate remain intact. A fresh session starts with
the server's Survivor/face0 profile until that intent is accepted. Skin, hair and
beard customization are not fields in the current profile protocol.

Scope steady uses the original shell prediction and `ScopeSteadySim`. Movement
input accepts `steadyKey`, `aiming` and `scopeZoom`; all three source gates must
pass before requesting a breath hold. `state.steady` reports `active`, `blend`,
`rateScale` and `lockout`. The accepted `MoveInput.ButtonSteady` bit rides the
existing player-state command and server vitals own oxygen expenditure. The
predictive client copy also retains its drained oxygen between quantized
snapshots, as the original PlayerController explicitly requires; throwing that
copy away can skip the floor and leave visual steadying active indefinitely.
At the floor the hold stops, rate returns to1, and the2.5s lockout counts down
only after release. Init/load/death reset transient hold state. No wire or core
rule changes are needed, and `simulate` only records intent while `tick` steps
steadying exactly once.

Optional `init.listenPort` starts the same integrated world on the original UDP
transport, with `serverName`, `maxPlayers` and `motd` metadata. It defaults to
cheats off and a bounded shared origin. The local player also traverses UDP;
`state.network` reports session metadata and `state.players` exposes peer replicas.
The managed client-only boundary uses `init.connectHost` and optional
`connectPort` (47872 by default), mutually exclusive with `listenPort`. It creates
only the original UDP client. Init returns while connecting; continue `tick`
without requiring a player body until `state.connected` becomes true. The first
actual owner replica sets `player.position` and increments `correctionVersion`
before any owner position is sent. A caller's local map-spawn hint never replaces
the server's selected spawn.

`state.network` reports `mode`, `ownsWorld`, `awaitingSpawn`, session `state`,
`reason`, source `rejectReason`/`rejectText`/`retryable`, protocol and the original
16-digit hexadecimal `contentHash`. `wireGaps` lists host-only enrichments which
are absent from this protocol. Unknown weather, absolute day, projectile lifetime,
weapon timers, precise stuck poses and vehicle part health remain absent rather
than fabricated. Inventory, vitals, entities, events and peer appearances come
from the actual client replicas. Equip waits for a changed accepted inventory;
ambiguous identical items cannot supply an exact instance identity over this wire.

A client-only session refuses host registration, damage, save/load and rebase
operations. Unsupported attachment/repair/explicit-respawn intents return
`queued:false` with a retained refusal; the original server automatic respawn
still works. Nonempty `password` is refused because the existing handshake has no
password field. `disconnect` closes an owned host and releases its listener,
leaving no state and refusing later ticks. A remote disconnect retains a readable
terminal network state until replacement. `shutdown` works in either mode.
See [the multiplayer ledger](../docs/MULTIPLAYER_PORT.md) for verified boundaries
and the native/remaining protocol integration.

`serverProbeStart {host,port}` is available before `init` and returns `token`,
`contentHash`, and original `connectAttempts`/`rejectRetryLimit`/`linkTimeoutSeconds`
policy. `serverProbePoll {token}` is nonblocking and returns `pending`; completion
adds `status {ok,ping,players,max,version,protocol,pvp,passworded,hasFlags,maxPing,
map,gamemode,motd,iconBytes,compatible,pingRefusal,refusal}`. Version/content hashes
are16-digit hexadecimal strings. The exact original1200-byte UDP browser request,
1.5s socket timeout, nonce check, bounded optional fields and text sanitization
run on one worker. A new token supersedes an older query; it queues behind any
running query. `serverProbeCancel {token}` invalidates its result without blocking
for that worker. Stale/cancelled polls report `cancelled:true`. Native owns the
source four-attempt UI loop and cancellation generation. `compatible` is the
original content-hash check; the subsequent handshake owns protocol admission.

`reconnect` retries an initial retryable rejection through the source
`NetWorldClient.Connect`. The original session policy permits four rejection
retries, independently of the four browser probes. A previously joined session
must use `init` again to create fresh replicas; explicit disconnect has already
closed its socket. No movement publishes while awaiting the new owner's spawn.
Remote animal rows also expose the source puppet clip and static species body
height/radius; these do not authorize a remote physics body. Original
`AnimalPuppets` selects these clips on the normal engine-driven animation player
and has no collider; its comment describing a frozen pose is stale.

`mapBindings` is a read-only counterpart to `mapServices` with the same
`contentRoot,mapName,mapRoot` request. Call after acceptance; it parses the full
source manifest using `network.holiday`, never the remote machine's calendar.
The response preserves `objects,resources,objectDoors,containerDoors,seats,npcs`
and count fields. It adds `readOnly:true`, `holiday`, monotonic `revision`,
`complete`, `unboundContainers`, `unboundFixtures`, and corresponding `ambiguous*`
counts. Authored door/seat counters follow the same original full-manifest order.
Minted container/fixture IDs bind only from an actual unique replica with matching
kind and exact wire-quantized position/yaw. Missing/ambiguous IDs stay unset;
container-door metadata appears only after its actual container has bound.
Re-query when container/deployable replica IDs change. No authority, loot rolls,
health mutation or host registration occurs in this query.

The host captures the original holiday once for its world, shares that value in
Accept and map parsing/NPC checks, and preserves it in the optional save
extension. Older saves keep the current world's holiday. Native remote loading
must rebuild holiday props/resources from the accepted value, as the original
`ApplyServerHoliday` path does; merely obtaining the bindings cannot correct a
world already rendered with another calendar.
