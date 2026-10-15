# Native NPC characters

`entities/npcs.rs` presents the original `NpcCharacter.cs` using the shared human
rig, clothing, face and gear pipeline. Authored map placements come from the
managed `MapServices` DTO; keys and placement order identify characters because
Chef and Sous-Chef share numeric asset ID756. Positions already use the engine
coordinate frame. The bundled checkout has no `editor_<Map>_npcs.txt` files, so
normal maps correctly contain no authored NPCs until such content is supplied.

The fixed world capsule retains radius0.35, height1.8 and centreY0.9. Original
`Idle_Stand` animation advances every frame, with source skin colour validation,
empty clothing slots and clamped face selection. The original source does not
apply the catalog's hair field. No replacement NPC meshes or outfits are used.

The current source camera trace reaches2.6m and stops on world/vehicle geometry.
Look focus enables the nameplate; UI capture or obstruction disables it. The
shared60m visibility check respects that explicit gate. After the first posed
and propagated skeleton frame, the nameplate measures the eight corners of each
actual hat/mask/glasses mesh bounds and sits above the highest point. Remote
player nameplates retain their existing ungated range behavior.

F records speaker presentation context and sends the original `npcTalk` intent.
Missing catalog dialogue produces the source “has nothing to say” notice.
Characters never open or navigate an optimistic conversation: the separate
native UI consumes accepted `npcView` state, documented in `NPC_UI_PORT.md`.

The original diagnostic `npc` console command lists catalog keys or accepts
`npc <key substring> [distance] [talk]`. It uses the body look heading rather than
free camera gaze, clamps distance1–20m, samples the actual terrain ahead, faces
the player, and optionally sends the same talk intent. These diagnostic puppets
are local presentation, as in the original console; they do not create shared
NPC gameplay authority or write authored placement files. Session/map changes remove them.
Origin rebasing translates live roots and cached placement data without recreating
the rigs or resetting their animation.

Five native integration tests use the actual managed catalog and shared character
pipeline. They cover duplicate asset IDs, original appearance/posed chef hat,
Rapier occlusion and F→accepted dialogue, map/rebase/session identity, terrain
sampling with independent camera gaze, missing dialogue and visibility gating.
The opt-in Vulkan fixture uses explicit diagnostic placements for Captain, Chef
and Medic. It verifies the production physics focus and measured label, then
captures the actual shared assets; it does not imply shipped authored map content.

Validation: the five focused tests pass against immutable `npc-view-53`, as does
the opt-in Vulkan fixture (4.29s). The combined native working tree passed403
tests with four manual renders ignored before the final vehicle-focus guard;
that guard is covered by the subsequent focused run. Opened captures are
`bevy/artifacts/bevy-npc-characters.png` (all three original bodies) and
`bevy/artifacts/bevy-npc-chef-focused.png` (actual PEI console spawn and F focus).
The measured Chef plate isY2.4816253, above the real posed hat bounds. At ordinary
player eye height the full name is readable. The lower1.25m fixture camera lets
the hat obscure the bottom of the label: the source fixed top+0.15m anchor and
normal depth testing do not guarantee screen-space separation from every angle.
This perspective limitation is retained and recorded, not hidden by disabling
depth or introducing an invented height offset. Spine/Skull/Skeleton tracks in
the original idle clip are constant; this is not an unposed first-frame measure.
Both captures and the body/focus implementation received independent review.

The native capsule blocks movement and interaction rays. A subsequent collision
checkpoint also publishes its exact analytic shape into the playable/listen
host's world geometry. This follows `MpLoopback.GodotWorldRay`, which queries the
shared scene's world-layer bodies. `ZombieCombat.RayCapsule` and `CapsuleNormal`
provide the existing exact rounded geometry and outward normals; capsules remain
opaque world bodies with no invented damage, loot or NPC entity replication.
World and cosmetic rays, blast obstruction, and navigation box queries include
them. Native publication uses placement identity and the current geometry revision;
removal, terrain replacement, origin barriers and session changes are covered.

Remote scenes retain their local movement/focus colliders but do not publish
authority geometry, and the bridge refuses a remote upload. Source dedicated
worlds explicitly omit NPC puppets; this change does not fabricate them there.
The host's diagnostic `npc` command does produce an opaque collider in its own
scene, exactly as the original shared viewport does.

Collision validation: all seven native NPC integration tests pass, including
geometry replacement/rebase/session cleanup and the remote publication gate.
Three managed regressions cover exact cap normals, nearest-hit identity and a
real UDP accepted shot that spends ammo but cannot damage a tree behind the NPC;
the same shot kills the tree after the NPC collider is removed. The native host
regression fails against the previous `resource-contact-59` bridge at its first
expected capsule hit. The frozen combined checkpoint passes407 native tests
(four manual fixtures ignored) and62 managed tests. The updated Vulkan fixture
passes in6.72s and asserts that the visible Chef also stops the host ray;
`bevy/artifacts/bevy-npc-authority-capsules.png` was opened and reviewed. The
low-camera label perspective limitation described above remains unchanged.

This checkpoint does not complete NPC/editor parity. NPC placement authoring,
helicopter behavior and remaining NPC integration are separate work. Accepted
dialogue, dollar trading and quest UI journeys are recorded in `NPC_UI_PORT.md`.
The current source's missing quest progress/save producers are recorded in
the interface audit rather than implemented with invented rules.
