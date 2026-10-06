# Mossberg 500 moving pump and single-shell insert

October 6, 2026. Existing gun/item 112 (`bluntforce`) and factory-irons item 114.

## Meshes and materials
- `bluntforce_gun.txt`: original stationary body, 104 triangles.
- `bluntforce_pump.txt`: original fore-end, 20 triangles, a separate child in first person. The existing Hammer clip drives a local-Y stroke derived from the left palm relative to the right-hand gun mount: 0 → approximately -0.1861 m → 0 in 0.4667 seconds. Barrel, magazine tube, receiver and stock do not slide.
- `AttachmentFit.PartsFor` also returns the separate pump for world/paperdoll/third-person assembly, at its parked position. Animated pump and visible inserted shell are first-person channels; this does not claim remote pump-motion replication.
- Body plus pump preserve **every original face corner, normal, UV and winding**; no reshaping. The re-rip-safe originals are frozen under `tools/models/mossberg/`.
- The existing separate irons are a 104-triangle rear ring. It remains unchanged. A 12-triangle simple ramp front post was added near the muzzle, at the existing ADS height. Combined irons are 116 triangles; whole gun with pump/irons is 240.
- Gun/world pickup duplicates, item bounds and icons regenerated from these assets. A shell-fed internal tube must not spawn a pretend external magazine: the old missing `mag_shells_8.txt` fallback is suppressed for this gun only.

## One-shell animation and clock
`mossberg_anims.json` adds `Bluntforce_Reload_OneShell`; it does not replace or rewrite the shared rig. The existing donor has one fetch, repeated insertion taps, then one return. Preserve donor time [0,0.9667] and [1.9667,2.3], splice matching contact poses, and retime that complete **single** fetch/insert/return to 1.1 seconds. Donor interpolation uses SLERP for rotations and linear positions. A single red/brass shell is shown at the loading hand during the insertion, then hidden. Reload sound is cut from the same normalized donor windows rather than replaying its multiple-insertion sequence.

One reload press still starts filling the tube, not a new one-R-per-shell control scheme. Each completed insert adds one shell and replays this one-shell cycle while shells/capacity remain. Fire can interrupt using already-loaded ammunition. Both animation and insertion timer use the reload-speed multiplier.

The old dedicated-server reload command refilled to capacity. For **owned, held** Mossbergs only, the server now completes one shell per request, uses the same clip duration and authoritative dexterity, allows accepted fire to cancel the pending insertion, and handles a new request on the prior completion tick without losing its shell. Per-owner profile clones preserve the host's existing damage, cadence and ballistic tuning. Generic whole-magazine reload behavior and explicit host profile overrides remain intact. No wire-format/protocol change.

## Wood-stock alternative
`bluntforce_wood_gun.txt` / `bluntforce_wood_albedo.png` are an **alternate exported mesh/material, not a new spawnable item or loot entry**. Original stock shape is retained and recolored brown; pump stays dark and butt-end faces stay black. Stock UVs use an isolated atlas half; every original metal texel is unchanged. The standard black-stock gun remains the game default. User choice is still needed for replacing it versus registering a separate weapon/skin.

## Regeneration
- `python3 tools/install_mossberg_parts.py` regenerates meshes, wood atlas and item bounds.
- `python3 tools/author_mossberg_anims.py --source game/content/rig.json --out-dir /tmp/mossberg --max-pump .19`; copy only `mossberg_anims.json` and `mossberg_pump_action.json` to `game/content/`. This tool is pure stdlib and never writes the source rig.
- `tools/render_mossberg_icons.sh` regenerates icons 112/114 using Godot/Xvfb.

## Verification
- Build: 0 errors, 23 existing warnings.
- `gun.mossberg_parts`: 11 checks; separate geometry, single insert, shell visibility, pump travel/return, shared parked assembly and non-shotgun control.
- `gun.mossberg_reload`: 12 checks; ownership, clip/server clock, one round rather than full refill, interruption, exact-tick re-request, capacity, authoritative skill speed and legacy reload control.
- Zeroing only pump action keys makes the moving-pump test fail while the reload test stays green; restoring the keys passes both.
- All six MAC-10 tests passed as a focused regression slice (47 checks); headless cadence test logs its existing unsupported-display-server calls. No full suite.
- Actual Godot/Vulkan viewmodel snapshots at reload 0.30/0.80/1.10 seconds, pump 0.20 seconds and ADS. Separate matching-camera black/wood/pump/front-post mesh renders. These are **pose samples, not a full motion video**; the longer CPU-render capture attempts timed out, so they are not claimed as verification.
- Whole-body/pump geometry conservation and unchanged rear-ring corners verified independently; wood atlas metal half compared pixel-identical.

Staging integration only; no main merge or live-server deployment.
