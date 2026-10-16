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

- **The overrides apply to every run, not only packages.** `UG_CONTENT_DIR`,
  `UG_DATA_DIR` and `UG_PACKAGE_ROOT` left set in a shell also redirect
  `cargo run`, `bevy/run.sh` and the tests, as `UG_BRIDGE_DLL` always did.
  Unset them for normal dev work.
- Without overrides, the checkout row is byte-for-byte the old behaviour. `bevy/run.sh`,
  `bevy/check.sh` and the tests are unchanged. Test code still uses
  `CARGO_MANIFEST_DIR` directly, and `CoreBridge::launch(manifest)` keeps its
  signature as the checkout layout.
- A package never falls back to the checkout. If its bridge is missing, the
  error names the package path; it does not quietly start the compiling
  machine's `bin/Debug`. If `UG_BRIDGE_DLL` is set and wrong, the error
  names that variable instead of suggesting a reinstall.
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
- Bug reports. A checkout run passes `projectDir`, and BuildStamp reads the
  enclosing `.git` as before. A package sends `buildCommit`, the marker's
  `commit` in BuildStamp's form (12 hex digits, plus `+dirty` when the tree was
  dirty), and `BuildStamp.UsePackagedCommit` stops the upward `.git` walk. A
  package under `bevy/dist`, or inside any other repository, would otherwise
  report that checkout's HEAD.

Bridge start (`bridge::host_command`): a self-contained publish lists
`includedFrameworks` in its `runtimeconfig.json` and starts through its
apphost. Anything else, including the dev `bin/Debug` output (which also has
an apphost), runs on `dotnet` as before.

`RuntimeCopy` (a private temp copy of the bridge directory) exists because the
checkout's `bin/Debug` is rebuilt in place while games run. A package's own
bridge is immutable, so `Layout::needs_private_copy` runs it in place. That
avoids putting the whole .NET runtime into the temp directory on every launch,
failing on a `noexec` `/tmp`, and leaking the copy after a SIGKILL. A
`UG_BRIDGE_DLL` pointing outside the package is still copied.

The only `CARGO_MANIFEST_DIR` left in non-test code is the checkout fallback in
`install.rs`.

## Package script (`bevy/tools/package.sh`)

```
UG_HEAVY=/tmp/claude-1000/ug-bin/heavy.sh bevy/tools/package.sh \
    [--out DIR] [--framework-dependent] [--no-content] [--skip-build] [--archive] [--no-smoke]
```

The script runs these steps in order:

1. Refuses an empty `--out`, `/`, `$HOME` or the repository itself. It also
   refuses an existing `--out` that lacks the package marker, so it never
   deletes a folder it did not make.
2. `dotnet publish -c Release -r <host rid> --self-contained`.
3. `cargo build --release`.
4. Stages into `<out>.staging`. An `EXIT` trap removes the stage on any
   failure.
5. Smoke-runs the STAGE with `--headless --map Procedural --ticks 25`, with
   `UG_BRIDGE_DLL`, `UG_CONTENT_DIR`, `UG_PACKAGE_ROOT`, `UG_DATA_DIR` and
   `DOTNET` cleared. With `--no-content`, the smoke uses `UG_CONTENT_DIR` if
   it is set, and is skipped otherwise.
6. Swaps the stage in only after the smoke passes.

The marker records the build actually packaged: `commit` is HEAD before the
build, with `dirty`. `--skip-build` reuses artifacts from an unknown commit,
so it writes `commit: "unknown"`, `dirty: true`, and keeps HEAD only as
`sourceHead`. Heavy steps use `${heavy[@]+"${heavy[@]}"}` so an unset
`UG_HEAVY` also works on bash before 4.4.

The package layout:

```
unturned-bevy                  release binary
unturned-bevy-package.json     marker + {format, commit, dirty, sourceHead, built, rid, bridge, content}
bridge/                        published UnturnedBevy.Bridge (+ .NET runtime when self-contained)
content/                       git-tracked game/content (ls-files, so editor/island bakes are excluded)
licenses/                      bevy/licenses/*, CREDITS.md, content CREDITS,
                               THIRD_PARTY_RUST.txt, DOTNET_RUNTIME_{LICENSE,THIRD-PARTY-NOTICES}.TXT
README.txt                     how to run, overrides, requirements, the retail-content
                               private-use note
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

Licenses. `cargo about` is not installed here, so
`bevy/tools/third_party_licenses.py` walks the `cargo metadata` resolve graph
from the root over normal dependencies for the target triple. It writes each
crate's name, version, SPDX expression and verbatim LICENSE/COPYING/NOTICE
texts, deduplicated: 434 crates and 219 distinct texts, about 750 KB. 24 crates
ship no license file, and only their SPDX expression is listed. The .NET
runtime's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` come from the runtime
pack matching the published `runtimeconfig.json` version; a self-contained
package fails if they are missing. `content/` is ripped retail data for private
use, and the README says so.

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
- The 24 Rust crates without a license file could get canonical SPDX texts
  appended.
- The launcher work above, plus release hosting and signing.
