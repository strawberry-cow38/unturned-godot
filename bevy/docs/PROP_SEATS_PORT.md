# Prop seats and reclined bodies

The native seat renderer follows `game/PropSeat.cs` and the local/remote body
branches in `PlayerController.cs` and `RemotePlayers.cs`. A chair places the rig
origin 0.422m below its cushion and uses `Idle_Sit`. A reclined seat leaves the
feet at the anchor, uses `Idle_Stand`, and rotates the visual body by -90 degrees
around the seat's X axis. It clears local look pitch and lean so neither can
bend the torso through the mattress. The controller remains upright and its
collision stays disabled until the accepted seat is released.

First-person bed eyes use the original seat-local `(0, 0.25, -1.5)` offset. Third
person uses the shared shoulder camera, including its collision sweep and world
seat yaw. Switching perspectives restores the bed eye without moving the body.
Remote recline requires both accepted sitting stance/occupancy and authored
`recline` metadata for that seat ID; late metadata changes the existing rig.
Rotation is rebuilt from the accepted yaw each frame, so it cannot accumulate.

## Which beds exist in the original

`WorldBuilder.LoadSeatCatalog` has only position/facing pairs. The retail
`Bed_*` meshes are ordinary props, not reclined seats. The original `Bed.cs`
creates its own reclined `PropSeat` on a 1m × 0.45m × 2m box. Its only production
caller is the optional no-map/CI fallback furniture path; normal map builds
explicitly disable this furniture. The separate native fallback fixture port
owns registration, claim interaction and materialization. This renderer change
does not add new bed interactions to retail map props.

## Verification

Four regressions check all four seat headings against the original anchor/eye
constants, the real local rig and pitch reset, late accepted remote occupancy
with stable rotation and hull restoration, and the native controller/camera
after an actual managed-authority seat acceptance. The latter exercises first
person, third-person shoulder orbit, return to first person and seat exit.

The opt-in production remote-rig fixture supports `UG_REMOTE_POSE_BEDS=1` and
`UG_REMOTE_POSE_SIDE=1`. It uses the original box mattress geometry and two
orthogonal bed headings; the other two peers retain prone/sitting controls.
These fixed accepted replicas test presentation, not a complete live bed
claim/sit/release journey.

`bevy-reclined-front.png` and `bevy-reclined-side.png` were rendered and opened
for independent source/code and image review. They preserve the source's exact
face-down pose and mattress intersection rather than inventing a corrective
offset. Both renderer runs exited successfully without shader errors. The
combined native suite passes 385 tests (three opt-in render fixtures ignored)
against immutable `profile-config-49`; the four recline regressions also pass
in the frozen `native-reclined-checkpoint` executable.

## Explicit demo bed

`--demo-furniture` enables the original fallback bed for a native diagnostic
session; it is off by default and never changes the authored retail `Bed_*`
props. `WorldBuilder.SpawnInteractables` supplies the bed's offset (-5, +2) from
`InteractableAnchor`, its 90-degree yaw, and the mattress dimensions/material.
The anchor uses the original `LevelSpawns.PlayerSpawns` parser and Godot's seeded
PCG selection (seed 7); it ignores player spawn/capture overrides. Missing map
spawns retain the original `(0, 0, -350)` anchor. The native dedicated fallback
launcher remains separate work; this flag makes the fixture callable now.

The server registers bed ID 1 in the independent bed namespace. Its reclined
seat follows the complete authored seat list, exactly as the original
`InteractableNetSync` world walk appends the bed's child seat. UDP host and client
must both use `--demo-furniture` with the same map/content. Clients obtain the
same complete source seat order through read-only `mapBindings`, and do not
register a bed, seat, or collider with authority. The native bed appears only
after both IDs are present in accepted snapshots. This diagnostic is not
advertised by the handshake; joining without the flag does not create its
presentation. Normal retail sessions need no flag and contain no such bed.

The visible box and world-layer collider are both 1 × 0.45 × 2 metres, centered
0.225m above the registered base. The material is source sRGB `(0.55, 0.52, 0.60)`
with roughness 1; direct unobstructed focus uses the source emission color
`(0.20, 0.24, 0.35)`. F first sends `claimBed`. Accepted ownership enables
`Lie down`, which sends `sitSeat`; accepted occupancy uses the shared recline
renderer and changes F to `Stand up`. Neither prompt changes ownership or pose
locally. A stolen-claim attempt still reaches the original server rejection.
The authority's claimed base/yaw also becomes the original respawn location.
The same box triangles block authoritative bullets; streaming and load rebuilds
republish its named geometry. Session cleanup retires its render/seat metadata,
and origin changes translate its cached base once alongside world roots.

The host adapter now uses the original `ServerCombat.DamageBarricadeAlong` seam
and `Bed.cs` 200-HP rule. Accepted bullets and melee supply their original
`VehicleDamage`; the nearest actual world collider must be this bed's named
geometry. Static walls and other dynamic colliders block that damage. At zero
health the authority removes the bed claim, its child seat, and its collision
geometry; accepted snapshot removal then retires the native box and metadata.
Repeated host registration or a delayed geometry upload cannot heal or revive
the destroyed fixture. There is no client damage operation.

The source server melee ray is horizontal at feet + 1.2m, so a low mattress on
the same ground is below that ray. This inherited targeting limitation is kept
separate from the damage adapter. Source explosion code does not call bed damage.
The original save format has no bed health/claim record; native session reload
reconstructs this diagnostic fixture. Persistence extensions and the standalone
demo door remain separate work.

Seed-selection references: Godot 4.6
[RandomPCG](https://github.com/godotengine/godot/blob/4.6-stable/core/math/random_pcg.cpp)
and [PCG32](https://github.com/godotengine/godot/blob/4.6-stable/thirdparty/misc/pcg.cpp).
The deterministic values are checked against the actual Godot 4.6.2 executable,
including a large bounded range. The PCG32 implementation is derived from M. E.
O'Neill's Apache-2.0 minimal PCG code through Godot's copy.

The seven focused native tests exercise the actual authority and UDP peer, an
existing authored-seat prefix, accepted claim/seat/stand and claimed-bed respawn,
remote registration rejection, default-off/disconnect cleanup, original seeded
selection, and real Rapier focus/occlusion. The UDP test exposed a read-only
`worldSpawns` request incorrectly missing from the remote bridge allowlist; it
fails against that older bridge and passes with the parser allowed. Claimed-bed
respawn preserves the original base plus 0.5m lift. Both original respawn paths
ignore the bed's stored yaw and retain the player's heading.

The production `--play --demo-furniture` PEI journey was rendered and opened as
`bevy-demo-bed-claim.png`, `bevy-demo-bed-owned.png`,
`bevy-demo-bed-reclined.png`, and `bevy-demo-bed-standing.png`, each with its
accepted-state JSON. Actual F-key input changed bed 1 from unclaimed to owner 1,
occupied appended seat 394, then released it; third-person rendering shows the
original reclined body on the mattress. The run exited cleanly via Quit and had
no shader validation errors. Its immutable pre-NPC bridge reported the newer
unrelated `npcView` request as unsupported; this did not affect bed operations.

The damage adapter has three managed regressions: exact 200-HP retirement with
an occupied child seat and stale host uploads; nearest static/dynamic occlusion
and origin translation; and six accepted Eaglefire shots from a real UDP peer
(35 VehicleDamage each), followed by replicated bed/seat removal. The last test
fails against the pre-adapter bridge with the bed still present. Eight native
bed tests include accepted gunfire retiring the rendered collider and recline
metadata without rematerializing it. The immutable candidate also passes all
56 managed adapter tests and all 121 bridge protocol/authority tests.

The production PEI disappearance journey uses the same original fixture and
actual mouse fire input. `bevy-bed-destruction-five-hits.png` and its snapshot
show the claimed bed still present with ammo 25 and a wood impact decal;
`bevy-bed-destruction-sixth-shot.png` shows clear ground, no prompt, ammo 24,
and both bed 1 and child seat 394 absent. Both images received independent code
and visual review. The renderer exited successfully via Quit without shader
errors. A separate loaded-save seated-position correction loop was discovered
and retained in the preceding snapshots for the player-authority follow-up;
the sixth shot and disappearance were captured standing. The eight native bed
tests also pass against the immutable v63 `resource-contact-59` bridge.
