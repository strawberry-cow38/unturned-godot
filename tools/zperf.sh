#!/usr/bin/env bash
# Zombie GPU render probe. Spawns N zombies under a pulled-back 3p-style camera and prints the engine's
# render counters with them visible, hidden, and visible-without-shadows.
#
# MUST run with a real rendering driver. Under --headless the engine renders nothing, so every counter
# reads zero and any timing is just the frame pacer -- that mistake produced two rounds of garbage
# numbers (+250ms one run, negative the next) before this script existed.
#
# draws / prims / vram are hardware-independent and show whether anything is drawn more times than it
# should be. lavapipe's ABSOLUTE ms is meaningless -- but it is a software rasteriser, so its cost is
# fragment-dominated, which makes the RATIO between phases the only fill-rate reading available without
# the real GPU. Counters cannot see overdraw or shadow-map fill at all. Read ratios, not numbers.
#
# Runs both camera placements, because the 3p chase cam (34m back) is what the fps tank is gated on, and
# both a small and a large resolution: fragment cost scales with pixels, geometry cost does not, so the
# ratio between the two resolutions says WHICH kind of cost the zombies are. That scaling law transfers
# to real hardware; lavapipe's absolute numbers do not.
#
#   ./tools/zperf.sh          # 20 zombies
#   ./tools/zperf.sh 100      # 100
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
N="${1:-20}"
GODOT="${GODOT:-/home/ec2-user/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64}"
VK_ICD="${VK_ICD:-/usr/share/vulkan/icd.d/lvp_icd.aarch64.json}"

[ -x "$GODOT" ] || { echo "godot not executable: $GODOT (set \$GODOT)" >&2; exit 1; }

for cam in 1p 3p; do
  for res in 640x360 1920x1080; do
    echo "=== camera $cam  res $res  (n=$N) ==="
    # xvfb's default screen is 1280x1024, which would silently clamp the large resolution -- give it room.
    UG_ZN="$N" UG_ZCAM="$cam" UG_ZRES="$res" VK_ICD_FILENAMES="$VK_ICD" timeout 240 \
      xvfb-run -a -s "-screen 0 2048x1280x24" "$GODOT" --path "$ROOT/game" --rendering-driver vulkan -- --zperf 2>&1 \
      | grep -E '^\[zperf\]'
  done
done
