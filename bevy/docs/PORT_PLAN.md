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

The user confirmed this hybrid architecture on 2026-10-15: finish a working
hybrid game first, then pursue a Rust-only runtime as the next phase. The
[Rust migration plan](RUST_MIGRATION_PLAN.md) maps the retained C# systems,
verification gates and removal of the .NET shipping/build dependency.

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
source protocol is version61; the port is now version78 for Nearby row operations (77), host-owned power inputs (fuel burn, battery charge, turbine wind, side triggers), accepted equipment,
resource contacts, mounted weapons, occupant rise, remote mounted flight
effects, NPC crew, vehicle electrical/lamp state, glazing/maintenance, full fleet fuel/health ranges, original skill nodes, bus doors, exact owned/viewed inventory and magazine actions, source crafting-station context, accepted respawn facing, fifth-wheel pairing and owner-only Nearby full-item projection with Take/Store, ground split/Empty and ground magazine jobs. Inventory has22 pages including vehicle compartments; AREA whole drags, equipment, wear and drop stay refused as in the source (see NEARBY_PORT.md).

## Milestones

Each row stays incomplete until implementation, integration, relevant tests and
visual inspection (where applicable) pass. A referenced core library alone does
not make its features playable.

| Stage | Scope | Status / evidence |
| --- | --- | --- |
| 0 | Source audit, branch, reproducible toolchains and original test baseline | In progress |
| 1 | Engine boot, real mesh/material/map loading, capture harness | In progress |
| 2 | Core bridge, fixed simulation, single local body, input/camera, server loopback | In progress |
| 3 | Player stances, water/ladders, vitals, death/respawn, skills, appearance | In progress; owner temperature band, status icons, deadzone grain/Geiger and held-bottle drink/fill in `SURVIVAL_FEEDBACK_PORT.md`; remaining parity below |
| 4 | Inventory, equipment, loot, attachments, weapons/ballistics/melee/grenades, viewmodels | In progress; remaining parity below |
| 5 | World saves, UDP multiplayer, replicas, reconnection, server browser and moderation | In progress; source save lifecycle (60 s autosave, entry restore, carry-over, console save/wipe) in `SAVE_LIFECYCLE_PORT.md`; remaining parity below |
| 6 | All vehicle types, suspension, seats/storage, tow/hitch, crane/turrets, damage | In progress; original fleet/mount geometry in `TURRET_PORT.md`, accepted mounted authority in `MOUNTED_WEAPONS.md`, local playable tank controls/effects in `MOUNTED_NATIVE_PORT.md`; source NPC native flight in `NPC_NATIVE_PORT.md` and crew presentation/authority in `NPC_CREW_PORT.md` / `NPC_HELICOPTERS.md`; complete fleet, airborne combat and remote journeys remain unfinished |
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

Ordinary accepted deployable body rendering is now integrated; source seating,
bounds and host collision are tracked in [DEPLOYABLES_PORT.md](DEPLOYABLES_PORT.md).
The specialized bodies, placement tools and utility networks remain unfinished.
The shared native look-at canvas is verified in
[INFO_BILLBOARD_PORT.md](INFO_BILLBOARD_PORT.md). Ordinary deployed bodies now
have source camera focus, basic health/fuel information and accepted hold-F
pickup, including an actual PEI main-game visual/input journey. Remaining
utility readouts, configuration, specialized bodies and placement tools are
tracked separately in [DEPLOYABLES_PORT.md](DEPLOYABLES_PORT.md).

## Parity gap ledger (2026-10-15 audit)

The source was audited against native consumers. All 86 CommandIds, every
EventId and every snapshot field were traced through `BridgeHost` actions and
`ClientEvents`. Rows leave this table only when the native implementation, the
authority path, tests with negative controls and opened captures all land.
Owner "agent" means an isolated worktree branch merged by the integrator.

| # | Feature (source) | Gap | Size | Impact | Owner / status |
| --- | --- | --- | --- | --- | --- |
| 1 | Deployable placement (`DeployablePlacer`, `BarricadePlacer`, `PlacementGhost`) | No held ghost/snapping/LMB place; cmd 7 test-only | L | high | agent G (in progress) |
| 2 | Special deployable bodies (`DoorDeploy`, `FridgeDeploy`, `StorageCrate`) | `ordinary()` excludes storage/fixture/fluid/turbine bodies | M | high | powered fixtures DONE (`17b9a035`); rest agent G |
| 3 | Player structures (`BuildTool`, `StructureManager`) | Absent; source is local-only, no command/save | XL | high | needs authority design |
| 4 | Survival HUD status icons (`HUD.cs`) | Bleeding/broken/starved/dehydrated/deadzone/temperature rows | S | high | DONE (`bc01598c`) |
| 5 | Temperature (`PlayerTemperatureSim`, `ThermalField`) | Band never computed/streamed (`TempBand`) | M | med | DONE (`bc01598c`) |
| 6 | Deadzones (`DeadzoneOverlay`, `GeigerCounter`) | Volumes exist, no overlay/Geiger | S | med | DONE (`bc01598c`; remote deadzone/tap aim, tank fill, mains switch, Reset Data open) |
| 7 | Save lifecycle (`WorldSaveDriver`) | Manual save/load only; no autosave, entry load or quit save | S | high | DONE (`bc01598c`) |
| 8 | Drink / fill at tap (cmds 69/70) | Never sent | S | high | DONE (`bc01598c`; remote deadzone/tap aim, tank fill, mains switch, Reset Data open) |
| 9 | Vehicle hand tools: paint, carjack, spare tire, pump, pour (cmds 29/50/55/56) | Not sent; `paintRgb` not rendered | M | med | agent F: paint/tire/carjack (cmd 29 with agent B) |
| 10 | Tow rope (`TowRope`, cmds 30/31) | Attach/detach, rope render/constraint | M | med | DONE (rope checkpoint; remote towing journey with row 16) |
| 11 | Restraints (`ArrestSim`, cmds 52/53) | Only Struggle | S | low-med | queued |
| 12 | Binoculars, umbrella, fishing | Absent | S-M | low | queued |
| 13 | Crops (`CropManager`, cmds 24/25) | No render/harvest | S-M | low-med | DONE (crops checkpoint, `CROPS_PORT.md`) |
| 14 | Store shelves (`StoreShelf`, cmd 45) | Contents not drawn; take-one not sent | M | med | agent H (in progress) |
| 15 | MP wire gaps (`RemoteWireGaps`) | Repair, respawn, weather/day, cosmetic flights, impacts, projectile expiry, part health, wildlife/zombie host data; joined-client proximity vehicle enter (`RequestEnterNearestPuppet`, 4 m, ranks above crop harvest) | L | high (MP) | queued |
| 16 | Hybrid dedicated server and moderation | No headless bridge host on 47872; live Godot server is protocol 61 | L | high (MP) | agent D (in progress) |
| 17 | Infinite/finite world MP | Origin protocol, island descriptor negotiation | L | med | queued |
| 18 | Menus/settings/bug report/launcher | Difficulty/config/island list/arena; limited settings; launcher still Godot | L | med | agent E (in progress) |
| 19 | Generated buildings | Preview stage; furniture data only (`61272db6`) | L | med | queued |
| 20 | World devices (elevator, car lift, crane, train, SAM, flares, magnet, vending, notes, traffic lights, AI drivers) | None native | L | low | queued |
| 21 | Fluid networks / item pipes (cmds 64-66) | Not sent; pipe events not bridged | L | low-med | check with power agent |
| 22 | Map and NPC editor | Absent | XL | low for play | last |
| — | Nearby AREA operations and selected-item Salvage | Drag/split/equip/drop/Empty/magazine/Salvage | M | high | DONE (`0b3075df`, protocol 77; whole ground drag refused as source) |
| — | Remaining power utilities | Specialized endpoints, fuel/ramp, consumer lights/audio, readouts | L | med | DONE (`17b9a035`, protocol 78; vehicle pour/siphon with row 9) |
| — | Fifth-wheel towing | Full-game rocking fixed; remote/MP towing journey, rigid cargo | M | med | integrator |
| — | Focus outlines (`OutlineOverlay`: items, shelves/containers, notes, puppets, fruit) | No silhouette/rim renderer natively; focus is prompt-only | M | med | queued |
| — | Packaging | Bridge/content paths are compile-time `CARGO_MANIFEST_DIR`; no relocatable release | M | high (ship) | queued |

N/A: the zombie attack loop/death loot and NPC map placements/quest persistence
are absent from the source itself. FreezeMode, TargetDummy, DamageNumbers,
DiscordPresence and the `--*test` harness flags are development aids.
