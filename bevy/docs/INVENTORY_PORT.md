# Original InventoryUI port

The current checkpoint adds the managed read model and owner-instance
transactions. The authored native dashboard, paperdoll and interaction modules
are being integrated separately; this document does not yet claim a complete
inventory UI or visual validation of the new dashboard.

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

## Validation and remaining integration

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

Source InventoryUI offers Empty for fuel cans in viewed storage and Nearby too.
Command79 intentionally admits owned pages only; viewed-container/ground Empty
still needs exact live container/item identity plus view/reach authorization.
The read model retains the source option, but the upcoming native context must
not present that unsupported request as successful. Nearby crafting-station tags
also remain the separate authority integration task; this read model currently
uses the existing empty-station policy. No new craft ability or station bypass is
introduced. Native drag/split/hold-transfer/context/quick-craft, hotbar bindings,
timed magazine UI integration, paperdoll lighting and complete dashboard renders remain
pending until their own tests and actual UI journeys pass.

The exact81 adapter also currently admits only owned magazine/round instances.
Original InventoryUI can address viewed pages; those operations need a separate
validated view/ground identity seam before native UI can enable them safely.
This limitation does not broaden legacy39 or claim the complete dashboard done.
