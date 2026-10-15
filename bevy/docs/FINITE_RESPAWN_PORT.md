# Original finite spawn and respawn service

The native finite entry now consumes EditorPlayMode's regular authored player
pool. EditorSpawns.PlayerSpawnPoints includes every non-alt point regardless of
Type. Initial entry chooses uniformly, uses the exact stored XYZ without terrain
grounding, and keeps yaw0. Respawn follows PlayerController.PickRandomSpawn and
Respawn: a current claimed bed wins, its position gains0.5m and its yaw is ignored;
otherwise a new regular point supplies both position and yaw. The source origin
placeholder guard runs afterward and preserves the already chosen yaw.

`FiniteSpawnPool` caches a pending random point per owner so origin-rebase
preflight and actual revival use the same selection. It rechecks bed state during
the countdown, clears random selection when a bed wins, rerolls after bed removal
and forgets a disconnected owner. Rebasing translates the pool and cached target
while the source origin test remains in the original frame. Only regular
position/yaw rows are persisted with existing service specifications. Saved games
restore those rows without rereading changed authored files or regenerating a map.
The initial read-only map export returns one position for native streaming; init
installs the same authored pool through `finiteDirectory`. Parsed map objects are
not retained by this service.

Protocol73 appends owner-only Event62 `RespawnPose` (uint32 accepted tick and the
existing quantized degree yaw). It is sent immediately before the unchanged
PlayerRespawned payload on the same reliable ordered stream. The client waits for
its own respawn, drops duplicate/stale ticks and resets pending state on session
replacement. Other owners, beds, ordinary maps, initial entry and save loading
emit no extra yaw fact. Published protocol72 is rejected at join. The existing
PlayerRespawned two-byte body is unchanged.

Every accepted self respawn resets source pitch to0, including bed and ordinary
map respawns. Bridge `player.respawnLook` stores its own version and optional yaw;
this stable fact survives a later native input publication. Native publish_body
consumes each version once, independently of position correctionVersion. Load and
session initialization establish a fresh baseline. Normal snapshots and later
teleports therefore cannot keep imposing the old facing after the player looks
around.

Verification on immutable `finite-respawn-v73-verified`:208 managed tests pass,
including original-source pool parsing, cached selection, bed changes, save/load,
rebase and a real UDP owner-only roundtrip. The UDP negative control removes only
the bridge typed-fact consumer: regular respawn and accepted player state still
arrive, but the one-time yaw disappears. All721 core tests pass, including the
unchanged legacy bytes, truncated input, correlation, duplicate/session guards
and explicit72 rejection;128 JSONL tests pass. Native `native-info-respawn-first`
passes two look-adoption tests and four finite tests, including exact authored
initial altitude and existing empty-chunk retirement/streaming coverage.
The final `native-coupling-geometry-first` executable also passes both look tests;
the integration case runs the production publisher with a real bridge through
death, respawn and a later position correction, rather than calling adoption alone.

Actual Vulkan `native-finite-respawn-main` reopened an isolated source map with
regular point `[659,60.356,-388]`, yaw−90. The initial native spawn was exact and
faced yaw0. Real mouse movement changed yaw/pitch to283.2/−33.6; console sethp0
caused accepted death, and the real Respawn button returned to the authored point
with yaw270/pitch0. Another real mouse movement changed the view to241.2/−14.4;
a later accepted teleport from X659 to662 preserved both angles and lookversion2.
Opened artifacts are `bevy-finite-respawn-initial`, `-turned`, `-dead`, `-returned`,
`-free-look`, and `-after-correction` PNG/JSON. The process exited through
Pause→Quit with code0. The camera uses normal player input, with no fixed-camera
override. These are inspected source/authority/render checks, not pixel goldens.

Remaining finite scope includes fresh-only original EditorPlayMode populations,
custom fixtures/editor tools and sidecars, chart/menu/remote-map negotiation, and
separate BuildingGen. Existing wire has no explicit remote respawn-now/force-map
versus claimed-bed command; this checkpoint preserves the current authoritative
bed-first request and automatic timer. An empty finite pool still uses the
existing native fallback because there is no editor camera ray in the CLI entry.
