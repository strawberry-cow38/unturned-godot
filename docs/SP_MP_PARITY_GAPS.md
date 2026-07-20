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

## Implementation ordering (cheapest→hardest, for the workflow)
A. GridPowerSource (reuse deployable graph) + GasPump (un-gate + entity) — share the deployable/
   power replication that already exists.
B. Crops client-view (CropReplicaView + input binding) — server already done.
C. TowRope relationship (extend VehicleReplication).
D. Containers (new SystemContainers + ReplicaView) — new system, highest impact.
E. Animals (new SystemId + puppet pattern) — new system.
F. (defer) Building real StructureManager; weather synced flag.
