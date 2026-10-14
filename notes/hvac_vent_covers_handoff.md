# Indoor HVAC vent covers — art handoff

Requested by Strawberry in message `1559679829199028285` on October 13, 2026. Two simple fixed covers, modelled and inspected in Blender, then rendered in Godot. Art review pending; no registration or in-game placement is claimed.

## Models

| Name | Native dimensions | Geometric vertices | Triangles | Proposed GUID |
|---|---|---:|---:|---|
| Floor_Vent_0 | 1.00 m X × 0.50 m Y footprint; 0.035 m Z thickness | 84 | 130 | `10db56bd553a4989b723180bee5d9752` |
| Wall_Vent_0 | 1.00 m X × 0.60 m Z face; 0.055 m Y projection | 60 | 94 | `c9b9f7b2e26d42efb28da815eca5beb2` |

Each is one flat-shaded mesh, one material and a 2×2 nearest-filter palette texture. The metal greys and charcoal are exact swatches from stock `Chair_Metal_0` and `Counter_2`, not detailed photo textures.

The floor register has eight tilted slats across its short direction. The wall grille has five tilted horizontal louvers. Both have a simple framed rim and real geometric spacing over a shallow dark backing plane. There are no fans, ducts, tiny screws, moving parts or realistic micro-detail.

## Native mounting

Both native OBJ models use metres and +Z up. Export origins are zero; the positions in the editable Blender scene are display staging only.

**Floor_Vent_0**
- Origin centred on X/Y at the bottom mounting plane **Z=0**.
- All geometry is on or above Z=0; top is Z=0.035 m.
- Long axis is X; the normal floor placement keeps it flat. Any floor yaw is fine.
- Decorative cover: it does not cut a hole through the slab. Do not treat it as a blocking floor obstacle merely because it has a visible rim.
- Placement should use free floor area, not under furniture, rugs, stairs or a door sweep.

**Wall_Vent_0**
- Same mount convention as Clock_0/the wall décor kit: origin centred on X/Z at the back face **Y=0**, front **+Y**.
- No geometry goes behind the wall; frontmost rim is Y=0.055 m.
- Suggested low-wall origin ~0.35 m above the floor for a return-grille look. An upper-wall location is also a placement choice; the art has no height dependency.
- Keep the room's existing exclusions for windows, doors, fireplace walls, furniture and open-plan cuts.

The GLB previews use the corresponding standard Godot Y-up orientation. No scale correction or negative scaling is needed.

## Review and handoff

Included: two **actual Godot 4.6 renders per cover** (overhead/front and three-quarter), a labelled four-view review sheet, a two-page visual catalog, native OBJ/PNG, GLB/geometry dumps, manifest and the packed editable Blender source.

All four renders were visually checked: slats remain clear at the game-like scale, rim depth stays low, the recess is visible, and the wall/floor mounting planes meet the preview surfaces without a visible gap. The geometry export also verifies the dimensions and origin conventions.

The separate `Staging-Astra-HVAC-Vents` art branch starts from Tinyclaw's already-published `09255d473ab87a55d17713d870c5e365ae351048`. Its delta is only the four model/texture files plus these notes/manifest. It does not include or replace the deer trophy, plain-mirror revision, runtime curtain/rug code, or catalog registration changes.

The supplied GUIDs are proposed IDs to preserve when the props are registered. Tinyclaw owns catalog entries and procedural placement. These are **visual covers only**, not a ventilation/HVAC simulation.

## Editable source

`HVAC_Vent_Covers_First_Review.blend` contains only the two covers and their packed palettes, not temporary review-image cards or game scene geometry. The recipe/config/palette references are provided to reproduce the art. Run the recipe only in a separate/empty Blender art scene: it deliberately replaces the current scene objects. Only the final OBJ/PNG files are needed for the game.