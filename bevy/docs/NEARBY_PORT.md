# Nearby owner inventory

The hybrid client now has a dedicated accepted Nearby projection. Public world-item snapshots still carry their original lightweight fields; they are not treated as an inventory or used to invent attachment, cartridge, fluid or instance state.

## Source and authority

`PlayerController.ScanNearbyItems` reads the actual active camera, tests distance strictly below four metres, and scans the world-item scene group in insertion order. `WorldItem.HasLineOfSightFrom` accepts any clear ray to the model hitbox centre or eight padded corners using source world bit 0. `Items.tryAddItem` supplies the original 6×6 packing, stacking and currency normalization. The bridge resolves current original item-model bounds and the existing native dropped/stuck pose. This includes native look-to's finite collinear fallback. Native ordinary dropped yaw remains its pre-existing deterministic net-ID pose, rather than Godot's random tumble.

The server applies those operations to private clones. Each displayed row retains every contributing live world ID, original item ID, and exact instance token. Opening the dashboard never mutates loot. Scene insertion order is tracked independently of net IDs. Only contributions that fitted the opening scan are retained; refresh repacks their remaining admitted amounts and current accepted fields. Newly spawned items require reopening. This refresh/repacking policy is an adapter choice, not a claim of identical source cell retention during concurrent changes.

Every Take or Store rechecks the live owner, accepted camera envelope, strict radius, current LOS, scan/revision/root cell, complete original item bytes, and all contributor tokens before the original destination add operation. Store also requires the owner's current crate, viewer membership, current access/reach, exact destination page and backing dimensions. Only the amount actually accepted is debited from the original world sources; partial currency stacks and change conserve value. Public world-item change/removal and all affected container viewers are refreshed. Server AREA remains empty.

## Protocol 76

Append-only Command 86 carries Open, Close or Transfer with an owner request ID, bounded eye and exact scan/row/world identity. Event 63 is owner-only and carries a revision, accepted tick, result, and bounded full-item rows with contribution identities. It reuses the jar codec and explicitly appends its missing/raw fields, including nullable cartridge strings and preserved/fuel/fluid values. Existing System 7/System 8 and legacy command encodings are unchanged. Published version 75 peers are rejected by the version handshake.

Rows are limited to 36 and total contributions to 4096; strings are bounded before allocation. Decode is atomic. Duplicate/ambiguous identities, overlap, malformed dimensions, invalid numbers and truncated packets are rejected. Exact request replay cannot debit twice; disconnect discards scans and replay state. Legacy grid commands cannot target AREA.

## Native scope

The production Inventory controller requests Nearby once per opening from the completed camera, draws only the received rows, and dispatches exact accepted addresses. Context Take, context Store and Ctrl-grab are supported. Ctrl-grab always targets the owner's inventory; Store uses the currently accepted source container page. No optimistic inventory edits or automatic weapon equip occur.

AREA drag placement, splitting, equipment/drop, magazine operations, Empty and Salvage remain explicit later work. The context exposes only the supported transfer and Close actions. Those missing operations are not routed through synthetic grid cells or substituted owned copies.

## Verification

Focused coverage includes nine core tests, six actual-UDP/geometry bridge tests and three native receiver/gesture tests. Tests cover every Item field, wire truncation/bounds, insertion order, strict radius and nine-sample LOS, vertical/invalid stuck directions, stale/replayed/foreign identities, death/close/disconnect, other-owner removal, all-viewer Store refresh, huge currency overflow/conservation and legacy AREA refusal.

The immutable `bevy/.local/nearby-v76-final` candidate was built from `/tmp/unturned-nearby-v76-source`: base `0567ae87` plus the 16 scoped files listed in `NEARBY_FREEZE.json`. Full checks pass **746 core, 244 managed and 128 JSONL tests**. This isolated source excludes the later building-opening preview and ongoing ports/furniture work. Its SHA-256 hashes are:

- `UnturnedBevy.Bridge.dll`: `cc1a92814a3221f28a12926ef62edf26b8b338010cbbce81112225e3b31dd2fb`
- `UnturnedNet.dll`: `cf257a18d991ce5861f3649b0c4d490d2269fa1e7399eadebd6f677da5f90fa3`

Four surgical controls each fail the new regressions: omitting owner event registration, skipping transfer-time LOS, retaining the debited world source, or omitting the other-viewer repaint. The final native Inventory suite passes **47 tests** (6 opt-in cases ignored) on `native-coupling-pei-first` with the immutable final DLL; this includes the actual receiver Ctrl-grab increment and current-container Store tests.

The actual-UDP Vulkan fixture passes on `native-coupling-schedule-ccd` with `nearby-v76-third`, whose Nearby production source is the same reviewed boundary but also includes then-current building-opening adapters. All four `bevy/artifacts/bevy-nearby-inventory-{0,1,2,3}.png` images were opened and independently reviewed; the combined JSON sidecar records the accepted values. They show full received ground items, the source Take menu, accepted 23-round transfer to Hands with the remaining rifle repacked, and accepted rifle Store in Trunk with Nearby empty. The fixture uses the production Inventory renderer, portrait and vitals, a checker backdrop and fixed camera eye `[0,1.75,0]`. This is an isolated UDP/render proof, not a whole-game OS-input claim.
