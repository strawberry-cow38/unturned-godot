# Map Editor — Retail Parity Gap Analysis

**Ground truth:** the open-sourced Unturned 3 source at
`~/projects/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/` (cited below as `U3:<file>:<line>`).
**Ours:** `unturned-godot/game/Editor*.cs` + subsystem fields (cited as `ours:<file>:<line>`).

Every claim here was read from the actual source, not inferred. Retail editor logic+UI is
~16.5K lines across `Edit/` + `UI/Edit/`; ours is ~2.5K lines. This doc is the map of the delta,
produced by a 6-way source audit (one per subsystem).

## TL;DR parity by subsystem

| Subsystem | Parity | One-line state |
|---|---|---|
| Editor shell (camera / look / mode tabs / save) | **~45–50%** | fly-cam + look + 4-tab switching near-1:1; everything *around* it thin |
| Transform / selection / gizmo / copy-paste / undo | **~55–60%** | strongest area; solid interactive loop, but no redo + shallow gizmo |
| Objects mode | **~45%** | transform core ~70%, surrounding subsystem ~25% |
| Terrain + foliage | **~30%** | Height tab ~55% (works, runtime-mutable); Materials ~20%; Foliage + Tiles 0% |
| Spawns | **~30–35%** | place/preview near-done; the entire spawn-**table authoring** layer absent |
| Roads / Nodes / Nav / Volumes / Environment | **~20–25%** | Roads ~65%; Lighting ~10%; **Nodes / Nav-editor / Volumes = 0%** |

**Whole modes at 0% (entirely unimplemented):** Volumes (21 types), Nodes (airdrop / named-location /
spawnpoint), Navigation *editor*, Foliage *editor*, terrain **Tiles**, spawn-**table authoring**,
the lighting keyframe/color model, the pause menu, the level-visibility system.

---

## Cross-cutting themes (the real story — read these first)

These recurred in **every** subsystem audit and dominate the effort estimate:

1. **THE SCOPE FORK (biggest decision).** Is the editor meant to be a *faithful authoring tool that
   round-trips real Unturned maps* (binary `Level/*.dat` + `.level` v12, 64×64 region bucketing,
   `instanceID`s, placementOrigin, material overrides) — or a *lightweight in-Godot editor* that
   builds/edits maps for **our** port using our own sidecar formats? Every audit hinges on this.
   Today the port writes bespoke text sidecars everywhere (`content/spawns/editor_*.txt`,
   `editor_<map>.txt` for objects) and never touches binary `.dat` — so there is **zero retail-map
   interop** and metadata (instanceID, region bucket, material palette, culling flag) is lost on save.

2. **No redo, anywhere. Undo is a flat closure stack.** `ours:Editor.cs:37-54` is a
   `List<(label, Action undo)>` popped newest-first. Retail has a reified bidirectional command
   system: `IReun{ int step; redo(); undo(); }` (`U3:IReun.cs:9-18`) with per-op Add/Remove/Transform
   records + `step` grouping, plus a `DevkitTransactionManager` delta layer. Terrain/foliage edits
   have **no undo at all**. A real undo/redo backbone is a port-wide decision, not a per-tool one.

3. **Placement/interaction core is solid; deep authoring + surrounding subsystems are thin.**
   You can fly, look, pick, box-select, gizmo-drag, copy/paste, place props/spawns, and sculpt height.
   You cannot: edit the loaded map's objects, author spawn tables, paint blended splats, place volumes
   or nodes, edit lighting colors, or adjust snap/brush options.

4. **Hardcoded values that retail exposes as options:** snap presets (1u/15° hardcoded vs
   ETransformSnapPreset 1/0.5/0.25 + ERotateSnapPreset 15/10/5), brush falloff (`BRUSH_FALLOFF=0.5`),
   FOV (60) + look sensitivity (0.12), material palettes (fixed 8-layer PEI set vs asset-driven).

---

## 1. Editor shell — ~45–50%  (`game/Editor.cs`, `EditorCamera.cs`, `EditorDashboard.cs`)

**Parity (good):** free-fly WASD+E/Q camera-relative, scroll fly-speed (32 default, ×0.2/notch,
0.5..2048 clamp), RMB-gated flight, mouse-look with ±90 pitch clamp, 4-tab mode bar
(Terrain/Environment/Spawns/Level) with exclusive panels — all near-1:1
(`ours:EditorCamera.cs:35-59,40-45`; `U3:EditorMovement.cs:14-61`, `U3:EditorLook.cs:27-48`).

**Gaps:**
- **Pause menu — ABSENT.** Retail ESC → `EditorPauseUI` (Save/Map/Chart/Options/Display/Graphics/
  Controls/Audio/Exit/Quit + spawn-table export) `U3:EditorPauseUI.cs:29-136`. Ours: ESC = deselect only.
- **Level-visibility system — ABSENT.** F1–F9 layer toggles + `EditorLevelVisibilityUI` 10 checkboxes +
  7×7 region density/triangle overlay `U3:EditorInteract.cs:69-121`, `U3:EditorLevelVisibilityUI.cs:289-410`.
- **Screenshot / satellite / chart capture — ABSENT.** `U3:EditorScreenCaptureComponent.cs:19-31`,
  pause Map→`CaptureSatelliteImage` / Chart→`CaptureChartImage` `U3:EditorPauseUI.cs:61-69`.
- **Camera-pose persistence — ABSENT.** Retail loads/saves `Editor/Camera.dat` `U3:EditorInteract.cs:163-191`.
- **Ctrl+S full-level save — PARTIAL.** Only a Dashboard "Save" button fanning to sub-editors
  (`ours:EditorDashboard.cs:44-45`); no Ctrl+S, no `Level.save()` (no metadata/camera/map images).
- **`EditorArea` region/bound tracking — ABSENT.** Per-frame region+nav-bound events that drive the
  visibility overlay + per-viewer lighting `U3:EditorArea.cs:44-89`.
- **Interaction raycast — PARTIAL.** Ours picks a single `EditorPickLayer`; retail casts 3 typed rays
  (world/interact/logic) against distinct `RayMasks` layer sets `U3:EditorInteract.cs:59-62`.
- Minor: sprint FOV boost, HUD-hide key, live asset reload, invert-look, timed hint banner.

## 2. Transform / selection / undo — ~55–60%  (`game/EditorGizmo.cs`, logic in `EditorObjects.cs`)

**Parity (good):** single + shift-multi select, viewport marquee box-select, 3-mode gizmo
(translate/rotate/scale) with per-axis + uniform handles, the exact `ProjectRayOntoRay` drag math,
screen-space constant sizing, local/global toggle, object copy/paste (Ctrl+C/V) and transform
copy/paste (Ctrl+B/N incl. the local=full / global=position-only nuance)
(`ours:EditorGizmo.cs:51-101,127-187`; `U3:TransformHandles.cs`, `U3:SelectionTool.cs`).

**Gaps:**
- **No redo; undo is a flat closure stack** (theme #2). No `IReun`/`step`/transaction model.
- **Gizmo shallow:** no hover highlight (retail yellow) / active-drag highlight (white)
  `U3:TransformHandles.cs:839,857`; no planar handles (POSITION_PLANE_*) `U3:TransformHandles.cs:722-737`;
  no bounds-editor modes (PositionBounds/ScaleBounds) `U3:TransformHandles.cs:15-41`; no `viewAxisFlip`.
- **Snap hardcoded** 1u / 15° while Ctrl held (`ours:EditorGizmo.cs:171,177`) vs configurable presets.
- **Multi-select pivots on most-recent object, not selection centroid** (`ours:EditorObjects.cs:37`
  vs `U3:SelectionTool.cs:502-519`) — rotating a group spins around the wrong point.
- Meta-props (crate/shelf/grid/pump, no `guid`) silently excluded from copy/paste + delete-undo
  (`ours:EditorObjects.cs:416,440`).

## 3. Objects mode — ~45%  (`game/EditorObjects.cs`, `EditorObjectBrowser.cs`)

**Parity (good):** place-at-surface (E), select, marquee, gizmo, copy/paste, delete, per-session undo.

**Gaps (3 load-bearing):**
- **Only session-placed props are editable — the loaded map is untouchable.** `_pickToObj` is populated
  only in `Place*()` (`ours:EditorObjects.cs:169,345`); loaded `WorldBuilder` objects are never
  registered. Retail selects ALL `LevelObjects.objects[x,y]`+`buildables` `U3:EditorObjects.cs:711-762`.
  **Single biggest fidelity gap.**
- **No material-palette / variant system.** Retail: per-instance `MaterialPaletteOverride` GUID +
  index, random variant seeded by instanceID `U3:LevelObject.cs:361-404`, live-applied
  `U3:EditorLevelObjectsUI.cs:329-354`. Ours binds one fixed albedo per mesh.
- **Saves to bespoke `.txt`, not binary `Objects.dat` v12** (`ours:EditorObjects.cs:540-561`) — loses
  instanceID/placementOrigin/material/culling, no region buckets, no retail interop.
- Category filters (Large/Medium/Small/Barricade/Structure/NPC) ABSENT → barricades/structures/NPCs/
  **decals not placeable at all** `U3:EditorLevelObjectsUI.cs:118-206`. No bounds mode, no focus key,
  no hover name/origin readout, no culling-volume toggle, name-substring search only.

## 4. Terrain + foliage — ~30%  (`game/EditorTerrain.cs`, `EditorTerrainPanel.cs`, `Terrain.cs`)

**Note:** terrain height + splat ARE runtime-mutable (live `_grid` + chunk rebuild,
`ours:Terrain.cs:115-229`) — the "static baked terrain" worry is false for height/splat. Foliage IS
baked/static/uneditable (`ours:FoliageField.cs:11-30`).

**Height tab ~55%:** raise/lower/smooth/ramp/flatten present; missing falloff control, per-mode
strength, flatten-**target** + Min/Max methods, smooth-method toggle, per-vertex preview, undo.
**Materials tab ~20%:** `PaintSplat` hard-sets ONE layer at weight 1.0 — no weighted blend/strength/
falloff/erase (`ours:Terrain.cs:94-111`) vs retail's blended PAINT + AUTO(slope)/SMOOTH/CUT(holes)
`U3:TerrainEditor.cs:1281-1620`; fixed 8-layer palette vs `LandscapeMaterialAsset` picker + eyedropper;
**splat paint not saved at all** (lost on reload).
**Foliage editor 0%** (paint/exact/bake `U3:FoliageEditor.cs:83-581`) — baking is offline via
`tools/foliage_all.py`. **Tiles tab 0%** — port fuses tiles into one merged grid; can't grow/shrink
map footprint or edit per-tile layers `U3:TerrainEditor.cs:522-578`.

## 5. Spawns — ~30–35%  (`game/EditorSpawns.cs`, `EditorSpawnsPanel.cs`)

**Parity (good):** place at cursor, remove-by-radius, add/remove toggle, per-category markers,
category switching. **Players ~100%** (place + rotation + `isAlt` alt-spawn + radius).

**Gaps:** the **entire spawn-table authoring layer is absent** for items/vehicles/animals/zombies —
no table CRUD, no rename/ID/color edit, no **tiers**, no per-tier **weights**, no asset **rosters**
(`U3:EditorSpawnsItemsUI.cs:337-432` and siblings). A spawn point is inert without a table, so this is
~80% of retail's spawns surface. **Zombies is the deepest hole:** no table selection *at all* + no
mega/difficulty/health/damage/loot/xp/regen/4-clothing-slots/uniqueId `U3:EditorSpawnsZombiesUI.cs:128-616`.
Persists to text sidecar, not binary `.dat`, and skips retail's 64×64 region bucketing.

## 6. Roads / Nodes / Nav / Volumes / Environment — ~20–25%

**Roads ~65% (strongest):** create/add-vertex/move/remove/tangent(MIRROR/ALIGNED/FREE)/loop/offset/
ignore-terrain all present (`ours:RoadField.cs`), live re-extrude. Gaps: material width/height/depth/
offset/isConcrete are loaded but not **editable** `U3:EditorEnvironmentRoadsUI.cs:159-182`; no modern
`RoadAsset` mode; keyboard-only, no GUI panel.
**Environment/Lighting ~10%:** time-of-day + overcast only. Missing azimuth, bias, fade, seaLevel,
snowLevel, moon phase, rain/snow freq/dur, weather asset — and the **entire 4-keyframe × 12-color ×
5-slider `LevelLighting` model** (`U3:EditorEnvironmentLightingUI.cs:209-300`).
**Nodes = 0%:** airdrop-marker / named-location / spawnpoint devkit systems all absent
`U3:EditorEnvironmentNodesUI.cs:157-191`.
**Navigation editor = 0%:** we *consume* PEI nav (parse Bounds/Flags + bake Recast, `ours:ZombieNav.cs`)
but can't author/move/resize/config flags or rebake in-editor `U3:EditorNavigation.cs`.
**Volumes = 0%:** all **21 volume types** absent (Water, Deadzone, Teleporter×2, Ambiance, Effect,
Safezone, HordePurchase, ArenaCompactor, Cartography, Culling, Foliage, Kill, LandscapeHole,
NPCOverlap, NPCReward, NavClip, NoStructures, Oxygen, PlayerClip, UndergroundWhitelist)
`U3:EditorVolumesUI.cs:128`. Note retail folded the ex-node roles (deadzone/safezone/purchase/arena/
effect) into Volumes, so this is the whole "trigger region" feature space.

---

## Decisions that gate implementation (for strawberry)

Ranked. Each materially changes the size/shape of the work.

1. **Scope fork (theme #1) — the master decision.** Faithful round-trip authoring of real Unturned
   maps (binary `.dat`/`.level`, region buckets, instanceIDs — big lift, enables retail interop) **or**
   a functional in-Godot editor using our own formats (much smaller, no interop)? Everything else
   inherits from this.
2. **Undo/redo (theme #2).** Build a real bidirectional command stack port-wide (retail `IReun`-style,
   or adopt Godot's `UndoRedo`), or accept undo-only? Gates terrain/foliage/nodes/volumes editing too.
3. **Which whole-missing modes are actually in scope for PEI?** Volumes (21 types — but PEI may need
   only a handful), Nodes, Navigation-editor, Foliage-editor, Tiles, spawn-table authoring, the
   lighting keyframe model. Pick the target set rather than "all 21 volumes + everything."
4. **Depth vs breadth on existing modes.** e.g. objects: make the loaded map editable + material
   palette (deep) before adding barricades/structures/decals (breadth)? Terrain: weighted splat blend
   + brush options before Foliage/Tiles?

**Recommended first slice (my read, pending #1):** if the goal is a *usable PEI authoring tool for our
port* (not retail interop), the highest-leverage first tranche is (a) real undo/redo backbone,
(b) make loaded-map objects selectable/editable, (c) weighted splat painting + brush falloff/strength,
(d) spawn-table authoring for items+zombies. That turns it from "place-and-preview" into "actually
edit a map" without the binary-format rabbit hole. But this is decision #1's call, not mine.
