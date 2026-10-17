# Focus outline port

Sources:
- `game/OutlineOverlay.cs`: `MakeOutline`, `ShowOutline`, the mask SubViewport and the CanvasLayer 50.
- `game/content/item_outline.gdshader`: the dilate rim.
- `WorldItem.FocusColor`.
- The `SetFocused`/`SetLookFocused`/`SetShelfFocused` callers.

## How the source works

Each focusable builds an always-white, unshaded, double-sided silhouette of its own mesh on `OutlineLayer` (bit 19), which main cameras cull. `ShowOutline` toggles it and claims the single global rim colour (`WorldItem.FocusColor`: item rarity, white for containers, doors and props).

A SubViewport shares the world, copies the active camera every frame, and renders only that layer into a transparent mask. A fullscreen dilate pass (`thickness` 3.5 px) then colours the mask's outside rim. It sits on CanvasLayer 50, modulated by the night-vision tint, and warps with the underwater refraction.

The whole pass is disabled while driving (`DrivingSuppress`) and by `GraphicsOptions.Outline`.

## Native

`bevy/src/outline.rs` (`OutlinePlugin`):
- **Claim.** `InteractionFocus::outline(action, args, entity, colour)` attaches the silhouette to the candidate that offered it. Only the winning candidate's claim survives, so exactly one target glows, matching the source's single look focus and single `FocusColor`.
- **Mirror.** On a focus change, every non-skinned `Mesh3d` under the target (the target and its descendants, so a body and its swinging leaf both glow) gets an unlit, white, cull-less child copy on render layer 19, with no shadow. Children follow doors and bodies. The old mirrors despawn when focus moves.
- **Mask.** A `Camera3d` parented to the `PlayerCamera` with the same projection renders only layer 19 into an `Rgba8UnormSrgb` image the size of the window, with no MSAA and a transparent clear. It is active only while something is outlined.
- **Rim.** `item_outline.wesl` is the shader ported line for line: 8-neighbour max at `thickness` px, clear inside the silhouette, and the same underwater warp expression driven by `UnderwaterState` and the shared `WOBBLE` constants. It draws on a fullscreen `MaterialNode` on its own UI camera at `OUTLINE_ORDER` (between the layer-40 tool label and the modal layer, as canvas layer 50 sits). The colour is `FocusColor` × the night-vision tint in sRGB, as the source's `Modulate` does.

Claimers wired so far:
- dropped world items, with the rarity colour (`ItemTool.RarityColorUI`);
- map containers/shelves (`openStorage`), in white;
- store-shelf items, in their rarity colour (`ShelfItemBody.SetFocused`);
- deployables, in `Deployable.OutlineColor` (0.82/0.83/0.90);
- vehicles, in their `.dat` `Rarity` colour, through `InteractionFocus::outline_net_id` (any action the winner offered for that NetId);
- map doors, container doors and prop seats, which outline the whole prop (body plus leaf) in white. These are wired through the same claim but have no render yet.
- world devices (`WORLD_DEVICES_PORT.md`): toggle lamps, notes, radios and patient monitors in white, world-scenery desk/standing lamps in `Deployable.OutlineColor`, vending machines in their state colour (with the red refusal blink), through `InteractionFocus::outline`.

## Deviations and gaps

- **Claimers not yet wired:** the vehicle's red "can't act" tool colour, beds (natively an emissive highlight), TVs, NPC/puppet bodies (skinned meshes are not mirrored yet) and forage fruit. They join by calling `focus.outline` beside their `offer`.
- **`GraphicsOptions.Outline`:** the native `settings.graphics.outline` (default on) disables the whole pass, with no mask and no rim; a unit test and a negative control cover it.
- **No `DrivingSuppress`:** nothing offers focus from a seat natively, so the pass is already idle there.

## Verification

- **Unit tests** (`outline::tests`):
  - Only the winning candidate can claim the silhouette and colour; a nearer offer drops an earlier claim.
  - Every mesh under the target (body and leaf) is mirrored onto layer 19 with an unlit, white, cull-less material, and the mirrors follow the focus and are cleared when it ends.
- **Negative controls, each failing as intended:** the mirror missing its render layer, descendants not mirrored, and a claim not tied to the winning candidate.
- **Visual:** the ignored fixture `entities::outline_visual::render_source_item_focus_outline_beside_its_neighbours` (`UG_OUTLINE_SHOT`) reproduces `Main.BuildItemTest` with `UG_FOCUS=1` (items 13, 4, 14). It drives real world-item replicas with bridge catalogue meshes through the production `interact_items` look and claim. The rare-blue rim surrounds the AR-15's full silhouette, outside it only, as in the Godot 4.6.2 oracle. Renders: `bevy/artifacts/{bevy,godot}-outline-itemfocus.png`, plus the mask `bevy-outline-itemfocus-mask.png`.
- **Full-game journey** (`--play --vehicle roadster`, real xdotool input): walking up to the Roadster (`.dat` RARE) shows the blue rim with "[F] Open trunk" (`bevy/artifacts/bevy-outline-journey-roadster.png`).
- **Two implementation notes:**
  - The rim samples the mask's red channel: the mask is white on black, so red carries the source's alpha coverage regardless of how the image target stores alpha.
  - The overlay node must not be parented to its UI camera, otherwise Bevy UI never lays it out.
