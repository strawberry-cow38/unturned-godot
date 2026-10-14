# Parking meter — approved uniform-grey version

Strawberry's approval message `1559862625376927745`, October 14, 2026: keep the revised shape and apply the post's uniform grey throughout, then approved. **That colour condition is applied in this version.** Registration/publication is still a separate integration step.

## Final asset

- `Parking_Meter_0`, preserved GUID **`d58deacfeb1f4e82849a07411a160c49`**.
- **24 geometric vertices / 40 triangles**, one flat-shaded mesh and one opaque 2×2 nearest-filter palette.
- **0.52 m wide × 0.28 m deep × 2.10 m high**.
- Blank original eight-corner head, square 0.14 × 0.14 m post, no dial/slots/hardware, **no foot or base**. Post goes from ground Z=0 to 1.48 m, entering the head by 0.08 m.
- Every palette texel is now **sRGB (130,130,130)**, the exact post-grey swatch from `Street_Light_0`. No lighter head colour or baked per-face tint remains; ordinary lighting still shades the faces.
- **Geometry OBJ is byte-for-byte unchanged from `99b15b46`**. This is a texture/status/documentation update only, not another silhouette change.

Native metres, **+Z up, +Y front**, post centred in X/Y, bottom/base at Z=0. Standard Godot conversion is Y-up/front −Z. **Already upright: do not apply the retail hedge/bench 270° X correction.** No new GUID or prop name.

## Integration

Approved for registration/placement by Cow tools / Tinyclaw. The main game files are `Parking_Meter_0.obj` and the **latest uniform-grey** `Parking_Meter_0_tex.png`; do not pair the final model with the older two-tone palette or use the first face-detailed model.

This delta implements **no** catalog/registry edits, road/parking placement, payment/timer interaction, core/protocol or save changes. Registration remains false in the art manifest until the integrator actually adds it. Suitable for kerbside parking or the pedestrian edge of a parking lot, controls/front toward accessible pavement, clear of doorways, driveway mouths, vehicle aisles and through-pedestrian routes. The 0.6 m front-access metadata is a placement suggestion, not a legal/accessibility rule.

`REFERENCE__Mailbox_0` and `REFERENCE__Chair_Metal_0` are actual native-size stock scale references. They are excluded from the main OBJ/GLB and must not become new registered props.

## Source and visual review

Editable `Parking_Meter_Approved.blend` contains the approved meter plus those two clearly named reference-only props, three packed palettes, no temporary image cards. Recipe/config/native OBJ/GLB/JSON/manifest and a reproducible standalone Godot art-review project are supplied. Rebuild only in a separate empty Blender scene: the recipe replaces its current objects.

Three actual Godot captures—full-height front, full-height angle, and head detail—were visually inspected through the private Blender viewport. Clean source restored after inspection. **No test suites run**; model/material edits, engine renders and visual art checks only.

The first colour edit applied successfully but its GLB export hit an immediate context error after opening a Blender file. The current scene/material were inspected (uniform 130 grey, unchanged 24 vertices / 40 triangles); only the pending export/save was completed under an explicit viewport context. The scene edit was not blindly replayed.

Art-only branch `Staging-Astra-Parking-Meter-Approved` is based on `99b15b4613d81e8a218ec6ffca0336e56636073c`. The latest delta changes only the palette plus notes/manifest. Bundle includes the preceding meter commits and requires published `862306c7131ab2a71efd7b8cc45cf4f8fed5249f`. No remote push or game registration is claimed by this handoff.