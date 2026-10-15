# Multiplayer migration

## Integrated UDP host boundary

The managed bridge can now initialize its existing authoritative world over the
original UDP transport. `init` accepts `listenPort` (1..65535), `serverName`,
`motd`, and `maxPlayers` (1..32, including the local player). Omitting the port,
or using zero, retains the existing memory transport. Both paths use the original
`NetContent.Hash` and current core protocol. A UDP host defaults to cheats off;
the caller can explicitly enable the existing authority gate for a test world.

The local player also uses an actual UDP client in listen mode. Remote peers join
the same `NetWorldServer`, through its normal admission, command validation and
replication. The original browser query responder reports the map, protocol,
content hash, capacity and live peer count. Session capacity agrees with the
advertised count. Failed startup and session replacement close the owned sockets.

Snapshots now include `network` session metadata and `players` from client
replicas, including identity, position, yaw, alive state, clothing, held item,
stance and looping gesture. Clothing includes original unpacked condition; held
attachments and worn/held light flags are also exposed. The pose uses the original
transform-wire stance, whose numeric values differ from the combat appearance
enum. UDP late joins use the initial map spawn instead of following the local
player's current position.

UDP currently uses the original bounded shared coordinate frame. A nonzero
initial/saved origin and runtime rebase are rejected; the singleplayer rebase
barrier cannot negotiate an origin with remote peers. Valid respawns near the
wire boundary remain in that frame. Initial spawns outside the wire's range are
rejected, using the core constants rather than a duplicated range.

## Managed client-only session

`init.connectHost` plus optional `connectPort` creates the original UDP client
without a local server or host services. Joining is asynchronous. Native code must
keep polling before activating owner movement; the first actual owner replica
provides its spawn and recovery epoch before any position publication. Normal
movement still has one native physics body and the existing server envelope.
Inventory, combat, skills, world entities and appearances read client replicas.
Commands retain connection identity and original validators; no local console or
damage shortcuts run in this mode.

Host-only registration, save/load, damage and rebasing are refused. Unknown
host enrichments remain absent and `network.wireGaps` enumerates them: exact held
instance identity, weapon timers, attachment transactions, repair, explicit
respawn, weather/absolute day, cosmetic flights, detailed impact/warhead/stuck-pose
facts, projectile expiry, vehicle part health, host wildlife/zombie data, map
bindings, password authentication and accepted shot tick. These are concrete
protocol follow-ups. The existing automatic respawn and recovery events work.
The original handshake has no password field, so a nonempty password is refused.

`disconnect` tears down an owned integrated/listen world, closes its UDP port and
leaves no authority to tick. Remote disconnect instead retains its terminal
network envelope so the UI can report source rejection text and retry policy.
Session replacement discards old replicas and services. `shutdown` remains valid
in either mode.

Nine additional actual-UDP integration tests (18 total) cover first spawn,
accepted equip/fire, independent owner inventory, cheats and host-operation
rejection, recovery/death/automatic respawn, chat/gesture/rejoin, full-server
refusal, local/client mode replacement, host listener release and unsupported
passwords. The frozen executable also passes all121 existing bridge tests. A
separate two-process JSONL smoke verifies join, authoritative spawn, accepted
inventory, port reuse after host disconnect and clean remote process shutdown.
These checks establish the managed boundary, not complete remote gameplay.

## Asynchronous status and initial-join retry

The managed status adapter extracts the original `StatusQueryFull`, bounded
string decoder/sanitizer and ping-refusal helper. Start/poll/cancel operations
work before any world exists. One worker runs the original1200-byte query;
replacement tokens discard stale answers and queue behind a running request.
The UI can keep drawing and owns the original four probe attempts, cancellation
generation and 30-second connection timeout. The source rejected-join policy
allows four retries (separate from the four status probes). `reconnect` preserves
the original initial-rejection `Client.Connect` path; a session that previously
joined must use fresh `init` to prevent old replica state leaking across joins.

Nine further real UDP tests bring this managed suite to27: nonblocking/superseded
status requests, malformed endpoints, exact request size, nonce rejection, truncated optional data,
content mismatch, sanitized text, cancellation, silent timeout, source policy
metadata, real host status before world creation, successful retry after capacity
clears, and remote animal clip/species metadata. Remote animals use the original
puppet clip mapping and static dimensions only; unknown health and corpse expiry
remain absent. Native remote puppets must preserve the source's lack of a body.
All121 existing bridge tests also pass against this unchanged executable.

## Read-only remote map bindings

The managed `mapBindings` query reuses the full source manifest and the server's
accepted holiday. Authored door/seat counters keep original order. Container and
fixture IDs come only from unique actual replicas of the matching kind and
wire-quantized pose; missing/ambiguous matches stay unset and later arrivals can
rebind. The query creates no server, fixture, loot, inventory or health state.
Its shape matches native `mapServices` metadata, with explicit readiness and
unbound counts. Native consumption and accepted-holiday world rebuilding remain
a separate integration step.

This exposed a host omission: the bridge had parsed local seasonal content but
left the original Accept holiday argument empty. The host now captures one source
holiday for Accept, parsing and NPC checks and persists it in saves. Three real
UDP regressions compare all696 container/11fixture bindings and authored counters
against a host, test Christmas host versus nonholiday client resource ordering,
late registration, duplicate same-pose refusal, and save/load across a changed
local holiday. All 30 managed UDP/status tests and all 121 bridge regressions pass.

## Native remote bodies

Accepted peer replicas now create the original human rig, face, clothing and gear
on the world camera layer. The local owner is excluded. Position uses the source
14/s exponential glide and strict greater-than-5m teleport rule; measured speed
uses the source 8/s smoothing before selecting original locomotion clips. Crouch,
prone and sitting use transform-wire stance. Capsules share the local controller's
stance heights and contact the local player without blocking vehicles. Furniture
sitting or replicated vehicle occupancy disables the remote capsule.

Appearance changes retain the existing rig. Leave, disconnect and world-session
replacement remove the entire old body tree. Accepted held weapons now mount on
the source body hook with original sight/magazine/barrel geometry. Changing an
attachment on the same gun replaces the old tree. Remote sights preserve the
source body mount and zero-ID factory-irons fallback, which differ from the
first-person alignment rules. A replica arriving before the catalog retries once
the item definition becomes available.

Gun and melee holds use the original equip clip's final upper-body pose. Accepted
melee facts play the weapon's weak/strong clip and return to that hold; bare fists
use the original punch clips and unknown items do not invent punches. Looping
gestures and one-shot gesture facts use the shared source playback rules, preserve
locomotion legs and low-stance torso posture, and deduplicate retained events.
Native local and remote footsteps now share source stride, material/gait fallback,
landing threshold, puddle fields and exact shelter probes; local swim banks and
voice pause/session cleanup also run (see [FOOTSTEPS_PORT.md](FOOTSTEPS_PORT.md)).
Actual listening remains unverified without an audio output device. Nameplates,
reclined seats and muzzle effects remain presentation work; this checkpoint does not establish complete multiplayer.

Accepted remote worn/held light flags now use the same original brightest-colour
emission masks as local equipment. Headlamp/NVG lenses and held torch bulbs glow;
the original remote puppets do not emit extra spotlights. Switching flags keeps
the same rig, equipment and per-player materials. A pending albedo suppresses
emission until its mask can be built, preventing a temporary whole-mesh glow.
The actual source-texture regression verifies 812 torch bulb/near-twin pixels,
dark housing, original energies, delayed texture load and OFF without remount.
Opened production render fixtures `bevy-remote-lamps-off.png` and
`bevy-remote-lamps-on.png` show all three worn fixtures and four torch bulbs in a
fixed dim scene. These are accepted-replica fixtures, not live UDP toggle proof.
The combined native suite passed 356 tests (two opt-in visual fixtures ignored).

## Verification

`tests/UnturnedBevy.Tests` exercises the actual bridge host and original client
over OS UDP sockets. Nine tests cover join/movement/chat, leave/reconnect with
stale replica removal, cheat rejection, content mismatch, capacity, browser
status, occupied-port cleanup/recovery, local-session replacement, origin guards,
late-join spawn stability, explicit/automatic edge-of-range respawn and wire-stance
decoding for peer presentation. The
respawn test uses `NetQuantization.PositionXZRange - 128`, within the source's
singleplayer rebase margin. Code review identified that lifecycle conflict before
the checkpoint.

The complete 121-test bridge protocol/authority suite also passes against the
frozen UDP-capable executable, preserving the default memory-transport journeys.

Nine native remote-body tests load the shipped rig and check accepted outfit/wear,
local-owner exclusion, glide/speed/stance/teleport behavior, seat and world cleanup,
actual Rapier contact filtering, weapon/attachment changes, catalog-arrival order,
body attachment rules, gesture completion/deduplication and melee selection. All
328 native tests pass on the integrated remote-equipment candidate, with the
interactive renderer fixture excluded from the automatic run.

The opt-in `render_remote_pose_fixture` uses the production renderer, rig,
clothing and peer plugins with fixed snapshots. Both `bevy-remote-poses.png` and
`bevy-remote-held.png` were rendered under Vulkan, opened and independently reviewed:
standing/crouching/prone/sitting bodies, clothing, standing/prone scoped rifles
and a crouched knife are coherent. This fixture is deliberately separate from
live two-client gameplay verification. To repeat the equipped side view:

```sh
env -u WAYLAND_DISPLAY WGPU_BACKEND=vulkan \
  VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
  UG_REMOTE_POSE_HELD=1 UG_REMOTE_POSE_SIDE=1 \
  UG_REMOTE_POSE_SHOT=/tmp/bevy-remote-held.png \
  xvfb-run -a cargo test --manifest-path bevy/Cargo.toml \
  avatar::remotes::tests::visual::render_remote_pose_fixture -- --ignored --exact --nocapture
```

Run with:

```sh
dotnet test tests/UnturnedBevy.Tests/UnturnedBevy.Tests.csproj
```

## Native lifecycle and controls

The native main menu now offers the authored official server row, direct connect,
and an integrated UDP host. The direct form collects host, port and player name;
its asynchronous status probe must accept the server's content/protocol, ping
policy and supported map before loading the world or creating a session. The
original four connection attempts, four retryable initial rejections and 30-second
link timeout are preserved. Favorites/history/LAN and password submission are
shown as unavailable instead of presenting invented records or authentication.
Refresh uses the original five-second cooldown and cancelled probes cannot start
a stale session. The host form uses the selected retail map, server name, port
and 1–32 player capacity.

CLI equivalents are `--listen 47872 --server-name "My world" --max-players 8`
and `--connect 127.0.0.1 --port 47872 --name Survivor`. CLI clients take the same
status-before-map path. Network sessions explicitly reset to the shared zero
origin and disable local rebasing. Procedural hosting is refused while that
origin protocol is absent. The main menu itself creates no authority or socket.

The native owner exists as a reusable presentation/body entity, but neither
movement simulation nor position publication starts before the accepted owner
spawn. Pause/settings/inventory keep network ticks running; menus suppress owner
input. Leaving disposes the real listener or client, clears accepted state and
presentation, and permits a fresh local or network world. Save is host-only;
loading a world and explicit remote respawn are unavailable in network menus.
Local memory-transport play retains its pause and load behavior.

Host-only geometry, services, wildlife, zombies, vehicle spawns, physical damage,
water cosmetic registration and dynamic vehicle uploads are gated by accepted
ownership. A remote vehicle uses original 12/s exponential glide, 8m teleport snap
and 0.5-second extrapolation cap; its wheels stop at the cap. Only the accepted
local driver activates the single native dynamic body. Remote wildlife uses the
source 10/s transform interpolation and selected original animation, with no
host collision capsule. The stale AnimalPuppets comment saying animation is
frozen contradicts its actual engine-driven AnimationPlayer. Current source
ClientWorldSession explicitly removed the old remote-zombie renderer; native
clients do not invent host HOT bodies or wardrobe to fill that source gap.

Opened live Vulkan captures include `bevy-network-no-response` (four failed
attempts), `bevy-network-remote-joined` (separate UDP host, accepted owner 2),
`bevy-network-remote-paused` / `-paused-later` (ticks 148→171 while paused),
`bevy-network-remote-left-confirmed` (cleared state and returned main menu),
and `bevy-network-native-host` (real listen world with 696 containers and 8 vehicles).
`bevy-network-host-paused-peer` confirms a separate UDP peer joined that native
host; `bevy-network-host-left-confirmed` records the cleared session, followed
by a successful OS bind to the released listener port.
These PNG+JSON sidecars verify actual native controls and wire ownership, beyond
the isolated remote-character fixtures. The earlier `bevy-network-remote-left`
capture only showed a hovered button and does not prove disconnection.

The integrated 349-test native suite passes, including real-UDP status/map ordering,
owner-spawn adoption, listen port release, pause progression, remote→local session
replacement, actual Rapier driver ownership, puppet extrapolation and live rig
animation without a remote animal collider. X11 repeated key edges no longer
retoggle pause while text fields retain character repetition. The rendered host
leave also caught dirty door leaves publishing after the authority was disposed;
closed-session guards now retire the upload cache, with an actual bridge teardown
regression. The corrected guard changes no rendered geometry.

## Remaining integration

Read-only remote map bindings are a managed follow-up awaiting native adoption;
without them remote fixture/container interactions remain unavailable, while
native static world geometry renders. Remaining remote-player presentation,
moderation UI, dedicated world boot and the explicit wire gaps above require
further port work. A client must never construct another local authority to fill
them. Infinite-world multiplayer needs a separate origin protocol and is not
established by these bounded UDP tests. Remote vehicle tow ropes, source light/
alarm dress and visual/audio parity remain separate presentation gaps.
