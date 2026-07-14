# Semi trailer (semi_1 prop) — rip package for a `_trailer` Spec

Ripped 2026-07-14 from `core.masterbundle` `objects/large/vehicles/semi_1/object.prefab`, mesh `Model_0` (268 verts). semi_0 = cab, semi_1 = trailer.

## Mesh: `trailer_0.txt`
- Oriented + grounded. Net rotation from the raw extract: `(x,y,z) -> (-x,-z,-y)` — the SAME net transform that made the semi_0 CAB right-side-up (semi_0 + semi_1 are one authoring set), then Y-grounded (min-Y -> 0).
- Bbox: **X -1.50..1.50 (width 3.0), Y 0.00..2.50 (height 2.5), Z -8.00..8.10 (LENGTH 16.1)** — a long box trailer.
- ⚠ **FLIP-CHECK INCONCLUSIVE**: bakeicon renders it edge-on (the 16 m length auto-frames to a bar). Orientation follows the corrected-cab transform so it's very likely upright, but **VISUALLY CONFIRM in-context** when you wire the Spec. If inverted, roll 180° about Z (`x->-x, y->(minY+maxY)-y, z->z`, re-ground) — the cab's exact fix. Don't trust a positive Y-range as "upright" (that's the trap the cab taught us).

## Spec guidance (clone `_semi` in `game/Vehicle.cs`)
- `Body = "trailer_0.txt"`. Albedo = semi_1's `_MainTex` → `trailer_0_albedo.png` (I didn't re-rip it — quick UnityPy pull once you have core.masterbundle, or flat grey placeholder to start).
- `BoxSize ~ (3.0, 2.5, 16.1)`, `BoxCenter ~ (0, 1.25, 0.05)`.
- **`.dat`: NONE** — semi_1 is a static OBJECT prop (`objects/large/vehicles/`), not a `Bundles/Vehicles/` vehicle, so no stat .dat. Health/mass/tow = design call.
- **Wheels**: a trailer = a REAR tandem bogie only (no steer, no drive — it's TOWED). Estimate from bbox: rear axles ~Z **+6.0 and +7.2**, X **±1.30**, radius ~0.55 (match the cab). NO front axle — the front rests on the cab's fifth wheel.

## The new mechanic: HITCH / tow
- A kingpin at the trailer FRONT (~Z -7.5) pins to the cab's fifth wheel (behind the cab seat). Godot: a `PinJoint3D` (or `Generic6DOF`) between the cab + trailer RigidBodies. **The cab has no hitch point yet — this is the real design work.**
- Confirm which Z end is the hitch (front) vs wheels (rear) when you render it behind the cab; flip Z if they land swapped.
