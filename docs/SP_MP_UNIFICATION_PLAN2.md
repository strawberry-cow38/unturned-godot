# SP / MP Unification — Implementation Plan v2 (closing the parity gaps)

**Status:** planning, branch `sp-mp-unify` (worktree `ug-unify`). Composed from the per-gap
implementation specs, the protocol-coordinator plan, and the adversarial plan review — every review
issue is folded into the relevant gap as a **Review note + resolution**.
**Scope:** the post-flip parity gaps only (Category B default-SP regressions + Category A
world-fixture/content gaps). The player-action layer (SystemId 1–12) is already unified; this plan
does NOT re-touch it.
**Predecessor:** `SP_MP_UNIFICATION_PLAN.md` (the P0–P7 migration + P6a flip) and
`SP_MP_PARITY_GAPS.md` (the reconciled audit). This doc is the implementable follow-on.

Git discipline for this branch: the repo TRACKS `bin/obj`, so any build/test dirties tracked
artifacts. **Commit source first** before running `./test.sh`. **Never** `git checkout -- .` (it
reverts uncommitted source, not just bin/obj drift — `./test.sh` rebuilds artifacts itself). Stay on
`sp-mp-unify`; do not merge to `main`, do not deploy, do not push.

---

## 1. The unified paradigm + what is already unified

### 1.1 The paradigm

SP runs as an **in-process integrated (listen) server + a loopback client whose local views consume
replicas**. Every feature is authored **once**, server-authoritative, exactly like retail Unturned.
Under the P6a flip, `--spconsume` (`ConsumeDeployables`) is the DEFAULT for real SP game entries;
`--direct` / `UG_DIRECT=1` is the byte-identical opt-out that keeps the old direct path (and is the
harness/L2-golden substrate). Four structural patterns recur, and every gap below is one of them:

- **entity** — the server holds plain data (a `*Replication` `IReplicatedSystem`); the client
  materializes the ONLY Godot nodes via a diff-driven `*ReplicaView`; zero duplication. (deployables,
  world-items, crops, containers, grid-power, gas-pump, animals-data.)
- **physics-body (listen-server)** — the HOST keeps the real body and runs the sim ON it directly
  (write-once: the server logic is the only logic); only REMOTE clients get interpolated puppets.
  This preserves the client-auth / anti-inchworm decision (the driven body stays the real client-auth
  body, never re-simmed on a second process). (vehicles, zombies, animals-body, rope-tow physics.)
- **split-authority** — one entity is co-authored by two disjoint writers that never touch the same
  field: e.g. position client-auth + HP server-auth; transform-adopted + vitals-owned + tow-published.
- **seam-wiring / guard-fix** — a `Net*` seam already exists and is wired on ONE surface
  (`ClientWorldSession`) but not the other (`MpLoopback`), or a guard selects the wrong branch. Pure
  client-side routing; no wire change.

**Anti-patterns that are hard paradigm violations (never do):** build a SECOND client-local body for
a thing the host already owns (the two-body "inchworm"); re-simulate the local player from input;
mutate a replica locally instead of routing an intent; let two authorities write one field.

### 1.2 Already UNIFIED — SystemId 1–12 (do NOT re-touch)

`players(1) combat(2) zombies(3) grenades(4) skills(5) deployables+power+wires(6) inventory(7)
world-items/loot(8) vehicles(9) day-night clock(10) crops-SERVER(11) resources/tree-alive(12)` — all
publish via a `*NetSync` and consume via a `*ReplicaView/*View/*Puppets`, and are consumed under
`--spconsume`. `SystemSyncCheck(255)` composes LAST. **Verified in source:** `NetProtocol.Version =
10`; `ReplicationIds` runs `SystemPlayers=1 … SystemResources=12`, `SystemSyncCheck=255`; highest
command in use is `CommandPlayerState=27` (so command ids 28–31 are free); the `NetProtocol.cs:54`
changelog explicitly reserves **SystemId 13 for the pending owner-vitals branch — do not reuse**.

### 1.3 What this plan adds

Three new SystemIds (13 vitals, 14 containers, 15 animals), four new commands (28–31), new fields on
two existing blocks (SystemVehicles tow, SystemPlayerCombat appearance) and one command
(PlayerStateCommand.HeldItemId), and a set of purely client-side seam/guard fixes — all under **ONE**
coordinated `NetProtocol.Version 10→11` bump (§3), never per-gap.

---

## 2. Category B — default-SP regressions from the P6a flip

These break the SHIPPED solo experience today (consume is the default). Fix them first; most are
seam-wiring/guard-fixes with **no** protocol change.

### B1 — Storage item-loss on close (guard-fix, no protocol)

**Bug (verified `PlayerController.cs` `CloseCrate` @1482-1497):** F-interact opens a world StoreShelf
via the SP-direct `OpenCrate` (@1467), which sets `_openCrate` but leaves `_openCrateNetId==0` (only
`OnReplicatedStorageOpened` @1508 sets the NetId). But `NetCloseStorage` IS wired in the loopback
(`MpLoopback.cs:98`), so `CloseCrate` takes the net branch, sees `_openCrateNetId==0`, and `return`s
BEFORE the local copy-back (@1491-1496) — items dragged into the shelf are silently destroyed on close.

**Fix:** change the outer guard from `if (NetCloseStorage != null)` to
`if (NetCloseStorage != null && _openCrateNetId != 0)`. When `_openCrateNetId==0` (look-opened
SP-local crate) fall through to the existing local copy-back (CopyPage STORAGE→`_openCrate.Storage` +
EndLiveDisplay + clear). The inner `if (_openCrateNetId != 0)` @1488 becomes redundant — drop it.
`_openCrate` and `_openCrateNetId` are mutually exclusive by construction, so the guard cleanly
selects server-path vs local-copy-back.

**Files:** `game/PlayerController.cs` (sole site).
**Server:** none — `ServerCloseStorage` (`InventoryReplication.cs:281-295`) is correct and only runs
for `_openCrateNetId!=0`.
**Tests (teeth):** L1 `unify.storage_close_no_loss` — look-open a NetId==0 StoreShelf, drop an item
into its grid, `DebugCloseCrate`, assert `shelf.Storage.getItemCount()==1`. Pre-fix, CloseCrate hits
the net branch, sees NetId 0, returns before copy-back → shelf empty, assertion fails.
**Determinism:** none — pure state-preservation guard. Keep the two open paths mutually exclusive so a
crate is never both locally copied-in AND server-opened; B9 guarantees NetId!=0 crates take the wire
path (echo sets `_openCrateNetId`) and NetId==0 take the local path.
**Depends on:** none. **Risk: low.**

**Review note + resolution (Issue #4, low):** the spec set contained TWO `B1` entries describing the
identical fix (both correct). **Resolved:** deduped to this single B1. Do not double-implement or
double-test; it is one one-line guard change + one L1.

### B2 — Deployable pickup no-op (entity, PROTOCOL: new command)

**Bug (verified `PlayerController.cs` `PickupDeployable` @675-695):** hold-F to retrieve a placed
generator early-returns on `d.NetId != 0`; under consume, deployables are replica nodes (NetId!=0), so
pickup does nothing.

**Fix (a distinct full-item command, NOT overloading Salvage):** `OnSalvageDeployable`
(`ServerTransactions.cs:248-264`) spawns SCRAP (blowtorch-wreck semantics); hold-F pickup must return
the actual deployable item with quality+fuel. These are distinct player intents, so they get distinct
commands — overloading Salvage with server-side wreck-vs-live inference is fragile.

**Protocol change: YES.** New `CommandPickupDeployable = 28` (rides the one coordinated bump).
`PickupDeployableCommand{uint NetId}` (shape of `SalvageDeployableCommand` @71-82). Reuses
`EventDeployableRemoved(12)` + `EventWireRemoved(14)` for teardown and `SystemInventory(7)` owner-echo
for the returned item — NO new events/systems.

**Files:** `core/UnturnedNet/PlayerReplication.cs` (const `CommandPickupDeployable=28`);
`core/UnturnedNet/DeployableReplication.cs` (`PickupDeployableCommand` struct);
`core/UnturnedNet/ServerTransactions.cs` (register + `OnPickupDeployable`);
`core/UnturnedNet/NetWorldHost.cs` (`SendPickupDeployable`); `game/PlayerController.cs` (add
`NetPickupDeployable`; in `PickupDeployable` drop `|| d.NetId != 0` from the guard @677, then after the
null/wreck/onfire guard add `if (d.NetId != 0) { NetPickupDeployable?.Invoke(d.NetId); return; }` BEFORE
the local makeLoot/Pickup path = return-after-send, no local mutation); `game/MpLoopback.cs` +
`game/ClientWorldSession.cs` (wire the seam to `Client.SendPickupDeployable`);
`core/UnturnedNet/NetProtocol.cs` (Version, coordinated).

**Server:** `OnPickupDeployable` is authority — read entity Health/Fuel/DefId + def; `ServerRemove` +
cascade `WiredRemoved` (reuse @250-259); build the item via `makeLoot(defId)` with
`quality=clamp(Health/def.Health*100,1,100)` and `fuelLevel=Fuel` (def.FuelCapacity>0);
`SenderInventory.tryAddItem` else `SpawnWorldItem(item, entity.Pos+up)` when the bag is full (mirrors SP
"drop where it stood" @691). Ownership/reach validation deferred `TODO(mp-security)`, consistent with
`OnPlaceDeployable` @238-245.
**Client:** send-and-return; node retires via `DeployableReplicaView` on `EventDeployableRemoved`; item
lands via owner-inventory echo (`Inventories.ReplicaUpdated → AdoptReplicatedInventory`), full-bag drop
via `WorldItemReplicaView`.
**Tests (teeth):** L0 `deploy_pickup_returns_item` — ServerPlace a generator, dispatch the command,
assert entity removed + wires cascade-removed + sender server inventory gained the item with right
id/quality/fuel (and a full-bag case spawns a world-item); pre-fix the round-trip can't even be
expressed (no command). L1 `unify.deploy_pickup` — place a generator (NetId!=0), PickupDeployable,
tick, assert node retired AND item back in the local bag; pre-fix the @677 early-return leaves both
unchanged.
**Determinism:** fuel/quality reconstructed from the SERVER entity's Health/Fuel — if the loopback
server doesn't sim generator fuel-burn/HP-decay, a picked-up item carries placement-time values (a
deployable-entity-sim divergence, flag not fix here). Double-authority avoided by return-after-send
(the `DeployableReplicaView` stays sole node owner). Intent is unambiguous: `UpdateDeployPickup` gates
hold-F on `!IsWreck/!OnFire` @656, so pickup never targets a wreck (no collision with Salvage).
**Depends on:** none (command reserved in the coordinated bump). **Risk: medium.**

**Review note + resolution (Issue #2, high):** the per-gap spec strings collided on command id 28
(A2 and B11 also wrote 28/29). **Coordinator-canonical is authoritative:** `CommandPickupDeployable =
28` here (B2), `CommandExtractFuel = 29` (A2), `CommandAttachTow = 30` + `CommandDetachTow = 31` (B11).
B2's id 28 is correct and stands.

### B3 — Harvest-yield invisible in loopback (seam-wiring, no protocol)

**Bug (`PlayerController.cs:2501`, `MpLoopback.cs:171`):** F-interact calls local `CropManager.Harvest`;
the dropped yield is a local SP WorldItem born with `SuppressLocalVisual=true` → invisible +
un-focusable + un-pickup-able; XP awarded locally then overwritten by adoption.
**Correction to the cluster brief:** there is NO "NetHarvest command (protocol)" work — the harvest wire
is ALREADY complete. `CommandHarvestCrop(25)` + `SendHarvestCrop` + `OnHarvestCrop` (removes crop, spawns
the REPLICATED yield, rolls AGRICULTURE server-side, awards XP — `ServerTransactions.cs:382-401`) +
`EventCropHarvested(26)` are live under v10 and L0-tested. Only a game-side seam is missing.

**Fix:** `game/MpLoopback.cs` — under `ConsumeDeployables`, next to `NetPickupItem` (@180), add
`Player.NetHarvestCrop = netId => Client.SendHarvestCrop(netId);`. Do NOT add a `CropReplicaView` here
(the loopback host keeps its real `CropManager` CropNodes; the yield materializes through the existing
`WorldItemReplicaView` @172). `game/WorldNetSync.cs` — in `CropNetSync.Tick`, stamp `c.NetId = netId`
after minting a locally-planted crop's entity (@129 region) and `node.NetId = e.NetIdValue` after
materializing a remote-planted entity (@152 region). Idempotent writes, harmless when the seam is null.
The F-interact routing helper (`RequestHarvestNearestCrop`) and the seam declaration come from A4.
**Server:** none — `OnHarvestCrop` is the authoritative sink; the loopback in-process server runs it.
**Client:** F-interact routes `RequestHarvestNearestCrop → NetHarvestCrop → SendHarvestCrop(node.NetId)`
INSTEAD of the direct harvest — yield is the server's replicated (visible+focusable) world-item, XP is
server-adopted via `AdoptReplicatedSkills`, and the local CropNode retires when `CropNetSync` sees the
entity gone (NOT a direct QueueFree).
**Tests (teeth):** L1 `unify.crop_harvest_yield` — plant server-side, mature, `SuppressLocalVisual=true`,
harvest via SendHarvestCrop, assert crop retired + yield materialized VISIBLE+FOCUSABLE + `ded.Server.Skills`
experience +1; pre-fix the direct harvest yields a hidden WorldItem and awards XP locally → both asserts
fail. L1 `unify.crop_harvest_no_double` — shell + CropManager + one grown CropNode (NetId stamped),
`NetHarvestCrop` set to a capturing lambda; drive F-interact; assert the lambda fired with the NetId AND
`CropManager.Harvest` did NOT run (no local yield node, crop not self-QueueFree'd); pre-fix both the
direct drop AND a minted second entity appear (double-mutation), racing `CropNetSync`'s removal.
**Determinism:** zero bytes change, no re-golden. Real risk is DOUBLE-MUTATION — route the seam AND skip
the direct branch; `RequestHarvestNearestCrop` returning true (seam set) supersedes the direct path
(same "seam set ⇒ direct superseded" invariant as P1/P2). Under `UG_FARMSPEED>1`, a harvest in the
<=0.5 s pre-force-grown window is server-rejected and retried next F (self-healing). XP double-count
avoided via server-owned adoption.
**Depends on:** A4 (the seam declaration + F-interact helper). **Risk: low.**

### B4 — Consume "Use" button resurrect (guard-fix, no protocol)

**Bug (`InventoryUI.cs:575-587`):** `UseSelected` decrements the LOCAL jar and never calls `NetConsume`,
so the server never sees it; the next owner echo re-adopts the old amount → the item resurrects.
**Fix:** reuses `CommandConsume(17)` (the `NetConsume` seam already exists at `PlayerController.cs:1740`
and is wired in loopback `MpLoopback.cs:126` + client `ClientWorldSession.cs:459`). Add
`PlayerController.RequestConsume(byte page,x,y){ if (NetConsume==null) return false; NetConsume(page,x,y);
return true; }`. In `UseSelected`: keep `Player?.Consume(jar.GetAsset())` for vitals, then
`if (Player!=null && Player.RequestConsume(_selPage,_selX,_selY)) { /* server owns the delete */ } else {
local amount--/removeItem }` — mirror the `DropSelected` route @558-567. Add a `DebugUse(byte,byte,byte)`
helper (mirror `DebugEquip`) so an L1 can drive it headlessly.
**Files:** `game/PlayerController.cs`, `game/inventory/InventoryUI.cs`.
**Server:** none — `OnConsume` (`ServerTransactions.cs:343-362`) validates `IsConsumable`,
`removeItemAmount(id,1)`, applies useHealth. (B5 extends it to raise food/water — that's B5's edit.)
**Client:** mirrors `TickConsume` — local `Consume(asset)` still applies vitals (food/water client-led;
HP server-adopted via `AdoptReplicatedVitals`), `RequestConsume` routes the DELETE and skips the local
decrement; the owner-block echo repaints the grid.
**Tests (teeth):** L1 `unify.use_button_consume` — place a stack (amount 2), `DebugUse`, tick past an
owner echo, assert SERVER grid decremented to 1 AND local stays 1; pre-fix the local-only decrement
means the next `AdoptReplicatedInventory` re-adopts 2 → count jumps UP → assertion fails.
**Determinism:** no worldgen/RNG. Double-mutation avoided by skipping the local decrement when routed.
The dual useHealth apply (local + server) doesn't double-count because `AdoptReplicatedVitals` is the
last HP writer each tick.
**Depends on:** none. **Risk: low.** (Note: B5's server food/water raise depends on B4 routing the
dashboard Use through `NetConsume`, else the "Use" button never raises server vitals.)

### B5 — Server-authoritative fine-vitals (split-authority, PROTOCOL: new SystemId 13)

**Bug (`PlayerController.cs:2262-2274`):** HP is server-adopted, but food/water/stamina/infection run the
LOCAL sim and the `died` result is DISCARDED under `NetVitalsAdopted` → drain to Food=0 and never die;
the server runs no hunger sim.

**Protocol change: YES.** New **`SystemVitals = 13`**, an OWNER-ONLY `IReplicatedSystem` (mirrors
`SkillsReplication` SystemId 5). Owner-block payload per entry: `OwnerPlayerId:u16`,
Food/Water/Stamina/Infection each `WriteUnsignedNormalizedFloat(8 bits)`, Bleeding:1bit, Broken:1bit.
This consumes exactly the reserved slot (`NetProtocol.cs:54`). No new command (raise via `CommandConsume`
17; starvation/regen via internal `ServerCombat.DamagePlayerExternal` + direct `CombatState.HealthExact`
write — neither crosses the wire). No new event (adoption rides the snapshot every tick like HP/Skills).
NOT added to `EnableSyncCheck` (owner-only, excluded by design like Skills/Inventory).

**Files:** `PlayerReplication.cs` (`SystemVitals=13`); `NetProtocol.cs` (Version 10→11; changelog: v11
SystemVitals(13); resolve the "v8 RESERVED … SystemId 13" note → "landed v11"); **NEW**
`PlayerVitalsReplication.cs` (owner-only, modeled 1:1 on `SkillsReplication`; `VitalsEntry{OwnerPlayerId;
PlayerVitalsSim Sim; LastChangedTick}`; seam fields `IsAlive/SprintingOf/MultipliersOf/HealthOf/DamageSink/
RegenSink/SurvivalDrain`; `ServerStep` re-seeds `Sim.Health=HealthOf(pid)` each tick, steps, routes the
delta OUT — `DamageSink` for loss, `RegenSink` for gain; hash the QUANTIZED floats);
`NetQuantization.cs` (`QuantizeUnsignedNormalizedFloat(v,bits)` mirroring the signed one, so the server
hashes the round-tripped wire value); `NetWorldHost.cs` (`Vitals` on server+client; append to
Composer[]/Applier[]; `Vitals.ServerAdd/ServerRemove` on peer connect/disconnect; wire the seams —
`SprintingOf` from adopted stance / held MoveInput.Stance, `MultipliersOf` from `Skills`, `HealthOf` from
`CombatState.HealthExact`, `DamageSink=Combat.DamagePlayerExternal`, `RegenSink=` direct HealthExact
raise; insert `Vitals.ServerStep(...)` BETWEEN `VehicleHost.Step` and `Combat.Step` so queued starvation
drains in THIS tick's Combat.Step); `ServerTransactions.cs` (`OnConsume` applies the previously-stubbed
food/water/stamina/infection effects @354-356; needs a `PlayerVitalsReplication` ref);
`game/PlayerController.cs` (`NetFineVitalsAdopted` + `AdoptReplicatedFineVitals(...)`; in `UpdateVitals`
skip the local `_vitals.Step` fine-vitals mutation when adopted); `game/ClientWorldSession.cs` +
`game/MpLoopback.cs` (adopt fine vitals each tick after `AdoptReplicatedVitals`; MpLoopback additionally
PACKS stance into `SendMoveInput` — currently buttons=0 — and mirrors `PlayerController.SurvivalDrain →
Server.Vitals.SurvivalDrain`); `game/DedicatedServer.cs` (default `SurvivalDrain=false`, SP-identical, +
a config hook); **NEW** `tests/UnturnedNet.Tests/PlayerVitalsReplicationTests.cs`; `game/testing/tests/
UnifyTests.cs` (new L1s).

**Server:** `PlayerVitalsReplication` holds one `PlayerVitalsSim` per player; stepped every 50 Hz tick
BETWEEN `VehicleHost.Step` and `Combat.Step`. HP is NEVER owned by the vitals sim — each tick it is
re-seeded from `CombatState.HealthExact` (the single HP authority) and the delta is routed OUT
(starvation DAMAGE via the queued `DamagePlayerExternal` env-attacker sink, death-capable, lands same
tick; regen via direct `HealthExact` raise). Death/respawn stay owned by `ServerCombat`. `OnConsume` now
raises server food/water/stamina/infection.
**Client:** the shell CONSUMES the owner-only block via `AdoptReplicatedFineVitals` each tick (the
`AdoptReplicatedVitals`/`AdoptReplicatedSkills` analogue), so the HUD's direct reads reflect server
truth; the local fine-vitals `_vitals.Step` is skipped under adoption. RAISING vitals reuses the
already-wired `NetConsume → CommandConsume(17)` (this is where **B4 matters**: without B4 the dashboard
"Use" bypasses the server and won't raise server food/water; the held-consumable `TickConsume` already
routes).
**Tests (teeth):** L0 `fine_vitals_replicate_to_owner_only` (owner sees drain, other client's TryGet
false, server StateHashFor==replica StateHash quantized); L0 `starvation_routes_through_combat_sink_and_
kills` (HealthExact decreases via DamagePlayerExternal, then Alive==false + PlayerDied Killer=0); L0
`consume_raises_server_food_water`; L0 `regen_while_fed_heals_combat_hp`; L1 `unify.fine_vitals_starve`
(a real ClientWorldSession shell starves to death server-owned, then a consumed food raises Food + HP
regens, DESYNC-QUIET); L1 `unify.fine_vitals_loopback_starve` (listen-server host drains server-side,
dies, sprint reflected in server Stamina — proves the SendMoveInput stance-pack). All fail pre-fix
because there is no server vitals model.
**Determinism:** (1) NO double HP-authority — re-seed from HealthExact, route delta OUT, never a direct
HealthExact write for DAMAGE; ServerStep BEFORE Combat.Step drains same tick. (2) Stamina↔sprint split —
stamina server-owned+adopted, sprint client-auth; server derives `sprinting` from the ADOPTED stance
(requires MpLoopback to pack stance); residual is a few ticks of lag, acceptable (matches HP adoption),
NO second body. (3) Owner-block float parity — hash the QUANTIZED wire value. (4) No RNG/index-order
(sorted owner list). (5) SurvivalDrain authority server-owned (default false = SP byte-identical). (6)
Byte-identity — with SurvivalDrain=false + a healthy player the regen delta is 0, coarse HP path
unperturbed; `--direct` shells never adopt.
**Depends on:** none (but see B4 coupling). Its SystemId 13 must be allocated BEFORE A1/A5 take 14/15
so the arrays stay contiguous. **Risk: medium.**

### B6 — Combat not wired in loopback (seam-wiring, no protocol)

**Bug:** host shots are local-only, so a remote joiner can't be shot and fx go server-blind.
`CommandFire(2)/Melee(3)/Grenade(4)/Reload(5)` + `EventHitConfirm(2)/ImpactFx(3)/ZombieHit(6)/
GrenadeExploded(9)` all exist and `Server.Combat` is constructed in the loopback with `WorldRay` set
(`MpLoopback.cs:63`) — pure wiring, verbatim from `ClientWorldSession.cs:154,159-181,444-447`.
**Fix:** `game/MpLoopback.cs` — under `ConsumeDeployables`, set `NetFire/NetMelee/NetGrenade/NetReload`
to the `Client.Send*` calls; add `ProjectileReplicaView{Client=Client}` (host sees its wire-thrown
grenade); subscribe the fact consumers (`HitConfirmed→HitmarkerHUD`, `ImpactFx→RenderImpactFx`,
`GrenadeExploded→FlinchAllFromExplosion`, `ZombieHit→blood at the brain by NetId`). `game/ZombieNetSync.cs`
— add `bool TryGetBrain(uint netId, out ZombieController)` over the private `_byId` map (the loopback has
no ZombiePuppets to look up).
**Server:** none new — `Server.Combat` resolves Fire/Melee/Grenade against `ZombieReplication` positions
→ `IZombieHost.DamageZombie` → the REAL brain (same path SP uses) and against remote CombatEntities. Kill
credit already correct (`ZombieNetSync.Tick` announces Killer=0 for non-wire kills; `ServerCombat`
announces with credit + sets `DeadAnnounced` for the wire path).
**Client:** setting `NetFire` flips the local bullet to Cosmetic (`PlayerController.cs:2817`) → no local
damage/decals/blood/hitmarker; `NetMelee`/`NetGrenade` skip the local hit/node; the wired fact events
become the SOLE fx source.
**Tests (teeth):** L1 `unify.loopback_fire_zombie` (RequestFire at a brain, assert wire kill with host
credit + local bullet Cosmetic, single DamageHit no double-count); L1 `unify.loopback_host_shoots_remote`
(two loopback clients, host fires at a joined remote's position, assert the remote's server CombatEntity
HP dropped). Pre-fix (NetFire null) the local non-cosmetic bullet applies DamageHit directly, the wire
never runs → both assertions fail.
**Determinism:** server hits resolve against 12.5 Hz-published zombie positions (up to 80 ms stale) — a
favor-shooter feel delta for the SOLO host (inherent to the paradigm; wire the fx consumers or the host
loses hitmarkers/blood/decals). Double-count avoided (Cosmetic bullets apply no local fx). No worldgen/RNG.
**Depends on:** none. **Risk: medium** (P2 change that risks the currently-"correct-for-solo" host feel).

### B7 — Skills not wired/adopted in loopback (seam-wiring, no protocol)

**Bug:** `NetUpgradeSkill` unset + `AdoptReplicatedSkills` never called in loopback. `CommandUpgradeSkill(6)`
+ `SystemSkills(5)` exist and `SkillsUI` already routes through `RequestUpgradeSkill` — pure wiring,
verbatim from `ClientWorldSession.cs:260,468`.
**Fix:** `game/MpLoopback.cs` — set `Player.NetUpgradeSkill = (spec,index) => Client.SendUpgradeSkill(spec,
index);`; in `TickLocal` beside vitals adoption add `if (ConsumeDeployables && Client.Skills.TryGet(
Client.PlayerId, out var sk)) Player.AdoptReplicatedSkills(sk.Skills);`.
**Server:** none new — `ServerTryUpgrade` is the cost/cap validator; `AwardXp` the XP hook. The server
skills entity is `ServerAdd`'d at 0 XP on join, which MATCHES the SP shell (the game path grants no demo
skills), so NO seed is needed here (unlike the P1b inventory seed).
**Tests (teeth):** L1 `unify.loopback_upgrade_skill` — AwardXp server-side, RequestUpgradeSkill, tick,
assert the shell's local Skills level rose via adoption; pre-fix `NetUpgradeSkill` null → RequestUpgradeSkill
returns false → SkillsUI falls back to LOCAL TryUpgrade (0 XP) and adoption is never called → assertion fails.
**Determinism:** per-tick adoption overwrites the shell's local Skills — SAFE only because the SP shell
starts at 0 XP. If a demo-XP grant is ever added, the SERVER skills entity MUST be seeded on
PeerConnected or adoption zeroes it (the P1b failure mode). No RNG; server is sole XP owner.
**Depends on:** none. **Risk: low.**

### B8 — Vehicle seat arbitration: a tick-ordering race, not missing seams (physics-body, no protocol)

**Correction to the brief:** do NOT "mirror the seams." Wiring `NetEnterVehicle/NetExitVehicle` in the
loopback is a DEAD wire — F-interact branch 6 (SP-direct EnterVehicle on a focused real node) always
wins because the host focuses/drives REAL `vehicles`-group nodes; the puppet branches scan
`vehicle_puppets`, which the host's cars are never in. Routing the host through the puppet/Part-A path
would build a SECOND client-local vehicle body = the two-body inchworm = a hard paradigm violation. **The
host MUST keep its real body.** The real gap is a 1-tick occupancy race + a missing regression test.
**Fix:** `game/VehicleNetSync.cs` — extract the listen-server local-occupancy reconcile (@77-85) into a
public `ReconcileLocalOccupancy()` that applies only the claim/free, leaving publish/drive/hold in Tick().
`game/MpLoopback.cs` — register a pre-sim step `net.vehicles.occupancy` running `ReconcileLocalOccupancy()`
BEFORE `net.server.sim` (@201), so the host's current Driving state is reflected in `DriverPlayerId` before
any remote EnterVehicle command is dispatched+validated that tick. Do NOT set NetEnterVehicle/NetExitVehicle.
**Server:** `ServerVehicles.CanEnter` already rejects occupied/dead/out-of-reach; `Vehicle.NetDriverId`
already blocks the host from taking a remote-occupied seat. The fix only reorders WHEN the occupancy
bridge runs relative to command dispatch.
**Client:** none — the host stays on SP-direct EnterVehicle/ExitVehicle (real body).
**Tests (teeth):** L1 `unify.host_seat_arbitration` — host SP-direct-enters a car; the SAME tick a
remote sends EnterVehicle for that NetId; assert CanEnter REJECTS the remote (DriverPlayerId stays
localId). Reverse: remote drives, host F is blocked by NetDriverId. Pre-fix the reconcile runs AFTER
`net.server.sim`, so the remote's Enter validates against DriverPlayerId==0 and wins the seat (double-seat).
**Determinism:** the double-seat race is closed by deterministic step order on the SimRoot. NEVER route
the host through `RequestEnterNearestPuppet` (two-body inchworm, structurally unfixable). Solo has no
remotes → correct no-op for the shipped solo game. Note: the brief's stated bug is mostly ALREADY
mitigated by `VehicleNetSync`'s occupancy bridge; only the 1-tick race + the test remain.
**Depends on:** none. **Risk: medium.**

### B9 — Containers reachable + openable on a joined client (seam-wiring, no protocol; behavior gated on A1)

**Bug:** `WorldMode.Client` builds no containers + `OpenCrate` never routes → a joiner can't reach a
store shelf. Reuses `CommandOpenStorage=18`/`CommandCloseStorage=19` + `StorageOpened/Closed`(21/22) — no
new wire element; depends on A1 putting `SystemContainers` on the wire.
**Fix:** `game/PlayerController.cs` — `OpenCrate` (@1467-1479): when the crate has NetId!=0 AND
`NetOpenStorage!=null`, route through `RequestOpenStorage(crate.NetId)` and RETURN (no local CopyPage);
the dashboard opens on the `StorageOpened` echo, the grid arrives via the owner-block echo. For NetId==0
keep the local copy path. For a double-sided shelf resolve `ResolveSide` FIRST. `game/ClientWorldSession.cs`
— instantiate the A1 `StorageReplicaView` and AddChild it (storage seams already wired @466-467).
`game/WorldBuilder.cs` — WorldMode.Client `TryContainer` skips the decoration mesh (the replica draws it).
**Server:** none new — A1's `ContainerNetSync` registered the crate; `ServerOpenStorage/ServerCloseStorage`
validate + arbitrate + copy the grid (as `net.shell_open_storage` proves at `NetTests.cs:2452-2519`).
**Client:** `OpenCrate` becomes intent-only for replicated crates; close is correct via `NetCloseStorage`
once B1's guard lets it run. The A1 `StorageReplicaView` supplies the openable node; this gap wires the
node's NetId into the open intent.
**Tests (teeth):** L1 `net.container_open_on_client` — join a Dedicated host with a registered shelf,
drive `OpenCrate` on the materialized node, assert it routed (crate.OpenBy==PlayerId), dashboard opened,
STORAGE echoed, then move an item bag-ward + close, assert the crate saved back; pre-fix `OpenCrate` does
the LOCAL CopyPage, OpenBy stays 0, the item vanishes on close (and before A1 there's no node to open).
L2 `container_shelf_client_render` — golden of a joined-client store gondola (mesh + tier loot exactly
once); without the Client-mode decoration skip the prop draws twice + tiers empty.
**Determinism:** intent-only routing, server owns the grid. The one hazard is a DOUBLE decoration mesh if
`TryContainer` doesn't skip the prop in Client mode. Ensure both the proximity scan and look-focus resolve
the REPLICA node (in the `crates` group, NetId!=0) so F opens the server crate, never a client phantom.
**Depends on:** A1. **Risk: medium.**

### B10 — Broadcast worn-clothing + held-item + stance (split-authority, PROTOCOL: fields on existing blocks)

**Bug:** `RemotePlayers` renders each remote as a flat orange `CharacterModel`, pos+yaw only
(`RemotePlayers.cs:38-44`); worn slots live in owner-scoped `InventoryReplication`, never broadcast.
**Protocol change: YES — fields only, NO new SystemId (13 is reserved — do NOT allocate a new system for
appearance; ride an existing one).** (1) `PlayerCombatReplication.CombatEntity` (SystemId 2, the ONLY
globally-mirrored per-player block) gains 7 worn-slot ushorts (hat/glasses/mask/shirt/vest/backpack/pants,
0=none) + a held-item ushort + a stance byte — appended to `WriteEntity`/`ReadEntity`/`StateHash`. (2)
`PlayerStateCommand` (CommandPlayerState 27, owner→server) gains a `HeldItemId` ushort (stance already
rides Buttons bits). Re-baseline the `PacketHeader`/`MoveInput` goldens in the SAME commit as the bump.
**Files:** `CombatReplication.cs` (fields + Write/Read/StateHash + `ServerSetAppearance(owner,worn7,held,
stance,tick)` dirty-on-change); `PlayerAuthority.cs` (`HeldItemId` on PlayerStateCommand +
DrivenPlayerState + DrivenState; store in `OnPlayerState` un-validated, like PitchDegrees);
`NetProtocol.cs` (Version + changelog + repeat the SystemId-13-reserved caveat); `NetWorldHost.cs`
(`SendPlayerState` gains a `heldItemId` param); **NEW** `game/PlayerAppearanceNetSync.cs` (server-side
publisher, `VehicleNetSync` shape: for each server player compute worn7+held+stance and
`ServerSetAppearance` — for the LOCAL listen-server owner read the shell directly, for remotes read
`Inventories`/`TryGetDrivenState`; READ-ONLY projection, never mutate inventory); `game/PlayerController.cs`
(`HeldItemId` read-only property); `game/RemotePlayers.cs` (swap CharacterModel→`RiggedCharacter.Build`;
per-puppet `PlayerInventory` + `PlayerClothingController`; on appearance diff set worn slots + `Refresh()`,
attach/detach held mesh, `SetLocomotion(observedSpeed, stance)`); `game/RiggedCharacter.cs` (`AttachHeld`/
`DetachHeld` on bone `Right_Hand`); `game/ClientWorldSession.cs` (pass `Shell.HeldItemId` into
`SendPlayerState`); `game/MpLoopback.cs` + `game/DedicatedServer.cs` (construct + register
`net.appearance.publish` after `net.server.sim`, before `net.server.replicate`); `game/testing/tests/
NetTests.cs` (relabel the remote-puppet path).
**Server:** split authority onto ONE existing globally-mirrored block. Worn slots stay authored SOLELY by
`InventoryReplication` (SystemId 7); the appearance block is a COSMETIC READ-ONLY PROJECTION (never writes
inventory back). Held+stance are owner-reported dressing (adopted verbatim, un-validated — never trusted
for damage/reach). Dirty-on-change so the 25 Hz delta doesn't fire every tick.
**Client:** `RemotePlayers` (keyed by ownerPlayerId, skips self @34) becomes the consumer; the puppet is
a RiggedCharacter that dresses/poses/animates from the broadcast block. The client `Inventories` replica
is owner-only, which is exactly WHY worn ids must ride the globally-mirrored combat block.
**Tests (teeth):** L0 `appearance.combat_block_roundtrips`; L0 `appearance.held_rides_player_state`; L0
`appearance.statehash_covers_block_and_matches` (two players differing only in clothing must hash
DIFFERENT — pre-fix they collide, the desync check is blind); L0 `appearance.projection_readonly_and_
dirty_on_change` (no inventory mutation, no spurious LastChangedTick); L1 `unify.remote_appearance_applied`
(a two-client world: A wears shirt+hat, holds a gun, crouches → B's puppet reflects the worn ids + held +
CROUCH). Pre-fix `CombatEntity` has no fields (won't compile) and `RemotePlayers` exposes nothing to assert.
**Determinism:** exact integers, no float quantization → exact hash equality, BUT the fields MUST be
mixed into StateHash on BOTH sides symmetrically (a one-sided mix false-desyncs every tick; forgetting it
blinds the check to clothing divergence). No worldgen/RNG; publisher iterates SortedIds. Double-authority
avoided (inventory is the single author; appearance is a read-only projection; held is cosmetic-only). The
CharacterModel→RiggedCharacter swap is a client-only render change needing a headless render-verify
(readability + proportions), and any L2 golden touching remote avatars must be re-baselined intentionally.
**Depends on:** none (rides the coordinated bump). **Risk: medium.**

### B11 — Client-side rope tie/untie (seam-wiring, PROTOCOL: two new commands)

**Bug (`SP_MP_PARITY_GAPS.md:161-163`):** the rope scan iterates `GetNodesInGroup('vehicles')`, which is
EMPTY on a client (the local car is `RemoveFromGroup`'d at `ClientWorldSession.cs:344`, other cars are
puppets), so a joined client can't tie a rope.
**Protocol change: YES — two new commands (no new SystemId/event; the RESULT rides A6's replicated
fields).** `CommandAttachTow = 30` `{u32 TowerNetId, u32 TowedNetId}` + `CommandDetachTow = 31` `{u32
NetId}` (either end; the handler resolves to the tower like `Vehicle.DetachTow`). These ride the SAME
single bump A6 makes.
**Files:** `VehicleReplication.cs` (`AttachTowCommand`/`DetachTowCommand` structs, fail-closed on short
read); `PlayerReplication.cs` (`CommandAttachTow=30` + `CommandDetachTow=31`); `NetWorldHost.cs`
(`SendAttachTow`/`SendDetachTow` on ReliableOrdered); `game/VehicleNetSync.cs` (register BOTH commands on
`_server.Commands` — game-side, the apply mutates real nodes; VALIDATE: both exist+in-tree, NEITHER
remote-driven, neither already roped, tow-point gap ≤ `Vehicle.TowAttachReach`, requester within RopeReach;
then `towerNode.AttachTow(towedNode)`/`node.DetachTow()`; A6's publish mirrors the result back. ALSO: when
a remote driver takes a rope-end vehicle, `node.DetachTow()`); `game/PlayerController.cs` (seams
`NetAttachTow`/`NetDetachTow`; RopeLmb/RopeRmb send intents instead of the direct AttachTow; the SCAN
iterates `vehicle_puppets` carrying puppet NetId when the seam is set; keep the `vehicles` path for direct
SP/loopback host); `game/ClientWorldSession.cs` (wire the two seams; left null in SP/MpLoopback so the host
keeps the direct AttachTow — no double); `game/VehiclePuppet.cs` (optional tow-nub parity);
`tests/UnturnedNet.Tests/VehicleReplicationTests.cs` (wire round-trip + fail-closed); `game/testing/tests/
VehicleTowTests.cs` (L1 remote-tie + reject cases + scan test).
**Server:** the handler is the authority + anti-cheat choke point (game-side because the apply mutates
real Vehicle NODES): validate reach + not-already-roped + not-remote-driven, then call the real
`towerNode.AttachTow` (which computes restLen from the live gap, so the command carries only two NetIds).
Tow physics still runs solely in `Vehicle.UpdateTow` on the host's real bodies. Dispatch (`net.server.sim`)
runs BEFORE `net.vehicles.sync`, so the AttachTow applied this tick is published by A6's `ServerPublishTow`
the SAME tick → replicate LAST.
**Client:** never mutates tow state; the scan retargets to `vehicle_puppets`; the committed rope appears
only when A6's `TowedNetId` echoes back (the CompleteWire discipline). The rigid semi-hitch
(`CoupledCab/CoupledTrailer` PinJoint) is a distinct low-cost follow-on reusing this exact field+publish+
render mechanism (a `HitchedNetId` field, no restLen, drawn rigid), NOT blocking the rope.
**Tests (teeth):** L0 `AttachDetachTow_WireRoundTrip_FailClosed`; L1 `vehicle.rope_tow_remote_tie` (a
client SendAttachTow ties two host vehicles → `tower.Towing==towed` on the real node + `replica.TowedNetId
==towed` + one puppet rope; SendDetachTow clears; out-of-reach/already-roped/remote-driven rejected at the
choke point); L1 `vehicle.rope_tow_scan_finds_puppets`. Pre-fix there's no SendAttachTow and RopeLmb
mutates a LOCAL Vehicle that doesn't exist on the client → the tie is unreachable, and the scan finds
nothing in the empty `vehicles` group.
**Determinism:** double-authority avoided (handler mutates the NODE only; A6's publish is the sole entity
writer). Two-body/physics-authority: the not-remote-driven validate + the detach-on-enter guard ensure a
client-auth/held vehicle never becomes a rope end, so the host spring never fights an adopted transform —
the remote-driven-tower-on-dedicated case is EXPLICITLY deferred (rejected/detached), by design.
Ordering: dispatch precedes tow publish precedes replicate (same-tick, deterministic). No RNG/index-order/
worldgen.
**Depends on:** A6. **Risk: high.**

**Review note + resolution (Issue #2, high):** the spec strings wrote `CommandAttachTow=28;
CommandDetachTow=29`, colliding with B2 (28) and A2 (29). **Coordinator-canonical:** AttachTow=30,
DetachTow=31. Corrected above; the coordinator `collisions` table (§3) records the remap B11:28→30, 29→31.

---

## 3. Category A — world-fixture / world-content gaps

### A1 — World containers as replicated entities (entity, PROTOCOL: new SystemId 14)

**Gap:** SP spawns `StoreShelf/LootCrate` as local NetId-0 nodes (`Main.SpawnMapContainers/
SpawnEditorLootCrates/StoreShelves`, `_peiPlayable`-gated); `TryContainer` returns false for
Dedicated/Client → containers ABSENT for a joiner and the dedicated world.
**Protocol change: YES.** New **`SystemContainers = 14`** (NOT 13 — reserved). No new command (open/close
reuse 18/19). No new event (fixtures placed at world-build + static → ride the join FULL snapshot; content
edits ride `WriteDelta` + the opener's `InventoryReplication` owner block).
**Files:** **NEW** `core/UnturnedNet/ContainerReplication.cs` (`IReplicatedSystem` SystemId 14, a cross of
`DeployableReplication` + `WorldItemReplication` relevancy; `ContainerEntity{NetId; KindId; Pos; Yaw;
Width,Height; DisplayCell[] Display; LastChangedTick}`; `DisplayCell{Cell; ItemId; Rot}` = a SERVER-derived
read-only projection of the crate grid; `ServerRegisterFixture/ServerSetDisplay/ServerRemove`;
`ContainerSchema` engine-free); `PlayerReplication.cs` (`SystemContainers=14`, comment: 13 reserved);
`NetProtocol.cs` (Version + changelog); `NetWorldHost.cs` (`Containers` on server+client; append to
Composer[]/Applier[]); **NEW** `game/ContainerNetSync.cs` (mirror `CropNetSync`: schema RegisterAll; at
server init take the container MANIFEST, mint a NetId per container, roll the loot table ONCE into an
`InventoryReplication` crate grid via the tested `ServerRegisterCrate`, register the FIXTURE, project the
DISPLAY digest; low-cadence `Tick` refresh on grid-signature change); `game/DedicatedServer.cs` (instantiate
+ sim step + `Interest = InterestPolicy{RingRadius=128}` so a joiner gets only nearby fixtures of the
~426 PEI containers); `game/MpLoopback.cs` (under consume, register schema + `ContainerNetSync` + a
`StorageReplicaView` — INVARIANT: with the view present the SP-local container spawners must NOT run);
`game/WorldBuilder.cs` (`TryContainer` flags containers in Dedicated AND Playable; Client mode returns true
but SKIPS the decoration mesh; fold the editor crate/shelf parse into `result.Containers` as the single
manifest); **NEW** `game/StorageReplicaView.cs` (mirror `DeployableReplicaView`: diff-drive
`Client.Containers.All` → `StoreShelf/LootCrate` nodes, `ServerOwned=true` no local roll, `node.NetId`
stamped, `ApplyDisplay(digest)`, self-register in `crates`); `game/inventory/StorageCrate.cs` (`NetId` +
`ServerOwned`); `game/inventory/StoreShelf.cs` (gate the `_Ready` loot roll on `!ServerOwned`; add
`ApplyDisplay`); `game/inventory/LootCrate.cs` (gate the roll on `!ServerOwned`); `game/Main.cs` (gate the
SP-local container spawners to the direct path only).
**Server:** `ContainerNetSync` (game-side, runs on Dedicated + loopback host) mints one NetId per
container, rolls the loot ONCE into a crate grid via `ServerRegisterCrate`, registers the fixture, projects
the digest. Open/loot/close reuse `CommandOpenStorage/CloseStorage` (validate reach + one-opener arbitration
+ copy the grid into STORAGE page 7; on close the writeback settles edits). Server holds NO StoreShelf node.
**Client:** `StorageReplicaView` is the SOLE materializer on BOTH the remote client and the loopback host;
interaction rides the existing look/proximity path (F → OpenCrate/OpenNearestCrate); the storage seams +
StorageOpened/Closed facts are already wired. The one missing seam-wire (routing OpenCrate through
RequestOpenStorage when NetId!=0) is B9.
**Tests (teeth):** L0 `container.fixture_roundtrips`; L0 `container.digest_delta_and_interest` (WriteDelta
updates the digest; an out-of-ring fixture is omitted and reappears when ViewPos moves in — a naive
full-resend would blow the join snapshot with all ~426 fixtures); L1 `net.container_materialize` (a joiner
sees a StoreShelf stamped with the fixture NetId, in `crates`, with the digest items on its tiers). All
fail pre-fix (no `ContainerReplication`, no `SystemContainers` block).
**Determinism:** (1) DOUBLE-ROLL — server rolls ONCE, every replica gates on `ServerOwned` (a
non-deterministic roll is FINE because contents are replicated verbatim; only the FIXTURE — pos/yaw/kind/
dims, parsed+quantized identically both sides — must be byte-identical). (2) DOUBLE-SPAWN on the host —
the SP-local spawners MUST be gated off under consume. (3) NetId is server-only (Mint) + crosses the wire
(no client/server map-parse index dependence). (4) hash the deterministic FIXTURE, not the mutable digest.
(5) SystemId collision: take 14, not the reserved 13.
**Depends on:** the owner-vitals SystemId-13 reservation (allocate `SystemContainers=14`); the coordinated
bump with A5 (animals). **Risk: high.**

**Review note + resolution (Issue #1, high):** the spec said "Optionally include SystemContainers in
EnableSyncCheck for the FIXTURE only" and the coordinator echoed "fixture MAY be in sync-check". This is
WRONG: `EnableSyncCheck` composes the SERVER's FULL per-system StateHash and the client compares its
applied state; a relevancy-FILTERED system hashes all ~426 fixtures server-side vs the client's nearby
subset → a guaranteed StateHash mismatch every check interval → `DesyncDetected` fires continuously on
every PEI world (WorldItems + Zombies are relevancy-filtered and correctly excluded for exactly this
reason). **Resolved: do NOT add `SystemContainers` to `EnableSyncCheck`.** Keep the sync-check list exactly
as-is (Players/PlayerCombat/Projectiles/Deployables/Vehicles/WorldClock/Crops/Resources — all globally
mirrored, no InterestPolicy). The "fixture MAY be in sync-check" note is struck from both the spec and the
coordinator registration order. If a container integrity check is wanted, do it in an L0 round-trip, not
the live cross-client sync-check.

### A2 — GasPump fuel fixture (entity, PROTOCOL: new command 29)

**Gap:** SP `GasPump.Attach` on every `Gas_Pump_0` with an RMB gas-can Extract from a client-local
`StationFuel` tank the server never sees; ABSENT in MP.
**Protocol change: YES.** New `CommandExtractFuel = 29` `{uint PumpNetId}`. The fixture materialization +
fuel-STATE replication need NO wire change on their own (the GasPump DeployableDef is content-hash only,
the fill rides the EXISTING `entity.Fuel` scalar as a 0..100 PERCENT). ONLY Extract needs the command.
**Files:** `game/DeployableDef.cs` (`GasPump` def Id 9201, `FixtureKind=GasPump`, one Consumer port 750W,
FuelCapacity=0); `game/GasPump.cs` (`Materialize` self-contained node with a `gaspump`-meta collider; a
replicated `float FillPercent`; under consume the node owns NO tank — Extract is server-routed);
`game/DeployableReplicaView.cs` (GasPump branch alongside A3's GridSource: `Materialize` matched by
quantized position, stamp NetId, each tick `node.FillPercent = e.Fuel`); `core/UnturnedNet/
DeployableReplication.cs` (`ExtractFuelCommand` struct); `PlayerReplication.cs` (`CommandExtractFuel=29`);
`NetProtocol.cs` (Version + changelog); `ServerTransactions.cs` (register + `OnExtractFuel`: validate
sender pos + pump exists + FixtureKind==GasPump + reach + holds a gas can with free space; `Solve()` the
graph, reject if the pump's Consumer port is unpowered; `pulled=min(canFreeSpace, stationRemaining)`;
drain the absolute station tank; add pulled to the held can; write the recomputed percent onto EVERY
same-station pump entity's Fuel scalar in ONE tick; seam `IFuelStation FuelStations`); **NEW**
`game/GasStationServer.cs` (owns the authoritative absolute per-station `FluidTank`s + the pumpNetId→
stationId map; `IFuelStation`); `NetWorldHost.cs` (`SendExtractFuel`); `game/PlayerController.cs`
(`NetExtractFuel` seam; in `TryExtractFuel` @887 when the pump is a replica send the intent + RETURN —
skip the local Extract + fuelLevel add; the owner-echo re-adopts the fuller can); `game/ClientWorldSession.cs`
+ `game/MpLoopback.cs` (wire the seam; construct + register `GasStationServer`); `game/WorldBuilder.cs`
(record `Gas_Pump_0` placements into `result.Fixtures` with `stationId=StationFuel.StationIdFor(gpos)`;
the Playable-inline Attach moves to the direct path); **NEW** `tests/.../GasPumpExtractTests.cs`;
`game/testing/tests/UnifyTests.cs` (new L1).
**Server:** the GasPump is a `DeployableEntity` (FixtureKind=GasPump), server-placed at build. The shared
station tank is server-authoritative (`GasStationServer`). Extract is the ONLY mutation, at the
`ServerTransactions` choke: `Solve()` to confirm Powered, `pulled=min(canFreeSpace, stationRemaining)` so
fuel can't be double-spent, drain the absolute tank, add pulled to the held can, write the recomputed
percent onto EVERY same-station pump in one tick (atomic fan-out).
**Client:** `DeployableReplicaView` materializes a GasPump node with a `gaspump` collider + a fuel bar
from the replicated percent; RMB routes through `NetExtractFuel`; the fuller can arrives via the owner-echo
and the drained level via the Fuel-scalar snapshot.
**Tests (teeth):** L0 `gaspump.extract_drains_station_server_side` (drains min(canSpace, remaining),
updates ALL same-station pumps equally in one tick, rejects unpowered/empty/no-can, a second extract can't
over-drain); L1 `unify.gaspump_fixture_extract`. Pre-fix `CommandExtractFuel(29)` is unregistered (dropped)
and there's no server station tank (the invariant is unprovable; SP drains a client-local tank).
**Determinism:** (1) station fan-out ATOMIC (same LastChangedTick) or two pumps replicate divergent fill →
desync. (2) the "is powered" gate is a FRESH deterministic Solve(). (3) under consume the direct Attach
(local tank drain) is DISABLED; the can is server-mutated + owner-echo-adopted, NEVER locally added. (4)
the `entity.Fuel` scalar (12int/2frac, max ~4095.75) CANNOT hold the 8000L capacity → replicate a 0..100
PERCENT; the absolute tank stays server-side. (5) min(canSpace, remaining) validated server-side. (6)
worldgen byte-identity: fixtures RECORDED not spawned inline; `stationId` derived identically via
`StationFuel.StationIdFor`.
**Depends on:** A3 (shares `FixtureKind`/`DeployableDef` + `WorldBuilder.Fixtures` plumbing). **Risk: medium.**

**Review note + resolution (Issue #2, high):** the spec string wrote `CommandExtractFuel = 28`, colliding
with B2. **Coordinator-canonical: `CommandExtractFuel = 29`.** Corrected above; the coordinator `collisions`
table (§3.3) records A2:28→29.

### A3 — GridPowerSource mains as a server-placed fixture entity (entity, no Version bump, content-hash)

**Gap:** SP `GridPowerSource.Attach` on every `Circuit_0` (a 10kW wire-able source, F1
`toggleGlobalPower`); ABSENT in MP.
**Protocol change: NONE (Version).** Reuses `SystemDeployables(6)` entirely — the grid source is a
`DeployableEntity` with a new `DeployableDef` (a def changes `NetContent.Hash`, NOT `NetProtocol.Version`).
Producing-while-mains-on rides the EXISTING `entity.ToggledOn` bit; the global toggle reuses `ConsoleCommand
(20)` + `DeployableToggledEvent(15)`. No new SystemId/command/event/field.
**Files:** `game/DeployableDef.cs` (`FixtureKind` enum {None,GridSource,GasPump} + field; `GridSource` def
Id 9200, one 10kW Output port, FuelCapacity=0, NOT player-placeable); `game/DeployableNetSchema.cs`
(propagate `FixtureKind`→`DeployableNetDef.FixtureKind`); `core/UnturnedNet/DeployableReplication.cs`
(`byte FixtureKind` on the DEF table only — zero wire-shape change); `game/GridPowerSource.cs`
(`bool? NetProducingOverride` deriving producing from the replicated `entity.ToggledOn` instead of the
process-global `PowerNet.GlobalPower`; a static `Materialize` building a self-contained node);
`game/DeployableReplicaView.cs` (GridSource branch → `Materialize` matched by quantized position, stamp
NetId, each tick `node.NetProducingOverride = e.ToggledOn`); `game/WorldBuilder.cs` (stop the inline
Playable-only Attach; RECORD Circuit_0 placements into `result.Fixtures` in every mode, keep the mesh+
collider draw byte-identical; add `List<FixtureRecord> Fixtures` to `WorldBuildResult`);
`game/DedicatedServer.cs` (`ServerPlace` each GridSource fixture with `ToggledOn=false`, mains default OFF);
`game/MpLoopback.cs` (under consume ServerPlace the fixtures; on `--direct` call the old `Attach`; set
`DevConsole.RemoteClient = Client` so the mains-toggle routes over the wire); `game/Main.cs` (pass
`res.Fixtures`; on the direct branch iterate + `Attach`); `core/UnturnedNet/ServerTransactions.cs`
(in `RunConsole` add `toggleglobalpower`/`grid [on|off]` BEFORE the AllowCheats gate — a legit mechanic:
for every GridSource entity `ServerToggle` + broadcast `DeployableToggledEvent`); `game/DevConsole.cs`
(add the verb to `ServerGatedVerbs`; gate the local `PowerNet.SetGlobalPower` on `RemoteClient == null` so
ONLY `--direct` flips the process-global); **NEW** `tests/.../DeployableGridSourceTests.cs`;
`game/testing/tests/UnifyTests.cs` (new L1).
**Server:** the GridSource is a `DeployableEntity` in `Server.Deployables`, server-placed at build in
deterministic map-file order; producing-state is `entity.ToggledOn`. The mains toggle is applied at the ONE
choke (`RunConsole 'toggleglobalpower'`): `ServerToggle` every GridSource + broadcast — reuses the existing
toggled wire. `Solve()` (pure) turns the 10kW into wired-consumer Powered on both sides.
**Client:** `DeployableReplicaView` spawns a GridPowerSource NODE via `Materialize` matched by quantized
position, stamped NetId, into `deployables`; each tick `NetProducingOverride = e.ToggledOn`. The local F1/
toggleGlobalPower routes as a ConsoleCommand intent — no local state mutation.
**Tests (teeth):** L0 `grid.source_replicates_and_energizes`; L1 `unify.grid_power_fixture`. Pre-fix there's
no GridSource def (ServerPlace→null) AND no server toggle verb → the consumer never powers.
**Determinism:** (1) mains-bit double-authority — F1/toggleGlobalPower MUST route over the wire and the node
MUST derive producing from `entity.ToggledOn`, NEVER local GlobalPower (a local flip diverges → StateHash
desync). (2) Fixture NetIds minted server-side ONLY, in deterministic map-file order. (3) adding the def
bumps content-hash — both sides register or `ById` returns null and the fixture FAIL-CLOSED doesn't
materialize (a missing render, never a desync). (4) `Solve()` is pure/deterministic. (5) worldgen
byte-identity — fixtures RECORDED not spawned inline; match by QUANTIZED position (exact key).
**Depends on:** none (Version). But it DOES bump content-hash → ship with the content-def cluster (§4), not
truly free-standing. **Risk: medium.**

### A4 — Crops client-view (entity, no protocol)

**Gap:** `SystemCrops(11)` + `CommandPlantCrop(24)`/`CommandHarvestCrop(25)` + events already replicate; a
joined client already populates `Client.Crops` from snapshots — but there's NO `CropReplicaView` and the
plant/harvest send calls have ZERO call sites, so a joiner can't see/plant/harvest crops. All new artifacts
are game-side.
**Protocol change: NONE.** No SystemId/command/event/field/version bump.
**Files:** **NEW** `game/CropReplicaView.cs` (copy `WorldItemReplicaView` as the diff-driven template; walk
`Client.Crops.All`, resolve the crop name via `CropRegistry.TryBySeed`, `CropNode.Spawn`, stamp
`node.NetId`, AddToGroup `crop`, `GlobalPosition = e.Pos + ResetPhysicsInterpolation()`, each tick derive
`grown = Client.Crops.IsGrown(e, Client.Applier.LastAppliedServerTick)` and `SetGrown` DIRECTLY — no
CropManager clock on the client; retire diff-driven); `game/CropNode.cs` (`uint NetId` + `bool Grown =>
_lastGrown` so the harvest scan picks grown replicas without a `PlantedCrop.Crop`); `game/PlayerController.cs`
(seams `NetHarvestCrop`/`NetPlantCrop`; `RequestHarvestNearestCrop()` scans `crop` for the nearest grown
NetId!=0 within ~3 m and invokes the seam; insert `else if (RequestHarvestNearestCrop()) {}` BEFORE the
direct `CropManager.NearestGrown` branch @2501); `game/ClientWorldSession.cs` (`Crops = new CropReplicaView`
+ wire `NetHarvestCrop`/`NetPlantCrop` to the Client.Send* calls); `game/DevConsole.cs` (plant verb: when
`!CropManager.Active && RemoteClient!=null`, resolve seedId + `SendPlantCrop`).
**Server:** NO server change — `OnPlantCrop`/`OnHarvestCrop` already own the full authoritative path (seed
spend, mint, reach+ripeness validate, replicated yield, server-owned AGRICULTURE roll, server XP);
`CropNetSync` registers the schema on the dedicated server.
**Client:** `CropReplicaView` is the SOLE materializer on the joined client (there is no CropManager, so the
SP direct branch never fires); growth is DERIVED from `Client.Crops.IsGrown(e, LastAppliedServerTick)`;
plant/harvest route as intents; the yield reflects through the already-present `WorldItemReplicaView`.
**Tests (teeth):** L1 `unify.crop_view` (materialize a CropNode with `node.NetId == entity NetId`, then
step past GrowthSeconds*50 and assert `node.Grown` flips — the tick-derived path); L1
`unify.crop_client_harvest` (RequestHarvestNearestCrop → despawn + a visible+focusable yield puppet). The
existing L0 `WorldStateReplicationTests.PlantCommand_...` is ALREADY green, confirming zero server/wire work.
**Determinism:** NOT worldgen (player-planted; zero wire bytes change → content hash unchanged). Growth is
DERIVED both sides from `(LastAppliedServerTick - PlantedAtTick)` vs the content-hash-matched schema — read
`Client.Crops.IsGrown(e, LastAppliedServerTick)`, never a wall/frame clock. Iteration is NetId-sorted; the
view rolls NO RNG (the second-yield stays server-side); node yaw is NetId-derived. DOUBLE-AUTHORITY GUARD:
the view is CLIENT-ONLY — do NOT add it to MpLoopback (the loopback host owns real CropManager nodes; a view
there double-renders every crop — the P2b passive-loot doubling failure).
**Depends on:** none. Enables B3 (the loopback harvest seam). **Risk: medium.**

### A5 — Animals/wildlife MP replication (physics-body, PROTOCOL: new SystemId 15)

**Gap:** SP-only `AnimalField`, keyed on the LOCAL PlayerController; zero animal replication (no SystemId/
NetSync/view). Dedicated skips it.
**Protocol change: YES.** New **`SystemAnimals = 15`** (13 reserved, 14 = containers). No new command, no
new event (despawn/stream-out rides the snapshot removed[] list like zombies). The animal wire block is
byte-shape-identical to the zombie block (NetId + Pos + Yaw + AnimState + Kind byte) — reuse
`PlayerReplication.Quantize` + `NetWire` verbatim, NO new quantization.
**Files:** `PlayerReplication.cs` (`SystemAnimals=15`, comment: 13 reserved, coordinated bump); **NEW**
`core/UnturnedNet/AnimalReplication.cs` (copy `ZombieReplication` near-verbatim; swap the Speciality byte
for a Kind byte; drop death/crawler); `NetProtocol.cs` (Version + changelog + re-golden wire goldens);
`NetWorldHost.cs` (`Animals` on server+client; append to Composer[]/Applier[] in the SAME position both
sides; `Animals.ForgetClient` on disconnect; do NOT add to `EnableSyncCheck`); `game/AnimalAgent.cs`
(`IsPuppet` + `Id`; `_Ready` AddToGroup `animals` only if !IsPuppet; early-return AI when IsPuppet;
`PuppetFrame` mirroring `ZombieController.PuppetFrame`); **NEW** `game/AnimalCatalog.cs` (hoist the id→
(rig,tex,foot) table + a static `BuildRig(id)` so server body + client puppet build byte-identical species);
`game/AnimalField.cs` (C4-generalize streaming onto `PlayerRegistry` via `GatherAnchors`; set `agent.Id`
at spawn; use `AnimalCatalog.BuildRig`; keep it OFF `GD.Randi`); **NEW** `game/AnimalNetSync.cs` (mirror
`ZombieNetSync` MINUS damage: gate on `tick % 4` = 12.5 Hz, walk `animals`, Mint + `ServerSpawn(id, kind,
pos)`, derive Idle/Walk from displacement, `ServerPublish`, retire freed agents); **NEW**
`game/AnimalPuppets.cs` (mirror `ZombiePuppets`: walk `Client.Animals.All`, one `IsPuppet` AnimalAgent per
NetId built via `AnimalCatalog.BuildRig(e.Kind)`, `PuppetFrame`, free retired); `game/WorldBuilder.cs`
(Dedicated branch: add an `Animals` phase with `AnimalField{Player=null}`; Client branch MUST NOT build
AnimalField); `game/DedicatedServer.cs` (`AnimalNetSync` + `net.animals.publish` step +
`Interest=RingRadius 160`); `game/MpLoopback.cs` (`AnimalNetSync` + step; NO AnimalPuppets — the host
renders its real bodies); `game/ClientWorldSession.cs` (`AnimalPups = new AnimalPuppets` — the ONLY site
that attaches animal puppets); **NEW** `tests/.../AnimalReplicationTests.cs`; `game/testing/tests/NetTests.cs`
(new L1 `net.animal_stream_sync`).
**Server:** the HOST (listen-server, Playable already spawns AnimalField with Player set) and the DEDICATED
server (NEW spawn, Player=null) keep the REAL bodies + run the cosmetic wander AI on them (never a second
body). `AnimalNetSync` (between `net.server.sim` and `net.server.replicate`) walks `animals` every 4th tick,
mints a NetId per new agent, ServerSpawns with the Kind byte, derives Idle/Walk, ServerPublishes. No
commands/events.
**Client:** NEVER builds AnimalField. A NEW `AnimalPuppets` (attached ONLY in ClientWorldSession, NOT
MpLoopback) walks `Client.Animals.All`, keeps one `IsPuppet` AnimalAgent per NetId built with the
REPLICATED Kind via `AnimalCatalog.BuildRig`, drives them through `PuppetFrame` (glide-interpolate to the
12.5 Hz snapshot, snap past 5 m, apply yaw + Walk/Idle). Puppet agents set IsPuppet=true, so `_Process` AI
early-returns and they never join `animals` → single authority.
**Tests (teeth):** L0 `AnimalReplicationTests.MixedHerd_UnderLossAndReorder_ConvergesToExactParity_Including
LateJoin` (transform+yaw+anim+KIND round-trips to exact StateHash parity through the real wire under seeded
loss+reorder + a late joiner + a removal; a deer replica reads Kind=1, a cow Kind=6); L1 `net.animal_stream_
sync` (`Client.Animals.Count==N`, exactly N tracked server-side, one IsPuppet AnimalAgent per replica with
the correct species tracked by INTERPOLATION (max per-tick step < 1.5 m), and the observer world has ZERO
nodes in `animals`). Both fail pre-fix (animals ABSENT in MP; no AnimalReplication/NetSync/Puppets).
**Determinism:** (1) shared-RNG byte-identity — `AnimalField` must stay OFF `GD.Randi` (it uses its own
splitmix Hash + a per-agent LCG) so inserting the Dedicated `Animals` phase does NOT shift the zombie
spawn-selection `GD.Randi` stream. (2) single authority / no double-body — only the server runs AnimalField;
the client builds ONLY IsPuppet bodies that skip `_Process` and never join `animals`; enforced by the
empty-group L1 tooth. (3) frame-rate wander is single-authority + cosmetic → LEFT AS-IS (SystemAnimals is
deliberately EXCLUDED from the desync StateHash check — clients receive animals and never derive them). (4)
Kind determinism — `Hash(idx)` per Fauna.dat point index. (5) reuse `Quantize`/`NetWire` verbatim.
**Depends on:** A1 (shares the ONE coordinated bump; allocate SystemContainers=14 + SystemAnimals=15
together); the PlayerRegistry C4 generalization (already landed for Zombie/Loot fields). **Risk: medium.**

**Review note + resolution (Issue #3, medium):** the spec left the SystemId ambiguous (`SystemAnimals =
14/15`). Because the Composer[]/Applier[] appends are order-sensitive and must be byte-SYMMETRIC
server↔client, ambiguity invites a hard framing break. **Resolved: hard-pin `SystemVitals=13,
SystemContainers=14, SystemAnimals=15`** per the coordinator; append the three new instances to Composer[]
and Applier[] in identical ascending-SystemId order (SyncCheck 255 last); leave SystemAnimals AND
SystemContainers OUT of `EnableSyncCheck` (both relevancy/owner filtered). Add an L0 assertion that
Composer and Applier register the same SystemId sequence.

### A6 — Replicate the rope-tow relationship (split-authority, PROTOCOL: new fields on SystemVehicles)

**Gap:** tow state (`Vehicle.Towing/TowedBy` + `TowRope`) is not replicated; a joined client sees no rope
and no coupling.
**Protocol change: YES — new FIELDS on the existing `SystemVehicles(9)` entity block, no new SystemId/
command/event.** Add `uint TowedNetId` + `float TowRestLen` to `VehicleEntity` (append after `Flags` in
`WriteEntity`/`ReadEntity`/`StateHash`). Re-golden `VehicleBlock_GoldenBytes`. This single bump is the
coordinated one B11's commands ride under.
**Files:** `core/UnturnedNet/VehicleReplication.cs` (`TowedNetId` + `TowRestLen` on `VehicleEntity`; wire
consts `TowRestIntBits=3, TowRestFracBits=4` — restLen clamped to [TowRestMin 2.0, TowAttachReach 4.5]; a
new `ServerPublishTow(id, towedNetId, restLen, tick)` modeled on `ServerPublishVitals` — quantize via
`QuantizeClampedFloat`, dirty-check both fields, stamp LastChangedTick only on change; append the wire
reads/writes; mix into StateHash; CRITICAL: do NOT touch tow in `ServerPublish` or `ServerAdoptDriverState`
— tow is a THIRD disjoint writer); `NetProtocol.cs` (Version + changelog); `game/Vehicle.cs` (expose
`_towRestLen` as `TowRestLenValue`; set puppet tow-node locals with the IDENTICAL formula the real Build
uses); `game/VehiclePuppet.cs` (`FrontTowLocal/RearTowLocal` + `FrontTowWorld/RearTowWorld` accessors);
`game/VehicleNetSync.cs` (after the held/non-held publish block, UNCONDITIONALLY — tow is field-disjoint —
resolve `v.Towing`→its Tracked.NetId and `ServerPublishTow`; the dirty-check makes a static rope cost zero
delta bytes); `game/VehicleReplicaView.cs` (a `Dictionary<uint,TowRope> _ropes` keyed by TOWER NetId; for
each entity with `TowedNetId!=0` look up both puppets and `rope.SetEndpoints(tower.RearTowWorld,
towed.FrontTowWorld, restLen)`; retire ropes whose tower has TowedNetId==0 or whose puppet is gone);
`tests/.../VehicleReplicationTests.cs` (extend the round-trip + re-golden `VehicleBlock_GoldenBytes` + the
layout comment); `game/testing/tests/VehicleTowTests.cs` (L1).
**Server:** NO new authoritative LOGIC — the tow spring stays in `Vehicle.UpdateTow` on the HOST's real
bodies. This gap adds only a PUBLISH: `VehicleNetSync` reads each node's `Vehicle.Towing` + `_towRestLen`
and mirrors it via `ServerPublishTow` (publish-only, like transform/vitals). The NODE is the single source
of truth; the entity never independently mutates tow.
**Client:** pure consume/render — `VehicleReplicaView` gains a parallel diff-driven `TowRope` layer reading
`entity.TowedNetId` + `TowRestLen`, drawing a cosmetic rope between the two puppets' tow-node transforms.
The loopback HOST keeps its own real `Vehicle._rope` (SP path) and runs no ReplicaView for its own cars →
never a double rope.
**Tests (teeth):** L0 `TowRelationship_RoundTrips_HashParity` (survives full+delta, hashes equal with tow
set); L0 `VehicleBlock_GoldenBytes` (re-goldened — the golden is 6 bytes shorter pre-fix, forcing the
Version bump to land in the same commit); L1 `vehicle.rope_tow_replicates` (a host AttachTow lands as
`entity.TowedNetId`+`TowRestLen` on a client replica → exactly one TowRope). Pre-fix `VehicleEntity` has no
tow fields (won't compile) and `VehicleNetSync` never publishes tow.
**Determinism:** worldgen none (runtime relationship). StateHash byte-identity: `TowedNetId` (raw u32) +
`TowRestLen` (quantized in `ServerPublishTow` with the SAME `QuantizeClampedFloat` the wire uses) mirror
the Fuel/Health pattern → server + client hash identical bytes (SystemVehicles is globally mirrored → the
desync check stays green). Double-authority: the node is the sole tow writer, the entity is publish-only,
tow fields are DISJOINT from transform + vitals writers. The reverse direction ("am I towed") is DERIVED on
the client, never replicated/hashed. **Avoid: do NOT also write `TowedByNetId` to the wire** (redundant → a
hash-divergence footgun) — derive it.
**Depends on:** none. Blocks B11 (the tow commands apply to real nodes; the RESULT rides these fields).
**Risk: medium.**

---

## 4. The ONE coordinated NetProtocol.Version bump + allocation table

**Current `Version = 10` → target `11`, bumped exactly ONCE.** Land ALL eight protocol-touching gaps
(B2, B5, A2, A6, B11, A1, A5, B10) on one integration branch and re-golden every affected byte test in the
same final commit. **Never bump per-gap** — that reprises the v8/v9 bite (each interim Version
version-rejects the live launcher and fragments the population). No new EVENT ids are needed by any gap
(event registry 1–31 untouched).

### 4.1 SystemId allocation (append-only; 1–12 unchanged; 255 = SyncCheck, last)

| SystemId | Name | Gap | Sync-check? | Notes |
|---|---|---|---|---|
| 13 | SystemVitals | B5 | **EXCLUDED** (owner-only) | consumes the slot reserved at `NetProtocol.cs:54` |
| 14 | SystemContainers | A1 | **EXCLUDED** (relevancy-filtered — see Review #1) | not 13 |
| 15 | SystemAnimals | A5 | **EXCLUDED** (clients receive, never derive) | pair-allocated with 14 |

Append the three new `IReplicatedSystem` instances to `Composer[]` (server, `NetWorldHost.cs:67-69`) and
`Applier[]` (client, `:413-415`) in **identical ascending-SystemId order**, SyncCheck(255) still last. Add
an L0 assertion that Composer and Applier register the same SystemId sequence (Review #3).

### 4.2 Command allocation (28–31; highest in use pre-bump = `CommandPlayerState = 27`)

| CommandId | Name | Gap | Payload |
|---|---|---|---|
| 28 | CommandPickupDeployable | B2 | `{uint NetId}` |
| 29 | CommandExtractFuel | A2 | `{uint PumpNetId}` |
| 30 | CommandAttachTow | B11 | `{uint TowerNetId, uint TowedNetId}` |
| 31 | CommandDetachTow | B11 | `{uint NetId}` (either end) |

### 4.3 Collision remaps (Review #2 — the deconfliction that MUST be honored)

The per-gap specs hardcoded COLLIDING ids. `CommandRegistry` dispatches by the byte id, so a duplicate
`Register(28,...)` means the last registration wins and the other command's datagrams silently mis-dispatch.
Implementation MUST follow the canonical ids above. Recorded remaps:

| Gap | Spec-string id | Canonical id |
|---|---|---|
| A2 CommandExtractFuel | 28 | **29** |
| B11 CommandAttachTow | 28 | **30** |
| B11 CommandDetachTow | 29 | **31** |

### 4.4 New fields on existing blocks

| Block | Field | Gap |
|---|---|---|
| SystemVehicles(9) / VehicleEntity | `TowedNetId : uint32` (append after Flags) | A6 |
| SystemVehicles(9) / VehicleEntity | `TowRestLen : ClampedFloat 3int/4frac` (`QuantizeClampedFloat`) | A6 |
| SystemPlayerCombat(2) / CombatEntity | `Worn[7] : uint16 x7` (append after kills/deaths) | B10 |
| SystemPlayerCombat(2) / CombatEntity | `HeldItemId : uint16` | B10 |
| SystemPlayerCombat(2) / CombatEntity | `Stance : byte` | B10 |
| CommandPlayerState(27) / PlayerStateCommand | `HeldItemId : uint16` (append after Grounded, before the v10 event carry) | B10 |

Plus `NetQuantization.QuantizeUnsignedNormalizedFloat(v,bits)` (B5) so the SystemVitals owner block hashes
the round-tripped wire value.

### 4.5 Content-hash def cluster (separate from Version; settle ONCE)

A3 GridSource def + A2 GasPump def + A1 ContainerKindDefs + A5 animal defs all change `NetContent.Hash`,
NOT `NetProtocol.Version`. Coordinate them into the same content settle so the content hash bumps once, not
per-def. A3 is zero-Version but DOES bump content-hash → ship it with this cluster, not free-standing.

### 4.6 Final-commit checklist

Flip `Version = 11` + ALL golden re-baselines together (`VehicleBlock_GoldenBytes`, CombatEntity /
PlayerStateCommand goldens, B5/A1/A5 snapshot goldens) and update the `NetProtocol.cs:54` changelog: the
v8-reserved note → "landed v11/SystemId 13", plus a v11 line describing SystemVitals(13) + SystemContainers
(14) + SystemAnimals(15) + vehicle-tow-fields + combat-appearance-block + PlayerStateCommand.HeldItemId +
CommandPickupDeployable/ExtractFuel/AttachTow/DetachTow.

---

## 5. Implementation ordering

Two waves. Wave 1 (zero-Version) ships independently and can land before the bump. Wave 2 is the single
coordinated `Version 10→11` content. Within each wave, respect the dependency arrows.

### Wave 1 — zero-protocol shipped bug-fixes + views (NO Version bump, ship first)

These fix the SHIPPED default solo game (consume is the default) and add zero-wire client views. Order:

1. **B1** — CloseCrate guard (storage item-loss). *No deps.*
2. **B4** — UseSelected → NetConsume (consume resurrect). *No deps.* (Unblocks B5's dashboard-Use raise.)
3. **B6** — combat seam-wiring in loopback. *No deps.*
4. **B7** — skills seam-wiring + adoption in loopback. *No deps.*
5. **B8** — vehicle seat-arbitration tick-ordering + regression test. *No deps.*
6. **A4** — `CropReplicaView` + client plant/harvest input + the `NetHarvestCrop` seam declaration. *No deps.*
7. **B3** — loopback harvest routes over the (already-complete) crop wire. *Depends on A4.*

Zero-Version but content-hash (ship with the Wave-2 content-def settle, §4.5, so the hash bumps once):

8. **A3** — GridPowerSource fixture. *No Version dep; content-hash only.*

Zero-Version but behaviorally gated on Wave-2 content (lands with A1):

9. **B9** — containers openable on a joined client. *Depends on A1 (SystemContainers on the wire).*

### Wave 2 — the coordinated Version 10→11 content (bump once, at the end)

Land on one integration branch, in dependency order, then flip `Version=11` + re-golden in the final commit
(§4.6):

- **Registry reservations first** (pure append, no behavior, no golden move): `SystemVitals=13`,
  `SystemContainers=14`, `SystemAnimals=15`; `CommandPickupDeployable=28`, `CommandExtractFuel=29`,
  `CommandAttachTow=30`, `CommandDetachTow=31`. Append the three new systems to Composer[]/Applier[] in
  SystemId order, symmetric. **B5's SystemId 13 must be allocated BEFORE A1/A5 take 14/15** so the arrays
  stay contiguous and the reserved-slot note resolves.
- **New-field wire edits on existing blocks** (these move goldens): **A6** (VehicleEntity TowedNetId +
  TowRestLen, publish-only via `ServerPublishTow`, the disjoint third writer); **B10** (CombatEntity 7 worn
  + held + stance, symmetric StateHash; PlayerStateCommand.HeldItemId). **B5** adds
  `QuantizeUnsignedNormalizedFloat`.
- **Dependency-ordered gap bodies:** **A6 → B11** (tow commands apply to real nodes; the result rides A6's
  fields, so A6's publish must exist first). **A3 → A2** (A3's FixtureKind/DeployableDef + WorldBuilder.
  Fixtures precede A2's ExtractFuel + GasStationServer). **A1 → A5** (coordinated 14/15 pair) and **A1 →
  B9**. **A4 → B3** (Wave 1). **B2** and **B5** are standalone.
- **Final commit:** flip `Version=11` + all golden re-baselines + the `NetProtocol.cs:54` changelog update,
  together.

Separately, coordinate the content-hash def cluster (A3/A2/A1/A5 defs, §4.5) into the same content settle so
`NetContent.Hash` bumps once.

---

## 6. Review verdict + open risks

**Overall verdict: needs-changes → resolved.** The adversarial review returned four issues; all are folded
into the gap sections above and the allocation tables, and none survives as an open blocker:

- **#1 (high, A1):** SystemContainers must NOT go into `EnableSyncCheck` — it is relevancy-filtered, so
  server-full-hash vs client-nearby-subset guarantees a continuous false `DesyncDetected`. **Resolved:**
  struck the "fixture MAY be in sync-check" note from A1 + the coordinator; the sync-check list is unchanged.
- **#2 (high, A2/B11):** colliding CommandIds (three gaps wrote 28, two wrote 29). **Resolved:** canonical
  28/29/30/31 (§4.2) with the remap table (§4.3); the specs' id strings are corrected in-line.
- **#3 (medium, A5):** ambiguous `SystemAnimals=14/15`. **Resolved:** hard-pinned 13/14/15 (§4.1) + an L0
  Composer/Applier-sequence assertion.
- **#4 (low, B1):** duplicate B1 entries. **Resolved:** deduped to a single B1.

**Determinism verdict (from the review, verified against source): holds up.** (1) `SystemResources(12)` is
keyed by manifest×.bin load order, which no gap touches — recording fixtures (A1/A3/A2) or adding the
Dedicated Animals phase (A5) does not reorder it. (2) The global `GD.Randi()` stream `ZombieField`
consumes is NOT touched by any new server spawn: container loot rolls go through `LootTables._rng` / a fresh
local RNG, and AnimalField uses its own splitmix Hash — so moving container/animal rolls to server-init
provably does not shift zombie selection. (3) B5's tick order is correct: `DamagePlayerExternal` is a queued
sink drained at the top of `Combat.Step`, so `Vitals.ServerStep` between `VehicleHost.Step` and `Combat.Step`
lands starvation damage same-tick; HP double-authority is avoided by re-seeding `Sim.Health` from
`CombatState.HealthExact` each tick. Two-body / bare-GlobalPosition / owner-re-sim surfaces are clean: B8,
B11, A5 keep the host's real body and only build remote puppets; `RemotePlayers` skips self; A4/B3 keep
`CropReplicaView` off the loopback.

**Open risks to watch during implementation:**
1. **The double-spawn gate (A1/A2/A3) is load-bearing.** The `Main.cs` SP-local container/fixture spawners
   MUST be gated off under `--spconsume` so the `*ReplicaView` is the SOLE node source; a slip becomes a
   double-roll + double-visual regression (the P2b passive-loot doubling trap). Enforce with the empty-
   local-group L1 teeth.
2. **B6 is a solo-feel change (P2).** Wiring host shots to the wire resolves hits against 12.5 Hz zombie
   positions (favor-shooter delta) — wire the fx consumers or the host loses hitmarkers/blood/decals.
3. **B10 render-verify.** The CharacterModel→RiggedCharacter puppet swap needs a headless render-verify
   (readability + proportions), and any L2 golden touching remote avatars must be re-baselined intentionally.
4. **B5 stamina↔sprint split** carries a few ticks of adopted-stamina lag vs client sprint (acceptable,
   matches HP adoption) and REQUIRES MpLoopback to pack stance into `SendMoveInput` (currently buttons=0).
5. **A6 hash footgun:** derive "am I towed", never replicate `TowedByNetId` — a redundant field is a
   hash-divergence source on a globally-mirrored system.
6. **The single bump is all-or-nothing:** every Version-touching gap and every affected golden must land in
   the one final commit; a partial bump version-rejects the live launcher and fragments the population.
