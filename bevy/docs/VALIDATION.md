# Migration validation log

Source baseline: `2ca64fc61a8607598612896c547b89a36e773a88`.

## 2026-10-14 initial engine integration

- Original `./test.sh --l0`: **2,581 passed, eight failed**, before any changes to
  the original core or its tests. The longest suite took 17m53s. Failures are
  `TallMirrorAndTrophyKeepTheirSpace`, `WallsComeOutCornerSolved`,
  `PottedPlantsKeepToTheirOwnDraws`, `WindowsMakeWayForABedOrAWardrobe`,
  `OpeningsFitTheirWalls`, `RuntimeDecorSurvivesASave`,
  `WindowsSitOverWhatStandsBeforeThem`, `DesignationFloorsSurviveASave`.
  These concern procedural building decoration, clearances and save round trips.
  Results and individual assertions are in ignored `.testresults/*.trx`.
- Native Rust tests: 33 passed after checking weighted skeletal rest dimensions
  against the original animal measurements. A naive identity bind-matrix test
  was incorrect for the source animal skeleton's 90-degree axis transform.
- Bridge integration: 12 passed, including real loopback inventory mutations,
  rejection of invalid moves, collision-aware combat, loot provenance across
  saves, stance headroom and server clock rollover.
- Headless bridge smoke: 250 ticks at 50 Hz, exact original walk model, final
  position `[0,0,-22.500029]` and connected local server.
- Catalog regression found and fixed: preserving the trailing slash in the
  resource-path adapter restores all 59 source gun data/model pairs and the nine
  original heavy-weapon movement penalties.

## Visual checks

The original Godot 4.6.2 Mono build was compiled and imported, then rendered with
Vulkan/lavapipe. `artifacts/godot-pei-reference.png` shows the original PEI world
from the original play harness. The original renderer emitted billboard sync
warnings but produced a valid image.

`artifacts/bevy-menu.png` was captured and inspected at 1280x720. Original PEI
buildings and textures are correctly oriented, roads already present in town
props align, and vegetation and terrain are visible. The first capture exposed
missing UI: Bevy selected the inactive higher-order viewmodel camera as the UI
target. A dedicated default UI camera fixes this and requires a fresh capture.
Sky, terrain material blending and distant streaming still need further parity
work; this capture is not evidence of full visual parity.

Follow-up `bevy-menu-fixed.png` visibly confirms the menu and the original cloud
textures rendered by the native sky shader. `bevy-pei-play.png` confirms the
first-person terrain, sea, vegetation, buildings and HUD. Its companion snapshot
records 614 real simulation ticks, health 100, and stable feet at y=30.827 near
the authored spawn (initial y=31.301 includes the source's spawn clearance).
There was no fall through the terrain. The native suite then passed 42 tests.
Remaining visual issues include terrain boundaries, lighting calibration and
the HUD's default ammunition display when the hands are empty.

For Xvfb captures under WSL, unset `WAYLAND_DISPLAY`; otherwise winit can connect
to the host Wayland compositor and wait without producing frames in Xvfb:

```sh
env -u WAYLAND_DISPLAY \
  VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json WGPU_BACKEND=vulkan \
  xvfb-run -a bevy/target/debug/unturned-bevy \
  --shot bevy/artifacts/bevy-menu.png --shot-frame 20
```

Artifacts are ignored by Git. Each new native capture writes a companion JSON
authority snapshot for checking the actual player's position and world state.
Audio can be loaded and controlled in this environment, but its missing audio
device prevents an audible listening test.
