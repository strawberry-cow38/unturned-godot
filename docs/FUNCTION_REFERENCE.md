# FUNCTION_REFERENCE

Per-function detail: signature, what it actually computes, inputs with units and
sentinels, what comes back on the failure path, and a **verified** example call site.

The front door is `docs/FUNCTION_INDEX.md` — task-oriented ("I need to X → use this"),
plus the sentinel and wire-budget tables. Come here once you know the function and need
its contract.

## What this is worth

Generated from a seven-reader sweep at `c3dd80bf`, then mechanically checked. **Every
example call site below was verified by opening that exact file and line and confirming
the symbol appears there.** An entry with no example means no citation survived that
check — not that none exists.

Deliberately **absent**: any "this has no callers" claim. The readers made ~160 of them
and they are unreliable in both directions — `Crafting.CanCraft`, `ItemAsset.IsConsumable`
and `WorldItem.BuildItemPuppet` were each reported dead and all three have live call
sites. Re-deriving those claims with a bare-name scan produced false positives of its own,
from trailing comments and from names shared across types. So absence is not asserted
here. Use `tools/callerless` — type-attributed, with an explicit *cannot decide* bucket —
if you need that question answered.

Line numbers are from `c3dd80bf` and drift; file + signature are the durable part.

**675 functions** · 477 with a verified call site · 291 with documented inputs · 347 with a documented failure path

---

## core/UnturnedSim — engine-free rules

### `core/UnturnedSim/BallisticsMath.cs`

#### `public static Vector3 NextPos(Vector3 pos, Vector3 vel)`
<sub>`core/UnturnedSim/BallisticsMath.cs:18`</sub>

- **in** — `pos` world metres; `vel` m/s (typical muzzle 300-1000 m/s, so the segment is 6-20 m long). No sentinels; NaN passes straight through.
- **out** — world-space metres. No failure path — it is unconditional arithmetic.
- **used at** `core/UnturnedNet/ServerCombat.cs:348` — `var next = BallisticsMath.NextPos(b.Pos, b.Vel); // the exact SP step (UseableGun 0.02 s segment)`

#### `public static Vector3 StepVel(Vector3 vel, float gravity)`
<sub>`core/UnturnedSim/BallisticsMath.cs:21`</sub>

- **in** — `vel` m/s; `gravity` m/s² and expected to be **already negative and already multiplied by the gun's `GravityMultiplier`** (callers pass `-9.81f * Gun.GravityMultiplier`, typically `-39.24`). Passing `+9.81` silently makes bullets rise.
- **out** — post-step velocity, m/s. No failure path.
- **used at** `core/UnturnedNet/ServerCombat.cs:432` — `b.Vel = BallisticsMath.StepVel(b.Vel, b.Gravity);`

### `core/UnturnedSim/BedClaims.cs`

#### `public void Register(int id, Vector3 position, float yaw = 0f)`
<sub>`core/UnturnedSim/BedClaims.cs:37`</sub>

- **in** — `id` caller-assigned unique key; `position` world metres; `yaw` radians/degrees per caller convention (stored opaquely).
- **out** — `void`; **throws `ArgumentException` on a duplicate id** rather than overwriting.
- **used at** `core/UnturnedNet/InteractableReplication.cs:166` — `_beds.Register(id, pos, yaw);`

#### `public bool Remove(int id)`
<sub>`core/UnturnedSim/BedClaims.cs:48`</sub>

- **in** — `id`.
- **out** — `true` if it existed, `false` for an unknown id (safe to call blindly).
- **used at** `game/Bed.cs:109` — `Claims.Remove(BedId);`

#### `public bool CanClaim(int id, ulong player, double now)`
<sub>`core/UnturnedSim/BedClaims.cs:63`</sub>

- **in** — `player` Steam id, **`0` = nobody, always refused**; `now` sim seconds on the same clock as `LastClaimed`.
- **out** — `true`/`false`.
- **used at** `game/Bed.cs:153` — `public bool CanClaim(ulong player, double now) => Claims.CanClaim(BedId, player, now);`

#### `public bool Claim(int id, ulong player, double now)`
<sub>`core/UnturnedSim/BedClaims.cs:73`</sub>

- **in** — as `CanClaim`.
- **out** — `true` on success; `false` with **zero mutation** when not claimable.
- **used at** `game/Bed.cs:114` — `public bool TryClaim(ulong player, double now) => Claims.Claim(BedId, player, now);`

#### `public bool Unclaim(ulong player, double now)`
<sub>`core/UnturnedSim/BedClaims.cs:93`</sub>

- **out** — `true` if the player held one; `false` if they had none.

#### `public bool TryGetSpawn(ulong player, out Vector3 position, out float yaw)`
<sub>`core/UnturnedSim/BedClaims.cs:103`</sub>

- **in** — `player` Steam id.
- **out** — `true` + position/yaw; on failure `false` with `position = Vector3.zero, yaw = 0f` and the caller is expected to fall back to the map default spawn.
- **used at** `core/UnturnedNet/InteractableReplication.cs:267` — `public bool TryGetSpawn(ulong player, out Vector3 pos, out float yaw) => _beds.TryGetSpawn(player, out pos, out yaw);`

#### `public bool Adopt(int id, ulong owner, double now)`
<sub>`core/UnturnedSim/BedClaims.cs:122`</sub>

- **in** — `id`; `owner` Steam id where **`0` means release**; `now` sim seconds.
- **out** — `true`; **`false` only when the bed id is unknown**.
- **used at** `game/Bed.cs:151` — `public void ApplyReplicatedClaim(ulong owner) => Claims.Adopt(BedId, owner, 0.0);`

### `core/UnturnedSim/BlueprintDef.cs`

#### `public static List<BlueprintDef> ParseAll(IDatDictionary d, string ownerId)`
<sub>`core/UnturnedSim/BlueprintDef.cs:28`</sub>

- **in** — `d` a parsed `.dat` (may be null); `ownerId` the numeric item id string, stored verbatim on every produced def.
- **out** — a list (possibly empty, never null). Ingredient `Amount` defaults to 1; `Delete false` marks a non-consumed tool.
- **used at** `game/Main.cs:3281` — `var list = BlueprintDef.ParseAll(d, ownerId);`

#### `public string ToTsv()`
<sub>`core/UnturnedSim/BlueprintDef.cs:91`</sub>

- **out** — one tab-separated line: `ownerId | operation | name | skill | skillLevel | inputs | outputs | stations`, where inputs are `guid:amount:consume(1|0)` pipe-separated and outputs `guid:amount`.
- **used at** `game/Main.cs:3284` — `foreach (var bp in list) { lines.Add(bp.ToTsv()); bps++; }`

#### `public static BlueprintDef FromTsv(string line)`
<sub>`core/UnturnedSim/BlueprintDef.cs:102`</sub>

- **in** — one TSV line; a line with **fewer than 8 tab-separated columns returns `null`**.
- **out** — a `BlueprintDef`, or **`null`** on the malformed path — callers must null-check.
- **used at** `game/inventory/BlueprintRegistry.cs:22` — `var bp = BlueprintDef.FromTsv(line);`

### `core/UnturnedSim/ClothingDef.cs`

#### `public static ClothingDef FromDatText(string datText, EItemType slot)`
<sub>`core/UnturnedSim/ClothingDef.cs:88`</sub>

- **in** — `datText` the raw `.dat` contents; `slot` selects the slot-specific parse branch (SHIRT/VEST/MASK/GLASSES have extra keys; anything else gets base + bag keys only). Pro/Gold items are explicitly **not** modelled — feeding one a Pro asset reads its literal Armor keys instead of forcing 1.0.
- **out** — a populated `ClothingDef`; content-pointer fields (`shirtTexture`, `prefabMesh`, …) are declared but left `null` in this phase. Missing keys fall back to documented defaults rather than throwing; a completely empty `datText` yields an all-defaults object with `id == null`.

### `core/UnturnedSim/CombatMath.cs`

#### `public static float Linear(float damage, float range, float radius)`
<sub>`core/UnturnedSim/CombatMath.cs:12`</sub>

- **in** — `damage` HP at epicentre; `range` metres from blast centre to target; `radius` metres, must be `> 0` (radius 0 divides by zero → ±Infinity/NaN rather than throwing).
- **out** — HP to apply, `0f` when `range > radius`.
- **used at** `core/UnturnedNet/ServerCombat.cs:550` — `float dmg = ExplosionMath.Linear(prof.ZombieDamage, range, prof.Radius); // zombies: LINEAR falloff (Zombie.cs:270)`

#### `public static float Squared(float damage, float range, float radius)`
<sub>`core/UnturnedSim/CombatMath.cs:15`</sub>

- **in** — same units as `Linear`; `radius > 0` required.
- **out** — HP to apply, `0f` when `range > radius` or when the factor is non-positive.
- **used at** `core/UnturnedNet/ServerCombat.cs:566` — `float dmg = ExplosionMath.Squared(prof.PlayerDamage, pr, prof.Radius); // players: SQUARED falloff (Player.cs:1975); thrower included`

#### `public static bool Hurts(float verticalVel)`
<sub>`core/UnturnedSim/CombatMath.cs:30`</sub>

- **in** — `verticalVel` m/s, **signed, negative = falling**. Callers that hold a positive fall speed must negate it first (see PlayerAuthority below).
- **out** — `true` if this landing does damage.
- **used at** `game/PlayerController.cs:2080` — `if (!FallMath.Hurts(verticalVel)) return; // a normal jump lands at ~7 m/s -> no damage`

#### `public static int Damage(float verticalVel, float armorMultiplier = 1f)`
<sub>`core/UnturnedSim/CombatMath.cs:32`</sub>

- **in** — `verticalVel` m/s signed-negative; `armorMultiplier` dimensionless product of worn-clothing `fallingDamageMultiplier` × STRENGTH skill, nominally `(0, 1]`, default `1f` = unmitigated.
- **out** — whole HP, `0` on the non-hurting path (delegates to `Hurts`), capped at `101`.
- **used at** `core/UnturnedNet/PlayerAuthority.cs:426` — `int dmg = FallMath.Damage(-st.PeakFallSpeed);`

#### `public static bool BreaksLegs(float verticalVel, bool preventsBoneBreak)`
<sub>`core/UnturnedSim/CombatMath.cs:36`</sub>

- **in** — `verticalVel` m/s signed-negative; `preventsBoneBreak` = "any worn piece has `Prevents_Falling_Broken_Bones`".
- **out** — `true` if legs should break; `false` for a soft landing or when gear prevents it.
- **used at** `game/PlayerController.cs:2081` — `Broken = FallMath.BreaksLegs(verticalVel, Inventory?.PreventsFallingBoneBreak ?? false); // legs break on a hard fall UNLESS worn clothing h…`

#### `public static float Radius(EPlayerStance stance, bool moving)`
<sub>`core/UnturnedSim/CombatMath.cs:53`</sub>

- **in** — `stance` enum; `moving` bool (any nonzero locomotion).
- **out** — metres within which a zombie can sense the player; never below 1 or above 64 (the clamp means it has no "silent" value).
- **used at** `game/PlayerController.cs:2436` — `return StealthDetection.Radius(_move.Stance, Moving); // the DETECT_* table lives in core/UnturnedSim/CombatMath.cs (L0-tested)`

#### `public static float DrivingRadius(float forwardSpeedPct)`
<sub>`core/UnturnedSim/CombatMath.cs:66`</sub>

- **in** — `forwardSpeedPct` = fraction of the vehicle's top forward speed, expected `[0, 1]`; values above ~1.33 saturate at 64.
- **out** — metres, `[1, 64]`.
- **used at** `game/PlayerController.cs:2435` — `if (IsDriving) return StealthDetection.DrivingRadius(_driving.ForwardSpeedPct()); // source DRIVING: DETECT_FORWARD(48) * fwd-speed% -> loud…`

### `core/UnturnedSim/Crafting.cs`

#### `public static bool MeetsSkill(BlueprintDef bp, PlayerSkills skills)`
<sub>`core/UnturnedSim/Crafting.cs:25`</sub>

- **in** — `bp` non-null (NREs on null); `skills` may be null.
- **out** — `true` if the player may attempt this recipe.
- **used at** `core/UnturnedNet/ServerTransactions.cs:569` — `if (!Crafting.MeetsSkill(bp, skillsEntry?.Skills)) { Diag.CraftsRejected++; return; }`

#### `public static bool CanCraft(BlueprintDef bp, IInv inv, out string reason)`
<sub>`core/UnturnedSim/Crafting.cs:47`</sub>

- **in** — `bp`, `inv`. Station proximity is explicitly *not* checked here.
- **out** — `true` with `reason = "ok"`; on failure `false` with either `"unresolved ingredient {guid}"` (the GUID isn't in `Assets` — a content-loading problem, not a player problem) or `"need {N}x {itemName} (have {M})"`.

#### `public static bool DoCraft(BlueprintDef bp, IInv inv)`
<sub>`core/UnturnedSim/Crafting.cs:67`</sub>

- **in** — `bp`, `inv`. Not transactional — there is no rollback if `inv.Add` throws mid-way.
- **out** — `true` if the craft ran; `false` **with no mutation at all** when not craftable.
- **used at** `core/UnturnedNet/ServerTransactions.cs:571` — `if (Crafting.DoCraft(bp, adapter)) Diag.CraftsApplied++; else Diag.CraftsRejected++;`

### `core/UnturnedSim/DeadzoneSim.cs`

#### `public static DeadzoneDef Default(DeadzoneKind kind = DeadzoneKind.Radiation)`
<sub>`core/UnturnedSim/DeadzoneSim.cs:25`</sub>

- **in** — `kind` — `FullSuitRadiation` additionally requires shirt+pants.
- **out** — a populated `DeadzoneDef` value.
- **used at** `game/DeadzoneField.cs:36` — `=> AddVolume(center, halfExtent, DeadzoneDef.Default(kind));`

#### `public void Exit()`
<sub>`core/UnturnedSim/DeadzoneSim.cs:84`</sub>

- **out** — `void`.
- **used at** `core/UnturnedNet/ServerDeadzones.cs:110` — `if (_inside.TryGetValue(playerId, out var left)) { left.Exit(); _inside.Remove(playerId); }`

#### `public static bool IsProtected(in DeadzoneDef zone, in RadiationGear gear)`
<sub>`core/UnturnedSim/DeadzoneSim.cs:93`</sub>

- **in** — `zone` (its `Kind` selects the rule); `gear.MaskQuality` 0..100 where **0 = spent filter = no protection**.
- **out** — `true` if the loadout holds. Pure; no state.
- **used at** `core/UnturnedSim/DeadzoneSim.cs:112` — `result.Protected = IsProtected(zone, gear);`

#### `public DeadzoneTickResult Step(in DeadzoneDef zone, in RadiationGear gear, float dt)`
<sub>`core/UnturnedSim/DeadzoneSim.cs:103`</sub>

- **in** — `zone`; `gear`; `dt` seconds — **`dt <= 0` returns an empty result *without* marking `IsInside`**, so a zero-delta frame doesn't register entry.
- **out** — `DeadzoneTickResult { Damage (HP to remove), Radiation (infection to add, 0 while protected), MaskQualityLost (whole filter points), Protected }`. The caller owns applying all of it — the sim touches no health, infection or item.
- **used at** `game/DeadzoneField.cs:100` — `var r = sim.Step(volume.Zone, gear, dt);`

### `core/UnturnedSim/DoorLogic.cs`

#### `public static bool HasAccess(in DoorState door, ulong player, ulong group)`
<sub>`core/UnturnedSim/DoorLogic.cs:44`</sub>

- **in** — `door` state; `player` Steam id, `0` = nobody; `group` group id, `0` = no group (a `0` group never matches, even against a door whose group is also 0, because of the explicit `door.Group != 0` guard).
- **out** — `true` if this player may operate the door.
- **used at** `core/UnturnedSim/DoorLogic.cs:59` — `if (!HasAccess(door, player, group)) { why = DoorRefusal.Locked; return false; }`

#### `public static bool CanToggle(in DoorState door, ulong player, ulong group, double now, bool arcBlocked, out DoorRefusal why)`
<sub>`core/UnturnedSim/DoorLogic.cs:55`</sub>

- **in** — `now` = sim seconds on the same clock as `door.LastToggled` (a freshly built door should use `double.NegativeInfinity`, not `0`, or it sits in its own 0.75 s cooldown at sim start — see `BedClaims.Register` for the same bug fixed); `arcBlocked` = the engine's overlap answer, the one thing the sim can't compute.
- **out** — `true`/`false`, plus `why` ∈ `{None, Cooldown, Locked, Obstructed}` — `None` on the success path.
- **used at** `game/Door.cs:96` — `if (!DoorLogic.CanToggle(_state, player, group, now, blocked, out var why))`

#### `public static DoorState Toggle(DoorState door, double now)`
<sub>`core/UnturnedSim/DoorLogic.cs:69`</sub>

- **in** — current state; `now` sim seconds.
- **out** — the new state. Cannot fail.
- **used at** `game/Door.cs:102` — `_state = DoorLogic.Toggle(_state, now);`

#### `public static bool TrySetLocked(ref DoorState door, ulong player, bool locked)`
<sub>`core/UnturnedSim/DoorLogic.cs:78`</sub>

- **in** — `door` by ref (mutated in place on success); `player` Steam id; `locked` desired state.
- **out** — `true` if applied; `false` — leaving `door` untouched — when the door is unowned or the caller isn't the owner.
- **used at** `game/Door.cs:111` — `public bool TrySetLocked(ulong player, bool locked) => DoorLogic.TrySetLocked(ref _state, player, locked);`

### `core/UnturnedSim/FluidHoseRule.cs`

#### `public static bool IsSourceSide(FluidPortKind k)`
<sub>`core/UnturnedSim/FluidHoseRule.cs:13`</sub>

- **in** — `k` ∈ {Source, Consumer, Passthrough}.
- **out** — `true` for the two pushing kinds, `false` for `Consumer`. Total function; no failure path.
- **used at** `game/PlayerController.cs:760` — `var (sp, cp) = FluidHoseRule.IsSourceSide(_hoseSrc.Kind) ? (_hoseSrc, _hosePort) : (_hosePort, _hoseSrc);`

#### `public static HoseVerdict Completion(FluidPortKind startKind, FluidPortKind targetKind, bool startEmpty, bool targetEmpty, bool typesEqual, bool sameOwner, bool targetHosed)`
<sub>`core/UnturnedSim/FluidHoseRule.cs:21`</sub>

- **in** — the two port kinds; `startEmpty`/`targetEmpty` = "that container's tank fluid is None" (an empty tank adopts the other's type on connect, so it never mismatches); `typesEqual` only consulted when both are non-empty; `sameOwner` = the two ports sit on the same device; `targetHosed` = target already has its one allowed h…
- **out** — `HoseVerdict.Ok` / `.Mismatch` ("cannot mix fluids", the only one worth a UI message) / `.None` (illegal target, show nothing).
- **used at** `game/PlayerController.cs:712` — `return FluidHoseRule.Completion(start.Kind, target.Kind,`

### `core/UnturnedSim/FluidSolver.cs`

#### `public static void Solve(IReadOnlyList<FluidDevice> devices, IReadOnlyList<FluidHose> hoses)`
<sub>`core/UnturnedSim/FluidSolver.cs:51`</sub>

- **in** — `devices` and `hoses` — hoses with a null `Source` or `Consumer` are skipped; a cycle is guarded by the `seen` set. `Supplying`/`Blocked`/`Open` on each device gate conduction (`Blocked` OR `!Open` stops a consumer conducting, killing its passthrough). Finite amounts are **not** handled here — the caller must clamp a so…
- **out** — `void`; results are written back onto the ports — `Flow` (source = supplied, consumer = received, passthrough = exported), `Flowing` (consumer got ≥ its demand), `Load` (source only, total downstream draw). Empty inputs are a legal no-op.
- **used at** `game/FluidNet.cs:249` — `FluidSolver.Solve(devices, hoses);`

### `core/UnturnedSim/ItemAsset.cs`

#### `public bool IsConsumable { get; }`
<sub>`core/UnturnedSim/ItemAsset.cs:61`</sub>

- **out** — bool. **NO CALLERS** outside `game/testing/` paths.

#### `public bool IsMagazine => magCapacity > 0;`
<sub>`core/UnturnedSim/ItemAsset.cs:68`</sub>

- **used at** `game/PlayerController.cs:2870` — `bool UsesMagItem => Gun != null && !Gun.ShellReload && (SDG.Unturned.Assets.find((ushort)Gun.MagazineId)?.IsMagazine ?? false);`

#### `public bool IsFuelContainer => fuelCapacity > 0f;`
<sub>`core/UnturnedSim/ItemAsset.cs:70`</sub>


#### `public bool IsFluidContainer => fluidCapacity > 0f;`
<sub>`core/UnturnedSim/ItemAsset.cs:79`</sub>


#### `public static byte ParseSize(IDatDictionary d, string key)`
<sub>`core/UnturnedSim/ItemAsset.cs:96`</sub>

- **in** — `d` parsed `.dat`; `key` the size key name.
- **out** — grid cells, always ≥ 1. Never fails.

#### `public static void add(ItemAsset a)`
<sub>`core/UnturnedSim/ItemAsset.cs:152`</sub>

- **in** — `a` may be null (no-op).
- **out** — `void`.
- **used at** `game/inventory/ItemCatalog.cs:253` — `Assets.add(new ItemAsset`

### `core/UnturnedSim/ItemJar.cs`

#### `public ItemAsset GetAsset()`
<sub>`core/UnturnedSim/ItemJar.cs:15`</sub>

- **in** — none.
- **out** — the `ItemAsset`, or `null` if the jar holds no item or the id was never registered.
- **used at** `game/inventory/InventoryUI.cs:626` — `var asset = jar.GetAsset();`

### `core/UnturnedSim/Items.cs`

#### `public ItemJar getItem(byte pos_x, byte pos_y)`
<sub>`core/UnturnedSim/Items.cs:40`</sub>

- **in** — grid cell coordinates.
- **out** — the covering jar, or **`null`** for an empty cell.
- **used at** `game/PlayerController.cs:1399` — `var j = pg.getItem(idx);`

#### `public void addItem(byte x, byte y, byte rot, Item item)`
<sub>`core/UnturnedSim/Items.cs:66`</sub>

- **in** — cell coords; `rot` where odd swaps width/height; `item`.
- **out** — `void`. Overlapping an existing item is silently allowed and corrupts the occupancy grid.
- **used at** `core/UnturnedNet/InventoryReplication.cs:305` — `to.addItem(j.x, j.y, j.rot, j.item);`

#### `public void clear()`
<sub>`core/UnturnedSim/Items.cs:114`</sub>

- **out** — `void`.
- **used at** `game/PlayerController.cs:2233` — `s.clear(); s.loadSize(0, 0);`

### `core/UnturnedSim/Layers.cs`

#### `public static class LayerMasks`
<sub>`core/UnturnedSim/Layers.cs:7`</sub>


#### `public static class RayMasks`
<sub>`core/UnturnedSim/Layers.cs:44`</sub>

- **used at** `game/inventory/WorldItem.cs:52` — `/// RayMasks.BLOCK_PICKUP) per pending drop and skips anything the ray hits.`

### `core/UnturnedSim/PlayerInventory.cs`

#### `public float FallingDamageMultiplier { get; }`
<sub>`core/UnturnedSim/PlayerInventory.cs:58`</sub>

- **out** — dimensionless multiplier, `1.0` when nothing is worn.
- **used at** `game/Main.cs:730` — `GD.Print($"[wearcloth] worn: shirt={inv.wornShirt?.id} pants={inv.wornPants?.id} hat={inv.wornHat?.id} vest={inv.wornVest?.id} | fall x{inv.…`

#### `public bool PreventsFallingBoneBreak { get; }`
<sub>`core/UnturnedSim/PlayerInventory.cs:70`</sub>

- **out** — bool; `false` when nothing is worn.
- **used at** `game/PlayerController.cs:2081` — `Broken = FallMath.BreaksLegs(verticalVel, Inventory?.PreventsFallingBoneBreak ?? false); // legs break on a hard fall UNLESS worn clothing h…`

#### `public RadiationGear RadiationProtection()`
<sub>`core/UnturnedSim/PlayerInventory.cs:75`</sub>

- **out** — a `RadiationGear` value; `MaskQuality` is `0` when no mask is worn (which `DeadzoneSim.IsProtected` reads as an unprotected loadout).
- **used at** `core/UnturnedNet/NetWorldHost.cs:140` — `Deadzones.GearOf = pid => Inventories.TryGet(pid, out var inv) ? inv.Inventory.RadiationProtection() : default;`

#### `public bool equipToSlot(byte slot, Item item)`
<sub>`core/UnturnedSim/PlayerInventory.cs:116`</sub>

- **in** — `slot` 0 = primary, 1 = secondary; **anything ≥ 2 is refused**.
- **out** — `true` if placed; `false` if the slot index is out of range or already holds something.
- **used at** `game/PlayerController.cs:3660` — `inv.equipToSlot(0, new Item(4)); // Eaglefire -> primary`

#### `public void restoreQuality(ushort id, byte quality)`
<sub>`core/UnturnedSim/PlayerInventory.cs:219`</sub>

- **in** — `id`; `quality` 0..100 (callers pass 100).
- **out** — `void`; silently no-ops when no instance exists.
- **used at** `game/inventory/CraftingUI.cs:141` — `Inv.restoreQuality(oid, 100);`

### `core/UnturnedSim/PlayerMovementDef.cs`

#### `public static float SpeedForStance(EPlayerStance stance)`
<sub>`core/UnturnedSim/PlayerMovementDef.cs:37`</sub>

- **in** — stance enum.
- **out** — m/s in `[1.5, 7]`. No failure path — unknown values return the STAND default rather than throwing.
- **used at** `core/UnturnedSim/PlayerMovementSim.cs:24` — `float speed = PlayerMovementDef.SpeedForStance(Stance);`

#### `public static float HeightForStance(EPlayerStance stance)`
<sub>`core/UnturnedSim/PlayerMovementDef.cs:50`</sub>

- **in** — stance enum.
- **out** — metres in `{0.8, 1.2, 2.0}`; unknown values return 2.0.
- **used at** `game/PlayerController.cs:2804` — `float h = PlayerMovementDef.HeightForStance(stance);`

### `core/UnturnedSim/PlayerMovementSim.cs`

#### `public Vector3 Step(Vector2 inputDir, bool wantJump, bool grounded, float dt)`
<sub>`core/UnturnedSim/PlayerMovementSim.cs:21`</sub>

- **in** — inputDir.x` = strafe, `inputDir.y` = forward, each in `[-1,1]`; magnitudes `> 1` are normalised down, magnitudes `< 1` are *not* scaled up (analog stick works). `wantJump` only acts while `grounded`. `grounded` = was-on-floor after the previous move. `dt` seconds (0.02 in the fixed loop). Speed comes from the public `S…
- **out** — velocity in m/s to hand to the character body. No failure path; note grounded+falling snaps `Velocity.y` to 0 before the jump impulse, so jump height is framerate-independent.
- **used at** `game/PlayerController.cs:4645` — `var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, grounded, delta);`

### `core/UnturnedSim/PlayerSkills.cs`

#### `public Skill GetSkill(int speciality, int index)`
<sub>`core/UnturnedSim/PlayerSkills.cs:76`</sub>

- **in** — `speciality` 0..2 (`EPlayerSpeciality`), `index` 0..6 for OFFENSE/DEFENSE, 0..7 for SUPPORT.
- **out** — the skill object; never null within range.
- **used at** `game/CropManager.cs:68` — `var ag = by.Skills?.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.AGRICULTURE);`

#### `public void AwardExperience(uint xp)`
<sub>`core/UnturnedSim/PlayerSkills.cs:82`</sub>

- **in** — `xp` points.
- **out** — `void`.
- **used at** `game/CropManager.cs:71` — `by.Skills?.AwardExperience(HarvestRewardExperience); // source InteractableFarm: harvest awards Harvest_Reward_Experience (all crops = defau…`

#### `public void NetSetExperience(uint xp)`
<sub>`core/UnturnedSim/PlayerSkills.cs:86`</sub>

- **in** — `xp` the server's authoritative total.
- **out** — `void`.
- **used at** `game/PlayerController.cs:2322` — `Skills.NetSetExperience(replica.experience);`

#### `public float SharpshooterRecoilMultiplier()`
<sub>`core/UnturnedSim/PlayerSkills.cs:89`</sub>

- **out** — `[0.6, 1.0]`; `1.0` at level 0.
- **used at** `game/PlayerController.cs:3789` — `float sharp = Skills.SharpshooterRecoilMultiplier(); // SHARPSHOOTER: up to -40% recoil + spread at max level (source UseableGun)`

#### `public float StrengthFallMultiplier()`
<sub>`core/UnturnedSim/PlayerSkills.cs:92`</sub>

- **out** — `[0.25, 1.0]`.
- **used at** `game/PlayerController.cs:2082` — `int dmg = FallMath.Damage(verticalVel, (Inventory?.FallingDamageMultiplier ?? 1f) * Skills.StrengthFallMultiplier()); // worn clothing (whol…`

#### `public bool TryUpgrade(int speciality, int index)`
<sub>`core/UnturnedSim/PlayerSkills.cs:118`</sub>

- **in** — unchecked indices (throws out of range).
- **out** — `true` if a level was bought; `false` — with **no XP spent** — when at max or too poor.
- **used at** `game/Main.cs:2007` — `skills.TryUpgrade((int)SDG.Unturned.EPlayerSpeciality.SUPPORT, (int)SDG.Unturned.EPlayerSupport.CRAFTING);`

#### `public bool TryFind(string name, out Skill skill, out string label)`
<sub>`core/UnturnedSim/PlayerSkills.cs:130`</sub>

- **in** — `name` a skill enum name (`"crafting"`, `"sharpshooter"`, …).
- **out** — `true` with the live `Skill` and its canonical `label`; on failure `false` with **both out-params `null`**.
- **used at** `game/DevConsole.cs:285` — `if (!Player.Skills.TryFind(pp[0], out var sk, out var label)) { Log($"no skill '{pp[0]}' (try: crafting, agriculture, sharpshooter, strength…`

### `core/UnturnedSim/PlayerStanceSim.cs`

#### `public EPlayerStance Step(bool crouchKey, bool proneKey, bool sprintKey, float stamina, bool broken, EPlayerStance? scriptedStance, float currentCapsuleHeight, Func<float,bool> headroomFor)`
<sub>`core/UnturnedSim/PlayerStanceSim.cs:21`</sub>

- **in** — `crouchKey`/`proneKey`/`sprintKey` = raw key-down (not edges — the class does edge detection itself); `stamina` 0..1, sprint requires `> 0.05`; `broken` = legs broken, demotes SPRINT→STAND; `scriptedStance` overrides the base stance when non-null (driving/sitting); `currentCapsuleHeight` metres — **`<= 0` is the sentine…
- **out** — the stance to adopt this tick. No failure path — it always returns a legal stance; a null `headroomFor` will NRE if the height gate is reached.
- **used at** `game/PlayerController.cs:4631` — `_move.Stance = _stance.Step(crouchKey, proneKey, sprintKey, Stamina, Broken, scriptedStance, _capStance, HeadroomFor);`

### `core/UnturnedSim/PlayerVitalsSim.cs`

#### `public static Multipliers None { get; }`
<sub>`core/UnturnedSim/PlayerVitalsSim.cs:27`</sub>

- **out** — a `Multipliers` value; never fails.
- **used at** `core/UnturnedNet/PlayerVitalsReplication.cs:110` — `var m = MultipliersOf != null ? MultipliersOf(pid) : PlayerVitalsSim.Multipliers.None;`

#### `public bool Step(bool sprinting, bool survivalDrain, float dt, in Multipliers m)`
<sub>`core/UnturnedSim/PlayerVitalsSim.cs:33`</sub>

- **in** — `sprinting` bool; `survivalDrain` = the F1-console `survival` toggle (hunger/thirst are **off by default**); `dt` seconds; `m` skill multipliers — `Multipliers.None` (`PlayerVitalsSim.cs:27`) is the neutral all-`1f` value, and a zero-initialised `default` struct would freeze all rates to 0.
- **out** — `true` **only on the step health crossed to ≤ 0** — the doc comment states callers must not step a dead player, and it will keep returning `true` if they do. All state changes land on the public fields (`Health`, `Stamina` 0..1, `Food`/`Water` 0..1, `Infection` 0..1).
- **used at** `game/PlayerController.cs:3185` — `bool died = _vitals.Step(sprinting, SurvivalDrain, dt, new PlayerVitalsSim.Multipliers`

### `core/UnturnedSim/PowerSolver.cs`

#### `public static void Solve(IReadOnlyList<PowerDevice> devices, IReadOnlyList<PowerWire> wires)`
<sub>`core/UnturnedSim/PowerSolver.cs:51`</sub>

- **in** — `devices` — every device in the graph; port `Watts` are absolute watts (output = produced cap, consumer = drawn, passthrough = ignored/overwritten), `Producing`/`OnFire`/`Conducting` are the gates. `wires` — directed source-port → consumer-port edges; **assumes at most one wire per source port** (the `foreach … break` a…
- **out** — `void` — results are mutated onto the passed `PowerPort` objects (`Live`, `Powered`, `Draw`). Failure/empty path: with no producing output every consumer ends `Live=0, Powered=false` and every output `Draw=0`.
- **used at** `game/PowerNet.cs:65` — `PowerSolver.Solve(devices, wires);`

### `core/UnturnedSim/SimClock.cs`

#### `public int Advance(double frameDelta)`
<sub>`core/UnturnedSim/SimClock.cs:21`</sub>

- **in** — `frameDelta` seconds of wall/engine time; negatives clamped to 0, anything `> 0.33` clamped to 0.33 (time is *lost*, not banked).
- **out** — number of fixed steps to run this frame (0 is normal and common at high framerate). Side effects: advances `Tick` and drains `Accumulator`.
- **used at** `core/UnturnedSim/SimRoot.cs:29` — `int steps = _clock.Advance(frameDelta);`

#### `public void Reset()`
<sub>`core/UnturnedSim/SimClock.cs:36`</sub>

- **used at** `core/UnturnedSim/SimRoot.cs:40` — `public void Reset() => _clock.Reset();`

### `core/UnturnedSim/SimRoot.cs`

#### `public void Add(ISimStepped system)`
<sub>`core/UnturnedSim/SimRoot.cs:23`</sub>

- **in** — any `ISimStepped`; `null` is accepted here and NREs later inside `Frame`.
- **out** — `void`.
- **used at** `game/DedicatedServer.cs:160` — `Driver.Sim.Add(new DelegateSimStep((tick, dt) => Server.TickSimulation(), "net.server.sim"));`

#### `public bool Remove(ISimStepped system)`
<sub>`core/UnturnedSim/SimRoot.cs:24`</sub>

- **in** — the same instance handed to `Add`.
- **out** — `true` if found and removed, `false` if it was never registered.
- **used at** `game/testing/tests/UnifyTests.cs:141` — `world.Sim.Sim.Remove(pump);`

#### `public int Frame(double frameDelta)`
<sub>`core/UnturnedSim/SimRoot.cs:27`</sub>

- **in** — `frameDelta` seconds (same clamping rules as `SimClock.Advance`).
- **out** — number of fixed steps executed; `0` when the frame was shorter than 0.02 s (systems are not called at all — no partial step exists).
- **used at** `game/SimDriver.cs:18` — `Sim.Frame(delta);`

#### `public void Reset()`
<sub>`core/UnturnedSim/SimRoot.cs:40`</sub>


### `core/UnturnedSim/ZombieCombat.cs`

#### `public static float RayCapsule(Vector3 origin, Vector3 dir, Vector3 foot, float radius, float height)`
<sub>`core/UnturnedSim/ZombieCombat.cs:41`</sub>

- **in** — `origin` world metres; **`dir` must already be normalised** (there is no internal normalise, and `t` is returned in units of `dir` length); `foot` = the *feet* position, not the centre; `radius`, `height` metres from the kind record.
- **out** — distance `t` along the ray to the entry point, in metres. **`-1f` on a miss** — and also `-1f` when the only intersection is *behind* the origin (`t < 0`), so an origin inside the capsule reads as a miss.
- **used at** `core/UnturnedSim/ZombieSim.cs:813` — `float t = ZombieCombat.RayCapsule(origin, dir, _pos[row], kind.Radius, kind.Height);`

#### `public static ZombieLimb LimbAt(Vector3 point, Vector3 foot, float height)`
<sub>`core/UnturnedSim/ZombieCombat.cs:95`</sub>

- **in** — `point` world hit position; `foot` the zombie's stored feet position; `height` metres. A point *below* the feet returns `Leg`; a point above the head returns `Skull` (no upper bound check).
- **out** — `ZombieLimb.Skull|Spine|Leg`. Total function.
- **used at** `core/UnturnedSim/ZombieSim.cs:826` — `Limb = ZombieCombat.LimbAt(point, _pos[bestRow], _kinds[_kind[bestRow]].Height),`

### `core/UnturnedSim/ZombieKind.cs`

#### `public ushort Register(ZombieKind kind)`
<sub>`core/UnturnedSim/ZombieKind.cs:46`</sub>

- **in** — `kind` non-null (throws `ArgumentNullException`); the table throws `InvalidOperationException` past 65535 kinds.
- **out** — the assigned id, `0`-based, sequential. Ids are never reused.
- **used at** `game/ZombieDirector.cs:99` — `kinds.Register(new ZombieKind { Name = "normal", MoveSpeed = 5.5f, Health = 100f });`

#### `public ZombieKind this[ushort id] { get; }`
<sub>`core/UnturnedSim/ZombieKind.cs:56`</sub>

- **in** — `id` from `Register`.
- **out** — the live, mutable `ZombieKind` record (callers can and do tune fields on it in place).

#### `public static ZombieKindTable Default()`
<sub>`core/UnturnedSim/ZombieKind.cs:68`</sub>

- **out** — a fresh table; never null. This is the silent fallback when `ZombieSim` is constructed with `kinds: null` (`ZombieSim.cs:188`) — so a director that forgets to register kinds gets slow 1.6 m/s zombies rather than an error.
- **used at** `core/UnturnedSim/ZombieSim.cs:188` — `_kinds = kinds ?? ZombieKindTable.Default();`

### `core/UnturnedSim/ZombieRegions.cs`

#### `public bool ContainsExpanded(Vector3 p, float margin)`
<sub>`core/UnturnedSim/ZombieRegions.cs:17`</sub>

- **in** — `margin` metres, expected ≥ 0 (a negative margin shrinks the box, which is legal but probably unintended).
- **out** — `true`/`false`. **NO CALLERS** outside `ZombieRegions.cs:93`.
- **used at** `core/UnturnedSim/ZombieRegions.cs:93` — `if (!_bounds[r].ContainsExpanded(players[p], HotMargin)) continue;`

#### `public static ZombieRegions UniformGrid(int cellsPerAxis = 64, float regionSize = 128f, float originOffset = 4096f)`
<sub>`core/UnturnedSim/ZombieRegions.cs:59`</sub>

- **in** — `cellsPerAxis` must be `> 0` (throws `ArgumentOutOfRangeException`); `regionSize` metres; `originOffset` metres of shift so the grid straddles the origin.
- **out** — a populated `ZombieRegions` of `cellsPerAxis²` regions.

#### `public int RegionOf(Vector3 p, int hint = -1)`
<sub>`core/UnturnedSim/ZombieRegions.cs:74`</sub>

- **in** — `p` world position; `hint` the caller's previous region index, **`-1` = no hint** (also the natural "was off-partition" value).
- **out** — region index, or **`-1` if the point is outside every region** — which `ZombieSim` counts as `Stats.Orphan` and treats as never-hot.
- **used at** `game/Main.cs:3872` — `int r = sim.RegionOf(i);`

#### `public void MarkHot(Vector3[] players, int playerCount)`
<sub>`core/UnturnedSim/ZombieRegions.cs:84`</sub>

- **in** — `players` may be `null` and `playerCount` may be `<= 0` — both are handled by bumping the stamp and returning, which correctly makes **everything cold**. `playerCount` is trusted to be ≤ `players.Length`.
- **out** — `void`; state lands in `IsHot`/`HotCount`.
- **used at** `core/UnturnedSim/ZombieSim.cs:362` — `_regions.MarkHot(_players, _playerCount);`

#### `public bool IsHot(int region)`
<sub>`core/UnturnedSim/ZombieRegions.cs:103`</sub>

- **in** — `region` index, `-1` sentinel accepted.
- **out** — `true` if hot as of the last `MarkHot`.
- **used at** `core/UnturnedSim/ZombieSim.cs:900` — `if (!_regions.IsHot(region)) return busy ? ZombieTier.Far : ZombieTier.Ambient;`

### `core/UnturnedSim/ZombieSim.cs`

#### `public ReadOnlySpan<int> DueRows { get; }`
<sub>`core/UnturnedSim/ZombieSim.cs:205`</sub>

- **used at** `tests/UnturnedSim.Tests/ZombieSimTests.cs:214` — `foreach (int row in sim.DueRows.ToArray())`

#### `public ZombieId Spawn(ushort kind, Vector3 position)`
<sub>`core/UnturnedSim/ZombieSim.cs:209`</sub>

- **in** — `kind` a registered kind id — **throws `ArgumentOutOfRangeException` for an unregistered one**; `position` world metres (a position outside every region yields `_region = -1` and counts as `Stats.Orphan`).
- **out** — a stable `ZombieId`. Generation 0 is never issued, so `default(ZombieId)` is always invalid.
- **used at** `game/ZombieDirector.cs:109` — `foreach (var (_, pos) in planned) _sim.Spawn(0, new UVector3(pos.X, pos.Y, pos.Z));`

#### `public bool Despawn(ZombieId id)`
<sub>`core/UnturnedSim/ZombieSim.cs:251`</sub>

- **in** — `id`.
- **out** — `true` if it existed; `false` for an unknown/stale handle (double-despawn is a safe no-op, not corruption). Note row indices held across a despawn become stale.
- **used at** `core/UnturnedSim/ZombieSim.cs:416` — `foreach (var id in _toRecycle) Despawn(id);`

#### `public bool IsAlive(ZombieId id)`
<sub>`core/UnturnedSim/ZombieSim.cs:303`</sub>

- **out** — `true`/`false`.
- **used at** `game/ZombieNetSync.cs:169` — `if (!sim.IsAlive(kv.Key)) _simStale.Add(kv.Key);`

#### `public bool TryGetRow(ZombieId id, out int row)`
<sub>`core/UnturnedSim/ZombieSim.cs:305`</sub>

- **in** — `id`.
- **out** — `true` + row index; on failure `false` with **`row = -1`**.
- **used at** `game/ZombieNetSync.cs:191` — `if (!sim.TryGetRow(kv.Key, out int r) || sim.IsDead(r)) return false;`

#### `public void SetPosition(int row, Vector3 p)`
<sub>`core/UnturnedSim/ZombieSim.cs:327`</sub>

- **in** — `row`; `p` world metres.
- **out** — `void`. Also clears the path-failed flag and shamble budget.

#### `public void SetPlayers(Vector3[] players, int count)`
<sub>`core/UnturnedSim/ZombieSim.cs:336`</sub>

- **in** — `players` — `null` is coerced to an empty array; `count` — negatives are clamped to 0.
- **out** — `void`.
- **used at** `game/ZombieDirector.cs:156` — `_sim.SetPlayers(_players, n);`

#### `public long UsRegions, UsSpatial, UsTier, UsMove, UsPaths`
<sub>`core/UnturnedSim/ZombieSim.cs:347`</sub>


#### `public ReadOnlySpan<ZombieAttackEvent> Attacks { get; }`
<sub>`core/UnturnedSim/ZombieSim.cs:781`</sub>

- **used at** `game/ZombieDirector.cs:184` — `var attacks = _sim.Attacks;`

#### `public ReadOnlySpan<ZombieDeathEvent> Deaths { get; }`
<sub>`core/UnturnedSim/ZombieSim.cs:783`</sub>

- **used at** `tests/UnturnedSim.Tests/ZombieCombatTests.cs:212` — `Assert.That(sim.Deaths.Length, Is.EqualTo(1));`

#### `public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out ZombieHit hit)`
<sub>`core/UnturnedSim/ZombieSim.cs:792`</sub>

- **in** — `origin` world metres; `direction` need **not** be normalised (it normalises internally, and bails if the result is degenerate); `maxDistance` metres, **`<= 0` returns false immediately**.
- **out** — `true` + a populated `ZombieHit` (`Id`, `Row`, `Point`, `Distance`, `Limb`, `Hit=true`); on a miss `false` with `hit = default` — note `default(ZombieHit).Hit` is `false`, which callers double-check.
- **used at** `game/ZombieDirector.cs:325` — `if (!_sim.Raycast(U(origin), U(dir), maxDistance, out var hit) || !hit.Hit) return false;`

#### `public bool Damage(ZombieId id, float amount, ZombieLimb limb = ZombieLimb.Spine)`
<sub>`core/UnturnedSim/ZombieSim.cs:834`</sub>

- **in** — `id`; `amount` raw HP, already multiplied; `limb` recorded for the corpse/FX only.
- **out** — `true` **only if this call killed it** — `false` both for a non-fatal hit and for an unknown handle or an already-dead row, so `false` is not "it survived".
- **used at** `game/ZombieDirector.cs:326` — `killed = _sim.Damage(hit.Id, damage, hit.Limb);`

#### `public int Hear(Vector3 pos, float loudness)`
<sub>`core/UnturnedSim/ZombieSim.cs:860`</sub>

- **in** — `pos` world metres; `loudness` **metres of carry**, not decibels — `<= 0` returns 0 immediately.
- **out** — number of zombies for whom this became their new best-heard noise (not the number that are within range). `0` on an empty sim.
- **used at** `game/SoundBus.cs:34` — `zd?.Sim?.Hear(new UnityEngine.Vector3(pos.X, pos.Y, pos.Z), loudness);`

### `core/UnturnedSim/ZombieSpatial.cs`

#### `public void Build(Vector3[] pos, int count)`
<sub>`core/UnturnedSim/ZombieSpatial.cs:54`</sub>

- **in** — `pos` dense position array; `count` how many entries are live — **throws `ArgumentOutOfRangeException` if negative**, and `ArgumentNullException` if `pos` is null with `count > 0` (`count == 0` with a null array is legal).
- **out** — `void`.
- **used at** `core/UnturnedSim/ZombieSim.cs:364` — `_spatial.Build(_pos, _count);`

#### `public int QuerySphere(Vector3 centre, float radius, int[] results)`
<sub>`core/UnturnedSim/ZombieSpatial.cs:71`</sub>

- **in** — `centre` world metres; `radius` metres — **negative returns 0**; `results` caller-owned output buffer that must be sized for the worst case.
- **out** — number of indices written. Returns early (truncating silently) when `results` fills, and `0` when the grid is empty. **Throws `ArgumentNullException` if `results` is null.** Capsule extent is the caller's problem — pass a widened radius.
- **used at** `core/UnturnedSim/ZombieSim.cs:864` — `int n = _spatial.QuerySphere(pos, loudness, _queryScratch);`

#### `public int QuerySegment(Vector3 a, Vector3 b, float radius, int[] results)`
<sub>`core/UnturnedSim/ZombieSpatial.cs:117`</sub>

- **in** — `a`,`b` segment endpoints in world metres; `radius` metres, **negative returns 0**; `results` output buffer.
- **out** — number of indices written, truncated at `results.Length`; `0` on an empty grid. Throws on a null `results`.
- **used at** `core/UnturnedSim/ZombieSim.cs:805` — `int n = _spatial.QuerySegment(origin, origin + dir * maxDistance, pad, _queryScratch);`

#### `public static float SqrDistanceToSegment(Vector3 p, Vector3 a, Vector3 d)`
<sub>`core/UnturnedSim/ZombieSpatial.cs:192`</sub>

- **in** — `p` the point; `a` the segment **start**; `d` the segment's **direction-and-length vector** (i.e. `b - a`, *not* a normalised direction — this is the easy one to get wrong).
- **out** — squared distance in m². Never negative, never throws.
- **used at** `core/UnturnedSim/ZombieSpatial.cs:137` — `if (SqrDistanceToSegment(_pos[i], a, d) > r2) continue;`


## core/UnturnedNet — replication

### `core/UnturnedNet/CombatReplication.cs`

#### `public static void WritePos(NetPakWriter w, Vector3 p)`
<sub>`core/UnturnedNet/CombatReplication.cs:339`</sub>

- **in** — ** metres; XZ clamp ±2048, Y clamp ±256.
- **out** — ** void; on writer overflow the underlying `WriteClampedFloat` fails silently (the block-length framing in `SnapshotComposer` is what contains the damage).
- **used at** `core/UnturnedNet/ZombieReplication.cs:216` — `NetWire.WritePos(w, e.Pos);`

#### `public static bool ReadPos(NetPakReader r, out Vector3 p)`
<sub>`core/UnturnedNet/CombatReplication.cs:346`</sub>

- **in** — ** reader at the position field.
- **out** — ** `true` + position; on truncation `false` with `p = default` (`Vector3.zero`) — callers must propagate the `false` rather than use the zero.
- **used at** `core/UnturnedNet/ZombieReplication.cs:226` — `if (!NetWire.ReadPos(r, out Vector3 pos)) return false;`

#### `public static void WriteDir(NetPakWriter w, Vector3 d)`
<sub>`core/UnturnedNet/CombatReplication.cs:358`</sub>

- **in** — ** a unit aim direction. A non-unit vector encodes fine and is re-normalized by the consumer (`ServerCombat.OnFire` divides by magnitude and rejects magnitude < 0.5).
- **out** — ** void.
- **used at** `core/UnturnedNet/CombatReplication.cs:25` — `NetWire.WriteDir(w, Dir);`

#### `public static bool ReadDir(NetPakReader r, out Vector3 d)`
<sub>`core/UnturnedNet/CombatReplication.cs:365`</sub>

- **out** — ** `true` + direction (magnitude ≈ 1 but not exactly); `false` with `d = default` on truncation.
- **used at** `core/UnturnedNet/CombatReplication.cs:33` — `if (!NetWire.ReadDir(r, out Vector3 dir)) return false;`

#### `public static void WriteVel(NetPakWriter w, Vector3 v)`
<sub>`core/UnturnedNet/CombatReplication.cs:375`</sub>

- **in** — ** m/s. Anything beyond ±32 clamps, so a terminal-velocity fall reads as 32 m/s.
- **out** — ** void.
- **used at** `core/UnturnedNet/PlayerAuthority.cs:57` — `NetWire.WriteVel(w, LinVel);`

#### `public static bool ReadVel(NetPakReader r, out Vector3 v)`
<sub>`core/UnturnedNet/CombatReplication.cs:382`</sub>

- **out** — ** `true` + velocity in `[-32, 32)` per axis; `false` with `v = default` on truncation.
- **used at** `core/UnturnedNet/PlayerAuthority.cs:74` — `if (!NetWire.ReadVel(r, out Vector3 vel)) return false;`

#### `public static void WriteDamage(NetPakWriter w, float damage)`
<sub>`core/UnturnedNet/CombatReplication.cs:392`</sub>

- **in** — ** HP. A >512 hit clamps (grenades cap at 175, so headroom is real).
- **out** — ** void.
- **used at** `core/UnturnedNet/CombatReplication.cs:191` — `NetWire.WriteDamage(w, Damage);`

#### `public static bool ReadDamage(NetPakReader r, out float damage)`
<sub>`core/UnturnedNet/CombatReplication.cs:393`</sub>

- **out** — ** `true` + damage in `[-512, 512)`; `false` with `damage = 0` on truncation.
- **used at** `core/UnturnedNet/CombatReplication.cs:202` — `if (!NetWire.ReadDamage(r, out float dmg)) return false;`

#### `public bool PlayerCombatReplication.IsAlive(ushort ownerPlayerId)`
<sub>`core/UnturnedNet/CombatReplication.cs:468`</sub>

The alive gate every movement/combat/vehicle validator shares.

- **out** — ** `true` if the player has no combat entity at all (**defensive default** — entities are created on `PeerConnected`, before commands can flow) or if `e.Alive`; `false` only for a known-dead player.
- **used at** `core/UnturnedNet/NetWorldHost.cs:193` — `validate: (sender, pkt) => CombatState.IsAlive(sender) && !VehicleHost.IsDriver(sender));`

### `core/UnturnedNet/CommandRegistry.cs`

#### `public void Register<T>(byte commandId, TryReadCommand<T> tryRead, Action<ushort, T> apply, Func<ushort, T, bool> validate = null)`
<sub>`core/UnturnedNet/CommandRegistry.cs:51`</sub>

- **in** — ** `tryRead` = the message's static `TryRead`; `apply(senderPlayerId, cmd)`; `validate(senderPlayerId, cmd)` — `null` means "no authority check", which is a deliberate choice at each site, not a default.
- **out** — ** void. A parse failure bumps `Diag.MalformedRejected`; a `false` from `validate` bumps `Diag.ValidationRejected`; neither reaches `apply`.
- **used at** `core/UnturnedNet/ServerTransactions.cs:144` — `commands.Register<UpgradeSkillCommand>(ReplicationIds.CommandUpgradeSkill, UpgradeSkillCommand.TryRead,`

#### `public bool TryDispatch(byte[] data, ushort senderPlayerId)`
<sub>`core/UnturnedNet/CommandRegistry.cs:75`</sub>

- **in** — ** `data` may be null or empty (both counted as malformed); `senderPlayerId` from the delivering connection.
- **out** — ** `true` only if a handler ran to completion. `false` for null/empty data, unknown id, or a handler that threw — a throwing handler is caught, counted in `Diag.HandlerExceptionsCaught`, and never rethrown (fuzz-proof by construction).
- **used at** `core/UnturnedNet/NetWorldHost.cs:284` — `while (peer.TryReceiveReliable(out byte[] msg)) Commands.TryDispatch(msg, peer.PlayerId);`

### `core/UnturnedNet/DeployableReplication.cs`

#### `public bool CanPlace(ushort defId, Vector3 pos, Vector3 senderPos)`
<sub>`core/UnturnedNet/DeployableReplication.cs:365`</sub>

Range-only gate: `|pos - senderPos| <= def.Range + PlaceRangeSlack` (4 m). Returns `false` for an unregistered defId. No collision/overlap test — the client's ghost rules are stricter by design.

- **used at** `core/UnturnedNet/ServerTransactions.cs:151` — `&& _deployables.CanPlace(cmd.DefId, cmd.Pos, pos)`

#### `public bool CanConnectWire(uint srcId, byte srcPort, uint dstId, byte dstPort, Vector3 senderPos)`
<sub>`core/UnturnedNet/DeployableReplication.cs:371`</sub>

Seven rules in order: no self-loop, both entities exist, neither on fire, both defs registered, port indices in range, src is Output/Passthrough and dst is Consumer, neither port already wired, and **both** endpoints within `WireReach` (16 m) of the sender.

- **out** — ** `false` on any rule; no reason code — the client sees silence.
- **used at** `core/UnturnedNet/ServerTransactions.cs:205` — `&& _deployables.CanConnectWire(cmd.SrcId, cmd.SrcPort, cmd.DstId, cmd.DstPort, pos));`

#### `public bool IsPortWired(uint netId, byte portIndex)`
<sub>`core/UnturnedNet/DeployableReplication.cs:386`</sub>

Linear scan over all wires for either endpoint matching `(netId, portIndex)`. O(wires) per call, called twice per connect attempt.

- **out** — ** `true` if occupied; `false` for an unknown netId too (indistinguishable).
- **used at** `core/UnturnedNet/DeployableReplication.cs:381` — `if (IsPortWired(srcId, srcPort) || IsPortWired(dstId, dstPort)) return false; // one wire per port`

#### `public bool CanToggle(uint netId, out DeployableEntity e)`
<sub>`core/UnturnedNet/DeployableReplication.cs:394`</sub>

Entity exists, def registered, `def.FuelCapacity > 0` (i.e. it is a generator, not a lamp), and not on fire.

- **out** — ** `true` + `e` set; on `false`, `e` is whatever `TryGet` left (null when the entity is missing, but **populated** when the entity exists and only the fuel/fire rule failed — callers must not read it on `false`).
- **used at** `core/UnturnedNet/ServerTransactions.cs:221` — `&& _deployables.CanToggle(cmd.NetId, out var e)`

#### `public static float QuantizeScalar(float v)`
<sub>`core/UnturnedNet/DeployableReplication.cs:627`</sub>

- **in** — ** health or fuel, or (for gas pumps) the 0..100 fill percent.
- **out** — ** grid-snapped scalar; values above 4095.75 clamp.

### `core/UnturnedNet/InteractableReplication.cs`

#### `public bool CanToggleDoor(uint netId, Vector3 senderPos, ulong player, ulong group)`
<sub>`core/UnturnedNet/InteractableReplication.cs:211`</sub>

Door exists → `|door.Pos - senderPos| <= InteractReach` (4 m) → `DoorLogic.CanToggle(state, player, group, Now, arcBlocked: false, out _)`.

- **in** — ** `player`/`group` as `ulong` owner ids (the server passes the ushort playerId widened); `Now` is *sim seconds* driven by the host tick, not wall clock, and must be refreshed **before** dispatch (see `NetWorldHost.cs:280`).
- **out** — ** `false` on any step. `arcBlocked` is hardcoded `false` — the server has no physics arc test and refusing on the client's say-so would let a client veto other people's doors.
- **used at** `core/UnturnedNet/ServerTransactions.cs:234` — `&& _interactables.CanToggleDoor(cmd.NetId, pos, sender, 0UL));`

#### `public bool CanClaimBed(uint netId, Vector3 senderPos, ulong player)`
<sub>`core/UnturnedNet/InteractableReplication.cs:243`</sub>

netId → bedId map hit, bed exists, within `InteractReach`, then `BedClaims.CanClaim(id, player, Now)`.

- **out** — ** `false` on any step.
- **used at** `core/UnturnedNet/ServerTransactions.cs:245` — `&& _interactables.CanClaimBed(cmd.NetId, pos, sender));`

### `core/UnturnedNet/NetClientSession.cs`

#### `NetClientSession`
<sub>`core/UnturnedNet/NetClientSession.cs:14`</sub>

public static void Info(string message)` / `public static void Warn(string message)

- **in** — ** axes in `[-1,1]` (quantized to 8 bits on the wire); `yawDegrees` any real (wrapped); `buttons` = `MoveInput.ButtonJump | MoveInput.PackStance(...)`.
- **out** — ** the input seq, or **`0` when not connected** (nothing sent); seq 0 is skipped on wrap because it is the reconciler's "none" sentinel.

### `core/UnturnedNet/NetHash.cs`

#### `public static ulong MixUInt64(ulong hash, ulong value)`
<sub>`core/UnturnedNet/NetHash.cs:17`</sub>

- **in** — ** running hash (seed with `NetHash.FnvOffset`); any 64-bit value.
- **out** — ** new hash. **Not commutative** — callers must sort by NetId before folding or server and client hashes diverge on dictionary order.
- **used at** `core/UnturnedNet/InventoryReplication.cs:472` — `h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunAmmo ?? -1));`

#### `public static ulong HashString(string s)`
<sub>`core/UnturnedNet/NetHash.cs:34`</sub>

- **in** — ** any string; `null` is tolerated.
- **out** — ** the u64 content hash. **Returns `FnvOffset` unchanged for `null` and for `""`** — those two are indistinguishable.
- **used at** `game/NetContent.cs:12` — `public static readonly ulong Hash = NetHash.HashString(Identity);`

### `core/UnturnedNet/NetId.cs`

#### `public NetId Mint()`
<sub>`core/UnturnedNet/NetId.cs:41`</sub>

- **in** — ** none.
- **out** — ** a fresh `NetId`; 0 is never minted (it is `NetId.Invalid`). Wraps silently past `uint.MaxValue` — no reuse guard.
- **used at** `core/UnturnedNet/NetWorldHost.cs:206` — `var e = Players.ServerSpawn(Ids.Mint(), peer.PlayerId, spawn, Session.CurrentTick);`

### `core/UnturnedNet/NetMessagePak.cs`

#### `public static byte[] Pack(byte messageId, Action<NetPakWriter> writePayload, int bufferSize = 256)`
<sub>`core/UnturnedNet/NetMessagePak.cs:13`</sub>

- **in** — ** `messageId` = a `ReplicationIds.Command*`/`Event*` byte (0..255; 0 is reserved for the snapshot ack); `writePayload` = payload writer, null is tolerated and yields a 1-byte message; `bufferSize` = scratch bytes, must exceed the payload or `NetPakWriter` silently truncates at flush.
- **out** — ** exactly `writeByteIndex` bytes, ready for `SendReliable`/`SendUnreliableSequenced`. Never null; a null payload writer returns `[messageId]`.
- **used at** `game/ZombieNetSync.cs:87` — `_server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventAttackSwing, evt.Write));`

### `core/UnturnedNet/NetProtocol.cs`

#### `public static bool WriteHeader(NetPakWriter writer, in Header h)`
<sub>`core/UnturnedNet/NetProtocol.cs:106`</sub>

- **in** — ** `h.MagicByte` must be `NetProtocol.Magic` (0x75); `h.Channel` 0..2; `h.Seq` 1..65535 (0 = "none", never sent); `h.Ack` 0 = nothing received yet; `h.AckBits` bit n ⇒ `Ack-1-n` received.
- **out** — ** `true` if all six field writes fit; `false` if the writer overflowed (header partially written — caller must discard the datagram).
- **used at** `core/UnturnedNet/NetServerSession.cs:240` — `NetProtocol.WriteHeader(_rawWriter, new NetProtocol.Header`

#### `public static bool TryReadHeader(NetPakReader reader, out Header h)`
<sub>`core/UnturnedNet/NetProtocol.cs:117`</sub>

- **in** — ** a reader positioned at datagram byte 0.
- **out** — ** `true` + populated `h`; on truncation returns `false` with `h = default` (`MagicByte` 0, which fails the caller's magic check anyway).
- **used at** `core/UnturnedNet/NetSession.cs:150` — `if (!NetProtocol.TryReadHeader(_reader, out var h) || h.MagicByte != NetProtocol.Magic)`

#### `public static bool NetSeq.IsNewer(ushort a, ushort b)`
<sub>`core/UnturnedNet/NetProtocol.cs:143`</sub>

- **in** — ** two 16-bit sequence numbers / msgIds / input seqs. Equal values return `false`.
- **out** — ** `true`/`false`; no failure path. Meaningless if the two values are more than 32767 apart (aliases).
- **used at** `core/UnturnedNet/NetSession.cs:312` — `if (_lastUnreliableSeq != 0 && !NetSeq.IsNewer(datagramSeq, _lastUnreliableSeq))`

#### `public static bool NetSeq.IsNewerOrEqual(ushort a, ushort b)`
<sub>`core/UnturnedNet/NetProtocol.cs:145`</sub>

- **used at** `core/UnturnedNet/NetSession.cs:219` — `if (!NetSeq.IsNewerOrEqual(msgId, _nextDeliverMsgId))`

#### `public static int NetSeq.Diff(ushort a, ushort b)`
<sub>`core/UnturnedNet/NetProtocol.cs:148`</sub>

- **in** — ** two seqs. **output:** `[-32768, 32767]`; no sentinel — an out-of-window pair silently aliases to a small number, which is why callers gate on `RecvWindowMessages`.
- **used at** `core/UnturnedNet/NetSession.cs:224` — `if (NetSeq.Diff(msgId, _nextDeliverMsgId) >= NetProtocol.RecvWindowMessages)`

### `core/UnturnedNet/NetQuantization.cs`

#### `public static float QuantizeClampedFloat(float value, int intBits, int fracBits)`
<sub>`core/UnturnedNet/NetQuantization.cs:37`</sub>

- **in** — ** `value` in any unit; `intBits` sets range `±2^(intBits)`; `fracBits` sets grain `1/2^fracBits`. Out-of-range values **clamp**, they do not wrap.
- **out** — ** the grid-snapped value. No failure path — a NaN input produces whatever the bit encoder makes of it, so callers validate finiteness upstream (see `ServerTransactions.RunConsole` teleport).
- **used at** `core/UnturnedNet/DeployableReplication.cs:627` — `public static float QuantizeScalar(float v) => NetQuantization.QuantizeClampedFloat(v, 12, 2);`

#### `public static float QuantizeSignedNormalizedFloat(float value, int bitCount)`
<sub>`core/UnturnedNet/NetQuantization.cs:52`</sub>

- **in** — ** `value` expected in `[-1,1]` (caller clamps first; the encoder does not); `bitCount` typically 8.
- **out** — ** the quantized axis value; no failure path.
- **used at** `core/UnturnedNet/Prediction.cs:152` — `MoveX = NetQuantization.QuantizeSignedNormalizedFloat(Clamp1(moveX), 8),`

#### `public static float QuantizeUnsignedNormalizedFloat(float value, int bitCount)`
<sub>`core/UnturnedNet/NetQuantization.cs:69`</sub>

- **in** — ** `value` in `[0,1]` (caller clamps); `bitCount` typically `PlayerVitalsReplication.VitalsBits` = 8.
- **out** — ** quantized 0..1; idempotent (re-running it on an already-quantized value is a no-op), which is what lets `StateHashFor` match the replica exactly.
- **used at** `core/UnturnedNet/PlayerVitalsReplication.cs:214` — `static float Q(float v) => NetQuantization.QuantizeUnsignedNormalizedFloat(Clamp01(v), VitalsBits);`

#### `public static float QuantizeDegrees(float value, int bitCount)`
<sub>`core/UnturnedNet/NetQuantization.cs:82`</sub>

- **in** — ** any degree value including negatives (−90 comes back as 270); `bitCount` = `YawBits`/`PitchBits` (11) or `VehicleReplication.SteerBits` (9).
- **out** — ** wrapped, grid-snapped degrees in `[0,360)`. This wrap is why `VehicleEntity.SteerSigned` exists to undo it.
- **used at** `core/UnturnedNet/ZombieReplication.cs:93` — `float newYaw = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits);`

### `core/UnturnedNet/NetServerSession.cs`

#### `NetServerSession`
<sub>`core/UnturnedNet/NetServerSession.cs:34`</sub>


#### `public NetPeer FindPeer(ushort playerId)`
<sub>`core/UnturnedNet/NetServerSession.cs:302`</sub>

- **in** — ** playerId 1..65535 (0 is the never-minted "none" sentinel).
- **out** — ** the `NetPeer`, or **`null`** if not connected — callers use `?.`.
- **used at** `core/UnturnedNet/NetWorldHost.cs:271` — `Session.FindPeer(playerId)?.SendReliable(message);`

### `core/UnturnedNet/PlayerAuthority.cs`

#### `public void Register(CommandRegistry commands)`
<sub>`core/UnturnedNet/PlayerAuthority.cs:259`</sub>


#### `public bool TryGetDrivenState(ushort playerId, out DrivenPlayerState state)`
<sub>`core/UnturnedNet/PlayerAuthority.cs:273`</sub>

- **out** — ** `true` + state; `false` with `state = default` (zero pos, `Stance` = STAND) when the owner has never had a claim adopted.
- **used at** `game/PlayerNetSync.cs:85` — `EPlayerStance stance = _server.PlayerHost.TryGetDrivenState(e.OwnerPlayerId, out var st) ? st.Stance : EPlayerStance.STAND;`

#### `public bool RepositionOwner(ushort playerId, Vector3 pos, long tick)`
<sub>`core/UnturnedNet/PlayerAuthority.cs:451`</sub>

- **in** — ** `pos` in metres; `tick` = current server tick.
- **out** — ** `true` if it handled the reposition; **`false` when this owner has never sent a `PlayerStateCommand`** — the caller must then fall back to a plain `ServerTeleport`.
- **used at** `core/UnturnedNet/ServerCombat.cs:337` — `if (RepositionOwner == null || !RepositionOwner(cs.OwnerPlayerId, where, tick))`

### `core/UnturnedNet/PlayerReplication.cs`

#### `public bool TryGetByOwner(ushort ownerPlayerId, out PlayerEntity entity)`
<sub>`core/UnturnedNet/PlayerReplication.cs:311`</sub>

- **in** — ** playerId.
- **out** — ** `true` + entity; `false` with `entity = null` for an unknown/disconnected owner. This is the single "where is player X" lookup the whole server side goes through.
- **used at** `game/ClientWorldSession.cs:293` — `if (Client.Players.TryGetByOwner(Client.PlayerId, out var me)) SpawnShell(me);`

#### `public static Vector3 Quantize(Vector3 pos)`
<sub>`core/UnturnedNet/PlayerReplication.cs:458`</sub>

- **in** — ** a world position in metres. A Y below −256 pins at −256 (which is exactly why `ServerPlayerAuthority.OutOfBoundsFloorY` is −250, not the SP shell's −1030).
- **out** — ** grid-snapped position; never fails, silently clamps out-of-map coordinates.
- **used at** `core/UnturnedNet/ZombieReplication.cs:77` — `Pos = PlayerReplication.Quantize(pos),`

### `core/UnturnedNet/Relevancy.cs`

#### `public bool InterestPolicy.IsRelevant(Vector3 viewPos, Vector3 entityPos)`
<sub>`core/UnturnedNet/Relevancy.cs:25`</sub>

- **in** — ** both positions in metres. `RingRadius <= 0` disables the ring (cells only). `CellOf` returns a cell id, or **−1 for "no cell"** (open country) — two entities both in "no cell" are *not* relevant to each other.
- **out** — ** `true`/`false`; a null `CellOf` means rings-only. A whole `InterestPolicy` of `null` on a system means AllRelevant, byte-identical to pre-Phase-8.

#### `public bool RelevancyTracker.ShouldWrite(ushort clientPlayerId, uint netId, bool relevant, long entityChangedTick, long baselineTick, long serverTick)`
<sub>`core/UnturnedNet/Relevancy.cs:70`</sub>

- **in** — ** ticks in server ticks; `relevant` is the caller's `IsRelevant` result.
- **out** — ** `true` = write this entity into this delta. When `relevant` is false it starts an exit-pending record and returns `false`.
- **used at** `core/UnturnedNet/WorldItemReplication.cs:229` — `else if (_relevancy.ShouldWrite(ctx.ClientPlayerId, id, Interest.IsRelevant(ctx.ViewPos, e.Pos),`

#### `public void CollectRemovals(ushort clientPlayerId, long baselineTick, List<uint> removals)`
<sub>`core/UnturnedNet/Relevancy.cs:87`</sub>

- **in** — ** `removals` must be non-null; the method **appends**, it does not clear.
- **out** — ** void; mutates both `removals` and internal state.
- **used at** `core/UnturnedNet/ZombieReplication.cs:159` — `if (Interest != null) _relevancy.CollectRemovals(ctx.ClientPlayerId, baselineTick, removed);`

### `core/UnturnedNet/ServerCombat.cs`

#### `public void DamagePlayerExternal(ushort victimPlayerId, float damage, ushort attackerPlayerId = 0)`
<sub>`core/UnturnedNet/ServerCombat.cs:227`</sub>

- **in** — ** `damage` in HP, positive; `attackerPlayerId` 0 = environment (no kill credit, `Killer` 0 in the event).
- **out** — ** void, always. Not PvP-gated (`PvPEnabled` governs target *selection*, never the HP sink). Damage to an unknown or already-dead victim is dropped inside `ApplyPlayerDamage`.
- **used at** `game/PlayerNetSync.cs:66` — `body.NetDamageSink = amount => _server.Combat.DamagePlayerExternal(owner, amount);`

#### `public void OnFire(ushort sender, in FireCommand cmd, long tick)`
<sub>`core/UnturnedNet/ServerCombat.cs:240`</sub>

- **in** — ** `cmd.Origin` in metres, `cmd.Dir` a unit vector (re-normalized here), `cmd.Seq` echoed back in `HitConfirm`.
- **out** — ** void. On acceptance, consumes one ammo and queues `max(1, Pellets)` bullets. A rejected shot costs no ammo.

#### `public void OnReload(ushort sender, in ReloadCommand cmd, long tick)`
<sub>`core/UnturnedNet/ServerCombat.cs:270`</sub>


#### `public void OnMelee(ushort sender, in MeleeCommand cmd, long tick)`
<sub>`core/UnturnedNet/ServerCombat.cs:278`</sub>

- **in** — ** `cmd.YawDegrees` is the shell's `RotationDegrees.Y` verbatim (Godot frame).
- **out** — ** void; rejections bump `Diag.MeleeRejected`.

#### `public void OnGrenade(ushort sender, in GrenadeCommand cmd, long tick)`
<sub>`core/UnturnedNet/ServerCombat.cs:287`</sub>


#### `public void Step(long tick)`
<sub>`core/UnturnedNet/ServerCombat.cs:303`</sub>

- **in** — ** the current server tick.
- **out** — ** void. Note the `== tick` equality checks — if `Step` is not called every tick, reloads and respawns are silently skipped forever.
- **used at** `core/UnturnedNet/NetWorldHost.cs:296` — `Combat.Step(Session.CurrentTick);`

#### `internal static bool SegmentHitsCylinder(Vector3 p0, Vector3 p1, Vector3 feet, float radius, float top, out float t, out float relY)`
<sub>`core/UnturnedNet/ServerCombat.cs:629`</sub>

- **in** — ** `p0`/`p1` = one tick's bullet segment (metres); `feet` = target's ground position; `radius` = 0.42 for players / 0.4 for zombies; `top` = 1.8 for players, `ZombieReplication.HeightFor(speciality)` for zombies. The height band is padded to `[-0.1, top + 0.15]`.
- **out** — `** `true` on a hit, with `t` ∈ `[0,1]` = the fraction along the segment (used to pick the *nearest* of several candidates) and `relY` = hit height above the feet (the zone multiplier keys on it: ≥1.45 head, ≥0.78 torso, else leg). For a near-vertical segment (`axz < 1e-8`) `t` is forced to 0 and `relY` to `p0.y - feet.…
- **used at** `core/UnturnedNet/ServerCombat.cs:363` — `if (SegmentHitsCylinder(b.Pos, next, pe.Pos, PlayerZoneRadius, PlayerZoneTopY, out float t, out float relY) && t < bestT)`

### `core/UnturnedNet/ServerTransactions.cs`

#### `public bool MayModify(ushort sender, DeployableReplication.DeployableEntity e)`
<sub>`core/UnturnedNet/ServerTransactions.cs:82`</sub>

Ownership gate for salvage/pickup/wire/toggle. Returns `true` when `EnforceOwnership` is off (the default), when `e` is null, when `e.OwnerPlayerId == 0` (world fixtures — street lamps, gas pumps, grid mains stay usable by everyone), or when the sender owns it.

- **used at** `core/UnturnedNet/ServerTransactions.cs:162` — `&& MayModify(sender, e)`

#### `public void Register(CommandRegistry commands)`
<sub>`core/UnturnedNet/ServerTransactions.cs:142`</sub>

- **used at** `core/UnturnedNet/NetWorldHost.cs:101` — `Transactions.Register(Commands);`

#### `public CropReplication.CropEntity PlantCrop(ushort seedId, Vector3 pos, bool grown)`
<sub>`core/UnturnedNet/ServerTransactions.cs:609`</sub>

- **in** — ** `seedId` must be in the `CropSchema`; `pos` in metres (quantized inside); `grown` = spawn pre-matured (the console `plant <crop> grown` path).
- **out** — ** the entity, or **`null`** if the seed isn't in the schema (nothing broadcast in that case).
- **used at** `game/WorldNetSync.cs:125` — `var e = _server.Transactions.PlantCrop(c.Crop.Def.Id,`

#### `public bool SetResourceAlive(int index, bool alive)`
<sub>`core/UnturnedNet/ServerTransactions.cs:651`</sub>

- **in** — ** `index` in the authored resource index space (out-of-range is rejected inside `ServerSetAlive`).
- **out** — ** `false` when `_resources` is null (host has no resource system) or the bit was already at that value — nothing broadcast either way.
- **used at** `game/WorldNetSync.cs:219` — `if (!_server.Transactions.SetResourceAlive(index, alive)) return false;`

#### `public string RunConsole(ushort sender, string text)`
<sub>`core/UnturnedNet/ServerTransactions.cs:676`</sub>

- **in** — ** `sender`; `text` — the dispatch-side validator already caps it at 128 chars and rejects null. Teleport coordinates are parsed with `InvariantCulture` and **rejected if non-finite** (NaN/Inf would poison the replicated position); a seated sender is rejected (the seat teleport would win the fight).
- **out** — ** the human-readable result line, always non-null — the failure path returns an explanatory string (`"console commands are disabled on this server"`, `"no item matching 'x'"`, `"usage: …"`) and bumps `Diag.ConsoleRejected`.
- **used at** `core/UnturnedNet/ServerTransactions.cs:669` — `string reply = RunConsole(sender, cmd.Text ?? "");`

#### `public uint AwardXp(ushort playerId, uint amount)`
<sub>`core/UnturnedNet/ServerTransactions.cs:779`</sub>

- **out** — ** the new total. **Returns 0 for a player with no skills entry** — and still sends an event carrying `TotalExperience = 0`.

#### `public WorldItemReplication.WorldItemEntity SpawnWorldItem(Item item, Vector3 pos, Vector3 vel)`
<sub>`core/UnturnedNet/ServerTransactions.cs:788`</sub>

- **in** — ** `item` (server keeps the reference); `pos` metres; `vel` m/s, clamped to ±32 by `NetWire.WriteVel` on the wire.
- **out** — ** the entity; never null.
- **used at** `game/WorldItemNetSync.cs:47` — `var e = _server.Transactions.SpawnWorldItem(wi.Item,`

#### `public void SettleWorldItem(uint netId, Vector3 pos)`
<sub>`core/UnturnedNet/ServerTransactions.cs:807`</sub>

- **out** — ** void; silently no-ops for an unknown netId or an already-settled item (settling is one-way).
- **used at** `game/WorldItemNetSync.cs:56` — `_server.Transactions.SettleWorldItem(netId, new UnityEngine.Vector3(sp.X, sp.Y, sp.Z));`

### `core/UnturnedNet/SnapshotApplier.cs`

#### `DesyncReport`
<sub>`core/UnturnedNet/SnapshotApplier.cs:19`</sub>

public override string ToString()` (`:26`) — formats `"desync at server tick N: system S server hash X != client hash Y"` (hashes in `x16`). Caller: `core/UnturnedNet/SnapshotApplier.cs:157`.


#### `public bool Apply(byte[] data, int length)`
<sub>`core/UnturnedNet/SnapshotApplier.cs:71`</sub>

- **in** — ** `data`/`length` as delivered.
- **out** — ** `false` **only** for a malformed/truncated datagram (bad framing header, or a block whose declared byteLen runs past the buffer), counted in `Diag.TruncatedSnapshotsDropped`. An unrecognized-but-well-formed `systemId` is **not** an error — it is skipped and counted in `UnknownSystemBlocksSkipped` (the forward-compat…
- **used at** `core/UnturnedNet/NetWorldHost.cs:633` — `bool ApplySnapshot(byte[] data, int length) => Applier.Apply(data, length);`

### `core/UnturnedNet/SnapshotComposer.cs`

#### `public void EnableSyncCheck(int intervalTicks, params byte[] systemIds)`
<sub>`core/UnturnedNet/SnapshotComposer.cs:129`</sub>

- **in** — ** `intervalTicks` > 0 (**throws `ArgumentOutOfRangeException`** otherwise); `systemIds` must all be registered on this composer (**throws `InvalidOperationException`** otherwise). Only list systems every client mirrors *completely* — owner-only and relevancy-filtered systems false-alarm by design.
- **out** — ** void. The block is withheld whenever any checked system's block was budget-skipped in the same compose.
- **used at** `core/UnturnedNet/NetWorldHost.cs:256` — `Composer.EnableSyncCheck(intervalTicks,`

#### `public void SetClientBaseline(ushort clientPlayerId, long baselineTick)`
<sub>`core/UnturnedNet/SnapshotComposer.cs:186`</sub>

- **in** — ** `baselineTick` from the client's ack. A tick **greater than `CurrentTick()`** is rejected and counted in `Diag.FutureBaselineAcksRejected` (review L1 — a client acking `0xFFFFFFFF` would otherwise starve its own deltas forever). Ticks ≤ the current baseline are silently ignored.
- **out** — ** void.
- **used at** `core/UnturnedNet/SnapshotComposer.cs:176` — `if (reader.ReadUInt32(out uint tick)) SetClientBaseline(senderPlayerId, tick);`

#### `public bool WillSendFull(ushort clientPlayerId, long serverTick)`
<sub>`core/UnturnedNet/SnapshotComposer.cs:212`</sub>

- **out** — ** `true`/`false`. **Side effect:** creates the client's state record if absent.
- **used at** `core/UnturnedNet/NetWorldHost.cs:400` — `if (Composer.WillSendFull(peer.PlayerId, Session.CurrentTick))`

#### `public byte[] Compose(long serverTick, ushort clientPlayerId, Vector3 viewPos, int maxBytes = 0)`
<sub>`core/UnturnedNet/SnapshotComposer.cs:226`</sub>

- **in** — ** `serverTick`; `clientPlayerId`; `viewPos` = the owning player's position (the §2.6 interest hook — relevancy-filtered systems read it); `maxBytes` ≤ 0 falls back to `BudgetBytes` (= `MaxUnreliablePayload`, 1187). The join/recovery path passes `MaxReliableMessageBytes / 2`.
- **out** — ** a right-sized `byte[]`, never null and never empty. A block that would overflow the budget is emitted as an **empty** (byteLen 0) block — framing stays valid, `Diag.OversizedBlocksSkipped` increments, that system's priority accumulator grows, and its per-client baseline pins so the next included delta carries everyt…
- **used at** `core/UnturnedNet/NetWorldHost.cs:413` — `peer.SendUnreliableSequenced(Composer.Compose(Session.CurrentTick, peer.PlayerId, viewPos));`

### `core/UnturnedNet/VehicleReplication.cs`

#### `public bool IsDriver(ushort playerId)`
<sub>`core/UnturnedNet/VehicleReplication.cs:885`</sub>

- **used at** `game/PlayerNetSync.cs:96` — `if (moved && t.FootNoiseTicks <= 0 && !_server.VehicleHost.IsDriver(e.OwnerPlayerId))`

#### `public bool TryGetDriven(ushort playerId, out uint vehicleNetId)`
<sub>`core/UnturnedNet/VehicleReplication.cs:887`</sub>


#### `public bool CanEnter(ushort sender, uint netId)`
<sub>`core/UnturnedNet/VehicleReplication.cs:889`</sub>

Vehicle exists, seat empty (`DriverPlayerId == 0`), not exploded, sender not already driving, sender alive, and `|v.Pos - p.Pos| <= EnterReach` (6 m, from vehicle centre).

- **used at** `core/UnturnedNet/VehicleReplication.cs:774` — `validate: (sender, cmd) => CanEnter(sender, cmd.NetId));`

#### `public bool ServerExit(ushort playerId)`
<sub>`core/UnturnedNet/VehicleReplication.cs:915`</sub>

- **in** — ** playerId.
- **out** — ** `false` (idempotent) if the player wasn't driving. If the vehicle entity has already despawned the exit still succeeds but the broadcast spot is `Vector3.zero`, the documented "no spot, fall back locally" sentinel.
- **used at** `game/VehicleNetSync.cs:202` — `_server.VehicleHost.ServerExit(driver);`

#### `public void Step(long tick)`
<sub>`core/UnturnedNet/VehicleReplication.cs:954`</sub>

- **used at** `core/UnturnedNet/NetWorldHost.cs:288` — `VehicleHost.Step(Session.CurrentTick); // drivers ride their vehicle entity; dead drivers exit`

### `core/UnturnedNet/WorldReplication.cs`

#### `public static float QuantizeBase(float value01)`
<sub>`core/UnturnedNet/WorldReplication.cs:57`</sub>

- **in** — ** time of day at tick 0, any real (1.25 → 0.25); 16 bits ≈ 1.3 s of a PEI hour-long day.
- **out** — ** the quantized base; no failure path.
- **used at** `core/UnturnedNet/WorldReplication.cs:41` — `float b = QuantizeBase(baseTime01);`

#### `public bool IsGrown(CropEntity e, long tick)`
<sub>`core/UnturnedNet/WorldReplication.cs:249`</sub>

Both sides' agreed maturity check: forced-grown flag, or `tick - PlantedAtTick >= def.GrowthSeconds * 50` (50 ticks/s).

- **out** — ** `false` for an unknown seed id (nothing to grow into).
- **used at** `core/UnturnedNet/ServerTransactions.cs:329` — `&& _crops.IsGrown(e, _tick()));`

### `core/UnturnedNet/ZombieReplication.cs`

#### `public static float HeightFor(byte speciality)`
<sub>`core/UnturnedNet/ZombieReplication.cs:25`</sub>

- **in** — ** `ZombieController.ESpeciality` byte (0 NORMAL … 5 ACID); only `SpecialityCrawler` (2) is distinguished.
- **out** — ** `0.8f` for crawlers, `1.8f` for everything else — including unknown/out-of-range bytes (fails to the tall hitbox, never zero).
- **used at** `core/UnturnedNet/ServerCombat.cs:370` — `float top = ZombieReplication.HeightFor(ze.Speciality);`


## game/inventory

### `game/inventory/CraftingUI.cs`

#### `static string Describe(BlueprintDef bp)`
<sub>`game/inventory/CraftingUI.cs:147`</sub>

Recipe → `"Result x2 < 3x Input, 1x Tool (tool) [station] [Crafting 2]"`. For a target-op (Repair/Salvage) it prints operation + owned item instead of an output.

- **in** — `bp` non-null; unresolvable GUIDs render as `"?"`/`"item"`.
- **out** — always a string.

### `game/inventory/ItemTool.cs`

#### `public static Godot.Color RarityColorUI(EItemRarity r)`
<sub>`game/inventory/ItemTool.cs:9`</sub>

Byte-exact port of `ItemTool.getRarityColorUI`. **The repo-wide item/vehicle rarity palette.**

- **in** — `EItemRarity` COMMON..MYTHICAL.
- **out** — the colour; `Colors.White` for COMMON and any unmapped value.
- **used at** `game/Vehicle.cs:1047` — `p.OutlineColor = ItemTool.RarityColorUI(s.Rarity); // match the real vehicle's look-at rim colour (line 931)`

#### `public static Godot.Color QualityColor(float q01)`
<sub>`game/inventory/ItemTool.cs:21`</sub>

Condition ramp red→yellow→green, `Lerp`ing `#bf1f1f`/`#dcb413`/`#1f871f` with the pivot at 0.5.

- **in** — `q01` = quality/100, clamped to [0,1] internally, so raw 0–100 input silently pins to green.
- **out** — the colour; never fails.
- **used at** `game/inventory/InventoryUI.cs:1445` — `var qcol = ItemTool.QualityColor(q / 100f);`

### `inventory/LootField.cs`

#### `public void LoadFromPei(string peiRoot)`
<sub>`inventory/LootField.cs:32`</sub>

- **in** — ** `peiRoot` = the map directory.
- **out** — ** void. **Both files are independently optional** — a missing `Items.dat` leaves `_tiers` null (every `Roll` returns −1 → colour/name-only markers); a missing `Jars.dat` leaves zero points and no loot at all. Neither logs an error.
- **used at** `game/WorldBuilder.cs:681` — `loot.LoadFromPei(mapRoot);`

### `inventory/WorldItem.cs`

#### `public static bool SuppressLocalVisual;`
<sub>`inventory/WorldItem.cs:30`</sub>

- **used at** `game/MpLoopback.cs:226` — `WorldItem.SuppressLocalVisual = true;`

#### `public static Color FocusColor = Colors.White;`
<sub>`inventory/WorldItem.cs:31`</sub>


#### `public const uint ItemHitLayer = 1u << 7; // = 128`
<sub>`inventory/WorldItem.cs:33`</sub>


#### `public bool Settled => _settled;`
<sub>`inventory/WorldItem.cs:47`</sub>

- **used at** `game/WorldItemNetSync.cs:53` — `if (wi.Settled && _server.WorldItems.TryGet(netId, out var ent) && !ent.Settled)`

#### `public bool LocalVisualSuppressed => _suppressed;`
<sub>`inventory/WorldItem.cs:48`</sub>


#### `public bool HasLineOfSightFrom(Vector3 eye)`
<sub>`inventory/WorldItem.cs:62`</sub>

- **in** — ** `eye` in world metres.
- **out** — ** `true` if any sample is clear. **Returns `false` if not in the tree or `_suppressed`; returns `true` when there is no physics world** (headless/test) so loot is not hidden behind a missing raycast. Breaks on the first clear sample — a visible item costs 1 ray, a walled one costs 9.
- **used at** `game/PlayerController.cs:2258` — `&& wi.HasLineOfSightFrom(eye)) // don't list loot through a wall (source: Linecast, BLOCK_PICKUP)`

#### `public static MeshInstance3D BuildReplicaVisual(ushort itemId, Color rarity)`
<sub>`inventory/WorldItem.cs:153`</sub>

- **in** — ** `itemId` = Unturned item id; **0 means "no model, use the box"**. `rarity` = tint used only on the fallback/flat-colour paths.
- **out** — ** always a non-null `MeshInstance3D`; the empty path is the rarity box, never null.
- **used at** `game/inventory/StoreShelf.cs:282` — `var vis = WorldItem.BuildReplicaVisual(id, rar);`

#### `public static WorldItemPuppet BuildItemPuppet(ushort itemId, Color rarity, string name)`
<sub>`inventory/WorldItem.cs:174`</sub>

- **in** — ** `itemId` (0 → 24 cm fallback box, hitbox 0.276 m after the ×1.15); `rarity` colour; `name` — **null/empty renders as `"?"`**.
- **out** — ** always non-null `WorldItemPuppet`.
- **used at** `game/MpLoopback.cs:215` — `// walks Client.WorldItems.All into focusable item puppets (WorldItem.BuildItemPuppet),`

#### `public static WorldItem Spawn(Node parent, Item item, Vector3 pos, Color? fallbackColor = null, string fallbackName = null)`
<sub>`inventory/WorldItem.cs:212`</sub>

- **in** — ** `item` may be **null** (unknown loot id) → the item renders as a 24 cm rarity box using `fallbackColor`/`fallbackName`; `fallbackColor`/`fallbackName` are the loot-table tint and label ("Military Canada", "Food") for ids with no registered asset.
- **out** — ** always non-null `WorldItem`, joined to the `"worlditems"` group at `_Ready`.
- **used at** `game/inventory/LootField.cs:163` — `_live[idx] = WorldItem.Spawn(this, item, pos, _tblColor[p.Type], _tblName[p.Type]);`

#### `public void SetFocused(bool on)`
<sub>`inventory/WorldItem.cs:328`</sub>

- **out** — ** void; a `_suppressed` node ignores it entirely.
- **used at** `game/PlayerController.cs:258` — `_focusItem?.SetFocused(true);`

#### `public partial class WorldItemPuppet : Node3D, IPuppetFocusable`
<sub>`inventory/WorldItem.cs:421`</sub>



## game

### `AnimalField.cs`

#### `public void LoadFromPei(string peiRoot)`
<sub>`AnimalField.cs:34`</sub>

- **out** — ** void; **silently returns if the file is missing** (no log). Streaming radii are private: `SpawnR = 130f`, `DespawnR = 165f`, `MaxLive = 36`.
- **used at** `game/WorldBuilder.cs:703` — `animals.LoadFromPei(mapRoot);`

### `CropManager.cs`

#### `public static double Now => _inst?._clock ?? 0;`
<sub>`CropManager.cs:35`</sub>

- **used at** `game/WorldNetSync.cs:121` — `bool grown = c.Crop.IsFullyGrown(CropManager.Now);`

#### `public static bool Active => _inst != null;`
<sub>`CropManager.cs:36`</sub>

- **used at** `game/DevConsole.cs:261` — `if (!CropManager.Active)`

#### `public static CropNode Plant(string cropName, Vector3 pos, bool grown = false)`
<sub>`CropManager.cs:39`</sub>

- **in** — ** `cropName` **case-insensitive** (lower-cased internally) — "carrot", "corn", "wheat", "potato", "tomato", "pumpkin"…; `pos` in world metres, used verbatim as `GlobalPosition` (no ground snap).
- **out** — ** the `CropNode`, added to group `"crop"`. **Returns `null` if no `CropManager` is in the scene, or the name is unknown.**
- **used at** `game/WorldNetSync.cs:151` — `var node = CropManager.Plant(name, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z),`

#### `public static bool Harvest(CropNode crop, PlayerController by)`
<sub>`CropManager.cs:58`</sub>

- **in** — ** `crop` may be null; `by` may be **null**, in which case the crop is still destroyed but **no item drops and no XP is awarded** — a silent loot loss.
- **out** — ** `true` on harvest. **`false` if `crop`/`crop.Crop` is null or the crop is not fully grown** — nothing is consumed on false.
- **used at** `game/PlayerController.cs:3513` — `else if (CropManager.NearestGrown(GlobalPosition) is CropNode grownCrop) CropManager.Harvest(grownCrop, this); // harvest a nearby fully-gro…`

#### `public static CropNode NearestGrown(Vector3 from, float reach = 3.0f)`
<sub>`CropManager.cs:78`</sub>

- **in** — ** `from` in world metres; **`reach` default 3.0 m is the E-harvest reach rule** — the MP path at `PlayerController.cs:2609` re-states "~3 m, the SP `CropManager.NearestGrown` reach" as a separate literal rather than referencing this default.
- **out** — ** the nearest crop, or **`null` if no manager exists / nothing grown in range**.
- **used at** `game/PlayerController.cs:2609` — `/// crop (~3 m, the SP CropManager.NearestGrown reach) asks the server to harvest it. Scans the "crop"`

### `DatComment.cs`

#### `public bool AreMessageLinesNullOrEmpty { get; }`
<sub>`DatComment.cs:42`</sub>


#### `public string JoinLines(char separator)`
<sub>`DatComment.cs:62`</sub>


#### `public string MessageWithLineBreaks { get; set; }`
<sub>`DatComment.cs:95`</sub>


#### `public void DebugDumpToStringBuilder(System.Text.StringBuilder output, int indentationLevel = 0)`
<sub>`DatComment.cs:118`</sub>


### `DatDictionaryEx.cs`

#### `public static bool TryGetValue(this IDatDictionary dictionary, string key, out IDatValue node)`
<sub>`DatDictionaryEx.cs:11`</sub>

Resolves a key to a scalar value node. In: `key` (case-insensitive). Out: `true` + non-null node only when the key exists *and* the node is an `IDatValue`; otherwise `false` with `node = null`.

- **used at** `tests/UnturnedDat.Tests/DatParserMetadataInlineCommentTests.cs:63` — `dictionary.GetDictionary("dict").TryGetValue(key, out IDatValue value);`

#### `public static bool TryGetDictionary(this IDatDictionary dictionary, string key, out IDatDictionary node)`
<sub>`DatDictionaryEx.cs:19`</sub>

Sub-dictionary lookup. Returns `false`/`null` if missing or wrong node type.

- **used at** `tests/UnturnedDat.Tests/DatListTests.cs:20` — `Assert.IsFalse(list.TryGetDictionary(0, out IDatDictionary dict0));`

#### `public static IDatDictionary GetDictionary(this IDatDictionary dictionary, string key)`
<sub>`DatDictionaryEx.cs:27`</sub>

Same, non-`Try` form. Failure path returns **`null`** (no default parameter).

- **used at** `tests/UnturnedDat.Tests/DatParserMetadataInlineCommentTests.cs:63` — `dictionary.GetDictionary("dict").TryGetValue(key, out IDatValue value);`

#### `public static bool TryGetList(this IDatDictionary dictionary, string key, out IDatList node)`
<sub>`DatDictionaryEx.cs:32`</sub>

Sub-list lookup; `false`/`null` on missing or wrong type.

- **used at** `tests/UnturnedDat.Tests/DatStructTests.cs:89` — `Assert.IsTrue(rootDictionary.TryGetList("list", out IDatList list));`

#### `public static IDatList GetList(this IDatDictionary dictionary, string key)`
<sub>`DatDictionaryEx.cs:40`</sub>

List lookup returning **`null`** when absent — the idiomatic "optional array" reader.

- **used at** `core/UnturnedSim/BlueprintDef.cs:31` — `IDatList bps = d?.GetList("Blueprints");`

#### `public static bool TryGetString(this IDatDictionary dictionary, string key, out string value)`
<sub>`DatDictionaryEx.cs:45`</sub>

Raw string fetch. Returns `true` with `value` possibly **`null`** for a bare-flag key; `false` with `value = null` when missing/wrong type.


#### `public static string GetString(this IDatDictionary dictionary, string key, string defaultValue = default)`
<sub>`DatDictionaryEx.cs:59`</sub>

String fetch with default. `defaultValue` defaults to `null`. **Returns `null` (not `defaultValue`) for a bare-flag key** — see contract above.

- **used at** `game/GunDef.cs:61` — `Id = d.GetString("ID"),`

#### `TryParseInt8(this IDatDictionary, string key, out sbyte value)`
<sub>`DatDictionaryEx.cs:65`</sub>

sbyte` in `[-128, 127]`. Missing/unparseable/out-of-range → `false`+`0`, or `defaultValue` (which defaults to `0`).


#### `TryParseUInt8(this IDatDictionary, string key, out byte value)`
<sub>`DatDictionaryEx.cs:76`</sub>

byte` in `[0, 255]`; `"-1"` fails (returns default). Used for grid sizes and clothing dimensions.


#### `TryParseInt16(...out short)`
<sub>`DatDictionaryEx.cs:87`</sub>

[-32768, 32767]`; default `0`.


#### `TryParseUInt16(...out ushort)`
<sub>`DatDictionaryEx.cs:98`</sub>

[0, 65535]` — the item-ID width. Default `0`, which is also Unturned's "no item" sentinel.


#### `TryParseInt32(...out int)`
<sub>`DatDictionaryEx.cs:109`</sub>

Default `0`.


#### `TryParseUInt32(...out uint)`
<sub>`DatDictionaryEx.cs:120`</sub>

Default `0u`.


#### `TryParseInt64(...out long)`
<sub>`DatDictionaryEx.cs:131`</sub>


#### `TryParseUInt64(...out ulong)`
<sub>`DatDictionaryEx.cs:142`</sub>


#### `TryParseFloat(this IDatDictionary, string key, out float value)`
<sub>`DatDictionaryEx.cs:153`</sub>

Caller: `game/GunDef.cs:63: PlayerDamage = d.ParseFloat("Player_Damage"),


#### `TryParseDouble(...out double)`
<sub>`DatDictionaryEx.cs:164`</sub>


#### `TryParseEnum<T>(this IDatDictionary, string key, out T value) where T : struct`
<sub>`DatDictionaryEx.cs:175`</sub>

Enum.TryParse` with `ignoreCase: true`. Accepts comma-separated flag lists and **also accepts bare numeric strings** (`"7"` → `(T)7` even if 7 is not a defined member) — that is `Enum.TryParse` behaviour, not a bug here. Missing → `defaultValue`.


#### `TryParseBool(this IDatDictionary, string key, out bool value)`
<sub>`DatDictionaryEx.cs:186`</sub>

Accepts single chars `y`/`t`/`1` → true and `n`/`f`/`0` → false; anything longer goes to `bool.TryParse` (`"true"`/`"false"`, case-insensitive). **Empty/null value → `false` return → `defaultValue`.** Default `defaultValue` is `false`.


#### `TryParseGuid(this IDatDictionary, string key, out System.Guid value)`
<sub>`DatDictionaryEx.cs:197`</sub>

Guid.TryParse` (accepts `N`, `D`, `B`, `P`, `X` formats). Missing → `Guid.Empty` by default.


#### `TryParseDateTimeUtc(this IDatDictionary, string key, out System.DateTime value)`
<sub>`DatDictionaryEx.cs:208`</sub>

Delegates to `DatValueEx.TryParseDateTimeUtc` (`DatValueEx.cs:177`), which parses with `DateTimeStyles.AssumeUniversal` then calls `.ToUniversalTime()` — **the returned `Kind` is `Utc`**. Missing → `default(DateTime)` (= `0001-01-01`, `Kind.Unspecified`).

- **used at** `core/UnturnedDat/DatValueEx.cs:177` — `public static bool TryParseDateTimeUtc(this IDatValue valueNode, out System.DateTime value)`

#### `public static System.Type ParseType(this IDatDictionary dictionary, string key, System.Type defaultValue = default)`
<sub>`DatDictionaryEx.cs:219`</sub>

Reflection type lookup, security-hardened: rejects any string containing `\`, `:`, or `/` (`DatValue.INVALID_TYPE_CHARS`, `DatValue.cs:118`) before `Type.GetType(..., throwOnError: false, ignoreCase: true)`. Missing key or rejected/unresolvable name → `defaultValue` (`null` by default).

- **used at** `tests/UnturnedDat.Tests/DatDictionaryTests.cs:431` — `Assert.AreEqual(expectedValue, dictionary.ParseType("key", defaultValue));`

#### `TryParseStruct<T>(this IDatDictionary, string key, out T value) where T : struct, IDatParseable`
<sub>`DatDictionaryEx.cs:229`</sub>

Note this one uses `TryGetNode` directly, so **it works for value, list, and dictionary nodes** — the `T.TryParse(IDatNode)` implementation decides. Missing key → `false` / `defaultValue`.


#### `public static List<T> ParseListOfStructs<T>(this IDatDictionary dictionary, string key) where T : struct, IDatParseable`
<sub>`DatDictionaryEx.cs:244`</sub>

Parses each list element, **silently skipping elements that fail** — the result may be shorter than the list. **Returns `null` if the key is missing or is not a list** (not an empty list).

- **used at** `tests/UnturnedDat.Tests/DatStructTests.cs:131` — `List<TestStruct> structs = rootDictionary.ParseListOfStructs<TestStruct>("list");`

#### `public static T[] ParseArrayOfStructs<T>(this IDatDictionary dictionary, string key, T defaultValue = default) where T : struct, IDatParseable`
<sub>`DatDictionaryEx.cs:253`</sub>

Index-preserving counterpart: array length always equals list length; failed elements get `defaultValue`. **Returns `null` if the key is missing/not a list.**

- **used at** `tests/UnturnedDat.Tests/DatStructTests.cs:151` — `TestStruct[] structs = rootDictionary.ParseArrayOfStructs<TestStruct>("array");`

#### `public static IEditableDatDictionary GetOrAddDictionary(this IEditableDatDictionary dictionary, string key, out bool isNew)`
<sub>`DatDictionaryEx.cs:261`</sub>

Get-or-create. If the key exists with the **wrong** node type it is *replaced* by a dictionary while preserving the old node's line number (via `ReplaceWithDictionary`, `EditableDatDictionary.cs:95`). `isNew` is `false` for both "already a dictionary" and "replaced". Never returns `null`.

- **used at** `tests/UnturnedDat.Tests/DatValueEditTests.cs:414` — `IEditableDatDictionary dict = rootDictionary.Edit().GetOrAddDictionary("Dict");`

#### `GetOrAddList(this IEditableDatDictionary, string key, out bool isNew)`
<sub>`DatDictionaryEx.cs:290`</sub>

Caller: `NO CALLERS` in production (tests only: `tests/UnturnedDat.Tests/DatValueEditTests.cs:393`).

- **used at** `tests/UnturnedDat.Tests/DatValueEditTests.cs:393` — `IEditableDatList list = rootDictionary.Edit().GetOrAddList("Key");`

#### `GetOrAddValue(this IEditableDatDictionary, string key, out bool isNew)`
<sub>`DatDictionaryEx.cs:319`</sub>

Caller: `NO CALLERS` in production (tests only: `tests/UnturnedDat.Tests/MergingGeneratedCommentTests.cs:60`).

- **used at** `tests/UnturnedDat.Tests/MergingGeneratedCommentTests.cs:60` — `IEditableDatValue key = dictionary.GetOrAddValue("Key");`

### `DatListEx.cs`

#### `public static bool TryGetValue(this IDatList list, int index, out IDatValue value)`
<sub>`DatListEx.cs:11`</sub>


#### `public static bool TryGetDictionary(this IDatList list, int index, out IDatDictionary dictionary)`
<sub>`DatListEx.cs:18`</sub>


#### `public static IDatDictionary GetDictionary(this IDatList list, int index)`
<sub>`DatListEx.cs:25`</sub>


#### `public static bool TryGetList(this IDatList thisList, int index, out IDatList list)`
<sub>`DatListEx.cs:30`</sub>


#### `public static IDatList GetList(this IDatList thisList, int index)`
<sub>`DatListEx.cs:37`</sub>


#### `public static bool TryGetString(this IDatList list, int index, out string value)`
<sub>`DatListEx.cs:42`</sub>


#### `public static string GetString(this IDatList list, int index, string defaultValue = null)`
<sub>`DatListEx.cs:56`</sub>

Element-as-string with `null` default. Same null-passthrough caveat as the dictionary version.

- **used at** `core/UnturnedSim/BlueprintDef.cs:51` — `string tag = stations.GetString(i);`

#### `public static List<T> ParseListOfStructs<T>(this IDatList list) where T : struct, IDatParseable`
<sub>`DatListEx.cs:64`</sub>

Pre-sized to `list.Count` but **skips `null` elements and elements whose `TryParse` returns false** — result length ≤ `list.Count`. Empty list → empty (non-null) `List<T>`.

- **used at** `tests/UnturnedDat.Tests/DatStructTests.cs:90` — `List<TestStruct> structs = list.ParseListOfStructs<TestStruct>();`

#### `public static T[] ParseArrayOfStructs<T>(this IDatList list, T defaultValue = default) where T : struct, IDatParseable`
<sub>`DatListEx.cs:80`</sub>

Length == `list.Count` exactly; failures become `defaultValue`. Empty list → zero-length array.

- **used at** `tests/UnturnedDat.Tests/DatStructTests.cs:111` — `TestStruct[] structs = list.ParseArrayOfStructs<TestStruct>();`

### `DatListValueEnumerator.cs`

#### `public struct DatListValueEnumerable`
<sub>`DatListValueEnumerator.cs:51`</sub>

Allocation-free filtered iteration over only the `IDatValue` elements of a list, skipping dictionaries/lists/nulls. Obtained via `IDatList.GetValues()` (`DatList.cs:52`). Empty result when the list has no scalar elements.

- **used at** `core/UnturnedDat/DatList.cs:52` — `public DatListValueEnumerable GetValues()`

### `DatNodeEx.cs`

#### `public static string DebugDumpToString(this IDatNode node)`
<sub>`DatNodeEx.cs:11`</sub>

Allocates a `StringBuilder` and renders the whole subtree (keys, values, comments, line numbers) for logging. Throws `NullReferenceException` on a null node. Returns the dump; never empty for a valid node. `NO CALLERS`.


#### `TryParseStruct<T>(this IDatNode node, out T value) where T : struct, IDatParseable`
<sub>`DatNodeEx.cs:18`</sub>

Note the implementation quirk: `value = default; return value.TryParse(node);` — it calls `TryParse` on the **already-defaulted struct**, so `T.TryParse` must mutate `this`. Failure → `default(T)` / `defaultValue`.

- **used at** `core/UnturnedDat/DatListEx.cs:69` — `if (node != null && node.TryParseStruct(out T value))`

#### `public static bool TryGetNodePath(this IDatNode node, out string path)`
<sub>`DatNodeEx.cs:32`</sub>

Walks parents to build a `/`-separated path (`""` for root, `"/Key"`, `"/Key/3"`). **Requires metadata** — returns `false` with `path = null` when `TryGetParentNode` is unavailable (i.e. `DatParser.EnableMetadata == false`, since plain `DatDictionary.TryGetParentNode` at `DatDictionary.cs:100` always returns `false`). Also `false` if the node isn't findable in its declared parent.

- **used at** `tests/UnturnedDat.Tests/DatParserMetadataNodePathTests.cs:40` — `Assert.IsTrue(node.TryGetNodePath(out string actualPath));`

#### `public static string GetPath(this IDatNode node)`
<sub>`DatNodeEx.cs:100`</sub>


#### `public static int GetParsedLineNumber(this IDatNode node)`
<sub>`DatNodeEx.cs:108`</sub>

1-based source line. **Returns `-1` when metadata is unavailable** (not `0`) — but note a metadata-enabled node created by code returns `0`. Both are "no line", with different values.


### `DatParser.cs`

#### `public IDatDictionary Parse(System.IO.TextReader inputReader)`
<sub>`DatParser.cs:17`</sub>

Tokenizes and builds the root dictionary. **Never returns `null` and never throws for malformed input** — errors accumulate in `ErrorMessages` and parsing continues (see the deliberate comment at `:452`). A completely empty/garbage input yields an empty root dictionary. Duplicate keys log an error and the *last* one wins (`:145`).

- **used at** `core/UnturnedSim/ClothingDef.cs:90` — `IDatDictionary d = new DatParser().Parse(datText);`

#### `public bool EnableMetadata { get; set; }`
<sub>`DatParser.cs:97`</sub>

When `true`, nodes become `*WithMetadata` wrappers carrying line numbers, prefix comments, inline comments and parent links — required for `TryGetNodePath`, `GetParsedLineNumber`, `TryGetParsedComment`, and any `MetadataPreservingDatWriter` round-trip. Defaults `false`. Setting it lazily allocates `commentLines`.

- **used at** `tests/UnturnedDat.Tests/DatParserMetadataLineNumberTests.cs:21` — `parser.EnableMetadata = true;`

#### `public bool HasError => errorMessages.Count > 0;`
<sub>`DatParser.cs:110`</sub>


#### `public string ErrorMessage`
<sub>`DatParser.cs:115`</sub>


#### `public IReadOnlyList<string> ErrorMessages`
<sub>`DatParser.cs:120`</sub>

DatWriter` is a stateful stack machine; unlike the parser it **throws** on misuse. `NO CALLERS` in production for the entire class — only `tests/UnturnedDat.Tests/DatWriterTests.cs` and `MetadataPreservingDatWriter`.


### `DatValue.cs`

#### `public static readonly char[] INVALID_TYPE_CHARS = { '\\', ':', '/' }`
<sub>`DatValue.cs:118`</sub>

Security allowlist backing `ParseType`. Reuse this constant rather than re-listing the characters — the comment at `:114` records the reported `Type.GetType` assembly-load exploit it defends against.

- **used at** `core/UnturnedDat/DatValueEx.cs:194` — `if (string.IsNullOrEmpty(valueNode.Value) || valueNode.Value.IndexOfAny(DatValue.INVALID_TYPE_CHARS) >= 0)`

### `DatValueEx.cs`

#### `public static bool IsValueNullOrEmpty(this IDatValue valueNode)`
<sub>`DatValueEx.cs:9`</sub>

The only null-safe member here: `true` if the node itself is `null` **or** its `Value` string is null/empty. This is the correct "is this a bare flag / empty" test.


#### `TryParseInt8(this IDatValue, out sbyte)`
<sub>`DatValueEx.cs:14`</sub>

All: `T.TryParse(node.Value, NumberStyles.Any, InvariantCulture, out value)`; on failure `Try*` returns `false` with `value = 0` and `Parse*` returns `defaultValue` (all defaults are `0`).


#### `TryParseEnum<T>(this IDatValue, out T value) where T : struct`
<sub>`DatValueEx.cs:114`</sub>

Note `ParseEnum<T>` here has **no default for `defaultValue`** (unlike the dictionary overload). `NO CALLERS`.


#### `public static bool TryParseBool(this IDatValue valueNode, out bool value)`
<sub>`DatValueEx.cs:134`</sub>

The authoritative bool grammar: length-1 `y|t|1` → `true`; `n|f|0` → `false`; length ≥ 2 → `bool.TryParse`; **length-1 anything-else and empty/null → `false` return, `value = false`**.

- **used at** `core/UnturnedDat/DatDictionaryEx.cs:189` — `return TryGetValue(dictionary, key, out IDatValue node) && node.TryParseBool(out value);`

#### `public static bool ParseBool(this IDatValue valueNode, bool defaultValue = default)`
<sub>`DatValueEx.cs:162`</sub>


#### `TryParseGuid(this IDatValue, out System.Guid)`
<sub>`DatValueEx.cs:167`</sub>


#### `TryParseDateTimeUtc(this IDatValue, out System.DateTime)`
<sub>`DatValueEx.cs:177`</sub>

AssumeUniversal` + `ToUniversalTime()`. Note `value = value.ToUniversalTime()` runs **even on parse failure**, so a failed `Try*` leaves `value` = `default(DateTime).ToUniversalTime()`, i.e. `0001-01-01T00:00:00Z` shifted by local offset. `NO CALLERS`.


#### `public static System.Type ParseType(this IDatValue valueNode, System.Type defaultValue = default)`
<sub>`DatValueEx.cs:192`</sub>

The security check lives here: empty, or containing any of `DatValue.INVALID_TYPE_CHARS` (`\`, `:`, `/`) → `defaultValue` without touching reflection. `NO CALLERS`.


### `DatWriter.cs`

#### `public void SetOutput(System.IO.TextWriter output)`
<sub>`DatWriter.cs:19`</sub>


#### `public void Dispose()`
<sub>`DatWriter.cs:26`</sub>


#### `public void CloseStack()`
<sub>`DatWriter.cs:34`</sub>


#### `public void WriteEmptyLine()`
<sub>`DatWriter.cs:55`</sub>


#### `public void WriteKey(string key)`
<sub>`DatWriter.cs:60`</sub>


#### `public void WriteValue(string value, string comment = null)`
<sub>`DatWriter.cs:82`</sub>

The escaping core. Emits `\n`→`\\n`, `\t`→`\\t`, `\`→`\\`; **`\r` is dropped entirely** (deliberate, `:136`). Quotes the value if it starts with `"` **or** if a `comment` is supplied. A `null`/empty `value` writes nothing after the key — i.e. it produces a **bare flag line**, round-tripping to the `ContainsKey` idiom. **Throws** on an empty stack or when the top is a dictionary.

- **used at** `core/UnturnedDat/DatWriter.cs:173` — `public void WriteValue(sbyte value, string comment = null)`

#### `public void WriteValueEnumString<T>(T value, string comment = null) where T : struct`
<sub>`DatWriter.cs:223`</sub>


#### `public void WriteKeyValueEnumString<T>(string key, T value, string comment = null) where T : struct`
<sub>`DatWriter.cs:316`</sub>


#### `WriteDictionaryStart()`
<sub>`DatWriter.cs:340`</sub>


#### `public void WriteDictionaryEnd()`
<sub>`DatWriter.cs:370`</sub>


#### `WriteListStart()`
<sub>`DatWriter.cs:388`</sub>


#### `public void WriteListEnd()`
<sub>`DatWriter.cs:418`</sub>


#### `public void WriteComment(string message)`
<sub>`DatWriter.cs:436`</sub>


#### `public void WriteNode(IDatNode node)`
<sub>`DatWriter.cs:443`</sub>


#### `public void WriteDictionary(IDatDictionary dictionary)`
<sub>`DatWriter.cs:466`</sub>


#### `public void WriteList(IDatList list)`
<sub>`DatWriter.cs:499`</sub>

Caller for the class: `tests/UnturnedDat.Tests/DatWriterTests.cs:13: DatWriter writer = new DatWriter();


### `DeadzoneField.cs`

#### `public IReadOnlyList<DeadzoneVolumeDef> Volumes => _volumes;`
<sub>`DeadzoneField.cs:33`</sub>

- **used at** `game/InteractableNetSync.cs:102` — `foreach (var v in field.Volumes)`

#### `public void AddVolume(Vector3 center, Vector3 halfExtent, DeadzoneKind kind = DeadzoneKind.Radiation)`
<sub>`DeadzoneField.cs:35`</sub>

- **in** — ** `center` world metres; `halfExtent` **half**-extents in metres (the world build uses `(30,25,30)` for a "60 m" pocket); `kind`/`zone` select the damage curve.
- **out** — ** void; no validation (a zero or negative half-extent silently contains nothing).
- **used at** `game/WorldBuilder.cs:790` — `deadzones.AddVolume(new Vector3(ax + 120f, H(ax + 120f, az + 120f) + 15f, az + 120f),`

#### `public bool TryGetVolume(Vector3 p, out DeadzoneVolumeDef found)`
<sub>`DeadzoneField.cs:49`</sub>

- **out** — ** `true` + the volume, else `false` + `default`. Linear scan; first match wins on overlap.
- **used at** `game/DeadzoneField.cs:87` — `if (!TryGetVolume(player.GlobalPosition, out var volume))`

#### `public bool IsInside(Vector3 p) => TryGetVolume(p, out _);`
<sub>`DeadzoneField.cs:58`</sub>


#### `public void Apply(PlayerController player, float dt)`
<sub>`DeadzoneField.cs:83`</sub>

- **in** — ** `dt` in seconds; the field's own poll is `PollSeconds = 0.25`.
- **out** — ** void; a null or freed player, or one outside every volume, exits and clears accrued state.
- **used at** `game/DeadzoneField.cs:70` — `Apply(player, dt);`

### `DestructibleField.cs`

#### `public static readonly StringName MetaKey = "destructible_index";`
<sub>`DestructibleField.cs:33`</sub>


#### `public int BuiltCount { get; private set; }`
<sub>`DestructibleField.cs:40`</sub>

- **used at** `game/WorldBuilder.cs:408` — `if (destN > 0) GD.Print($"[rubble] {destField.BuiltCount} destructible props wired ({destN} reserved, {destField.InstanceCount} slots)");`

#### `public int InstanceCount => _recs.Length;`
<sub>`DestructibleField.cs:45`</sub>

- **used at** `game/WorldNetSync.cs:255` — `_server.DestructibleHost.ServerInit(field.InstanceCount, server.Session.CurrentTick);`

#### `public bool IsAlive(int index)`
<sub>`DestructibleField.cs:49`</sub>

- **used at** `game/WorldStateViews.cs:97` — `if (Field.IsAlive(i) != Client.Destructibles.IsAlive(i))`

#### `public float MaxHealth(int index)`
<sub>`DestructibleField.cs:50`</sub>

- **used at** `game/WorldNetSync.cs:258` — `_server.DestructibleHost.SetMeta(i, field.MaxHealth(i), field.ResetTicks(i));`

#### `public void SetCount(int total)`
<sub>`DestructibleField.cs:59`</sub>

- **in** — ** `total` ≥ 0; a value smaller than the current length is a no-op.
- **used at** `game/WorldBuilder.cs:406` — `destField.SetCount(destN); // reserve the whole deterministic index space (built + unbuilt holiday slots)`

#### `public void Register(int index, StaticBody3D body, MeshInstance3D[] meshes, float maxHealth, long resetTicks, int effectId = 0)`
<sub>`DestructibleField.cs:63`</sub>

- **in** — ** `index` ≥ 0 (**negative is silently dropped**); `body` may be null (no collider mode) → stored layer 0; `meshes` = main + optional foliage, null slots tolerated; `maxHealth` in HP; `resetTicks` at 50 Hz; `effectId` 0 = no retail VFX.
- **out** — ** void. Re-registering an index overwrites without double-counting `BuiltCount`.
- **used at** `game/WorldBuilder.cs:371` — `destField.Register(destIndex, destBody, mis, rub.Health, rub.ResetTicks, rub.EffectId);`

#### `public void SetAlive(int index, bool alive)`
<sub>`DestructibleField.cs:74`</sub>

- **out** — ** void; out-of-range or unbuilt slot = no-op.
- **used at** `game/WorldStateViews.cs:98` — `Field.SetAlive(i, Client.Destructibles.IsAlive(i));`

#### `public void PlayBreakEffect(int index)`
<sub>`DestructibleField.cs:93`</sub>

- **in** — ** index in range with at least one valid mesh.
- **out** — ** void; no-op for out-of-range, unbuilt, or freed-mesh slots. Particle nodes self-free on a timer.
- **used at** `game/ClientWorldSession.cs:177` — `if (Destructibles != null) Client.ObjectDestroyed += e => Destructibles.PlayBreakEffect(e.Index); // break VFX (debris + dust) on a LIVE bre…`

#### `public readonly struct Rubble { public readonly float Health; public readonly long ResetTicks; public readonly int EffectId; }`
<sub>`DestructibleField.cs:214`</sub>


#### `public static Dictionary<string, Rubble> LoadCatalog()`
<sub>`DestructibleField.cs:222`</sub>

- **in** — ** none (fixed path).
- **out** — ** dictionary. **Returns an EMPTY dictionary (not null) when the file is missing**, which silently disables all destructibles.
- **used at** `game/WorldBuilder.cs:270` — `var rubbleCat = DestructibleField.LoadCatalog();`

### `DevConsole.cs`

#### `public static readonly List<(string Name, Vector3 Pos)> Locations = Load();`
<sub>`DevConsole.cs:605`</sub>

- **out** — ** **Empty list (not null) when `nodes.tsv` is missing.** Coordinates are already in port space. Malformed lines are skipped; a malformed *float* **throws during static construction**, which would surface as a `TypeInitializationException` on first touch of `MapNodes`.
- **used at** `game/ObjMesh.cs:28` — `public static ArrayMesh Load(string globalPath)`

### `EditableDatNode.cs`

#### `public static TNode SetComment<TNode>(this TNode node, string comment) where TNode : IEditableDatNode`
<sub>`EditableDatNode.cs:81`</sub>


#### `public static TNode SetMargins<TNode>(this TNode node, int margins)`
<sub>`EditableDatNode.cs:90`</sub>


#### `public static TNode SetTopMargin<TNode>(this TNode node, int topMargin)`
<sub>`EditableDatNode.cs:110`</sub>


#### `public static TNode SetBottomMargin<TNode>(this TNode node, int bottomMargin)`
<sub>`EditableDatNode.cs:119`</sub>


#### `public static TNode SetSortingPreference<TNode>(this TNode node, IEditableDatNode.ESortingPreference sortingPreference)`
<sub>`EditableDatNode.cs:128`</sub>


#### `public static TNode MergeGeneratedComment<TNode, TEnumerable>(this TNode node, string prefix, TEnumerable generatedLines, System.Text.StringBuilder stringBuilder, List<string> parsedLines) where TNode…`
<sub>`EditableDatNode.cs:139`</sub>

Merges machine-generated comment lines into a user-edited comment block: strips existing lines starting with `prefix.TrimEnd()`, then re-inserts `generatedLines` (each prefixed) at the position the old generated block occupied — preserving user text before and after. `stringBuilder` and `parsedLines` are **caller-supplied scratch buffers for thread safety** and are `Clear()`ed on entry. `generated…

- **used at** `tests/UnturnedDat.Tests/MergingGeneratedCommentTests.cs:65` — `key.MergeGeneratedComment("> ", generatedLinesArray, sb, lines);`

#### `public static TNode MergeGeneratedCommentAlloc<TNode, TEnumerable>(this TNode node, string prefix, TEnumerable generatedLines)`
<sub>`EditableDatNode.cs:202`</sub>

Same, allocating its own scratch. Use when not on a hot path. `NO CALLERS`.


### `EditableDatValue.cs`

#### `public static TValueNode SetInlineComment<TValueNode>(this TValueNode valueNode, string inlineComment) where TValueNode : IEditableDatValue`
<sub>`EditableDatValue.cs:27`</sub>

Sets the trailing `// ...` comment. **Side effect: setting a non-empty inline comment forces the value to be written in quotes** (`DatWriter.cs:120`). Returns the node.

- **used at** `tests/UnturnedDat.Tests/DatValueEditTests.cs:52` — `dictionary.Edit().AddValue("Key2").SetString("Value2").SetInlineComment("Comment 1");`

### `EditorObjects.cs`

#### `public static Basis Upright(float yawDeg) => new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)) * new Basis(Vector3.Right, Mathf.DegToRad(270f));`
<sub>`EditorObjects.cs:138`</sub>

- **in** — ** `yawDeg` in degrees, unbounded (wraps).
- **out** — ** orthonormal `Basis`, no scale. No failure path.

### `FoliageField.cs`

#### `public void LoadGrass()`
<sub>`FoliageField.cs:24`</sub>

- **in** — ** none.
- **out** — ** void; each type independently skipped (with a print) when its `.bin`/`.obj`/mesh is missing.
- **used at** `game/WorldBuilder.cs:431` — `ff.LoadGrass();`

### `InteractableNetSync.cs`

#### `public const uint FirstId = 1;`
<sub>`InteractableNetSync.cs:38`</sub>

- **used at** `game/WorldStateViews.cs:193` — `uint doorId = InteractableNetSync.FirstId, bedId = InteractableNetSync.FirstId;`

#### `public bool DamageAlong(UVector3 from, UVector3 to, float amount, PhysicsDirectSpaceState3D space)`
<sub>`InteractableNetSync.cs:61`</sub>

- **in** — ** `from`/`to` world metres as `UnityEngine.Vector3`; mask is hardcoded `1u << 0`; `space` **may be null** → returns false.
- **out** — ** `true` if a `Door` or `Bed` was hit and damaged; `false` on no hit, a non-barricade hit, or a null space.
- **used at** `game/DedicatedServer.cs:201` — `InteractableSync.DamageAlong(from, to, amount, GetViewport()?.World3D?.DirectSpaceState);`

### `InventoryReplication.cs`

#### `public const float StorageReach = 4f`
<sub>`InventoryReplication.cs:210`</sub>

Server crate interaction radius in metres (SP's own gate is 2.5 m, hardcoded as `6.25f` squared at `PlayerController.cs:2185` — see cross-dir).


#### `public event Action<ushort> ReplicaUpdated`
<sub>`InventoryReplication.cs:218`</sub>

Client-side "my inventory snapshot landed" hook — the correct thing to subscribe to instead of polling.

- **used at** `game/ClientWorldSession.cs:219` — `Client.Inventories.ReplicaUpdated += owner =>`

#### `public void ServerCommitDirty(long tick)`
<sub>`InventoryReplication.cs:249`</sub>

Stamps the dispatch round's dirty entries with a tick so delta baselines work. Must be called once per tick after dispatch.

- **out** — standard try-pattern; `out` is `null` on false.
- **used at** `core/UnturnedNet/NetWorldHost.cs:298` — `Inventories.ServerCommitDirty(Session.CurrentTick);`

#### `public CrateEntry ServerRegisterCrate(NetId id, byte width, byte height, Vector3 pos)`
<sub>`InventoryReplication.cs:255`</sub>

Creates the authoritative crate grid (`Items` page tagged `PlayerInventory.STORAGE`) at the given dims.

- **out** — the entry; never null. Re-registering the same NetId silently replaces the old grid (contents lost).
- **used at** `game/ContainerNetSync.cs:48` — `var crate = _server.Inventories.ServerRegisterCrate(id, w, h, upos); // authoritative grid (InventoryReplication owns the contents)`

#### `public bool ServerOpenStorage(ushort ownerPlayerId, uint crateId, Vector3 senderPos, long tick)`
<sub>`InventoryReplication.cs:265`</sub>

One-opener-at-a-time arbitration + range check, then copies the crate grid into the opener's STORAGE page — deliberately mirroring the SP mechanic so `MoveItem` needs no crate addressing.

- **in** — `senderPos` in metres, world space; `crateId` a NetId value (0 is never valid).
- **out** — `true` = opened. `false` = unknown player, unknown crate, someone else holds it, or out of `StorageReach`. No distinction between failure reasons.
- **used at** `core/UnturnedNet/ServerTransactions.cs:290` — `if (_inventories.ServerOpenStorage(sender, cmd.NetId, pos, _tick())`

#### `public bool ServerCloseStorage(ushort ownerPlayerId, long tick)`
<sub>`InventoryReplication.cs:281`</sub>

Saves STORAGE back into the crate, clears the page to 0x0, releases the arbitration lock.

- **out** — `false` if the player is unknown or has nothing open.
- **used at** `core/UnturnedNet/ServerTransactions.cs:302` — `if (_inventories.ServerCloseStorage(sender, _tick()))`

#### `public ulong StateHashFor(ushort ownerPlayerId)`
<sub>`InventoryReplication.cs:449`</sub>

FNV parity hashes over pages+jars+worn, quantized to the wire values so both sides agree.

- **out** — `NetHash.FnvOffset` (the empty hash) when the owner is unknown — **not 0**.
- **used at** `core/UnturnedNet/SkillsReplication.cs:178` — `public ulong StateHashFor(ushort ownerPlayerId)`

### `InventoryUI.cs`

#### `public bool WearFromGrid(EItemType slotType, byte page, byte x, byte y)`
<sub>`InventoryUI.cs:554`</sub>

The equip core: remove the jar from the grid, wear it (state + on-body visual + bag-page resize via `PlayerClothingController`), then `forceAddItem` the displaced garment back to the grid.

- **in** — `slotType` must equal the asset's own `type` — a mismatch is a rejected no-op, not a coercion; `page` bounds-checked against `Inv.items.Length`.
- **out** — `true` = equipped. `false` for: null `Inv`, page out of range, empty cell (`getIndex == byte.MaxValue`), null asset, type mismatch, or already-worn-here.
- **used at** `game/inventory/InventoryUI.cs:500` — `WearFromGrid(_clothing[ci].type, page, cx, cy);`

#### `public bool TakeOff(EItemType slotType)`
<sub>`InventoryUI.cs:574`</sub>

Unequip: clear state + visual (resizing that bag page to 0x0), return the garment to the grid, or drop it in the world if the grid is full (`ReturnToGrid`, `:544`).

- **out** — `true` if something was removed; `false` if the slot was empty.
- **used at** `game/inventory/InventoryUI.cs:389` — `TakeOff(fromType);`

#### `public static bool HasHandAction(ItemAsset asset)`
<sub>`InventoryUI.cs:739`</sub>

- **in** — `asset` may be null.
- **out** — `false` for null or non-holdable. Adding a new holdable type means editing **here and** the button dispatch in `OpenSelection` (`:695-712`) — the two are not linked by the compiler, which is exactly how the Rope shipped holdable-but-unreachable.
- **used at** `game/testing/tests/InventoryTests.cs:52` — `T.Check("rope (tool 64) offers a hand action", InventoryUI.HasHandAction(new ItemAsset { id = 64, type = EItemType.SUPPLY }));`

#### `public void Refresh()`
<sub>`InventoryUI.cs:1168`</sub>

Full rebuild: repaints the paperdoll clothing, re-lays every page as [header bar → grid] pairs, rebuilds `_drop` (the drag-target registry), and re-lays the dashboard. **Closes any open selection panel as a side effect.**

- **out** — void; early-returns silently if `Inv == null` or the tree isn't built.
- **used at** `game/PlayerController.cs:1333` — `_invUI?.Refresh();`

#### `static Texture2D Icon(int id)`
<sub>`InventoryUI.cs:1344`</sub>

The item icon loader/cache: `res://content/items/icons/{id}.png` → `ImageTexture`, cached forever including **negative results** (a missing file caches `null`, so a later-installed icon never appears).

- **out** — `Texture2D` or `null`; callers fall back to a rarity-tinted name label.

#### `Control MakeTile(ItemJar jar, int w, int h, int rotParam = -1)`
<sub>`InventoryUI.cs:1355`</sub>

The item tile renderer: rarity-tinted background + rarity border + rotated icon + amount badge + fuel bar + fluid bar + autodrink badge + food condition chip. `rotParam >= 0` overrides the jar's rot (used by the drag preview).


#### `static void StyleBox(Panel p, Color c)`
<sub>`InventoryUI.cs:1514`</sub>

StyleBoxFlat` + 3px corner radius. Trivially reusable, trivially reimplemented (`AttachmentMenu.cs:66` `Box(...)` is the same idea with a border).


#### `public partial class GridPanel : Control`
<sub>`InventoryUI.cs:1523`</sub>

The empty-grid backdrop: draws `Cells.X × Cells.Y` rounded translucent tiles at `Cell` px each in `_Draw`.


### `ItemAsset.cs`

#### `public static Item Assets.makeLoot(ushort id)`
<sub>`ItemAsset.cs:154`</sub>

The world-loot factory: magazines spawn full, FOOD rolls a random condition in `[qualityMin, qualityMax]`, fuel cans spawn empty, fluid containers spawn with their default fluid.

- **out** — a fresh `Item`; if the asset is unknown it still returns `new Item(id, 1)` rather than null.
- **used at** `game/inventory/LootField.cs:160` — `var item = id >= 0 ? Assets.makeLoot((ushort)id) : null; // magazines spawn full (master)`

### `ItemJar.cs`

#### `public ItemJar(Item newItem)`
<sub>`ItemJar.cs:17`</sub>

The 1-arg ctor makes a **positionless** jar (x=y=rot=0) — used as a transient wrapper for rendering a worn garment or a drag preview.

- **used at** `game/inventory/InventoryUI.cs:1479` — `var icon = MakeTile(new ItemJar(worn), HDRH - 12, HDRH - 12);`

### `Items.cs`

#### `public byte getIndex(byte pos_x, byte pos_y)`
<sub>`Items.cs:53`</sub>

Pixel-free cell→jar-index lookup: linear scan, rot-aware rectangle containment.

- **in** — cell coords.
- **out** — index into `items` list. **Empty-cell sentinel is `byte.MaxValue`** — every caller must check this.
- **used at** `game/PlayerController.cs:1397` — `byte idx = pg.getIndex(x, y);`

#### `public static bool StackingEnabled = false`
<sub>`Items.cs:77`</sub>

Global opt-in that makes the per-item `stackSize` cap effectively `byte.MaxValue`.


#### `public bool tryAddItem(Item item)`
<sub>`Items.cs:79`</sub>

Stack-then-place: merges into an existing same-id jar up to `Assets.find(id).stackSize` (or unlimited when `StackingEnabled`), then `tryFindSpace` + `fillSlot` for the remainder.

- **in** — `item` — **mutated in place**: `item.amount` is decremented by whatever merged into existing stacks.
- **out** — `true` = fully placed. Failure: `false` **and the item may have been partially merged already** (amount reduced) — the caller cannot assume nothing happened. Hard cap: 200 jars per page returns `false` immediately.
- **used at** `game/PlayerController.cs:1323` — `else shelf.Storage.tryAddItem(grabbed); // inventory full -> put it back on the shelf`

#### `public void removeItem(byte index)`
<sub>`Items.cs:103`</sub>

removeItem` frees the occupancy cells and shifts the list (so indices after it change — `PlayerInventory.TryDrag:154` compensates). `clear()` empties the jar list **without** clearing the `slots` occupancy array — a `clear()` not followed by `loadSize()` leaves phantom occupancy.

- **used at** `game/inventory/StoreShelf.cs:432` — `if (j != null && j.x == gx && j.y == gy) { var it = j.item; Storage.removeItem(i); return it; }`

#### `public void loadSize(byte newWidth, byte newHeight)`
<sub>`Items.cs:118`</sub>

Rebuilds the occupancy array at a new size and re-seats surviving jars, **silently dropping** any jar that no longer fits (or all of them when either dim is 0). `resize` = `loadSize` + the `onItemsResized`/`onStateUpdated` events.

- **in** — cells; `(0,0)` is the canonical "this page doesn't exist" state used everywhere for unworn bags and closed crates.
- **out** — void; the loss path is silent — inspect `getItemCount()` before/after if you care.
- **used at** `game/inventory/StorageCrate.cs:28` — `Storage.loadSize(Width, Height);`

#### `public bool checkSpaceEmpty(byte pos_x, byte pos_y, byte size_x, byte size_y, byte rot)`
<sub>`Items.cs:157`</sub>

The canonical "does an item of this footprint fit at (x,y)" test: swaps `size_x`/`size_y` when `rot % 2 == 1`, then walks the block rejecting out-of-bounds and occupied cells.

- **in** — `pos_x/pos_y` = cell coords, 0-based, valid `[0,width)`/`[0,height)` — values ≥ dims are *not* pre-rejected, the loop catches them; `size_x/size_y` = item footprint in cells, ≥1; `rot` = 0..3, only parity matters.
- **out** — `true` = fits. Empty/failure path: `false`. Special case: for pages `< PlayerInventory.SLOTS` (the two hand holsters) it ignores every argument and returns `items.Count == 0`.
- **used at** `game/testing/tests/NetTests.cs:2327` — `if (pg.checkSpaceEmpty(x, y, 1, 1, 0)) { dx = x; dy = y; }`

#### `public bool checkSpaceDrag(byte old_x, byte old_y, byte oldRot, byte new_x, byte new_y, byte newRot, byte size_x, byte size_y, bool checkSame)`
<sub>`Items.cs:172`</sub>

Fit test for a MOVE: same as `checkSpaceEmpty` but when `checkSame` the item's own old footprint is not treated as blocking, so you can nudge an item onto cells it already covers.

- **in** — `old_*`/`oldRot` = where it is now; `new_*`/`newRot` = candidate; `size_x/size_y` = the *unrotated* footprint (the function rotates internally, twice — once per rot); `checkSame` = true iff source page == destination page.
- **out** — `true` = the move is legal. Failure: `false`. Slot pages (`page < SLOTS`) short-circuit to `items.Count == 0 || checkSame`.
- **used at** `core/UnturnedSim/PlayerInventory.cs:141` — `if (!items[page1].checkSpaceDrag(x0, y0, item.rot, x1, y1, rot1, item.size_x, item.size_y, page0 == page1)) return false;`

#### `public bool checkSpaceSwap(byte x, byte y, byte oldSize_X, byte oldSize_Y, byte oldRot, byte newSize_X, byte newSize_Y, byte newRot)`
<sub>`Items.cs:193`</sub>

Fit test for a SWAP: can `newSize@newRot` sit at (x,y) once `oldSize@oldRot` is lifted out of that spot.

- **in** — all cell units; both sizes unrotated, both rots 0..3.
- **out** — `true` = swap legal. Failure: `false`. Slot pages return `true` unconditionally.
- **used at** `core/UnturnedSim/PlayerInventory.cs:150` — `if (!items[page0].checkSpaceSwap(x0, y0, item.size_x, item.size_y, item.rot, dest.size_x, dest.size_y, rot0)) return false;`

#### `public bool tryFindSpace(byte size_x, byte size_y, out byte x, out byte y, out byte rot)`
<sub>`Items.cs:212`</sub>

Auto-placement scan: row-major over the page for the first free unrotated block; if none, a second full pass for the rotated fit.

- **in** — footprint in cells, ≥1. Note `height - size_y + 1` is computed in `byte` arithmetic promoted to int, so an item taller than the page just yields an empty loop.
- **out** — `true` + `(x,y,rot)`; **failure sentinel is `x = y = byte.MaxValue`, `rot = 0`, return `false`** (set before the scan, so the outs are always written). `rot` is only ever 0 or 1 from here — never 2/3.
- **used at** `core/UnturnedSim/Items.cs:94` — `if (!tryFindSpace(itemJar.size_x, itemJar.size_y, out var x, out var y, out var rot)) return false;`

### `Mathf.cs`

#### `Sin(float f)`
<sub>`Mathf.cs:16`</sub>

Callers: `core/UnturnedNet/ServerTransactions.cs:416: float pulled = Mathf.Min(canSpace, remaining);` and `core/UnturnedNet/InventoryReplication.cs:384: w.WriteClampedFloat(Mathf.Max(0f, j.item?.fuelLevel ?? 0f), 12, 2);

- **used at** `core/UnturnedNet/ServerCombat.cs:453` — `var fwd = new Vector3(-Mathf.Sin(yawRad), 0f, -Mathf.Cos(yawRad));`

### `MetadataPreservingDatWriter.cs`

#### `public static IEditableDatDictionary CreateRoot()`
<sub>`MetadataPreservingDatWriter.cs:40`</sub>

Caller: `NO CALLERS` in production (tests: `tests/UnturnedDat.Tests/DatValueEditTests.cs`).


#### `public void WriteRootDictionary(IDatDictionary rootDictionary, DatWriter writer)`
<sub>`MetadataPreservingDatWriter.cs:45`</sub>

Writes the tree back through a `DatWriter`, re-sorting nodes by their original line numbers (`ListLineNumberComparer`, `:227`) and re-emitting blank-line spacing and prefix comments so an edited file keeps its layout. **Throws** `ArgumentNullException` for either `null` arg and `ArgumentException("not compatible")` if the root is not metadata-backed (i.e. you parsed with `EnableMetadata = false` o…


### `NetMaxValue.cs`

#### `public struct NetLength`
<sub>`NetMaxValue.cs:7`</sub>


#### `public uint Clamp(int otherValue)`
<sub>`NetMaxValue.cs:20`</sub>

Public fields `value` (`:25`), `bitCount` (`:26`).


### `NetPakConst.cs`

#### `public const float INV_SQRT_OF_TWO = 0.70710678118f`
<sub>`NetPakConst.cs:17`</sub>


#### `public const int MAX_STRING_BYTE_COUNT_BITS = 11`
<sub>`NetPakConst.cs:23`</sub>

Caller: `tests/SDG.NetPak.Tests/StringNetPakTests.cs:230: Assert.IsTrue(writer.WriteBits((uint) (badSequence.Length - 1), NetPakConst.MAX_STRING_BYTE_COUNT_BITS));` — `NO CALLERS` in production.

- **used at** `tests/SDG.NetPak.Tests/StringNetPakTests.cs:230` — `Assert.IsTrue(writer.WriteBits((uint) (badSequence.Length - 1), NetPakConst.MAX_STRING_BYTE_COUNT_BITS));`

#### `public static int CountBits(uint value)`
<sub>`NetPakConst.cs:38`</sub>

Bits needed to represent `value`: naive shift loop. **`CountBits(0) == 0`**, `CountBits(1) == 1`, `CountBits(255) == 8`, `CountBits(256) == 9`. Explicitly "not used in the hot path" (`:40`).

- **used at** `core/SDG.NetPak/NetMaxValue.cs:12` — `bitCount = NetPakConst.CountBits(value);`

### `NetPakReader.cs`

#### `public bool ReachedEndOfSegment { get; }`
<sub>`NetPakReader.cs:53`</sub>

readByteIndex == bufferLength`. **Deliberately imprecise** (`:49`) because byte length is rounded up from bit length — use it as a sanity check, not a contract. `NO CALLERS`.


#### `public int RemainingSegmentLength { get; }`
<sub>`NetPakReader.cs:58`</sub>

Caller: `core/UnturnedNet/SnapshotApplier.cs:82: while (_reader.RemainingSegmentLength > 0)` — and see the caveat noted at `core/UnturnedNet/NetWorldHost.cs:360`.

- **used at** `core/UnturnedNet/SnapshotApplier.cs:82` — `while (_reader.RemainingSegmentLength > 0)`

#### `public bool SaveState(out uint scratch, out int scratchBitCount, byte[] buffer)`
<sub>`NetPakReader.cs:63`</sub>

Snapshots the unread remainder into a caller-supplied `buffer` for deferred processing. Returns `false` with `scratch = 0, scratchBitCount = 0` if `RemainingSegmentLength > buffer.Length`. On success it **moves `readByteIndex` to the end** so `ReachedEndOfSegment` doesn't warn. Assumes the pending scratch fits in 32 bits. `NO CALLERS`.


#### `public void LoadState(uint scratch, int scratchBitCount, byte[] buffer, int bufferLength)`
<sub>`NetPakReader.cs:106`</sub>


#### `public void Reset()`
<sub>`NetPakReader.cs:116`</sub>

Caller: `core/UnturnedNet/NetSession.cs:148: _reader.Reset(); // SetBufferSegment alone keeps the previous datagram's read position

- **used at** `core/UnturnedNet/NetSession.cs:148` — `_reader.Reset(); // SetBufferSegment alone keeps the previous datagram's read position`

#### `public void ResetErrors()`
<sub>`NetPakReader.cs:127`</sub>


#### `public int GetBufferSegmentLength()`
<sub>`NetPakReader.cs:133`</sub>


#### `public void SetBuffer(byte[] buffer)`
<sub>`NetPakReader.cs:138`</sub>

Caller: `game/Main.cs:531: r.SetBuffer(w.buffer); r.ReadBits(12, out uint got);

- **used at** `game/Main.cs:531` — `r.SetBuffer(w.buffer); r.ReadBits(12, out uint got);`

#### `public void SetBufferSegment(byte[] buffer, int bufferLength)`
<sub>`NetPakReader.cs:144`</sub>

Caller: `core/UnturnedNet/NetSession.cs:149: _reader.SetBufferSegment(buffer, length);

- **used at** `core/UnturnedNet/NetSession.cs:149` — `_reader.SetBufferSegment(buffer, length);`

#### `public void SetBufferSegmentCopy(byte[] sourceBuffer, byte[] destinationBuffer, int bufferLength)`
<sub>`NetPakReader.cs:153`</sub>


#### `public bool ReadBit(out bool value)`
<sub>`NetPakReader.cs:168`</sub>

Caller: `core/UnturnedNet/InventoryReplication.cs:412: if (!r.ReadBit(out bool autoDrink)) return false; // autodrink toggle

- **used at** `core/UnturnedNet/InventoryReplication.cs:412` — `if (!r.ReadBit(out bool autoDrink)) return false; // autodrink toggle`

#### `public bool ReadBits(int valueBitCount, out uint value)`
<sub>`NetPakReader.cs:177`</sub>

Reads `valueBitCount` bits (valid `[0, 32]`, unchecked here) LSB-first, refilling scratch up to 4 bytes at a time. **On any overflow returns `false` with `value = 0` and sets `SourceBufferOverflow`** — a truncated packet reads as zeros, so always check the return value rather than trusting the value.

- **used at** `game/Main.cs:531` — `r.SetBuffer(w.buffer); r.ReadBits(12, out uint got);`

#### `public bool AlignToByte()`
<sub>`NetPakReader.cs:255`</sub>

Consumes `scratchBitCount % 8` padding bits and **verifies they are zero**; a nonzero pad returns `false` and sets `AlignmentPadding`. Already-aligned → `true`.

- **used at** `core/UnturnedNet/SnapshotApplier.cs:89` — `_reader.AlignToByte();`

#### `public bool ReadBytesPtr(int length, out byte[] source, out int bufferOffset)`
<sub>`NetPakReader.cs:287`</sub>

Zero-copy: hands back the backing array plus an offset instead of copying, and advances the reader. **`length` must be ≥ 1** (unchecked here). Aligns first. Failure → `source = null`, `bufferOffset = 0` (or `0` from the align failure), `SourceBufferOverflow`. `NO CALLERS`.


#### `public bool ReadBytes(byte[] destination, int length)`
<sub>`NetPakReader.cs:339`</sub>

Copies `length` bytes into `destination`. **`length > destination.Length` → `false` + `DestinationBufferOverflow` (no exception).** `length < 1` returns `true` without aligning. On source overflow → `false`, and `destination` is left untouched.

- **used at** `core/UnturnedNet/NetSession.cs:217` — `if (len > 0 && !_reader.ReadBytes(payload, len)) { Diag.MalformedDropped++; return; }`

### `NetPakWriter.cs`

#### `public void Reset()`
<sub>`NetPakWriter.cs:31`</sub>

Caller: `core/UnturnedNet/SnapshotComposer.cs:234: _writer.Reset();

- **used at** `core/UnturnedNet/SnapshotComposer.cs:234` — `_writer.Reset();`

#### `public bool WriteBit(bool value)`
<sub>`NetPakWriter.cs:39`</sub>

Writes exactly **1 bit**, LSB-first into a 64-bit scratch, auto-flushing 4 bytes once 32 bits accumulate. Returns `false` only if that flush overflows.

- **used at** `core/UnturnedNet/InventoryReplication.cs:393` — `w.WriteBit(j.item?.autoDrink ?? true); // autodrink toggle (default on)`

#### `public bool WriteBits(uint value, int valueBitCount)`
<sub>`NetPakWriter.cs:56`</sub>

Writes the low `valueBitCount` bits of `value`. **Valid range `[0, 32]`; unchecked in this build** — passing 33+ silently corrupts (`1UL << 33` wraps the mask), and `valueBitCount == 32` relies on `1UL << 32` being computed in 64-bit (correct here). **High bits are masked, not clamped:** writing `300` in 8 bits yields `44`, not `255`. `WriteBits(0xFFFFFFFFu, n)` is the idiom for "all ones in n bit…

- **used at** `core/SDG.NetPak/SystemNetPakWriterEx.cs:143` — `return writer.WriteBits(value, 8);`

#### `public bool Flush()`
<sub>`NetPakWriter.cs:76`</sub>

Pushes remaining scratch bits into `buffer` as `ceil(scratchBitCount / 8)` bytes and advances `writeByteIndex`. No-op returning `true` when `scratchBitCount < 1`. Returns `false` + `BufferOverflow` if the buffer can't take them. **You must call this before reading `writeByteIndex` as a length.**

- **used at** `core/UnturnedNet/NetSession.cs:568` — `_writer.Flush();`

#### `public bool AlignToByte()`
<sub>`NetPakWriter.cs:132`</sub>

Pads with `8 - (scratchBitCount % 8)` **zero** bits so the next write starts on a byte boundary. Already-aligned → `true`, no bits written. Note the alignment is computed from `scratchBitCount` (bits pending in scratch), which stays congruent to total bits written mod 8.

- **used at** `core/UnturnedNet/SnapshotComposer.cs:159` — `_writer.AlignToByte();`

#### `public bool WriteBytes(byte[] bytes, int offset, int length)`
<sub>`NetPakWriter.cs:156`</sub>

Aligns, flushes, then `Buffer.MemoryCopy`s `length` bytes. **`length < 1` returns `true` immediately without aligning** (deliberate: zero-length costs no padding bits). Bounds are **unchecked** in this build — a bad `offset`/`length` reads out of `bytes`; only the *destination* overflow is caught (`false` + `BufferOverflow`).

- **used at** `core/UnturnedNet/NetSession.cs:411` — `_writer.WriteBytes(data, offset, count);`

### `ObjMesh.cs`

#### `public static int CachedCount => _cache.Count;`
<sub>`ObjMesh.cs:25`</sub>

- **out** — ** ≥0. Never fails.
- **used at** `game/Warmup.cs:89` — `GD.Print($"[warmup] preloaded {ObjMesh.CachedCount} meshes across {_entries.Count} assets");`

#### `public static bool IsCached(string globalPath)`
<sub>`ObjMesh.cs:26`</sub>

- **in** — ** absolute path, must match the `Load` key byte-for-byte.
- **out** — ** bool.

#### `public static ArrayMesh Load(string globalPath)`
<sub>`ObjMesh.cs:28`</sub>

- **in** — ** `globalPath` = an OS-absolute path (callers pass `ProjectSettings.GlobalizePath(...)` output). Faces are fan-triangulated; `f` indices are 1-based per OBJ spec.
- **out** — ** cached `ArrayMesh` (same instance on repeat calls, keyed by exact path string — a differently-spelled path double-parses). **Returns `null` if the file produced zero triangles**; **throws** (uncaught `FileNotFoundException`) if the path does not exist — callers such as `WorldBuilder.cs:299` guard with `File.Exists` …
- **used at** `game/WorldBuilder.cs:299` — `fmesh = System.IO.File.Exists(fp) ? ObjMesh.Load(fp) : null;`

### `PlayerInventory.cs`

#### `public event Action<byte> onPageChanged`
<sub>`PlayerInventory.cs:28`</sub>

Per-page change hook, already wired to every page's `onStateUpdated` in the ctor.


#### `public bool tryAddItem(Item item)`
<sub>`PlayerInventory.cs:107`</sub>

Auto-place across pages `SLOTS .. PAGES-3` (pockets, then the four worn bags) — deliberately skips holsters, STORAGE and AREA.

- **out** — `true` = landed somewhere. Failure: `false`, item unplaced (but see `Items.tryAddItem`'s partial-merge caveat).
- **used at** `core/UnturnedNet/ServerTransactions.cs:543` — `if (inv.tryAddItem(e.ServerItem))`

#### `public bool TryDrag(byte page0, byte x0, byte y0, byte page1, byte x1, byte y1, byte rot1)`
<sub>`PlayerInventory.cs:127`</sub>

The single move/swap entry point. Empty destination → `checkSpaceDrag` then remove+add; occupied destination → `checkSpaceSwap` *both* pages then cross re-add. Forces `rot = 0` on hand-slot pages. **This is both the SP mutator and the MP server validator.**

- **in** — page ids `< PAGES-1` (i.e. AREA=8 is rejected as source *and* destination — you cannot `TryDrag` out of Nearby); cell coords; `rot1` 0..3.
- **out** — `true` = applied. Failure: `false` **with no mutation** (all validation precedes any remove/add) — this is why it is safe as a server validator.
- **used at** `core/UnturnedNet/ServerTransactions.cs:253` — `bool ok = SenderInventory(sender)?.TryDrag(cmd.Page0, cmd.X0, cmd.Y0, cmd.Page1, cmd.X1, cmd.Y1, cmd.Rot1) == true;`

#### `public int getItemCount(ushort id)`
<sub>`PlayerInventory.cs:164`</sub>

Sum of `amount` for `id` across pages `0..PAGES-3` (excludes STORAGE + AREA).

- **out** — total rounds/units; `0` if absent.
- **used at** `game/PlayerController.cs:1857` — `if (Inventory.getItemCount(id) <= 1) { (_revertEquip ?? EquipUnarmed)(); return; } // last one just went over the wire -> revert`

#### `public byte peekItemQuality(ushort id)`
<sub>`PlayerInventory.cs:182`</sub>

Quality (0–100) of the *first-found* instance — deliberately the same page/index scan order `removeItemAmount` deletes in, so a consume scores against the instance actually eaten.

- **out** — 0–100. **Not-found sentinel is `100` (treated as fresh), not 0.**
- **used at** `game/PlayerController.cs:1698` — `int eatenQuality = Inventory?.peekItemQuality(id) ?? 100; // condition of the instance removeItemAmount will delete -> scores the moldy-food…`

#### `public void removeItemAmount(ushort id, int amount)`
<sub>`PlayerInventory.cs:197`</sub>

Consume up to `amount` across own pages, removing emptied jars (and correctly not advancing the index after a removal).

- **in** — `amount` may exceed what's held — it just consumes everything available; no error signal.
- **out** — void — **there is no way to know how much it actually removed.** Callers who care must `getItemCount` before/after.
- **used at** `core/UnturnedNet/ServerTransactions.cs:582` — `inv.removeItemAmount(asset.id, 1); // the SP consume path removes by id (PlayerController.TickConsume)`

### `ResourceField.cs`

#### `public bool VisualInstances = true;`
<sub>`ResourceField.cs:24`</sub>


#### `public int InstanceCount => _instances.Count;`
<sub>`ResourceField.cs:37`</sub>

- **out** — ** ≥0; **0 before `LoadResources` runs**, which is exactly the P3 client-holiday deferral hazard the code comments call out.
- **used at** `game/WorldNetSync.cs:211` — `_server.Resources.ServerInit(field.InstanceCount, server.Session.CurrentTick);`

#### `public bool IsAlive(int index)`
<sub>`ResourceField.cs:39`</sub>

- **in** — ** `0 .. InstanceCount-1`.
- **out** — ** bool; **out-of-range returns `false`** (i.e. "felled"), the opposite polarity of `DestructibleField.IsAlive`.
- **used at** `game/WorldStateViews.cs:68` — `if (Field.IsAlive(i) != Client.Resources.IsAlive(i))`

#### `public StaticBody3D DebugTrunk(int index)`
<sub>`ResourceField.cs:43`</sub>

- **out** — ** `null` for non-trees **and** for out-of-range indices — the two are indistinguishable.
- **used at** `game/testing/tests/NetTests.cs:1801` — `T.Check("the felled tree's client trunk collider is OFF (§7 risk 7)", clientField.DebugTrunk(treeIdx).CollisionLayer == 0);`

#### `public void SetAlive(int index, bool alive)`
<sub>`ResourceField.cs:48`</sub>

- **in** — ** index in range; `alive` bool. Idempotent — a no-change call returns immediately.
- **out** — ** void; out-of-range is a silent no-op.
- **used at** `game/WorldNetSync.cs:220` — `_field?.SetAlive(index, alive);`

#### `public void LoadResources(string activeHoliday)`
<sub>`ResourceField.cs:59`</sub>

- **in** — ** `activeHoliday` = `"NONE"`, `"CHRISTMAS"`, `"HALLOWEEN"`; a type whose manifest holiday is neither `"NONE"` nor this value is skipped entirely (and, unlike objects, does **not** reserve an index — which is why the client must defer the whole load).
- **out** — ** void. Missing `resources.txt` → prints and returns with `InstanceCount == 0`.
- **used at** `game/WorldBuilder.cs:443` — `rsf.LoadResources(activeHoliday); // gate CHRISTMAS resources (candy canes/snow piles) like the objects`

### `RoadField.cs`

#### `public void LoadFromEnvironment(string envDir)`
<sub>`RoadField.cs:66`</sub>

- **in** — ** the map's `Environment` directory.
- **out** — ** void; a road with <2 joints or an out-of-range material is retained but not built.
- **used at** `game/WorldBuilder.cs:423` — `rf.LoadFromEnvironment(mapRoot + "/Environment");`

#### `public void LoadMaterialsOnly(string envDir)`
<sub>`RoadField.cs:83`</sub>

- **used at** `game/Main.cs:2410` — `rf.LoadMaterialsOnly(_mapRoot + "/Environment"); // shared road materials so roads can be added on the blank map`

### `SystemNetPakReaderEx.cs`

#### `public static bool ReadSignedInt(this NetPakReader reader, int bitCount, out int value)`
<sub>`SystemNetPakReaderEx.cs:14`</sub>

Caller: `core/UnturnedNet/CombatReplication.cs:349: if (!r.ReadClampedFloat(NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits, out float x)) return false;


### `SystemNetPakWriterEx.cs`

#### `public static bool WriteSignedInt(this NetPakWriter writer, int value, int bitCount)`
<sub>`SystemNetPakWriterEx.cs:14`</sub>

Zig-less bias encoding: writes `value + 2^(bitCount-1)` in `bitCount` bits. **Range `[-2^(bitCount-1), +2^(bitCount-1))`** — e.g. `bitCount = 7` → `[-64, +64)`. Out-of-range values are **not clamped in this build** (the range check is inside `WITH_NETPAK_EXCEPTIONS`); they wrap via `WriteBits`' mask. `NO CALLERS`.


#### `public static bool WriteUnsignedClampedFloat(this NetPakWriter writer, float value, int intBitCount, int fracBitCount)`
<sub>`SystemNetPakWriterEx.cs:35`</sub>

Fixed-point, **`intBitCount + fracBitCount` bits total**. Representable range **`[0, 2^intBitCount)`**; fraction resolution `1 / 2^fracBitCount`.


#### `public static bool WriteClampedFloat(this NetPakWriter writer, float value, int intBitCount, int fracBitCount)`
<sub>`SystemNetPakWriterEx.cs:71`</sub>

Three special paths, all load-bearing:

- **used at** `core/UnturnedNet/CombatReplication.cs:341` — `w.WriteClampedFloat(p.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);`

#### `WriteInt8(this NetPakWriter, sbyte value)`
<sub>`SystemNetPakWriterEx.cs:119`</sub>

Caller: `core/UnturnedNet/InventoryReplication.cs:377: w.WriteInt8((sbyte)(j.item?.gunFiremode ?? -1));` (`-1` = sentinel "none").

- **used at** `core/UnturnedNet/InventoryReplication.cs:377` — `w.WriteInt8((sbyte)(j.item?.gunFiremode ?? -1));`

#### `public static bool WriteUnsignedNormalizedFloat(this NetPakWriter writer, float value, int bitCount)`
<sub>`SystemNetPakWriterEx.cs:166`</sub>

value` must be in **`[0.0, 1.0]`** — **not clamped in this build**; out-of-range values are masked by `WriteBits` and wrap. `bitCount` `[1,31]`. Encodes `(uint)((value * (2^bitCount - 1)) + 0.5f)` — **round-to-nearest**, endpoints exact, midpoint `0.5` not exactly representable.

- **used at** `core/UnturnedNet/PlayerVitalsReplication.cs:156` — `w.WriteUnsignedNormalizedFloat(Clamp01(e.Sim.Food), VitalsBits);`

#### `public static bool WriteSignedNormalizedFloat(this NetPakWriter writer, float value, int bitCount)`
<sub>`SystemNetPakWriterEx.cs:184`</sub>

value` in **`[-1.0, +1.0]`**, **not clamped in this build**. `bitCount` `[2,32]`. Uses a **sign-magnitude** layout, not two's complement: magnitude in the low `bitCount-1` bits (max `2^(bitCount-1) - 1`), sign as the top bit. Endpoints and `0.0` are exact; there are **two encodings of zero** (`+0` and `-0`).

- **used at** `core/UnturnedNet/CombatReplication.cs:360` — `w.WriteSignedNormalizedFloat(Clamp1(d.x), DirBits);`

#### `public static bool WriteFloat(this NetPakWriter writer, float value)`
<sub>`SystemNetPakWriterEx.cs:209`</sub>

Caller: `core/UnturnedNet/WorldReplication.cs:111: w.WriteFloat(DayLengthSeconds);

- **used at** `core/UnturnedNet/WorldReplication.cs:111` — `w.WriteFloat(DayLengthSeconds);`

#### `public static bool WriteRadians(this NetPakWriter writer, float value, int bitCount = 8)`
<sub>`SystemNetPakWriterEx.cs:222`</sub>

Wraps into `[0, 2π)` with `((v % TAU) + TAU) % TAU`, then quantizes to `bitCount` bits over the **full period** (divisor `2^bitCount`, not `2^bitCount - 1`), i.e. step = `2π / 2^bitCount`. Truncating. Default **8 bits** (~1.4°). `NO CALLERS`.


#### `public static bool WriteDegrees(this NetPakWriter writer, float value, int bitCount = 8)`
<sub>`SystemNetPakWriterEx.cs:235`</sub>

Same, in degrees: wraps into `[0, 360)`, step = `360 / 2^bitCount`. Default **8 bits** (1.40625°); the repo uses **11 bits** (0.176°) via `NetQuantization.YawBits`/`PitchBits`.

- **used at** `core/UnturnedNet/PlayerAuthority.cs:55` — `w.WriteDegrees(YawDegrees, NetQuantization.YawBits);`

#### `public static bool WriteString(this NetPakWriter writer, string value, int lengthBitCount = NetPakConst.MAX_STRING_BYTE_COUNT_BITS)`
<sub>`SystemNetPakWriterEx.cs:244`</sub>

Wire format: **1 bit** `isNullOrEmpty`; if not, **`lengthBitCount` bits** holding `byteCount - 1` (so the length field covers 1..2^lengthBitCount bytes), then the UTF-8 bytes. Default `lengthBitCount` = **11** → up to **2048 bytes**.

- **used at** `core/UnturnedNet/NetClientSession.cs:72` — `w.WriteString(_playerName);`

#### `public static bool WriteGuid(this NetPakWriter writer, System.Guid value)`
<sub>`SystemNetPakWriterEx.cs:278`</sub>


#### `public static bool WriteDateTime(this NetPakWriter writer, System.DateTime value)`
<sub>`SystemNetPakWriterEx.cs:293`</sub>


#### `public static bool WriteList<T>(this NetPakWriter writer, List<T> list, WriteListItem<T> writeFunc, NetLength maxLength)`
<sub>`SystemNetPakWriterEx.cs:313`</sub>

Writes the count in `maxLength.bitCount` bits, **clamped down to `maxLength.value`** — excess elements are silently dropped. Then invokes `writeFunc` per element. `NO CALLERS`.


#### `public static bool WriteStateArray(this NetPakWriter writer, byte[] value)`
<sub>`SystemNetPakWriterEx.cs:339`</sub>

Every method returns `bool`; **on failure the `out` value is whatever the partial read produced (usually `0`), never a sentinel** — check the return.


### `Terrain.cs`

#### `public void PaintSplat(float worldX, float worldZ, float radiusWorld, int layer)`
<sub>`Terrain.cs:94`</sub>

- **in** — ** world XZ (Z negated internally); radius in metres; `layer` **must be 0..7** — an out-of-range value writes all-zero colour channels and stores an invalid byte in `_dom` with **no validation**.
- **out** — ** void; no-op if `_dom == null || _s0Img == null`. Note it does **not** set `Dirty` (splat paint is not covered by save/load).
- **used at** `game/EditorTerrain.cs:90` — `if (_paint) { _terr.PaintSplat(pt.X, pt.Z, _radius, _layer); return; }`

#### `public void EditHeight(float worldX, float worldZ, float radiusWorld, float deltaWorldY)`
<sub>`Terrain.cs:115`</sub>

- **in** — ** brush centre in world metres (Z negated internally); `radiusWorld` in metres; `deltaWorldY` in world metres per call — converted to normalized grid delta by `/2048`. Samples clamp to `0..1` (i.e. world Y `-1024..+1024`).
- **out** — ** void; no-op if `_grid == null`. Sets `Dirty`.
- **used at** `game/EditorTerrain.cs:93` — `case EBrush.Raise: _terr.EditHeight(pt.X, pt.Z, _radius, _strength * dt); break;`

#### `public bool Dirty => _dirty;`
<sub>`Terrain.cs:136`</sub>

- **used at** `game/EditorTerrain.cs:46` — `if (_terr == null || !_terr.Dirty) return 0;`

#### `public void SaveHeightmap(string path)`
<sub>`Terrain.cs:138`</sub>

- **in** — ** absolute path.
- **out** — ** void; no-op if `_grid == null`. **Throws** on an unwritable path.
- **used at** `game/EditorTerrain.cs:47` — `_terr.SaveHeightmap(SavePath);`

#### `public bool LoadHeightmap(string path)`
<sub>`Terrain.cs:147`</sub>

- **in** — ** absolute path.
- **out** — ** `true` on success. **`false` if `_grid == null`, the file is missing, or the stored dimensions differ from the live grid** (the three failure modes are indistinguishable).
- **used at** `game/EditorTerrain.cs:55` — `if (_terr != null && _terr.LoadHeightmap(SavePath)) GD.Print("[editor-terrain] loaded saved sculpt");`

#### `public void EditFlatten(float worldX, float worldZ, float radiusWorld, float strength)`
<sub>`Terrain.cs:157`</sub>

- **in** — ** `strength` is a per-call lerp weight, clamped with the falloff to `0..1`; callers pass `strength * dt * 0.15` clamped to `0.01..1`.
- **out** — ** void; no-op if `_grid == null`. Sets `Dirty`.
- **used at** `game/EditorTerrain.cs:95` — `case EBrush.Flatten: _terr.EditFlatten(pt.X, pt.Z, _radius, Mathf.Clamp(_strength * dt * 0.15f, 0.01f, 1f)); break;`

#### `public void EditSmooth(float worldX, float worldZ, float radiusWorld, float strength)`
<sub>`Terrain.cs:175`</sub>

- **in** — ** same units as `EditFlatten`.
- **out** — ** void; no-op if `_grid == null`. Sets `Dirty`.
- **used at** `game/EditorTerrain.cs:132` — `for (int i = 0; i < 4; i++) _terr.EditSmooth(at.X, at.Z, 62f, 0.5f);`

#### `public void EditRamp(Vector3 begin, Vector3 end, float radiusWorld)`
<sub>`Terrain.cs:197`</sub>

- **in** — ** `begin`/`end` are full world points — **their Y values are the target heights**, converted via `(Y + 1024)/2048`. Samples behind `begin`, past `end`, or outside the corridor are skipped.
- **out** — ** void. **Early-returns (no-op) if the horizontal span is under 1 m**, or if `_grid == null`. Sets `Dirty`.
- **used at** `game/EditorTerrain.cs:148` — `_terr.EditRamp(a, b, 40f);`

#### `public void RebuildChunk(int cxi, int cyi, bool withCollider = true)`
<sub>`Terrain.cs:234`</sub>

- **in** — ** chunk indices `0 .. _chunksX/_chunksY - 1`; `withCollider` is ANDed with the terrain's own `_withCollider`.
- **out** — ** void; out-of-range or unbuilt terrain is a silent no-op, as is a chunk smaller than 2×2.
- **used at** `game/Terrain.cs:282` — `for (int cx = cx0; cx <= cx1; cx++) for (int cy = cy0; cy <= cy1; cy++) { RebuildChunk(cx, cy, withCollider); if (!withCollider) _dirtyChunk…`

#### `public void RebuildAll()`
<sub>`Terrain.cs:285`</sub>

- **out** — ** void; no-op if `_chunkMi == null`.
- **used at** `game/Terrain.cs:507` — `terr.RebuildAll(); // builds every chunk's mesh + collider from _grid`

#### `public void FlushColliders()`
<sub>`Terrain.cs:287`</sub>

- **out** — ** void; no-op when the terrain has no colliders (still clears the set).
- **used at** `game/EditorTerrain.cs:110` — `else if (!mb.Pressed && !_paint && _brush != EBrush.Ramp) _terr.FlushColliders(); // held-drag stroke end: rebuild the touched chunks' colli…`

#### `public float SampleHeight(float worldX, float worldZ)`
<sub>`Terrain.cs:298`</sub>

- **in** — ** `worldX`, `worldZ` = Godot world metres (PEI spans roughly ±1800 on both axes). Z is **negated internally** (`float fy = (-worldZ - _bz) / UNIT;`) — callers pass ordinary Godot Z. Out-of-range XZ is silently clamped to the grid edge, so it returns the border height rather than erroring.
- **out** — ** world Y in metres, range `-1024 .. +1024` (`h*2048 - 1024`, 0.5 normalized = sea datum 0). **Failure path: returns `0f` when `_grid == null`** (terrain not loaded / no Unturned install) — that is a *valid-looking* height, not a sentinel, and every caller treats it as ground.
- **used at** `game/inventory/LootField.cs:161` — `float gy = Mathf.Max(p.Y, Terr.SampleHeight(p.X, p.Z)); // authored height (floors/shelves); never below the port's terrain`

#### `public byte SampleDominantLayer(float worldX, float worldZ)`
<sub>`Terrain.cs:314`</sub>

- **in** — ** same world-metre XZ convention, same internal `-worldZ` negation, clamped to grid.
- **out** — ** layer index `0..7` (0 Dirt, 1 Wheat, 2 Grass, 3 Gravel, 4 Road, 5 Sand/ocean, 6 Snow, 7 Stone). **Returns `255` when `_dom == null`** (no splatmaps loaded) — that is the real sentinel; `IsWater(255)` is false and `SurfAt` maps it to Grass.
- **used at** `game/AnimalField.cs:110` — `if (Terrain.IsWater(Terr.SampleDominantLayer(p.X, p.Z))) continue;`

#### `public static bool IsWater(byte layer) => layer == 5;`
<sub>`Terrain.cs:321`</sub>

- **in** — ** a layer byte from `SampleDominantLayer` (0..7 or 255).
- **out** — ** bool. 255 (no splat data) → `false`, i.e. unloaded terrain reads as land.
- **used at** `game/ZombieField.cs:68` — `if (Terrain.IsWater(Terr.SampleDominantLayer(gx, gz))) { water++; continue; } // no ocean spawns`

#### `public static Terrain Active;`
<sub>`Terrain.cs:323`</sub>

- **used at** `game/PlayerController.cs:4234` — `if (Terrain.Active != null && n.IsInGroup("terrain")) sf = Terrain.Active.SurfAt(p.X, p.Z);`

#### `public PlayerController.Surf SurfAt(float worldX, float worldZ)`
<sub>`Terrain.cs:326`</sub>

- **in** — ** world XZ metres.
- **out** — ** `PlayerController.Surf` enum. Default arm covers layers 0/2/7 **and 255**, so unloaded terrain returns `Surf.Grass` rather than failing.
- **used at** `game/PlayerController.cs:3926` — `if (Terrain.Active != null && n.IsInGroup("terrain")) sf = Terrain.Active.SurfAt(point.X, point.Z);`

#### `public static Node3D LoadTile(string heightmapPath, int coordX, int coordY, bool withCollider = true)`
<sub>`Terrain.cs:337`</sub>

- **in** — ** absolute `.heightmap` path (**big-endian ushorts, x outer / y inner**); tile landscape coords.
- **out** — ** `Node3D`. **Throws** if the file is missing or shorter than 257²×2 bytes; no null path.
- **used at** `game/Terrain.cs:420` — `t.AddChild(LoadTile(path, cx, cy, withCollider));`

#### `st.AddVertex(new Vector3(coordX * TILE_SIZE + y * UNIT, h[x,y] * TILE_HEIGHT - TILE_HEIGHT/2f, -(coordY * TILE_SIZE + x * UNIT))); // y-index = world X, x-index = world Z`
<sub>`Terrain.cs:355`</sub>


#### `public static Terrain CreateFlat(int tilesX = 3, int tilesZ = 3, bool withCollider = true)`
<sub>`Terrain.cs:381`</sub>

- **in** — ** sizes in 1024 m Landscape tiles (grid = `tiles*256 + 1` verts per axis).
- **out** — ** always non-null.
- **used at** `game/Main.cs:2385` — `var terr = Terrain.CreateFlat(3, 3);`

#### `public static Terrain LoadMap(string heightmapsDir, bool withCollider = true)`
<sub>`Terrain.cs:411`</sub>

- **out** — ** always non-null; **throws** on a missing directory.

#### `public static Terrain LoadMapMerged(string heightmapsDir, bool withCollider = true)`
<sub>`Terrain.cs:428`</sub>

- **in** — ** `heightmapsDir` absolute; `withCollider` gates all trimesh colliders.
- **out** — ** `Terrain` node. **Returns `null` when the directory does not exist** (logs the `UG_UNTURNED_DIR` hint). Returns an *empty but valid* `Terrain` when the directory exists with zero tiles.
- **used at** `game/WorldBuilder.cs:172` — `var terr = Terrain.LoadMapMerged(mapRoot + "/Landscape/Heightmaps", withCollider: true);`

### `UnityDatColorEx.cs`

#### `public static bool TryParseColor32RGB(this IDatValue node, out Color32 value)`
<sub>`UnityDatColorEx.cs:14`</sub>

Hex `RRGGBB` with optional leading `#`. **Length must be exactly 6 (+1 for `#`)** — `"FFF"` and `"FFFFFFFF"` both fail. Each channel `[0,255]` from `NumberStyles.HexNumber`. **Every failure path sets `value = new Color32(0, 0, 0, byte.MaxValue)` — opaque black, NOT `default(Color32)` which would be transparent.**

- **used at** `tests/UnturnedDat.Tests/UnityDatColorExTests.cs:27` — `Assert.AreEqual(expectedSuccess, value.TryParseColor32RGB(out Color32 actualValue));`

#### `public static Color32 ParseColor32RGB(this IDatValue node, Color32 defaultValue = default)`
<sub>`UnityDatColorEx.cs:57`</sub>


#### `public static bool TryParseColor32RGBA(this IDatValue node, out Color32 value)`
<sub>`UnityDatColorEx.cs:106`</sub>

Accepts **either** 6 hex chars (alpha defaults to 255) **or** 8 hex chars (`RRGGBBAA`), optional `#`. Any other length fails. **Failure path here is `value = default` — transparent black `(0,0,0,0)` — the opposite of the RGB variant.**

- **used at** `tests/UnturnedDat.Tests/UnityDatColorExTests.cs:121` — `Assert.AreEqual(expectedSuccess, value.TryParseColor32RGBA(out Color32 actualValue));`

#### `public static Color32 ParseColor32RGBA(this IDatValue node, Color32 defaultValue = default)`
<sub>`UnityDatColorEx.cs:173`</sub>


#### `public static Color LegacyParseColor(this IDatDictionary dict, string key, Color defaultValue)`
<sub>`UnityDatColorEx.cs:223`</sub>

Data.readColor` compat. Modern hex/sub-dictionary first (alpha forced to `1.0f`); otherwise falls back to `<key>_R`/`_G`/`_B` read as **floats in `[0,1]`** with per-channel defaults from `defaultValue`. Returned alpha is always `1.0f` (3-arg `Color` ctor). No `defaultValue` parameter default — you must pass one.

- **used at** `tests/UnturnedDat.Tests/UnityDatColorExTests.cs:220` — `Assert.AreEqual(expectedValue, dictionary.LegacyParseColor("key", defaultValue));`

#### `public static Color32 LegacyParseColor32RGB(this IDatDictionary dict, string key, Color32 defaultValue)`
<sub>`UnityDatColorEx.cs:240`</sub>

Data.ReadColor32RGB` compat. Same shape but the legacy fallback reads `<key>_R`/`_G`/`_B` as **bytes in `[0,255]`** (`ParseUInt8`), alpha forced 255. **This byte-vs-float asymmetry with `LegacyParseColor` is the thing to not re-derive by hand.**

- **used at** `tests/UnturnedDat.Tests/UnityDatColorExTests.cs:232` — `Assert.AreEqual(expectedValue, dictionary.LegacyParseColor32RGB("key", defaultValue));`

### `UnityDatEx.cs`

#### `public static Vector2 ParseVector2(this IDatValue node, Vector2 defaultValue = default)`
<sub>`UnityDatEx.cs:72`</sub>


#### `public static bool TryParseVector2(this IDatDictionary dictionary, string key, out Vector2 value)`
<sub>`UnityDatEx.cs:82`</sub>

Dual-format: inline string **or** a sub-dictionary with `X`/`Y` keys. **Gotcha: the sub-dictionary branch returns `true` unconditionally**, filling missing components with `0f` — a `{ }` sub-dictionary is a "successful" `(0,0)`. Missing key or a list node → `false`/`(0,0)`.

- **used at** `tests/UnturnedDat.Tests/UnityDatExTests.cs:60` — `Assert.AreEqual(expectedSuccess, dictionary.TryParseVector2("key", out Vector2 actualValue));`

#### `public static bool TryParseVector3(this IDatValue node, out Vector3 value)`
<sub>`UnityDatEx.cs:122`</sub>

"1, 2, 3"` or `"(1, 2, 3)"`. Requires two commas with ≥1 char between them (second delimiter searched from `firstDelimiterIndex + 2`). Failure → `default` (`(0,0,0)`) + `false`. Source comment at `:119` flags it as duplicated at `Vector3Ex.TryParseVector3` in upstream U3-SDK (that type is **not** present in this repo).

- **used at** `tests/UnturnedDat.Tests/UnityDatExTests.cs:101` — `Assert.AreEqual(expectedSuccess, value.TryParseVector3(out Vector3 actualValue));`

#### `public static Vector3 ParseVector3(this IDatValue node, Vector3 defaultValue = default)`
<sub>`UnityDatEx.cs:191`</sub>


#### `public static Vector3 LegacyParseVector3(this IDatDictionary dict, string key)`
<sub>`UnityDatEx.cs:241`</sub>

Back-compat for `Data.readVector3`. Tries modern inline/sub-dictionary first; on failure falls back to three separate keys `<key>_X`, `<key>_Y`, `<key>_Z` read with `ParseFloat` default `0f`. **Has no `defaultValue` parameter — the failure/empty path is always `(0,0,0)`.**

- **used at** `tests/UnturnedDat.Tests/UnityDatExTests.cs:185` — `Assert.AreEqual(expectedValue, dictionary.LegacyParseVector3("key"));`

### `UnityMath.cs`

#### `public struct Vector2 : IEquatable<Vector2>`
<sub>`UnityMath.cs:11`</sub>

Statics: `zero` `:15`, `one` `:16`, `up` `:17`, `right` `:18`. Indexer `:19` (**index ≥ 1 returns `y`, no bounds check**). `magnitude` `:20`, `sqrMagnitude` `:21`, `normalized` `:22` (**returns `zero` when magnitude ≤ `1E-05f`, does not divide by zero**). Operators `+ - unary- * (both orders) /` `:23-28`. `operator ==` `:29` uses **`sqrMagnitude < 9.99999944E-11f`, i.e. approximate equality with ~…

- **used at** `core/UnturnedSim/PlayerMovementSim.cs:21` — `public Vector3 Step(Vector2 inputDir, bool wantJump, bool grounded, float dt)`

#### `public struct Vector3 : IEquatable<Vector3>`
<sub>`UnityMath.cs:41`</sub>

Statics `zero one up down forward back right left` `:46-53`. Indexer `:54` (unchecked; index ≥ 2 returns `z`). `magnitude` `:55`, `sqrMagnitude` `:56`, `normalized` `:57` (**`zero` below `1E-05f`**). Operators `:58-65`, with the same **approximate `operator ==`** (`:64`). `Dot` `:66`, `Cross` `:67` (right-handed, Unity convention), `Distance` `:68`, `Lerp` `:69` (clamped), `LerpUnclamped` `:70`, `…

- **used at** `core/UnturnedNet/ServerCombat.cs:469` — `if (d < reach && d > 1e-4f && Vector3.Dot(to / d, fwd) > 0.3f && d < bestD) { bestD = d; bestPlayer = pe.OwnerPlayerId; bestZombie = null; }`

#### `public struct Vector4 : IEquatable<Vector4>`
<sub>`UnityMath.cs:79`</sub>

No `magnitude`/`normalized`/`Dot`. Exists mainly as the `Color` conversion vehicle.

- **used at** `core/SDG.Compat/UnityMath.cs:146` — `public static implicit operator Vector4(Color c) => new Vector4(c.r, c.g, c.b, c.a);`

#### `public struct Quaternion : IEquatable<Quaternion>`
<sub>`UnityMath.cs:98`</sub>


#### `public static Quaternion Euler(float px, float py, float pz)`
<sub>`UnityMath.cs:104`</sub>


#### `operator *`
<sub>`UnityMath.cs:117`</sub>


#### `public static float Dot(Quaternion a, Quaternion b)`
<sub>`UnityMath.cs:124`</sub>

Caller: `game/GodotCompat.cs:13: public static Godot.Quaternion ToGodot(this UnityEngine.Quaternion q) => new Godot.Quaternion(q.x, q.y, -q.z, -q.w);


#### `public struct Color : IEquatable<Color>`
<sub>`UnityMath.cs:132`</sub>

Caller: `game/GodotCompat.cs:15: public static Godot.Color ToGodot(this UnityEngine.Color c) => new Godot.Color(c.r, c.g, c.b, c.a);

- **used at** `game/GodotCompat.cs:15` — `public static Godot.Color ToGodot(this UnityEngine.Color c) => new Godot.Color(c.r, c.g, c.b, c.a);`

#### `public struct Color32 : IEquatable<Color32>`
<sub>`UnityMath.cs:156`</sub>


#### `public static Color32 Lerp(Color32 a, Color32 b, float t)`
<sub>`UnityMath.cs:167`</sub>


#### `GetHashCode()`
<sub>`UnityMath.cs:170`</sub>

Caller: `core/UnturnedDat/UnityDatColorEx.cs:50: value = new Color32(r, g, b, byte.MaxValue);` — `NO CALLERS` outside `UnturnedDat` and its tests.


### `VehicleNetSync.cs`

#### `public bool TryGetNode(uint netId, out Vehicle node)`
<sub>`VehicleNetSync.cs:42`</sub>


#### `public const float RopeReach = 9f;`
<sub>`VehicleNetSync.cs:65`</sub>


### `WindField.cs`

#### `public static float SampleWind(Vector3 worldPos)`
<sub>`WindField.cs:23`</sub>

- **in** — ** `worldPos` — **only X and Z are used**; Y is ignored (height bonuses are applied by the caller).
- **out** — ** `0f..1f`. **`TestWind` (`WindField.cs:22`, `public static float?`) short-circuits it to a fixed value; `null` = live noise.** Stateless and allocation-free after first call.
- **used at** `game/Deployable.cs:616` — `_windFactor = Mathf.Min(2f, WindField.SampleWind(GlobalPosition) * heightMult);`

### `WorldBuilder.cs`

#### `public struct FixtureRecord { public ushort DefId; public Vector3 Pos; public float YawDegrees; public Basis Basis; public int StationId; }`
<sub>`WorldBuilder.cs:22`</sub>


#### `public sealed class WorldBuildResult { ... }`
<sub>`WorldBuilder.cs:31`</sub>


#### `public static List<(string mesh, bool display, string label)> ContainerKinds()`
<sub>`WorldBuilder.cs:98`</sub>

- **in** — ** none.
- **out** — ** deterministic list; currently 13 entries. Empty only if the registry is emptied.
- **used at** `game/inventory/ContainerSchema.cs:25` — `var kinds = WorldBuilder.ContainerKinds();`

#### `public static async Task<WorldBuildResult> BuildFullWorld(Node root, WorldMode mode, string mapRoot, string mapPlace, bool noZombies, bool syncLoad, bool bakeNav, string activeHoliday)`
<sub>`WorldBuilder.cs:113`</sub>

- **in** — `** `mode` ∈ `{Aerial, Playable, Dedicated, Client, Editor}` (`WorldBuilder.cs:16`) — `Aerial` alone disables colliders, `Playable` alone spawns the local player; `mapRoot` = absolute map dir; `mapPlace` = placements filename under `content/objects/`; `syncLoad=true` removes every frame-yield (bake tools + dedicated); `…
- **out** — ** `WorldBuildResult`, never null. **Failure path: no map data → returns early with `Terr == null` and `Ready == false`** for non-Dedicated; Dedicated instead builds a `WorldBoundaryShape3D` ground plane, spawns interactables, sets `Ready = true`, and returns with `Terr == null`.
- **used at** `game/Main.cs:2235` — `var res = await WorldBuilder.BuildFullWorld(this, _peiPlayable ? WorldMode.Playable : WorldMode.Aerial,`

#### `public static void SpawnInteractables(Node root, Terrain terr, string mapRoot, WorldBuildResult result)`
<sub>`WorldBuilder.cs:776`</sub>

- **in** — ** `terr` **may be null** — then every Y is 0, which is where the fallback world's ground plane is; `result` may be null (deadzones simply not recorded).
- **out** — ** void; ids come from world-build order, identically on every peer.
- **used at** `game/WorldBuilder.cs:534` — `SpawnInteractables(root, terr, mapRoot, result);`

#### `public static Vector3 InteractableAnchor(string mapRoot)`
<sub>`WorldBuilder.cs:803`</sub>

- **in** — ** `mapRoot`.
- **out** — ** `Vector3` with **Y always 0**. **No spawn data → `new Vector3(0f, 0f, -350f)`.**
- **used at** `game/WorldBuilder.cs:779` — `var anchor = InteractableAnchor(mapRoot);`

#### `public static void AttachPlayerShell(Node root, PlayerController player, bool withCropManager)`
<sub>`WorldBuilder.cs:820`</sub>

- **in** — ** `withCropManager` — `true` for SP (local growth authority), `false` on a joined client (server owns growth).
- **out** — ** void. No null guard on `player`.
- **used at** `game/ClientWorldSession.cs:480` — `WorldBuilder.AttachPlayerShell(this, shell, withCropManager: false); // the SP shell block verbatim; crops: the SERVER owns growth`

#### `public static void SpawnFixturesDirect(Node root, IEnumerable<FixtureRecord> fixtures)`
<sub>`WorldBuilder.cs:839`</sub>

- **in** — ** `fixtures` — **null is tolerated** (early return); unknown `DefId` and unhandled `Fixture` kinds are skipped.
- **out** — ** void.
- **used at** `game/MpLoopback.cs:276` — `WorldBuilder.SpawnFixturesDirect(GetParent() ?? this, Fixtures);`

#### `public static WorldBuildResult BuildPeiPlayWorld(Node root, string mapRoot, bool horde)`
<sub>`WorldBuilder.cs:864`</sub>

- **in** — ** `mapRoot`; `horde` bool.
- **out** — ** `WorldBuildResult`; **no map data → returns with `Terr == null`, `Player == null`, `Ready == false`** (no fallback ground here, unlike `BuildFullWorld`).
- **used at** `game/Main.cs:3251` — `var res = WorldBuilder.BuildPeiPlayWorld(this, MapDir("PEI"), _peiHorde);`

### `WorldItemNetSync.cs`

#### `ContainerNetSync`
<sub>`WorldItemNetSync.cs:24`</sub>

public int TrackedCount { get; }


### `WorldNetSync.cs`

#### `public static class CropNetSchema { public static void RegisterAll(CropSchema schema) }`
<sub>`WorldNetSync.cs:17`</sub>

- **in** — ** the schema to fill; must be called on **both** server and client with the same tsv (content-hash-matched).
- **out** — ** void; an empty tsv registers nothing and every plant then falls to the SP-local fallback def.
- **used at** `game/ClientNode.cs:46` — `CropNetSchema.RegisterAll(_client.Crops.Schema); // Phase 8 (§3.7): growth stages derive from the synced defs + snapshot tick`

#### `public sealed class WorldClockNetSync`
<sub>`WorldNetSync.cs:36`</sub>

- **in** — ** `dnc` **may be null** — the ctor and `Tick` then no-op, but `Publish()` would NRE.
- **used at** `game/MpLoopback.cs:317` — `ClockSync = new WorldClockNetSync(Server, DayNight, driveFromTick: false);`

#### `public sealed class CropNetSync`
<sub>`WorldNetSync.cs:89`</sub>

- **in** — ** `host` = any in-tree node (used for `GetTree`); a null tree makes `Tick` no-op.
- **used at** `game/MpLoopback.cs:319` — `CropSync = new CropNetSync(Server, this);`

#### `public sealed class ResourceNetSync`
<sub>`WorldNetSync.cs:199`</sub>

- **in** — ** `field` may be null (bitmap not initialized; `Tick` no-ops); `index` in the field's index space.
- **out** — ** `SetAlive` returns **`false` if the transaction was rejected** (out of range / already in that state), in which case the world is not touched.
- **used at** `game/MpLoopback.cs:327` — `ResourceSync = new ResourceNetSync(Server, Resources);`

#### `public sealed class DestructibleNetSync`
<sub>`WorldNetSync.cs:243`</sub>

- **in** — ** `field` may be null → nothing seeded, but `Tick` still drives `DestructibleHost.Tick`.
- **used at** `game/MpLoopback.cs:329` — `DestructibleSync = new DestructibleNetSync(Server, Destructibles); // seed health/respawn + mirror rubble alive-bits`

### `WorldStateViews.cs`

#### `public partial class WorldClockView : Node // public NetWorldClient Client; public DayNightCycle DayNight;`
<sub>`WorldStateViews.cs:18`</sub>

- **in** — ** null `Client`/`DayNight`, no clock, or `LastAppliedServerTick <= 0` → no-op.
- **used at** `game/ClientWorldSession.cs:169` — `AddChild(new WorldClockView { Client = Client, DayNight = DayNight });`

#### `public partial class ResourceAliveView : Node // public NetWorldClient Client; public ResourceField Field;`
<sub>`WorldStateViews.cs:49`</sub>

- **in** — ** null `Client`/`Field` → no-op; `DestructibleAliveView` notably does **not** `IsInstanceValid`-check its Field, unlike `ResourceAliveView`.

#### `public partial class InteractableStateView : Node // public NetWorldClient Client; public Node WorldRoot;`
<sub>`WorldStateViews.cs:116`</sub>

- **in** — ** `WorldRoot` **defaults to `GetParent()`** when null.
- **used at** `game/ClientWorldSession.cs:176` — `AddChild(new InteractableStateView { Client = Client, WorldRoot = GetParent() ?? (Node)this });`

### `ZombieField.cs`

#### `public void LoadFromPei(string peiRoot)`
<sub>`ZombieField.cs:42`</sub>

- **out** — ** void. **Zero pockets → prints and returns (no zombies at all).** Missing `Animals.dat` → silent return with pockets but no points.
- **used at** `game/WorldBuilder.cs:692` — `zf.LoadFromPei(mapRoot);`

#### `public List<(int pk, Vector3 pos)> DebugPlanSpawns()`
<sub>`ZombieField.cs:188`</sub>

- **out** — ** possibly-empty list; pockets with `Cap <= 0` or no points are skipped.
- **used at** `game/ZombieDirector.cs:106` — `var planned = planner.DebugPlanSpawns();`

### `game/AnimalAgent.cs`

#### `public void Begin()`
<sub>`game/AnimalAgent.cs:27`</sub>

- **used at** `game/AnimalField.cs:130` — `agent.Begin(); // idle -> wander loop (see AnimalAgent)`

### `game/AnimalCatalog.cs`

#### `public struct Kind`
<sub>`game/AnimalCatalog.cs:10`</sub>


#### `public static byte SpeciesForAnimalId(ushort animalId)`
<sub>`game/AnimalCatalog.cs:20`</sub>

- **out** — ** the byte; **fail-safe `0` (deer)** for any unknown id — never an exception, never out of range.
- **used at** `game/AnimalField.cs:126` — `var agent = new AnimalAgent { Terr = Terr, Foot = def.foot, Home = new Vector3(p.X, 0f, p.Z), Seed = h ^ 0xA53Cu, Species = AnimalCatalog.Sp…`

#### `public static Kind Get(byte species)`
<sub>`game/AnimalCatalog.cs:26`</sub>

- **out** — ** the `Kind`; **out-of-range → `All[0]` (deer)**, so a bad byte renders something rather than nothing.
- **used at** `game/AnimalPuppets.cs:50` — `var kind = AnimalCatalog.Get(e.Species);`

### `game/AttachmentMenu.cs`

#### `static readonly string[] Slots = { "Sight","Tactical","Grip","Barrel","Magazine" }`
<sub>`game/AttachmentMenu.cs:12`</sub>

The attachment slot-name vocabulary, keyed against `Viewmodel.SlotHasModel/SlotAttached/SetSlotMesh/TryGetSlotScreen`.


### `game/Bed.cs`

#### `public static readonly BedClaims Claims = new BedClaims()`
<sub>`game/Bed.cs:17`</sub>


#### `public static Bed Spawn(Node parent, Vector3 basePos, float yawDeg)`
<sub>`game/Bed.cs:33`</sub>

- **in** — `basePos` world metres; `yawDeg`.
- **out** — the live node with a fresh incrementing `BedId`.
- **used at** `game/WorldBuilder.cs:784` — `Bed.Spawn(root, new Vector3(ax - 5.0f, H(ax - 5.0f, az + 2.0f), az + 2.0f), 90f);`

#### `public void ApplyReplicatedClaim(ulong owner)`
<sub>`game/Bed.cs:151`</sub>

- **in** — `owner`, **0 = released**.
- **used at** `game/WorldStateViews.cs:146` — `if (Bed.TryGetByNetId(kv.Key, out var b)) b.ApplyReplicatedClaim(kv.Value);`

#### `public static bool TryGetSpawn(ulong player, out Vector3 position, out float yaw)`
<sub>`game/Bed.cs:156`</sub>

- **out** — **`false` = no bed, use the map spawn** (`position` zeroed).
- **used at** `game/PlayerController.cs:3144` — `Vector3 target = Bed.TryGetSpawn(PlayerId, out var bedSpawn, out _) ? bedSpawn + Vector3.Up * 0.5f : Spawn;`

#### `public static void ResetForNewWorld()`
<sub>`game/Bed.cs:167`</sub>

- **used at** `game/WorldBuilder.cs:778` — `Bed.ResetForNewWorld(); // a map reload must not inherit the previous level's claims`

### `game/ConnectionPort.cs`

#### `public interface IPowerDevice { bool PowerProducing; bool PowerOnFire; bool PowerConducting => true; float PowerScale => 1f; uint PowerNetId; IReadOnlyList<ConnectionPort> PowerPorts }`
<sub>`game/ConnectionPort.cs:7`</sub>

- **used at** `game/GridPowerSource.cs:16` — `public partial class GridPowerSource : Node3D, IPowerDevice`

#### `public const uint PortLayer = 1u << 8`
<sub>`game/ConnectionPort.cs:22`</sub>


#### `public static ConnectionPort Create(IPowerDevice owner, DeployableDef.Port p, string providerName)`
<sub>`game/ConnectionPort.cs:58`</sub>

- **in** — `owner` non-null (stored, drives `Usable`); `p.Pos` in the owner's *local authored* frame (metres); `p.Watts` ≥ 0 watts; `p.Kind`/`p.Role`; `providerName` used verbatim in `InfoLine`.
- **out** — the port node; **never null**, no failure path.
- **used at** `game/FluidPurifier.cs:31` — `_powerInput = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = new Vector3(0f, 1.25f, 0.42…`

#### `public static StandardMaterial3D ArrowMaterial(Color c)`
<sub>`game/ConnectionPort.cs:87`</sub>

- **in** — `c` the tint (use `ArrowBlue`/`ArrowRed`).
- **out** — a fresh material; the underlying texture is a lazily built static singleton.
- **used at** `game/DeployablePlacer.cs:39` — `_arrowMat = ConnectionPort.ArrowMaterial(ConnectionPort.ArrowRed); // in/out arrows on the ghost's ports (stand up with it)`

#### `public static Node3D MakeArrow(DeployableDef.Port p, StandardMaterial3D mat, Vector3 basePos, Vector3 outDirOverride = default)`
<sub>`game/ConnectionPort.cs:120`</sub>

- **in** — `p.Pos` local metres; `p.Kind` picks the flow direction; `mat` shared (tinting it recolours every arrow using it); `basePos` = the port position when the arrow parents the *device* (ghost), `Vector3.Zero` when it parents the port cube; `outDirOverride` — pass a non-zero world-ish direction when the caller is **not** in …
- **out** — a `Node3D` root holding two quads; never null.
- **used at** `game/HosePort.cs:67` — `_arrow = ConnectionPort.MakeArrow(new DeployableDef.Port { Kind = pk, Pos = Position }, _arrowMat, Vector3.Zero, outDir);`

#### `public bool DebugArrowVisible { get; }`
<sub>`game/ConnectionPort.cs:148`</sub>

- **used at** `game/testing/tests/DeployTests.cs:50` — `foreach (var p in d.Ports) if (p.DebugArrowVisible) allHidden = false;`

#### `public void SetArrowState(bool show, bool available)`
<sub>`game/ConnectionPort.cs:153`</sub>

- **in** — `show`; `available` ignored.
- **out** — `void`; no-op if the arrow wasn't built.
- **used at** `game/PlayerController.cs:1053` — `p.SetArrowState(true, p.Usable && !PortWired(p));`

#### `public string InfoLine()`
<sub>`game/ConnectionPort.cs:170`</sub>

- **out** — a non-null string; falls through to bare `ProviderName` for an unknown kind.
- **used at** `game/PlayerController.cs:400` — `WireHudSet(_wirePort == null ? null : _wirePort.InfoLine() + (PortWired(_wirePort) ? " ([RMB] hold: clear · tap: unplug)" : ""));`

#### `public void Deactivate()`
<sub>`game/ConnectionPort.cs:181`</sub>

- **out** — `void`. Irreversible (nothing restores the layer).
- **used at** `game/Deployable.cs:519` — `foreach (var p in Ports) if (IsInstanceValid(p)) p.Deactivate();`

#### `public void UpdateCubeColor()`
<sub>`game/ConnectionPort.cs:196`</sub>

- **used at** `game/PowerNet.cs:72` — `kv.Key.UpdateCubeColor(); // reflect the new occupancy shade`

### `game/Deployable.cs`

#### `IPowerDevice`
<sub>`game/Deployable.cs:15`</sub>

public bool PowerProducing => IsPowered`, `public bool PowerOnFire => OnFire`, `public bool PowerConducting => Def == null || !Def.IsSwitch || _switchOn`, `public float PowerScale => Def == null ? 1f : Def.IsWindTurbine ? _windFactor : (Def.Fuel > 0f && !Def.IsBattery) ? _powerLevel : 1f`, `public uint PowerNetId => NetId`, `public IReadOnlyList<ConnectionPort> PowerPorts => Ports


#### `public bool SwitchOn { get; }`
<sub>`game/Deployable.cs:40`</sub>

Other public state: `Def`, `NetId`, `Ports` (`List<ConnectionPort>`, def-authored order — the MP sub-address), `Health`/`HealthMax`, `Fuel`/`FuelMax`.


#### `public bool IsPowered { get; }`
<sub>`game/Deployable.cs:53`</sub>

- **used at** `game/Main.cs:1685` — `GD.Print($"[POWERTEST] gen.IsPowered={placedGen.IsPowered} output={outp.Live:0}w consumer.recv={cons.Live:0}w powered={cons.Powered} passthr…`

#### `public static MeshInstance3D BuildMesh(DeployableDef def, out Aabb localAabb)`
<sub>`game/Deployable.cs:106`</sub>

- **in** — `def` non-null.
- **out** — the mesh instance; `localAabb` is `default(Aabb)` when the mesh failed to load.
- **used at** `game/DeployablePlacer.cs:36` — `_ghost = Deployable.BuildMesh(def, out _localAabb);`

#### `internal static Vector3 EnvVec3(string name, Vector3 dflt)`
<sub>`game/Deployable.cs:152`</sub>

- **out** — the default on a missing/malformed value.
- **used at** `game/Main.cs:1889` — `var gp = GasPump.Attach(this, pumpPos, standUp, Deployable.EnvVec3("UG_GPP", GasPump.PortLocal), pumpMesh);`

#### `public static Deployable Spawn(Node parent, DeployableDef def, Vector3 surface, float yawDeg, SDG.Unturned.Item backing = null)`
<sub>`game/Deployable.cs:162`</sub>

- **in** — `surface` the **ground contact point** (raycast hit, world metres) — the model is lifted so its base sits there; `yawDeg`; `backing` — **null = fresh spawn (full HP/fuel)**, non-null restores HP from `item.quality` (%) and fuel from `item.fuelLevel` (clamped to `def.Fuel`).
- **out** — the live node, already parented.
- **used at** `game/DeployableReplicaView.cs:77` — `node = Deployable.Spawn(parent, def, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees);`

#### `public void SetLookFocused(bool on)`
<sub>`game/Deployable.cs:253`</sub>

- **used at** `game/PlayerController.cs:270` — `_focusDeployable?.SetLookFocused(true);`

#### `public void TakeDamage(float amount)`
<sub>`game/Deployable.cs:270`</sub>

- **in** — `amount` HP, ≤ 0 ignored.
- **out** — `void`; re-entrant calls while already dying (`_deadTimer >= 0`) or exploded are ignored.
- **used at** `game/PlayerController.cs:3918` — `else if (collider is Deployable dep && !dep.IsWreck) { dep.TakeDamage(b.VehicleDamage); SpawnSurfaceImpact(point, hit["normal"].AsVector3(),…`

#### `public void DetonateManual()`
<sub>`game/Deployable.cs:365`</sub>

- **used at** `game/Deployable.cs:378` — `{ dp.DetonateManual(); n++; }`

#### `public static int DetonateAllCharges(SceneTree tree)`
<sub>`game/Deployable.cs:372`</sub>

- **in** — `tree` — **null returns 0**.
- **out** — the count fired.
- **used at** `game/PlayerController.cs:1808` — `int n = Deployable.DetonateAllCharges(GetTree());`

#### `public void TogglePower()`
<sub>`game/Deployable.cs:391`</sub>

- **out** — `void`; silently no-ops when the toggle gate rejects.
- **used at** `game/PlayerController.cs:3523` — `if (!RequestToggleDeployable(_fHeldDeploy)) _fHeldDeploy.TogglePower();`

#### `public void NetSetPowered(bool on)`
<sub>`game/Deployable.cs:406`</sub>

- **used at** `game/DeployableReplicaView.cs:83` — `node.NetSetPowered(e.ToggledOn);`

#### `public void SetSalvagePrompt(string line2, Color color)`
<sub>`game/Deployable.cs:484`</sub>

- **used at** `game/PlayerController.cs:1283` — `else if (!HasBlowtorch) { dp.SetSalvagePrompt("Requires blowtorch to salvage", red); _salvageTimer = 0f; }`

#### `public void Salvage()`
<sub>`game/Deployable.cs:492`</sub>

- **used at** `game/PlayerController.cs:1292` — `else dp.Salvage();`

#### `public void Pickup()`
<sub>`game/Deployable.cs:525`</sub>

- **used at** `game/PlayerController.cs:1136` — `d.Pickup(); // frees any wires plugged into it + despawns`

#### `public void DebugStage(string s)`
<sub>`game/Deployable.cs:532`</sub>

- **used at** `game/Main.cs:1931` — `placedGen.DebugStage(dmgStage);`

### `game/DeployableDef.cs`

#### `public const float SeaLevel = 25.6f`
<sub>`game/DeployableDef.cs:63`</sub>


#### `public struct DeployLight { bool Spot; Vector3 Pos; Vector3 Dir; float Range; float AngleDeg; float Energy; Color Color; }`
<sub>`game/DeployableDef.cs:81`</sub>


#### `public static readonly DeployableDef[] All`
<sub>`game/DeployableDef.cs:311`</sub>

- **used at** `game/DeployableNetSchema.cs:14` — `foreach (var def in DeployableDef.All)`

#### `public static DeployableDef ById(ushort id)`
<sub>`game/DeployableDef.cs:313`</sub>

- **out** — **`null` for an unknown id** — `DeployableReplicaView` treats that as fail-closed (never materialize).
- **used at** `game/DeployableReplicaView.cs:45` — `var def = DeployableDef.ById(e.DefId);`

#### `public StandardMaterial3D MakeMaterial()`
<sub>`game/DeployableDef.cs:352`</sub>

- **out** — never null; falls back to a plain white roughness-1 material when the texture is missing.
- **used at** `game/Deployable.cs:110` — `var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = def.MakeMaterial() };`

#### `public Basis MeshBasis()`
<sub>`game/DeployableDef.cs:378`</sub>

- **out** — `Basis.Identity` when `MeshEuler == Vector3.Zero` (the common case).
- **used at** `game/Deployable.cs:111` — `Basis mrot = def.MeshBasis(); // per-def model orientation fixup (battery's ripped mesh stands up upside-down + 180 off); identity for the r…`

#### `public static float GroundLift(Aabb localAabb)`
<sub>`game/DeployableDef.cs:395`</sub>

- **in** — `localAabb` in the flat authored frame.
- **out** — metres; `0` for a degenerate/empty AABB.
- **used at** `game/DeployablePlacer.cs:92` — `Point + Vector3.Up * (up ? -_localAabb.Position.Y : DeployableDef.GroundLift(_localAabb))); // base sits on the surface point`

### `game/DeployableNetSchema.cs`

#### `public static void RegisterAll(DeployableSchema schema)`
<sub>`game/DeployableNetSchema.cs:12`</sub>

- **in** — `schema` non-null; called on both server and client (the content hash guarantees they match).
- **out** — `void`.

### `game/DeployablePlacer.cs`

#### `public bool Aim(Camera3D cam)`
<sub>`game/DeployablePlacer.cs:47`</sub>

- **in** — `cam` non-null; `Def` set.
- **out** — `Valid`; **`false` when `Def`/`cam` is null or the ray hits nothing** (in the no-hit case `Point` is still set to the range endpoint and the ghost turns red).

### `game/DevConsole.cs`

#### `public static bool TryResolveTeleport(string arg, out string serverCmd, out string locationName)`
<sub>`game/DevConsole.cs:467`</sub>

Location-name prefix match (spaces stripped, case-insensitive, shortest name wins) → the numeric `teleport x y z` wire form with the same +3 m drop height as the SP path.

- **in** — `arg` a partial place name; must be non-null (`arg.Replace` would throw).
- **out** — `true` + both outs. Failure: `false`, both outs `null`.

#### `public static bool ParseClock(string s, out float hours, bool allowNeg)`
<sub>`game/DevConsole.cs:549`</sub>

Parses named times (`noon`/`dawn`/…), 12-hour (`8am`, `6:30pm`), 24-hour `HH:MM`, military `HHMM`, or a bare number, into hours.

- **in** — `s` non-null; `allowNeg` true for `timeAdd` deltas, false for absolute `timeSet`.
- **out** — `true` + `hours` (float hours, typically 0–24). Failure: `false`, `hours = 0f` — also `false` for minutes ≥ 60 and, when `!allowNeg`, for negatives.

#### `public static bool ParseVolume(string s, out float ml)`
<sub>`game/DevConsole.cs:576`</sub>

"500"`/`"500ml"` → 500; `"1.5L"` → 1500.

- **in** — null-tolerant (`(s ?? "")`); negative values rejected.
- **out** — `true` + mL. Failure: `false`, `ml = 0f`.

#### `public static string FormatTime(float t01)`
<sub>`game/DevConsole.cs:589`</sub>

0..1 day fraction → `"HH:MM"` plus `" (midnight|dawn|noon|dusk)"` on the exact hour.

- **in** — `t01` wrapped with `PosMod`, so any real number is legal.
- **out** — always a string; handles the 59.5→60 minute rollover into the next hour.

#### `public static class MapNodes`
<sub>`game/DevConsole.cs:603`</sub>

PEI's real named locations, parsed once from `content/nodes.tsv`.

- **out** — the list; **empty (not null) if the TSV is missing** — so `MapUI` just draws no towns rather than crashing.
- **used at** `game/MapUI.cs:40` — `foreach (var (name, pos) in MapNodes.Locations)`

### `game/Door.cs`

#### `public static Door Spawn(Node parent, Vector3 basePos, float yawDeg, ulong owner, Vector3? size = null)`
<sub>`game/Door.cs:38`</sub>

- **in** — `basePos` world metres (the floor point); `owner` steam-id-shaped, 0 = unowned; `size` — **null = the default 1.0 × 2.0 × 0.12 m leaf**.
- **out** — the live node.
- **used at** `game/WorldBuilder.cs:783` — `Door.Spawn(root, new Vector3(ax - 3.0f, H(ax - 3.0f, az + 2.0f), az + 2.0f), 0f, owner: 1UL);`

#### `public bool TryToggle(ulong player, ulong group, double now)`
<sub>`game/Door.cs:93`</sub>

- **in** — `player`/`group` ids; `now` **sim seconds, not wall clock** (the cooldown is measured against it).
- **out** — `true` = toggled; `false` sets `LastRefusal`.
- **used at** `game/PlayerController.cs:3062` — `if (d.TryToggle(PlayerId, GroupId, _interactClock)) return true;`

#### `public uint NetId { get; set; }`
<sub>`game/Door.cs:116`</sub>


#### `public static bool TryGetByNetId(uint netId, out Door door)`
<sub>`game/Door.cs:133`</sub>

- **out** — **`false` is normal, not an error** — a door outside this client's world or already broken down locally.
- **used at** `game/ClientWorldSession.cs:233` — `if (!Door.TryGetByNetId(e.NetId, out var d)) return;`

#### `public void ApplyReplicatedToggle(bool open)`
<sub>`game/Door.cs:159`</sub>

- **out** — `void`; no-op when already in that state.
- **used at** `game/InteractableNetSync.cs:132` — `if (node.IsOpen != open) node.ApplyReplicatedToggle(open);`

#### `public bool TakeDamage(float amount)`
<sub>`game/Door.cs:170`</sub>

- **in** — `amount` HP; ≤ 0 or an already-dead door returns false.
- **out** — **`true` only on the call that destroyed it** (which `QueueFree`s the node).
- **used at** `game/PlayerController.cs:2048` — `_focusBed.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult);`

### `game/FluidContainer.cs`

#### `public enum FluidRole { Source, Storage, Consumer, Splitter, Combiner, Pump, Transformer, Valve }`
<sub>`game/FluidContainer.cs:13`</sub>


#### `public virtual (string text, Color color) StatusLine()`
<sub>`game/FluidContainer.cs:60`</sub>

- **out** — **`text == null` is the sentinel for "this device gets no status billboard"** (splitter/combiner). Overridden by `FluidPump` (`off` / `no power` / `idle — no supply` / `pumping`) and `FluidPurifier` (`no power` / `idle — no water` / `purifying`).

#### `public static FluidContainer Make(FluidRole role, FluidTank tank, float flowRate = 50f)`
<sub>`game/FluidContainer.cs:75`</sub>

- **in** — `role`; `tank` non-null for a tanked role; `flowRate` mL/s base supply/intake.
- **used at** `game/FluidDeploy.cs:17` — `FluidRole.Source => FluidContainer.Make(FluidRole.Source, new FluidTank(def.FluidType, def.FluidCapacity, def.FluidCapacity), def.FluidRate)…`

#### `public static FluidContainer MakeFitting(FluidRole role, int ways)`
<sub>`game/FluidContainer.cs:78`</sub>

- **used at** `game/FluidDeploy.cs:20` — `FluidRole.Splitter => FluidContainer.MakeFitting(FluidRole.Splitter, def.FluidWays),`

#### `public static FluidContainer MakeTransformer(FluidType inp, FluidType outp, float flowRate = 50f, float ratio = 1f)`
<sub>`game/FluidContainer.cs:82`</sub>

- **used at** `game/FluidDeploy.cs:25` — `: FluidContainer.MakeTransformer(def.FluidType, def.FluidOut, def.FluidRate, 1f),`

#### `public static FluidContainer MakeValve()`
<sub>`game/FluidContainer.cs:86`</sub>

- **used at** `game/DevConsole.cs:348` — `var valve = FluidContainer.MakeValve(); valve.Position = c + Vector3.Up * 1.0f; world.AddChild(valve);`

#### `public virtual void OnPostTick(float dt)`
<sub>`game/FluidContainer.cs:100`</sub>

- **in** — `dt` seconds, the same value passed to `Tick`.
- **used at** `game/FluidNet.cs:286` — `foreach (var c in allC) c.OnPostTick(dt);`

#### `public void Pickup()`
<sub>`game/FluidContainer.cs:195`</sub>

- **used at** `game/PlayerController.cs:1219` — `c.Pickup(); // frees its hoses + (a pump) its power wire, then despawns`

#### `public void ToggleValve()`
<sub>`game/FluidContainer.cs:210`</sub>

- **used at** `game/PlayerController.cs:3533` — `_fHeldFluid.ToggleValve();`

#### `public void SetValveOpen(bool open)`
<sub>`game/FluidContainer.cs:222`</sub>

- **used at** `game/FluidValve.cs:33` — `if (_onTrigger != null && GodotObject.IsInstanceValid(_onTrigger) && _onTrigger.Live >= 1f) SetValveOpen(true);`

#### `public string RoleLabel()`
<sub>`game/FluidContainer.cs:233`</sub>

- **out** — never null/empty.
- **used at** `game/HosePort.cs:85` — `string name = Owner != null ? Owner.RoleLabel() : "Fluid";`

### `game/FluidDeploy.cs`

#### `public static Node3D SpawnFor(DeployableDef def, Node parent, Vector3 pos, float yawDeg)`
<sub>`game/FluidDeploy.cs:12`</sub>

- **in** — `def` **must** have `def.Fluid != null`; `parent` non-null.
- **out** — the spawned node, or **`null` when `def?.Fluid == null` or `parent == null`**.
- **used at** `game/PlayerController.cs:1845` — `FluidDeploy.SpawnFor(_deployable, GetParent(), _placePoint, _placeYaw);`

### `game/FluidFuelInlet.cs`

#### `public static FluidFuelInlet Make(Deployable gen)`
<sub>`game/FluidFuelInlet.cs:14`</sub>

- **in** — `gen` the generator whose `Fuel` it feeds; null is tolerated (the post-tick no-ops).
- **used at** `game/Deployable.cs:235` — `d.AddChild(FluidFuelInlet.Make(d));`

#### `public override void OnPostTick(float dt)`
<sub>`game/FluidFuelInlet.cs:46`</sub>

- **out** — `void`; no-op below 0.001 mL or with a dead generator.
- **used at** `game/FluidNet.cs:286` — `foreach (var c in allC) c.OnPostTick(dt);`

### `game/FluidItem.cs`

#### `public static Item ActiveAutoDrink(SDG.Unturned.PlayerInventory inv)`
<sub>`game/FluidItem.cs:20`</sub>

- **in** — `inv` may be null.
- **out** — the item, or **`null`** for none / null inventory.

#### `public static void Read(Item it, ItemAsset a, out FluidType type, out float amount, out WaterQuality q)`
<sub>`game/FluidItem.cs:58`</sub>

- **out** — `type/amount/q`; `amount` is `max(0, …)`; a null item yields `(None, 0, Clean)`.
- **used at** `game/FluidItem.cs:81` — `Read(it, a, out var type, out var amount, out var q);`

#### `public static float Fill(Item held, ItemAsset a, FluidTank from, out string msg)`
<sub>`game/FluidItem.cs:90`</sub>

- **in** — `held`/`a` a fluid container; `from` the source tank.
- **out** — mL moved; **0 with `msg` set** on: not a container, tank empty/`None`, container full (≤0.01 mL space), or a type mismatch ("won't mix X and Y"). `msg` is `null` on success.

#### `public static float Sip(Item held, ItemAsset a, out float hydration, out string msg)`
<sub>`game/FluidItem.cs:111`</sub>

- **out** — mL drunk; `hydration` = mL × `HydrationPerML` (0..1 vital scale). **0 with `msg`** on: not a container, empty (≤0.01 mL), or undrinkable ("can't drink dirty water").

#### `public static float DrinkAll(Item held, ItemAsset a, out float hydration, out string msg)`
<sub>`game/FluidItem.cs:131`</sub>

- **out** — mL drunk; same failure shape as `Sip`.

### `game/FluidNet.cs`

#### `public class FluidPortNode { FluidPortKind Kind; float Rate; FluidContainer Owner; float Flow; bool Flowing; float Load; float SolveRate; }`
<sub>`game/FluidNet.cs:9`</sub>


#### `public static FluidType ResolveNetType(SceneTree tree, HosePort p, HashSet<FluidContainer> seen)`
<sub>`game/FluidNet.cs:31`</sub>

- **in** — `tree` live; `p` may be null; `seen` **must be a fresh set per top-level call** (it is the cycle guard and is mutated).
- **out** — the resolved `FluidType`; **`FluidType.None` on: null port, a non-relay untyped device, a cycle re-entry, or an unconnected fitting.**
- **used at** `game/PlayerController.cs:710` — `var st = FluidNet.ResolveNetType(GetTree(), start, new System.Collections.Generic.HashSet<FluidContainer>());`

#### `public static WaterQuality ResolveWaterQuality(SceneTree tree, HosePort p, HashSet<FluidContainer> seen)`
<sub>`game/FluidNet.cs:50`</sub>

- **in** — same contract as above (`seen` fresh per call).
- **out** — `WaterQuality`; **`Clean` on every failure/unknown path** (null port, non-relay, cycle).
- **used at** `game/FluidNet.cs:272` — `if (far != null) c.Tank.Contaminate(ResolveWaterQuality(tree, far, new System.Collections.Generic.HashSet<FluidContainer>()));`

#### `public static bool WouldNeedPump(SceneTree tree, HosePort srcPort, HosePort consPort)`
<sub>`game/FluidNet.cs:73`</sub>

- **in** — `tree`; `srcPort` the source-side port, `consPort` the consumer-side port (caller must order them — `FluidHoseRule.IsSourceSide` does this).
- **out** — `true` = needs a pump. **Returns `false` when either port or owner is null** (fails "open"), and **`true`** when either owner is missing from the device group (fails "needs a pump"). Recomputed per aimed frame — O(devices + hoses²).
- **used at** `game/PlayerController.cs:761` — `needsPump = FluidNet.WouldNeedPump(GetTree(), sp, cp);`

#### `public static void Tick(SceneTree tree, float dt)`
<sub>`game/FluidNet.cs:130`</sub>

- **in** — `tree` live; `dt` seconds — **`dt <= 0` returns immediately** (the guard also protects the `1/dt` clamp).
- **out** — `void`; all effects are mutations on the tank/port objects.
- **used at** `game/FluidNet.cs:295` — `public override void _Process(double delta) => FluidNet.Tick(GetTree(), (float)delta);`

#### `public partial class FluidManager : Node`
<sub>`game/FluidNet.cs:292`</sub>


### `game/FluidPump.cs`

#### `public void SetHasWork(bool w)`
<sub>`game/FluidPump.cs:27`</sub>

- **in** — `w`.
- **out** — `void`.
- **used at** `game/FluidNet.cs:230` — `pump.SetHasWork(supply && demand);`

### `game/FluidTank.cs`

#### `public static bool IsBeverage(FluidType id)`
<sub>`game/FluidTank.cs:51`</sub>

- **used at** `game/FluidTank.cs:64` — `public static bool Safe(FluidType id, WaterQuality q) => (id == FluidType.Water && q == WaterQuality.Clean) || IsBeverage(id);`

#### `public static bool TryParse(string s, out FluidType type)`
<sub>`game/FluidTank.cs:67`</sub>

- **in** — `s` may be null (treated as "").
- **out** — false + `type = None` on an unknown name.

#### `public FluidTank(FluidType type, float capacity, float amount = -1f, WaterQuality quality = WaterQuality.Clean)`
<sub>`game/FluidTank.cs:107`</sub>

- **in** — `capacity` mL; **`amount = -1` (the default) is the sentinel for "start full"**; any other value is clamped to `[0, capacity]`.
- **used at** `game/FluidDeploy.cs:18` — `FluidRole.Storage => FluidContainer.Make(FluidRole.Storage, new FluidTank(def.FluidType, def.FluidCapacity, 0f), def.FluidRate), // starts e`

#### `public void Contaminate(WaterQuality q)`
<sub>`game/FluidTank.cs:114`</sub>

- **used at** `game/FluidNet.cs:272` — `if (far != null) c.Tank.Contaminate(ResolveWaterQuality(tree, far, new System.Collections.Generic.HashSet<FluidContainer>()));`

### `game/GasPump.cs`

#### `public const float Watts = 750f`
<sub>`game/GasPump.cs:13`</sub>


#### `public static readonly Vector3 PortLocal = new Vector3(0.45f, -0.3f, 0.25f)`
<sub>`game/GasPump.cs:16`</sub>


#### `public float FillPercent`
<sub>`game/GasPump.cs:30`</sub>

- **used at** `game/DeployableReplicaView.cs:72` — `pump.FillPercent = e.Fuel; // the replicated 0..100 percent of the shared station tank`

#### `public static GasPump Attach(Node parent, Vector3 pos, Basis basis, Vector3 portLocal, Mesh pumpMesh = null, int stationId = -1)`
<sub>`game/GasPump.cs:46`</sub>

- **in** — `stationId` — **`-1` is the sentinel for "derive from position"** via `StationFuel.StationIdFor(pos)`; any value ≥ 0 is used verbatim. `pumpMesh` null = no outline.
- **out** — the live node, `NetId` 0.
- **used at** `game/WorldBuilder.cs:857` — `var gp = GasPump.Attach(root, f.Pos, f.Basis, GasPump.PortLocal, null, f.StationId);`

#### `public static GasPump Materialize(Node parent, Vector3 pos, float yawDegrees, uint netId)`
<sub>`game/GasPump.cs:74`</sub>

- **out** — the node; the fuel bar rides `FillPercent`, extraction is server-routed.
- **used at** `game/DeployableReplicaView.cs:69` — `pump = GasPump.Materialize(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees, e.NetIdValue);`

#### `public void AddInteractionCollider()`
<sub>`game/GasPump.cs:98`</sub>


#### `public void SetLookFocused(bool on)`
<sub>`game/GasPump.cs:111`</sub>

- **used at** `game/PlayerController.cs:289` — `_focusGasPump?.SetLookFocused(true);`

### `game/GasStationServer.cs`

#### `public void RegisterPump(DeployableReplication.DeployableEntity pump, int stationId, DeployableReplication deployables, long tick)`
<sub>`game/GasStationServer.cs:24`</sub>

- **in** — `pump` the live entity; `stationId` **must be the same value `StationFuel.StationIdFor` produced** for that position; `tick` the server tick.
- **out** — `void`.

#### `public float Percent(int stationId)`
<sub>`game/GasStationServer.cs:36`</sub>

- **out** — **0 for an unknown station id** (indistinguishable from an empty one).

#### `IFuelStation`
<sub>`game/GasStationServer.cs:40`</sub>

public bool TryGetStation(uint pumpNetId, out int stationId)`, `public float Remaining(int stationId)`, `public float Capacity(int stationId)`, `public float Drain(int stationId, float requested)`, `public IReadOnlyList<uint> Pumps(int stationId)


### `game/GridPowerSource.cs`

#### `public const float DefaultWatts = 10000f`
<sub>`game/GridPowerSource.cs:18`</sub>


#### `public static readonly Vector3 PortLocal = new Vector3(0.32f, 0.18f, 0.933f)`
<sub>`game/GridPowerSource.cs:37`</sub>


#### `public bool? NetProducingOverride { get; set; }`
<sub>`game/GridPowerSource.cs:53`</sub>

- **in** — `null` / `true` / `false`.
- **used at** `game/DeployableReplicaView.cs:57` — `grid.NetProducingOverride = e.ToggledOn;`

#### `public static GridPowerSource Attach(Node parent, Vector3 pos, Basis basis, Vector3 portLocal, float watts = DefaultWatts, string gridName = "", Mesh boxMesh = null)`
<sub>`game/GridPowerSource.cs:77`</sub>

- **in** — `pos` world metres; `basis` the **already-stood-up** placement basis (from `WorldBuilder.PlaceObject`); `portLocal` in that basis's local frame; `watts` > 0; `gridName` editor label; `boxMesh` null = no outline glow.
- **out** — the live node (already in the tree). `NetId` stays 0 — the direct/local path.
- **used at** `game/WorldBuilder.cs:850` — `var g = GridPowerSource.Attach(root, f.Pos, f.Basis, GridPowerSource.PortLocal);`

#### `public static GridPowerSource Materialize(Node parent, Vector3 pos, float yawDegrees, float watts, uint netId)`
<sub>`game/GridPowerSource.cs:108`</sub>

- **in** — `pos` quantized world metres; `yawDegrees`; `watts` from the replicated def's port; `netId` ≠ 0.
- **out** — the node; `NetProducingOverride` starts `false` (mains off) until the view sets it from `entity.ToggledOn`.
- **used at** `game/DeployableReplicaView.cs:54` — `grid = GridPowerSource.Materialize(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees, watts, e.NetIdValue);`

#### `public void AddInteractionCollider()`
<sub>`game/GridPowerSource.cs:134`</sub>

- **out** — `void`; safe to call once (a second call orphans the first body).

#### `public void SetLookFocused(bool on)`
<sub>`game/GridPowerSource.cs:145`</sub>

- **used at** `game/PlayerController.cs:295` — `_focusGrid?.SetLookFocused(true);`

### `game/HUD.cs`

#### `public partial class HUD : CanvasLayer`
<sub>`game/HUD.cs:12`</sub>

Setting `Player = null` hides the whole on-foot HUD (`_playerOnly` list) so the class doubles as a vehicle-only overlay.


### `game/HitmarkerHUD.cs`

#### `public static HitmarkerHUD Instance`
<sub>`game/HitmarkerHUD.cs:11`</sub>

Global singleton (nulled in `_ExitTree`), `crit = true` draws the red headshot marker. `:12` `const float HitTime = 0.33f` = source `PlayerUI.HIT_TIME`.

- **used at** `game/ClientWorldSession.cs:185` — `HitmarkerHUD.Instance?.Show(e.Headshot); // the hitmarker now only ever tells the server's truth`

### `game/HosePort.cs`

#### `public const uint PortLayer = 1u << 11`
<sub>`game/HosePort.cs:13`</sub>


#### `public FluidType EffectiveType { get; }`
<sub>`game/HosePort.cs:23`</sub>

- **used at** `game/Main.cs:2666` — `bool typedPorts = refinery.PortNodes[0].EffectiveType == FluidType.Oil && refinery.PortNodes[1].EffectiveType == FluidType.Gas;`

#### `public static HosePort Create(FluidContainer owner, FluidPortNode node, Vector3 localPos)`
<sub>`game/HosePort.cs:37`</sub>

- **in** — `owner` non-null; `node` the data port the solver drives; `localPos` metres in the owner's Y-up local frame — a port at exactly (0, y, 0) falls back to `Vector3.Forward` for the arrow.
- **out** — the node; never null.
- **used at** `game/FluidContainer.cs:141` — `var fp = HosePort.Create(this, node, local);`

#### `public void SetArrowState(bool show, bool available)`
<sub>`game/HosePort.cs:73`</sub>

- **used at** `game/PlayerController.cs:951` — `p.SetArrowState(true, p.Usable && !PortHosed(p));`

#### `public string InfoLine(bool hosed = false)`
<sub>`game/HosePort.cs:83`</sub>

- **in** — `hosed` — pass true when a hose is attached; an **out node with no hose and no flow shows only its name** (no numbers).
- **out** — never null; degrades to `"Fluid"` when `Owner` is null.
- **used at** `game/PlayerController.cs:785` — `HoseHudSet(_hosePort == null ? null : _hosePort.InfoLine(IsInstanceValid(_hosePort) && PortHosed(_hosePort)) + hint);`

#### `public void Deactivate()`
<sub>`game/HosePort.cs:139`</sub>


### `game/InfoBillboard.cs`

#### `public static readonly Color HealthColor = new Color(0xbf/255f, 0x1f/255f, 0x1f/255f)`
<sub>`game/InfoBillboard.cs:12`</sub>


#### `public void SetActive(bool on)`
<sub>`game/InfoBillboard.cs:78`</sub>

- **in** — `on`.
- **out** — `void`; **silently no-ops before `_Ready` has run** (`_sprite == null`), so calling it from a constructor-adjacent path does nothing.
- **used at** `game/GridPowerSource.cs:151` — `_info?.SetActive(on);`

#### `public void SetName(string text, Color color)`
<sub>`game/InfoBillboard.cs:85`</sub>

- **in** — `text` — assigned verbatim, **`null` becomes an empty label** (unlike `SetPrompt` there is no `?? ""`, but Godot tolerates a null `Text` assignment as empty).
- **out** — `void`; no-ops before `_Ready`.
- **used at** `game/GasPump.cs:124` — `_info.SetName("Gas Pump", PumpColor);`

#### `public void SetPrompt(string text, Color color)`
<sub>`game/InfoBillboard.cs:86`</sub>

- **out** — `void`; no-ops before `_Ready`.
- **used at** `game/GasPump.cs:130` — `_info.SetPrompt($"{FillPercent:0}% station fuel · {rstate}", PumpColor);`

#### `public void SetBar(int i, float value, Color color, bool visible = true)`
<sub>`game/InfoBillboard.cs:89`</sub>

- **in** — **`i` must be 0, 1 or 2** — `0 = health` (health icon), `1 = fuel` (fuel icon), `2 = battery` (stamina icon). **The icon per row is fixed at build time**, so callers reusing row 1 for a wind-level bar or row 2 for a generator load bar get the fuel/stamina icon regardless of the colour they pass. `value` is a 0..1 fract…
- **out** — `void`; **silently returns for `i < 0`, `i > 2`, or before `_Ready`.**
- **used at** `game/Deployable.cs:695` — `_info.SetBar(1, Mathf.Clamp(_windFactor, 0f, 1f), InfoBillboard.LoadColor, true); // WIND-level bar (cyan) in the fuel slot (master)`

### `game/LoadingScreen.cs`

#### `public static string NextMode`
<sub>`game/LoadingScreen.cs:16`</sub>

Mode selector consumed once in `_Ready` (`"launch"` = two bars for asset warmup, anything else = one bar for a level load), then nulled. Env fallback `UG_LOADMODE`.

- **used at** `game/Main.cs:507` — `LoadingScreen.NextMode = "launch";`

#### `public void SetTotal(int n)`
<sub>`game/LoadingScreen.cs:143`</sub>

Denominator for `Advance()`.

- **in** — clamped to ≥1, so `SetTotal(0)` is safe.
- **used at** `game/WorldBuilder.cs:129` — `if (mode != WorldMode.Dedicated) { loading = new LoadingScreen(); root.AddChild(loading); loading.SetTotal(mode == WorldMode.Client ? 5 : 11…`

#### `public void SetStatus(string s)`
<sub>`game/LoadingScreen.cs:145`</sub>

Sets the phase text — routed to the **bottom** bar in launch mode (prefixed `"Loading "`, underscores→spaces) or the **only** bar in map mode (verbatim).

- **in** — null `s` throws on the launch path (`s.Replace`) — pass `""`.
- **used at** `game/WorldBuilder.cs:146` — `curPhase = name; loading?.SetStatus(name + "…"); phaseSw.Restart();`

#### `public void Advance()`
<sub>`game/LoadingScreen.cs:151`</sub>

Increments done and repaints the overall bar + percent. No clamp on overshoot beyond the fill's own `Clamp(f,0,1)`.

- **used at** `game/Warmup.cs:85` — `_ls?.Advance();`

#### `public void SetStage(string name, int x, int n)`
<sub>`game/LoadingScreen.cs:160`</sub>

Launch mode only (silent no-op in map mode): names the current stage on the top bar with an `(x / n)` counter and fills `x/n`.

- **in** — `n <= 0` → fill 0 rather than a divide.
- **used at** `game/Warmup.cs:81` — `_ls?.SetStage(e.Stage, e.X, e.N);`

#### `public void Finish(Dictionary<string,double> timings)`
<sub>`game/LoadingScreen.cs:168`</sub>

Hides the overlay, prints and shows a per-phase ms/% breakdown for 8 s, then self-`QueueFree`s.

- **in** — `timings` phase→milliseconds; an empty dict yields `LOAD 0 ms` and a 0% guard.
- **used at** `game/WorldBuilder.cs:755` — `loading?.Finish(timings); // hide the overlay + show the per-category timing breakdown top-left for a few seconds (master)`

### `game/MainMenu.cs`

#### `public void ShowTab(int tab)`
<sub>`game/MainMenu.cs:342`</sub>

Jump the camera-glide target to an anchor index; clamps to `[0, Anchors.Length-1]` and marks the cinematic intro as already-played for any non-zero tab.

- **in** — 0 Title / 1 Play / 2 Survivors / 3 Configuration / 4 Workshop.
- **used at** `game/Main.cs:3951` — `if (_menuShotIdx < switchAt.Length && _frame == switchAt[_menuShotIdx]) _menuShotMenu.ShowTab(_menuShotIdx);`

### `game/MainMenuAdvanced.cs`

#### `static readonly (string cat, (string n, char t)[] fields)[] AdvancedSchema`
<sub>`game/MainMenuAdvanced.cs:17`</sub>

The full vanilla `ModeConfigData` schema (10 categories, ~230 fields) with per-field type codes `'b'`/`'f'`/`'i'`. Data, not code — the one place the vanilla gameplay-config surface is enumerated. Private.


### `game/MainMenuPlay.cs`

#### `Texture2D LoadTex(string file, int maxSize = 0)`
<sub>`game/MainMenuPlay.cs:55`</sub>

Loads `content/menu/<file>` and optionally Lanczos-downscales so the longest side ≤ `maxSize`.

- **out** — `null` if missing or `img.Load != Ok`. `maxSize = 0` = no scaling.
- **used at** `game/MainMenuPlay.cs:193` — `var icon = key != "" ? LoadTex($"mapicon_{key}.png", 36) : null;`

#### `Control OptionRow(string label, string[] values, int initial, System.Action<int> onChange)`
<sub>`game/MainMenuPlay.cs:209`</sub>

The retail `SleekButtonState` `< value >` cycler and a labeled `CheckButton`. Genuinely generic settings-row builders, private to `MainMenu`.

- **used at** `game/MainMenuPlay.cs:153` — `right.AddChild(OptionRow("Zombies", ZombieModes, _optZombies, i => _optZombies = i)); // REAL`

### `game/MainMenuServers.cs`

#### `public sealed record ServerEntry(string Name, string Host, ushort Port, string Map, int Max, bool Pvp, bool Locked)`
<sub>`game/MainMenuServers.cs:15`</sub>

Public helper record for a browsable server.


#### `static (bool ok, int ping, int players, int max, ulong version) StatusQuery(string host, ushort port, uint nonce)`
<sub>`game/MainMenuServers.cs:244`</sub>

Blocking UDP A2S-substitute on a worker thread: sends 24-byte `UGSQ`+nonce (padded so request ≥ response, no amplification), waits 1500 ms for `UGSR`+echoed nonce+players(u16)+max(u16)+version(u64 LE); ping = measured RTT ms.

- **out** — `(false,0,0,0,0)` on timeout, DNS failure, magic mismatch, or nonce mismatch. Private, though it's the only server-probe implementation in the repo.

### `game/MapUI.cs`

#### `public PlayerController Player`
<sub>`game/MapUI.cs:14`</sub>

LevelSize` encodes PEI = MEDIUM (`Level.size 2048 − 2×border 64`) — the map projection's only tunable.

- **used at** `game/WorldBuilder.cs:824` — `root.AddChild(new MapUI { Player = player }); // M: full-screen PEI map (town nodes + player pos/facing)`

#### `static Vector2 WorldToNorm(Vector3 p)`
<sub>`game/MapUI.cs:107`</sub>

nx = X/1920 + 0.5, ny = 0.5 + Z/1920` — the source's `ProjectWorldPositionToMap` with the Godot Z-flip already folded in.

- **out** — normalized [0,1]² (unclamped — off-map positions go negative/over 1).
- **used at** `game/Main.cs:1279` — `// Per pixel -> world X/Z (inverse of MapUI.WorldToNorm, levelSize 1920) -> sample the live wind -> thermal tint.`

### `game/MeleeDef.cs`

#### `public static MeleeDef Fists => new() { ... }`
<sub>`game/MeleeDef.cs:23`</sub>


#### `public static MeleeDef FromDatText(string name, string datText)`
<sub>`game/MeleeDef.cs:30`</sub>

- **in** — ** `name` becomes `Name`; `datText` full .dat contents. Defaults when a key is missing: Range 2.2 m, Zombie/Player 45, Vehicle 10, Structure 5, Resource 5, Stamina 0 (**dat scale 0–100**, callers divide by 100 into the 0..1 bar), Strong 0.5, Strength **1.5×**, Alert 0 m (= silent). `Repeated` / `Repair` are presence fl…
- **out** — ** always a non-null `MeleeDef`; a garbage/empty `datText` yields all-defaults, not an error.
- **used at** `game/PlayerController.cs:1354` — `_melee = System.IO.File.Exists(p) ? MeleeDef.FromDatText(meleeName, System.IO.File.ReadAllText(p)) : new MeleeDef { Name = meleeName };`

### `game/PlayerController.cs`

#### `public static bool SurvivalDrain = false`
<sub>`game/PlayerController.cs:61`</sub>


#### `public void Infect(float amount)`
<sub>`game/PlayerController.cs:63`</sub>

- **in** — ** `amount` in 0..1 virus units (callers divide dat 0–100 by 100). **Output:** void.
- **used at** `game/ZombieController.cs:445` — `player.Infect((AttackDamage * mult / 3f) / 100f); // Zombie.askDamage: askInfect(b/3)`

#### `public void Consume(ItemAsset a, int quality = 100)`
<sub>`game/PlayerController.cs:69`</sub>

- **in** — ** `a` null → no-op. `quality` 0–100 condition byte; food/water use 0–100 dat units divided by 100 into the 0..1 bars; `useHealth` is raw HP.
- **out** — ** void; clamped at `MaxHealth` / 1.0 / 0.0.
- **used at** `game/inventory/InventoryUI.cs:916` — `Player?.Consume(jar.GetAsset(), jar.item?.quality ?? 100); // pass the eaten instance's CONDITION -> moldy-food penalty (vitals stay client-…`

#### `public Vector3 LookPoint()`
<sub>`game/PlayerController.cs:91`</sub>

- **in** — ** none. Implicit: `_cam`, and the private `const float LookReach = 2.6f` (line 176) — **the orb reaches only 2.6 m**, not "arbitrary distance". Mask `(1<<0)|(1<<5)|(1<<6)` = world + vehicles + props. Self-RID excluded.
- **out** — ** world-space Vector3. **Failure paths:** no camera → `GlobalPosition - basis.Z * 3f` (a 3 m stub in front of the *body*, not the eye); no hit → `camPos + fwd * 2.6f`. Never returns a sentinel/NaN.
- **used at** `game/DevConsole.cs:228` — `Vector3 at = Player?.LookPoint() ?? Vector3.Zero;`

#### `public void TeleportTo(Vector3 pos)`
<sub>`game/PlayerController.cs:104`</sub>

- **in** — ** world m. **Output:** void.
- **used at** `game/ClientWorldSession.cs:329` — `Shell.TeleportTo(new Vector3(rc.Pos.x, rc.Pos.y, rc.Pos.z));`

#### `public float MapFacingAngle()`
<sub>`game/PlayerController.cs:117`</sub>

- **in** — ** none. **Output:** radians, `(-π, π]`. **Failure:** no camera → falls back to the body basis; never throws.
- **used at** `game/MapUI.cs:87` — `_arrow.Rotation = Player.MapFacingAngle();`

#### `public void DropWorldItem(Item item, Vector3 pos)`
<sub>`game/PlayerController.cs:123`</sub>

- **in** — ** `item` — must be non-null (dereferenced by `WorldItem.Spawn`); `pos` — world metres, cast runs from `pos + Up` down 2048 m against layer 0 only.
- **out** — ** void. **Empty path:** if the downward ray misses (item dropped over a void), `pos` is used verbatim with no +0.25 m lift, so the collider can start buried.
- **used at** `game/CropManager.cs:67` — `by.DropWorldItem(new Item(yield), at);`

#### `public ITowNode PickTowNode(Vector3 from, Vector3 fwd, out bool rear)`
<sub>`game/PlayerController.cs:542`</sub>

- **in** — ** `from` = ray origin world m; `fwd` **must be normalised** (used as `Dot` projection axis and as `from + fwd*t`); consts: `RopeReach = 6f` m forward, `RopePickRadius = 0.7f` m perpendicular. Nodes behind the ray (`t < 0`) are rejected.
- **out** — ** best `ITowNode`, `rear=true` if the rear hitch won. **Empty path:** `null` + `rear=false` when nothing is inside 6 m / 0.7 m.
- **used at** `game/testing/tests/VehicleTowTests.cs:370` — `var picked = sess.Shell.PickTowNode(from, fwd, out bool rear);`

#### `public static AudioStreamWav LoadWavOneShot(string resPath, bool loop = false)`
<sub>`game/PlayerController.cs:1969`</sub>

- **in** — ** `resPath` is globalized through `ProjectSettings.GlobalizePath`. **16-bit PCM only.**
- **out** — ** `AudioStreamWav`, or **`null`** for: file missing, <44 bytes, no `data` chunk, or `bits != 16`.
- **used at** `game/Deployable.cs:226` — `var eng = PlayerController.LoadWavOneShot("res://content/sounds/generator_engine.wav", loop: true);`

#### `public void MeleeAttack(bool strong = false)`
<sub>`game/PlayerController.cs:1994`</sub>

- **in** — ** `strong` — RMB. Stamina cost `= MeleeDef.Stamina / 100` (dat is 0–100 → 0–1 normalised bar); refuses if `Stamina < cost`. Cooldown fallback when the clip is missing/<0.05 s: 0.75 s strong / 0.45 s weak. `IsRepeatedMelee` (blowtorch/chainsaw) → immediate no-op.
- **out** — ** void; silent no-op is the failure path (cooldown, dead, driving, holding a consumable, inventory open, no camera).
- **used at** `game/testing/tests/CombatTests.cs:26` — `if (i > 5) p.MeleeAttack();`

#### `public void Explode(Vector3 point, float radius, float zombieDamage, float playerDamage, float vehicleDamage)`
<sub>`game/PlayerController.cs:2092`</sub>

- **in** — ** `point` world m; `radius` m (>0; ≤0 makes `DamageSphere` return 0 and every distance check fail); damages in HP. `Kills` is incremented in place.
- **out** — ** void. **Empty path:** nothing in radius / everything walled → no damage, but the flinch still fires for all players.
- **used at** `game/Grenade.cs:44` — `if (IsInstanceValid(Thrower)) Thrower.Explode(GlobalPosition, Radius, ZombieDamage, PlayerDamage, VehicleDamage);`

#### `public void FlinchFromExplosion(Vector3 point, float radius, float magnitudeDegrees)`
<sub>`game/PlayerController.cs:2138`</sub>

- **in** — ** `point`/`radius` m; `magnitudeDegrees` — real `Bomb_*` EffectAsset values 2–45. **Sentinels:** `dist <= 0` or `dist >= radius` → early return (no shake). `|deg| <= 0.01` or non-finite axis → skipped rather than poisoning `_flinch`.
- **out** — ** void. **Failure:** no camera → return.
- **used at** `game/PlayerRegistry.cs:40` — `if (GodotObject.IsInstanceValid(p)) p.FlinchFromExplosion(point, radius, magnitudeDegrees);`

#### `public void ThrowGrenade()`
<sub>`game/PlayerController.cs:2153`</sub>

- **in** — ** none. 1.0 s cooldown; no inventory consumption yet.
- **out** — ** void; cooldown-refused is silent.

#### `public float GetStealthDetectionRadius()`
<sub>`game/PlayerController.cs:2433`</sub>

- **in** — ** none. Table (in `core/UnturnedSim/CombatMath.cs:43`): stand 12, crouch 6, prone 3, sprint 20 m, ×1.1 moving, driving `48 * fwdSpeed%`, clamped `[1, 64]`.
- **out** — ** metres, always in `[1,64]`. No failure return (parked car → ~1 m, effectively silent).
- **used at** `game/testing/tests/PlayerTests.cs:22` — `float r = p.GetStealthDetectionRadius();`

#### `public void NetRecovRestore(UnityEngine.Vector3 simVelocity) => _move.Velocity = simVelocity`
<sub>`game/PlayerController.cs:2473`</sub>

- **used at** `game/ClientWorldSession.cs:330` — `Shell.NetRecovRestore(rc.Vel);`

#### `public void NetHoldPose(EPlayerStance stance, bool moving)`
<sub>`game/PlayerController.cs:2483`</sub>

- **in** — ** `stance` — `EPlayerStance` enum, drives both hit volume and detection radius; `moving` bool.
- **out** — ** void.
- **used at** `game/PlayerNetSync.cs:86` — `t.Body.NetHoldPose(stance, moved);`

#### `public void LinkWorldLighting(DirectionalLight3D sun, Godot.Environment env)`
<sub>`game/PlayerController.cs:2953`</sub>

- **in** — ** both may be null (viewmodel then renders fullbright).
- **out** — ** void; no failure return. Must be called or the FP gun ignores time-of-day.
- **used at** `game/Main.cs:1078` — `player.LinkWorldLighting(sun, env); // FP gun takes the world's day/night lighting`

#### `public void TakeDamage(float amount, Vector3? fromPos = null)`
<sub>`game/PlayerController.cs:3005`</sub>

- **in** — ** `amount` HP (>1 marks bleeding, >5 flashes pain); `fromPos` = attacker world position, **used only for the flinch axis** — pass `null` for starvation/infection/sourceless.
- **out** — ** void. **No-damage paths:** any of the four server-owned/dead guards.
- **used at** `game/ZombieController.cs:444` — `player.TakeDamage(AttackDamage * mult, GlobalPosition); // pass my position so the hurt-flinch kicks away from me`

#### `public static void PopulateDemoKit(PlayerInventory inv)`
<sub>`game/PlayerController.cs:3656`</sub>

- **in** — ** `inv` must be non-null and already sized. **Output:** void; silently drops items that don't fit.
- **used at** `game/DedicatedServer.cs:90` — `PlayerController.PopulateDemoKit(inv.Inventory);`

#### `public bool Fire()`
<sub>`game/PlayerController.cs:3769`</sub>

- **in** — ** none; reads `Gun` (null → 34 zombie dmg / 40 vehicle / 25 object / 0.1 s cd / 500 m·s⁻¹ / 20 steps / 4× gravity / 1 pellet), `Skills.SharpshooterRecoilMultiplier()` (≤1.0, −40 % at max), `_viewmodel.AimAlpha` (0 hip … 1 ADS).
- **out** — ** `true` = a shot left the barrel (hits land later in `StepBullets`); `false` = any gate refused. In MP the local bullets are **cosmetic** (`Cosmetic = NetFire != null`).
- **used at** `game/Main.cs:3983` — `if (_fireTest && _ftPlayer != null) { _ftFrame++; if (System.Environment.GetEnvironmentVariable("UG_ADS") == "1") { if (_ftFrame >= 40) _ftP…`

#### `public void DebugFireBullet(Vector3 from, Vector3 dir, float damage = 40f)`
<sub>`game/PlayerController.cs:3862`</sub>

- **in** — ** `dir` is normalised internally (any non-zero length works). `damage` HP.
- **out** — ** void.
- **used at** `game/testing/tests/DoorBedDeadzoneTests.cs:331` — `p.DebugFireBullet(new Vector3(0f, 0.2f, 0f), new Vector3(0f, 0f, -1f), 40f);`

#### `public enum Surf`
<sub>`game/PlayerController.cs:3988`</sub>

public static Color SurfDust(Surf s)

- **used at** `game/Terrain.cs:271` — `if (body == null) { body = new StaticBody3D { CollisionLayer = 1u << 0 }; body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf…`

#### `public void RenderImpactFx(Vector3 point, bool flesh)`
<sub>`game/PlayerController.cs:4215`</sub>

- **in** — ** `point` world m (the server's authoritative impact point); `flesh` — true routes to `SpawnFleshImpact`, direction derived cam→point.
- **out** — ** void. **Failure path:** probe miss (e.g. a collider-less vehicle puppet) → soft `Surf.Dirt` up-facing burst with **no decal**. Degenerate direction (<1e-4 length²) → `Vector3.Forward`.
- **used at** `game/ClientWorldSession.cs:191` — `Shell.RenderImpactFx(new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.Surface == (byte)ImpactSurface.Flesh);`

### `game/PowerNet.cs`

#### `public static void MarkDirty()`
<sub>`game/PowerNet.cs:16`</sub>

- **in** — none.
- **out** — `void`; no failure path.
- **used at** `game/PlayerController.cs:674` — `PowerNet.MarkDirty(); // a new wire changes the graph`

#### `public static bool ToggleGlobalPower()`
<sub>`game/PowerNet.cs:25`</sub>

- **out** — new flag value.
- **used at** `game/testing/tests/PowerTests.cs:464` — `bool nowOn = PowerNet.ToggleGlobalPower(); PowerNet.Recompute(Tree);`

#### `public static void SetGlobalPower(bool on)`
<sub>`game/PowerNet.cs:26`</sub>

- **in** — `on` — true = mains live.
- **out** — `void`.
- **used at** `game/DevConsole.cs:112` — `PowerNet.SetGlobalPower(on); // flips the flag + MarkDirty()s so the graph recomputes (Circuit_0 sources turn on/off)`

#### `public static void ResetForTests()`
<sub>`game/PowerNet.cs:28`</sub>

- **out** — `void`. **Semantic hazard**: leaves `GlobalPower == false` even though the field's production default is `true`.
- **used at** `game/testing/TestHost.cs:117` — `PowerNet.ResetForTests();`

#### `public static void RecomputeIfDirty(SceneTree tree)`
<sub>`game/PowerNet.cs:30`</sub>

- **in** — `tree` — must be non-null and the live scene tree (it reads groups off it); a null tree throws.
- **out** — `void`; on the idle path it returns before touching anything.
- **used at** `game/PowerNet.cs:102` — `PowerNet.RecomputeIfDirty(GetTree());`

#### `public static void Recompute(SceneTree tree)`
<sub>`game/PowerNet.cs:38`</sub>

- **in** — `tree` non-null. Ports that are null or freed (`!IsInstanceValid`) are skipped; wires whose `Source`/`Consumer` node is freed are skipped.
- **out** — `void`; with no devices it's a no-op solve (all ports left at their reset values).
- **used at** `game/DevConsole.cs:381` — `gen.TogglePower(); PowerNet.Recompute(GetTree());`

#### `public static void PrewarmWireArrows(SceneTree tree, bool show)`
<sub>`game/PowerNet.cs:78`</sub>

- **in** — `tree`; `show` — true = arrows on for this frame.
- **out** — `void`.
- **used at** `game/PowerNet.cs:103` — `if (_prewarm > 0) { PowerNet.PrewarmWireArrows(GetTree(), _prewarm > 1); _prewarm--; }`

#### `public partial class PowerManager : Node`
<sub>`game/PowerNet.cs:93`</sub>


### `game/RiggedCharacter.cs`

#### `public float ClipLength(string name)`
<sub>`game/RiggedCharacter.cs:169`</sub>

- **used at** `game/PlayerController.cs:4337` — `_body.PlayLoop(_body.ClipLength("Idle_Drive") > 0f ? "Idle_Drive" : "Idle_Sit"); // seated DRIVING pose (hands on the wheel) instead of a st…`

#### `public void SetLocomotion(float speed)`
<sub>`game/RiggedCharacter.cs:184`</sub>

- **in** — ** `speed` in **m/s, horizontal**. **Output:** void. **No-op paths:** no player, a one-shot is still running (`_oneShot > 0`), or the target clip doesn't exist.
- **used at** `game/ZombieDirector.cs:292` — `v.Rig.SetLocomotion(_sim.StateOf(row) == ZombieState.Pursue ? _sim.Kinds[_sim.KindOf(row)].MoveSpeed : 0f);`

#### `public void UsePhysicsAnimRate()`
<sub>`game/RiggedCharacter.cs:237`</sub>

- **used at** `game/ZombieController.cs:126` — `_rig.UsePhysicsAnimRate(); // perf: shamble the skeleton at 50 Hz, not the render rate (a POI of zombie rigs at 280fps was the CPU spike)`

#### `public void SetupAimAdditive(string clip = "Gun_Aim")`
<sub>`game/RiggedCharacter.cs:248`</sub>

- **in** — ** `clip` — the gun's own `{Gun}_Aim` where present, else the rifle-tuned generic (a single generic delta pitched pistols up in ADS). **Output:** void; no-op if no player/skeleton or the clip is absent. Blend amount is the public field `AimBlend` (`:147`, 0..1).
- **used at** `game/Viewmodel.cs:243` — `_arms.SetupAimAdditive(_arms.ClipLength(capGun + "_Aim") > 0f ? capGun + "_Aim" : "Gun_Aim");`

#### `public static RiggedCharacter Build(string resPath, Color tint, bool armsOnly = false, string albedoTexPath = null, string faceTexPath = null)`
<sub>`game/RiggedCharacter.cs:424`</sub>

- **in** — ** `resPath` a Godot `res://` path; `tint` skin/base colour; `armsOnly=true` builds the viewmodel arms (and auto-runs `SetupAimAdditive()`, `:602`); texture paths optional.
- **out** — ** the node, or **`null`** if the file can't be opened (`GD.PrintErr("[rig] cannot open ...")`).
- **used at** `game/ZombieController.cs:123` — `_rig = RiggedCharacter.Build("res://content/rig.json", _tint, false, atlas, "res://content/face_19.png");`

#### `public static RiggedCharacter BuildFrom(RigData rig, Color tint, bool armsOnly = false, string albedoTexPath = null, string faceTexPath = null)`
<sub>`game/RiggedCharacter.cs:438`</sub>


### `game/StationFuel.cs`

#### `public const float StationCapacity = 20_000_000f`
<sub>`game/StationFuel.cs:11`</sub>


#### `public static FluidTank Tank(int stationId)`
<sub>`game/StationFuel.cs:14`</sub>

- **in** — any int id (negative ids included — no validation).
- **out** — never null; silently allocates on a miss.
- **used at** `game/GasPump.cs:21` — `public FluidTank Fluid => StationFuel.Tank(StationId); // the shared station tank -- drained by extraction, never respawns (DIRECT SP only)`

#### `public static int StationIdFor(Vector3 pos)`
<sub>`game/StationFuel.cs:21`</sub>

- **in** — `pos` world metres (Y ignored).
- **out** — a stable id; collisions across distant cells are possible but unhandled.
- **used at** `game/WorldBuilder.cs:313` — `result.Fixtures.Add(new FixtureRecord { DefId = DeployableDef.GasPump.Id, Pos = gpos, YawDegrees = 180f - ey, Basis = basis, StationId = Sta…`

#### `public static void Reset()`
<sub>`game/StationFuel.cs:27`</sub>

- **used at** `game/WorldBuilder.cs:390` — `StationFuel.Reset(); // fresh shared station tanks for this world build (before any gas pumps attach)`

### `game/StreetLight.cs`

#### `public const float Watts = 200f`
<sub>`game/StreetLight.cs:13`</sub>


#### `public static Color KelvinToColor(float kelvin)`
<sub>`game/StreetLight.cs:23`</sub>

- **in** — `kelvin` **clamped to [1000, 12000]**.
- **out** — an sRGB colour; each channel clamped 0..1.
- **used at** `game/StreetLight.cs:62` — `var col = KelvinToColor(ColorTempK);`

#### `public static StreetLight Make(Vector3 lampWorldPos, float reach)`
<sub>`game/StreetLight.cs:35`</sub>

- **in** — `lampWorldPos` world metres (the lamp head); `reach` metres, **floored at 4**.
- **out** — the node; nothing is built until `_Ready`.
- **used at** `game/WorldBuilder.cs:342` — `root.AddChild(StreetLight.Make(lampWorld, System.Math.Max(4f, lampWorld.Y - gpos.Y)));`

### `game/Vehicle.cs`

#### `public static CpuParticles3D MakeSmoke(string texName, Color c, float life, float vel, int amount, bool fire, float sizeMin, float sizeMax)`
<sub>`game/Vehicle.cs:238`</sub>

- **in** — `texName` a filename under `res://content/`; `life` seconds; `vel` m/s (min = 0.6× this); `amount` particle count; `sizeMin/sizeMax` particle diameter in metres.
- **out** — the system with `Emitting = false` — **the caller must turn it on**; a missing texture yields an untextured but functional system.
- **used at** `game/Deployable.cs:216` — `d._smoke = Vehicle.MakeSmoke("veh_smoke_1.png", new Color(0.55f, 0.55f, 0.55f), 2.0f, 1.8f, 12, false, 0.8f, 1.6f); // light damage smoke (<…`

#### `public void TakeDamage(float amount)`
<sub>`game/Vehicle.cs:307`</sub>

- **used at** `game/Deployable.cs:468` — `if (d <= R) v.TakeDamage(SDG.Unturned.ExplosionMath.Linear(120f, d, R));`

#### `public static Vehicle BuildByName(string name, int variant = 0)`
<sub>`game/Vehicle.cs:958`</sub>

- **in** — `name`; `variant` = paint index.
- **out** — a live `Vehicle`; **an unknown name silently falls back to the jeep.**
- **used at** `game/ClientWorldSession.cs:385` — `var v = Vehicle.BuildByName(key, e.Variant);`

#### `public static VehiclePuppet BuildPuppetByName(string name, int variant)`
<sub>`game/Vehicle.cs:978`</sub>

- **out** — a `VehiclePuppet`; same jeep fallback.
- **used at** `game/VehicleReplicaView.cs:72` — `var pup = Vehicle.BuildPuppetByName(key, e.Variant);`

#### `public void Drive(float throttle, float steer, bool handbrake)`
<sub>`game/Vehicle.cs:1342`</sub>

- **in** — `throttle` and `steer` in **[-1, 1]**; `handbrake`.
- **out** — `void`; a wrecked vehicle zeroes engine/steer/brake and returns.
- **used at** `game/PlayerController.cs:4467` — `_driving.Drive(throttle, steer, handbrake);`

#### `public bool CoupleTo(Vehicle trailer)`
<sub>`game/Vehicle.cs:1391`</sub>

- **in** — `trailer` must be a real trailer, both ends uncoupled, and `FifthWheelWorld` within `CoupleReach` of `KingpinWorld`.
- **out** — `false` on any of those gates.
- **used at** `game/PlayerController.cs:4383` — `if (n is Vehicle cab && cab.CanTow && cab.CoupledTrailer == null && cab.CoupleTo(trailer)) return true; // a cab backed under -> couple`

#### `public void Uncouple()`
<sub>`game/Vehicle.cs:1417`</sub>

- **out** — `void`; no-op when not coupled.
- **used at** `game/PlayerController.cs:4381` — `if (trailer.CoupledCab != null) { trailer.Uncouple(); return true; } // already hitched -> disconnect`

#### `public void SetTowedFreeRoll(bool on)`
<sub>`game/Vehicle.cs:1472`</sub>

- **out** — `void`; no-op before the wheels exist, and a second `on` call is ignored.
- **used at** `game/Vehicle.cs:1500` — `towed.SetTowedFreeRoll(true); // the towed car free-rolls so the rope can drag it (else its grippy wheels resist the pull)`

#### `public bool AttachTow(Vehicle towed)`
<sub>`game/Vehicle.cs:1491`</sub>

- **in** — `towed` non-null, not self, neither wrecked, neither end already roped, not already joined by the semi hitch, gap ≤ `TowAttachReach`.
- **out** — `false` on any gate.
- **used at** `game/VehicleNetSync.cs:84` — `// already roped, and the requester is within reach of both. AttachTow does the FINAL physics gate`

#### `public void DetachTow()`
<sub>`game/Vehicle.cs:1510`</sub>

- **out** — `void`; no-op when not roped.

#### `public void SetTowGhost(bool ghost)`
<sub>`game/Vehicle.cs:1564`</sub>

- **used at** `game/Vehicle.cs:1408` — `AddCollisionExceptionWith(trailer); trailer.SetTowGhost(true); // ghost the cab<->trailer pair ONLY: the exception makes the two BODIES igno…`

#### `public float ForwardSpeedPct()`
<sub>`game/Vehicle.cs:1638`</sub>

- **out** — **0 when `_speedMax <= 0`** (a towed body).
- **used at** `game/PlayerController.cs:2435` — `if (IsDriving) return StealthDetection.DrivingRadius(_driving.ForwardSpeedPct()); // source DRIVING: DETECT_FORWARD(48) * fwd-speed% -> loud…`

#### `public void Honk()`
<sub>`game/Vehicle.cs:1645`</sub>

- **used at** `game/PlayerController.cs:3444` — `if (_driving != null) _driving.Honk(); // LMB while driving: horn`

#### `public void DriveTrailerLights(bool running, bool braking)`
<sub>`game/Vehicle.cs:1685`</sub>

- **used at** `game/Vehicle.cs:1609` — `trailer.DriveTrailerLights(EngineOn && Battery > 0f, _braking); // pass the cab's running + brake state through to the trailer's brake light…`

#### `public void SetLookFocused(bool on)`
<sub>`game/Vehicle.cs:1704`</sub>

- **used at** `game/PlayerController.cs:264` — `_focusVehicle?.SetLookFocused(true);`

#### `public Aabb WorldMeshAabb()`
<sub>`game/Vehicle.cs:1732`</sub>

- **out** — a world `Aabb`; falls back to a ±1 m box when the vehicle has no meshes.
- **used at** `game/PlayerController.cs:4478` — `size = _driving.WorldMeshAabb().Size.Length(); // bounding diagonal -> bigger vehicle, further back`

#### `public bool LookRayHitsHull(Vector3 from, Vector3 to)`
<sub>`game/Vehicle.cs:1783`</sub>

- **in** — `from`/`to` world metres.
- **out** — `true` on the first hull crossed.
- **used at** `game/PlayerController.cs:249` — `if (d < maxD && d < bestV && vv.LookRayHitsHull(from, _lookEnd)) { bestV = d; hitVeh = vv; } // cheap distance gate before the tight per-hul…`

#### `public IEnumerable<(Transform3D xf, Vector3 size)> LookHullBoxes()`
<sub>`game/Vehicle.cs:1797`</sub>

- **out** — lazily enumerated; empty when the vehicle has no box shapes.
- **used at** `game/PlayerController.cs:340` — `foreach (var (xf, size) in v.LookHullBoxes())`

#### `public void SetSalvagePrompt(string line2, Color color)`
<sub>`game/Vehicle.cs:1809`</sub>

- **used at** `game/PlayerController.cs:1264` — `else if (!HasBlowtorch) { v.SetSalvagePrompt("Requires blowtorch to salvage", red); _salvageTimer = 0f; }`

### `game/VehiclePuppet.cs`

#### `public sealed class WheelDress { Node3D Pivot; bool Steer; float Radius; float Spin; }`
<sub>`game/VehiclePuppet.cs:12`</sub>


#### `public void SetNameLabel(string name, Color color)`
<sub>`game/VehiclePuppet.cs:81`</sub>

- **in** — `name` — **null/empty renders as `"?"`**.
- **out** — `void`; **calling it twice leaks a second label** (no null check).
- **used at** `game/Vehicle.cs:1048` — `p.SetNameLabel(s.Name, p.OutlineColor); // look-at name tag (hidden until focused), like the real Vehicle's InfoBillboard title`

#### `public float MeshSize { get; }`
<sub>`game/VehiclePuppet.cs:126`</sub>

- **used at** `game/PlayerController.cs:4486` — `void PositionRideCam(Transform3D vt) => PositionVehicleCam(vt, _riding.DriverEyeLocal, _fp ? 0f : _riding.MeshSize);`

#### `public void DressWheels(float steerDegrees, float forwardSpeed, float dt)`
<sub>`game/VehiclePuppet.cs:140`</sub>

- **in** — `steerDegrees` degrees; `forwardSpeed` m/s signed; `dt` seconds. `Radius` is floored at 0.05 m to avoid a divide blow-up.
- **out** — `void`; skips freed pivots; the steer model is skipped when `SteerAxis == Vector3.Zero` (e.g. the trailer).
- **used at** `game/VehicleReplicaView.cs:102` — `t.Node.DressWheels(e.SteerSigned, fwdSpeed, dt);`

### `game/Viewmodel.cs`

#### `public void Kick(Vector3 shakeMin, Vector3 shakeMax, float recoilPitch, float recoilYaw)`
<sub>`game/Viewmodel.cs:386`</sub>

- **in** — ** `shakeMin/Max` in viewmodel-local metres (Eaglefire `Shake_Min/Max_*`, ~±0.0025); min/max are re-ordered internally so a swapped pair is safe. `recoilPitch/recoilYaw` in **degrees**; yaw is negated because horizontal recoil shipped inverted.
- **out** — ** void; both springs decay back to rest in `_Process`.
- **used at** `game/PlayerController.cs:3785` — `_viewmodel?.Kick(new Vector3(Gun.ShakeMinX, Gun.ShakeMinY, Gun.ShakeMinZ),`

#### `public bool TryMuzzleScreenPos(out Vector2 px)`
<sub>`game/Viewmodel.cs:407`</sub>

- **in** — ** none. **Output:** `px` = pixel coords in the viewport rect (SubViewport is sized to the main viewport, so 1 px here = 1 px there). **`false`** when no camera, no muzzle node, or the muzzle is **behind** the camera (unprojecting a behind-point mirrors it across the screen).
- **used at** `game/PlayerController.cs:3852` — `if (!NetAvatar && _viewmodel != null && _cam != null && _viewmodel.TryMuzzleScreenPos(out var _mpx))`

#### `public void SetLocomotion(bool moving, EPlayerStance stance)`
<sub>`game/Viewmodel.cs:417`</sub>

- **used at** `game/PlayerController.cs:4599` — `_viewmodel?.SetLocomotion(moving, _move.Stance);`

#### `public void SwingMelee(bool strong = false)`
<sub>`game/Viewmodel.cs:421`</sub>

- **used at** `game/PlayerController.cs:2005` — `_viewmodel?.SwingMelee(strong); // source Weak / Strong swing anim`

#### `public float MeleeSwingLength(bool strong)`
<sub>`game/Viewmodel.cs:428`</sub>

- **in** — ** `strong` = RMB. **Output:** seconds; **`0f`** when there are no arms or no matching clip — callers must supply their own fallback (`PlayerController` uses 0.75 s / 0.45 s below 0.05 s).
- **used at** `game/PlayerController.cs:2003` — `_meleeCd = _viewmodel?.MeleeSwingLength(strong) ?? 0f;`

#### `public void SetAiming(bool on)`
<sub>`game/Viewmodel.cs:511`</sub>

- **used at** `game/PlayerController.cs:3471` — `else _viewmodel?.SetAiming(rmb.Pressed); // hold RMB to ADS -- GUNS only (a melee weapon has no sights)`

#### `public void SetWorldLights(IReadOnlyList<(Vector3 camLocalPos, Color color, float energy, float range)> lights)`
<sub>`game/Viewmodel.cs:543`</sub>

- **in** — ** `camLocalPos` is the light's position **in the player camera's view space**, already sign-corrected by the caller (the subview cam is 180° about Y, so X and Z are negated at the call site). `range` metres, `energy` Godot light energy.
- **out** — ** void; no-op if no camera. Caller caps at 4 (`MaxMirrorLights`).
- **used at** `game/PlayerController.cs:2993` — `_viewmodel.SetWorldLights(_mirrorLights);`

#### `public bool TryGetSlotScreen(string slot, out Vector2 screen)`
<sub>`game/Viewmodel.cs:666`</sub>

- **in** — ** `slot` ∈ `{"Sight","Tactical","Barrel","Grip","Magazine"}` — an unknown key returns false. **Output:** `screen` px; **`false`** on no gun / no cam / unknown slot / behind camera; `screen` set to `Vector2.Zero`.
- **used at** `game/AttachmentMenu.cs:99` — `if (VM.TryGetSlotScreen(slot, out var screen))`

### `game/WheelDebris.cs`

#### `public partial class WheelDebris : RigidBody3D`
<sub>`game/WheelDebris.cs:8`</sub>


#### `public StandardMaterial3D Mat`
<sub>`game/WheelDebris.cs:10`</sub>

- **used at** `game/Vehicle.cs:388` — `var rb = new WheelDebris { Mass = 18f, Mat = mat, CollisionLayer = 1u << 2, CollisionMask = 1u << 0 }; // debris on its own bit, masks GROUN…`

### `game/Wire.cs`

#### `game/Hose.cs:28`
<sub>`game/Wire.cs:27`</sub>

public void SetPoints(List<Vector3> pts, bool valid)

- **in** — `pts` world-space points, ≥2 for anything to draw; segments shorter than 1e-4 m are hidden; `valid` picks the colour only.
- **out** — `void`; `pts.Count < 2` yields an all-hidden wire.

### `game/ZombieController.cs`

#### `public bool IsHeadshot(Vector3 worldPoint)`
<sub>`game/ZombieController.cs:166`</sub>

- **in** — ** world-space impact point. **Output:** bool; a point below the feet just returns false.
- **used at** `game/PlayerController.cs:3915` — `if (collider is ZombieController z) { bool head = z.IsHeadshot(point); SpawnFleshImpact(point, hdir); bool wd = z.Dead; z.DamageHit(b.Damage…`

#### `public void PuppetFrame(double delta, Vector3 targetPos, float yawDegrees, byte anim)`
<sub>`game/ZombieController.cs:479`</sub>

- **in** — ** `targetPos` world m; `yawDegrees` degrees (pitch/roll forced to 0); `anim` = `ZombieNetAnim` byte (`Dead` → `PuppetDie()`, `Attack` → one-shot on the **edge** only, else locomotion from measured speed). Internal `_puppetAnim` starts at **255** so the first Attack edge still triggers.
- **out** — ** void; early-returns if `!IsPuppet` or already `Dead`.
- **used at** `game/ZombiePuppets.cs:61` — `pup.PuppetFrame(delta, target, e.YawDegrees, e.AnimState);`

#### `public partial class AcidSpit : Node3D`
<sub>`game/ZombieController.cs:687`</sub>


### `game/ZombieDirector.cs`

#### `public void LoadFromPei(string peiRoot)`
<sub>`game/ZombieDirector.cs:86`</sub>

- **in** — ** `peiRoot` = map directory. **Output:** void; zero pockets → prints `[zdirector] no pockets -- no zombies` and leaves `_sim` null (every seam above then fast-outs).
- **used at** `game/WorldBuilder.cs:598` — `zd.LoadFromPei(mapRoot);`

#### `public void DebugBuild(ZombieRegionBounds[] regions, Vector3[] spawns, float moveSpeed = 5.5f)`
<sub>`game/ZombieDirector.cs:121`</sub>


#### `public bool ShootRay(Vector3 origin, Vector3 dir, float maxDistance, float damage, out bool killed)`
<sub>`game/ZombieDirector.cs:321`</sub>

- **in** — ** `dir` must be normalised; `maxDistance` m; `damage` HP. **Output:** `true` if a row was hit + damaged; `killed` reports whether that hit finished it so callers keep their own counters. **`false` + `killed=false`** when `_sim == null` or nothing was hit.
- **used at** `game/PlayerController.cs:2056` — `if (ZombieDirector.Instance is { } zdm && zdm.ShootRay(origin, fwd, range + 0.5f, dmg, out bool zdKilled) && zdKilled) Kills++;`

#### `public int DamageSphere(Vector3 point, float radius, System.Func<float, float> falloff, System.Func<Vector3, bool> blocked = null)`
<sub>`game/ZombieDirector.cs:332`</sub>

- **in** — ** `falloff(distance) -> damage`; `blocked(zombieWorldPos) -> true to skip` (null = no LoS test). `radius` m; **`radius <= 0` returns 0 immediately**.
- **out** — ** number killed; `0` when no sim.
- **used at** `game/PlayerController.cs:2096` — `Kills += zde.DamageSphere(point, radius,`

#### `public bool ShootSegment(Vector3 from, Vector3 dir, float length, float wallDistance, float damage, out Vector3 point, out bool head, out bool killed)`
<sub>`game/ZombieDirector.cs:351`</sub>

- **in** — `** `length` = this tick's segment length m; `wallDistance` = distance to the geometry hit on the same segment (pass `float.MaxValue` for "no wall"). **Output:** `point` = entry point for the impact FX, `head` = `hit.Limb == ZombieLimb.Skull` for the hitmarker, `killed`. All three out-params are zero/false on the `false…
- **used at** `game/PlayerController.cs:3899` — `if (segLen > 1e-5f && zdb.ShootSegment(b.Pos, seg / segLen, segLen, wallDist, b.Damage,`

#### `public string DebugLine()`
<sub>`game/ZombieDirector.cs:375`</sub>

- **out** — ** the string, or `"zdirector: no sim"`.
- **used at** `game/Main.cs:3900` — `GD.Print($"[zdirtest] {zd.DebugLine()}");`

#### `public sealed class GodotLineOfSight : IZombieLineOfSight`
<sub>`game/ZombieDirector.cs:392`</sub>


#### `public sealed class GodotNavQuery : IZombieNavQuery`
<sub>`game/ZombieDirector.cs:417`</sub>


### `game/ZombieNav.cs`

#### `public struct NavPocket`
<sub>`game/ZombieNav.cs:12`</sub>


#### `public static List<NavPocket> LoadPockets(string peiRoot)`
<sub>`game/ZombieNav.cs:32`</sub>

- **out** — ** the list; **empty list** if `Bounds.dat` is missing (prints `[zombienav] no Bounds.dat -- no pockets`).
- **used at** `game/ZombieField.cs:44` — `var navPockets = ZombieNav.LoadPockets(peiRoot);`

#### `public static Rid MapFor(UnityEngine.Vector3 p)`
<sub>`game/ZombieNav.cs:81`</sub>

- **out** — ** the pocket's navigation map `Rid`, or **`default` (invalid Rid)** when none — the caller must check `IsValid`.

#### `public static void BuildOrLoad(Node worldRoot, List<NavPocket> pockets, bool overlay = false, bool save = true, bool bakeIfMissing = true)`
<sub>`game/ZombieNav.cs:93`</sub>

- **in** — ** `overlay` = debug render; `save=false` for verify shots so they don't clobber the canonical bake; `bakeIfMissing=false` = load-only. Agent radius **0.4 m** and cell size **0.2 m** are load-bearing — 0.25 would erode ~1 m doorways shut.
- **out** — ** void; **returns immediately if `pockets.Count == 0`**. Clears and repopulates the static `PocketMaps` (`:78`).
- **used at** `game/WorldBuilder.cs:760` — `try { var _navPk = ZombieNav.LoadPockets(mapRoot); ZombieNav.BuildOrLoad(root, _navPk, overlay: false, save: bakeNav, bakeIfMissing: bakeNav…`

### `game/ZombieNetSync.cs`

#### `public bool DamageZombie(uint zombieNetId, float damage, UnityEngine.Vector3 point, UnityEngine.Vector3 dir, ushort attackerPlayerId, bool headshot)`
<sub>`game/ZombieNetSync.cs:180`</sub>

- **in** — ** `attackerPlayerId` is accepted but **not used here** — `ServerCombat` owns kill credit; this only sets `DeadAnnounced`. `headshot` selects the limb enum for the sim path but is ignored on the node path (the node already resolved the limb).
- **out** — ** `true` only when this hit **killed**. `false` for: unknown NetId, no sim, row not found, row already dead, invalid/dead brain, or a non-fatal hit.
- **used at** `core/UnturnedNet/ServerCombat.cs:401` — `bool killed = ZombieHost.DamageZombie(hitZombie.NetIdValue, dmg, point, dir, b.Shooter, head);`
