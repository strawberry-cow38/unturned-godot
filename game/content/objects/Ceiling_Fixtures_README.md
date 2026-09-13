# Ceiling fixtures

Four new Z-up, metre-scale fixtures. The ceiling mounting point is `(0,0,0)`;
all geometry hangs at `z <= 0`. Each base OBJ has exactly **60 triangular faces**,
of which **12 (20%)** pass its selected runtime splitter. Every face corner has
both a UV and an explicit flat normal. The four PNGs are exactly 2 x 2 pixels.
Each `_lod1.obj` shares its base model's `_tex.png`; there are no LOD textures.

| Model | Shape | LOD0 v / f / emitter f | LOD1 v / f / emitter f | X x Y x Z bounds size, metres (both LODs) |
| --- | --- | --- | --- | --- |
| `Ceiling_Bulb_0` | Small bare bulb, ceramic socket and ceiling rose, short flex | 124 / 60 / 12 | 72 / 32 / 6 | 0.12 x 0.12 x 0.50 |
| `Ceiling_Shade_Cone_0` | Eight-sided metal cone with recessed white reflector | 136 / 60 / 12 | 78 / 32 / 6 | 0.40 x 0.40 x 0.60 |
| `Ceiling_Shade_Dome_0` | Wider six-sided enamel shade with two profile bands | 140 / 60 / 12 | 86 / 34 / 6 | 0.60 x 0.60 x 0.55 |
| `Ceiling_Utility_0` | Short workshop/barn fixture with two crossed guard straps | 120 / 60 / 12 | 76 / 34 / 8 | 0.30 x 0.24 x 0.30 |

The sizes describe these new models, not measurements copied from existing
pendants. The quantitative references are the shipped light props' triangle
budgets, flat shading, palette mapping and metre scale. Guard straps use thin
sheet geometry with the existing object's material convention (`CullMode.Disabled`).

## Runtime kind and UV contract

**All four models, including their LODs, require `LampLight.Kind.CeilingStrip`.**
The kind is also stated in every OBJ header. `ObjMesh.Load` leaves positions
unchanged under default `CONV=1`, flips V and reverses triangle winding.
`LampLight.cs:130` calls `ObjMesh.SplitLens`: all three triangle corners must
have `u > 0.5 && v > 0.5` after loading.

Only the `g emitter` triangles have OBJ UV `(0.75,0.25)`, which loads as
`(0.75,0.75)`. Every corner is 0.25 clear of both threshold lines. Group names
are labels for review; runtime selection depends exclusively on these UVs.
Emitters are closed, consistently wound solids: `SplitLens.CapHoles` has no
boundary to cap and adds no triangles to them. LOD emitter fractions are
18.75%, 18.75%, 17.65% and 23.53%, respectively.

These are unregistered assets. **`LampLight.KindFor` currently returns `Generic`
for these new names.** A future caller must explicitly pass `CeilingStrip` to
`LampLight.Make`, or add the names to `KindFor` during a separately scoped
integration. OBJ comments do not configure runtime kind. The existing kind
also retains its existing light placement and fluorescent hum behavior.
No manifest, GUID mapping, existing asset or C# source was changed here.

The supplied palette specification was retained exactly, even where it differs
from this checkout's existing palette:

| PNG pixel (top-to-bottom rows) | RGB | OBJ UV | Role |
| --- | --- | --- | --- |
| Top left | 131,131,131 | 0.25,0.75 | Matte grey housing/shade |
| Top right | 46,39,19 | 0.75,0.75 | Dark flex/guard |
| Bottom left | 237,237,237 | 0.25,0.25 | Ceramic/reflector |
| Bottom right | 0,0,0 | 0.75,0.25 | Emitter; black while off, warm emission when powered |

The V-flip puts `CeilingStrip`'s emitter in the PNG's bottom-right texel.
Putting a dark cord there would incorrectly make the cord glow.

## Measurements checked against this checkout

`Light_0` has 118 vertices, 60 triangles and **12 selected triangles (20%)**
under the real V-flipped `SplitLens` predicate, as stated in the brief.
All 34 world placements (12 PEI, 22 Washington) have `ex=270`. The placement
basis is `Ry(180-ey) * Rx(ex) * Rz(-ez)`; at `ex=270, ez=0`, local negative Z
becomes negative world Y, so these fixtures hang below the mounting point.

Two supplied measurements differ from the checked-out reference:

- The full `Light_0.obj` AABB is `(-0.5,-2.000001,-0.25)` to
  `(0.5,2.000001,0.75)`, or **1.00 x 4.000002 x 1.00 m**.
  Its selected emitter spans **0.600003 x 3.894548 x 0.20 m**: the brief's
  3.90 x 0.60 m measurement matches the emitter, rather than the full fixture.
- The actual `Light_0_tex.png` RGBA pixels, in row order, are
  `(131,131,131,255)`, `(177,170,150,255)`, `(112,112,112,255)`,
  `(177,170,150,255)`. The new palettes follow the user's explicitly supplied
  RGB values instead, as recorded above.

## Verification performed

A throwaway script (`/tmp/verify_ceiling_lamps.py`, intentionally not committed)
independently parsed all eight generated OBJs. It reproduced `ObjMesh.Load`'s
V-flip/winding and the selected kind's actual predicate, and printed vertex
counts, triangle counts, AABBs, kinds and emitter counts. It checked the
transcribed predicates against the C# source. Reference cross-checks also gave
`Lamp_0 / DeskBulb = 2/48` and `Lamp_1 / FloorShade = 16/40`.

All eight passed complete indices/UVs/normals, finite coordinates, unit normals,
consistent winding, nondegenerate geometry, emitter-only selection, closed
emitters, exact palette pixels and downward placement. A CPU projection of the
actual OBJ triangles was inspected from below for silhouette and LOD continuity.
No Godot executable was available, so this is a file/predicate audit and
geometry preview, not an engine rendering test.
