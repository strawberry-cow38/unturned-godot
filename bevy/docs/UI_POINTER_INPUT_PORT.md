# Fast mouse clicks

The native UI now accepts a mouse move, press and release delivered in one
render frame. It keeps Bevy's release activation semantics: an uncaptured press
on one control followed by release over another does not activate either.

The failure was in pinned Bevy 0.20.0 picking. Press targeted the current
HoverMap, while Click/Release targeted PreviousHoverMap. That rule accommodates
touch pointers, which disappear when released, but loses a newly hovered mouse
click and can activate the old button when moving away. ButtonPlugin's queued
Pressed insertion was not the cause: direct Press/Click/Release in one command
flush already activates correctly.

`bevy/vendor/bevy_picking` contains the published pinned crate with original
MIT/Apache licenses and recorded checksum. Only the mouse release selection,
retention of automatically released captures, and resource initialization are
changed; see its PATCHES.md. Touch/custom pointers retain previous-hit release.
Explicit mouse capture retains its original target until dispatch consumes it
once. Hover/Out timing, slider DragEnd cleanup, disabled controls and keyboard
activation remain upstream behavior. Hit testing still uses the final pointer
position for the frame; this does not add per-event geometric queries for
multiple intermediate cursor positions. There is no game-state mutation in this
patch; Equip and Respawn continue to send existing accepted-authority intents.

Validation:

- Original crate: real dispatcher fresh-hover click and move-off release tests
  both fail, while existing-hover, touch and direct queued button cases pass.
- Initial mouse correction: 11 pass, captured-release compatibility test fails.
- Final patch: 12 focused tests pass, covering capture/cancel/slider, two windows,
  disabled buttons and production inventory Select, Equip and Respawn observers.
- All 22 upstream picking library tests pass.
- The complete native UI filter passes 101 tests (10 GPU fixtures ignored)
  against the immutable crafting-bus-v70 authority candidate.

The real X11/Vulkan fixture passes with xdotool's 12 ms press/release during
500 ms frame pauses. Its JSON records movement+press+release together at frames
8, 10 and 12, three activations, selected cell (2,3,4), one exact equipAndHold
intent and one respawn intent. The final PNG was opened and checked. Artifacts:
`bevy/artifacts/bevy-fast-buttons.{png,json}`. The fixture uses the production
UI observer and ordinary ButtonPlugin; Equip/Respawn results are emitted
intents, not a replacement authority session. Source review passed independently.
Immutable test executable: `.local/native-bifold-accepted-first` (shared link;
UI input scope only).

Reproduction:

```sh
cargo test --manifest-path bevy/Cargo.toml ui::button_input_tests -- --test-threads=1
cargo test --manifest-path bevy/Cargo.toml -p bevy_picking --lib
# On a Vulkan/X11 display, run the ignored os_twelve_ms_clicks_at_two_fps_select_equip_and_respawn test.
# UG_BUTTON_INPUT_SHOT sets the PNG path; a JSON sidecar records actual input frames and intents.
```
