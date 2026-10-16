# Packaging: a relocatable native release (ledger row "Packaging")

Before this, the native client found its .NET bridge and `game/content` through
`env!("CARGO_MANIFEST_DIR")`, the compile-time path of the checkout, so a built
binary only worked on the machine and in the folder that compiled it.

## Runtime path resolution (`bevy/src/install.rs`)

`install::Layout::current()` is resolved once per process:

| Order | Layout | Bridge | Content | Writable data |
|---|---|---|---|---|
| 1 | per-path env overrides (beat both layouts) | `UG_BRIDGE_DLL` | `UG_CONTENT_DIR` (`--content` beats it) | `UG_DATA_DIR` |
| 2 | `UG_PACKAGE_ROOT=DIR` | `DIR/bridge/UnturnedBevy.Bridge.dll` | `DIR/content` | settings dir `/cache` |
| 3 | package: `unturned-bevy-package.json` beside the executable | `<exe dir>/bridge/…` | `<exe dir>/content` | settings dir `/cache` |
| 4 | checkout (`cargo run`, tests) | `bevy/bridge/bin/Debug/net8.0/…` | `game/content` | `bevy/.local` |

Notes:

- The checkout row is byte-for-byte the old behaviour. `bevy/run.sh`,
  `bevy/check.sh` and the tests are unchanged. Test code still uses
  `CARGO_MANIFEST_DIR` directly, and `CoreBridge::launch(manifest)` keeps its
  signature as the checkout layout.
- A package never falls back to the checkout. If its bridge is missing, the
  error names the package path; it does not quietly start the compiling
  machine's `bin/Debug`.
- `std::env::current_exe` resolves symlinks, so a link to the binary placed
  elsewhere still finds the package.
- The settings dir is `Settings::path()`'s folder, `$XDG_CONFIG_HOME` or
  `~/.config/unturned-bevy`, or `%APPDATA%`. A package can then be installed
  read-only. The only writes are the procedural terrain JSON (`main.rs`, the
  in-game map switch in `player.rs`).
- Settings and the local profile were already per-user and are untouched.
- Retail maps still come from `UG_UNTURNED_DIR`, `--unturned`, or the Steam
  default probe (`cli::detect_retail_install`). The package does not include
  retail data.
- The bug reporter passes `Layout::project_dir()`: the checkout root in dev,
  the package root otherwise. BuildStamp therefore reports `unknown` for a
  package; see Remaining.

Bridge start (`bridge::host_command`): a self-contained publish lists
`includedFrameworks` in its `runtimeconfig.json` and starts through its
apphost. Anything else, including the dev `bin/Debug` output (which also has
an apphost), runs on `dotnet` as before. The `RuntimeCopy` private copy is kept
for both.

The only `CARGO_MANIFEST_DIR` left in non-test code is the checkout fallback in
`install.rs`.

## Package script (`bevy/tools/package.sh`)

```
UG_HEAVY=/tmp/claude-1000/ug-bin/heavy.sh bevy/tools/package.sh \
    [--out DIR] [--framework-dependent] [--no-content] [--skip-build] [--archive] [--no-smoke]
```

The script runs these steps in order:

1. `dotnet publish -c Release -r <host rid> --self-contained`.
2. `cargo build --release`.
3. Stages `bevy/dist/unturned-bevy-<rid>/` and swaps it in only when complete.
4. Smoke-runs the result with `--headless --map Procedural --ticks 25`, with
   `UG_BRIDGE_DLL`, `UG_CONTENT_DIR`, `UG_PACKAGE_ROOT` and `DOTNET` cleared.

The package layout:

```
unturned-bevy                  release binary
unturned-bevy-package.json     marker + {format, commit, dirty, built, rid, bridge, content}
bridge/                        published UnturnedBevy.Bridge (+ .NET runtime when self-contained)
content/                       git-tracked game/content (ls-files, so editor/island bakes are excluded)
licenses/                      bevy/licenses/* + CREDITS.md
README.txt                     how to run, overrides, requirements
```

Measured for linux-x64 at 5707bbba:

| Part | Size |
|---|---|
| Whole folder | 787 MB (420 MB as `.tar.gz`) |
| `content/` | 554 MB on disk, 13,340 files |
| `bridge/` | 77 MB, 201 files: the self-contained .NET 8 runtime |
| `unturned-bevy` | 157 MB |

The binary is unstripped. Stripping takes it to 115 MB, but panic backtraces
would lose their function names, so it is left for a later decision.

Decisions:

- **Self-contained bridge (default).** A player then needs no .NET install,
  and the runtime version is pinned to the one we tested. The launcher decision
  in `SETTINGS_MENU_PORT.md` is that players must not build anything; a .NET
  prerequisite would bring back half of that. The cost is about 77 MB and one
  package per RID. `--framework-dependent` produces the small variant for
  machines that already have .NET 8; the binary handles both.
- **Content is bundled.** It is 545 MB of mostly PNG/OGG/OBJ in 13,340
  tracked files. That is the bulk of the package but it changes with the code
  (GUID manifest, meshes, `.dat`s), so shipping it alongside keeps a package
  self-consistent. A shared content download across branches is an
  optimisation for the launcher, not something to build now. `--no-content`
  plus `UG_CONTENT_DIR` already supports it.
- **Host RID only.** Cargo builds for the host, so the script refuses other
  hosts rather than pretending to cross-compile. A Windows package needs the
  same script run on Windows (or `cargo-xwin`), with the `.exe` apphost name;
  `install.rs` already uses `EXE_EXTENSION`.

## Verification

See `PROGRESS.md` (2026-10-16, Packaging) for the measured sizes, the test
counts, the negative controls and the opened relocation capture. In summary:
the package was copied outside the repository and run inside a mount namespace
with the checkout hidden under an empty tmpfs, a fresh `HOME`, no `dotnet` on
`PATH` and no `UG_*` path overrides. A `--play --shot` rendered PEI under
Xvfb/lavapipe.

## Launcher (`launcher/`): next step, not done here

The launcher is a Windows Avalonia app built around git clone, `dotnet build`
and Godot. None of that applies to a package, and the Windows package and
release hosting it would fetch do not exist yet, so it is unchanged. The
precise next step, once a Windows `package.sh` run exists:

1. A CI or manual job runs `package.sh --archive` per branch and uploads
   `unturned-bevy-win-x64.zip` plus `unturned-bevy-package.json` as GitHub
   release assets tagged `bevy-<branch>`. The launcher already downloads its
   own update from `releases/download/launcher/…`.
2. In `MainWindow.cs`, a "native" mode replaces `git fetch`/`rev-parse` with a
   GET of the remote `unturned-bevy-package.json`. It compares `commit` with
   the installed `<base>/native/<branch>/unturned-bevy-package.json`. If they
   differ, it downloads the zip, extracts to a staging folder and swaps it in,
   the same staging pattern `package.sh` uses.
3. Play starts `<base>/native/<branch>/unturned-bevy.exe` with the
   environment it already builds (`UG_UNTURNED_DIR`, `UG_USERNAME`,
   `UG_PROFILE_PNG`, `UG_OFFLINE`, `UG_BUGREPORT_KEY`, `UG_AUTH_*`). No
   path variables are needed, because the package finds its own bridge and
   content.

## Remaining

- Windows (and macOS) packages: run the script on those hosts. Untested.
- Bug-report build stamp for packages. Have the client pass the marker's
  `commit` to the bridge as `BuildStamp.ConfiguredVersion`. Today a packaged
  report says `game_commit: unknown`.
- Each bridge launch copies `bridge/` (about 77 MB self-contained) to a private
  temp folder. This protects against in-place rebuilds in dev and is harmless
  but redundant for an immutable package; it could be skipped for
  `Layout::Package`. The whole masked headless smoke, copy included, took
  1.1 s, so it is cheap here.
- The launcher work above, plus release hosting and signing.
