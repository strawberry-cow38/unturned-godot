# Inventory numeric split control

The split amount remains local preview state. Typing, wheel/arrow changes and
captured dragging never issue a gameplay command or change accepted items. The
existing preview drop sends the validated SplitItem intent.

## Original expression behavior

The evaluator follows Godot 4.6 `Expression`, `VariantUtilityFunctions` and
`Range` for scalar arithmetic, comparisons/logical/bitwise operators, numeric
constants/conversions, arrays/indexing and the implemented deterministic math
functions. The original reducer is left associative even for exponentiation;
integer division stays integer, while float results snap relative to minimum 1
and clamp to the accepted stack limit. Semicolon function arguments work.
Malformed drafts retain the prior amount. Original execution-error behavior is
also retained: a root division error converts to minimum 1, while that error
inside a larger expression leaves the prior amount untouched.

This is a bounded local numeric evaluator, not an embedded Godot runtime.
Arbitrary engine object methods, vector/dictionary constructors, random/general
engine utilities and locale-specific numeral conversion are not implemented.
The scalar utility table is in `expression/functions.rs`; additional Godot
math utilities beyond that table remain explicit work. Unsupported input keeps
the previous preview amount. Limits are 4096 bytes, 64 nested expressions and
bounded array/operator lists. No value from this parser is trusted by authority.

`tools/inventory_spin_reference.gd` runs the actual original SpinBox, not a
second hand-written expression oracle. `expression/cases.json` and its 241
recorded results cover types, precedence, functions, malformed drafts, bounds,
nonfinite values and nested execution failure. Regenerate with the original
Godot 4.6 binary using `--headless --script` and absolute environment paths
`UG_SPIN_CASES`/`UG_SPIN_OUTPUT`. This is a numeric test, not a render claim.
Godot attribution is in `licenses/GODOT_EXPRESSION.txt`.

## Captured dragging

An arrow press changes the value once and starts the original 600 ms repeat
wait / 75 ms repeats. Moving farther than 2 logical pixels enters capture with the
post-arrow value as its base; that threshold motion does not change the amount.
Further vertical deltas accumulate and apply the original
`-0.01 * abs(total_y)^1.8 * sign(total_y)` response before Range snapping.
Repeat stops while dragging. Release restores the press position; closing or
losing window focus retires the widget's cursor lease.

The shared immutable InputOrder batch retains global raw MouseMotion separately
from window-specific cursor movement. Before Winit applies capture, ordered
cursor deltas drive the widget; afterward only raw motion does. This avoids
counting a cursor/raw pair or cursor warp twice. Physical raw deltas are divided
by the current window scale factor once. The post-Update lease respects the
normal player mouse mode when returning to gameplay.

`tools/inventory_spin_drag_reference.gd` sends original GUI events to an actual
Godot SpinBox and records its seven numeric values. Native tests compare those
values and exercise coalesced events, timer suppression, warp rejection,
focus/close restoration and the real owner controller. The actual OS/Vulkan
fixture additionally exercises typing, capture/release and an accepted split.

## Verification

The immutable `native-inventory-spin-final-verified` executable with
`inventory-preview-v73-first` passes all 169 UI CPU tests, including 41 Inventory
tests (19 opt-in UI cases are ignored). The source oracle matches all 241 scalar
expressions and seven original accelerated-drag values. The actual owner
controller proves that 2× physical mouse deltas become logical deltas once,
without an inventory change or emitted intent. Focus-loss, close, capture
restoration and timer behavior have regressions. Independent source review passed.

The actual X11/Winit/Vulkan fixture passes in 11.8 seconds using identical
production code frozen as `native-inventory-spin-complete`. It clicks the real
field, types `100/2+1` and presses Enter, obtaining 51; an arrow press and captured
vertical drag produce 54. Release restores the cursor. Dragging the preview into
an empty cell sends exactly one SplitItem, and the accepted wallet changes from
150 to 96 + 54. The first three stages retain the accepted 150. All four
`bevy-inventory-spin-os-{0,1,2,3}.png` images were opened; the JSON sidecar records
accepted state, preview amounts and sent intents.

The OS run caught a missing TextInput focus marker: clicking the field left it
unfocused, allowing typed digits to reach Inventory hotbar binding. The field
now participates in Bevy pointer focus, with an actual AcquireFocus regression.
Enter and focus-loss apply earlier queued Bevy text edits before evaluation;
same-frame typing and Enter commit the current draft. Escape discards it. The
single-line field remains stable after deferred cursor edits are applied.
