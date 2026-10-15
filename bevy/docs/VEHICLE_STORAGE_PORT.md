# Vehicle trunk and cabin storage

Vehicle storage uses the existing authoritative `OpenVehicleStorage` command.
F on the source Trunk access zone requests the trunk; opening Inventory while
seated requests the cabin. Neither request creates a local container. The server
validates trunk reach or accepted cabin occupancy, creates the original lazy
crate, and supplies the owner's inventory pages and labels. The existing crate
transactions own item moves and update other viewers.

`StorageView` retains accepted `StorageOpened`/`StorageClosed` facts as a small
owner-only read model: revision, current crate ID, label and explicit compartment
page/width/height/label fields. Acknowledging one-shot events does not erase this
state. A fresh client/session starts closed. No protocol ID or encoding changes
are needed.

The native dashboard follows `OpenInventory` and `OnReplicatedStorageOpened`:
it opens immediately for the cabin request, closes a late cabin acceptance if
the dashboard has already closed, and sends CloseStorage when leaving Inventory.
An accepted close clears the container column without closing Inventory. Every
fresh Inventory opening may request the cabin again even while an earlier
request is pending. A real UDP regression reproduces the otherwise stranded
open-close-reopen sequence with no intervening network pump.

The container column preserves `InventoryUI.ContainerColumnPages`: Freezer,
the accepted main storage label, compartments10–21 under their accepted labels,
then Nearby. Empty zero-sized grids are skipped, while Nearby retains its
header. Own pages0–6 and container pages scroll independently, and scroll state
survives ordinary widget rebuilding. Source trunk6×4, glovebox3×2 and seat
pockets4×2 (at most12) come from the existing fleet registration. The column
uses the native dashboard's current40px cell size and viewport layout; this
checkpoint does not claim the wider original InventoryUI paperdoll/layout port.

The persistence audit found a source save omission: temporary owner view pages
were correctly excluded, but the vehicle's lazy crate contents were never saved.
VehicleSave now captures already-created trunk, glovebox and pocket grids under
the existing stable vehicle ordinal/type check. It does not allocate crates for
unopened cars or persist transient crate IDs. Restore uses the original page
serializer and item reconstruction, creates only saved containers, and remains
idempotent. Older saves with absent fields remain empty. This is an explicit
repair of the save omission, not a claim that the old source saved those items.

Validation uses immutable `.local/vehicle-storage-v69-first`: all134 managed
bridge tests, nine core vehicle-storage tests and45 base JSONL tests pass. Two
new managed cases exercise real UDP cabin refusal/acceptance, label lifetime,
owner privacy, exit cleanup and a bridge save/load/reopen with exact rifle ammo.
The core save regression additionally covers the last seat pocket, changed crate
IDs, unopened cars and repeated restore. Four native cases cover source page
ordering, actual F-to-UDP trunk acceptance, late cabin replies, occupied exit,
and the no-pump close/reopen race; the old pending guard fails that last case.

The production bus-cabin Vulkan fixture also passes. It renders accepted cabin
state through the actual inventory builder and checks all named pockets plus
independent scrolling. Both `bevy-bus-cabin-top.png` and
`bevy-bus-cabin-bottom.png` were opened and independently reviewed:
Glovebox/Driver's seat and the last
Row5 pockets/Nearby remain readable while the left Hands grid stays fixed.
This is a production-panel render with accepted state, not an assertion of a
complete bus-driving journey. Bifold doors and their manual/automatic movement
remain a separate vehicle subsystem.
