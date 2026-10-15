# Original crafting menu and accepted queue

The replacement uses the current `game/inventory/CraftingMenu.cs`, including
its category grid, typed search, item lookup, detail table and quantity controls.
`craftingView` is a read-only projection of the accepted owner's inventory,
skills and blueprint knowledge. Crafting/cancellation use the existing
`SendCraft`/`SendCraftCancel` commands; reading or selecting a tile spends nothing.

The managed extractor retains the original category order/type policy,
output/owner title resolution, ingredient/tool lookup, output lookup, search
matching, learning instructions and first-blocker order. Only method visibility
and instance dependencies become explicit parameters. Browsing starts from
`BlueprintRegistry.Index()`, while every tile retains its index into the wire's
full catalog. Recolors and salvage stay outside All. Browsing and bag lookup
hide unknown recipes and unheld salvage; a typed search can reveal both.
Search matches output title, ingredient names and skill names, ignoring case.

Details count only carried pages, clamp quantity against consumed ingredients,
and require tools once. The source table's visual quirk is preserved: tool rows
use `Amount*quantity` for their sufficient-color comparison while displaying
`-` in TOTAL; the craft gate still requires the tool once. Broken tools also
obey the existing authoritative `Crafting.CanCraft` admission rule.

The native menu uses the original icons, category/grid/detail/queue arrangement,
shared four-tab navbar, frost, Swoop and vitals exclusion. It adapts the authored
2560-wide sizes to the native viewport, with independently clipped scrolling.
Rebound U/R lookup uses the hovered item when empty-handed; R/Space rotates a
carried placement through all four source orientations. Double-click crafts one
eligible recipe, retaining its identity across widget replacement; the current
desktop implementation uses the original X11 backend's
[400ms/5px recognition](https://github.com/godotengine/godot/blob/4.6-stable/platform/linuxbsd/x11/display_server_x11.cpp#L4882).
Queue slot0 is active and draws rightmost. Only accepted owner queue events
populate tiles; progress interpolates from simulation ticks, so singleplayer
pause does not advance it. Cancel uses the displayed server slot and waits for
accepted removal/refund. The existing wire exposes at most eight queue entries.

Nearby station access now uses an owner-only probe and fresh server validation
for each craft, with source ranges and world-layer visibility. Legacy Craft16
retains its no-station admission. See [CRAFTING_STATIONS.md](CRAFTING_STATIONS.md)
for protocol72, tests and the inspected first-person playable UI journey.

Six managed presentation tests pass: original catalog/title policies,
browse/index/category restrictions, typed unknown/salvage visibility, lookup,
quantity, output counts, reopen/selection retention and carried-versus-viewed
storage counts. A real two-process UDP test passes remote browsing, accepted
queue privacy, ingredient escrow, cancel and refund. The earlier bridge without
`craftingView` fails that test at its first query.

Seventeen focused native crafting/search tests pass, plus the actual system
clipboard test under X11 and an additional real-picker same-frame click test.
The combined UI run passes 129 tests. The production Vulkan fixture passes all
eight stages: catalog/detail, quantity and accepted queue, cancellation/refund,
search, 1024x768 resize and close to zero roots. All eight
`bevy-crafting-0.png` through `-7.png` captures were opened. The original
`godot-crafting-reference.png` and isolated original padlock geometry were also
rendered and opened. The fixture exercises production widgets with an integrated
bridge; it does not substitute for OS input checks.

A full playable PEI journey uses real X11 mouse/keyboard input: open Crafting,
search Birch, first Escape stops editing, second closes, reopen retains the
search, accept console-granted logs/saw, choose quantity two, queue both jobs,
cancel one and then the other, and receive both logs back. Captures
`bevy-crafting-pei-*.png/json` record accepted inventory/queue ticks and matching
production UI. Early captures can lag the accepted queue until the next
read-model poll; the later event tick confirms the fast cancellation succeeded.
Skills can be opened from that game, learn Heavy Lifter for 20 XP and return to
the retained crafting search. The relevant captures were opened and inspected.

The playable check also exposed an engine focused-keyboard modifier batching
bug: a 12ms Ctrl+A chord consumed in one slow frame inserted `a` after Ctrl had
already been released. Its failing-first regressions and narrow engine fix are
tracked separately in `vendor/bevy_input_focus/PATCHES.md`. The fixed
engine passes three actual-dispatch regressions, forty upstream tests and a
four-stage real X11 12ms-chord fixture at 2 FPS. All four PNGs were opened.
The full native suite passes 637 tests with 33 renderer/OS tests ignored.
A newly linked playable PEI run also verifies fast Ctrl+A selecting Birch and
typing Maple replacing it; both `bevy-crafting-pei-patched-fast-*.png/json`
captures were opened.

Search supports the original caret, Unicode selection, IME, normal clipboard
shortcuts, control-character paste filtering and deferred undo/redo history.
The field is retained through queue updates and resize. Its original edit popup
now offers Cut, Copy, Paste, Select All, Clear, Undo and Redo, preserving the same
field, selection and pending edits while the popup owns focus. Source labels,
enabled states, font, colors, keyboard navigation and Menu-key entry are used.
Linux primary selection remains separate from the ordinary clipboard: mouse
selection publishes it, and middle-click strips source ASCII escapes and inserts
at the clicked caret without replacing the old selection. Selected text can be
moved or Ctrl-copied within the field; failed/cancelled drops preserve the source.
Double-click selects words and triple-click selects all with the source caret.

The shared `ui::input_order` reducer records each window's event-time logical and
physical keys, mouse buttons and cursor position. The search consumes its ordered
mouse events; touch and custom pointers keep the normal picking path. A same-frame
Ctrl/press/move/release/Ctrl-up test proves drop modifiers survive the final empty
held state. Focus loss and popup ownership cancel a drag without deleting its text;
a popup-open/release/dismiss/move regression prevents stale capture revival. Independent
consumers such as Inventory read the same immutable batch.

The isolated real X11/Vulkan journey uses right-click, popup keyboard Copy,
middle paste, double-click, selected-text drag and Ctrl+Z. Its seven assertions
retain exactly one field entity and update Browse from the actual editor. Four
`bevy-search-pointer-{popup,primary,dragged,undo}.png` captures were opened. Separate
actual X11 tests cover primary-versus-normal clipboard isolation and source
control-character filtering; the existing three keyboard dispatch regressions
still pass with the original-target guard preventing duplicate bubbled Undo.

Text-writing-direction, control-character display/insertion submenus, cross-field
or external text drops, and Windows OS click recognition remain unverified or
pending. Arbitrarily interleaved keyboard edits and pointer operations within one
OS batch are not claimed to have a unified edit dispatch order. Target-item
operations and complete deployable/editor integration remain separate work;
this is not a complete crafting parity claim.
