# Native mounted controls and accepted effects

The native operator resolves the original vehicle seat and selected mount from
accepted replicas. A seat's own guns take priority; the driver's fallback works
only while the corresponding gunner seat is free. The 1/2 bindings select the
original mount slots without replacing held inventory. Entering a vehicle resets
the source gaze and mount slot; changing seats inside that vehicle preserves
them. F1–F12 sends the existing validated seat command. Backquote remains the
console binding, with the F1 console alias available on foot.

Mount intent is refreshed before each 50 Hz authority tick, including multiple
ticks within a slow render frame. It contains aim, trigger and requested hatch
state, never damage, ammunition or a fabricated shot. Singleplayer pause queues
no commands; multiplayer menus continue sending trigger release. Missing mount
replicas cannot invent ammunition. The camera ray excludes the player's body and
all nested parts of the ridden vehicle, while retaining world, water, actors and
other vehicles. Authority still determines actual barrel direction, limits,
traverse rate, cadence and firing permission.

Original tank periscope/gunsight sensitivity replaces held-rifle ADS sensitivity
while seated. Ordinary passengers and the crosshair-driven driver retain source
head limits; turret gunners can look around their complete permitted view.
Current accepted yaw and gunner rise place the body before the camera. Gunner
position rotates with the main turret's yaw, while camera orientation remains
hull-relative. The explicit exported driver periscope anchor avoids an original
static-initializer ordering issue. See `VEHICLE_SEATS_PORT.md` and
`TANK_OPTICS_PORT.md` for source constants and fixture evidence.

An operated mount hides the held viewmodel and suspends its trigger, reload and
firemode path. Losing driver fallback restores driving arms; returning to an
ordinary passenger restores the existing rifle rig. The HUD reads the selected
mount's original name and accepted belt count. The generic mounted crosshair is
suppressed by the gunner's original gunsight reticle.

Only accepted `mountShot` facts produce muzzle effects. The original texture,
random billboard roll, 50 ms flash, muzzle light, source report attenuation and
per-gun 40 ms audio cap are retained. Player cannon shots use scale 2.2, twelve
propellant particles, the original attached breech reload cue, and the shared
local operator camera spring. NPC reports keep source scale 1. Duplicate facts
do not replay effects; disconnect/session replacement clears transient effects.
The original mounted camera overwrote its requested spring: preserving a visible
seated kick here is an explicit correction documented in `CAMERA_FLINCH_PORT.md`.

Projectile travel and damage remain in the shared authority. The remote flight
adapter consumes accepted launch/continuation/retirement/blast facts, as described
in `MOUNTED_WEAPONS.md`; native muzzle effects never perform a second damage ray.

## Verification and remaining work

The final frozen v4 verification executable passes 62 vehicle and 30 player tests
against the isolated mounted-muzzle v66 bridge. Separate original-rig transition,
optics/crosshair and UI seat-key tests pass. These include the actual native
fixed-tick input sender over the bridge, a 200 ms render frame, pause/menu release,
real Rapier self/target/water queries, source camera entry/seat changes, and actual
authority partial gunner rise adopted in the same native frame.

The first full-game PEI driver/cannon render caught an immediate collision with
the firing vehicle's own detailed barrel triangles. The failure capture is
`artifacts/bevy-mounted-self-contact-failure.png` with its authority sidecar.
The mounted-only collision adapter correction passed its negative control and
the playable rerun. The frozen `native-mounted-playable-v2` executable used
`mounted-muzzle-v66-final/UnturnedBevy.Bridge.dll` with `--play --vehicle=tank
--fixed-fps=50 --shot-event=mountShot --stay`. Console teleport only placed the
player near the original door; actual F interaction boarded the tank. Native
mouse/buttons then fired the cannon, switched with F2, cycled the sight with
Aim, opened the hatch with VehicleDoor, selected HMG with 2, held Fire, and
returned to the driver with F1.

Opened captures and independent reviews confirm:

- `bevy-mounted-driver-cannon-verified`: accepted cannon belt 60→59, original
  muzzle star and twelve smoke particles, 3.37° operator kick, health 100.
- `bevy-mounted-gunner-gunsight-verified` and `bevy-mounted-gunner-12x-verified`:
  accepted gunner seat/sitting pose, 8× then 12× source optics with one reticle.
- `bevy-mounted-gunner-hmg-verified` and `bevy-mounted-gunner-hmg-burst-verified`:
  accepted open hatch/rise 1, generic crosshair, HMG slot and muzzle flash;
  held firing advances shot sequence 2→6 and accepted belt 399→395.
- `bevy-mounted-driver-return-verified`: F1 returns to the driver, preserving
  HMG slot 1 and belt 395, restoring the periscope and lowering the empty gunner
  position. The operator remains alive at health 100.

PNG/authority sidecars are under `artifacts/`. The v2 firing captures also
exposed a one-frame HUD label delay. The production HUD now updates before
PostUpdate layout; its regression covers accepted cannon ammunition, HMG
selection/ammunition and exit back to held equipment in that same frame.
The frozen v3 playable executable repeats actual boarding/F2/hatch/2/Fire on PEI:
`bevy-mounted-hud-same-frame-verified` shows HMG399 in the flash frame alongside
accepted `remainingAmmo:399` at tick111, with operator health100.
These local gameplay captures do not establish a complete multiplayer journey.

A separate UDP observer check uses the same frozen v3 executable with
`--connect=127.0.0.1 --port=47890 --fixed-fps=50 --shot-event=mountShot --stay`.
A separate managed host owns a stationary source tank at [-520,31,50], accepts
its driver's cannon input toward [-520,33.8,-400], and has a diagnostic wall at
Z=-40. The observer uses `UG_INF_CAM=-510,37,40,135,-18` to inspect the muzzle.
`bevy-mounted-udp-cannon-verified` shows the original flash and source lighting
on that remote tank; its sidecar records `network.mode:client`,
`ownsWorld:false`, accepted shot tick627/sequence1/ammo59 and twelve propellant
particles. The independent observer has no operated mount and no local cannon
flinch. Its read-only rocket row is already15m along the accepted barrel axis.
This is real UDP delivery through the full native client, with a controlled
managed host, not two native players driving. The offscreen flight and remote
impact still need their own rendered captures; the host sidecar is retained as
`bevy-mounted-udp-host.json`.

After the receiving-frame input correction, the final v4 playable build repeats
actual boarding, F2, Aim cycling to12x and F1 return. Opened captures
`bevy-mounted-v4-gunner-12x-verified` and
`bevy-mounted-v4-driver-return-verified` retain the original optic presentations;
the scheduling regressions establish the input behavior on the receiving frame.

Mounted muzzle stars now use the original per-view vertex billboard. The current
view's right/up basis rotates each source quad independently for the main camera,
scopes and reflections. A mesh-instance tag retains each accepted shot's random
roll while sharing one HDR 2.6 additive material. The 50ms accepted-fact lifecycle,
light, report, smoke and operator spring remain unchanged. Shader-oriented quads
bypass the unchanged flat mesh bounds and never cast shadows.

The three-camera Vulkan fixture shows the same original star from front, side
and reflected angles (`bevy-mounted-flash-views.png`). All three retain 9524 lit
pixels and 153×148 bounds. Selecting the old CPU orientation makes the side view
vanish and the angled view flatten (`bevy-mounted-flash-cpu-reference.png`); the
same fixture assertions fail. Front-view pixels match between implementations.
These are simultaneous-view contract checks and opened images, not saved visual
goldens. Independent source/shader and image reviews passed. Runtime tests verify
actual accepted facts create the per-view material with independent roll tags,
shared material identity and the required culling/shadow guards. Both focused
accepted-event tests pass against the immutable mounted-muzzle-v66-final bridge;
the runtime-linked Vulkan fixture passes without shader validation errors.

Original door-gunner characters and their damage/removal, NPC helicopter
flight/targeting/burst/flee behavior, and complete armed-fleet multiplayer gameplay
journeys remain separate port work. Controlled tank anchor/optic render fixtures
do not establish those behaviors.
