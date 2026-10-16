# Store shelves port

Sources:
- `game/inventory/StoreShelf.cs`:
  - `ApplyDisplay`/`SyncDisplay`: the stable cell → slot diff;
  - `Profiles`, `SolidDefault`, `Prof`, `GridDims`, `StoodAabb`;
  - `PoseOf` (`content/items/item_poses.json`), `NearestAxis`, `_forceLie` and `PlaceItem`;
  - `AddFocus`/`ShelfItemBody`: the look hitbox and the name tag;
  - `SpawnDouble`/`ResolveSide`.
- `game/StorageReplicaView.cs`: materializes one `StoreShelf` per replicated container (kind → mesh, display, label) and re-applies the digest when it changes.
- `game/ContainerNetSync.cs`: server registration, the loot roll and `ProjectDisplay` (Cell = y*Width+x, display kinds only), re-projected at 2 Hz.
- `game/PlayerController.cs`:
  - `UpdateLookFocus`/`ResolveFocus`: 2.6 m ray, 0.16 m sphere, shelf item over shelf;
  - `TryPickup` → `RequestTakeFromStorage`;
  - the F chain (`_focusShelfItem` with item pickup, above vehicles and crop harvest).
- `core/UnturnedNet/ServerTransactions.cs` (command 45) and `InventoryReplication.ServerTakeFromStorage`.

## Authority

Contents were already server-owned. Command 45 (`TakeFromStorageCommand`) is validated in core, and the source checks were verified there:
- the sender must have an inventory;
- the crate must exist;
- `CanAccess`, i.e. `StorageReach` 4 m from the crate's registered position;
- the item's grid anchor must be exactly (x, y), so a stale or empty cell never takes the neighbour;
- add first, remove only on success, so a full bag leaves the item on the shelf;
- the crate is never opened, and other viewers are repainted.

The bridge already exposed `containers[]` rows with the display digest. The native `takeFromStorage` action reaches `NetWorldClient.SendTakeFromStorage` through the reflected `Send*` table on host and joined clients alike. The only bridge change is that the host `diagnostics` op now reports `shelfTakes {applied, rejected}`, copied from `ServerTransactionsDiagnostics`, so tests can see the server's verdict. No wire ids or protocol bytes changed.

## Native

`bevy/src/entities/shelves.rs` and `shelves/layout.rs`:
- **Catalogue:** reads `ContainerSchema.All` into `ItemCatalog.containers` and the item footprints (`width`/`height`) into `ItemVisual`.
- **Display (`sync`):** one shelf root per replicated display container, at its position and yaw (the replica path's yaw-only basis).
  - Each digest cell becomes the item's dropped model (`items::Models`, the native `BuildReplicaVisual`), so translucent items get the alpha-depth companions.
  - The stable slot diff is kept: a key that stays keeps its model, a gone key despawns it, a new one is placed. A repeated cell keeps the LAST item, as `_syncCurrent[key]` does. Unknown items (`makeLoot` null), cells past the slots, solid kinds and a missing shelf mesh show nothing.
  - Cost: the pass runs only when the snapshot or the catalogue changed. A container's digest is re-applied only when its signature changes (`StorageReplicaView.DisplaySig`); a catalogue change re-applies every digest. Scratch buffers are reused.
  - Models cull at `StorageCrate.RenderDist` 80 m.
- **Layout:** the shelf `.obj` stood up +270° X gives the box. Tier = mirrored grid row. A wide item spans its rotation-adjusted footprint, clamped to the end.
  - Orientation: stand by the icon pose, snapped to axes (up → +Y, front → +Z the aisle, a diagonal up stands on the longest axis), or lie flat with the icon front up.
  - The axis sort copies .NET `Array.Sort` on three keys, which is not stable: SwapIfGreater on (0,1), (0,2), (1,2). Equal dims can come out reversed, which decides how a diagonal-up item stands. The Loonie/Toonie Supply (1056/1057, 0.126 × 0.126 × 0.0525, icon up (0.7071, 0.7071, 0)) stands on X, not Y.
  - Lie rules: flat slabs (unless round), clothing (HAT..GLASSES), `_forceLie`, magazines and "Sandwich" lie; drinks always stand. The determinant fix keeps the up/front (stand) or up/width (lie) axes.
  - Scale: down to 90% of the slot width and the 0.24 tier gap. Position: base on the tier + 3% + 0.05, slot-centred, front-biased (0.76 depth, kept 0.34 off the back wall and inside the 0.96 lip).
- **Look (`offer`):** the 2.6 m eye ray, stopped by the world, claims the nearest item box (mesh AABB × 1.1). Failing that, the 0.16 m sphere at the ray end refines to the overlapping item whose origin is nearest, but only under the arbitration rule below.
  - Cost: only shelves whose root is within reach + 3 m are considered. Each item matrix is inverted once, the tag point is computed only for the winner, and nothing is allocated per frame.
  - It is offered as an ordinary candidate with no HUD prompt, so it beats the shelf's `openStorage` offer behind it. `fall_through` (crop harvest) never overrides it, and F dispatches `takeFromStorage {netId,x,y}`.
  - It is skipped while UI captures input, while seated or in a vehicle, and while dead; Alt-look is gated in `apply_interaction`.
  - F is only the intent: the model leaves when the accepted digest drops the cell, and the item arrives through the owner echo.
- **Name tag (`tag`):** one shared surface for the focused item, shown only while the look winner is still that item.
  - Label3D values: the item name, the rarity colour lerped 35% to white, 64 px font, outline 10 (2.5 px stroke, opaque black), 0.006 m per pixel, camera-facing, unshaded, depth-ignoring (the InfoBillboard material).
  - Anchor: world-up from the model origin by its oriented height + 0.12.

## Deviations and gaps

- **Focus outline:** `OutlineOverlay` (the rarity rim on the item, the white whole-shelf rim) has no native renderer yet. It is the separate "Focus outlines" ledger row; only the name tag is ported.
- **Back-to-back gondolas (`SpawnDouble`/`ResolveSide`): not ported, matching what the shipped game does.**
  - The source game consumes containers through `StorageReplicaView`, which spawns one single-sided `StoreShelf` per replicated container. `ContainerNetSync` registers one container per manifest row.
  - `SpawnDouble` only runs on the SP-direct path (`SpawnMapContainers` when not consuming, and `SpawnEditorStoreShelves`, whose `editor_PEI_shelves.txt` does not exist).
  - A stocked back aisle needs a server-side twin registration first.
- **Stale model on an in-place swap:** as in the source, a cell whose key survives keeps its model even if a different item now occupies it in the same digest step.
- **Arbitration (`ResolveFocus`, `rayTerminal`):** in the source, a ray claim on anything except a shelf is terminal. The sphere may refine to a shelf item only when the ray claimed nothing or claimed a shelf.
  - Natively, the sphere refines only when the eye ray hit nothing, or hit a container world object (a `WorldObject` whose services row carries a `containerNetId`). Any other hit (seat, door, deployable, NPC, vehicle) suppresses it, so a target the ray hit first beats a sphere-found shelf item.
  - Any container counts, not only the item's own shelf. The source's `Look.Shelf` is any `StoreShelf`, and `ResolveFocus` then takes the nearest sphere-found shelf item from any shelf.
  - It is narrower than the source in one way. The source also refines after hitting plain unclaimable geometry (a wall, terrain, a vehicle collider), but native colliders cannot be classified as claimable or not, so those hits suppress the refinement. A ray that hits the item box itself still claims it either way.
- **Ordering among offers:** the native `InteractionFocus` is distance-ordered.
  - The source ranks a shelf item above a vehicle or dropped item caught by the same sphere. Natively the nearer offer wins.
  - Native dropped items and storage still use their pre-existing 6 m look ray. Shelf items use the source 2.6 m reach and 0.16 m sphere.
- **Tag rasterization:** the tag ignores the sub-pixel ink-centring offset the nameplate applies (centimetres at this pixel size). The font rasterizer differs from Godot's FreeType at edge pixels.
- **Translucent items:** for the six translucent items, the alpha-depth companion children do not inherit the 80 m `VisibilityRange`.
- **`FlushGunState`:** has no native counterpart. The held gun's state is already server-side in the bridge.

## Verification

- **Native (`entities::shelves`, 16 + 1 ignored):**
  - Unit tests:
    - the .NET three-key sort and the Loonie Supply's stand axis;
    - last-item-wins for a repeated cell;
    - slot stability (a staying cell keeps its entity when a neighbour leaves), the DisplaySig gate, and the catalogue-change re-apply;
    - a real-Rapier arbitration test: a ray-hit seat beats a sphere-refined item; a container hit lets it refine;
    - profiles, grid dims and fallbacks;
    - `NearestAxis` ties and -0;
    - the real `item_poses.json`;
    - the stand/lie rules;
    - the diagonal-up rule;
    - digest decoding (linear index, rotation swap, unknown items);
    - tier mirroring and hidden cells;
    - the tag tint;
    - ray-then-sphere picking with scaled models;
    - the silent ordinary candidate that crop harvest does not override.
  - **Godot golden** (`production_sync_reproduces_the_godot_shelfdemo_layout_item_for_item`): `bevy/tools/shelf_reference.gd` dumped all 25 items placed by the actual Godot 4.6.2 `StoreShelf` in `Main.cs` `UG_SHELFDEMO` (front gondola plus its yaw-180 twin), into `shelves/godot_shelfdemo.json`. The same cells are fed through the production digest → `sync` path. Every item matches within 2 mm: basis (orientation × scale), origin, tag anchor, hitbox, mesh AABB and tag modulate. The catalogue's container dims equal `GridDims` for every kind.
  - **Authority journey** (real bridge, production `install` schedule):
    1. A `worldServices` gondola is stocked by give → open → move to (3,1) → close, and the digest shows cell 11.
    2. One model is placed; looking at it selects `takeFromStorage` with an empty HUD prompt and the tag name.
    3. Teleported 30 m away, F is refused: the bridge `diagnostics` `shelfTakes` counter reads 0 applied / 1 rejected, and nothing moves.
    4. Back in reach, F leaves the model in place until the next tick. The accepted digest then drops the cell, the item is in the bag, and the model is gone.
- **Managed (`ShelfTests`, real UDP, joined client):** the digest replicates to the peer. Each server verdict is checked on the `ShelfTakesApplied`/`ShelfTakesRejected` counters.
  - A take beyond reach is refused, and a stale empty cell takes nothing.
  - The named cell's take moves only that item into the peer's bag and drops only that cell, and the STORAGE page stays closed.
  - With no room in the bag, the take is refused and the item stays on the shelf (add first).
- **Negative controls:** each was reverted alone, failed as intended, and was restored byte-identical. The list is in `PROGRESS.md`.
- **Visual:** the ignored fixture `render_source_shelfdemo_through_the_production_display` (`UG_SHELF_SHOT`, `UG_SHELF_FOCUS`, `UG_SHELF_CAM`) runs the production sync/offer/tag path in the `UG_SHELFDEMO` scene and camera.
  - Default and `top` views: they match the Godot oracle item for item in orientation, placement and scale. Lying items show their detail side up, and the back twin's tops show over the spine. Only light levels differ.
  - Focused Carjack tag: green, at the same screen position and size as the Godot `UG_SHELFLABELS=1` label.
  - Renders: `bevy/artifacts/{godot,bevy}-shelfdemo{,-top}.png`, `godot-shelfdemo-labels.png`, `bevy-shelfdemo-tag-carjack.png`.
