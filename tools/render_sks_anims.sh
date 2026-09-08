#!/usr/bin/env bash
# Real Vulkan viewmodel rendering. UG_VM_SNAPSHOT_TIME=1.55 freezes the real
# clip for a quick contact check; omit it for the full 25 fps sequence.
set -euo pipefail
sks_action="${1:-reload}"
sks_output="${2:-/tmp/snow/guns/sks_animation_renders/final_${sks_action}}"
case "$sks_action" in
  reload) sks_default_caps=20,28,36,44,55,60,68,76,86,93,99,104,110,116,122,125,129,133,138,144,151,160,170 ;;
  handling) sks_default_caps=20,28,36,44,55,60,66,70,75,80,83,86,89,94,100,108,113,120,130,137,140,144,149,155 ;;
  ads) sks_default_caps=55,66,78 ;;
  *) sks_default_caps=55,60,66,72,78,84,90,100,110 ;;
esac
sks_caps="${3:-$sks_default_caps}"
sks_last="${sks_caps##*,}"
sks_godot="${GODOT_BIN:-/home/ec2-user/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64}"
sks_repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$sks_output"
cd "$sks_repo"
VK_ICD_FILENAMES="${VK_ICD_FILENAMES:-/usr/share/vulkan/icd.d/lvp_icd.aarch64.json}" \
UG_SHOT_TIMEOUT=0 UG_NOADS=1 UG_VMSMALL=1 UG_VM_NATIVE_SIZE=1 \
UG_VM_ACTION="$sks_action" UG_VM_ACTION_AT=60 UG_VMCAPS="$sks_caps" \
UG_UNTURNED_DIR="${UG_UNTURNED_DIR:-/home/ec2-user/unturned}" \
timeout 900 xvfb-run -a "$sks_godot" \
  --path game --rendering-driver vulkan --write-movie "$sks_output/v.avi" \
  --fixed-fps 25 --quit-after "$((sks_last + 2))" -- --vm="$sks_output" --gun=sks \
  > "$sks_output/render.log" 2>&1
python3 tools/sks_animation_contact_sheet.py "$sks_output" --frames "$sks_caps" --action "$sks_action"
