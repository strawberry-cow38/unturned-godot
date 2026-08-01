# DUPLICATE_AUDIT

Every place the codebase implements the same thing twice, from a full sweep of
`core/UnturnedSim/`, `core/UnturnedNet/`, `core/SDG.*/`, `game/`, and `tools/`.

Sorted by **what it would cost to be wrong**, not by size. Some of these are deliberate
and should stay — a faithful port duplicates what the source duplicates, and an SP/MP
split is sometimes the whole design. The tiers exist so the deliberate ones can be
marked as such and stop being re-reported by the next sweep.

Status key: **OPEN** (no decision yet) · **KEEP** (deliberate, confirmed) · **FIXED**.

**Verdict (strawberry, 2026-08-01):** *"keep everything that looks deliberate, or is src
accurate, then fix all the rest."* So every Tier 3 row is **KEEP** unless it is later shown
not to be a faithful port — they are left listed so the next sweep does not re-report them.
Tiers 1 and 2 are in scope for fixing.

**Lane note (cow tools, 2026-08-01):** anything in world/terrain/render/survival/UI/menus that
is not dead-obvious mechanical gets confirmed with them first. They cleared 1.5/1.6/1.7/1.9 and
supplied two catches that went straight in: the 4th `RotateYTo` in `TowRope.cs`, and the warning
that `RoadField`'s length is a bezier arc estimate that must stay OUT of the polyline dedup.

---

## Tier 1 — mechanical, no behaviour change

Pure extractions. Each is provably identical today; the risk is only in the mechanics of
the edit, not in the decision.

| # | What | Where | Status |
|---|---|---|---|
| 1.1 | `Clamp1(v)` — clamp to [-1,1] | `PlayerReplication.cs:198`, `Prediction.cs:191`, `CombatReplication.cs:395`, `VehicleReplication.cs:464` **and** `:536` (same file twice) | **FIXED** ae3e2c7d — `Mathf.Clamp(v,-1f,1f)` |
| 1.2 | `Clamp01` hand-rolled where the shim is already imported | `PlayerVitalsReplication.cs:215` | OPEN |
| 1.3 | `PruneTombstones` — the same 6-line loop | **ten** copies: `PlayerReplication:580`, `CombatReplication:565`, `ZombieReplication:234` + `:400`, `AnimalReplication:216`, `WorldItemReplication:306`, `ContainerReplication:209`, `WorldReplication:393`, `VehicleReplication:412`, `DeployableReplication:702` | **FIXED** ae3e2c7d — `ReplicationUtil.PruneTombstones` |
| 1.4 | `SortedIds` — copy `registry.Ids` to a list and sort | **nine** copies; `DeployableReplication.cs:711` is already the generic version the others could call | **FIXED** ae3e2c7d — `ReplicationUtil.SortedIds`/`SortedKeys` |
| 1.5 | `RotateYTo(Vector3)` | `ConnectionPort.cs:161`, `Wire.cs:58`, `Hose.cs:58` | **FIXED** ae3e2c7d — `NodeGeometry.RotateYTo` (4th copy in TowRope.cs, found by cow tools) |
| 1.6 | `CollectMeshes(Node, List)` | `Deployable.cs:242`, `Vehicle.cs:1720`, `VehiclePuppet.cs:110` | **FIXED** ae3e2c7d — `NodeGeometry.CollectMeshes` |
| 1.7 | Lazy world-manager creation | `PowerManager` **5×** (`Deployable:237`, `GasPump:62` + `:86`, `GridPowerSource:93` + `:120`); `FluidManager` 2× (`FluidDeploy:37`, `FluidFuelInlet:35`) | OPEN |
| 1.8 | `Kind(DeployableDef.PortKind)` → `PowerPortKind` | `PowerNet.cs:84`, `DeployableNetSchema.cs:38` — byte-identical switches | OPEN |
| 1.9 | Polyline length | `Wire.cs:50` + `Hose.cs:50` (**both uncalled**) and the live private `PolyLen` in `PlayerController.cs:392` | **FIXED** ae3e2c7d — `NodeGeometry.PolylineLength` |
| 1.10 | `Stamp(tick) => tick + 1` | 8 private copies + 2 inlined; `ZombieReplication`/`AnimalReplication`/`VehicleReplication`/`PlayerReplication` stamp the **raw** tick with no +1 — that inconsistency is load-bearing and undocumented | OPEN |
| 1.11 | `NetQuantization.Quantize*` — 4 copies of one writer→reader round-trip, **2 allocations per call to quantize one float**; `WorldReplication.cs:57` is a 5th, inlined | OPEN |
| 1.12 | `DirtyRingDepthTicks` declared twice | `NetQuantization.cs:32`, `SnapshotComposer.cs:54` (explicit alias) | OPEN |
| 1.13 | Trigger-sense threshold `Live >= 1f` as a bare literal | `Deployable:588`, `FluidPump:58` + `:59`, `FluidValve:33` + `:34` | OPEN |

## Tier 2 — the same idea, written twice, and they have **drifted**

These are the ones worth reading carefully. In each case the two implementations were
meant to agree and no longer do.

| # | Divergence | Detail | Status |
|---|---|---|---|
| 2.1 | **Power `PowerScale` is SP-only** | `PowerNet.cs:50` scales output ports by `d.PowerScale`; `DeployableReplication.cs:522` does not scale at all. A generator mid-spin-up produces full 4000 W server-side and `4000 × _powerLevel` in SP; a wind turbine produces a flat 2500 W server-side vs `2500 × _windFactor` (0..2) in SP | **BLOCKED** — needs a decision, see note below |
| 2.2 | **A Power Switch toggled OFF still conducts server-side** | `PowerNet.cs:45` passes `Conducting = d.PowerConducting`; `DeployableReplication.cs:520` leaves it at its `true` default. Everything downstream of an off switch reads powered in MP and dark in SP. `DeployableEntity.ToggledOn` exists and would carry it — nothing maps it | **FIXED** — `Conducting = !def.IsSwitch \|\| e.ToggledOn` in `Solve()` |
| 2.3 | **A Power Switch can never be toggled through the server** | `Deployable.CanTogglePower:52` has an `IsSwitch` branch; `DeployableReplication.CanToggle:394` requires `FuelCapacity > 0`, which a switch (`Fuel = 0`) never has | **FIXED** — `CanToggle` accepts `def.IsSwitch` |
| 2.4 | **Battery + wind turbine "producing" is fuel-generator logic only** | `Deployable.IsPowered:53` is a 3-way family rule (battery on `Energy`, turbine on `_windFactor`); `DeployableEntity.Producing:295` is `ToggledOn && !OnFire && (FuelCapacity <= 0 \|\| Fuel > 0)`. Battery `Fuel = 0` ⇒ produces whenever toggled; its whole charge/discharge economy is SP-only | OPEN |
| 2.5 | **Wire legality: same 5 rules, incompatible reach** | SP `PlayerController.cs:418` = 5.5 m look reach + 40 m/20-node polyline cap. Server `DeployableReplication.cs:371` = 16 m per endpoint, **no length cap**. A wire the SP UI refuses is accepted by the server | OPEN |
| 2.6 | **Door arc-block test is SP-only** | `Door.cs:96` passes the real physics `blocked`; `InteractableReplication.cs:217` hardcodes `arcBlocked: false`. A close the SP client refuses is accepted by the server. (Documented as deliberate — the server has no arc test and letting a client veto other people's doors is worse — but it *is* the one genuine rule divergence in the door path) | OPEN |
| 2.7 | **`IsAlive` out-of-range fails opposite ways** | `DestructibleReplication.cs:60` → `false` (dead); `DestructibleField.cs:49` → `true` (alive) | OPEN |
| 2.8 | **Two hit-geometry implementations that disagree** | `ZombieCombat.RayCapsule` (real capsule caps) vs `ServerCombat.SegmentHitsCylinder` (cylinder + `[-0.1, top+0.15]` fudge). The latter decides MP damage, so SP and MP disagree at the shoulders and at point-blank | OPEN |
| 2.9 | **Limb banding in three files** | `ZombieCombat.LimbAt:95` (0.82/0.45 fractions), `ZombieController.cs:166` (own hardcoded `h`), `ServerCombat.cs:105` (`ZombieHeadFrac`). Players use *absolute metres* (`PlayerHeadMinY 1.45`) where zombies use fractions | OPEN |
| 2.10 | **Zombie attack reach maintained twice** | `ZombieSim` (`AttackRange` 1.75 m from the kind record) vs `ZombieController.cs:42` (`ATTACK_PLAYER_SQ = 2f` ⇒ √2 ≈ 1.41 m). Vertical 2.1 matches; horizontal does not. Two complete zombie combat brains | OPEN |
| 2.11 | **yaw→forward copied 3×, one shipped inverted** | `ServerTransactions.cs:848` (wrapped in a method), `:534`, `ServerCombat.cs:453`. The melee copy was `(+sin, +cos)` and hit 180° **behind** the attacker until fixed | OPEN |
| 2.12 | **Two `BedClaims` tables for the same beds** | static `Bed.Claims` (SP + client) and `ServerInteractables._beds` (authority), keyed differently (`BedId` int vs `NetId`), bridged only by `ApplyReplicatedClaim` | OPEN |
| 2.13 | **Vehicle exit-spot basis derived twice** | `VehicleReplication.cs:928` and `ClientWorldSession.cs:560`. The client copy going stale against a frozen replica is the documented root cause of `docs/EXIT_POSITION_ROOTCAUSE.md`; `VehicleExitedEvent.Pos` exists because of it, and the client copy is still there as a fallback | OPEN |
| 2.14 | **Station fill percent computed twice** | `GasStationServer.Percent:36` and `ServerTransactions.cs:427` re-derive the identical clamp. `Percent` consequently has no callers | OPEN |
| 2.15 | **Extract-fuel implemented twice** | `GasPump.Extract:109` (SP, relies on `FluidTank.Drain`'s internal clamp) vs `ServerTransactions.OnExtractFuel:399` (computes the `min` explicitly, plus its own "which can" scan) | OPEN |
| 2.16 | **Salvage yield encoded in 3 places** | `Deployable.cs:496` (2× item 67 hardcoded), `Vehicle.cs:1820` (3× hardcoded), `DeployableNetSchema.cs:32` (`SalvageItemId`/`SalvageCount`) | OPEN |
| 2.17 | **`DisconnectWires` — 4 copies, 3 of which fix a bug the 4th has** | `Deployable.cs:504`, `FluidPump.cs:85`, `FluidPurifier.cs:53`, `FluidValve.cs:40`. The three fluid versions `RemoveFromGroup("wires")` before `QueueFree()` so the group is correct *this frame*; `Deployable.DisconnectWires` does **not**, and `PlayerController.cs:1016` explicitly documents needing that | **FIXED** — `RemoveFromGroup("wires")` before `QueueFree` |
| 2.18 | **`SetLookFocused` mesh collection differs** | `Vehicle` and `VehiclePuppet` re-collect on every focus; `Deployable` collects once and never refreshes — so a `Deployable` that gains a mesh after first focus (battery label, split turbine hub) is missed | OPEN |
| 2.19 | **Pump-lift ceiling propagation written twice** | `FluidNet.cs:83` (inside `WouldNeedPump`) and `:153` (inside `Tick`); any change to the conduction rule must be made in both | OPEN |
| 2.20 | **Deadzone host loop duplicated** | The *sim* is correctly shared, but the volume list, `TryGetVolume` scan, per-player dictionary and enter/exit bookkeeping are near line-for-line in `game/DeployableField`-side `DeadzoneField.cs` and `core/UnturnedNet/ServerDeadzones.cs` | OPEN |
| 2.21 | **`GodotCompat.cs` — the documented handedness-flip adapter — has exactly ONE call site** (`Main.cs:534`) while everything else converts inline. Either every inline conversion is a latent sign bug, or the adapter is dead code. **This either/or should be resolved before more cross-boundary code is written** | OPEN |

## Tier 3 — probably deliberate; confirm before touching

| # | What | Why it may be intentional | Status |
|---|---|---|---|
| 3.1 | `PowerSolver` ≡ `FluidSolver` (~200 lines each, line-for-line) | `FluidSolver`'s own header calls it "a clean mirror of the power net". Generic-ifying it couples two subsystems that currently evolve independently | KEEP |
| 3.2 | `ResourceReplication` ≡ `DestructibleReplication` (~200 lines, byte-identical wire shape) | Wants one generic `AliveBitmapReplication`; the cost is a wire-format-adjacent refactor | KEEP |
| 3.3 | `ZombieReplication` ≡ `AnimalReplication` | Zombies have `ServerSetAnim` + the `HeightFor` hitbox pair that animals lack | KEEP |
| 3.4 | The 4 relevancy-ringed `WriteFull`/`WriteDelta` bodies | Hand-copied, but each is on a hot path and the wire bytes are golden-tested | KEEP |
| 3.5 | `Items.checkSpaceEmpty` / `checkSpaceDrag` / `checkSpaceSwap` | Faithful port — they mirror the source method-for-method. Note `checkSpaceEmpty` has **zero callers anywhere** | KEEP |
| 3.6 | `Items.getItem(x,y)` ≡ `getIndex(x,y)` | Same loop, different return + sentinel. Also a faithful port | KEEP |
| 3.7 | The 4 `PlayerInventory` page walks (`getItemCount`/`peekItemQuality`/`removeItemAmount`/`restoreQuality`) | The **scan order is a contract** between two of them — `peekItemQuality` documents that it must match `removeItemAmount`. A shared enumerator would make that safe rather than incidental | KEEP |
| 3.8 | `BedClaims.Claim` vs `Adopt` | The divergence is intentional and documented (a replica must not re-judge a remote decision against a local clock); only the release-previous block is copy-pasted | KEEP |
| 3.9 | `PlayerAuthority.OnPlayerState` vs `VehicleReplication.OnVehicleState` | The envelope *math* differs deliberately (leaky token bucket on foot vs per-packet speed×elapsed); the surrounding state machine is what's duplicated | KEEP |
| 3.10 | 3 owner-only `WriteFull`/`WriteDelta`/`StateHashFor` scaffolds (Skills / Inventory / Vitals) | Only the per-entry payload differs | KEEP |
| 3.11 | `ClothingDef` ≡ the clothing block of `ItemAsset` | `ClothingDef` parses from a real `.dat` and has **zero callers**; the shipping path loads the same numbers from `content/clothing_armor.tsv` into `ItemAsset`. One of the two is dead | KEEP |
| 3.12 | `ZombieRegionBounds.Contains` vs `DeadzoneVolumeDef.Contains` | Genuinely different: XZ-only + half-open (regions tile) vs 3D + fully inclusive (volumes overlap on a face). Easy to grab the wrong one, but both are correct | KEEP |
| 3.13 | `SetTowGhost` ≡ `NetGhost` (layer bit0→bit6) | `NetGhost`'s own comment calls it "the `SetTowGhost` trick" | KEEP |
| 3.14 | Wreck lifecycle in `Vehicle` and `Deployable` | Same 0/40/60/360 s state machine twice; the vehicle fades particle transparency and the deployable disconnects wires, so neither is a superset | KEEP |
| 3.15 | Flat→upright fixture basis, 3 copies with a **sign flip** | `GasPump:76` and `GridPowerSource:110` use `Basis(Right, -90°)`; `DeployableDef.StandBasis:371` uses `+StandRotX` (default +90). The two families have different mesh provenance (world objects vs barricades) — but nothing in the code says so | KEEP |
| 3.16 | `SeaLevel` declared twice (`DeployableDef.cs:63`, `Deployable.cs:45`) | The second's comment already acknowledges it | KEEP |
| 3.17 | Container display hashing — 3 FNV folds over the same value | `ContainerReplication.StateHash:159`, `StorageReplicaView.DisplaySig:74`, `ContainerNetSync.GridSig:94` | KEEP |
| 3.18 | "Is this thing powered" — 6 variants | 4 are the same "does my single Consumer port report `Powered`" with escalating extra gates; 2 (`GridPowerSource`, `Deployable`) mean something else entirely | KEEP |
| 3.19 | Port/wire lookup scans — `PortWired`/`WireOnPort`/`PortHosed`/`HoseOnPort`/`FarPort`/`PortFor`/`HosePortForNode` | 7 private O(n) scene-group scans, several exact pairs. `HosePort.Node` already exists as a back-pointer, so `PortFor` is an O(n) search for something a field could answer | KEEP |

---

## Dead code found along the way

Not duplicates, but surfaced by the same sweep and worth a decision.

**Provenance:** every claim below was re-verified mechanically after the sweep — comment
lines stripped, tests excluded, declaration lines excluded, and receiver-typed where the
name is declared on more than one class. Four entries in the first draft of this list were
wrong and are corrected here; the reader-reported "no callers" claims they came from turn
out to be unreliable in both directions (see the note at the end of this section).

Confirmed dead — no non-comment production reference anywhere:

- `core/UnturnedSim/Layers.cs` — the full verified 32-slot retail layer table, **zero
  non-comment references outside its own file**, while the game uses an unrelated ad-hoc
  numbering. Looks authoritative; is not used. Adopt or delete.
- `Mathf.Repeat` — no callers, and it is the *correct* positive modulo while
  `game/WorldItemReplicaView.cs:79` hand-writes a plain `%` that is wrong for negatives.
- `NetMaxValue` — no references.
- `ItemAsset.ParseSize` — no callers anywhere, tests included.
- `Items.checkSpaceEmpty`, `Wire.TotalLength`, `Hose.TotalLength`,
  `GridPowerSource.IsPowered`, `GridPowerSource.Tooltip`, `Deployable.SwitchOn`.
- `HosePort.Deactivate` — dead. Note its twin `ConnectionPort.Deactivate` **is** live
  (`game/Deployable.cs:519`); the two share a name, so an untyped grep reports the pair
  as reached and hides this one.
- `Read/WriteRadians` — identical to the Degrees pair modulo one constant; no callers.
- `ReadEnum`/`WriteEnum`/`NetEnumAttribute` are inside `#if UNITY_EDITOR` — **not
  compiled**.

Corrected — these were listed as dead and are not:

- `Mathf.Clamp01` — **used**, 4 sites, all inside `core/SDG.Compat/UnityMath.cs`'s own
  `Lerp` overloads. The accurate statement is "no callers *outside the shim*", which is
  still the point: `PlayerVitalsReplication.cs:215` hand-rolls a private copy in a file
  that already imports the shim.
- `Mathf.Deg2Rad` — **used**, `UnityMath.cs:106-108` (`Quaternion.Euler`). Again the real
  finding is external: 5 gameplay sites write `Mathf.PI / 180f` instead.
- `NetLength` — **used as a parameter type** on the `ReadList`/`WriteList` overloads. What
  is true is narrower and stranger: `new NetLength(...)` appears **nowhere in the repo,
  tests included**, so those overloads can never have been called. The `bitCount 0` footgun
  is real but unreachable.
- `GasStationServer.Percent` — **used** at `GasStationServer.cs:31`, in its own file. The
  finding is that `ServerTransactions.cs:427` re-derives the identical expression rather
  than calling it (see 2.14), not that it is dead.

**Do not trust a bare "no callers" claim in a review without re-running the check.** Of the
cleanly-adjudicable disagreements between the readers that produced this document and a
mechanical scan, the mechanical scan was right every time: `Crafting.CanCraft` (live at
`game/inventory/BlueprintRegistry.cs:36`), `ItemAsset.IsConsumable` (five live sites incl.
`PlayerController.cs:1410`), and `WorldItem.BuildItemPuppet` (live at
`game/WorldItemReplicaView.cs:75`) were each reported as callerless and are not.

---

## 2.1 is blocked on a decision, not on effort

`PowerScale` cannot be mirrored server-side the way `Conducting` was, because neither input
to it is replicated or derivable today:

- **generator ramp** (`_powerLevel`, 0..1 over `WarmupTime`) is local wall-clock timing. It
  *could* be derived from `LastChangedTick`, which the server already has — no wire change.
- **wind turbine** (`_windFactor`, 0..2) is `WindField.SampleWind`, which samples Perlin noise
  at `Time.GetTicksMsec()` — **local wall clock, not the sim tick**. Server and every client
  therefore sample different wind at the same instant. It is not derivable until that noise is
  re-based on the replicated tick.

So there are two coherent designs and they are not equivalent:

1. **Make the ramp authoritative.** Derive `_powerLevel` server-side from `LastChangedTick`,
   and re-base `WindField` on the sim tick so wind is deterministic across machines. No wire
   change either way. Costs: `WindField` is a shared world system, and a deterministic wind
   clock is a behaviour change for everything else that reads it.
2. **Declare the ramp cosmetic.** Keep it for audio/shake/blade-spin and stop scaling the
   *authoritative* output cap with it, in SP as well as MP. There is precedent in this file:
   `CanToggle` already drops the warmup buffer server-side as "client-side feel, not
   authority". Costs: an SP generator would reach full output instantly, which is a gameplay
   feel change, and the wind turbine would lose its wind-strength coupling entirely.

Option 2 is smaller and matches the existing precedent; option 1 preserves the feature. Either
way it is a call for the people who own the feel, not a mechanical dedup.
