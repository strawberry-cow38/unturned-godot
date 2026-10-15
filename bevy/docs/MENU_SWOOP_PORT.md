# Original dashboard transitions

`ui::swoop` applies `game/MenuSwoop.cs` to Inventory, Crafting and Information.
Each screen retains one 0–1 clock across accepted snapshot/widget rebuilds.
Opening and closing take 85 ms for a full traversal; reversal keeps the current
position. Opening uses `1-(1-t)^3`, closing uses `t*t`, and panel translation is
`(1-alpha)*54` logical pixels. The backdrop stays anchored to the viewport.

The existing UI state changes immediately. Retiring widgets lose buttons and
picking while their pixels finish fading; their frost disappears at once, so a
tab switch cannot apply the dark tint twice. Inventory/Crafting remain on layer11
while closing. Information follows `MapUI.Close`: it ducks under the HUD at
layer9, and a direct close retains the free cursor and blocks world input until
the animation completes. Tab navigation uses the source `captureMouse:false`
behavior; reopening cancels the pending capture. The shared `UiState` gate reaches
the existing player, Alt-look and mounted-input consumers. Continuous vehicle
axes, handbrake, aircraft stick inputs and mouse horn also respect the cursor;
discrete ignition, light and seat keys retain the original `_UnhandledInput`
behavior during that short close.

Bevy UI lacks Godot's inherited `Modulate`, so child background, border, text,
image and box-shadow colors retain their source alpha and last applied value.
Animation multiplies the source alpha once and adopts real hover/status changes.
Fully open roots stop traversing their children. Session teardown removes
retiring roots and clocks; authority inventory/storage state is unchanged.

Eight focused tests cover exact timing/reversal, alpha retention, real Inventory
snapshot rebuilding, tab retirement, Information layer/cursor behavior and the
actual player and vehicle input readers. Mouse movement/fire/movement stay
blocked with a visible cursor during map closure and resume afterward; driving
axes stay zero while the discrete ignition key remains live.
Root and interfaces independently reviewed source formulas, lifecycle and
scheduling with no blocking findings. The complete UI group passes 68 tests
with five opt-in renderer fixtures ignored; the focused set passes eight tests.
The broader vehicle group passes 107 tests with nine opt-in fixtures ignored.

The Vulkan run passed and all ten frames were opened: `bevy-menu-swoop-0..9.png`
plus `bevy-menu-swoop.json` retain the actual source clocks and root offsets.
Partial opening visibly rises/fades, a tab change retires the old panel, closing
keeps only fading panel pixels over the unfrosted world, and Information reverses
from the same clock. Vitals remain sharp in every frame and final pixels equal
the initial scene within that run. This uses a controlled Information layer root;
the actual map builder's layer and lifecycle are covered separately by UI tests.
Root and interfaces independently opened the partial/tab/close/reversal frames
and passed the visual review.

The renderer retains one shared dashboard frost pass. An outgoing panel is a
separate fading draw; it is not re-sampled by a later equal-layer menu screen copy
as Godot's original canvas ordering can do. The source's immediate old-frost
removal and single tint are preserved. Skills, original Inventory/Crafting layout
and dashboard-open WASD routing remain separate existing UI work; this slice
does not claim those features.

The Vulkan fixture uses the production Inventory/Crafting builders plus an
Information layer control, freezes only its supplied frame delta while pipelines
and screenshots settle, then advances the real source clock. It captures open,
tab, close and reversal states and compares restoration/unchanged vitals within
the same run, without stored visual goldens:

```sh
env -u WAYLAND_DISPLAY WINIT_UNIX_BACKEND=x11 WGPU_BACKEND=vulkan \
  VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
  UG_SWOOP_SHOT=/tmp/bevy-menu-swoop \
  xvfb-run -a cargo test --manifest-path bevy/Cargo.toml \
  ui::swoop::visual::gpu_source_menu_swoop_fades_slides_switches_and_restores_the_scene \
  -- --ignored --exact --nocapture
```
