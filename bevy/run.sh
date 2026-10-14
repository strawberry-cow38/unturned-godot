#!/usr/bin/env bash
set -euo pipefail
bevy_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_bin="${DOTNET:-dotnet}"
if ! command -v "$dotnet_bin" >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_bin="$HOME/.dotnet/dotnet"
fi
"$dotnet_bin" build "$bevy_root/bridge/UnturnedBevy.Bridge.csproj" -c Debug --nologo
exec cargo run --manifest-path "$bevy_root/Cargo.toml" -- "$@"
