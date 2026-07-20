# SP↔MP parity gaps — reconciled audit (2026-07-20)

Post-flip: the PLAYER-ACTION layer is unified (deployables/power, inventory, world-items,
combat, vitals, skills, crafting, crops-server, day-night — SystemId 1–12 all replicate and
are consumed under `--spconsume`). The remaining divergences are in the **world-fixture /
world-content layer**: things the SP `mode == WorldMode.Playable` branch builds as local
NetId-0 nodes that never spawn server-side, so a joined MP client (and the dedicated world)
never gets them. Three independent audits agree on this list (tinyclaw net/authority slice +
world-content agent + catboy feature inventory), with three conflicts resolved (gaspump,
grid-power, tow — catboy marked MP-aware, verified SP-only).

## Already UNIFIED — do NOT re-touch
players(1) combat(2) zombies(3) grenades(4) skills(5) deployables+power+wires(6) inventory(7)
world-items/loot(8) vehicles(9) day-night clock(10) crops-SERVER(11) resources/tree-alive(12).
Loot items, vehicles, zombies, resource nodes, holiday content, player-placed deployables +
power graph — all publish via a *NetSync and consume via a *ReplicaView/*View/*Puppets.

## GAPS — ranked by player-visible impact

### 1. World containers / store-shelf loot — HIGHEST — NEW SystemId
- SP: WorldBuilder.cs:56-74 registry maps ~24 prop GUIDs → lootable containers; TryContainer
  (289) flags them; Main.SpawnMapContainers (Main.cs:1683+) + SpawnEditorLootCrates/StoreShelves
  spawn real StoreShelf/LootCrate in the "crates" group, `_peiPlayable`-gated (Main.cs:1678),
  scanned only by local PlayerController.cs:1457 for F-to-open.
- MP: TryContainer returns false for Dedicated/Client (289) → ABSENT. No "crates" NetSync, no
  container SystemId; InventoryReplication is player-grid-only.
- ROOT: SP-only spawn + zero replication.
- UNIFY (entity-pattern): new SystemContainers + ContainerReplication + ContainerReplicaView
  (mirror DeployableReplicaView). Move TryContainer/SpawnMapContainers server-side (Dedicated
  rolls the loot table once, owns each grid). Open/loot rides an interact command validated in
  ServerTransactions (reuse the grid math). Contents = server-authoritative Storage grid.

### 2. GasPump (fuel fixture) — HIGH — un-gate + entity replication
- SP: WorldBuilder.cs:255 GasPump.Attach on every Gas_Pump_0; collider meta "gaspump" (273);
  RMB gas-can Extract (GasPump.cs:60); shared StationFuel tank (GasPump.cs:21).
- MP: ABSENT. GasPump.PowerNetId => 0 (GasPump.cs:26) "not replicated → SP/local wiring only".
- UNIFY (just-spawn-on-server + entity): give GasPump a NetId + ride the deployable entity
  system (already IPowerDevice). Move Attach out of the mode==Playable guard → runs on Dedicated;
  server owns StationFuel; replicate Fluid.Amount per station; client materializes via a fixture
  view; Extract becomes a server command (validate min(canSpace, remaining) server-side, so fuel
  can't be double-spent).

### 3. GridPowerSource (10kW mains) — HIGH — reuse deployable graph replication
- SP: WorldBuilder.cs:260 GridPowerSource.Attach on every Circuit_0; wire-able 10kW output in
  "deployables" group (GridPowerSource.cs:66); F1 toggleGlobalPower.
- MP: ABSENT. GridPowerSource.PowerNetId => 0 (GridPowerSource.cs:51) "SP/local wiring only …
  MP is a later task"; WorldBuilder.cs:258-259 comment states the gate.
- UNIFY (entity, shares deployable path — LOWEST incremental cost of the 3 fixtures): it's
  already an IPowerDevice in "deployables". Register grid sources as server-side deployable-graph
  source entities (fixed world-placed subtype, ServerPlace at build on Dedicated) so wires/solver
  replicate through the existing SystemDeployables; the Circuit_0 source materializes on the client
  via DeployableReplicaView like any other power device. Replicate the global-power toggle bit.

### 4. Crops — client-view + input gap — MED-HIGH — server DONE, add client view
- Host/listen-server: local CropManager (withCropManager:true) + CropNetSync bridges nodes ↔
  CropReplication (WorldNetSync.cs:82-175, only if CropManager.Active). Works on the host.
- Dedicated: no CropManager; crops are engine-free CropReplication entities (correct).
- Remote client: withCropManager:false (ClientWorldSession.cs:437), NO CropReplicaView anywhere,
  SendPlantCrop/SendHarvestCrop (NetWorldHost.cs:708-712) have ZERO call sites → a joined client
  cannot see/plant/harvest crops.
- UNIFY (entity-pattern): add CropReplicaView (mirror WorldItemReplicaView) materializing
  Client.Crops.All → CropNodes (growth from PlantedAtTick + synced schema, already registered);
  bind client plant/harvest input to SendPlantCrop/SendHarvestCrop. Server side complete.

### 5. Animals / wildlife (deer/pig/cow) — MED — NEW SystemId, zombie-puppet pattern
- SP: WorldBuilder.cs:499-501 only; AnimalField streams keyed on the LOCAL PlayerController
  (AnimalField.cs:13,70-72).
- MP: ABSENT everywhere; zero animal replication (no SystemId/NetSync/view). Dedicated skips it
  (WorldBuilder.cs:531-533 "stays out until its streamer generalizes").
- UNIFY (physics-body/puppet-pattern, like zombies): generalize AnimalField streaming onto
  PlayerRegistry (the C4 move already done for ZombieField+LootField), spawn on Dedicated, add
  AnimalReplication SystemId + AnimalNetSync + AnimalPuppets mirroring Zombie*. Server keeps the
  real body; remote clients get interpolated puppets.

### 6. TowRope / vehicle towing — MED — extend VehicleReplication
- SP/host: Vehicle.Towing/TowedBy + TowRope re-pointed each physics tick (Vehicle.cs:129-133);
  pull force in UpdateTow; rope cosmetic.
- MP: tow state not replicated (no tow fields in VehicleReplication/VehicleNetSync); a joined
  client sees no rope and no coupling in the puppet transform.
- UNIFY (physics-body-pattern): replicate the tow RELATIONSHIP (tower NetId ↔ towed NetId +
  restLen) as a couple bytes on the vehicle entity; VehicleReplicaView draws a cosmetic TowRope
  between the two puppet transforms. Physics stays host-authoritative.

### 7. Building / structures (BuildTool) — LOW — stub even in SP
- catboy: BuildTool = box floor/wall grid-snap STUB, not the real StructureManager (assets/
  health/save). No sync. Defer until the SP feature itself is real; then replicate as an entity
  system like deployables.

### 8. Weather / rain (RainOverlay) — COSMETIC / deferred
- Not in the unified BuildFullWorld path; only legacy/demo code (Main.cs:1034,2435) as a local
  unsynced CanvasLayer, Raining = Randf() < 0.35. No MP client ever sees it; not a functional
  divergence. If weather ever drives gameplay, add a synced flag on SystemWorldClock.

## Editor-placed fixtures
editor_PEI_gridpower/gaspump/shelves/crates via SpawnEditor* are all `_peiPlayable`-gated
(Main.cs:1678) — same SP-only class; unifying fixtures 1-3 fixes these automatically (same
Attach/Spawn methods).

---

# CATEGORY B — DEFAULT-SP REGRESSIONS FROM THE P6a FLIP (player-systems audit)

The P6a flip made CONSUME the DEFAULT SP path: `Main.ResolveLoopbackMode` (Main.cs:2030-2036)
returns `(true,true)` for every real SP game entry (Drive PEI / --peidrive / --peiplay);
`--direct`/`UG_DIRECT=1` is the opt-OUT. VERIFIED. So every "loopback" gap below is what a solo
player gets BY DEFAULT today. Several `Net*` seams were wired only on the MP-CLIENT path
(ClientWorldSession) and NOT on the loopback path (MpLoopback) → the default SP game half-routes
through consume with the wrong fallback. These are REGRESSIONS the flip introduced, and rank ABOVE
the Category-A content gaps because they break the shipped solo experience.

Seam matrix ground truth: MpLoopback.cs:92-180 (loopback) vs ClientWorldSession.cs:440-468 (client).

## P0 — shipped SP BUGS in the default (consume) path — fix FIRST
B1. **STORAGE item-loss** (VERIFIED PlayerController.cs:1484-1490). F-interact opens a crate via the
   SP-direct OpenCrate (never RequestOpenStorage), so `_openCrateNetId==0`; but `NetCloseStorage` IS
   wired, so CloseCrate takes the net branch, sees `_openCrateNetId==0`, and returns WITHOUT the local
   copy-back → items placed in a world StoreShelf are LOST on close. FIX: gate the net branch on
   `_openCrateNetId!=0` else fall through to local copy-back; route open through RequestOpenStorage for
   NetId!=0 crates. (Server storage path exists+tested: ServerTransactions.cs:189-207.)
B2. **DEPLOYABLE pickup no-op** (VERIFIED PlayerController.cs:677). `PickupDeployable` early-returns on
   `d.NetId != 0`; consume deployables are replica nodes (NetId!=0) → F-hold to retrieve a placed
   generator does nothing. FIX: when NetId!=0 + NetSalvageDeployable!=null, send a pickup/salvage intent
   and let the item return via the owner-inventory echo; or add a distinct NetPickupDeployable command.
B3. **CROP harvest invisible** (PlayerController.cs:2501, MpLoopback.cs:171). F-interact calls local
   CropManager.Harvest; the dropped yield is a local SP WorldItem which SuppressLocalVisual=true HIDES
   and de-focuses → harvested item invisible + un-pickup-able; XP local (not adopted). FIX: add a
   NetHarvest(cropNetId) command → server validates ripeness/reach, spawns the world-item entity
   (WorldItemReplicaView materializes it, visible), awards XP server-side. (Pairs with A4 crops-view.)
B4. **CONSUME "Use" button resurrect** (InventoryUI.cs:574-587). The inventory-dashboard Use decrements
   the stack LOCALLY and never calls NetConsume → server doesn't see it, next owner echo resurrects the
   item. FIX: route UseSelected through NetConsume(page,x,y) + skip local decrement when the seam is set
   (mirror TickConsume PlayerController.cs:1038-1043).

## P1 — gameplay regression
B5. **VITALS toothless** (PlayerController.cs:2262-2274). HP is server-adopted, but food/water/stamina/
   infection still run the LOCAL sim and the `died` result is DISCARDED under NetVitalsAdopted → drain
   to Food=0 and never die; the server runs no hunger sim. FIX: move fine vitals server-side (owner-only
   block), route starvation/dehydration damage through ServerCombat.DamagePlayerExternal (the sink the
   loopback host already uses, MpLoopback.cs:156), adopt the fine block like HP.

## P2 — loopback doesn't exercise MP authority (correct for solo, but breaks host-vs-remote + is half-migrated)
B6. **COMBAT** NetFire/Melee/Grenade/Reload NOT wired in MpLoopback (wired on client CWS:444-447) → the
   host's shots aren't server-resolved; a remote joiner on a listen-server can't be shot by the host.
   FIX: wire the four in MpLoopback under ConsumeDeployables (host shots become server-resolved via the
   in-process Server.Combat; Cosmetic flips identically). Also wire NetReload (B/minor).
B7. **SKILLS** NetUpgradeSkill not wired + AdoptReplicatedSkills never called in loopback (both on client
   CWS:260,468) → skills fully local, unreconciled; on a client any local AwardExperience is overwritten
   next tick. FIX: wire NetUpgradeSkill + AdoptReplicatedSkills in MpLoopback; route all XP awards server-side.
B8. **VEHICLE enter/exit** NetEnterVehicle/NetExitVehicle not wired in loopback (host drives a real owned
   node) → a remote joiner could seat into the host's occupied car. FIX (interim): wire the two in
   loopback so seat arbitration is respected; long-term run host driving through the replicated-vehicle path.

## P2 — MP-client-facing (feature works in SP, broken/absent on a JOINED client) — overlaps Category A
B9. **STORAGE unreachable on client** — WorldMode.Client builds no containers + DeployableReplicaView
   never materializes StorageCrate → OpenNearestCrate finds nothing. (Same fix as A1 containers.)
B10. **CLOTHING/held-weapon/stance/anim not broadcast** — RemotePlayers renders each remote as a flat
   orange CharacterModel, pos+yaw only (RemotePlayers.cs:38-44); worn slots live in owner-scoped
   InventoryReplication, never broadcast. FIX: add a small per-player appearance broadcast block (worn
   slot ids + held item + stance byte) and have RemotePlayers apply a PlayerClothingController + stance
   + basic move anim. (Pairs with the SP clothing port, task #54.)
B11. **ROPE-TOW/hitch broken on client** — scans the "vehicles" group of real nodes; the client's Part A
   local vehicle RemoveFromGroup("vehicles") (CWS:344) → scan finds nothing. (Same fix as A6 tow: replicate
   the tow relationship over vehicle NetIds; scan "vehicle_puppets" on the client path.)

## Intentional asymmetries — NOT bugs (leave as-is)
- NetDamageSink wired in loopback (host IS authority, forwards fall/OOB to ServerCombat) but null on the
  client (client fall/OOB is server-derived; local TakeDamage no-ops via NetVitalsAdopted). By design.
- NetRespawn(reposition:) true for the loopback host (its node is authority) vs false for the client
  (reposition rides PlayerRecovEvent). By design.

---

## Implementation ordering (the workflow — severity + coupling)
Do Category B P0/P1 FIRST (they break the shipped default solo game and are mostly seam-wiring in
MpLoopback + guard fixes — low risk, NO protocol change), then Category A content:
0. B6/B7/B8 seam-wiring + B1 CloseCrate guard + B4 UseSelected route + B2 PickupDeployable route +
   B5 server vitals — mostly wiring seams already wired on the client into the loopback. No protocol bump.
A. GridPowerSource (reuse deployable graph) + GasPump (un-gate + entity) — share the deployable/
   power replication that already exists.
B. Crops client-view (A4 CropReplicaView + input binding) + B3 NetHarvest — server already done.
C. TowRope relationship (A6/B11 extend VehicleReplication).
D. Containers (A1/B9 new SystemContainers + ReplicaView + StorageReplicaView) — new system, highest impact.
E. Animals (A5 new SystemId + puppet pattern) — new system.
F. Clothing broadcast (B10) — pairs with the SP clothing port.
G. (defer) Building real StructureManager; weather synced flag.
One coordinated NetProtocol.Version bump covers the new SystemIds (containers, animals) + new fields
(tow, appearance) + new commands (harvest, pickup-deployable). Allocate together to avoid version collisions.
