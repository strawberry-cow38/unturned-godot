# Original pain and low-health screen pass

The native viewer now follows `game/HUD.cs` and `game/LowHealthOverlay.cs`:
source pain tint is painted first, then the screen copy applies desaturation and
the red vignette, then hurt wedges and ordinary HUD are drawn sharply above it.
This is the Godot port's authored low-health effect; its source explicitly says
it is a game-feel addition rather than a retail U3 effect.

The accepted owner health fraction starts the effect below 50% and reaches full
strength at 15%. `1-exp(-6*dt)` smooths both worsening and recovery. Death does not
reset or hide it; respawn/healing gradually returns to healthy color. A new
session resets the source HUD state. Singleplayer pause freezes the clock;
network pause continues it. No client health or damage is changed.

The shader performs the original non-HDR canvas arithmetic in sRGB: first mix
HUD.CR `(0.7490196,0.12156863,0.12156863)` by accepted pain alpha, then luma weights
`(.299,.587,.114)`, strength×.85 desaturation and the aspect-corrected
`smoothstep(.25,.85,d)` vignette toward `(.55,.02,.02)` at strength×.8. It returns
to the native linear HDR target once. The pass runs when either pain or lowHP
is active, so healthy players still see accepted hurt flashes. Godot4.6's
`RendererCanvasRenderRD::canvas_render_items` flushes preceding items before the
first `hint_screen_texture` copy; HUD constructs pain before LowHealthOverlay.

Camera ordering now explicitly preserves that canvas boundary:

| Native view | Source content |
|---|---|
| SceneCanvas2 | completed world/viewmodel, NightVision, then departing Information layer9 |
| Default HUD3 | pain + lowHP, then hurt wedges and ordinary HUD10 |
| Dashboard4 | dashboard11 frost/panels, vitals12 |
| Modal5 | AmmoPie60 frost and modal content |
| Console6 | console100; lightning remains above the last active lower UI view |

The layer9 route is refreshed before UI layout/camera update, including camera
replacement. HDR configuration follows it; the default HUD cannot composite the
viewmodel a second time. Existing world/scope/reflection rendering is unchanged.

All 570 native tests pass on the immutable `player-damage-v69` bridge; 24 renderer
cases remain opt-in. This includes 79 UI tests, the real UDP accepted hurt journey,
the source low-health clock and camera replacement/layer-routing regressions.

The ten-stage Vulkan fixture passed and every PNG was opened. It compares rendered
center/corner pixels against an independent source equation, keeps HUD pixels
unchanged, affects layer 9, heals back to the same initial scene, resizes to square
and stacks dashboard plus modal. It also uses the production world/viewmodel HDR
compositor and both night-vision modes, verifying that those effects precede the
map and HUD boundary. Root independently opened the pain, critical, square, modal
and both night-vision captures and found no blocker. It uses no stored visual
golden. Artifacts are `bevy/artifacts/bevy-low-health-{0..9}.png` and
`bevy-low-health.json`; the GPU log is `/tmp/unturned-low-health-v2-gpu.log`.
The combined application also passed a real PEI journey with the immutable
`player-damage-v69` bridge and `native-feedback-rain-main`: healthy frame 20,
accepted no-source damage 85 to HP15 at frame 162, settled low health at 231,
frosted Inventory at 247, equipped rifle at 440, death at 481 and accepted respawn
at 1655/tick1666 with HP100. The rifle and world receive the low-health effect;
ordinary HUD and Inventory text stay sharp. Death retains the red source effect
under the death panel, and respawn restores the original scene colors and removes
the vignette. All seven `bevy-feedback-*.png`/JSON pairs were opened by root and
independently reviewed by the world agent. The combined build includes RainGlass;
this feedback journey does not additionally claim wet-pane driving validation.

```sh
env -u WAYLAND_DISPLAY WINIT_UNIX_BACKEND=x11 WGPU_BACKEND=vulkan \
 VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
 UG_LOW_HEALTH_SHOT=/tmp/bevy-low-health \
 xvfb-run -a cargo test --manifest-path bevy/Cargo.toml \
 ui::low_health::visual::gpu_original_pain_low_health_and_canvas_boundaries \
 -- --ignored --exact --nocapture
```
