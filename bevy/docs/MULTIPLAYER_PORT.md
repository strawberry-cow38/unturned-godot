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
Footsteps, nameplates, reclined seats, remote light fixtures and muzzle effects
remain presentation work; this checkpoint does not establish complete multiplayer.

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

## Remaining integration

This checkpoint establishes the managed listen transport. Native host/join
controls, a client-only bridge mode, world/replica adoption, remaining remote-player
presentation, networking while menus are open, connection failures/reconnection,
browser UI, moderation UI, dedicated world boot, and cross-process gameplay
journeys remain to be ported and tested. Several current snapshot fields and
weapon/vehicle adapters still read host services; a remote client must replace
those reads with the source client prediction and replicated/event data, without
constructing another local authority. Infinite-world multiplayer needs a separate
origin protocol and is not established by these bounded UDP tests.
