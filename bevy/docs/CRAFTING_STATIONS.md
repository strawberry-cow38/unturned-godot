# Crafting station access

The Bevy Crafting dashboard and Inventory quick-craft projection consume station
tags received by the owning client. A probe never grants tags locally. Every
craft request recomputes station access on the host before the existing
knowledge, skill, ingredient and timed-queue transaction runs.

`game/PlayerController.cs::CraftingStationTags` is the source contract: use the
current camera position, measure squared distance to each placed deployable's
origin, and cast one world-layer visibility segment excluding the player and
that station's body. Tags union with ordinal identity. Workbench access is 4 m;
Kiln access is 5 m. Source access does not require power, fuel or a burning fire.
The bridge reads the original `DeployableDef` tag/range definitions directly.

Protocol 72 appends Command 82 (`CraftingContext`) and owner Event 61. The command
carries a nonzero request ID, probe/craft mode, recipe index and finite camera
position. Clients never submit tags. Exact request replays resend their old
result without spending again; stale IDs, dead owners and invalid origins grant
no access. Disconnect removes the per-owner last-request record. Legacy Craft 16
keeps its original bytes and cannot assert station access. Protocol 71 peers are
explicitly refused at join.

The native camera cache records the completed source first-person, shoulder or
chase view in absolute coordinates with a session stamp. It survives a world
rebase and cannot cross sessions. Ordinary camera origins use the existing 3 m
body envelope. A larger chase envelope requires an accepted occupied vehicle and
host scene bounds; its conservative bound uses the source maximum chase length
34 m, maximum zoom 1.8 and size-dependent rise. This bounds the submitted camera;
it does not reconstruct or authenticate the owner's exact orbit or zoom. Server
station distances and line of sight are always recomputed from that origin.

The client displays the latest received station fact and expires it after 25
local network ticks. Moving the camera or sending another probe does not erase
that received fact while a new reply is in flight. This prevents ordinary
physics jitter, camera easing and remote latency from starving the dashboard.
Crafting and Inventory poll this same owner view. Cached tags never authorize a
craft transaction.

World-layer queries are separate from the existing bullet triangle set. Vehicle
HitMesh, glass, tires and folding doors lack the source world bit and cannot
occlude a station simply because bullets can hit them. Independent fallback
world hulls are transported with `sourceWorld:true, combat:false`; they do not
add a broad bullet hull or alter the existing navigation overlap query. The
queried station is excluded inside BVH traversal, so its nearest face cannot
hide a different wall farther along the segment.

## Verification boundary

Focused tests cover wire truncation/invalid values, tag identity/bounds,
owner-only replies, default-host refusal, forged origins, dead owners, request
wrap/replay/disconnect, source ranges/tag union, independent world/bullet masks,
actual seat/bounds admission, and real UDP browsing/crafting with a wall inserted
after a successful probe. The wall refusal leaves ingredients and queue intact;
removing the wall permits the existing accepted timed craft. Native tests cover
session/rebase lifetime and source camera selection in first/third person.

The layer-filter candidate passed 717 network tests, 196 managed bridge tests and
128 JSONL integration tests. Source camera-envelope followup passed 11 focused
bridge tests, including continuous small camera movements. The strict earlier
cache cleared every newly submitted camera and starved the live view during
ordinary physics jitter; that real UDP regression guards the fix. The current
native camera, seated, menu, lean and Crafting filters pass 35 tests.

An actual Vulkan first-person PEI journey used an isolated source recipe with a
Workbench requirement and an accepted placed station in a task-only save.
`bevy-station-station-search-fixed.png` shows received access and the enabled
recipe. `bevy-station-accepted-queue-ready.png` shows one accepted job, one log
remaining and the reusable saw retained. Actual walking beyond the source
radius yields the red blocker in `bevy-station-outside-station-final.png`; the
accepted inventory then contains two completed planks, one log and the saw.
All three PNGs were opened. Early captures taken before the queue event arrived
are not queue-completion evidence.

Shipping `blueprints.tsv` deliberately contains no station-required authored
recipes. Tests vary an isolated authored row; they do not restore the removed
retail recipe catalog. Target-item operations such as repair/ammo and the source
editor station/population interactions remain separate port work. General
replicated deployable rendering is still pending: this checkpoint verifies the
accepted station access and dashboard, not a visible native Workbench model or
the complete placement/building flow.
