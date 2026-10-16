# Menus, settings, bug report and launcher (ledger row 18)

Sources: `game/MainMenu.cs`, `MainMenuPlay.cs`, `MainMenuAdvanced.cs`,
`EditorMaps.cs`, `OfflineMode.cs`, `SettingsScreen.cs`, `GraphicsPanel.cs`,
`GraphicsOptions.cs`, `ControlsOptions.cs`, `KeybindMenu.cs`, `Keybinds.cs`,
`BugReporter.cs`, `core/BugReportCore`, `content/chromatic.gdshader`, `launcher/`.

## Canvas and theme

The Godot project stretches a 2560x1440 canvas (`canvas_items`, `expand`), so a
1280x720 window shows every source size at 0.5. The ported menus author sizes in
that base and multiply by `ui::godot::stretch` (`min(w/2560, h/1440)`); the
GraphicsOptions UI-scale row multiplies on top through Bevy `UiScale`. Older
native screens (HUD, inventory) keep their existing conventions. Controls use
the Godot 4.6 default theme colours or `UITheme` where the source does, Open
Sans SemiBold, and a DejaVu Sans subset for the symbol glyphs Godot's
TextServer falls back to (`assets/fonts/README.md`).

## Main menu

`ui::main_menu` ports the dashboard (Play/Survivors/Configuration/Workshop at
y=170/230/290/350, Exit 70 above the bottom, 200x50, 40 px icons), the centred
SubPanel submenus (gold 24 px header, 300x50 rows with 32 px icons, Back with
the Exit icon) and the stub panel. Retail rows without a backend open the stub,
as the source does. Differences:

- The barn diorama and its camera anchors are not recreated; the native menu
  keeps its world view (content work).
- Play adds a native **Host** row (the integrated UDP listen host has no source
  row). `UG_OFFLINE=1` disables Multiplayer, Direct Connect and Host but leaves
  them visible, as `OfflineMode` does (no tooltip text).
- Configuration > Graphics opens the combined `GraphicsPanel.Build` layout.
  Configuration > Controls (a source stub described as "Keybinds + mouse
  sensitivity") opens the shared tabbed SettingsScreen at Controls, so key binds
  are reachable before play. Display/Audio/Options stay stubs.
- Workshop rows are stubs: the map editor is not native yet.
- `UG_MENUOPEN`/`UG_MENUTAB` keep the source render hooks; `graphics`,
  `settings`, `controls`, `keybinds` and `UG_MENUTAB=generate` are native
  additions for captures.

## Singleplayer selector and world entries

`ui::singleplayer` ports MenuPlaySingleplayerUI's three columns: preview,
PLAY, Difficulty, Zombies, two-press Reset Data, the Config fold (Loot, Day
Cycle, Combat, Cheats, Permadeath, Advanced), the seed row (seed, die, Lakes),
Back; the Official/Proc tabs (Curated/Workshop/Misc disabled) and the scrolled
list; the name/description column. Every entry drives the CLI-equivalent path:

| Row | Native entry |
| --- | --- |
| PEI / Washington / Yukon | `UiIntent::Play { map }` (`--map NAME`) |
| Russia, Germany, Hawaii, Greece, A6 Polaris | source refusal text, no session |
| Generate Island (seed, Lakes) | `finiteMap create` into `maps/Island SEED` (`--new-island DIR --seed N [--lakes]`), then Play |
| Proc tab row | `finiteMap` reopen of that directory (`--island DIR`) |
| Infinite World (seed) | Play `Procedural` with that seed (`--map Procedural --seed N`) |
| Playground | refused: the gun range world is not native yet |

Generation runs after two rendered frames of the Connecting/loading text, so the
blocking bridge call never hides the click. `bootstrap::open` is shared with the
CLI. Saved islands live under `<settings dir>/maps` (`UG_BEVY_MAPS`). The Proc
list is `EditorMaps.ListProc`: directories with a `_proc.txt` seed marker,
newest first; delete is two presses and refuses unmarked (hand-built) maps.

World rules go to the authority: Zombies travels as the bridge `init` field
`zombies` (`WorldBuilder.ZombiesOverride`); without it the host honours
`UG_NOZOMBIES`/`UG_DEDICATED_NOZOMBIES`. The host then never builds the retail
or generated zombie field; loot, animals and vehicles are unaffected. `--no-zombies`
is the CLI equivalent. Difficulty, Loot, Day Cycle, Combat, Cheats, Permadeath
and the whole Advanced table are placeholders in the source too; they persist
to `map_settings.json` (`map_settings.cfg`) and change nothing. Reset Data deletes
the save of the last retail row selected, which is the source's
`SelectedMapFolder` behaviour even while a generated entry is highlighted.

## Settings

`Settings.graphics` holds every GraphicsOptions value with the source defaults
(FXAA, High shadows at 120 m, 4x aniso, Medium planar water, High scope/effects,
bloom/outline/wind/ragdolls/grass on, V-Sync off) and cycle orders. Rows save
on every change (`graphics.cfg`/`Keybinds.Set`), atomically; unit tests write a
per-process temp file. `crate::graphics` applies them:

| Row | Native effect |
| --- | --- |
| Anti-aliasing | one `Msaa` for every window camera, including cameras spawned later (image targets keep theirs); FXAA on the world camera after the source tonemap pass; TAA (removed with its jitter, mip bias and motion prepass); unsupported sample counts fall back and say so |
| Shadow quality / distance | sun shadows on/off; Godot's 1024-8192 atlas is split in four PSSM quadrants, so each Bevy cascade gets half the edge (512-4096, same texels and memory); Hardware2x2/Gaussian filter; cascade max distance |
| Resolution / Fullscreen / V-Sync / Target frame rate | window size, `WindowMode`, present mode, sleep-based frame cap (off under `--fixed-fps`) |
| UI scale | `UiScale` |
| Effect quality | particle count and size at emitter creation |
| Ambient occlusion | Bevy SSAO on the world camera when no MSAA is active ("On (needs no MSAA)") |
| Bloom | the existing source glow pass |
| Chromatic aberration / strength | `chromatic.gdshader` ported as a layer-7 pass after night vision, before the HUD |
| Planar water reflection | mirror off or every 4/2/1 frames |
| Scope quality | 360/540/720/1080 optic target at next mount |
| Grass displacement / Wind | foliage disturbance slots / wind uniform |
| Ragdolls | off: the body stops where it is |
| Render thread (restart) | `PipelinedRenderingPlugin` at next launch |

Not drawn natively yet, and stated in the panel: anisotropic filtering (no
material requests it, as in the source), render scale (needs an offscreen world
target), render distance (no LodTable per-prop culling natively), sun shafts and
fog quality (volumetric fog), screen-space reflections (Bevy SSR needs deferred
rendering) and outline (no look-at outline overlay). The source's coupling of
the Wind row to wind-turbine output is deliberately not ported: a client
preference never changes world authority.

Controls keep the source sliders/toggles (look sensitivity, invert Y, heli and
plane pitch, heli sensitivity); the earlier native FOV and master-volume rows
remain under a "NATIVE" label. Sliders use `bevy_ui_widgets::Slider` and snap to the
source steps. The readout tracks the drag, but the value applies and saves on
release (the source applies on every change): the overlay rebuilds on each
settings change, which would end the drag. Scroll offsets are kept across those
rebuilds, as Godot's once-built screens do. Key Binds lists every action with
`Keybinds.DisplayName`/`ContextLabel` and Godot key labels, captures the next
key or mouse button, refuses conflicts, Delete/Backspace unbinds, Esc cancels and
Reset restores defaults, with the source hint texts and colours.

## Bug reporter

Holding the BugReport binding (default `\`, physical; ignored with
Ctrl/Alt/Super or while a text field has focus) captures a screenshot at press,
records the default input device through cpal while held and a second screenshot
at release (60 s cap, 0.4 s tap = screenshot only, focus loss ends it). The
"● REC m:ss" label and toasts use the source canvas layer 260 position; ●
and the "report sent ✓" check come from the DejaVu MenuSymbols fallback, as
Godot's TextServer does, because Open Sans lacks both. The
bridge's `BugReportOutbox` reuses BugReportCore (`AudioBuffer` amplitude verdict,
`Wav.FitRate`/`Encode`, `BuildStamp`, `AudioStatusWire`) and ports WriteOutbox,
UploadDir (Bearer header, 2xx delete, permanent 4xx parked as `rejected_CODE_`,
401/408/429/5xx kept), and the ten-report launch retry. Config is
`UG_BUGREPORT_KEY` then `<settings dir>/bugreport.cfg [report] endpoint/key`;
`UG_BUGREPORT_UPLOAD=0` keeps reports on disk. Console `report <text>` files a
typed report. The context carries engine/os/gpu/uptime/map/player/fps and the
5 s worst frame; Godot-only performance monitors are omitted.

## Launcher

`launcher/` is a Windows Avalonia app that clones a branch, runs
`dotnet build game/UnturnedGodot.sln`, `godot --headless --import` and starts
Godot with `UG_UNTURNED_DIR`, `UG_USERNAME`, `UG_PROFILE_PNG`, `UG_AUTH_*`,
`UG_OFFLINE` and `UG_BUGREPORT_KEY`. It is deliberately unchanged. A native path
should not build Rust on the player's machine: once packaging (ledger: bridge
and content paths are compile-time `CARGO_MANIFEST_DIR`) produces a relocatable
release, the launcher should download that release per branch and start it with
the same environment. The native build already honours `UG_UNTURNED_DIR`,
`UG_USERNAME`, `UG_PROFILE_PNG`, `UG_FACE`, `UG_OFFLINE` and `UG_BUGREPORT_KEY`;
`UG_AUTH_TOKEN`/`UG_AUTH_KEY` (signed identity) are not consumed yet.
The relocatable release now exists (`bevy/tools/package.sh`); the launcher's
download-and-run step is specified in [PACKAGING_PORT.md](PACKAGING_PORT.md).

## Verification

See PROGRESS.md (2026-10-16) for test counts, negative controls and the opened
original/native captures.
