#!/usr/bin/env bash
# Deploy a new version of the dedicated server: pull origin/main + rebuild the game assembly.
# The rebuild rewrites game/.godot/mono/temp/bin/Debug/UnturnedGodot.dll, which trips the
# unturned-server-reload.path unit -> restarts unturned-server.service on the fresh build.
#
# Run this whenever main advances (manually after a merge, or from a timer). It never touches
# the server unit directly -- the .path watcher owns the restart.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "[deploy] fetching origin/main..."
git fetch --quiet origin main
git merge --ff-only origin/main   # fast-forward only: a dirty/diverged tree fails loudly instead of guessing

echo "[deploy] building game assembly..."
dotnet build game/UnturnedGodot.csproj -c Debug -v q -nologo

echo "[deploy] done @ $(git rev-parse --short HEAD) -- the .path watcher will bounce unturned-server."
