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

case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64 | Linux-arm64) rid=linux-arm64 ;;
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

dotnet_bin="${DOTNET:-dotnet}"
if ! command -v "$dotnet_bin" >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_bin="$HOME/.dotnet/dotnet"
fi
heavy=()
[[ -n "${UG_HEAVY:-}" ]] && heavy=("$UG_HEAVY")
target_dir="${CARGO_TARGET_DIR:-$bevy_root/target}"
publish_dir="$bevy_root/bridge/bin/package/$rid-$([[ $self_contained == true ]] && echo sc || echo fd)"

if ((build)); then
    echo "[package] publishing bridge ($rid, self-contained=$self_contained)"
    rm -rf "$publish_dir"
    "${heavy[@]}" "$dotnet_bin" publish "$bevy_root/bridge/UnturnedBevy.Bridge.csproj" \
        -c Release -r "$rid" --self-contained "$self_contained" -o "$publish_dir" --nologo -v q
    echo "[package] building native release binary"
    "${heavy[@]}" cargo build --release --manifest-path "$bevy_root/Cargo.toml"
fi
binary="$target_dir/release/unturned-bevy"
[[ -f "$publish_dir/UnturnedBevy.Bridge.dll" ]] || { echo "package.sh: no published bridge at $publish_dir" >&2; exit 1; }
[[ -x "$binary" ]] || { echo "package.sh: no release binary at $binary" >&2; exit 1; }

# Stage beside the destination, then swap, so a failed run never leaves a
# half-written package where a launcher expects a whole one.
mkdir -p "$(dirname -- "$out")"
stage="$out.staging"
rm -rf "$stage"
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
mkdir -p "$stage/licenses"
cp "$bevy_root"/licenses/* "$stage/licenses/"
[[ -f "$repo_root/CREDITS.md" ]] && cp "$repo_root/CREDITS.md" "$stage/licenses/CREDITS.md"

commit="$(git -C "$repo_root" rev-parse HEAD)"
dirty=false
[[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=no)" ]] && dirty=true
cat >"$stage/unturned-bevy-package.json" <<EOF
{
  "format": 1,
  "commit": "$commit",
  "dirty": $dirty,
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
EOF

rm -rf "$out"
mv "$stage" "$out"

if ((smoke)); then
    echo "[package] smoke: core bridge from the package"
    # The checkout's bridge must not be what answers: clear the dev overrides.
    env -u UG_BRIDGE_DLL -u UG_CONTENT_DIR -u UG_PACKAGE_ROOT -u DOTNET \
        "$out/unturned-bevy" --headless --map Procedural --ticks 25
fi

echo "[package] $out ($(du -sh "$out" | cut -f1))"
if ((archive)); then
    tarball="$out.tar.gz"
    tar -C "$(dirname -- "$out")" -czf "$tarball" "$(basename -- "$out")"
    echo "[package] $tarball ($(du -sh "$tarball" | cut -f1))"
fi
