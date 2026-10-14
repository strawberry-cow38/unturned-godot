# Parking meter — first art review

Requested by Strawberry in Discord `1559853055573495829`, October 14, 2026. **Review pending, not approved or registered.** One new street-prop model; no town placement, payment, parking enforcement or ticking timer behavior is implemented here.

## Main asset

- Name: `Parking_Meter_0`
- Proposed stable GUID: `d58deacfeb1f4e82849a07411a160c49`
- 116 geometric vertices / **184 triangles**.
- **0.52 m width × 0.365 m depth × 2.10 m height**.
- Native metres, **+Z up, +Y front**, post centred at X/Y=0, bottom/base at Z=0. The normal Godot conversion is Y-up/front −Z. **This model is already upright: do not apply the retail hedge/bench 270° X correction.**
- One flat-shaded mesh, one opaque nearest-filtered **2×2 palette**. No photographic texture, tiny screws, micro-text, disconnected head or hovering hardware.

Chunky single-head analog silhouette, eight-sided metal base/post, pale inset gauge with one needle/three broad marks, a genuinely recessed coin slot, and a simple joined turning grip. The post enters the head by 0.08 m; the hardware is attached to the head rather than floating ahead of it.

## Matching stock props

Colours are exact samples from the checked-in stock textures:

| Role | RGB | Source |
|---|---|---|
| Head / grip metal | 176,176,176 | Chair_Metal_0 |
| Post / side metal | 130,130,130 | Street_Light_0 |
| Gauge face | 222,222,222 | Clock_0 |
| Gauge marks / slot recess | 39,39,39 | Clock_0 / Chair_Metal_0 |

The two full-height review views include the actual `Mailbox_0` and `Chair_Metal_0` at **unmodified native size**, not approximate replacements. Their reference meshes and textures are included for a reproducible art review; they are **not part of the parking-meter main OBJ/GLB, and must not be registered/exported as new props**.

The local object/item/catalog asset set was checked for a parking-meter model before authoring; none was found there. This is not a claim about every later/unfetched branch or every release of the original game.

## Placement/integration handoff

Suitable for kerbside parking or the pedestrian edge of a parking lot. Face its controls toward accessible pavement, keep clear of doorways, driveway mouths, vehicle aisles and the centre of through-pedestrian routes. Metadata suggests 0.6 m clear front access, not a mandatory legal design dimension or a substitute for the town generator's stronger clearances.

Cow tools / Tinyclaw own registration and any town placement **after art approval**. No registry/catalog, road geometry, solver, protocol or core-code edits are part of this delta. No paid parking/gameplay interaction was requested; the needle is static art.

## Source and visual review

`Parking_Meter_First_Review.blend` contains the meter and two clearly named reference-only stock props, with three packed palettes and no temporary image cards. Source recipe/config and native OBJ, GLB, JSON/manifest are supplied. The rebuild recipe replaces the current Blender scene: use a separate empty art scene.

Three actual standalone Godot captures: full-height front, full-height angle, and head detail. Final images were inspected through the real private Blender viewport. The head close-up hides unrelated reference props; full views retain them for scale. Clean source restored after inspection. **No test suites run: art generation, engine renders and visual checks only.**

Art-only branch `Staging-Astra-Parking-Meter` is based on published `862306c7131ab2a71efd7b8cc45cf4f8fed5249f`. Only the new meter OBJ/palette plus these notes/manifest are included, not subsequent switch edits or other project work. Source/reference/render assets are in the ZIP, not automatically added to the game. No remote push or approval is claimed by this first-review delivery.