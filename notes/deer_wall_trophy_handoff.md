# Deer wall-head trophy — art handoff

## Asset
- Name: `Deer_Wall_Trophy_0`
- Proposed GUID: `5675fbbc6be24c03ad46f40459907a02`
- Native size: **1.463758 m wide × 1.333108 m high × 0.763063 m deep**.
- 92 geometric vertices / **152 triangles**, one mesh, one material, one 4×2 nearest-filter palette texture.
- Art ready for review. **Not registered, spawned or merged into main.**

## Actual existing deer, not a new imitation
The source is the existing `game/content/deer_rig.json` animal. Its full rest skeleton and bind poses were evaluated before extracting the rigid `Skull` faces. All **90 original head/eye/muzzle/antler triangles** remain at the original scale and shape; the source face IDs are in the manifest. There is no invented face or replacement antler model.

The original neck's eight source triangles are clipped shorter at forward Y=0.98 in the extraction study's coordinates. Those become 12 triangles, plus a six-triangle hidden back cap. The only other new geometry is a simple eight-sided wooden plaque with a front bevel (44 triangles).

The left half of the small palette texture is the exact stock deer palette; the right half is the stock `Counter_0` wood palette. Source UVs are mapped into the left half and converted from the rig's Godot V-down convention to the native OBJ/Blender V-up convention. This preserves the **black eyes**, stock hide, antlers and pale underside in the actual engine renders.

No rig, animal AI, animation, interactable clothing or pickup is exported.

## Mount convention — same as Clock_0 and the wall kit
- OBJ: metres, **Z-up**, front **+Y**.
- Back face of the plaque: **Y=0**, no geometry behind the wall.
- Origin: middle of the complete object's back-face bounds (X/Z centred).
- Stock-size head; no whole-model scale compensation needed.
- GLB preview: the equivalent Godot Y-up orientation, front −Z.
- Suggested origin height: ~2.1 m above the room floor; choose a suitably clear wall in the placement pass.
- Suggested use: a rare rustic living-room/hall/office accent, not every home.
- Reserve the complete width/height and ~0.76 m projection. Avoid windows, doors, open-plan cuts and fireplace walls using the existing placement exclusions.

## Visual review
Two actual Godot 4.6 renders, straight-on and three-quarter, are included individually and on the review page. Both were inspected: recognisable stock silhouette, correct eyes/palette, neck joined into the plaque, board against the wall, no visible gap at the mount. The original head triangles were retained rather than redesigned to add realism.

The editable `Deer_Wall_Trophy_First_Review.blend` contains **only the trophy** and its packed atlas. Temporary image-review cards and the whole live animal are not part of this source.

## Integration delta
The separate `Staging-Astra-Deer-Trophy` branch is based on Tinyclaw's published décor expansion commit `09255d473ab87a55d17713d870c5e365ae351048`. Its new commit adds only the OBJ/PNG and these notes/manifest. It does **not** alter the prior shelf/mirror models, runtime curtains/rugs, catalog registrations or core placement code.

The previously delivered plain-mirror revision `dc5d33c9` is a separate handoff; do not treat this trophy delta as replacing or including it. Tinyclaw can register the proposed GUID/name and add it to the appropriate wall pool once the art is accepted.

## Editable/reproducible package
The ZIP includes the native OBJ/PNG, GLB and geometry dump, manifest, two captures/review page, packed Blender source and the extraction recipe with its existing deer reference data. The reference rig is provenance/rebuild input, **not a new animal asset** to install.

Run the Blender recipe only in a separate/empty art scene: it deliberately replaces the scene's current objects. The live game only needs the final OBJ/PNG, not the recipe, reference rig, Blender file or preview renders.