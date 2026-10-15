# Rust-only follow-on plan

The user chose to finish the working hybrid Bevy port first, then migrate the
retained gameplay runtime to Rust. The hybrid remains the current delivery
scope. This document plans the next phase; it does not claim a Rust-only build.

Today Bevy owns rendering, input, UI, camera, Rapier physics and presentation.
The engine-free C# libraries own authoritative rules, inventory, transactions,
networking, saves and parts of content/world generation. `CoreBridge` talks to a
separate .NET process through JSONL. Godot is a source/reference engine, not a
runtime dependency of that bridge. A Rust-only shipping build must remove the
.NET process and managed runtime dependency as well as its build requirement.

## Migration order

| Step | Replace | Mapping and acceptance gate |
| --- | --- | --- |
| 0 | Freeze the working hybrid reference | Tag a reviewed hybrid build; preserve source assets, content IDs, save fixtures, command/event tables and accepted gameplay journeys. Record CPU/frame time and memory on the same scenes. |
| 1 | Host API and core data types | Define typed Rust intents, accepted events, snapshots and a backend boundary around `CoreBridge`. Add an in-process Rust backend behind a development switch. Keep the existing bridge usable as a reference until replacement domains pass. |
| 2 | Data and compatibility helpers | Port required UnturnedDat readers, item/blueprint catalogs, original GUID resolution, exact source math and RNG behavior. Use Rust/glam types with explicit degree, handedness, basis and quantization conversions. Preserve original asset formats; C# source extractors may remain development tools temporarily. |
| 3 | Simulation and item rules | Move the 50Hz clock, movement/stance/vitals, inventory grids and exact item identities, equipment, crafting/knowledge/skills and fluid/food rules. Replay identical inputs against Rust and C# and compare accepted state and events, including rejection/no-mutation cases. |
| 4 | Binary protocol and transport | Port NetPak bit packing and quantization, packet headers, reliable channels, sequencing/ack/retry, UDP sessions, command validation and replication. Preserve published IDs and bytes where unchanged. Test Rust↔C# joins and real cross-process UDP; explicitly version any changed wire contract. |
| 5 | World authority | Move combat/ballistics, vehicles/occupancy/storage, deployables, utilities, resources, crops, wildlife/NPCs and quests. Preserve each accepted action's ownership, reach, lifecycle, save/load and remote-replica behavior. Migrate one authority domain at a time, with only one implementation applying mutations. |
| 6 | Source world generation and persistence | Port finite/infinite terrain, exact source rivers/holes/roads, placement/scatter, building generation, map parsing and save schemas. Reuse current differential source fixtures and edited-map reopen checks. Existing player saves and authored map files must reopen without regeneration or silent loss. |
| 7 | Remove the process boundary | Make the Rust backend the shipping default, replace internal JSONL exchanges with typed in-process calls and events, and remove automatic `dotnet` build/spawn paths. Keep expensive generation off the render thread and preserve fixed-step publication order. |
| 8 | Remove managed shipping/build dependencies | Build and package on clean Windows/Linux environments with no .NET or Godot installed. All required runtime metadata must be checked-in/generated Rust-readable assets. Any retained C# oracle/tooling must be optional and excluded from the shipping dependency chain. |

## How each replacement is verified

Existing engine-free tests supply the behavior to preserve; translate their
meaningful cases into Rust and retain differential checks against the frozen
C# reference during migration. Include source boundary values, failed admission,
request replay, disconnect/session resets, save/load, origin rebasing and real
UDP ownership/privacy. A matching function name or compiling translation is not
a completed domain.

Run the same playable journeys on both backends: movement and death/respawn;
inventory/storage/equipment; crafting/skills/stations; combat and projectiles;
vehicle driving, crew and airborne flows; building/utilities/resources; NPC
trade/quests; authored and generated maps; multiplayer join/reconnect and saves.
Render and open the affected captures. Continue the repository's visual review
workflow without reintroducing pixel-golden files.

The final gate is a fully playable Rust backend, passing regression and remote
journeys, compatible required saves/content, reviewed captures, clean-platform
packaging and measured performance. Merely hiding the .NET launch or keeping C#
as an essential runtime service does not satisfy the Rust-only goal.

## Boundaries to keep clear now

Finish current hybrid feature parity before starting wholesale rule translation.
Keep gameplay decisions in the existing authority rather than duplicating them
in Bevy UI or visual systems. Keep original IDs and units explicit, isolate host
adapters from rules, and retain reusable source/differential fixtures. This makes
current Bevy work reusable while giving the later Rust implementation a concrete,
working reference.
