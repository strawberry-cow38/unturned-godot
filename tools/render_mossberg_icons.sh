#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
USER_HOME=$(getent passwd "$(id -u)" | cut -d: -f6)
GODOT=${GODOT:-$USER_HOME/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64}
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
cp "$ROOT/tools/mossberg_icons/"* "$TMP/"
ln -s "$ROOT/game/content" "$TMP/content"
export VK_ICD_FILENAMES=${VK_ICD_FILENAMES:-/usr/share/vulkan/icd.d/lvp_icd.aarch64.json}
timeout --kill-after=5s 60s xvfb-run -a "$GODOT" --path "$TMP" --rendering-method mobile --write-movie "$TMP/icons.avi" --fixed-fps 30 --audio-driver Dummy
