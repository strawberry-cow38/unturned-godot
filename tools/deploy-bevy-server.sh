#!/usr/bin/env bash
# Deploy a new version of the hybrid dedicated server (Bevy host + .NET bridge):
# fast-forward the checkout, build the native host and the Release bridge, then
# write the stamp that deploy/systemd/unturned-bevy-server-reload.path watches.
# The watcher owns the restart (SIGTERM -> final save -> boot -> load).
#
# Branch: UG_DEPLOY_BRANCH (default port/bevy, where the hybrid lives today).
set -euo pipefail
cd "$(dirname "$0")/.."
branch="${UG_DEPLOY_BRANCH:-port/bevy}"
export PATH="$HOME/.dotnet:$HOME/.cargo/bin:$PATH"

# Only ever fast-forward the branch being deployed: the claw checkout is shared,
# and merging origin/port/bevy into whatever is checked out (main) would move it.
current="$(git symbolic-ref --quiet --short HEAD || true)"
if [ "$current" != "$branch" ]; then
  echo "[deploy] refusing: this checkout is on '${current:-detached HEAD}', not '$branch'." >&2
  echo "[deploy] deploy from a dedicated worktree: git worktree add ../unturned-bevy-server $branch" >&2
  exit 1
fi

echo "[deploy] fetching origin/$branch..."
git fetch --quiet origin "$branch"
git merge --ff-only "origin/$branch"   # a dirty/diverged tree fails loudly instead of guessing

echo "[deploy] building the native host..."
cargo build --manifest-path bevy/Cargo.toml --bin unturned-bevy

echo "[deploy] building the Release bridge..."
dotnet build bevy/bridge/UnturnedBevy.Bridge.csproj -c Release -v q -nologo

mkdir -p bevy/.local
date -u +%Y-%m-%dT%H:%M:%SZ > bevy/.local/dedicated-server.stamp
echo "[deploy] done @ $(git rev-parse --short HEAD) -- the .path watcher will restart unturned-bevy-server."
