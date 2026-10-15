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

## Native movement, interactions and vehicle integration

- Native suite: 66 passed after swimming, ladder attach/mantle, real vehicle
  suspension/acceleration/braking/steering, buoyancy and blocked exit checks.
- Source combat adapter changes: 618 UnturnedNet tests passed, including three
  new memory-transport regressions. Optional callbacks retain existing defaults.
- Peer review corrected the player capsule radius to the original 0.35 m and
  ladder colliders to the source solid backing box. A real collision test
  exercises attach, single entry snap, top hold and a flush-roof mantle.
- Vehicle seat review found persistent controller output replacing seated
  velocity with an old walking step. Seated players now ignore that output;
  physics also pauses with the menu/pause state.
- Native rendered vehicle smoke exposed nested compound colliders from async
  decomposition. The vehicle adapter flattens these into leaf hulls and tests
  the mixed decomposed/authored collision case. The subsequent rendered smoke
  reached tick 452 with eight authoritative cars and no panic.
- Real keyboard and mouse journey: F1, `give 4`, Enter, inventory, select AR-15,
  Equip, close, hotbar 1. `bevy-inventory.png` shows the original item icon and
  grid footprint. `bevy-equipment-before-animation.png` and its JSON confirm the
  weapon moved from page 2 to slot 0, heldItemId=4, gunAmmo=30, and original
  arms/weapon geometry rendered. Console spaces initially disappeared because
  winit supplies Key::Space separately from Key::Character. Fixed and covered by
  a real KeyboardInput-message regression.
- Short synthetic mouse clicks can land entirely between software-rendered
  frames; the test holds the button for 0.5 seconds to exercise the visible UI.
- Full-map services retain source placement indices, resource lifecycles and
  containers. Nearest visible item, container, forage resource and vehicle door
  now share one interaction selector, preventing one F press from firing
  several competing commands. Leaving storage releases its server lock.

Weapon animation/ADS, detailed vehicle views, procedural structures, source
foliage shading and the remaining parity ledger still need further validation.

## Native weapon trigger and first-person animation milestone

- Native suite: 92 tests passed. This includes original additive aim transforms,
  source RK4 viewmodel springs, held-food prefab LOD/animation paths, and trigger
  acceptance bookkeeping. Burst rounds count authoritative fire events, so
  cadence rejections and duplicate events cannot shorten or repeat a burst.
- Live keyboard/mouse tests with a real authoritative AR-15: holding fire for
  three seconds in semi mode spent exactly one round (30 to 29); burst spent
  exactly three (29 to 26); safety spent none. Reload without a spare magazine
  kept ammunition at 26. Snapshots and screenshots are saved as
  `bevy-semi-held-three-seconds`, `bevy-burst-held-three-seconds`, and
  `bevy-safety-and-no-free-reload` under ignored artifacts.
- Visual review found a source zero-position sentinel for legacy iron sights;
  the original fallback offset is now applied. Viewmodel bones also need
  inherited Bevy visibility for their held-mesh descendants. Corrected captures
  are pending. The source still-image harness resizes its oversized viewport;
  gameplay instead crops it. Native FOV therefore keeps the source's 60-degree
  vertical field of view rather than matching that distorted still framing.
- Inventory Equip now uses exact-instance `equipAndHold`, and hotbar selection
  uses authoritative cells. Two identical weapons cannot substitute for the
  selected instance while the slot transaction is pending.

## Native consumables, throws and chair exits

- Native suite: 115 passed; bridge integration: 33 passed; original core network
  suite with optional host adapters: 623 passed.

- Corrected weapon captures (`bevy-weapon-corrected-hip.png` and
  `bevy-weapon-corrected-ads.png`) show both original iron sights, with the
  sight line aligned in ADS. The native camera retains the original vertical FOV.
- Real UI journey equipped two separate cans of beans, played the original
  eating clip/held-part animation, deleted the selected can, restored food,
  and equipped the next can. Eating the last can restored the prior rifle
  with all 30 rounds intact. `bevy-food-held`, `bevy-food-used`,
  `bevy-food-rearmed` and `bevy-food-empty-restores-gun` capture this sequence.
- A six-second grenade hold kept both grenades in inventory and no projectile
  existed. Release spent one and created authoritative projectile 717 at
  `[-522.84375,33.84375,54.464844]`; its original mesh rendered in flight, and
  the entity retired after the fuse. The remaining grenade re-equipped. See
  `bevy-grenade-held`, `bevy-grenade-windup-held-six-seconds`,
  `bevy-grenade-released`, and `bevy-grenade-rearmed`. Smoke/flare/blast
  presentation remains a separate unfinished port.
- Peer review fixed same-ID weapon burst carryover using an exact-selection
  version, broken-gun burst cancellation, the source 2.2-second consume fallback,
  fallback weapon preservation across food switches, and rejected re-equip
  recovery. Native repair input now renews the server's short blowtorch lease;
  actual repair/salvage effects remain authoritative.
- A real Rapier chair-exit test checks the original ordered fan around a wall,
  supporting-floor leniency, and a fully blocked exit. Seated capsules and camera
  poses follow replicated prop-seat occupancy, then use the source exit search.
- Session loads reset hand transactions, player capsules and projectile replicas;
  explicit session epochs also refresh vehicle and door geometry even when IDs
  and transforms happen to match the previous session.

## Native recoil, throwable effects and world-origin integration

- The integrated bridge build and native runner `bevy/check.sh` passed 141 tests;
  the final native suite passed 143 after parking and curve regressions;
  the corresponding managed milestone passed 43 bridge and 630 network tests.
  The runner builds the actual bridge used by the native origin regressions.
- Live firing captures `bevy-recoil-before` / `bevy-recoil-after` show one held
  semi-auto shot spending one round (30 to 29). Pitch remains at 0.25670373° and
  yaw at 359.98474° after settling, matching the first original Eaglefire pattern
  increment. Recoil changes real aim, with original stance/skill/condition rules;
  the viewmodel only adds its separate positional shake. Event ticks preserve the
  pattern when several fixed updates arrive in one slow rendered frame.
- Visual inspection found a real event contract bug: authority event vectors use
  `{x,y,z}`, while snapshot positions use arrays. The native decoder now accepts
  both finite forms, with a regression using the actual smoke event payload.
  This also restores event-positioned gun audio.
- `bevy-smoke-cloud-fixed`, `bevy-flare-live`, and
  `bevy-flare-clear-ground` were opened and inspected. The original blue smoke
  palette produces a growing lit cloud (33 particles initially, 220 when mature).
  The original red flare has a white-hot tip, sparks, tinted smoke and a visible
  red light pool on the ground. Flare fading uses the remaining authoritative
  lifetime, including after load, instead of starting a fresh local timer.
- Source blast and vehicle emitters share native billboard meshes. Tests cover
  continuous birth rates across frame rates, moving parents, origin shifts,
  one-shot bursts, texture-atlas cycles per particle lifetime, stop/fade cleanup,
  and loaded flare light intensity. Peer review measured the original Godot
  growth curve and corrected its Hermite interpolation while keeping color
  gradients linear. The regression checks original quarter-life sample values.
- `bevy-night-flare` shows the original sparks, plume, hot tip and a broad
  reddish ground light pool at night. `bevy-night-blast-initial` confirms the
  short flash; `bevy-night-blast` captures the accepted grenade fireball and
  both dust bursts after mesh upload (28/36/24 particles at age 0.2152s).
  The event-triggered capture waits one render extraction so the transient mesh
  is visible. Both images were opened and inspected; no live renderer errors.
- Distant procedural captures `bevy-distant-origin-tunnel`,
  `bevy-origin-before-crossing`, and `bevy-origin-crossing` were inspected by the
  world reviewer. A live 1024m shift changed the absolute origin to
  `[-12032,-19712]`, retained local owner position `[176,59.406,34]`, and refreshed
  25,254 authority triangles with no missing assets or stale-frame errors. The
  original fall damage remained active. Real bridge/Rapier tests cover same-tick
  rebasing, queued teleport recovery, render/physics agreement and stale-coordinate
  rejection. See `WORLD_PORT.md` for the exact scope.

## Native driving arms

- Seven focused viewmodel tests pass. The actual original rig enters
  `Idle_Drive`, hides the held mesh, disables the overlay for a passenger, and
  restores the equipped hold after exiting, including driver-to-passenger changes.
- The wheel pivot, axis and angle come from the same source data as the rendered
  steering wheel. Projection tests cover different main-camera fields of view,
  0/70/90/180-degree head turns and both steering directions. Hands remain behind
  the camera when looking backwards. Peer review checked the native schedule,
  current camera/body transforms and the driver/passenger/aircraft visibility gates.
- A source translation error subtracted the hand midpoint before transforming it
  into the vehicle's camera-space basis. The port applies that basis first, so
  turning the head preserves the intended wheel pin and original 9cm hand drop.
  The regression demonstrates the old formula drifting more than half a metre.
- `bevy-driving-pilot` confirms actual authoritative driver seat 0 with hands
  around the wheel. `bevy-driving-turn-wheel` retains the pin while looking
  sideways with 27.42° steering. `bevy-driving-look-behind` leaves the arms behind
  the camera at a 180° head turn, without snapping them into view. Both root and
  peer reviewer inspected the original blocky rig geometry. The earlier
  `bevy-driving-forward` capture is actually passenger seat 2 and correctly has
  no driving overlay. No pixel-size parity claim is made without a matching
  original cockpit capture. This run also exposed a shader type error in the
  concurrently added water renderer; that independent fix and its validation
  belong to the water milestone.
