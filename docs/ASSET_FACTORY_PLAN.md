# Asset Factory — plan

A standalone in-game tool for **authoring new game assets by composing meshes and
hand-placing their metadata** (colliders, volumes, hook-points), then exporting a
self-contained "bundle" the game can spawn as a prop, deployable, vehicle, or gun.

**Why:** today new content requires reading the retail Unity bundle and *computing*
every hook/mount/collider by hand (error-prone math + z-flips — see the mag-mount and
vehicle-wheel sagas). The factory replaces guessing with **WYSIWYG hand-placement in
the port's own coordinate space** — you put the point/collider where you see it, and
the export is exact.

## Master's decisions (2026-07-22)
- **Standalone**, reached from the **main menu** (a sibling of Play / Workshop) — NOT
  a mode inside the map editor.
- **Each asset type gets its own separate editor** (Prop / Deployable / Vehicle / Gun),
  all built on one shared composer core.
- Export = **one new "bundled" asset type** (`.assetbundle`) that is easy to drop into
  the game (one loader, auto-registered).
- Compose from **existing port assets** first, with the ability to **pull more from the
  retail bundle** on request (via the existing `extract_*.py` tools).
- Undo-only (matches the map-editor decision). Our format + paradigm, no retail export.

## The bundle format (`content/assets/<name>.assetbundle`, JSON)
The thing you're really authoring. Coordinates/rotations in the **port's space**
(rotations = Euler degrees), so the file is WYSIWYG.

```jsonc
{
  "name": "jeep_with_pump",
  "type": "vehicle",                 // prop | deployable | vehicle | gun
  "parts": [                         // stacked meshes = the visible model
    { "mesh": "jeep_body.txt", "albedo": "jeep_albedo.png", "color": null,
      "pos": [0,0,0], "rot": [0,0,0], "scale": [1,1,1] },
    { "mesh": "gas_pump.txt",  "albedo": "gas_pump.png",   "color": null,
      "pos": [0,1.4,0.2], "rot": [0,0,0], "scale": [1,1,1] }   // welded to the roof
  ],
  "colliders": [                     // hand-placed; make it solid, not ghost
    { "shape": "box", "pos": [0,0.5,0],   "size": [1.0,0.6,2.2], "rot": [0,0,0] },
    { "shape": "box", "pos": [0,1.4,0.2], "size": [0.3,0.5,0.3], "rot": [0,0,0] }
  ],
  "volumes": [                       // named trigger boxes (Area3D)
    { "name": "cabin", "pos": [0,0.7,0], "size": [0.9,0.7,1.0], "rot": [0,0,0] }
  ],
  "points": [                        // named empty transforms = the hooks
    { "name": "Wheel_FL", "pos": [0.6,0.1,0.9],  "rot": [0,0,0] },
    { "name": "Seat_0",   "pos": [0.3,0.5,0.1],  "rot": [0,0,0] }
  ],
  "params": { "health": 200 }        // type-specific, freeform bag
}
```
`shape` ∈ box | sphere | capsule | convex (convex = auto-hull of a part).

## Runtime (the "easy to implement" half)
- **`AssetBundleLoader.Load(path) → Node3D`** — one loader turns any bundle into a Godot
  tree: a type-appropriate root body, `parts`→`MeshInstance3D` (mesh via
  `ContentProvider.ParseObj`, material from albedo/color), `colliders`→`CollisionShape3D`
  (Box/Sphere/Capsule/ConvexPolygon), `volumes`→named `Area3D`, `points`→named `Node3D`
  markers the game systems query by name.
- **Per-type binder** wires that tree into the system that consumes it:
  - **prop** → `StaticBody3D`, placeable object (drops into the map-editor object browser).
  - **deployable** → deployable/placement system; `storage` volume → inventory; `params.health`.
  - **vehicle** → `VehicleBody3D`; `Wheel_*` points → `VehicleWheel3D`, `Seat_*` → seats,
    `Steer` → steering; drive params from `params`.
  - **gun** → viewmodel; `Muzzle`/`Sight`/`Magazine`/`Eject`/`View` points → the hooks.
- **Auto-registration:** at boot, scan `content/assets/` → register each bundle in a
  catalog by name+type. New content = drop a file, no code.

## The editors
Standalone main-menu entry → **Asset Factory hub** → pick the type → that type's editor.
All editors share a **composer core**, reusing the map-editor infra:
`EditorCamera` (fly cam), `EditorGizmo` (move/rotate/scale), `EditorObjectBrowser` (pick a
part mesh). Core provides: add/remove parts, gizmo each part, add/place colliders + volumes
+ named points (wireframe-visualized), a hierarchy panel, save/load the bundle.
Each **type editor** specializes it: the standard point set auto-listed (empty slots to
fill), a type params panel, and a type-correct live preview.

## Integration points (grounded in current code)
- Main menu: `MainMenu` in `Main.cs` (~L489) already wires `OnEditor`/`OnNewMap`/`OnPlay`.
  Add `OnAssetFactory` → `BuildAssetFactory()` (mirrors `BuildEditor()` at ~L2016) + a menu button.
- Boot flag for headless test/render: add `--assetfactory` next to `--editor` (~L73).
- Parts: `ContentProvider.ParseObj(string)→ArrayMesh` (`ContentProvider.cs:92`).
- Reuse `EditorCamera` / `EditorGizmo` / `EditorObjectBrowser`.

## Build order (fidelity-first, verifiable slices)
1. **Format + loader + Prop binder (vertical slice).** `AssetBundle` (schema + JSON I/O),
   `AssetBundleLoader`, prop binder (StaticBody + colliders). Prove it by loading a
   hand-authored 2-part sample bundle and spawning/rendering it. *No UI yet* — de-risks the
   format, which everything else depends on.
2. **Composer core + Prop editor** (standalone, main-menu). Import port meshes, stack parts
   w/ gizmo, add box/convex colliders, save a prop bundle end-to-end in-editor.
3. **Metadata depth** — volumes + named points + wireframe viz + per-type hook dropdown.
4. **Deployable, then Vehicle, then Gun editors** — each = composer core + its point set +
   its binder. (Vehicle proves the "weld a pump onto a working car" case.)
5. **Polish** — live preview (spawn in a test scene), snapping, mirror/duplicate, undo,
   and bundle-import (retail prop name → `extract_*.py` → parts).

## Open / deferred
- Meshes referenced by filename (shared `content/`) vs copied into a per-bundle folder — start
  with references; revisit if bundles need to be portable.
- `params` schema per type — grow as each type editor lands.
- Whether prop/deployable bundles auto-appear in the map editor's object browser (likely yes).
