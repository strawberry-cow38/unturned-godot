# Wildlife host and presentation

The Bevy listen host retains the original `AnimalField` fauna parser, species
selection, streaming, wander/vision/flee logic and authority in the managed bridge.
Retail map registration reads `Spawns/Fauna.dat`; the original in-memory horse
expansion does not modify installed map files. Wildlife spawns within 130m,
despawns beyond 165m, and retains the source 36-animal cap.

Native `wildlife.rs` creates one kinematic body and original skinned rig per
accepted animal. It adopts the exact managed host pose at 50Hz; wire-replica
positions remain separately available. It does not run a second animal brain or
publish competing transforms. Capsule height/radius, feet-at-origin placement,
270-degree visual yaw correction, source animation clips, textures and 95m skin
visibility range follow `AnimalAgent`/`RiggedCharacter`. The source cow/pig panic
banks play only on accepted startle events, without consecutive repetition, using
the original -2dB gain, 8m unit distance and 90m maximum distance. Deer and horse
have no panic clips in the shipped content and remain silent.

Accepted death disables the live capsule and stops the rig. The actual shipped
four animal rig JSON files contain **no ragdoll metadata**. Original
`RiggedCharacter.BuildRagdoll` therefore creates no physical bones; the intended
flop described in comments is not present in current source assets. This adapter
preserves that frozen corpse rather than inventing masses, joint limits or
colliders. The authoritative retirement removes the complete hierarchy after the
source 600-tick corpse interval. Reload resets native identities while preserving
the managed remaining lifetime; native presentation does not start a new timer.

The current Procedural mode ports `InfiniteTerrain`/`RegionStreamer`, which has no
source wildlife path. The separate finite generated island's `ProcIslandSpawn`
fauna points remain part of that generator's outstanding port. Wildlife does not
substitute fabricated points in InfiniteTerrain.

## Validation

Native tests load and animate all four real rigs, verify actual bone motion and
heading, accepted host transforms, living Rapier hit capsules, disabled dead
capsules, frozen corpse pose, retirement hierarchy cleanup, load-epoch reset,
source panic banks and once-only retained panic audio after replica retirement.
Five focused native tests pass. The managed wildlife checkpoint also passed its
source behavior, damage, persistence, terrain and origin tests. An actual PEI
Vulkan/Xvfb journey registered all 60 original fauna points, used the original
`spawnanimal cow` command, and exercised idle, glance, wander and flee states.
`artifacts/bevy-wildlife-cow-rear.png` and `bevy-wildlife-cow-side.png` were opened
and inspected: the original cow atlas, rear/side silhouettes and articulated legs
render correctly in the terrain. No live ECS or shader errors occurred. The
screenshots are inspection artifacts, not golden-image gates.

Shared accepted-hit flesh particles, impact sounds and HUD markers are the next
presentation checkpoint; they are not yet claimed by this wildlife milestone.
