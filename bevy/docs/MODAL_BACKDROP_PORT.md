# Source modal screen backdrop

The shared `ui::backdrop::Backdrop::{ammo_pie,dashboard}(fade)` component marks a
UI root with its source backdrop. The source menu supplies its eased fade; the
renderer does not add another animation clock. AmmoPie uses the original radial
style; Inventory, Crafting and Information use the dashboard style.
MechanicsPanel retains its original dim-only background.

The rendering order is world and independent viewmodel composite, night vision,
ordinary HUD (camera2), dashboard blur/UI and vitals (camera3), modal blur/UI
(camera4), lightning, and console (camera5). Each backdrop camera has explicit target routing;
ordinary UI continues using the unique default UI camera. The backdrop reads
Bevy's completed shared view texture before its own UI pass and writes the other
ping-pong texture. No second world camera or read/write feedback texture is used.

The source AmmoPie backdrop has 25 taps weighted `1/(1+x*x+y*y)`, screen-physical
spread 3, mip LOD 1.8, and the original aspect-correct vignette/tint. Its alpha uses
the source cubic ease over the existing 120 ms open clock. The screen copy first
converts native linear colors into the original non-HDR sRGB RGBA8 backbuffer.
Levels 1/2 use Godot 4.6's seven-tap SIGMA2 Gaussian kernel, including its shared
workgroup layout and border sampling. Each level retains RGBA8 quantization.
The final source alpha composition happens in sRGB before conversion back to the
native linear target. The existing world/VM tone and UI passes are not repeated.

Primary implementation references are
[`AmmoPie.BACKDROP`](../../game/AmmoRadial.cs),
[Godot4.6 screen copy and mip lifecycle](https://raw.githubusercontent.com/godotengine/godot/4.6-stable/servers/rendering/renderer_rd/storage_rd/texture_storage.cpp),
and [Godot4.6 non-glow Gaussian](https://raw.githubusercontent.com/godotengine/godot/4.6-stable/servers/rendering/renderer_rd/shaders/effects/copy.glsl).
The adapted kernel retains the existing Godot MIT attribution in
`bevy/licenses/GODOT_POSTPROCESS.txt`.

Each mip chain belongs to its extracted camera, is rebuilt for physical
window-size changes, and is released with that view. Removed roots disable the
pass; removing the component restores ordinary routing. A replaced modal camera
is recreated and roots retarget before UI layout. Pipelines prewarm on inactive
frames, and no shared-view swap occurs until every pass can run.

`InventoryUI.BACKDROP_BLUR` uses the same weighted taps at LOD 1.5 with a flat
40% tint `(0.03, 0.04, 0.06)`, without the radial vignette. CraftingMenu contains
the same shader; SkillsUI and MapUI reference it. The native Inventory/Crafting
and Information roots replace their old flat scrims with this style. Vitals carry
`AboveDashboard`, route into the layer11/12 camera while a dashboard exists and
use their higher UI order. Ordinary HUD remains beneath the blur. When the
dashboard closes, vitals return to the ordinary HUD so the higher AmmoPie screen
copy still includes them. Simultaneous dashboard/modal layers also compose in
source order. Camera replacement and tab changes keep only one dashboard pass.

Dashboard fade now comes from the shared original `MenuSwoop` clock; see
`MENU_SWOOP_PORT.md` for panel movement, input lifetime and transition verification.
The native skills page and other source canvas blend behavior remain outside this
adapter.

Two routing/clock CPU cases, the complete 59-case UI group and five lightning
cases pass. Independent source/render-schedule review passed. The actual Vulkan
fixture passes and all four captures were opened: no fade, full fade, restored,
and resized from 1024×640 to 800×480. Prior HUD spatial variation drops from
1,852,200 to 3,378 and background variation from 2,343,600 to 9,156; opaque modal
and console controls are pixel-identical, and closing exactly restores the
baseline. These are within-run controls, not stored visual goldens. The initial
fixture sampled outside an automatically letterboxed image; explicit Stretch
fixes the fixture's bounds without changing the production pass.
Root independently opened the baseline, full-fade and resized frames and approved
the visual result.

The real integrated `bevy-lightbar-play-wail-frost.png` was subsequently opened by
root, gameplay and world reviewers: the original radial labels/chip remain sharp
over the frosted world and vitals. Dashboard validation adds production
Inventory/Crafting root coverage, map marker coverage, separate camera recreation
and a simultaneous layer11/12/60 test. All 62 UI tests and five lightning tests
pass. The dashboard Vulkan fixture and the repeated AmmoPie fixture both pass,
with all eight images opened. Dashboard vitals variation stays exactly 858,240
above an overlapping panel; the same control falls to 2,782 under AmmoPie.
Dashboard HUD/background variation falls below 1% of baseline, its flat tint
has no radial edge darkening, and both styles restore the baseline and survive
resizing. Root independently reviewed dashboard source and baseline/full-fade/
resized images; interfaces approved the separate routing/shader review and
independently opened both full-fade styles to verify the vitals layering.

Artifacts are `bevy/artifacts/bevy-modal-backdrop-{0,1,2,3}.png` and the accompanying
JSON report.
Dashboard artifacts use `bevy-dashboard-backdrop-*`; the expanded AmmoPie test
uses `bevy-modal-backdrop-layered-*`. Run the dashboard case by replacing the
test name below with
`ui::backdrop::tests::visual::gpu_dashboard_keeps_layer12_vitals_above_panels_without_radial_vignette`.
The actual fullgame Inventory/Crafting/Information capture subsequently passed:
`bevy-dashboard-{inventory,crafting,information}-fullgame.png` and JSON at frames
1228/1307/1339. Root and world independently opened all three. World frost, sharp
panels/text and sharp vitals are present; Information's vitals remain above its
panel. Blank crafting titles exposed a separate managed read-model gap being
fixed by root. Those application captures predate MenuSwoop and prove backdrop
integration, not animated transitions.
Run the ignored fixture on a display with Vulkan, for example from the repo root:

```sh
env -u WAYLAND_DISPLAY WINIT_UNIX_BACKEND=x11 WGPU_BACKEND=vulkan \
  VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
  UG_BACKDROP_SHOT=/tmp/bevy-modal-backdrop \
  xvfb-run -a cargo test --manifest-path bevy/Cargo.toml \
  ui::backdrop::tests::visual::gpu_blurs_prior_hud_preserves_modal_console_and_recovers_on_resize \
  -- --ignored --exact --nocapture
```
