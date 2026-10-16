# World save lifecycle (WorldSaveDriver)

Source: `game/WorldSaveDriver.cs`, driven by `game/MpLoopback.cs` (singleplayer,
`user://saves`) and `game/DedicatedServer.cs` (`UG_SAVE_DIR`); format and state
walk in core `WorldSave` (unchanged). The native port keeps the split: the host
bridge owns the file, the timer and the console verbs; the native client names the
file and chooses the restore moment.

## What the source does (and therefore the port does)

| Source behaviour | Port |
| --- | --- |
| One file per map: `PathFor(mapId)` = `UG_SAVE_DIR` (else the profile dir) + `Sanitize(mapId).json`; letters/digits/`_`/`-` lowercased, anything else `_`, empty = `world`. | `player/persistence.rs::save_path` (native dir: `UG_BEVY_SAVES`, then `UG_SAVE_DIR`, then the settings dir). Native files are now `pei.json`; an earlier native pause-menu file under the old ASCII-only, case-kept name (`PEI.json`) is adopted once by rename, never over an existing source-named file. |
| Autosave every `AutosaveSeconds = 60` of the **simulation** clock (`net.save.autosave` sim step, just before replication; frozen by singleplayer pause). | `BridgeHost.TickAutosave`, `SimClock.FixedDelta` accumulator in the 50 Hz `Tick`, immediately before `TickReplication`. Fires on exactly the 3000th tick from zero. |
| Load after the map is built and before anyone plays; the loaded block restores each player as they join (`PendingSave`). | Native `entry_restore` waits until the host has registered *this* map's services/geometry/loot (`Replicas::geometry_registered`), then issues the same accepted `Load` the pause menu uses. The loading screen is held meanwhile. |
| `LoadIntoWorld` restores the season start (`WorldTemperature.StartDayOfYear`) and the loot seed before the clock. | `AdoptSavedWorldStatics` before the restart. Each `init` (a new world) starts from the process defaults. |
| First save establishes the loot seed (`LootSeed.EnsureEstablished`), idempotent afterwards. | Same, in `WriteWorldSave` (every save path: autosave, console, pause menu). |
| Offline players ride along: `Capture(carryOver)` keeps every previous block whose name is not online; the written save becomes the next carry-over. | `WriteWorldSave` captures with `_server.PendingSave` as carry-over and then adopts the written save (existing bridge choice; see deviation 2). |
| Temp file + swap; a write failure logs and never throws into a tick. | Same (`path.tmp` + atomic move; `[save] write failed` on stderr, `persistence.lastResult`). |
| Console `save` → `saved to <path>`; `wipe` deletes the file, clears the carry-over, leaves the running world. | `SaveNowHandler`/`WipeSaveHandler` installed on the host server when a path is configured; otherwise the core verb answers `no save is configured on this server`. |
| **Leaving or quitting writes nothing.** `PauseMenu.ExitToMenu`, `MpLoopback._ExitTree` and `DedicatedServer._ExitTree` only tear down. | Native Disconnect/Quit unchanged: no write. The newest autosave (or explicit save) is what reopens. |
| A refused save starts a fresh world and leaves the file; the next autosave overwrites it. | A refused native `load` reports the error and plays the fresh world; autosave resumes. |
| Remote clients have no persistence. | `savePath` ignored for `connectHost`; save/load ops refused as before. |
| Test/render harness entries (`--vehicle=DIR` etc.) never attach MpLoopback, so never touch a save; the real game entries (`--peiplay`, menu Play) do, `--shot` or not. | The native `--vehicle` render harness configures no save. `--play` is the game entry and saves/restores like `--peiplay`; isolate render runs with `UG_BEVY_SAVES`. |

The native pause-menu **Save world / Load world** buttons predate this slice and are
port additions (the source pause menu is Resume / Settings / Exit to Menu). They now
use the same file and code path as the autosave.

## Deliberate deviations

1. **Entry hold.** The source loads before its first simulation step. The native host
   must upload map geometry first, so the placeholder world runs for a few seconds;
   `init.restorePending` suppresses the autosave until the restore's `load` runs (or is
   refused), so the placeholder can never overwrite the save. The console `save` verb and
   the bridge `save` op refuse the configured file during that window (native-only text:
   `save refused -- <path> has not been restored into this world yet`). The loading screen
   stays up for the whole hold, including a menu → Play of the same map. If the host's
   world-authority registration itself fails, the native hold is released so the error is
   visible, while the bridge keeps holding autosave for that broken session.
2. **Reconnect source.** The source keeps `PendingSave` at the boot-time load, so a
   player reconnecting mid-session restores boot-time state. The bridge already adopted
   each written save as `PendingSave`; kept (carry-over chain is identical).
3. **Listen-host restore window.** The entry restore restarts the bridge session, closing
   and re-binding the UDP port; a remote peer that joined in the few seconds before the
   restore is dropped and must rejoin. Late UDP joins keep the initial map spawn across
   that restart.
4. Main-menu **Reset Data** (two-click delete of the selected map's save) is not ported;
   the console `wipe` verb is.
5. **Process statics per world.** The source never resets `WorldTemperature.StartDayOfYear`
   or `LootSeed`; after Exit to Menu a second world in the same process inherits the
   previous world's season and seed. Each bridge `init` resets both to the process
   defaults, so a fresh world rolls its own seed at its first save.
6. Body temperature (`PlayerTemperatureSim`) is not part of `WorldSave` in the source
   either; a reopened world starts the body mid-comfort again.

## Evidence

See PROGRESS.md (2026-10-16 save lifecycle entry) for test counts, surgical negative
controls and the opened full-game captures.
