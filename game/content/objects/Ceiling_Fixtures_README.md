# Ceiling fixtures — fourth review pass

Geometry changes are complete and **uncommitted** on `astra-lamps2`. The six OBJ
files retain Z-up metre coordinates, `z=0` ceiling mounting, negative-Z hanging
geometry, and the 0.140 × 0.140 m square plates. The existing 2×2 palette bytes
and runtime/material code are unchanged.

## Geometry changes

Visible wire lengths, measured from the underside of the 18 mm plate to the
first fixture component, are:

| Fixture | Previous wire | Current wire | Reduction |
| --- | ---: | ---: | ---: |
| Ceiling_Bulb_0 | 0.358 m | 0.238 m | 33.52% |
| Ceiling_Shade_Cone_0 | 0.262 m | 0.174 m | 33.59% |
| Ceiling_Shade_Dome_0 | 0.302 m | 0.202 m | 33.11% |

The cone also loses the long wire section inside its old shade: total LOD0 wire
mesh length drops from 0.587 to 0.179 m. The bare and dish wire mesh lengths are
now 0.243 and 0.207 m. These include the small overlaps into the plate/socket.

The shared bulb’s shoulder, belly and tip move **18 mm up** relative to the
unchanged socket. The neck is widened to a 20 mm radius beneath the 22 mm
socket lip and its exposed straight section is reduced from 20 to **2 mm**.
Only the exposed envelope is authored, closed at the socket’s bottom mating
plane; no emitter vertices extend upward into the socket. The envelope retains
its 82 mm diameter and has an 82 mm exposed height. The rounded body is
translated without scaling. One part per LOD is authored and copied three times.

The four-sided taper now mounts around the socket, 6 mm below its top, with
no wire between socket and shade. Its height changes from 380 to **96 mm**
so the same bulb can both meet the shade at the socket and protrude below it.
The 180 mm square mouth is retained. At `z=-0.294`, it leaves **40 mm** of
bulb below the opening. The socket origin is `z=-0.192`.

The wide dish retains a 600 mm octagonal mouth and 100 mm overall depth,
with its top mounted at socket origin `z=-0.220` and mouth at `z=-0.320`.
The shell has separate outer and concave inner profiles, a 7 mm rim, and a
socket-sized mounting hole. There is **no underside cap across the mouth**.
The inner wall follows the outer shoulder and skirt, leaving a thin wall
instead of the previous shallow inner fan. The bulb extends 42 mm below the rim.

LOD0 dish housing: 32 outer faces, **32 inner faces**, 16 rim faces and 16
mounting-hole wall faces. LOD1 simplifies the profiles to 12 outer faces,
**12 inner faces**, 16 rim faces and 8 mounting-hole wall faces, preserving the
octagonal mouth and open cavity. **Zero inner faces are emissive at either LOD.**

## Per-file measurements

Vertices count OBJ `v` records; every face is a triangle with `vt` and `vn`.
Bounding boxes and Z ranges use all face vertices, in metres. The above-threshold
count includes a face if **any** of its vertices is above `z=-0.15`.

| Model | Verts | Faces | Bbox min → max (m) | Emissive faces | Emissive Z range (m) | Emissive faces above −0.15 | Shared bulb hash |
| --- | ---: | ---: | --- | ---: | --- | ---: | --- |
| Ceiling_Bulb_0 | 63 | 106 | (-0.070, -0.070, -0.398) → (0.070, 0.070, 0.000) | 62 | [-0.398, -0.316] | 0 | `a862d6543154` |
| Ceiling_Bulb_0_lod1 | 39 | 58 | (-0.070, -0.070, -0.398) → (0.070, 0.070, 0.000) | 38 | [-0.398, -0.316] | 0 | `d490d2f1716f` |
| Ceiling_Shade_Cone_0 | 79 | 138 | (-0.090, -0.090, -0.334) → (0.090, 0.090, 0.000) | 62 | [-0.334, -0.252] | 0 | `a862d6543154` |
| Ceiling_Shade_Cone_0_lod1 | 49 | 74 | (-0.090, -0.090, -0.334) → (0.090, 0.090, 0.000) | 38 | [-0.334, -0.252] | 0 | `d490d2f1716f` |
| Ceiling_Shade_Dome_0 | 103 | 202 | (-0.300, -0.300, -0.362) → (0.300, 0.300, 0.000) | 62 | [-0.362, -0.280] | 0 | `a862d6543154` |
| Ceiling_Shade_Dome_0_lod1 | 59 | 106 | (-0.300, -0.300, -0.362) → (0.300, 0.300, 0.000) | 38 | [-0.362, -0.280] | 0 | `d490d2f1716f` |

LOD1 face counts are 54.72% (bare), 53.62% (taper) and 52.48% (dish) of LOD0.
Each LOD pair has identical full bounds. The plates retain their previous exact
geometry: ten LOD0 triangles and two LOD1 triangles.

## Shared bulb and emitter verification

Only envelope faces use OBJ UV `(0.75,0.25)`, which `ObjMesh.Load` flips to
`(0.75,0.75)`. All three corners lie 0.25 clear of both selection boundaries.
The plate, wire, socket, shade, inner wall, rim and mounting wall contribute
**zero** faces to `SplitLens`. Every emitter is nonzero, closed after the
runtime’s 0.1 mm positional weld, and at/below its socket’s bottom plane.

[audit.py](../../../notes/ceiling_fixtures_review/audit.py) extracts every vertex
used by the UV-selected faces, normalizes Z to that envelope’s own top using
Decimal arithmetic, and sorts XYZ. The reported hash is the first twelve digits
of SHA-256 over six-decimal CSV rows with LF after each row. This serialization
is explicit so the identity check can be reproduced independently.

- All three LOD0 bulbs: **33 vertices**, `a862d6543154`.
- All three LOD1 bulbs: **21 vertices**, `d490d2f1716f`.

A second check byte-compares the complete socket-relative assembly’s
canonical `v/vt/vn/f` attributes, including winding, normals, UVs and topology.
All three assemblies match per LOD. Full hashes and measurements are in
[measurements.json](../../../notes/ceiling_fixtures_review/measurements.json).

The dish audit checks closed shell edges with consistent winding, inward/downward
inner normals, no flat inner cap, and shell Euler characteristic zero. At each
LOD, 32 upward rays from below the mouth enter the cavity, hit the inner wall,
then cross only the outer wall. LOD0 sampled cavity depths are 22–85 mm and
wall thicknesses along Z are 6.2–10.5 mm. LOD1 retains the open cavity with
a simpler conical wall profile.

## Owner review renders

- [Full fixtures: side, looking up, directly below](../../../notes/ceiling_fixtures_review/geometry.png)
- [Shared bulb and socket close-ups](../../../notes/ceiling_fixtures_review/bulb_detail.png)
- [Dish interior and exact half-section](../../../notes/ceiling_fixtures_review/dish_cutaway.png)
- [Pass-three / pass-four underside comparison](../../../notes/ceiling_fixtures_review/axial_comparison.png)

These are depth-buffered, backface-culled renders of the actual OBJ triangles
and normals, not engine screenshots. A matte color marks the bulb; blue in the
dish inspection marks non-emissive housing. They simulate no glass or glow.
The half-section removes only near-half shade triangles along their existing
`y=0` sector boundary. It adds no surfaces. Visible bulb pixel counts are saved
in [visibility.json](../../../notes/ceiling_fixtures_review/visibility.json).

Validation passed for indices, finite values, normals/winding, nondegenerate
faces, emitter closure/placement, shared parts, wire reduction, deeper seating,
socket-mounted taper, open dish, exact plates, LOD ratios/bounds and palette
hashes. No runtime, manifest or palette files were edited in this pass.

Run from the repository root:

```sh
python3 notes/ceiling_fixtures_review/generate.py
python3 notes/ceiling_fixtures_review/audit.py
python3 notes/ceiling_fixtures_review/render.py
```

The pre-edit working-tree assets and prior review artifacts are backed up under
`/tmp/ceiling_bulb_pass4/`. The previous utility-fixture deletions remain intact.
HEAD is unchanged; **nothing has been committed**.

Existing integration contract: these names still require the caller to supply
`LampLight.Kind.CeilingStrip` for `SplitLens`; runtime registration and glass/
emission material behavior belong to the separate code pass. All three palette
files retain SHA-256
`2f5377818dc60ef2297d2b263bfd343e2d6658e249975466516260644712a3f3`.
