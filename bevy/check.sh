#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -n "${DOTNET:-}" ]]; then
  task_dotnet="$DOTNET"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
  task_dotnet="$HOME/.dotnet/dotnet"
else
  task_dotnet=dotnet
fi
"$task_dotnet" build bevy/bridge/UnturnedBevy.Bridge.csproj -c Debug --nologo
cargo test --manifest-path bevy/Cargo.toml -j "${CARGO_BUILD_JOBS:-3}" "$@"
