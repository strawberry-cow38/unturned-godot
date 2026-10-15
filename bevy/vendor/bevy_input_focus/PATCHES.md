# Pinned Bevy focused keyboard correction

This is the crates.io `bevy_input_focus` 0.20.0 source, with both original licenses.
Original package checksum:
`83c81d369d0ab3ec17ba14bab1e07da7cacf3751a3104a93b1a3cd99d887887b`.
Upstream source: <https://github.com/bevyengine/bevy/tree/v0.20.0/crates/bevy_input_focus>.
The registry-normalized manifest is retained; the unused package Cargo.lock,
Cargo.toml.orig and registry bookkeeping are omitted.

Only `src/lib.rs` changes. `KeyboardInput` carries no modifier snapshot, while
the original focused dispatcher runs after InputSystems has consumed the entire
batch. A quick Ctrl+A whose Ctrl release arrives in the same slow frame is
therefore treated as ordinary A by TextInput and other focused observers.

The keyboard dispatcher records logical and physical keys before InputSystems,
then replays each message during its focused event. It restores the exact final
resources after each event, including pressed/released transition flags. These
temporary assignments bypass change detection. Observer deferred commands still
flush between events. Polled game input, the batch's chosen focus target, custom
message dispatch, pointer, gamepad and mouse-wheel handling are unchanged.

The actual InputPlugin/InputDispatchPlugin/TextInput regression in
`bevy/src/ui/crafting/search/tests/dispatch.rs` reproduces both the fast Ctrl+A
failure and its inverse (a later Ctrl press suppressing earlier ordinary text)
with the original crate. It also checks Shift+caret and the original search undo
observer, and preservation of final physical/logical key states.

Three focused dispatch regressions and all 40 upstream focus tests pass. The
ignored child `dispatch/visual.rs` fixture sends actual X11/xdotool 12 ms chords
through Winit while frames pause for 500 ms. Its six assertions and four opened
Vulkan captures prove Ctrl+A selection, replacement, Shift+Left selection and
Ctrl+Z/Ctrl+Shift+Z undo/redo. The JSON sidecar verifies each complete Ctrl chord
was received in one frame. The combined native UI suite passes 134 tests; the
existing real system clipboard test also passes. The full native suite passes
637 tests, with 33 opt-in fixtures ignored.

Remove this patch once an upstream upgrade provides equivalent event-time
modifier handling; retain the regressions across that upgrade.
