# FUNCTION_INDEX

The shared-function reference. **Check here before writing a helper.** Built from a
full sweep of `core/UnturnedSim/`, `core/UnturnedNet/`, `core/SDG.*/`, `game/`, and
`tools/` — the reusable surface only, i.e. things with a real non-test call site.

It is deliberately **not** a list of every public method (there are ~2800; nobody opens
that). It is organised by *what you are trying to do*, because the failure mode this
document exists to prevent is not "I could not find the function" — it is "it did not
occur to me that one existed".

Rules of the road:
- Entries state the **sentinel** as loudly as the return type. Most bugs in this repo's
  history came from a `0` / `-1` / `255` / `null` that meant two different things.
- `A ≡ B` means the two are known duplicates; see `docs/DUPLICATE_AUDIT.md`.
- Line numbers drift. The signature and the file are the durable part — grep the name.

---

## 1. "I need to…" — the anti-rewrite index

| I need to… | Use | Do **not** |
|---|---|---|
| damage falling off from a blast | `ExplosionMath.Linear/Squared` (`core/UnturnedSim/CombatMath.cs:12`/`:15`) | write `1 - dist/r` inline (`game/Deployable.cs:318` does; it is the one straggler) |
| decide if a fall hurts / how much / breaks legs | `FallMath.Hurts/Damage/BreaksLegs` (`core/UnturnedSim/CombatMath.cs:30`–`:36`) | compare against 22 yourself |
| know how far a zombie can sense a player | `StealthDetection.Radius/DrivingRadius` (`core/UnturnedSim/CombatMath.cs:53`/`:66`) | rebuild the stance table |
| ray-test a standing body | `ZombieCombat.RayCapsule` (`core/UnturnedSim/ZombieCombat.cs:41`) **SP** / `ServerCombat.SegmentHitsCylinder` (`core/UnturnedNet/ServerCombat.cs:629`) **MP** | add a third — these two already disagree, see DUPLICATE_AUDIT |
| band a hit into head/torso/leg | `ZombieCombat.LimbAt` (`core/UnturnedSim/ZombieCombat.cs:95`) | hardcode `0.82` (three files already do) |
| point-to-segment distance | `ZombieSpatial.SqrDistanceToSegment` (`core/UnturnedSim/ZombieSpatial.cs:192`) | — note `d` is `b-a`, **not** normalised |
| clamp to [-1,1] | `Mathf.Clamp(v, -1f, 1f)` | write a private `Clamp1` — there are **five** already |
| clamp to [0,1] | `Mathf.Clamp01` (`core/SDG.Compat/Mathf.cs:44`) | hand-roll it (`PlayerVitalsReplication.cs:215` does, in a file that already imports the shim) |
| degrees → radians | `Mathf.Deg2Rad` (`core/SDG.Compat/Mathf.cs:12`) | write `Mathf.PI / 180f` — **five** sites do, and it is not bit-identical to the constant |
| positive modulo / wrap | `Mathf.Repeat` (`core/SDG.Compat/Mathf.cs:48`) | plain `%` — it is wrong for negatives (`game/WorldItemReplicaView.cs:79`) |
| yaw → forward vector | the Godot-frame rule `(-sin yaw, 0, -cos yaw)` | copy it a fourth time — it is inline in 3 places and **one shipped inverted**, aiming 180° behind the player |
| parse `"x,y,z"` from a `.dat` | `UnityDatEx.TryParseVector3` | `Split(',')` + `float.Parse` — **five** hand-rolled copies exist, one of which (`game/CropNode.cs:26`) *throws* instead of defaulting |
| quantize a world position | `PlayerReplication.Quantize` (`core/UnturnedNet/PlayerReplication.cs:458`) | pick your own bit budget; this is THE grid every entity shares |
| write a pos/dir/vel/damage to the wire | `NetWire.WritePos/WriteDir/WriteVel/WriteDamage` (`core/UnturnedNet/CombatReplication.cs:339`+) | choose bit widths yourself — golden byte tests lock these |
| hash replicated state | `NetHash.Mix*` / `HashString` (`core/UnturnedNet/NetHash.cs`) | `string.GetHashCode` (not stable across processes) |
| put an item in a bag | `PlayerInventory.tryAddItem` (`core/UnturnedSim/PlayerInventory.cs:107`) | walk pages yourself |
| move/swap an item between slots | `PlayerInventory.TryDrag` (`:127`) | — it **is** the validator; MP calls the same one |
| count / peek / consume / repair by item id | `getItemCount` `:164` / `peekItemQuality` `:182` / `removeItemAmount` `:197` / `restoreQuality` `:219` | rewrite the page scan (these four are the same walk 4×) |
| solve a power network | `PowerSolver.Solve` (`core/UnturnedSim/PowerSolver.cs:51`) | — engine-free and L0-tested |
| solve a fluid network | `FluidSolver.Solve` (`core/UnturnedSim/FluidSolver.cs:51`) | — a line-for-line mirror of the power one |
| decide if a door may open | `DoorLogic.CanToggle/Toggle` (`core/UnturnedSim/DoorLogic.cs:55`/`:69`) | — SP and MP both route here, keep it that way |
| decide if a bed may be claimed | `BedClaims.CanClaim/Claim` (`core/UnturnedSim/BedClaims.cs:63`/`:73`) | — replicas use `Adopt` `:122`, which skips validation on purpose |
| decide if a hose may connect | `FluidHoseRule.Completion` (`core/UnturnedSim/FluidHoseRule.cs:21`) | — the wire-tool twin is still inline in `PlayerController`, see DUPLICATE_AUDIT |
| check a craft / run a craft | `Crafting.CanCraft/DoCraft` (`core/UnturnedSim/Crafting.cs:40`/`:67`) | — `DoCraft` re-validates itself |
| step deadzone exposure | `DeadzoneSim.Step` (`core/UnturnedSim/DeadzoneSim.cs:103`) | — the *sim* is shared; the volume/bookkeeping layer around it is duplicated |
| find nearby zombies / bullets vs zombies | `ZombieSpatial.QuerySphere` `:71` / `QuerySegment` `:117` | a linear scan — these already fall back to one when it is cheaper |
| format a fluid volume | `FluidDef.Litres` (`game/FluidTank.cs:94`) | — 1 unit = 1 mL everywhere |
| show a look-at info panel | `InfoBillboard` (`game/InfoBillboard.cs`) | build another Label3D rig |
| collect meshes for an outline | — **there is no shared helper**; 3 private `CollectMeshes` copies exist | (candidate for extraction) |

---

## 2. Sentinels that lie

The single highest-value table in this document. Each of these returns a value that is
indistinguishable from a legitimate result:

| Call | Returns on failure | Collides with |
|---|---|---|
| `Items.getIndex(x,y)` | `byte.MaxValue` (255) | nothing — but it is **not** `-1`, and it is not `0` |
| `ZombieCombat.RayCapsule` | `-1f` | also returns `-1f` when the hit is *behind* the origin, so an origin inside the capsule reads as a miss |
| `BedClaims.OwnerOf` / `ServerInteractables.BedOwner` | `0UL` | a bed that exists but is unclaimed |
| `SkillsReplication.ServerAward` | `0` | a player whose real XP total is 0 |
| `PlayerInventory.peekItemQuality` | `100` | a genuinely pristine item |
| `ServerCombat.AmmoOf` | `-1` (unknown player) | distinct from `0` = empty — do not conflate |
| `GasStationServer.Percent` | `0` | a genuinely empty station |
| `WorldClockReplication.TimeOfDayAt` | `0` (no clock configured) | midnight |
| `ZombieRegions.RegionOf` | `-1` | correctly reads "cold" everywhere; `BoundsOf(-1)` **throws** |
| `ItemAsset.makeLoot(unregistered)` | a valid `Item(id, 1)` | a real item — it does **not** return null |
| `Assets.find` / `findByGuid` | `null` | very common during boot before `ItemCatalog.RegisterAll` |
| `NetPakReader.ReadString` | `""` on **every** failure path, never null | an intentionally empty string — **check the bool return, not the value** |
| `.dat` `ParseBool("Flag")` on a bare flag | `false` | an explicit `false`. A bare flag is `DatValue(null)`. Read flags with `ContainsKey` |
| `.dat` `GetString("Flag", "fallback")` on a bare flag | `null` — **not** the fallback | — |
| `ParseListOfStructs` (missing key) | `null`, not empty | — |
| `FluidNet.ResolveNetType` | `FluidType.None` | a genuinely untyped fitting |
| `Bed.TryGetSpawn` / `BedClaims.TryGetSpawn` | `false` = no bed, use map spawn | — this is normal, not an error |
| `Door.TryGetByNetId` | `false` | normal — door outside this client's world |
| `DeployableDef.ById` | `null` | fail-closed by design; `DeployableReplicaView` never materialises |
| `ZombieSim.Damage` | `false` | means **both** "survived" and "unknown/already dead". `true` = *this call killed it* |
| `DestructibleReplication.IsAlive(out of range)` | `false` (dead) | **`game/DestructibleField.cs:49` returns `true` (alive) for the same input** — genuine divergence |

---

## 3. Reach, range and rate constants

Server validators are deliberately looser than the client UI. Do not "fix" a mismatch
without checking which side owns the rule.

| Constant | Value | Where |
|---|---|---|
| `PickupReach` | 6 m | `core/UnturnedNet/ServerTransactions.cs:36` |
| `PickupFacingMinDot` | 0.25 (~75° half-angle) | `:44` |
| `PickupFacingSkipRange` | 1.5 m (cone skipped inside) | `:48` |
| `CropReach` | 6 m | `:52` |
| `PlaceRangeSlack` | +4 m on the def's own `Range` | `core/UnturnedNet/DeployableReplication.cs:319` |
| `WireReach` (server) | 16 m per endpoint | `:322` |
| wire look reach (SP) | 5.5 m + 40 m/20-node polyline cap | `game/PlayerController.cs:351`/`:352` — **server has no length cap** |
| `StorageReach` | 4 m (SP opens at 2.5) | `core/UnturnedNet/InventoryReplication.cs:210` |
| `InteractReach` | 4 m | `core/UnturnedNet/InteractableReplication.cs:109` |
| `EnterReach` | 6 m **from vehicle centre** | `core/UnturnedNet/VehicleReplication.cs:695` |
| `CoupleReach` / `HitchReach` | 1.6 m / 3.5 m | `game/Vehicle.cs:117`/`:119` |
| `TowRestMin` / `TowAttachReach` / `TowBreakLen` | 2.0 / 4.5 / 7.5 m | `game/Vehicle.cs:136`–`:138` |
| `DoorLogic.ToggleCooldown` = `BedClaims.ClaimCooldown` | 0.75 s (two literals, one rule) | `DoorLogic.cs:27`, `BedClaims.cs:21` |
| `SimClock.FixedDelta` | 0.02 s — also `NetProtocol.TicksPerSecond`=50 and `BallisticsMath.StepSeconds` | three declarations of one 50 Hz |
| `SeaLevel` | 25.6 (PEI water plane) — also `Deployable.WindSeaLevel` | `game/DeployableDef.cs:63` |
| player envelope | 7 m/s sprint × 1.25 slack; down-rate 110 m/s | `core/UnturnedNet/PlayerAuthority.cs:154`–`:181` |
| `OutOfBoundsFloorY` | **−250**, not the SP shell's −1030 | `:212` — because the wire clamps Y at −256 |

---

## 4. Wire encoding budgets

Changing any of these breaks golden byte tests and cross-version compat. They are
listed so you can *read* a packet, not so you can retune one.

| Field | Encoding | Real range / grain |
|---|---|---|
| position X/Z | `ClampedFloat(11,8)` | ±2048 m, ~4 mm |
| position Y | `ClampedFloat(9,8)` | ±256 m |
| direction | 3× `SignedNormalized(12)` | ~0.03° |
| velocity | 3× `ClampedFloat(6,6)` | **±32 m/s** (the doc comment saying ±64 is wrong) |
| damage | `ClampedFloat(9,4)` | ±512 HP, 1/16 |
| yaw / pitch | `Degrees(11)` | wraps to `[0,360)` — **wraps, not clamps** |
| deployable health/fuel | `ClampedFloat(12,2)` | max 4095.75, ¼ |
| vitals (food/water/stamina/infection) | `UnsignedNormalized(8)` | 0..1 |
| time of day | `UnsignedNormalized(16)` | ~1.3 s of a PEI day |
| string | 1 bit null/empty + 11 bit len + UTF-8 | max 2048 **bytes**; oversized writes as EMPTY; **not thread-safe** (shared static buffer) |

Three special paths inside `WriteClampedFloat` are load-bearing and regression-guarded
(`NetPakClampedFloatTests:28`): under-range → all zeros, over-range → all ones, and
`|value| < 0.0001f` → decodes to exactly `0.0`. The bias is applied to the **floored
int**, not the raw float — biasing the float caused a +1.0 decode error just below an
integer. `WriteUnsignedClampedFloat` deliberately lacks the near-zero case.
`WriteSignedNormalizedFloat` is sign-magnitude (two encodings of zero) and does **not**
clamp — which is the entire reason five private `Clamp1` copies exist.

Decoder hazards: `ReadDateTime` is the one decoder that can **throw** on hostile input;
`ReadFloat` does no NaN/Inf filtering; `WriteBits` masks rather than clamps (300 in 8
bits yields 44); `WriteStateArray` length is an unchecked `(byte)` cast.

---

## 5. Subsystem reference

### 5.1 `core/UnturnedSim/` — engine-free rules

Everything here is L0-testable and has no Godot dependency. That is the point: if a rule
lives here, SP and MP cannot drift.

- **`SimRoot` / `SimClock`** — `Frame(delta)` converts one engine frame into N fixed
  0.02 s steps. **Registration order is execution order** and is load-bearing
  (replication must register last). `Advance` caps a stall at 0.33 s, so a 10 s hitch
  produces ≤16 catch-up steps, not 500.
- **`PlayerMovementSim.Step`** — no acceleration model; horizontal velocity is
  instantaneous. Input magnitudes >1 normalise down, <1 do **not** scale up (analog
  sticks work).
- **`PlayerVitalsSim.Step`** — returns `true` **only on the step health crossed ≤0**.
  Order is load-bearing: infection decays *before* the sick test. `Multipliers.None` is
  the neutral value; a `default` struct freezes every rate to 0.
- **`PlayerStanceSim.Step`** — mutates internal edge state; calling it twice per tick
  double-toggles. `currentCapsuleHeight <= 0` is the "first tick, skip the headroom gate"
  sentinel.
- **`ZombieSim`** — rows, not nodes. `PositionOf` returns **feet**, not centre. Row
  indices go stale across a despawn; hold a `ZombieId` and re-resolve with `TryGetRow`.
  `Attacks`/`Deaths` spans live exactly one tick. `Hear(pos, loudness)` takes **metres of
  carry**, not decibels.
- **`Items` / `PlayerInventory`** — the grid. `addItem` performs **no space check**
  (`checkSpace*` is the caller's job). `loadSize` **silently discards items that no
  longer fit** — that is how un-equipping a bag loses its contents. Page layout:
  `SLOTS=2, BACKPACK=3, VEST=4, SHIRT=5, PANTS=6, STORAGE=7, AREA=8`.
- **`Crafting`** — `MeetsSkill` **fails open** three ways (no requirement, null skills,
  *unrecognised* skill tag). `DoCraft` is not transactional and silently skips outputs
  whose GUID fails to resolve — you can lose supplies and get nothing if content is
  missing.
- **`PlayerSkills`** — every multiplier is a fixed shape: `1 - mastery*k` for reductions,
  `1 + mastery*k` for gains, `1/(1 ∓ mastery*k)` for the two interval→rate inversions.
  `DexterityReloadSpeed` is a **speed** — divide the duration by it.
- **`Layers.cs`** — the verified 32-slot retail layer table. **It has zero callers.** The
  game invented its own ad-hoc Godot numbering (`ZombieController.cs:82` uses bit 1 where
  retail ENEMY is 10). Either adopt the table or delete it; today it is a trap that looks
  authoritative.

### 5.2 `core/UnturnedNet/` — replication

- **`CommandRegistry.Register<T>` is the one validation choke point.** parse → validate →
  apply, with a separate diag counter per rejection class. A `null` validator means "no
  authority check" and is a deliberate choice at each site, not a default.
- **`TryDispatch(data, senderPlayerId)`** — `senderPlayerId` **must** come from the
  transport, never the payload. That is the entire anti-spoof guarantee. A throwing
  handler is caught and counted, never rethrown (fuzz-proof by construction).
- **`IReplicatedSystem`** — `WriteFull` / `WriteDelta` / `ReadSnapshot` / `StateHash`.
  `StateHash` **must** be order-independent: every implementation sorts by NetId first,
  because `NetEntityRegistry.Ids` is dictionary order.
- **`SnapshotComposer.Compose`** — a block that would overflow the budget is emitted as
  an **empty** block; framing stays valid and the system's baseline pins so the next
  delta carries everything the skips withheld. `EnableSyncCheck` must only list systems
  every client mirrors *completely* — owner-only and relevancy-filtered systems
  false-alarm by design.
- **`Relevancy`** — `ShouldWrite` keeps writing a newly-relevant entity into every delta
  until an ack proves the client saw it. `CollectRemovals` **appends**; it does not clear.
- **`ServerCombat.DamagePlayerExternal`** is the public non-weapon damage entry (fall,
  starvation, deadzone, blast). It **enqueues** rather than applying, so the hit lands at
  the next `Step` with the live tick — applying at a stale tick marks state dirty at a
  tick the sync-check already reflects, i.e. a phantom desync.
- **`ServerCombat.Step` uses `== tick` equality** for reload completion and respawn. If
  `Step` is not called every tick those are skipped *forever*, not merely delayed.

### 5.3 `game/` — the Godot layer

- **`PowerNet.Recompute`** applies `PowerScale` to **Output ports only**. The replicated
  adapter does not apply it at all — see DUPLICATE_AUDIT §A.
- **`ConnectionPort.PortLayer`** = `1<<8`, **`HosePort.PortLayer`** = `1<<11`,
  deliberately distinct so the wire ray never picks a fluid port.
- **`InfoBillboard.SetBar(i, …)`** — `i` must be 0/1/2 and **the icon per row is fixed at
  build time** (health/fuel/stamina). Callers reusing row 1 for a wind bar get the fuel
  icon regardless of the colour passed. Out-of-range `i` silently returns.
- **`Vehicle.LookRayHitsHull`** tests real oriented box hulls in their own local frames —
  no world-axis bloat, which is why a diagonal 16 m trailer no longer swallows the cab's
  airspace. It deliberately does **not** skip `.Disabled` hulls.
- **`Vehicle.MakeSmoke`** generates mipmaps on the runtime-loaded image. Without them a
  minified sprite samples black — that was the "stationary black smoke cluster" bug.
- **`Bed.Spawn` records position before `AddChild`** because `_Ready` registers the
  claim; assigning `GlobalPosition` afterwards registered every bed at the origin.
- **`StreetLight.Make`** derives its ±5% brightness jitter from a stable hash of the world
  position, so a fixture looks identical every load and for every MP player.

### 5.4 `.dat` parsing

Key lookup is **case-insensitive** (`OrdinalIgnoreCase`). All numeric parsers use
`NumberStyles.Any`, so `"(5)"` parses as **−5**, `"1,000"` as 1000, and `"NaN"`/
`"Infinity"` parse *successfully* for floats. `DatParser` never throws and never returns
null — errors accumulate in `ErrorMessages` and are **cleared on the next `Parse`**.
`DatValueEx` (node-level) does **not** null-check; only the `DatDictionaryEx` layer is
safe against missing keys.

Colour parsers are asymmetric and it is easy to grab the wrong one:
`TryParseColor32RGB` fails to **opaque** black, `TryParseColor32RGBA` to **transparent**
black; `ParseColor32RGB` forces `defaultValue.a = 255` (your alpha is discarded).
`LegacyParseColor` reads `_R/_G/_B` as floats `[0,1]`; `LegacyParseColor32RGB` reads the
same keys as bytes `[0,255]`.

### 5.5 Compat shims — a footgun worth knowing, not a bug

Two `Mathf` types coexist: `game/` compiles against `Godot.Mathf`, `core/` against
`core/SDG.Compat/Mathf.cs`. They genuinely differ in three places —
`Sign(0)` (Godot `0`, shim `1`), `Min/Max(NaN, x)` (Godot `NaN`, shim `x`), and
`Clamp` with `min > max` (Godot **throws**, shim returns silently).

**As of this sweep none of the three has a reachable call site**, and `RoundToInt` —
the one most often assumed to differ — is banker's rounding on *both* sides. Verified
empirically against `GodotSharp 4.6.2`, not from memory. Be aware of it when moving code
across the boundary; do not "fix" it by unifying the types.

`core/SDG.Compat/Quaternion` is missing `LookRotation`, `Slerp`, `Inverse`, `AngleAxis`,
`eulerAngles`, and `operator*(Quaternion, Vector3)` — a real gap for orientation maths in
`core/`. `Quaternion.Euler` is ZXY intrinsic (Unity order), not ZYX.

---

## 6. Maintaining this document

When you add a function that another subsystem could plausibly want, add a row to §1. If
it returns a sentinel, add a row to §2 — that table is the one that earns its keep.

Companion documents:
- `docs/FUNCTION_REFERENCE.md` — the per-function detail behind this index: signature,
  inputs with units and sentinels, failure path, and a verified example call site, for 675
  functions. This file is the front door; that one is what you read once you know which
  function you want.
- `docs/DUPLICATE_AUDIT.md` — known duplicate implementations and their status.
- `docs/SP_MP_PARITY_GAPS.md` — where the SP and MP paths knowingly differ.
