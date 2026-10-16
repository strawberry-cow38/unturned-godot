#!/usr/bin/env bash
# Build a relocatable Unturned Bevy release folder: the release native binary,
# a published game bridge and the bundled content, laid out so the binary finds
# them beside itself (bevy/src/install.rs). See bevy/docs/PACKAGING_PORT.md.
#
#   bevy/tools/package.sh [--out DIR] [--framework-dependent] [--no-content]
#                         [--skip-build] [--archive] [--no-smoke]
#
# Heavy steps (dotnet publish, cargo build --release) run through $UG_HEAVY when
# set, e.g. UG_HEAVY=/tmp/claude-1000/ug-bin/heavy.sh on the shared dev box.
set -euo pipefail

bevy_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
repo_root="$(dirname -- "$bevy_root")"
marker_name=unturned-bevy-package.json

case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 triple=x86_64-unknown-linux-gnu ;;
    Linux-aarch64 | Linux-arm64) rid=linux-arm64 triple=aarch64-unknown-linux-gnu ;;
    *) echo "package.sh: unsupported host $(uname -s)-$(uname -m); see PACKAGING_PORT.md" >&2; exit 2 ;;
esac
out="$bevy_root/dist/unturned-bevy-$rid"
self_contained=true
content=bundled
build=1
archive=0
smoke=1

while (($#)); do
    case "$1" in
        --out) out="$2"; shift ;;
        --out=*) out="${1#--out=}" ;;
        --framework-dependent) self_contained=false ;;
        --no-content) content=external ;;
        --skip-build) build=0 ;;
        --archive) archive=1 ;;
        --no-smoke) smoke=0 ;;
        -h | --help) sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "package.sh: unknown option $1" >&2; exit 2 ;;
    esac
    shift
done

# Everything below may `rm -rf` $out and $out.staging: refuse anything that is
# not plainly a dedicated package folder.
[[ -n "$out" ]] || { echo "package.sh: empty --out" >&2; exit 2; }
out="$(realpath -m -- "$out")"
for forbidden in / "$HOME" "$repo_root" "$bevy_root" "$(dirname -- "$repo_root")"; do
    if [[ "$out" == "$(realpath -m -- "$forbidden")" ]]; then
        echo "package.sh: refusing to use $out as the package folder" >&2
        exit 2
    fi
done
if [[ -e "$out" && ! -f "$out/$marker_name" ]]; then
    echo "package.sh: $out exists and is not a package (no $marker_name); not replacing it" >&2
    exit 2
fi
stage="$out.staging"
if [[ -e "$stage" && ! -f "$stage/$marker_name" && -n "$(ls -A -- "$stage" 2>/dev/null)" ]]; then
    echo "package.sh: $stage exists and is not a package staging folder; remove it yourself" >&2
    exit 2
fi
staged=0
cleanup() { if ((staged)) && [[ -d "$stage" ]]; then rm -rf -- "$stage"; fi; }
trap cleanup EXIT

dotnet_bin="${DOTNET:-dotnet}"
if ! command -v "$dotnet_bin" >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_bin="$HOME/.dotnet/dotnet"
fi
heavy=()
[[ -n "${UG_HEAVY:-}" ]] && heavy=("$UG_HEAVY")
target_dir="${CARGO_TARGET_DIR:-$bevy_root/target}"
publish_dir="$bevy_root/bridge/bin/package/$rid-$([[ $self_contained == true ]] && echo sc || echo fd)"

# Record the build that is actually packaged. A --skip-build reuses whatever
# artifacts are lying around, which may come from any commit: say so.
head="$(git -C "$repo_root" rev-parse HEAD)"
if ((build)); then
    commit="$head"
    dirty=false
    [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=no)" ]] && dirty=true
    echo "[package] publishing bridge ($rid, self-contained=$self_contained)"
    rm -rf -- "$publish_dir"
    ${heavy[@]+"${heavy[@]}"} "$dotnet_bin" publish "$bevy_root/bridge/UnturnedBevy.Bridge.csproj" \
        -c Release -r "$rid" --self-contained "$self_contained" -o "$publish_dir" --nologo -v q
    echo "[package] building native release binary"
    ${heavy[@]+"${heavy[@]}"} cargo build --release --manifest-path "$bevy_root/Cargo.toml"
else
    commit=unknown
    dirty=true
fi
binary="$target_dir/release/unturned-bevy"
[[ -f "$publish_dir/UnturnedBevy.Bridge.dll" ]] || { echo "package.sh: no published bridge at $publish_dir" >&2; exit 1; }
[[ -x "$binary" ]] || { echo "package.sh: no release binary at $binary" >&2; exit 1; }

# Stage beside the destination, smoke-test the stage, then swap, so a failed
# run never leaves a half-written or broken package where a launcher expects one.
mkdir -p "$(dirname -- "$out")"
rm -rf -- "$stage"
staged=1
mkdir -p "$stage"
cp "$binary" "$stage/unturned-bevy"
cp -r "$publish_dir" "$stage/bridge"
if [[ $content == bundled ]]; then
    echo "[package] copying tracked game/content"
    if [[ -n "$(git -C "$repo_root" status --porcelain -- game/content)" ]]; then
        echo "[package] warning: game/content has uncommitted changes; the package copies the working tree" >&2
    fi
    git -C "$repo_root" ls-files -z -- game/content |
        tar -C "$repo_root" --null -T - --transform 's,^game/content,content,' -cf - |
        tar -C "$stage" -xf -
fi

echo "[package] collecting licenses"
mkdir -p "$stage/licenses"
cp "$bevy_root"/licenses/* "$stage/licenses/"
[[ -f "$repo_root/CREDITS.md" ]] && cp "$repo_root/CREDITS.md" "$stage/licenses/CREDITS.md"
[[ -f "$repo_root/game/content/CREDITS.md" ]] && cp "$repo_root/game/content/CREDITS.md" "$stage/licenses/CONTENT_CREDITS.md"
# `cargo about` is not assumed; this walks `cargo metadata` for the linked crates.
python3 "$bevy_root/tools/third_party_licenses.py" "$bevy_root/Cargo.toml" "$triple" \
    "$stage/licenses/THIRD_PARTY_RUST.txt"
if [[ $self_contained == true ]]; then
    runtime_version="$(python3 -c 'import json,sys; c=json.load(open(sys.argv[1]))["runtimeOptions"]; print(next(f["version"] for f in c["includedFrameworks"] if f["name"]=="Microsoft.NETCore.App"))' \
        "$stage/bridge/UnturnedBevy.Bridge.runtimeconfig.json")"
    runtime_pack="${NUGET_PACKAGES:-$HOME/.nuget/packages}/microsoft.netcore.app.runtime.$rid/$runtime_version"
    for notice in LICENSE.TXT THIRD-PARTY-NOTICES.TXT; do
        [[ -f "$runtime_pack/$notice" ]] || { echo "package.sh: missing .NET runtime notice $runtime_pack/$notice" >&2; exit 1; }
        cp "$runtime_pack/$notice" "$stage/licenses/DOTNET_RUNTIME_$notice"
    done
fi

cat >"$stage/$marker_name" <<EOF
{
  "format": 1,
  "commit": "$commit",
  "dirty": $dirty,
  "sourceHead": "$head",
  "built": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "rid": "$rid",
  "bridge": "$([[ $self_contained == true ]] && echo self-contained || echo framework-dependent)",
  "content": "$content"
}
EOF

cat >"$stage/README.txt" <<EOF
Unturned (Bevy port) -- relocatable build ${commit:0:12} for $rid

Run:
  ./unturned-bevy               main menu
  ./unturned-bevy --play        straight into PEI
  ./unturned-bevy --listen 47872 / --connect HOST --port 47872
  ./unturned-bevy --help        every option

The folder can live anywhere: the game finds bridge/ and content/ beside the
executable (unturned-bevy-package.json marks this folder as a package).

Retail maps (PEI, Washington, Yukon) are read from your Unturned install.
Set UG_UNTURNED_DIR to its root (the folder holding Maps/) or pass
--unturned PATH. Without it, --map Procedural still works.

content/ holds assets ripped from the retail game Unturned (Smartly Dressed
Games). They are included for private use with this private port only and are
not licensed for redistribution. Do not share this package publicly.

Requirements: 64-bit Linux, Vulkan driver, X11 or Wayland, ALSA.
EOF
if [[ $self_contained == true ]]; then
    echo "The .NET game core is self-contained in bridge/; no .NET install is needed." >>"$stage/README.txt"
else
    echo "The .NET game core needs the .NET 8 runtime (dotnet on PATH, or DOTNET=path)." >>"$stage/README.txt"
fi
if [[ $content == external ]]; then
    echo "This package has no content/: set UG_CONTENT_DIR to a game/content tree." >>"$stage/README.txt"
fi
cat >>"$stage/README.txt" <<'EOF'

Overrides (environment): UG_BRIDGE_DLL (a different UnturnedBevy.Bridge.dll),
UG_CONTENT_DIR (or --content), UG_DATA_DIR (generated cache; default
~/.config/unturned-bevy/cache), UG_PACKAGE_ROOT (treat another folder as the
package). Settings live in ~/.config/unturned-bevy/ ($XDG_CONFIG_HOME).
Also honoured: UG_USERNAME, UG_PROFILE_PNG, UG_OFFLINE, UG_BUGREPORT_KEY.
Third-party notices: licenses/.
EOF

if ((smoke)); then
    # The checkout's bridge must not be what answers: clear the dev overrides.
    keep_content=()
    if [[ $content == external ]]; then
        if [[ -n "${UG_CONTENT_DIR:-}" ]]; then
            keep_content=(UG_CONTENT_DIR="$UG_CONTENT_DIR")
        else
            echo "[package] smoke skipped: --no-content and no UG_CONTENT_DIR to test with" >&2
            smoke=0
        fi
    fi
    if ((smoke)); then
        echo "[package] smoke: core bridge from the staged package"
        env -u UG_BRIDGE_DLL -u UG_CONTENT_DIR -u UG_PACKAGE_ROOT -u UG_DATA_DIR -u DOTNET \
            ${keep_content[@]+"${keep_content[@]}"} \
            "$stage/unturned-bevy" --headless --map Procedural --ticks 25
    fi
fi

rm -rf -- "$out"
mv -- "$stage" "$out"
staged=0

echo "[package] $out ($(du -sh -- "$out" | cut -f1))"
if ((archive)); then
    tarball="$out.tar.gz"
    tar -C "$(dirname -- "$out")" -czf "$tarball" "$(basename -- "$out")"
    echo "[package] $tarball ($(du -sh -- "$tarball" | cut -f1))"
fi
