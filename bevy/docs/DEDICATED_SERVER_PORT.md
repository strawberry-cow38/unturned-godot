# Hybrid dedicated server

`unturned-bevy --dedicated` is the hybrid equivalent of the source `--dedicated`
(`game/Main.cs` BuildDedicated, `game/DedicatedServer.cs`, `WorldSaveDriver`,
`BanStore`). It runs the authoritative world headless on UDP 47872 for native
`--connect` clients at the current hybrid protocol (79; this slice adds no wire
ids). The live claw server still
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
  geometry upload, unattended/NPC vehicle bodies, door and deployable geometry.
  As in the source, a dedicated host builds no zombie horde (row 15A, see
  [MP_WIRE_GAPS_PORT.md](MP_WIRE_GAPS_PORT.md)). Remote drivers stay client-authoritative exactly as on a
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
2. Native host systems register map services, geometry, loot, vehicles, wildlife
   (the zombie field answers `disabled`) and NPC helicopters against the fresh world (as a listen host does).
3. Thirty frames after the map services adopt, `dedicatedServe` runs
   `WorldSaveDriver.LoadIntoWorld` semantics through the existing `load` path:
   missing save → fresh world; unreadable/refused save → logged, moved aside to
   `<save>.refused-UNIX` (the source left it in place for the next autosave to
   overwrite) and a fresh world, and if the file cannot be moved the server
   refuses to serve rather than autosave over it; a save from another world
   origin (`CheckNetworkOrigin`) is a configuration refusal that fails the boot and
   leaves the file untouched; a restore that fails part-way moves the file aside
   and fails the boot rather than serve a half-restored world; otherwise the
   season start and loot seed are restored first. Then the
   session starts ticking and the source lines are printed
   (`[SAVE] ...`, `[DEDICATED] world up ...; listening on udp P`).

Persistence is the shared host lifecycle (`bevy/bridge/WorldPersistence.cs`,
`player::persistence`), not a second copy: the source `WorldSaveDriver.PathFor`
file (`UG_BEVY_SAVES`/`UG_SAVE_DIR` as given, else its own
`<settings dir>/saves/dedicated/<map>.json`, so a local `--dedicated` run never
touches the single-player world of the same map and adopts no legacy name),
the 60 s autosave on the simulation clock, the `save`/`wipe` verbs, the loot seed
established on first save and the season/seed restore on load. The dedicated host
only changes *when* the restore happens (at admission, `dedicatedServe`, instead
of the listen host's `entry_restore`), owes it whenever the file exists at boot,
and adds the shutdown save. `wipe` deletes the file and leaves the live world
standing; as in the source, the next autosave writes it again unless the server
restarts first, and the shutdown save never undoes a wipe (stop after `wipe` → the
next boot is fresh).

Shutdown improves on the source (Godot ignored SIGTERM and was SIGKILLed after
5 s, losing up to a minute): SIGINT/SIGTERM/SIGHUP become Bevy's graceful AppExit
(the `ctrlc` `termination` feature, installed explicitly so an inherited ignored
SIGINT cannot block it). The `Last`-schedule stop writes the final save, releases
the socket and ends the bridge. The bridge ignores those signals while it hosts
(the native parent owns the stop), and if the parent's pipe closes without a stop
it still writes the save once. A failed start (occupied port, lost session, map
services not registered within 15 minutes) exits non-zero so systemd restarts it,
and an exit after such an unrecovered failure never saves (the last good save
stays);
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
World items stream on the source 128 m interest ring. `UG_MAXPLAYERS` above 32 is
clamped with a warning (the source session cap).
Logs: the source `[DEDICATED]` join/leave/drop lines and 10 s heartbeat from the
bridge, plus a native heartbeat with the achieved authority rate, the live entity
and sound counts and refused ticks
(`[DEDICATED] native: N authority ticks/s (target 50) ...`).

## Faults

Like Godot, which logged a frame's exception and kept running, every step of
`TickDedicated` (simulation, weapons, NPC helicopters, mounted guns, cosmetics,
world services, appearance, autosave, replication) is guarded on its own: a
throw is logged once per step per 10 s with a running count (`tickFaults` in the
network state) and the server keeps serving. A tick the bridge still refuses is
logged by the native host and is not fatal either; only a broken pipe to the
bridge ends the process.

## Headless presentation

The graphics, bug-reporter (microphone) and focus-outline plugins are added only
to a windowed app. The audio plugin stays (its asset types are shared), but a
headless host has no output, so Bevy never retires a one-shot sound: the
dedicated host ends every despawn/remove one-shot the frame it starts (remote
gunfire, footsteps, impacts), and the heartbeat reports the entity count. Its
log filter drops bevy_render's one-time `ClearColor` extraction error (no render
world exists).

## Deploy

Versioned only; nothing is installed. The server runs from its own worktree,
never the shared Godot checkout:
`git -C ~/projects/unturned-godot worktree add ~/projects/unturned-bevy-server port/bevy`.
`deploy/systemd/unturned-bevy-server.service` runs
`bevy/target/debug/unturned-bevy --dedicated` there with the Release bridge
(`UG_BRIDGE_DLL`), `UG_SAVE_DIR` and `TimeoutStopSec=30` for the final save.
`tools/deploy-bevy-server.sh` refuses to run unless that checkout is on the
deployed branch (`UG_DEPLOY_BRANCH`, default `port/bevy`); a shared checkout on
`main` would otherwise be fast-forwarded to `port/bevy`. It then fast-forwards,
builds the native host and the Release bridge, and writes
`bevy/.local/dedicated-server.stamp`, which `unturned-bevy-server-reload.path`
watches, so a half-finished build never restarts the server.

Both servers use UDP 47872, so the unit declares
`Conflicts=unturned-server.service unturned-server-reload.path`. The Godot rebuild
watcher must go too: left running, a Godot rebuild restarts `unturned-server`,
which stops this unit, and the two fight over the port. Cut-over on claw is a
manual decision: `systemctl --user disable --now unturned-server.service
unturned-server-reload.path` (or `mask` them), install these units, then enable
`unturned-bevy-server.service` and `unturned-bevy-server-reload.path`.

## Performance

The bridge request path wrote JSON straight into the unbuffered child stdin, one
syscall per token; it now serializes once and writes once (the world geometry
upload went from 12.8 s to 6.5 s, a vehicle hull upload from ~60 ms to ~19 ms).
The Release bridge halves both again. Headless frames are slow (1–12 fps) because
the main thread does per-frame JSON snapshot work and re-uploads every moving
vehicle hull, so the virtual clock may catch up a full second per frame
(`dedicated::virtual_clock`) rather than letting Bevy's 250 ms clamp slow the
world. Measured on the journey below (PEI, 700 m region, one lavapipe client
on the same loaded box): the authority held 49.9–50.1 ticks/s in every 10 s
heartbeat (56 windows), 5–6 ms per bridge tick, 12–15 ms per fixed step,
6–16 headless frames/s; boot to serving 12–15 s, restore of a 12.5 MB save
included.

## Verification

Managed (`tests/UnturnedBevy.Tests/DedicatedServerTests.cs`, 17): no local
player and no admission before the restore; status counts only remote peers and
the sync check passes; ban persisted to the TSV and refused after restart; unban
survives restart; console save + restart restores world items and the bag;
60 s autosave; graceful stop saves and frees the port; closed pipe saves once;
wipe keeps bans; wipe → stop → fresh boot; no-cheats locks give and moderation;
cheats on by default; unreadable save moved aside and never overwritten; 128 m
item ring; UG_MAXPLAYERS clamp; host door probes accepted; invalid
configurations fail before binding; a throwing service is logged and the server
keeps serving; a stop after a failure keeps the last good save; a save from
another origin is refused without being moved; an unmovable refused save is
never served over; the deploy script refuses any other branch (22 in all).
Native: `dedicated::tests` (6, including one-shot sounds ending headless and
the test ban list/saves staying in the temp directory) and
`player::dedicated_tests` (3: a real dedicated app admits a `--connect` client
only after services register, re-poses vehicles on the restore, saves on
AppExit into `saves/dedicated/` and never the single-player file, and releases
the port; a bound port fails the start; a failed host exits without saving).
The opt-in `dedicated::signal_tests` spawns the built binary, sends SIGTERM to
its process group and finds the final save on disk with exit 0.
`vehicle::glazing::tests::authority::joined_rider_gets_out_through_a_door_the_host_probes`
and `vehicle::tests::admission_restore_readopts_the_restored_replica_pose_like_load`
each carry their own control arm (without the fix the rider stays seated / no
pose is adopted).

Negative controls (mutation reverted, named test failed, file restored): admission
gate, BansChanged save, BanStore.Load, autosave tick, restore on serve, final
save, EnableSyncCheck, cheats default, wipe flag, item ring, refused-save move,
door-probe allow-list, max-players clamp, per-step tick guard, no save after a
failure, origin check before moving, unmovable save refusal, deploy branch guard
(managed); stop → dedicatedStop, failed start exits, services gate,
WorldRestored on serve, residency all-players, one-shot reaper, no save after a
failure, dedicated save directory (native); the bridge dying on SIGTERM fails
the signal test.

Two-process journey (frozen debug native + Release bridge, Xvfb + lavapipe
client, `--dedicated` on UDP 48731, 700 m region): the client joined, walked,
picked up a Spotlight (gone from the ground, in bag page 2), entered an
Off_Roader at the driver door, drove 62 m at up to 15 m/s with health intact,
braked and got out through its door. SIGTERM wrote the shutdown save; the
restarted server loaded it and a new client process came back at the same spot
beside the parked Off_Roader with the Spotlight in its bag. The journey found
three bugs, fixed here with tests: no host door probe for remote riders (stuck
in a seat), the probe op refused by the dedicated allow-list, and parked host
bodies not adopting the restored pose (the car woke at its spawn point).

After merging `port/bevy` b8f90b40 (original menus, outline, packaging), a fresh
boot was repeated: join (through Play → Direct Connect in the new menus), walk
4.8 m, SIGTERM, restart, rejoin, and SIGTERM/restart again with no input: the
client came back at exactly the saved position (−813.05, 34.97, −76.58). Over
130 heartbeats (≈22 min) the authority held 49.9–50.1 ticks/s with 0 refused
ticks, the entity count stayed between 8,927 and 9,008 and live sounds at 2; no
`ClearColor` error was logged. A package-layout boot (`UG_PACKAGE_ROOT` naming a
`bridge/` + `content/` folder, run from `/tmp`) resolved both through
`install::Layout`, served, and wrote its shutdown save on SIGTERM with exit 0.

## Remaining

- A software-rendered client's first join stalls more than 5 s while it
  builds the world, so the session times out; rejoining from the menu works.
  A client keepalive during world build is the fix (any slow client, any host).
- Crash damage for remote-driven vehicles is not judged on a dedicated host:
  the frame-sampled replica velocity would read as crashes there.
- Headless frames are slow (per-frame JSON snapshot, hull re-uploads); the
  authority rate holds, the frame rate does not.
- Headless still opens the audio device (ALSA noise in the log); no sound is
  ever output or kept.
- A `wipe` followed by an autosave (60 s) before the restart writes the world
  again, as in the source (`WorldSaveDriver`); only the shutdown save is
  suppressed.
- `BanStore.Save` rewrites the TSV in place (inherited verbatim from the source),
  so a crash mid-write can truncate the ban list.
- Source parity gaps kept on purpose: no admin tier, no stdin console,
  moderation behind `AllowCheats`, no auth/encryption (MP_PLAN §7).
- Cutting claw over is a manual operator step; the units and
  `tools/deploy-bevy-server.sh` are versioned but not run there.
