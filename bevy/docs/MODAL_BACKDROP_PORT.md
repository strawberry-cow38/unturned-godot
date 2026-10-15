# Source modal screen backdrop

The shared `ui::backdrop::Backdrop::ammo_pie(fade)` component marks a modal UI
root. The source menu advances/eases its own fade; the renderer does not add
another animation clock. The first consumer is LightbarRadial's existing AmmoPie.
MechanicsPanel retains its original dim-only background.

The rendering order is world and independent viewmodel composite, night vision,
ordinary HUD (camera2), source screen-copy blur, modal UI (camera3), lightning,
and console (camera4). The modal camera has its own explicit target routing;
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

The mip chain belongs to the extracted modal camera, is rebuilt for physical
window-size changes, and is released with that view. Removed roots disable the
pass; removing the component restores ordinary routing. A replaced modal camera
is recreated and roots retarget before UI layout. Pipelines prewarm on inactive
frames, and no shared-view swap occurs until every pass can run.

Inventory, crafting, skills and map use another source backdrop style at layer11,
with vitals above them at layer12. They are not routed through this layer60 slice;
that lower-layer composition remains a separate follow-up. Other source canvas
blend behavior is outside this screen-backdrop adapter.

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

Artifacts are `bevy/artifacts/bevy-modal-backdrop-{0,1,2,3}.png` and the accompanying
JSON report. The real vehicle radial-menu journey is a separate integration check.
Run the ignored fixture on a display with Vulkan, for example from the repo root:

```sh
env -u WAYLAND_DISPLAY WINIT_UNIX_BACKEND=x11 WGPU_BACKEND=vulkan \
  VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
  UG_BACKDROP_SHOT=/tmp/bevy-modal-backdrop \
  xvfb-run -a cargo test --manifest-path bevy/Cargo.toml \
  ui::backdrop::tests::visual::gpu_blurs_prior_hud_preserves_modal_console_and_recovers_on_resize \
  -- --ignored --exact --nocapture
```
