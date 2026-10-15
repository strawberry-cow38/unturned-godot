# Original InventoryUI port

The current checkpoint adds the managed read model and two owner-instance
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

## Validation and remaining integration

The frozen `inventory-view-v70-second` candidate passes five actual UDP inventory
regressions and all six current CraftingPresentation tests. These cover owner
privacy/read-only fluid presentation, cooker lifecycle, stale same-ID replacement,
foreign token, wrong item type, dead owner, exact empty replication, and selecting
a worn duplicate for salvage while the pristine copy remains. The repeated stale
salvage request cannot spend the other copy or enqueue another job. The existing
source craft timer determines when outputs arrive. Sixteen focused core wire,
command-table and crafting tests pass; the complete core suite passes 712 tests.
The full managed bridge suite passes 159 tests, including existing crafting,
vehicle, network, save and finite-data consumers. All 128 JSONL protocol tests pass against the same frozen candidate.

Source InventoryUI offers Empty for fuel cans in viewed storage and Nearby too.
Command79 intentionally admits owned pages only; viewed-container/ground Empty
still needs exact live container/item identity plus view/reach authorization.
The read model retains the source option, but the upcoming native context must
not present that unsupported request as successful. Nearby crafting-station tags
also remain the separate authority integration task; this read model currently
uses the existing empty-station policy. No new craft ability or station bypass is
introduced. Native drag/split/hold-transfer/context/quick-craft, hotbar bindings,
timed magazine loading, paperdoll lighting and complete dashboard renders remain
pending until their own tests and actual UI journeys pass.
