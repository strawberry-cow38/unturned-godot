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
stance and looping gesture. These fields do not create native remote avatars by
themselves. UDP late joins use the initial map spawn instead of following the
local player's current position.

UDP currently uses the original bounded shared coordinate frame. A nonzero
initial/saved origin and runtime rebase are rejected; the singleplayer rebase
barrier cannot negotiate an origin with remote peers. Valid respawns near the
wire boundary remain in that frame. Initial spawns outside the wire's range are
rejected, using the core constants rather than a duplicated range.

## Verification

`tests/UnturnedBevy.Tests` exercises the actual bridge host and original client
over OS UDP sockets. Eight tests cover join/movement/chat, leave/reconnect with
stale replica removal, cheat rejection, content mismatch, capacity, browser
status, occupied-port cleanup/recovery, local-session replacement, origin guards,
late-join spawn stability, and explicit/automatic edge-of-range respawn. The
respawn test uses `NetQuantization.PositionXZRange - 128`, within the source's
singleplayer rebase margin. Code review identified that lifecycle conflict before
the checkpoint.

The complete 121-test bridge protocol/authority suite also passes against the
frozen UDP-capable executable, preserving the default memory-transport journeys.

Run with:

```sh
dotnet test tests/UnturnedBevy.Tests/UnturnedBevy.Tests.csproj
```

## Remaining integration

This checkpoint establishes the managed listen transport. Native host/join
controls, a client-only bridge mode, world/replica adoption, remote animated
players, networking while menus are open, connection failures/reconnection,
browser UI, moderation UI, dedicated world boot, and cross-process gameplay
journeys remain to be ported and tested. Several current snapshot fields and
weapon/vehicle adapters still read host services; a remote client must replace
those reads with the source client prediction and replicated/event data, without
constructing another local authority. Infinite-world multiplayer needs a separate
origin protocol and is not established by these bounded UDP tests.
