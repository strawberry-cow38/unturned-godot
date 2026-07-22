# Map Editor Port — Implementation Plan

Companion to `EDITOR_PARITY_GAPS.md` (the source-cited gap audit). This is the execution plan
after strawberry's scope decisions (2026-07-22).

## Locked decisions (strawberry)

1. **Our editor keeps its own format + paradigm.** No faithful binary `.dat`/`.level` round-trip.
2. **One-way converter:** retail U3 maps → our format. **No exporter back** to retail format.
3. **Undo only.** No redo, no bidirectional command stack. Keep the existing `Editor._history`
   closure stack (`game/Editor.cs:37-54`); just extend coverage to new tools.
4. **Port everything** — every mode, in our paradigm.

**Converter insight (important):** because the editor loads the real PEI map at runtime
(WorldBuilder parses retail `.dat`) and then *saves in our format*, the one-way converter is
largely an emergent property of the editor itself: **load a retail map → Save → an our-format map.**
So the converter isn't a separate tool to build first — it falls out as each mode gains a
load-real + save-ours path. The remaining explicit converter work is a headless "load map, save all,
exit" batch entrypoint once enough modes round-trip.

## Build order (fidelity-first)

1. **Loaded map editable + editor shell** ← current
2. Objects mode full (material palette/variants, barricades/structures/NPCs/decals, bounds handles)
3. Terrain full (weighted splat blend + brush options, foliage editor, tiles)
4. Spawns full (table authoring: tables/tiers/weights/rosters + the whole zombie table model)
5. The 0% modes (lighting keyframes/colors, nodes, navigation editor, volumes — all 21 types)
6. Roads + gizmo polish

## Testing discipline (every phase)

- Headless editor render/verify per `feedback_verify_visual_result` (xvfb + vulkan movie-mode,
  NOT `--headless`, per `reference_unturned_godot_render`).
- Run `./test.sh` (L0/L1/L2) — **any change to WorldBuilder or the shared world-build path must not
  regress non-editor (server/client/SP) builds.** That's the main risk in Phase 1.
- `dotnet build` before any runtime launch (`reference_ug_mono_no_autorebuild` — the runtime does NOT
  recompile C#). Commit onto the feature branch, no fix-branches (`feedback_no_fix_branches`).
- `git checkout -- .` any committed bin/obj first (`reference_ug_tracks_build_artifacts`).

---

## Phase 1 detailed design

### 1a. Make the loaded map editable (the crux)

**Problem (verified in source):** `WorldBuilder.PlaceObject` (`game/WorldBuilder.cs:266-342`) builds
each loaded object as **loose siblings** under `root`: a `MeshInstance3D` (`mainMi`), an optional
foliage `MeshInstance3D` (`folMi`), and a separate `StaticBody3D` collider — none wrapped in a
per-object node, collider on layer `bit0` (large/LOS) or `bit6|bit8` (small props), **no guid meta
retained**. Our editor (`game/EditorObjects.cs`) only registers props it places *this session*:
`_pickToObj` (collider RID → root) is populated only inside `Place*()` (`:169,188,…`), and the pick
raycast hits `EditorPickLayer` (`bit7`) only (`:345`). Loaded objects therefore can't be selected,
moved, or deleted.

**Design (editor-mode-only — must not touch server/client/SP structure):**
- Add a `WorldMode.Editor`-gated branch in `PlaceObject` (or a post-build wrap pass) that, **only in
  editor mode**, wraps each object's `mainMi`+`folMi`+collider under a per-object `Node3D` root
  carrying the same meta `Place()` writes: `obj_name`, `guid` (= `p[0]`), and enough to re-save
  (position/euler/scale are recoverable from the live transform via the existing
  `DecomposeEuler`, so meta only needs name+guid). Give the collider the `EditorPickLayer` bit
  **in addition to** its gameplay layer (so the existing pick ray finds it and player/sim collision
  is unchanged), and expose a `world-build → (collider RID → root)` registry (e.g. on `WorldResult`).
- In `EditorObjects` init, ingest that registry: add each wrapped loaded root to `_placed` +
  `_pickToObj`. From there **select / gizmo / markers / delete / copy / undo / Save all work
  unchanged** — they already operate on `_placed`/`_pickToObj`/Node3D-with-meshinstance-child[0].
- `Save()` (`game/EditorObjects.cs:540`) then writes the **unified** set (loaded + session-placed) =
  a complete our-format placements file → this is the object half of the one-way converter, and it
  makes loaded-object *moves and deletes* persist (a deleted loaded object simply isn't in `_placed`
  at save, so it's absent from the output — clean, no tombstones needed since we own the format).
- **Guard rails:** the wrap/registry path is `if (mode == WorldMode.Editor)` only; server/client/SP
  keep the current flat, cache-shared, perf-tuned structure byte-for-byte. Foliage `MultiMesh`,
  destructible binding, fixtures (gas/grid), holiday deferral all stay on the flat path — verify the
  wrap doesn't disturb `destBody`/`FixtureRecord`/`destField.Register` wiring (they run pre-wrap).

**Transform subtlety (the tricky bit — get it right):** WorldBuilder bakes the FULL world transform
`new Transform3D(basis, gpos)` — where `basis = rot.Scaled(scale)` (includes scale) — into each of
`mainMi`/`folMi`/`body` individually (`WorldBuilder.cs:281,292,324`). Our gizmo/save read the
*wrapper's* `GlobalTransform` (like `Place()`, where the root carries the transform and the
meshinstance child sits at local identity — `EditorObjects.cs:158-161`). So the wrap must: set
`wrap.Transform = new Transform3D(basis, gpos)` and re-parent `mainMi`/`folMi`/`body` under it at
**local identity** (not their baked world transforms — else the transform double-applies). Do the
wrap **at the END of `PlaceObject`, after** the destructible (`destField.Register`, uses `destBody`
+ the `mis` meshinstances) and fixture (`FixtureRecord`) wiring, so those still bind the same node
refs — only the final parenting changes, editor-mode only. Verify Save's `DecomposeEuler` +
`basis.Scale` round-trips a loaded object (its baked `basis` already matches the `FromEuler`×scale
form Save decomposes, so a loaded-then-saved object should reproduce its own placement line).

**Verify:** headless editor render of PEI → click an existing building → move/rotate/delete it →
Save → reload → edit persisted. Then `./test.sh` L0/L1/L2 green (no non-editor regression).

### 1b. Editor shell

- **Message/help system** (`EEditorMessage` `U3:EEditorMessage.cs`): a timed centered hint banner per
  active tool. Additive UI, low risk.
- **Pause menu** (`EditorPauseUI`): ESC → Save / Options / Exit(confirm) / Quit(confirm). Wire the
  existing `PauseMenu.cs` (currently player-path only, `Main.cs:1074`) into the editor, or a slim
  editor-specific menu. Add **Ctrl+S** → `Editor.Save()` (`EditorInteract.cs:64-67` parity).
- **Level-visibility system** (`EditorLevelVisibilityUI` + F1–F9, `U3:EditorInteract.cs:69-121`):
  per-layer toggles (roads/nav/nodes/items/players/zombies/vehicles/border/animals). Depends on the
  spawn/road/node markers existing (roads yes; nodes come in Phase 5) — ship the toggles that have
  targets now, add the rest as those modes land.

Phase 1a is the foundation and the highest-fidelity win; do it first and fully tested before 1b.
