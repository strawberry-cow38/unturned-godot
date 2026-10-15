# Native accepted vehicle lighting

`vehicle::lighting` consumes the protocol68 electrical state described in
`VEHICLE_LIGHTING_AUTHORITY.md`. Original geometry is split/built once by
`lighting::geometry`; `presentation::upload` registers exact hull-local source
lamp centers with the host once per world epoch. Refreshing metadata never
repairs a lamp. Vehicle L input sends `CycleHeadlights`; it does not flip a local
flag. Current swamping is published independently of permanent engine drowning.

## Presentation

The original low/high source constants come from the managed catalog: range45/98,
energy9/17, cone25/21 degrees and attenuation1.3/.85. Headlight profiles use the
shared per-view source fade adapter, and each original side binds only its first
matching spotlight. Tail emitters face backwards with the source range3,
energy2.2 and attenuation.6. Head fill remains lit until both headlights break.
Only source-tagged head/fill/siren lights join the existing viewmodel light pool.

Lens materials preserve the source's alias behavior: high beam changes only the
aliased headlight material; a later head/tail or break/repair transition resets
both to energy2. Source lightbar repair restores emission while retaining the
previous darkened albedo. Original three siren patterns and their separate audio
clock run at50Hz. A broken amplifier produces the original pitch wobble/dropout;
only all three broken lightbar parts silence the loop. The original siren and
horn audio files use source gains and distances.

Helicopter navigation lights follow accepted engine/health state. The belly
beacon follows source rotor spool, including engine-off rundown, and extinguishes
on wreck. These are visual clocks, with no separate battery or damage authority.
Singleplayer pause holds them; multiplayer menus leave simulation running.

Accepted `vehicleLampBroken` events drive the shared original glass burst and
sound before the lens material darkens. Lasting broken masks alone never replay
one-shot effects. Details: `VEHICLE_LAMPS_PORT.md`.

## Verification

The immutable bridge `bevy/.local/vehicle-lights-v68-final/UnturnedBevy.Bridge.dll`
and native test binary `bevy/.local/native-vehicle-lights-final` pass twelve lighting
CPU/ECS/integration tests. The central journey launches a real host and a separate
UDP driver, builds the original police meshes, and uploads their actual triangles.
Normal host Eaglefire shots consume ammunition and resolve lamp contacts; no
fixture writes damage masks or substitutes a trusted lamp-damage operation.
Checks cover low/high switches, one broken beam while its partner stays lit,
unchanged hull health, repeated metadata, accepted repair, all lightbar parts,
audio retirement and child cleanup. The broader vehicle group also passes93
tests (eight separate GPU tests ignored) against this same protocol68 build. Separate tests cover pause, geometry,
pattern/alias ordering and one-shot break lifecycle.

The ignored `render_actual_udp_low_high_damage_and_siren` test renders that same
journey with Vulkan. The inspected first captures are
`bevy/artifacts/bevy-vehicle-light-{off,low,high,broken-left,siren}.png`, each with
accepted-state JSON. They show the source cones and obstacle shadows, the single
shot-out lens/beam, restored pair and live red lightbar. HP remains6000 throughout;
low/high ranges are45/98 and battery drains on the host. The host holds the body
stationary: these images do not claim a full driving/input journey. The close `bevy-vehicle-light-break-burst.png` capture, from
`native-vehicle-controls-first`, additionally shows six original flat cream chips
after the accepted shot (36 mesh vertices), independently opened and reviewed.
The delayed-event regression covers unrelated local/server tick origins; pause
checks hold decoder settings and the original wind-down timer. Four independently
opened Huey/Hind off/lit images cover source navigation and belly lights. The
complete PEI application also boots with these plugins and registers all seven
police lamps; playable UI verification is continuing separately. Exact headlight shadow conversion has its independent
GPU/readback evidence in `HDR_NIGHTVISION_PORT.md`.

Reproduce (unset WSLg's Wayland display when using Xvfb):

```sh
UG_BRIDGE_DLL="$PWD/bevy/.local/vehicle-lights-v68-final/UnturnedBevy.Bridge.dll" \
  cargo test --manifest-path bevy/Cargo.toml vehicle::lighting

env -u WAYLAND_DISPLAY WINIT_UNIX_BACKEND=x11 WGPU_BACKEND=vulkan \
  VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
  UG_BRIDGE_DLL="$PWD/bevy/.local/vehicle-lights-v68-final/UnturnedBevy.Bridge.dll" \
  UG_VEHICLE_LIGHT_SHOT="$PWD/bevy/artifacts/bevy-vehicle-light" \
  xvfb-run -a bevy/.local/native-vehicle-lights-final \
  vehicle::lighting::presentation::tests::visual::render_actual_udp_low_high_damage_and_siren \
  --ignored --nocapture --test-threads=1
```

No rendered image is used as a pixel golden. Source shadow atlas allocation and
filter topology differ between engines; exact per-light conversion work is
tracked in `HDR_NIGHTVISION_PORT.md`. Trailer coupling, manual horn networking,
vehicle glazing, free per-index tire repair and the remaining fleet integration
journeys remain separate work.
