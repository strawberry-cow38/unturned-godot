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
  `spawn:[x,y,z]`, `starterKit:false`. Returns protocol version, capabilities and
  action names. The explicit starter kit is a development fixture seeded by the
  authority; normal starts have empty inventories.
- `simulate`: `input:{moveX,moveY,jump,grounded,stance,yaw,pitch,velocity?,heldItemId}`.
  Calls the original movement sim once at 0.02 seconds. Stance is `stand`, `sprint`,
  `crouch` or `prone`. Yaw/pitch are degrees. Returns world-space `velocity`, capsule
  `height`, and `jumpLocked`. Run before Bevy collision; it advances no server tick.
- `tick`: `input:{position:[x,y,z],velocity:[x,y,z],grounded,yaw,pitch,stance,heldItemId}`.
  Supply the resolved Bevy body after physics. Sends the existing client state
  command, pumps transport/client/server, then publishes replication. No second
  player body is simulated. Authority recovery increments `correctionVersion`;
  Bevy must teleport/reset its body to `player.position` only when that changes.
- `snapshot`: read current replica state. Inventory has all 22 pages.
- `action`: `name` and `args`, as below. Commands go through `NetWorldClient` and
  the existing server command validators. Most effects appear within 2–4 ticks.
- `save` / `load`: `path`, using original versioned `WorldSave` format and map gate.
- `terrain`: `seed,x,z,lod`, even before `init`; original `InfiniteTerrain` output.
- `shutdown`: reply and exit.

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

Current integration limits: terrain/body collision must be supplied by Bevy;
static combat ray callbacks, host-side entity scene registration and full
presentation are separate integration work. An available command does not imply
its required world entity has been registered. Initial catalog loading covers
shipping TSV metadata and gun `.dat` parsing; game-specific catalog overrides and
generated recipe tables are being migrated next.
