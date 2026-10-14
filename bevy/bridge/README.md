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
  `spawn:[x,y,z]`, `yaw`, `seed:1337`, `starterKit:false`. Returns protocol version, capabilities and
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

`state.events` contains `{seq,kind,data}` and remains available until the consumer
acknowledges it with `ackEvents:<lastConsumedSeq>` on any request. Consumers must
deduplicate and acknowledge only after processing. Events include console, chat,
fire, hit, impact, crafting queue, death and respawn. Result-only operations
(simulate, terrain, catalog, worldGeometry, worldLoot, worldSpawns, worldSpawn,
worldRay) omit state by default; `includeState:true` overrides that. Tick recipes
are emitted only when inventory/skills/knowledge change; snapshot returns them all.

Static geometry accepts `meshes:[{vertices:[[x,y,z],...],indices?:[i0,i1,i2,...],
terrain?:false,destructibleIndex?:-1}]`; omitted indices mean sequential triples.
Mark terrain meshes with `terrain:true` so ground-height queries exclude roofs.
Alternatively use `terrainPatches:[{origin:[x,z],cells,spacing,heights,holes?}]`,
with (cells+1)² row-major heights and cells² optional hole flags, or
`terrain:{seed,x,z,lod}` for the original generator. `replace:true` clears previous
geometry; use `replace:false` to append chunks. Defer BVH construction with
`finalize:false`, then finalize the last chunk. Combat rays use this host-owned
BVH; the player never supplies a claimed hit. `worldRay:{from,to}` is a read-only
diagnostic returning `{hit,point,destructibleIndex}`.

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
entity or host-side service is registered. Vehicle/animal/zombie physics, resource
and crop registration, animation and remaining presentation need Bevy adapters.
Weather timers restart on load as in the original scene lifecycle; clock/day and
retail loot provenance persist. The original server's default-gun fallback and
shared per-player ammo behavior remain core semantics rather than being rewritten
in this bridge.

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

Tree/ore health and their game-node harvesting lifecycle still need host adapters;
the resource table includes them for stable indexing but only original forage
plants currently have interaction metadata. Container random rolls preserve the
source's unseeded range/distribution contract through a portable RNG adapter;
the resulting inventories are saved, rather than rerolled on load.
