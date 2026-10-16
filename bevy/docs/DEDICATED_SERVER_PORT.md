# Hybrid dedicated server

`unturned-bevy --dedicated` is the hybrid equivalent of the source `--dedicated`
(`game/Main.cs` BuildDedicated, `game/DedicatedServer.cs`, `WorldSaveDriver`,
`BanStore`). It runs the authoritative world headless on UDP 47872 for native
`--connect` clients at the current protocol (76). The live claw server still
runs the Godot build at protocol 61; nothing here touches it.

## Where authority lives

The source dedicated server is one Godot process that both owns the netcode and
simulates the physical world. The hybrid splits that the same way a listen host
already does:

- The managed bridge owns the `NetWorldServer` over UDP with **no local client**
  (`bevy/bridge/DedicatedHost.cs`). There is no player slot, no body and no owner
  snapshot: the first joiner is player 1, the browser counts only remote peers, and
  `State()` reports `playerId: 0` (the existing "no local player" value).
- The native app runs headless: `DefaultPlugins` without a window, winit, gilrs or a
  GPU device (`RenderCreation` with no backends), driven by `ScheduleRunnerPlugin` at
  the source's 60 FPS cap, with the same game/host plugins as a listen host. Rapier
  and every host-authority system keep working: world collision and the authoritative
  geometry upload, unattended/NPC vehicle bodies, zombie HOT bodies, door and
  deployable geometry. Remote drivers stay client-authoritative exactly as on a
  listen host.
- Local-player systems (`spawn_player`, input, `simulate`, `publish_body`, camera,
  lean) are gated off by `dedicated::has_local_player`. `dedicated::tick` takes the
  owner publication's place in the 50 Hz schedule (after Rapier writeback and the
  vehicle/zombie publications) and sends an input-free `tick`.
- The snapshot the native host reads is built from the **server registries**
  (`DedicatedState`), sharing the exact world-row projections of the owner
  snapshot (`ReplicaRows.cs`: vehicles, mounts, deployables, world items). Owner
  sections and presentation-only lists are absent.

The world is not streamed around a camera. `dedicated::configure_world` loads one
region covering every authored placement and every Players.dat/Vehicles.dat point
plus 160 m (`UG_DEDICATED_RADIUS` overrides the radius). On PEI that is about a
1.58 km radius: 4063 props, 8325 resource meshes, 1.76 M authority triangles.

## Boot, persistence and shutdown

Order follows the source: build the world, register every host service, then
restore, then serve.

1. `init {dedicated:true, listenPort, savePath, bansPath, mapRoot}` creates the
   server but `TickDedicated` does nothing, so no datagram is read and nobody is
   admitted.
2. Native host systems register map services, geometry, loot, vehicles, wildlife,
   zombies and NPC helicopters against the fresh world (as a listen host does).
3. Thirty frames after the map services adopt, `dedicatedServe` runs
   `WorldSaveDriver.LoadIntoWorld` semantics through the existing `load` path:
   missing save → fresh world; unreadable/refused save → logged, file kept, fresh
   world; otherwise the season start and loot seed are restored first. Then the
   session starts ticking and the source lines are printed
   (`[SAVE] ...`, `[DEDICATED] world up ...; listening on udp P`).

Persistence is the shared host lifecycle (`bevy/bridge/WorldPersistence.cs`,
`player::persistence`), not a second copy: the source `WorldSaveDriver.PathFor`
file (`UG_BEVY_SAVES`/`UG_SAVE_DIR`, default `<settings dir>/saves/<map>.json`),
the 60 s autosave on the simulation clock, the `save`/`wipe` verbs, the loot seed
established on first save and the season/seed restore on load. The dedicated host
only changes *when* the restore happens (at admission, `dedicatedServe`, instead
of the listen host's `entry_restore`), owes it whenever the file exists at boot,
and adds the shutdown save. `wipe` deletes the file and leaves the live world
standing; as in the source, the next autosave writes it again unless the server
restarts first.

Shutdown improves on the source (Godot ignored SIGTERM and was SIGKILLed after
5 s, losing up to a minute): SIGINT/SIGTERM/SIGHUP become Bevy's graceful AppExit
(the `ctrlc` `termination` feature, installed explicitly so an inherited ignored
SIGINT cannot block it). The `Last`-schedule stop writes the final save, releases
the socket and ends the bridge. The bridge ignores those signals while it hosts
(the native parent owns the stop), and if the parent's pipe closes without a stop
it still writes the save once. A failed start (occupied port, lost session, map
services not registered within 15 minutes) exits non-zero so systemd restarts it;
a signal stop exits 0.

## Moderation and operator surface

Exactly the source surface. `game/BanStore.cs` is linked verbatim: the TSV is loaded
at boot, rewritten on every change and kept apart from the world save (`wipe` never
unbans). Path: `UG_BANS_FILE`, else `UG_BEVY_SAVES`/`UG_SAVE_DIR`/`bans.tsv`, else
`<settings dir>/bans.tsv`. Banned peers are refused at the handshake (core
`BanCheck`, `NetRejectReason.Banned`). The verbs are the existing server console
commands sent by a connected client: `kick`, `ban`, `unban`, `bans`, `say`,
`save`, `wipe`, plus the cheats. Like the source, all of them sit behind
`AllowCheats`, which is ON for a dedicated server unless
`UG_DEDICATED_NOCHEATS=1` (so a locked server cannot kick, a known source gap).
There is no admin tier and no stdin console in the source either.

Operator environment, read once at boot: `UG_NAME`, `UG_MOTD`, `UG_MAXPLAYERS`
(1–32, capacity and advertised count agree as on the hybrid listen host),
`UG_PVE` (advertised only, as in the source), `UG_MAXPING`, `UG_ICON`,
`UG_DEATHMSG`, `UG_DEATH_KILLER/WEAPON/DISTANCE/LOCATION`, `UG_PORT`, `UG_NETLOG=1`
or `--netlog` (NetLog on stderr, including the 1 Hz `[NET] 1s:` rollup). CLI
`--port`, `--server-name` and `--max-players` win over the environment. Flood
caps (8 half-open, 8 per source IP) are the core defaults. `EnableSyncCheck` is on.
Logs: the source `[DEDICATED]` join/leave/drop lines and 10 s heartbeat from the
bridge, plus a native heartbeat with the achieved authority rate
(`[DEDICATED] native: N authority ticks/s (target 50) ...`).

## Deploy

Versioned only; nothing is installed. `deploy/systemd/unturned-bevy-server.service`
runs `bevy/target/debug/unturned-bevy --dedicated` with the Release bridge
(`UG_BRIDGE_DLL`), `UG_SAVE_DIR`, `TimeoutStopSec=30` for the final save and
`Conflicts=unturned-server.service` (same port). `tools/deploy-bevy-server.sh`
fast-forwards (`UG_DEPLOY_BRANCH`, default `port/bevy`), builds the native host and
the Release bridge, then writes `bevy/.local/dedicated-server.stamp`, which
`unturned-bevy-server-reload.path` watches, so a half-finished build never
restarts the server. Cut-over on claw is a manual decision: disable the Godot
units, install these, enable `unturned-bevy-server.service` and the `.path`.

## Performance

The bridge request path wrote JSON straight into the unbuffered child stdin, one
syscall per token; it now serializes once and writes once (the world geometry
upload went from 12.8 s to 6.5 s, a vehicle hull upload from ~60 ms to ~19 ms).
The Release bridge halves both again. Headless frames are slow (1–12 fps) because
the main thread does per-frame JSON snapshot work and re-uploads every moving
vehicle hull, so the virtual clock may catch up a full second per frame
(`dedicated::virtual_clock`) rather than letting Bevy's 250 ms clamp slow the
world. RESULTS_PLACEHOLDER

## Verification

VERIFICATION_PLACEHOLDER

## Remaining

REMAINING_PLACEHOLDER
