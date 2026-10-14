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

Original assets and GUIDs remain the content contract. Bevy loaders read the
existing runtime mesh, texture, rig, animation and map data. Preserve source
coordinate conversion and validate winding/UVs by rendering. Retail data absent
from Git must be detected and reported, never silently represented as a complete
map. Existing Godot code remains available as the behavioral reference until its
replacement has evidence of parity.

Measured source: 187 engine-independent C# files / 60,586 lines, 342 gameplay
files / 146,706 lines, 319 in-engine test files / 62,166 lines, and 194 core test
files / 45,176 lines. Historical repository summaries are stale. The current
protocol is version 61; inventory has 22 pages including vehicle compartments.

## Milestones

Each row stays incomplete until implementation, integration, relevant tests and
visual inspection (where applicable) pass. A referenced core library alone does
not make its features playable.

| Stage | Scope | Status / evidence |
| --- | --- | --- |
| 0 | Source audit, branch, reproducible toolchains and original test baseline | In progress |
| 1 | Engine boot, real mesh/material/map loading, capture harness | In progress |
| 2 | Core bridge, fixed simulation, single local body, input/camera, server loopback | In progress |
| 3 | Player stances, water/ladders, vitals, death/respawn, skills, appearance | Pending |
| 4 | Inventory, equipment, loot, attachments, weapons/ballistics/melee/grenades, viewmodels | Pending |
| 5 | World saves, UDP multiplayer, replicas, reconnection, server browser and moderation | Pending |
| 6 | All vehicle types, suspension, seats/storage, tow/hitch, crane/turrets, damage | Pending |
| 7 | Deployables, building, power, fluids, appliances, crops, resource interactions | Pending |
| 8 | Terrain/foliage/roads, procedural worlds/buildings, streaming, weather/water/audio | Pending |
| 9 | NPCs, quests, trading, animals and remaining current gameplay | Pending |
| 10 | Full menus/settings/HUD/chat/map/console, editor and authoring tools | Pending |
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
`UG_UNTURNED_DIR` or an explicit map path supplies them. The user has been asked
for any local installation path; bundled content and procedural code remain
available independently. Migration completion must not claim those absent assets
were tested.
