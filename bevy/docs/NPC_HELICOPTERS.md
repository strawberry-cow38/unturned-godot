# Original helicopter controller

`NpcHeliBrain` is generated from the current `game/NpcHeli.cs`. It preserves the source transit/arrival/orbit envelopes, three-state collective, attitude damping, attack-triggered 300-second lock, canopy concealment, burst sizing, aim slew, firing cone and post-readiness spread. `NpcHeliServices` reads accepted vehicle poses and returns the original four control axes. Only Bevy integrates the airframe. A human driver suspends its AI controller; an occupied gunner seat excludes AI aim and fire, leaving the human's finite belt intact.

Host setup uses `worldNpcHelis`:

```json
{"mapBounds":[-1024,1024,-1024,1024],"nodeFile":"nodes.tsv","terrainPatchesReplace":true,"terrainPatches":[{"origin":[-1024,-1024],"cells":1,"spacing":2048,"heights":[30,30,30,30]}]}
```

These example heights are only protocol documentation. Production supplies the complete original tile vertices in world metres. Additional calls append patches; set `terrainPatchesReplace` on the first call. Positive scalar spacing means increasing X/Z rows; signed `[dx,dz]` also works. The sampler uses the original bilinear interpolation and edge clamp, independently of the nearby rendering/collision triangles. Holes do not change source `Terrain.SampleHeight`. The current tile metadata does not carry the original editor's additional analytic river carving, which remains a separate integration gap for edited rivers.

The original cheats-gated console command `spawnheli <name>` selects the nearest actual named node, then the nearest map edge inset by 60 m, samples terrain plus 50 m and points the airframe toward the node at half its original maximum speed. All vehicle stats, health, hit regions, seats and guns come from the embedded original fleet export. No client can choose damage or unlimited ammunition. `nodeFile` is a content filename; existing source map-node parsing is reused. Hosts without a terrain use the original test-harness absolute 50 m datum. Hosts that configure map bounds must upload terrain before spawning.

For explicit host fixtures, `vehicles:[{netId,target:[x,y,z],targetName}]` attaches the source controller to an existing accepted helicopter. This is host-only. `state.npcHelis` contains `netId`, `active`, `rotorRunning`, `controls:{collective,yaw,pitch,roll}`, `phase`, `mode`, `target`, `targetName`, `lastSeen`, `lockSeconds` and source telemetry. Remote clients receive the ordinary vehicle/mount/shot/flight replicas and no controller. Native startup must apply `SpawnRotorRunning`/instant ignition once, permit these aircraft outside the player's local render radius and drive the same physical vehicle from these controls.

Bullet contacts carry their original launch origin to the AI. Melee and splash do not start the source attack lock. The controller's original wall-clock lock uses accepted 50 Hz authority time in this adapter, so paused singleplayer cannot silently expire it. Source `DebugThrust` is the spec's constant helicopter thrust, not the current lift acceleration. Map/resource registration supplies the original scaled canopy cylinders; their source records persist after a tree is felled. Saved controllers retain targets, lock/burst state, accepted attack origin, full terrain grids and source node offset. Rebase shifts these coordinates without moving timers or creating another body.

AI shots use a synchronous, single-use readiness ticket. Only after source cadence/crew/seat checks succeed does the original controller sample spread and launch the same accepted direction into both authoritative and cosmetic flight. Tickets are bound to the issuing service, tick, mount value and occupancy; delayed, replayed or foreign tickets cannot fire. Human firing continues to use the existing validated mounted-input protocol.

The first managed checkpoint validates actual UDP spawning/refusal, authoritative body-input coupling, driver suspension, saved lock/terrain, signed-grid interpolation, original canopy shape, accepted NPC launch and stale/foreign/seat-takeover ticket rejection. Native playable AI flight and crew presentation/damage are separate subsequent checkpoints; this document does not claim those visual paths are complete.

Validation at this managed boundary: 105 bridge NUnit, 680 core networking and 121 JSONL integration tests pass. Independent source/controller/ticket review found no blocker. The exact immutable v66 candidate is `bevy/.local/npc-brain-v66-final/UnturnedBevy.Bridge.dll`, with its source overlay manifest alongside.
