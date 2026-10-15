# Pinned Bevy picking correction

This is the crates.io `bevy_picking` 0.20.0 source, with both original licenses.
Original package checksum:
`47a04fb644c62a57a4f338f5cd8b61519c569139742eafe87dccb523e4b7cf2e`.
The source is also published at
<https://github.com/bevyengine/bevy/tree/v0.20.0/crates/bevy_picking>.
Cargo.toml is the registry-normalized published manifest. The package's unused
Cargo.lock, Cargo.toml.orig and registry bookkeeping files are omitted.

The changes are limited to three source files:

- `src/events.rs`: uncaptured mouse release/click queries the current HoverMap.
  Touch and custom pointers retain the original previous-hover behavior. Using
  previous hover for a mouse loses enter+press+release batched into one slow
  frame and incorrectly activates a departed button on move-off+release.
- `src/hover.rs`: retain captures automatically removed during hover generation
  until release dispatch consumes them once. An explicit capture therefore
  keeps its original release target, without changing hover/Out timing. Cancel
  clears the capture without retaining a click; the temporary map clears each
  frame. The existing public PointerCaptureMap is unchanged.
- `src/lib.rs`: initialize that temporary resource.

Original press matching, event order, drag/drop, multi-click, disabled widgets
and focused-keyboard dispatch are retained. The dispatcher's optional resource
also preserves existing standalone event-system harnesses.

`bevy/src/ui/button_input_tests.rs` drives the actual picker and ButtonPlugin,
including real hover generation for capture and slider tests. Both mouse
failure cases reproduce with the unmodified crate; queued button events alone
work correctly. The initial current-hover correction fails the new captured
release regression, which the final retained-capture correction fixes.
The ignored child fixture uses actual X11/xdotool 12 ms clicks, Winit and the
production UI action observer during 500 ms frame pauses.

Remove this patch when an upgraded upstream version supplies equivalent
behavior; retain the regression suite across that upgrade.
