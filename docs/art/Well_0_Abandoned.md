# Well_0_Abandoned — actual stock prop duplicate, damaged

Owner request `1558977287377125437`, clarified by `1558980050563375168`: **same prop, just broken; take the actual prop, edit it and make a duplicate**. Approved as a prop by owner message1558987793685028976; this is not a replacement well design.

## What was actually edited

1. Imported the actual `content/objects/Well_0.obj` and original 2×2 palette from main `5f680f64bb65907db75cb19c9d063db4b10b6dac` into the real Blender instance.
2. Used an actual Blender object/mesh **copy**. The untouched stock reference remains in the source file.
3. Isolated the **existing roof from that duplicate**, welded only its exported UV/normal seams, and made three irregular Boolean cuts: front-left rotted edge, right-side hole and a rear-right eave bite. The original shallow gable, thickness and surrounding roof shape remain. **No new slatted roof, rafters, battens, posts or architectural redesign.**
4. Capped the four original post ends now potentially exposed by roof damage, using their existing corner coordinates.
5. Muted/desaturated the existing palette to age the wood and stone. No texture-size or UV-atlas layout change; the original stone swatch remains the top-right texel.

The **104 stock stone-ring/post triangles have exactly the same geometric corner positions**, checked to 1 micrometre. Overall bounds, origin and scale also match the stock prop: **2.000002 × 1.970322 m footprint, 2.625 m high**, base at zero. Stock reference has 124 triangles; the damaged copy has **248 vertices / 224 triangles** including new broken-edge faces and post-end caps.

## Native export / source

`models/Well_0_Abandoned.obj` + `Well_0_Abandoned_tex.png`: normal **PROP Z-up metres**, original XY origin, base Z=0. No resource part index or Y-up foliage transform.

`Well_0_Abandoned_Actual_Duplicate.blend`: untouched actual Well_0 reference on the left, damaged duplicate components on the right, packed palette images. Components remain separate only for editability; the native OBJ is one combined mesh.

`export/Well_0_Original.glb` and `export/Well_0_Abandoned.glb` are origin-centred Y-up preview exports. The duplicate was exported at zero translation before restoring source-lineup staging. Previews show these actual GLBs at scale 1 under the same lighting, not proxy reconstructions.

`edit_actual_well.py` records the actual duplicate-and-cut recipe; its input/output paths use private `/workspace` paths. `write_native_and_audit.py` writes the native model from real Blender snapshots. The original reference OBJ/palette files are provided unchanged in `reference/`.

## Shaft / gameplay boundary

The game already uses `WellShaft` / `well_depth.gdshader` to fake depth. **No interior shaft mesh was added, and that shader was neither loaded nor enabled in these previews.** A shallow-looking hole is the ordinary shader-off appearance, not a new floor. Its safety-off default and game code are untouched.

The inherited bore geometry remains unchanged; existing constants `InnerRadius=0.76`, `DiscZ=0.30` are not redefined here. Native UV `(0.754,0.769)` still reads the top-right stone texel from the 2×2 palette, so the existing wall-colour sampling convention is preserved. Any behaviour/catalog registration or variant alias is a separate integration step.

**Appearance approved.** This art branch adds the native model and texture only. GUID/catalog registration, placement and behaviour wiring remain an integration step; no gameplay, collider/destruction or shader-behaviour change is included. No variant LOD authored yet (full mesh is224 triangles).

## Focused verification

- `export/contract.json`: actual source SHA, duplicate/cut method, 104 retained geometric triangles, scope and shaft boundary.
- `audit.json`: finite geometry, unit normals, nondegenerate triangles, normal/winding agreement and valid native UVs. The native audit caught two near-collinear Boolean triangulation slivers; a focused beauty-triangulation/degenerate cleanup fixed them while keeping the roof closed. Count stays 224.
- Actual GLB import verifies both models have the same bounds, scale 1 and expected triangle counts.
- Live Blender model plus actual exported comparison and roof close-up were visually inspected. Temporary image-review geometry was discarded from the clean source.
- Standalone preview exit 0, about 9 seconds, peak RSS 425,552 kB (~416 MiB). **No game build or full L0/L1 suite was run.**