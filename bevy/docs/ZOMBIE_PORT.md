# Native current zombie field and bodies

The live source is `ZombieChunkField`, `ZombieFlowField`, `ZombieTables` and
`ZombieBody`, not the removed per-agent pathfinder or old speciality controller.
The managed host preserves the original chunk, wardrobe, noise, navigation,
damage, death and refill decisions. Only promoted HOT bodies are materialized by
Bevy. Each has one Rapier character solve; its position, velocity, heading and
unstuck window are published before the normal authoritative tick. There is no
second server movement solve or continuous replica teleport fighting the body.

The native capsule is source height1.8/radius.4 at feet+.9, world-only collision,
55-degree slope and .5m floor snap. Gravity is22m/s². Heading uses the source
exponential10/s response. Actual resolved speed selects Idle/Move and scales the
original Move_0..3 clips by the measured6.87m/s natural speed. Those skeletons
advance once at50Hz, independently of rendered frame rate. The one-second
unstuck window measures net progress, then steers along real contact normals.
Its timers and world position are saved through the host and rebased with the
world origin. Accepted death disables the live capsule immediately.

Appearance uses the original human rig, authority-selected wardrobe IDs and
source skin (.588,.620,.502). The native clothes material composites shirt/pants
albedo, emission and metallic maps with the original wear equations. Original
face19 follows Skull; wearable meshes follow Skull or Spine with their extracted
offsets. Hexadecimal accessory colors in the content table remain supported.
There is no extra random wardrobe roll on the rendering side.

The original11 physical bones come from `rig.json`: boxes, masses, damping,
nearest physical ancestors, circular swing and X twist limits. Adjacent bodies
do not collide; other bones and world geometry do. Spine receives the accepted
death impulse and Skeleton half. Bones occupy an unrotated world container so
Rapier writeback does not distort joints beneath a rotated character parent.
The rendered rig follows those bodies until the authoritative8-second corpse
retirement removes both hierarchies.

Original vehicle noise is also published to the field: an accepted horn emits
32m once, and a running engine emits every.4s with radius48 times signed forward
speed fraction, only above2m. Reverse travel and idle are quiet as in source;
audio volume never changes hearing. Accepted shot/melee/footstep hearing and rain
masking already run in the managed host.

## Validation and remaining scope

Five native physics tests cover grounded travel/cadence, a real wall and
contact-normal escape, oscillating net-progress detection plus origin shifts,
physical death/cleanup, and paused/save-load motion. Character tests exercise
actual gold clothing maps, face14 emission support, Skull/Spine accessory
placement, hexadecimal color, clean wardrobe replacement and fixed animation.
A regression checks accepted vehicle horns and engine cadence.

The real PEI session registered1403 authored spawn points (53 source water points
rejected),396 potential zombies and43 HOT bodies around the inspected town.
`artifacts/bevy-zombie-medic-live.png` and `bevy-zombie-chef-live.png` show native
wardrobe, Skull-mounted hat, walking characters and the interior chef face.
`bevy-zombie-accepted-death.png` plus its JSON records five accepted20HP rifle
hits (ammo30→25), authoritative death and independent head/arm/torso tilt. These
frames were opened and independently reviewed. No clothing shader or renderer
errors occurred, and the session exited cleanly through Pause/Quit. The shaded,
turned close faces do not establish detailed facial-texture parity.

A real-host origin regression rejects both stale zombie body and hearing
publications after a rebase, then accepts them after native origin adoption.
Existing native impact rendering consumes accepted zombie hit/death facts with
source blood/hitmarker deduplication.

Current source zombies have no attack loop or death loot; the table health,
damage and loot blocks are explicitly skipped by its parser. InfiniteTerrain
also deliberately has no source zombie/fauna registration. The separate finite
ProcIsland generator is not yet ported, so no invented infinite-world zombie
points are added. Human NPC dialogue/vendor/quest presentation remains separate.
The shared clothing layer does not yet expose player gear-condition shaders,
night-vision lens toggle or a ragdoll graphics setting. Those are player/NPC
integration work, not extra zombie rules.
