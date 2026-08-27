# Main-menu diorama extraction pipeline

Reconstructs the retail Unturned main-menu background as the port's `UG_MENUREAL` diorama:
`game/content/menu/{mesh/*.obj, tex/*.png, menu_scene.json, menu_lamps.json}`, placed by
`MainMenu.LoadMenuScene` / `LoadMenuLamps`.

Committed 2026-08-27 in response to the audit finding that the pipeline + its source lived only
in a scratchpad, making `menu_scene.json` an unreproducible artifact.

## Source of record
AssetRipper export of the retail game, on the 4080 box:
`C:\claude-workspace\ripped\unturned-up\ExportedProject\Assets\Game\Sources\MainMenu\` (and
`...\Scenes\Menu.unity`). This is NOT a standard install path (it is the AssetRipper output) and
may not persist, so the three scene files this pipeline reads are committed here verbatim:
- `Menu_Base.unity`      — the always-on diorama (501 GameObjects)
- `Menu.unity`           — the harness (camera rig, named anchor Transforms, the Hero)
- `Menu_NoHoliday.unity` — the no-holiday additive overlay (RenderSettings + a flag)

Intermediate box-derived indices are committed too so steps 4-9 run without the box:
`menu_guid_index.txt` (guid→file), `menu_mat_tex.txt` (material→texture guid).

## Pipeline (order)
1. `parse_menu.py Menu_Base.unity`      → `menu_objects.json`  per-object world transform (Unity→Godot F*M)
2. `build_index.ps1` (on box)           → `menu_guid_index.txt` mesh/material/texture guid → file
3. `get_mats.ps1` (on box)              → `menu_mat_tex.txt`   material → _MainTex guid
4. `plan_scene.py`                      → `placements.json`    drop gizmos, THEN origin-dedup to LOD0
5. `pull_meshes.ps1` + `mesh_decode.py` → `content/menu/mesh/*.obj` decode Unity mesh bytes (raw-Unity)
6. `pull_tex.ps1`                       → `content/menu/tex/*.png`
7. `emit_scene.py`                      → `content/menu/menu_scene.json` placements + axes + tex (keyed by material NAME)
8. `extract_lamps.py`                   → `content/menu/menu_lamps.json` the 6 Light docs
9. `extract_cameras.py Menu.unity`      → the RealViews anchor list (hand-pasted into MainMenu.cs)

## KNOWN LIMITATIONS — flagged by the 2026-08-27 5-agent audit, NOT yet fixed
- **dedup is origin-keyed** — collapses co-located distinct objects; 501→87 is lossy, no test.
- **Hero not placed** — `Menu.unity`'s skinned survivor (the diorama's subject, 22-bone rig, idle anim,
  8 equipment hooks) is absent → the Survivors camera frames empty shelves.
- **skybox is invented** — `MainMenu.cs` builds a `ProceduralSkyMaterial`; retail ships
  `Skybox_MainMenu.mat` (night: black sky + aurora + moon + stars, orange sun). Visible through the doorway.
- **Menu_NoHoliday objects not placed** — its flag (+ christmas/halloween/pride/anniversary siblings and a
  server promo override, via `MenuMapVisibility`) are walked but not ported.
- **camera anchors come from `Menu.unity`** — retail's `MenuOverridableObjects.Awake` overrides them with
  `Menu_Base`'s OWN camera refs; and `initialCamera` (the 6th pose, blends into Title) is missing.
- **glide not source-exact** — Slerp (retail nlerp/`Quaternion.Lerp`), `Basis.LookingAt` zeroes roll
  (Workshop has 1.9°), `_reachedTitle=true` at load kills the slow first pan.
- **loader not hardened** — no try/catch, one conflated `missing-mesh` counter, a failed texture caches grey
  UNDER that texture's name, `m_IsActive`/`m_Enabled` ignored, lamps shadowless, non-spot light → omni.
