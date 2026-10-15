# Current NPC source audit

The branch's current zombies differ substantially from the historical project
catalog. `ZombieController.cs`, `ZombieField.cs`, `ZombieNav.cs` and
`HordeSpawner.cs` no longer exist; a few `.uid` files and older log entries remain.
Port the following live implementation, not the retired per-agent pathfinder.

| Source | Actual responsibilities | Native/authority boundary |
| --- | --- | --- |
| `game/ZombieChunkField.cs` | Retail point/table parsing, 64m chunks, HOT/WARM/COLD/FROZEN hysteresis, simulated budget64/player, 48 visible-body cap, source respawn300s, noise alerts, visible target selection, separation | Extract original decisions server-side; register/promote native bodies only when required; data-only tiers remain managed |
| `game/ZombieFlowField.cs` | Shared regional navigation toward accepted noise/player targets, cached world walkability | Retain source field algorithm; bridge world probes must use real collision geometry |
| `game/ZombieBody.cs` | One live character movement solve, gravity22m/s², floor angle55°, snap.5m, contact-normal unsticking, smooth heading, cadence-scaled clips, wardrobe, damage/death | Native Rapier character controller owns this one host body; authority owns damage and lifetime; publish physical result back to the field |
| `game/ZombieTables.cs` | Actual `Spawns/Zombies.dat` wardrobe parsing and deterministic rolls; health/damage/loot blocks are explicitly skipped | Export original selected wardrobe IDs, outfit seed, and clip variant; no replacement random wardrobe |
| `core/UnturnedSim/ZombieSpatial.cs` and `ZombieRegions.cs` | Older spatial queries and chunk helpers with no current ZombieChunkField callers | Preserve the actual live field scans/chunks; do not replace the extracted field with dormant helper behavior |

Current zombie appearance uses the player clothing shader and original wearable
shirt/pants/gear assets, source skin tint (0.588,0.620,0.502), and fixed face19 on
the actual skull. The old baked zombie atlas is not the current wardrobe pipeline.
Each record keeps its outfit seed across body promotion/demotion. Move_0..3 clips
use the measured6.87m/s natural speed while actual source travel speed is4.5m/s.
A dead body disables its live capsule, starts the human rig's actual ragdoll, and
expires after8s; unlike animal assets, `rig.json` does contain physical-bone data.

The source fields no longer imply the old speciality AI or horde-spawner code is
present. Audit actual callers before claiming those removed behaviors need a new
implementation. Preserve all behavior that remains in the selected branch.

Human NPCs are a separate path: `NpcCharacter.cs` builds the shared player rig,
wearable gear and face from `NpcCharacterDef`, a static radius.35/height1.8 capsule,
focused nameplates measured above headwear, and interaction through the existing
F focus. The native body/focus/console implementation and its validation are
recorded in `NPC_CHARACTER_PORT.md`; accepted catalog/dialogue/trade/quest data
and native UI progress are recorded in `NPC_UI_PORT.md`. Those checkpoints do
not imply completed editor parity. Playable/listen host combat now receives the
exact opaque NPC capsules; remote scenes cannot upload their local diagnostics,
and the source dedicated-world omission is preserved. `NpcHeli` and its turret
tests are another explicit subsystem.

Wildlife implementation and its verified asset limitations are recorded in
`WILDLIFE_PORT.md`.

The current managed/native zombie implementation, physics and inspected capture
evidence are now recorded in `ZOMBIE_PORT.md`.
