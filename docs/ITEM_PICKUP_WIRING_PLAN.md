# MP Item Pickup + Transactional Inventory — Wiring Plan

Status: PLAN (read-only audit of `main` @ ce6e2ee, 2026-07-17). No code changed.

VoX's framing — "mostly built but needs to be all wired up" — is exactly right. The entire
engine-free core round trip (commands, validation choke point, server transaction handlers, world-item
entities, owner-only inventory replication, denial events, L0 test battery) **already exists and is
green**. What's missing is almost entirely **game-side wiring in `game/`**: nothing calls
`SendPickupItem`, nothing consumes the replicated owner inventory, and the inventory UI still mutates
the client-local SP grid. Plus two genuinely missing pieces: `WorldItemPuppet` carries **no NetId**,
and the server validator has **no look-at/facing check** (strawberry's requirement) — only a 6 m
radius.

---

## 1. Ground truth — what exists today (verified file:line)

### 1.1 The wire layer (complete, tested)

| Piece | Where |
|---|---|
| `PickupItemCommand { uint NetId }` | `core/UnturnedNet/InventoryReplication.cs:52-63` |
| `MoveItemCommand` (page/x/y → page/x/y/rot) | `core/UnturnedNet/InventoryReplication.cs:18-37` |
| `DropItemCommand`, `EquipItemCommand`, `ConsumeCommand`, `OpenStorageCommand`/`CloseStorageCommand` | `core/UnturnedNet/InventoryReplication.cs:39-50, 65-76, 91-102, 104-121` |
| Command ids 12-19 (MoveItem…CloseStorage), append-only | `core/UnturnedNet/PlayerReplication.cs:43-50` |
| Event ids 16-19 (WorldItemSpawned/Settled/Removed, ItemPickupDenied) | `core/UnturnedNet/PlayerReplication.cs:74-77` |
| Client send seams: `SendPickupItem` / `SendMoveItem` / `SendDropItem` / `SendEquipItem` / `SendConsume` / `SendOpenStorage` / `SendCloseStorage` (all ReliableOrdered) | `core/UnturnedNet/NetWorldHost.cs:539-561` |

### 1.2 Server side (complete, tested)

- **Registration + validation at the §2.3 choke point** — `ServerTransactions.Register`,
  `core/UnturnedNet/ServerTransactions.cs:133-191`. The pickup validator
  (`ServerTransactions.cs:147-152`) checks: sender has a position, sender has an inventory, the
  world-item entity exists, and `(e.Pos - pos).magnitude <= PickupReach` (6 m,
  `ServerTransactions.cs:36-37`). **There is no facing/look-at check — that's the strawberry gap
  (Phase 2 below).**
- **`OnPickupItem`** — `ServerTransactions.cs:286-307`. Already does everything else right:
  `inv.tryAddItem(e.ServerItem)` (the real server-held `Item`, gun state intact —
  `WorldItemReplication.cs:109-110`) → on success `RemoveWorldItem(cmd.NetId)`
  (`ServerTransactions.cs:481-487`: idempotent entity removal + `WorldItemRemovedEvent` broadcast);
  on failure it publishes a partially-merged stack amount, bumps `Diag.PickupsDenied`, and sends
  `ItemPickupDeniedEvent` to the requester only.
- **`OnDropItem`** — `ServerTransactions.cs:268-284`: removes the jar from the sender's grid and
  spawns a replicated world item 1.2 m ahead of the avatar's yaw with a toss velocity
  (`SpawnWorldItem`, `ServerTransactions.cs:471-477` — broadcast `WorldItemSpawnedEvent`).
- **`MoveItemCommand` handler** — `ServerTransactions.cs:133-141`: `PlayerInventory.TryDrag` on the
  SERVER grid is both validator and mutation (`core/UnturnedSim/PlayerInventory.cs:106-140` — the
  ported `ReceiveDragItem`/`ReceiveSwapItem` cell math, covering hand slots, moves, and swaps).
  `EquipItemCommand` (`ServerTransactions.cs:154-160`) is the same `TryDrag` into slot pages.
- **Owner-only inventory replication** (SystemId 7) — `InventoryReplication`,
  `core/UnturnedNet/InventoryReplication.cs:188-471`. Every grid mutation flips a dirty bit via the
  model's own `onStateUpdated` (`:228-238`), `ServerCommitDirty` stamps it each tick
  (`NetWorldHost.cs:169`), and the owner gets the FULL 9-page grid + worn clothing whenever dirty
  (`WriteDelta` `:313-317`). Client side, `ReadSnapshot` (`:337-367`) rebuilds the replica **as a new
  `PlayerInventory` instance** (`:365` — important for §4.2 below) and fires
  `ReplicaUpdated(ownerId)` (`:218, :366`).
- **World items as server entities** (SystemId 8) — `WorldItemReplication`,
  `core/UnturnedNet/WorldItemReplication.cs:97-323`: NetId-keyed registry, spawn/settle/remove with
  tombstoned deltas, 128 m interest ring on the dedicated server (`game/DedicatedServer.cs:89`).
  **Answer to "are ground items server-authoritative": yes.** On the dedicated server every ground
  item — LootField loot, player drops, salvage scrap — is bridged into an entity by
  `WorldItemNetSync` at 5 Hz (`game/WorldItemNetSync.cs:32-87`: node→entity minting `:43-52`,
  settled-transform publish `:53-57`, removals reconciled both directions `:60-79` — a remote pickup
  frees the server-side node, a local despawn retires the entity). The joined client mirrors the
  registry and the pickup command addresses that NetId.
- **Server host wiring** — `NetWorldServer` registers `Transactions` on the command registry
  (`core/UnturnedNet/NetWorldHost.cs:70-73`) and `Inventories.ServerAdd` on join (`:99`), before the
  join snapshot composes, so the joiner's owner block rides the join snapshot.

### 1.3 Client core (complete)

`NetWorldClient` applies world-item events idempotently onto the replica and re-surfaces them as C#
events (`NetWorldHost.cs:408-414`), including `ItemPickupDenied` (`:414`), and exposes
`Inventories.ReplicaUpdated` for UI refresh.

### 1.4 The L0 test battery (already green — the server round trip is PROVEN)

`tests/UnturnedNet.Tests/InventoryReplicationTests.cs`:
`drop_then_pickup_roundtrips_through_the_world` (`:88-111`),
`pickup_out_of_reach_rejected_at_choke_point` (`:113-127`),
`pickup_into_a_full_grid_is_denied_but_stays` (`:129-146`),
`legal_move_applies_illegal_move_rejected_grids_converge` (`:44-63`), `equip_to_hand_slot` (`:66-85`),
`console_give_lands_in_the_server_grid_and_replicates` (`:26-41`), crates (`:200-243`), 25 %-loss
convergence (`:245-259`). Harness: `TransactionalHarness` / `TransactionalFixtures`
(`tests/UnturnedNet.Tests/TransactionalTestKit.cs:16, :65 Connected, :81 Grant, :55 StepUntil`).

### 1.5 The game side — where the wiring stops dead

- **SP pickup flow** (the semantics MP must mirror, and does — server-side `tryAddItem` IS the same
  call): F-interact chain `game/PlayerController.cs:1805-1817`; the pickup branch `:1810` →
  `TryPickup()` `:517-528`: `Inventory.tryAddItem(wi.Item)` → `wi.QueueFree()` → `_invUI?.Refresh()`
  → equip-in-hand if unarmed (`:527`).
- **MP focus already works**: `UpdateLookFocus` (`PlayerController.cs:136-218`) eye-ray + look-sphere
  hits the puppet's bit-7 detection body and sets `_focusPuppet` (`:172-178, :212-217`) — the
  outline/name-tag renders. `WorldItemReplicaView` builds focusable `WorldItemPuppet`s
  (`game/WorldItemReplicaView.cs:68-79`, `WorldItem.BuildItemPuppet`
  `game/inventory/WorldItem.cs:135-171`) and **already despawns them** when the server retires the
  entity (diff-driven, `WorldItemReplicaView.cs:57-65`) — the client-side "item vanishes on pickup"
  half is done.
- **But the F chain never sends the command.** `:1810` only handles the SP `_focusItem`
  (`WorldItem` node); `_focusPuppet` is only ever used for the outline. There is no
  `NetPickupItem` seam next to `NetEnterVehicle`/`NetFire`/… (`PlayerController.cs:1147-1158`), and
  `game/ClientWorldSession.cs:220-227` wires vehicles + combat but nothing for items. The view's own
  header says so: "Pickup over the wire is deferred (§6) — these are visible-only"
  (`WorldItemReplicaView.cs:14`).
- **`WorldItemPuppet` has no NetId** (`game/inventory/WorldItem.cs:365-388` — fields are
  glow/label/rarity only), unlike `VehiclePuppet.NetId` (`game/VehiclePuppet.cs:73`). Even with a
  seam, the F chain has nothing to send. *Genuinely missing, not just unwired.*
- **The client inventory model is SP-local in MP.** The MP shell builds its own
  `new PlayerInventory()` + `PopulateDemoInventory()` + `InventoryUI` in `_Ready`
  (`PlayerController.cs:1711-1715`); **nothing anywhere in `game/` subscribes
  `Client.Inventories.ReplicaUpdated`** (verified by grep). So the server's authoritative grid — the
  one that pickup/give/craft actually mutate — is invisible: `DevConsole` "give" already routes over
  the wire (`game/DevConsole.cs:21, :85-92`), lands in the server grid, replicates back… and no UI
  ever shows it.
- **The inventory UI mutates locally everywhere**: drag-drop → `Inv.TryDrag`
  (`game/inventory/InventoryUI.cs:177-187`, the call at `:186`), Drop action → `pg.removeItem` + a
  client-local `Player.DropWorldItem` spawn (`:357-372`; `DropWorldItem` spawns a real SP `WorldItem`
  node, `PlayerController.cs:107-117` — on a joined client that item exists only for you and can
  never be picked up over the wire), equip-holster → `Inv.TryDrag` (`:299-301`). In MP all of this
  desyncs from the server grid the moment it's used.
- **Loopback is fine as-is**: `MpLoopback` keeps the local player on the direct SP paths by design
  (`game/MpLoopback.cs:10-14, :42-43`) and `WorldItemNetSync` reconciles both pickup directions
  (`WorldItemNetSync.cs:60-79`). None of the seams below get wired there — only
  `ClientWorldSession` wires them, which is the established pattern (`PlayerController.cs:1150-1152`:
  "wired ONLY by ClientWorldSession, null in SP/loopback").

### 1.6 The pattern to mirror (vehicle enter, C6)

Client request → server validate → server apply → broadcast fact → client reflects:
`RequestEnterNearestPuppet` (`PlayerController.cs:1177-1183`) → `NetEnterVehicle` seam (`:1147`) →
`Client.SendEnterVehicle` (`ClientWorldSession.cs:220`) → `ServerVehicles.CanEnter` validator
(`core/UnturnedNet/VehicleReplication.cs:493-501`, reach `EnterReach = 6f` `:451`) → `ServerEnter`
(`:503-511`) → `VehicleEnteredEvent` → `ClientWorldSession` latches + `EnterPuppet`
(`ClientWorldSession.cs:88, :164-170`). "The client never seats itself" (`ClientWorldSession.cs:86-87`)
— pickup follows the identical shape: **the client never pockets the item itself.**

---

## 2. Ordered work plan

Ranked per VoX: pickup first, then transactional inventory. Each step lists exact files/lines,
what's new vs existing, and its tests. Steps 1-4 = pickup visible end-to-end; step 5 = strawberry's
validation; steps 6-7 = inventory management.

### Step 1 — Give `WorldItemPuppet` its NetId  *(genuinely missing)*

- `game/inventory/WorldItem.cs` (`WorldItemPuppet`, `:365-388`): add `public uint NetId;` —
  mirroring `VehiclePuppet.NetId` (`game/VehiclePuppet.cs:73`).
- `game/WorldItemReplicaView.cs:46-52` (`BuildReplica` call site) — set `node.NetId = e.NetIdValue`
  when the puppet is built (`BuildReplica` `:68-79` takes the entity; pass the id through or set it
  at the call site alongside `_nodes[e.NetIdValue] = node`).

### Step 2 — The `NetPickupItem` seam + F-chain branch in `PlayerController`

- `game/PlayerController.cs:1147-1158` (the seam block): add
  `public System.Action<uint> NetPickupItem;   // wired by ClientWorldSession: F on a focused WorldItemPuppet asks the server for the item`
  — null in SP/loopback ⇒ **SP stays byte-identical** (the `NetEnterVehicle` contract, `:1150-1152`).
- Add a small public request method next to `RequestEnterNearestPuppet` (`:1177-1183`):
  `RequestPickupFocusedPuppet()` — returns false unless `NetPickupItem != null` and
  `_focusPuppet is WorldItemPuppet wp` (with `IsInstanceValid`); then `NetPickupItem(wp.NetId)`,
  return true. Public so the L1 test can drive it without synthesizing input (the
  `RequestEnterNearestPuppet` precedent). This is a REQUEST — no local state changes; the pickup
  lands only when the server's `WorldItemRemoved` + owner-block echo come back.
- F chain `game/PlayerController.cs:1805-1817`: insert the branch **between** `:1810`
  (`_focusItem != null → TryPickup()`, the SP path — unreachable in MP since joined worlds have no
  `WorldItem` nodes) **and** `:1811-1812` (vehicle enter), i.e.
  `else if (RequestPickupFocusedPuppet()) { }` — so, like SP, a focused item wins over a nearby
  vehicle (SP order `:1810` before `:1811`), and vehicle-puppet entry (`:1812`, proximity-based)
  still fires when the focused puppet isn't an item. Note the existing vehicle-puppet focus is
  *proximity*-driven (`NearestPuppet` `:1160-1171`) while items are *focus*-driven — matching SP
  semantics on both counts.

### Step 3 — Wire the seam + reflect the result in `ClientWorldSession`

- `game/ClientWorldSession.cs:220-227` (`SpawnShell`, next to `shell.NetEnterVehicle`): add
  `shell.NetPickupItem = netId => Client.SendPickupItem(netId);`.
- Denial UX: subscribe `Client.ItemPickupDenied` (event exists, `NetWorldHost.cs:344, :414`) in
  `_Ready` near the combat-fact subscriptions (`ClientWorldSession.cs:104-126`) — v1: `GD.Print` +
  a HUD line (the `_desyncLabel` pattern `:134-136` or `HUD` if it grows a toast); at minimum the
  player learns "no room" instead of silence.
- World-side reflection needs **no work**: the `WorldItemRemoved` broadcast lands on the replica
  (`NetWorldHost.cs:412-413`) and `WorldItemReplicaView` frees the puppet on the next physics tick
  (`WorldItemReplicaView.cs:57-65`). `UpdateLookFocus` already tolerates a freed focus puppet
  (`PlayerController.cs:212-217` guards with `IsInstanceValid`).

### Step 4 — Adopt the replicated owner inventory into the shell  *(required for the pickup to be VISIBLE)*

Without this, a confirmed pickup mutates the server grid and echoes the owner block — and the bag UI
(reading the shell's local demo `PlayerInventory`, `PlayerController.cs:1712-1714`) never shows it.
This is also what makes MP "give" visible (§1.5) and is the foundation of Step 6.

- **Design: copy-in-place, don't swap references.** The replica entry's `Inventory` is a *new*
  object every snapshot block (`InventoryReplication.cs:365`), while `PlayerController.Inventory`
  (`:18`) is referenced by `InventoryUI.Inv`, `CraftingUI.Inv`, ammo search (`:1384-1408`), armor
  math (`:962-963, :994`), etc. So: add
  `PlayerController.AdoptReplicatedInventory(PlayerInventory replica)` that copies each page
  (clear + `loadSize` + re-add jars — the existing `CopyPage` shape, `PlayerController.cs:1081`,
  extended to all 9 pages) and assigns the worn-item refs onto the EXISTING `Inventory` instance.
  Every existing reader keeps working; `InventoryUI`'s signature poll (`InventoryUI.cs:70-82`)
  auto-refreshes on the next `_Process`, and its `!_dragging && _selPanel == null` guard (`:77`)
  already prevents a mid-drag yank.
- **Wire it in `ClientWorldSession`**: in `SpawnShell` (`:201-232`), (a) do an initial pull —
  `if (Client.Inventories.TryGet(Client.PlayerId, out var e)) shell.AdoptReplicatedInventory(e.Inventory)`
  — because the join snapshot's owner block (`NetWorldHost.cs:99-109`) applies BEFORE the shell
  exists (both ride the same first snapshot), so the `ReplicaUpdated` for it already fired; then
  (b) subscribe `Client.Inventories.ReplicaUpdated += owner => { if (owner == Client.PlayerId && Shell valid) … }`
  re-reading the entry via `TryGet` at event time.
- **Decision: seed joiner inventories with the demo kit, server-side.** Today the MP shell's demo
  bag (`PopulateDemoInventory`, `PlayerController.cs:1906`) is a client-side fiction; the server grid
  starts empty. Once adoption lands, the bag goes empty at join and local reload's mag hunt
  (`:1384-1408`) finds nothing — degrading the D1 combat feel. Recommended: extract the demo item
  list into a shared helper and grant it server-side on join in **`game/DedicatedServer.cs`**
  (a `Server.Session.PeerConnected += …` granting into `Server.Inventories` — game-side only, so
  every L0 core test and harness stays byte-identical), keeping `shell.EquipHotbar(1)`
  (`ClientWorldSession.cs:212`) truthful. Alternative (defer seeding, accept an empty MP bag) is
  viable but makes reload/consume dead on arrival.
- **Known, accepted wrinkle (flag, don't fix here):** local-only inventory mutations MP hasn't
  routed yet — reload mag accounting (`PlayerController.cs:1405-1408`), consume decrement (`:702`),
  deployable spend (`:786-790`) — will be *resurrected* by the next owner-block echo (full-state
  overwrite). Consume gets routed in Step 7; mag/ammo accounting belongs to the deferred per-gun
  equip seam (`ClientWorldSession.cs:209-212`). Friendly-co-op acceptable; PROGRESS.md should note it.

**After Steps 1-4 the pickup round trip is complete:** F on focused puppet →
`SendPickupItem(NetId)` → validator (`ServerTransactions.cs:147-152`) → `OnPickupItem`
(`:286-307`) transacts into the server grid → `WorldItemRemoved` broadcast + owner-block dirty →
puppet despawns on every client + the owner's bag shows the item (adoption) — or
`ItemPickupDenied` → HUD line, item stays.

### Step 5 — Strawberry's server-side look-at validation  *(genuinely missing)*

The current validator is reach-only (6 m sphere, `ServerTransactions.cs:149-152`) — a modified
client could hoover every item within 6 m regardless of where it's looking. Server state available:
`PlayerEntity.Pos` + `YawDegrees` — **no pitch** (`core/UnturnedNet/PlayerReplication.cs:181-188`),
and the engine-free core cannot raycast world geometry. So the honest, testable v1:

- **Facing-cone check** in the `PickupItemCommand` validator (`ServerTransactions.cs:147-152`):
  forward = `(sin yawRad, 0, cos yawRad)` — the exact convention `OnDropItem` already uses
  (`:280-281`); require `dot(forward, normalize_horizontal(item.Pos - player.Pos)) >= PickupFacingMinDot`
  with a new const (recommend `0.25f` ≈ 75° half-angle — generous for quantized yaw + look-down
  pickups) **skipped when horizontal distance < ~1.5 m** (an item at your feet has an unstable
  bearing and SP allows it via the eye-ray anyway). Add both consts beside `PickupReach`
  (`:36-37`) with the same "server bounds it generously" commentary.
- Optionally tighten `PickupReach` 6f → 4f? **No** — keep 6f; it deliberately matches
  `ServerVehicles.EnterReach` slack for grid-quantized feet positions
  (`VehicleReplication.cs:448-451`); the cone is the new information.
- **Not covered (flag honestly):** LOS through walls. The client's eye-ray is wall-blocked
  (`PlayerController.cs:131-151`), so honest clients can't request through-wall pickups; a cheating
  client could pick through thin walls within 6 m + cone. Fixing that needs a game-side raycast seam
  on `ServerTransactions` (the `Combat.WorldRay` pattern, `game/DedicatedServer.cs:65`,
  `Func<Vector3,Vector3,bool>` LOS) — defer with the rest of MP_PLAN §7's pre-public hardening
  (same tier as the deliberate `TODO(mp-security)` ownership deferrals, `ServerTransactions.cs:101-104`).
- Same cone check applies verbatim to `HarvestCropCommand`/crate opens later if wanted — out of
  scope here.

### Step 6 — Transactional inventory management: route the UI through the wire

Everything server-side exists (§1.1-1.2); the work is seams in `InventoryUI` mirroring the
`NetFire`/`NetEnterVehicle` pattern — null ⇒ existing local code runs ⇒ **SP byte-identical**;
wired (only by `ClientWorldSession`) ⇒ send the command, **skip the local mutation**, and let the
owner-block echo + Step 4 adoption + the signature poll (`InventoryUI.cs:70-82`) repaint the grid.
No local prediction in v1 — deliberate: the echo arrives within a snapshot interval (25 Hz + RTT;
tens of ms on this server), it can never desync, and rejected ops simply don't repaint (matching
"the client never seats itself"). If it ever feels laggy, prediction can be added later since server
and client run the *same* `TryDrag` cell math on the *same* grid state (`InventoryReplication.cs:9-15`).

Seams on `InventoryUI` (set from `ClientWorldSession.SpawnShell` alongside the shell seams; reach
the UI via a small `PlayerController.WireNetInventory(...)` pass-through since `_invUI` is private,
`PlayerController.cs:19`):

1. **Drag/move/swap** — `Drop()` `game/inventory/InventoryUI.cs:177-187`: at `:186`, when
   `NetMoveItem != null` ⇒ `NetMoveItem(sp, sx, sy, page, x1, y1, srot)` →
   `Client.SendMoveItem(...)` (`NetWorldHost.cs:539-540`) and skip `Inv.TryDrag`. Covers grid↔grid,
   grid↔hand-slot, and swaps — the server handler is the same `TryDrag`
   (`ServerTransactions.cs:133-141`).
2. **Drop to world** — `DropSelected()` `:357-372`: when `NetDropItem != null` ⇒
   `NetDropItem(_selPage, _selX, _selY)` → `Client.SendDropItem` (`NetWorldHost.cs:542-543`) and
   skip `pg.removeItem` + `Player.DropWorldItem` (`:365-367` — the client-local spawn that would
   otherwise create an unpickable ghost item). The server spawns the replicated world item with the
   forward toss (`OnDropItem`, `ServerTransactions.cs:268-284`) → `WorldItemSpawned` broadcast →
   the puppet appears via `WorldItemReplicaView` for everyone including the dropper. The
   was-held→unarmed nicety (`:364, :368`) stays client-local (equip state is client-side in D1).
3. **Equip-holster** — `EquipSelected()` `:299-301`: the slot-stash `Inv.TryDrag(...)` loop routes
   through the same `NetMoveItem` seam (target = first empty slot page; `CommandEquipItem` exists
   (`ServerTransactions.cs:154-160`) but `MoveItem` reaches the identical server mutation — use one
   seam, fewer moving parts). The in-hand equip itself (`Player.EquipHeldGun/...`, `:296-297`)
   stays local (D1 contract).
4. **Split/stack: does not exist — flag, don't build.** `MoveItemCommand` carries no amount
   (`InventoryReplication.cs:18-37`), and SP has no split UI either (drag moves whole jars,
   `InventoryUI.cs:150-187`; stacking merges only inside `tryAddItem` on pickup,
   `core/UnturnedSim/Items.cs:94` area). If VoX wants splitting it's a NEW command (next id 26) +
   SP UI feature — out of scope for "wire up what's built".

### Step 7 — Consume + crates over the wire (same seam family, small)

- **Consume**: the live flow is Hold + LMB → `PlayerController.TickConsume` local decrement
  (`PlayerController.cs:702`). Add `NetConsume` (page,x,y of the held consumable) →
  `Client.SendConsume` (`NetWorldHost.cs:554-555`); server removes the item + heals the combat
  state (`OnConsume`, `ServerTransactions.cs:321-340`). Client keeps the local vitals effects
  (vitals are client-local until the D2 vitals split — `ServerTransactions.cs:332-334`), skips the
  local `removeItemAmount`; the echo removes the item from the bag.
- **Storage crates**: SP `OpenNearestCrate` (`PlayerController.cs:1053`, F chain `:1815`) copies the
  crate into the STORAGE page locally (`:1064`). MP: an `NetOpenStorage` seam →
  `Client.SendOpenStorage(netId)`; server arbitration + the crate-view-in-STORAGE-page mechanic
  already exist end-to-end (`InventoryReplication.cs:255-307`, events `ServerTransactions.cs:170-191`,
  L0 `InventoryReplicationTests.cs:200-243`), and once open, crate↔bag moves are ordinary
  `MoveItemCommand`s on page 7 — Step 6 covers them for free. Needs server-side crate registration
  for placed storage (`ServerRegisterCrate`, `InventoryReplication.cs:255-261` — currently only
  tests call it; a `StorageCrate`→server bridge alongside `WorldItemNetSync` is a small follow-up).
  Crates are the natural NEXT slice after Step 6, not a blocker for it.

---

## 3. Regression tests (Factorio rule: each step ships its test in the same commit)

Existing coverage stays the gate: the L0 battery (§1.4) already locks the server round trip. New
tests, cheapest layer that can express each:

**L0 — `tests/UnturnedNet.Tests/InventoryReplicationTests.cs` (extend, use `TransactionalHarness`):**
- `pickup_behind_the_back_rejected` (Step 5): spawn an item 3 m BEHIND the player's yaw
  (`h.Server.Transactions.SpawnWorldItem`, `ServerTransactions.cs:471`; drive yaw via a `MoveInput`
  or `ServerDrive`), `SendPickupItem`, assert `Commands.Diag.ValidationRejected` bumps and the item
  stays — the mirror of `pickup_out_of_reach_rejected_at_choke_point` (`:113-127`).
- `pickup_at_feet_allowed_regardless_of_yaw` (Step 5's close-range cone skip): item 0.5 m away,
  yaw facing away → pickup still lands.
- (Steps 1-4, 6 are game-side wiring — L0 can't see them; the existing
  `drop_then_pickup_roundtrips_through_the_world` `:88-111` and move/equip tests already pin the
  core they call into.)

**L1 — `game/testing/tests/NetTests.cs` (the `net.shell_*` `ClientWorldSession`-over-MemTransport
pattern, e.g. `net.shell_walk_reconcile` `:1036`, `net.deploy_wire_power` `:177-260`):**
- `net.shell_pickup_item` (Steps 1-4, the headline test): dedicated world + `DedicatedServer`
  (`TransportOverride = MemServerTransport`) + `ClientWorldSession` (`TransportOverride`,
  `ClientWorldSession.cs:37`); wait for the shell; `ded.Server.Transactions.SpawnWorldItem(...)`
   2 m ahead of the shell; `Until` the puppet exists (`sess.Items.TryGetNode`,
  `WorldItemReplicaView.cs:26`); call `shell.RequestPickupFocusedPuppet()` — or, to skip focus
  raycast flakiness under the headless host, call the seam path via the puppet directly (the public
  request method should accept the puppet for exactly this, mirroring how `net.shell_drive` `:1986`
  drives ride mode); assert: (a) server world-item count 0, (b) puppet node freed, (c)
  `ded.Server.Inventories` grid contains the item, (d) **the shell's local `Inventory` shows it**
  (adoption echo — the piece no L0 test can see).
- `net.shell_pickup_denied_stays` (Steps 3-4): pre-fill the server grid full (the
  `pickup_into_a_full_grid` recipe `:129-146`), request pickup, assert the `ItemPickupDenied`
  handler fired (hook a flag), puppet still present, shell inventory unchanged.
- `net.shell_move_item_roundtrip` (Step 6): grant an item server-side (console give or direct
  grant), wait for adoption, drive the UI seam (`NetMoveItem(2,0,0, 2,3,2, 0)` — call the seam
  delegate directly rather than synthesizing mouse drags), assert server grid moved
  (`Diag.GridMovesApplied == 1`) and the shell's local grid mirrors it after the echo; then an
  illegal move → `GridMovesRejected` bumps and the local grid is untouched.
- `net.shell_drop_item_becomes_puppet` (Step 6): drive `NetDropItem`, assert a world-item entity +
  puppet appear and the bag slot empties on echo.
- SP protection: the existing SP L1 inventory/item tests (`game/testing/tests/InventoryTests.cs`,
  `ItemTests.cs`) plus `test.sh` L0 stay the "SP byte-identical" gate — no new SP tests needed
  because every seam is null there by construction.

**Not L2** — nothing here alters rendering; per CLAUDE.md, goldens are not part of this gate.

---

## 4. Risks / decisions to confirm with VoX & strawberry

1. **Demo-kit server seeding** (Step 4): recommended yes (in `DedicatedServer`, game-side only).
   Without it, joining players start with an empty (true) bag and reload/consume have nothing to
   chew on until they loot.
2. **No client prediction for inventory ops** (Step 6): accepted echo latency (≤ ~1 snapshot +
   RTT) in exchange for cannot-desync. Revisit only if it feels bad on claw.
3. **Owner-block resurrect of unrouted local mutations** (Step 4 wrinkle): mag accounting stays
   client-local until the per-gun equip phase; visible as "the mag I loaded reappears in my bag
   after the next server inventory change". Bounded, co-op-acceptable, documented.
4. **Look-at = facing cone, not eye-ray LOS** (Step 5): matches what engine-free state can prove
   (yaw only, no pitch on `PlayerEntity`); through-wall pickup by a *modified* client within 6 m
   remains possible until the pre-public hardening pass (MP_PLAN §7) adds a game-side LOS seam.
5. **Split/stack and wear/unwear clothing**: genuinely absent everywhere (no command, no SP UI);
   explicitly out of scope — new features, not wiring.

## 5. Suggested commit sequence

1. `MP pickup: WorldItemPuppet.NetId + NetPickupItem seam + F-chain request` (Steps 1-3) — with
   `net.shell_pickup_denied_stays` scaffolding if adoption isn't in yet, else fold into 2.
2. `MP pickup: owner-block inventory adoption into the shell (+ dedicated demo-kit seeding)`
   (Step 4) + `net.shell_pickup_item`.
3. `MP pickup: server-side facing-cone validation (strawberry)` (Step 5) + the two L0 cone tests.
4. `MP inventory: route InventoryUI drag/drop/equip through MoveItem/DropItem commands` (Step 6) +
   `net.shell_move_item_roundtrip`, `net.shell_drop_item_becomes_puppet`.
5. `MP inventory: consume + crate-open over the wire` (Step 7) + tests.

Each lands green through `./test.sh` (L0+L1) before push; PROGRESS.md gets the decision log
(seeding choice, cone constants, the resurrect wrinkle).
