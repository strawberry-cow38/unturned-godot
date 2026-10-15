# Bevy migration plan and parity ledger

Source: `origin/Staging-Tinyclaw`, `2ca64fc61a8607598612896c547b89a36e773a88`,
2026-10-14 12:17:48 UTC. This was the remote branch with the newest commit when
the repository was cloned. All migration commits belong to `port/bevy`.

## Architecture

Bevy 0.20 owns the application, rendering, input, audio and physics. The existing
engine-independent C# libraries remain the authority for game rules and the wire
protocol. A versioned bridge hosts them without Godot. Initially its transport is
request/reply JSON lines to a local .NET process, allowing independent parity tests
and inspection before optimizing the boundary. The simulation remains 50 Hz;
replication follows mutations. Singleplayer continues to use a real server and
client over the existing memory transport. Inventory, combat and world changes
must route through existing authoritative commands.

Each native launch copies the built bridge and its dependencies into an owned
temporary directory. The child runs from that immutable copy, which is removed
after it exits, so later builds cannot overwrite mapped .NET assemblies. Build
the complete output before launching; `UG_BRIDGE_DLL` may select a separately
built bridge DLL in its own output directory. `DOTNET` selects the runtime host.

Original assets and GUIDs remain the content contract. Bevy loaders read the
existing runtime mesh, texture, rig, animation and map data. Preserve source
coordinate conversion and validate winding/UVs by rendering. Retail data absent
from Git must be detected and reported, never silently represented as a complete
map. Existing Godot code remains available as the behavioral reference until its
replacement has evidence of parity.

Measured source: 187 engine-independent C# files / 60,586 lines, 342 gameplay
files / 146,706 lines, 319 in-engine test files / 62,166 lines, and 194 core test
files / 45,176 lines. Historical repository summaries are stale. The current
source protocol is version61; the port is now version63 for accepted equipment
and resource contact replication. Inventory has22 pages including vehicle compartments.

## Milestones

Each row stays incomplete until implementation, integration, relevant tests and
visual inspection (where applicable) pass. A referenced core library alone does
not make its features playable.

| Stage | Scope | Status / evidence |
| --- | --- | --- |
| 0 | Source audit, branch, reproducible toolchains and original test baseline | In progress |
| 1 | Engine boot, real mesh/material/map loading, capture harness | In progress |
| 2 | Core bridge, fixed simulation, single local body, input/camera, server loopback | In progress |
| 3 | Player stances, water/ladders, vitals, death/respawn, skills, appearance | In progress; remaining parity below |
| 4 | Inventory, equipment, loot, attachments, weapons/ballistics/melee/grenades, viewmodels | In progress; remaining parity below |
| 5 | World saves, UDP multiplayer, replicas, reconnection, server browser and moderation | In progress; remaining parity below |
| 6 | All vehicle types, suspension, seats/storage, tow/hitch, crane/turrets, damage | In progress; remaining parity below |
| 7 | Deployables, building, power, fluids, appliances, crops, resource interactions | In progress; remaining parity below |
| 8 | Terrain/foliage/roads, procedural worlds/buildings, streaming, weather/water/audio | In progress; remaining parity below |
| 9 | NPCs, quests, trading, animals and remaining current gameplay | In progress; human rigs/focus/console, host capsule collision, accepted dialogue/trade/quest UI validated in `NPC_CHARACTER_PORT.md` / `NPC_UI_PORT.md`; remaining parity below |
| 10 | Full menus/settings/HUD/chat/map/console, editor and authoring tools | In progress; remaining parity below |
| 11 | Regression migration, gameplay journeys, reviewed visual captures, packaging | Pending |

## Parallel ownership

- Core agent: managed bridge, direct core parity tests and server integration.
- Content agent: runtime assets, terrain/maps, world rendering and character rigs.
- Interface agent: native UI/settings, input intents, audit and integration review.
- Integrator: Rust host/bridge client, player physics, build/runtime validation,
  cross-system wiring and commits of integration changes.

## Verification

1. Run existing engine-independent NUnit suite as the behavioral baseline.
2. Compare bridge operations with direct core calls; exercise real loopback
   command -> validation -> replication, including rejected actions.
3. Run Rust unit/integration tests for asset transforms, physics and lifecycle.
4. Build and run Bevy under a real Vulkan renderer (Xvfb/lavapipe if needed),
   capture PNGs, inspect them, and retain concise findings. Do not reintroduce the
   removed pixel-golden gate.
5. Review subsystem changes with another agent and correct findings before
   marking them done. Test gameplay journeys across subsystem boundaries.

## Known dependencies

Retail map heightmaps and bundles referenced by the source are not all in Git.
`UG_UNTURNED_DIR` or an explicit map path supplies them. The user supplied
`C:\Program Files (x86)\Steam\steamapps\common\Unturned`, accessible here under
`/mnt/c/Program Files (x86)/Steam/steamapps/common/Unturned`. PEI terrain, foliage
and player/loot spawns have been exercised from that install. Other maps and the
remaining systems still require their own checks. See `VALIDATION.md` for
baseline failures and the inspected capture evidence.

## Current integration boundary

Native player walk/stance collision, swimming, ladder attach/mantle, local
server commands, inventory/crafting screens, console/chat, saves, map services,
resource visibility/regrowth, road/procedural streaming and vehicle seats/physics
are implemented to varying levels and tested at their boundaries. These are
not completed parity stages. Native input and rendering still need the complete
weapon/attachment/use-item flows, third-person appearance, lean/free-look, all
vehicle specializations, dynamic deployables and utility networks, NPC/animal
presentation, multiplayer connection/replica flows, the editor and authoring UI.
Procedural infinite terrain and the separate finite island/building generators
are distinct systems; porting the former does not complete the latter.
