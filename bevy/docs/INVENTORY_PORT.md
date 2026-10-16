# Original InventoryUI port

The native dashboard now consumes the managed read model and routes original
inventory gestures through accepted transactions. It replaces the temporary
click-select/move panel. The remaining widget and viewed-item limitations are
listed below; this checkpoint does not claim complete InventoryUI parity.

## Native dashboard and interaction

`ui::inventory` follows the original 40px grid, independent left/right scrolling,
equipment slots, worn-item order, context panel, split strip, quick-craft area and
hotbar. Tile facts use original item art and font, rarity/condition, fluid/fuel,
food preservation and money fan policies. Original Godot runtime SpinBox arrows
and slider grabbers are included with their MIT attribution. Centered headings
and right-aligned counts retain the source unwrapped glyph metrics. Tooltips use
the original half-second stationary-hover delay and retained popup anchor.

The isolated portrait uses the original rig, outfit, held weapon and Equip-end
upper-body pose, with original camera framing, room/Sun lighting and Filmic
mapping. Shared avatar helpers also correct the original `leftHook` selection
for bows on remote players. Portrait images preserve transparency and follow the
dashboard's Swoop alpha; the viewport stays alive until the close transition
finishes. This is a presentation view, not another player or physics body.

The shared ordered input stream supplies per-event pointer positions and keys.
Left drag/drop, four orientations with R/Space, right-click selection or one-item
carry after the source six-pixel threshold, Ctrl transfer/drop, H hold-transfer,
hover U/R recipe lookup, hotbar binding, equipment/drop/salvage, split preview
drag and cooker controls retain source intent. No drawn page is mutated before
the server accepts the operation. Magazine timers consume owner-only results
and the corresponding accepted snapshot, retaining request IDs through reopen.
Closing cancels pending equipment/selection and hold-transfer operations; an
already-sent move may finish, but its delayed reply cannot equip or close a newly
opened dashboard.

Numeric split text is retained across accepted refresh, commits on Enter or focus
loss, and never sends an inventory command itself. Arrow steps/repeat, focused
wheel, right-click min/max and the original slider geometry are implemented.
Unlike the original automatic Refresh suppression while carrying or selecting,
the adapter adopts accepted changes while retaining the live editor and gesture
identity. Swoop progress survives those rebuilds; retiring roots have no live
buttons. Scalar expression entry and captured vertical drag are described in
[INVENTORY_NUMBER_PORT.md](INVENTORY_NUMBER_PORT.md).

## Accepted presentation

`inventoryView` reads only the requesting client's accepted inventory, held
selection, known blueprints, skills and container events. It exposes original
item names/descriptions, rarity and condition colors/text, clothing/hand actions,
magazine compatibility, fluid contents, cooking controls and the first eighteen
ready quick-craft recipes in original registry order. Source `ResolveMoveDest`,
`StoreTarget`, `FirstFreeOwnCell`, salvage descriptions, quality color and fluid
labels are extracted from the current Godot code. Fluid lazy initialization runs
on a clone: opening a context menu never mutates a replica.

The `tiles` projection supplies the original MakeTile facts for grid and seven
worn slots: real amount/quality, rarity and condition colors, fuel/fluid bars,
frozen/preserved food, source currency denomination order and the single active
autodrink bottle. ActiveAutoDrink runs on a private owned-page clone in original
page/item order; viewed storage cannot acquire its badge. `acceptedTick` is the
owner client's last applied snapshot tick, wrapped to the same u32 clock used by
completion facts. A client can wait for the echo after an event without editing
its inventory optimistically.

Addresses retain owner instance tokens. Container view cells do not gain invented
owner tokens. Equip suggestions preserve original slot choice, displaced-item
swap, first free owned space and drop fallback. They are suggestions for existing
server transactions, not a second placement authority. An equip sequence must
wait for the moved item to arrive in the accepted owned slot before selecting it.
Cooker kind/on/fuel follows the original StorageOpened/CookerState/StorageClosed
facts and resets at session replacement.

## Protocol70 owner actions

This is part of the coordinated protocol70 wave (skills77, vehicle door78), not
an additional version bump. Existing snapshot/item encoding is unchanged.

| Command | Payload | Authority |
|---|---|---|
|79 EmptyFuel|Owned EquipmentAddress (page/x/y/id) plus nonzero u64 instance token|Alive sender; exact current owned origin/id/token; actual fuel-container asset; set only that item's fuel to zero, then owner replication|
|80 SalvageItem|Same bounded owned address and token; no client recipe or quantity|Alive sender; exact current instance; host-selected original salvage recipe; existing operation/skill/knowledge admission; selected item must be a consumed ingredient|

Both parsers reject malformed/truncated input atomically, including container
addresses, zero IDs and zero tokens. Sender identity comes from the connection.
Salvage uses the existing timed server craft queue and original
`Crafting.PlayerInvAdapter.Prefer` consumption order. It escrows the clicked copy
before other identical items and retains existing tool, output, cancellation and
refund behavior. The new optional queue argument defaults to null; ordinary
Craft callers keep their previous behavior. The bridge captures its item-to-
salvage mapping while its own catalog owns the original static recipe registry,
so creating another bridge cannot silently substitute recipe indices.

## Protocol71 magazine completion

Protocol70 was published before the magazine UI completion adapter. Protocol71
therefore adds Command81 and Event60 and explicitly refuses70 peers. Legacy
Command39 retains its original encoding and admission behavior.

| Message | Payload | Authority |
|---|---|---|
|81 MagazineTransfer|Nonzero u32 request ID; exact owned magazine address/token; load or unload; exact owned round address/token for load, or only the expected loose-round ID for unload|Alive connection owner; current root cell, ID and instance tokens; original MagRules and inventory insertion/removal path|
|60 MagazineTransfer|Owner-only request ID, captured magazine token, accepted tick, bounded result, resulting magazine amount only on success|Never broadcast; refusal amount is zero; no client quantity or outcome assertion|

Malformed/truncated reads publish no partial command or event. Unload still adds
the loose round before decrementing the magazine, so a full bag loses nothing.
The handler uses exactly the same mutation as39; only81 adds instance admission
and an explicit completion. Source UI timers still choose when to ask for each
round; this adds no new timing authority.

The host retains one request/result per connected owner. An identical replay
returns the original result without moving another round; a stale or altered
request ID is refused. Wrap comparison is signed u32 distance, and disconnect
clears the entry. UI request IDs must survive closing/reopening Inventory.

The result distinguishes stale magazine/round, dead owner, non-magazine,
MagRules refusal, full bag and stale request. It solves a demonstrated ambiguity:
legacy39 full-bag unload produces neither a state change nor a completion event.
A client cannot distinguish that refusal from network delay by watching counts.
The native timer consumes accepted facts and waits for the corresponding owner
snapshot before rechecking the next source stack. It does not write ammo counts.

## Validation

The frozen `inventory-magazine-v71-final` candidate passes all 714 core tests,
189 managed bridge tests and 128 JSONL protocol tests. Seventeen focused managed
cases cover the seven InventoryView tests, four magazine UDP cases and six
CraftingPresentation cases. The magazine tests exercise accepted load/unload,
owner-only delivery, unchanged inventory before the request is processed, exact
same-ID replacement/foreign-token/dead-owner refusal, incompatible cartridge,
full-bag refusal without loss, request replay and published protocol70 rejection.
A legacy39 full-bag request is the negative control: no changed count and no
completion event. Core tests cover atomic malformed/truncated command/event
reads, bounded tags, request-counter wrap and disconnect cleanup, alongside all
unchanged legacy magazine regressions.

Tile tests use real UDP fuel/clothing/currency/bottle contents, verify exact
original denomination/worn order, and prove that repeated display reads do not
seed either owner or viewed-container fluid instances. Existing empty/salvage
cases retain owner privacy, cooker lifecycle and the selected worn duplicate's
consumption while the pristine copy survives; the original craft queue still
owns escrow and completion timing.

The native focused suite passes 32 tests, including real UDP move/split and
magazine sequencing, ordered input, retained editor focus, close/reopen delayed
equip, actual dashboard Swoop rebuild/retirement and portrait state/first-frame
transforms. The separate avatar suite covers shared held geometry and the
compound bow's original Left_Hook versus an ordinary rifle's Right_Hook.
The complete UI filter passes 160 tests with 17 opt-in render cases ignored;
the avatar filter passes 33 with one opt-in render ignored. These use immutable
`native-inventory-aligned-final` and `crafting-station-live-v72-final`.

The production Vulkan fixture opens four stages using actual accepted bridge
state: dashboard/portrait, right-click context, drag of the split preview with
accepted 30→27+3, then a magazine unload and its operation wheel. All images were
opened and checked. `bevy-inventory-aligned-{0,1,2,3}.png` and its JSON sidecar
record this journey; it drives the production ordered-input controller, not an
OS mouse. A separate original Godot `--invsel` render is retained as
`godot-inventory-selection-reference.png` for layout/widget comparison. Earlier
portrait renders cover rifle, compound bow and night lighting. Full-game OS
inventory interaction remains separate evidence from these production fixtures.

## Currency preview follow-up

The display-only tile catalog now includes the original largest-first currency
metadata even for an empty owner inventory. Split and carried previews project
their local amount through that table, using the same repeated-bill layout and
`$N` caption as accepted tiles. Refresh replaces only preview children; the
number editor and its draft/focus retain their identity. This never alters an
accepted wallet or adds a new command.

Validation: eight managed InventoryView tests and 33 native Inventory tests
pass against immutable `inventory-preview-v73-first` and
`native-coupling-geometry-first`. A checked-in 0–500 oracle is generated from
original C# `Currency.Breakdown`; actual UDP metadata drives the native comparison
and retained-editor test. The older bridge fails this test on the missing table.
The four-stage Vulkan fixture passes and all `bevy-inventory-money-{0,1,2,3}.png`
images were opened: accepted $150, preview $75 then $51, and the actual accepted
split into $99 and $51. Sidecars verify exactly one SplitItem intent; the preview
stages leave the owner wallet at $150. Independent source review passed.

## Remaining parity

Source InventoryUI offers Empty for fuel cans in viewed storage and Nearby too.
Command79 intentionally admits owned pages only; viewed-container/ground Empty
still needs exact live container/item identity plus view/reach authorization.
The read model retains the source option; native context reports that the item
must first be taken into the owned inventory. Salvage has the same exact-owner
restriction. Nearby crafting stations are now integrated through protocol72's
accepted source-camera probe; quick craft receives only host-accepted tags and
the host rechecks the action. See CRAFTING_PORT.md.

The exact81 adapter also currently admits only owned magazine/round instances.
Original InventoryUI can address viewed pages; those operations need a separate
validated view/ground identity seam before native UI can enable them safely.
This limitation does not broaden legacy39 or claim the complete dashboard done.

The bounded scalar SpinBox evaluator and accelerated capture have source
oracles, native controller tests and an actual OS expression/capture/split
journey. See INVENTORY_NUMBER_PORT.md for its verification boundary.
Arbitrary engine object methods/vector/locale expression behavior remains outside
the scalar adapter.
Rejected legacy hold-transfer requests remain reserved until H is released,
rather than inventing a completion or local inventory change. These are explicit
follow-ups after this reviewed dashboard checkpoint.

At 720p, the original LayoutDash minimum character-panel height of 600 pixels
overrides HUD.ContentBottom. The original layer-12 vitals therefore overlap the
layer-11 Inventory's lower portrait/PRIMARY area. The full-game pickup capture
`bevy-pickup-final-inventory.png` reproduces that source limitation; this parity
checkpoint does not introduce an adaptive-layout redesign.
