# Clothing + Playermodel System — Singleplayer Port Plan

Port Unturned's clothing/playermodel system into the Godot port, **singleplayer only** (no MP/wire).
Source of truth: `~/projects/U3-SDK/Assets/Runtime/Assembly-CSharp/`. Every mechanism below is quoted from
the real source; verified against the port's ripped rig (`game/content/rig.json`, 464 verts, 17 bones).

## How Unturned's SP clothing actually works

**7 slots**, each an item id/GUID + quality byte + state bytes: shirt, pants, hat, backpack, vest, mask,
glasses. Asset hierarchy: `ItemAsset → ItemClothingAsset → (ItemBagAsset | ItemGearAsset) → the 7 types`.

- `ItemClothingAsset` (base): `armor`, `explosionArmor`, `fallingDamageMultiplier`, `proofWater/Fire/Radiation`,
  `preventsFallingBrokenBones`, `movementSpeedMultiplier`, `hairVisible`/`beardVisible`, `visibleOnRagdoll`.
- `ItemBagAsset` (shirt/pants/backpack/vest): `+ width/height` (storage grid).
- `ItemGearAsset` (hat/mask/glasses): `+ hairOverride/beardOverride`.
- **Shirt** = **textures** (`_shirt` albedo + emission + metallic) painted on the body, **plus** an optional
  whole-body **mesh override** (`characterMeshOverride1p/3pLODs`, `characterMaterialOverride`) for
  astronaut/dress-type garments. **Pants** = textures only. **Hat/Backpack/Vest/Mask/Glasses** = **mesh
  prefabs** attached to a bone.

**The key insight:** the character body is **one skinned mesh with one baked UV atlas**. Shirt + pants are
**textures whose alpha gates where they paint** (torso/arms vs hips/legs); skin color is the base. This is
the entire "modularity" for the 993-item common case — no per-garment geometry. Assembled by
`HumanClothes.apply()` through `Shader.Find("Standard/Clothes")` (`StandardClothes.shader`):
`Albedo = lerp(lerp(lerp(skin, face, faceA), shirt, shirtA), pants, pantsA)`. Attachments
(hat→skull bone, vest/backpack→spine bone) are `Instantiate(prefab, bone)`. Hair/beard are prefabs on the
skull, hidden by an AND-fold of every worn piece's `hairVisible`/`beardVisible`.

`PlayerClothing` owns three `HumanClothes`: `firstClothes` (1P arms, shirt only), `thirdClothes` (3P body,
all slots — **the one SP needs**), `characterClothes` (menu paperdoll). Local equip = set the slot + call
`thirdClothes.apply()`; `UpdateStatModifiers()` products the movement/falling multipliers + ORs the
bone-break prevention. Save = v7 `/Player/Clothing.dat` (7×[guid+quality] + state arrays).

## What the port already has (a lot)

- `core/UnturnedSim/PlayerInventory.cs` — **already models the 7 worn slots + armor aggregation** (tested,
  `ArmorTests.cs`). The P4 state foundation is basically done.
- `game/inventory/ItemCatalog.cs` — **all 993 clothing items** already registered (Shirt 261, Pants 170,
  Hat 212, Backpack 88, Vest 138, Mask 80, Glasses 44) with id→guid→type→armor.
- `game/RiggedCharacter.cs` — single-surface skinned `Body` on a 17-bone skeleton (`Spine`, `Skull`) with a
  flat material, **and it already bone-attaches a face quad to `Skull`** (proves the attachment pattern).
- `game/GunDef.cs` — the exact `.dat`-port pattern to mirror for the clothing asset types.
- `game/ContentProvider.cs` (GUID→ripped-asset + `ParseObj`), `tools/` UnityPy rip pipeline, `test.sh`
  (L0/L1/L2 xvfb+lavapipe visual goldens).

**Gap:** no clothing *visual* data (mesh/texture refs, mesh-override flags, hair/beard-visible), no ripped
clothing content, no clothes shader/assembly on `RiggedCharacter`, no equip→visual wiring.

**Does the monolithic body need a modular refactor? No.** A single-surface body is *correct* — Unturned
paints shirt+pants as textures on it. The change is additive: swap `Body`'s flat material for a ported
`clothes.gdshader` `ShaderMaterial` + add bone-attach setters. Only whole-body mesh-override shirts touch
mesh structure (deferred).

## Phases (each gated by an xvfb+vulkan movie-mode render — NOT `--headless`)

- **P1 — clothing `.dat` data types.** Port `ItemClothingAsset`/`ItemGearAsset`/`ItemBagAsset` + the 7 slot
  field-sets into a `ClothingDef` (mirror `GunDef.FromDatText`) + a `clothing_content.tsv` id→content
  manifest. Easy, pure data. Verify: L0 parse real `.dat`s, assert fields, prove it fails pre-parse.
- **P2 — rip clothing content.** UnityPy scripts (mirror `extract_gun.py`) against `core.masterbundle`:
  shirt/pants textures (+emission/metallic), gear meshes (hat/mask/vest/backpack/glasses → `.obj` + albedo),
  hair/beard prefabs, face textures. Emit the content manifest. Start with a starter set. Verify: render each
  ripped asset standalone.
- **P3 — the assembly (hard).** Port `StandardClothes.shader` → `game/content/clothes.gdshader`; swap
  `RiggedCharacter.Body`'s material to it with `SetShirt/SetPants/SetFace/SetSkinColor` setters; add
  `AttachHat/Mask/Glasses`(→Skull) / `AttachVest/Backpack`(→Spine) mirroring `HumanClothes.apply()`;
  hair/beard AND-fold. **Keep the existing face-quad decal — do NOT port the shader's face path** (the
  ripped body UV0 has 0 verts in the shader's face cell). Mesh-override shirts = stretch/deferred. Verify
  (the gate): render `_body` with a real ripped shirt+pants + hat/vest through idle+walk; this proves or
  breaks the UV-atlas assumption.
- **P4 — equip/unequip wiring.** A `PlayerClothingController` (SP `PlayerClothing` analog): `Wear*(Item)`
  calls the existing `PlayerInventory.wear*` (state+armor done) + drives the P3 setters/attachers on `_body`.
  DevConsole `wear <id>`. Verify: L1 test + a wear-then-rewear render.
- **P5 — inventory slots + default outfit + save.** Clothing slots in `InventoryUI`; a default spawn outfit;
  optional v7 `Clothing.dat` save/load. Verify: paperdoll + spawn render + save round-trip.
- **P6 — first-person sleeves (optional v1).** Apply the shirt texture to the Viewmodel `_arms` (the
  `isMine` branch). Easy given P3.

## Risks / open questions

- **UV-atlas fidelity is the real gate (P3):** shirt/pants texture mapping *should* be pixel-correct (mesh
  UV + retail `Shirt.png` share a source), but only the P3 render proves it. Mitigate: verify a known
  garment (Construction_Top) before scaling.
- **Face:** keep the existing Skull-bone face-quad decal (the shader face path has no verts). Effectively
  already solved.
- **Mesh-override shirts** (astronaut/dresses) need a skinned rip + live `Body.Mesh` rebuild — deferred.
- **Attachment placement** depends on the prefab's baked local transform surviving the rip; may need
  per-item offset tuning.
- **Out of SP scope (dropped):** economy `visual*`/mythic cosmetics, left-hand mirroring, the
  first/third/character triplication (collapse to one body + optional arms).

## P3b-tune (deferred follow-up) — gear attach placement

P3b shipped the gear bone-attach mechanism (hat→Skull, vest/backpack→Spine) and it's structurally correct
(mesh loads via `ContentProvider.ParseObj`, binds to the bone, tracks through animation), BUT the per-item
**placement is not tuned**: gear renders oversized + mis-oriented (a tophat engulfs the head, a vest floats
as a slab off the torso). Root cause: `RiggedCharacter.AttachGear` applies only a position offset — no
rotation, no scale. The tophat `.obj`'s crown axis is Z; the Skull bone's rest basis maps mesh-Z → world-Z,
so the crown points backward/horizontal instead of up. **Fix:** give each gear item an explicit orientation
`Basis` (mirror the face-quad decal's `fq.Basis` in `BuildFrom`) — roughly a +90° rotation carrying the
crown axis to world-up — plus per-slot scale + offset tuning; recommend an `attach_rot` column in
`clothing_content.tsv` + a `Basis` param on the `Attach*` methods. Iterate via the `--wearcloth` render gate
(render → adjust → re-render) per gear item. Until tuned, the default outfit ships **shirt + pants only**
(both render-verified); `wear <hat/vest>` in the dev console shows the untuned placement. Tracked as its own
task; NOT blocking the shirt/pants v1.

## Scope calls for the repo owner (strawberry)

1. All 7 slots at once, or shirt+pants first then the rest?
2. First-person arm clothing (P6) in v1 or skip?
3. Mesh-override (astronaut/dress) shirts — OK to show as base body for now, add later?
4. Curated starter set (~1/slot) to prove the pipe, or bulk-rip all 993 up front?
5. Save/load worn clothing now, or default outfit only until SP saves exist?

**Recommended defaults:** shirt+pants first · skip 1P for v1 · defer mesh-override shirts · curated starter
set · default outfit (no save yet).
