# Shelf/mirror expansion + runtime curtain/rug factories

Owner request: Discord 1559605385311297569. Runtime clarification:
1559606381508563059 / 1559606641182380043. Tiny's mesh-only contract:
1559611079146086404. This handoff does not change FurniturePlan or placement.

## Game-side C# API — real runtime generation, not baked presets

`game/RuntimeDecorGenerators.cs`, namespace `UnturnedGodot`:

```csharp
ArrayMesh curtain = RuntimeDecorGenerators.CreateCurtainMesh(width, height, open);
ArrayMesh rug = RuntimeDecorGenerators.CreateRugMesh(length, depth,
    RuntimeDecorGenerators.RugStyle.Diamond);
```

- Both return ArrayMesh only. No Node is placed, no collision or interaction
  is created, and no layout data is serialized by the factory.
- Curtains use two pleated panels, a fitted rail/caps/supports/tabs, and gathered
  open panels with tiebacks. The bool overload is the ordinary open/closed API;
  a float overload also supports intermediate openness (clamped 0–1).
- `width` is desired FABRIC COVERAGE, not the rough window opening. Core can
  choose an overlap, e.g. opening width + 0.40 m. Rail adds 0.28 m at ordinary
  game scale. Fabric drop is exactly `height`; the rail/tabs add 0.13 m overall.
- Godot Y-up. Curtain back plane is local Z=0, front faces -Z, and the whole
  mesh is centred in X/Y. To align fabric top to a window, use
  `CurtainClothTopY(width,height)`. To align the rod, use
  `CurtainRailY(width,height)`. Core owns the world position and yaw.
- Rugs use a SINGLE flat quad, two triangles. `height` means the second floor
  dimension (depth/Z), not vertical pile thickness. Mesh is centred in X/Z;
  all vertices lie at Y=0.015 m to clear the caller's floor origin.
- Rug Image/ImageTexture is generated when called: Bordered, Diamond, Striped,
  Checker. Pixel-art nearest filtering, with size-dependent texture resolution
  and border/pattern solving. WHOLE repeats fit the inner field; repeat counts
  balance near-square physical motifs and a consistent border scale. This
  includes the owner's texture-fit refinement in message 1559628137082658957.
  Four different rug aspect ratios, including a runner, were rendered to check
  the fit. No PNG or OBJ is loaded by either factory.
- Valid finite positive floating-point sizes are accepted, not snapped to a
  list of presets. Impossible/overflow-prone inputs are rejected explicitly.
  Four runtime curtain dimensions, including 3.17×1.93 m, were exercised.
- Resources are shared through a bounded 128-entry cache keyed by exact inputs.
  `ClearCache()` drops cache ownership without destroying live node references.
  Do not mutate a shared returned mesh/material; regenerate for changed inputs.
- FurniturePlan should save generator type/version, the two dimensions, state
  and any selected style/tint. Rebuild on loading. There is deliberately no
  fixed-size GUID for the procedural examples shown in the visual catalog.

## Static art changes

Existing GUIDs retained: Wall_Shelf_0, Wall_Shelf_1, Mirror_0,
Medicine_Cabinet_0. Shelf boards are now 0.10 m instead of 0.04 m; stocked
books/plant were raised onto the new board top. Bounds/feature offsets updated.

Mirror highlights are adjacent, non-overlapping parts of ONE plane, not a
second nearly-coplanar face. Hidden front underlays were removed. This removes
the geometry responsible for the fighting rather than moving it 1 mm farther.
Mirrors remain flat stylized art, not realtime reflections.

New variants: Wall_Shelf_2 short utility, _3 two-tier, _4 twin cubbies,
_5 picture ledge, and Mirror_1 full-height (1.2×2.8×0.09 m). Proposed new GUIDs
and exact real bounds are in the expansion manifest. Native OBJ convention is
unchanged: metres, Z-up, front +Y, middle of back face on Y=0.

## Verification and visual review

- Self-contained factory compiled with Godot.NET.Sdk 4.6.0 and 4.6.2 / .NET 8,
  zero compiler errors/warnings; no full game suite was run.
- Actual standalone Godot C# process called the factories, checked requested
  dimensions/back planes/UV axes, and rendered the returned meshes directly.
  Closed seams were inspected visually and meet by construction.
- Rug mesh vertices are genuinely flat. Godot pads its resource AABB by 1e-5
  on the flat axis; this is not rug thickness.
- Nine revised/new static pieces: front + three-quarter views, visually checked.
  Both curtain states and four rug styles: two actual runtime views each,
  visually checked. Runtime size gallery also inspected and its camera framing
  corrected. Rug UV axes were corrected before final review.
- The curtain fit preview uses planner-selected side overlap so the opening
  is covered from an angled view; this is not hidden factory placement logic.
- Full V3 catalog: 37 current static pieces + 6 runtime examples, 43 entries,
  two views per entry. Unchanged original pieces retain their reviewed images.
- Blender sources include the nine-piece expansion, full 37-piece portfolio,
  and an editable art study imported from the RUNNING engine's mesh output.
  Those curtain/rug study meshes are references, NOT a baked runtime library.

## Integration status

Local source bundle only; no push to main, no new catalog/GUID rows installed,
and no FurniturePlan changes made by the artist. Tiny owns placement, layout
persistence and registration of new static variants. Art updates/variants are
review candidates until approved. The runtime factory is ready to wire in.

The rejected Coat_Hooks_1 remains excluded from the current kit.