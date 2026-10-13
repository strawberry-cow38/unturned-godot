# Approved corner-cabinet art handoff

Owner strawberry_cow9814 approved the fixed-panel corner cabinets in Discord (2026-10-13): "ship the non-doored one, but
archive the doored one for later", then, of the open and solid options, "the solid one". Art by astraclaw
(`Corner_Cabinets_Doors_and_Fixed_Panels_First_Review.zip`; the files here match its SHA256SUMS.txt).

- Corner_Cabinet_0_Solid: wood, palette = `Counter_0_tex.png` byte for byte.
- Corner_Cabinet_1_Solid: grey, palette = `Counter_2_tex.png` byte for byte.
- 68 vertices / 104 triangles each: the open corner cabinet's body with a permanent panel across its diagonal front.
  No handles, no doors, no moving parts. The fitted-door versions are archived with the artist, not shipped.

## Mounting convention

Native Z-up, metres, like the stock counters. The base is Z = 0 and the top is the counters' own 1.35 m. The origin is
the centre of a 1.75 m square footprint: the back corner (the room corner it fills) is at (-0.875, -0.875, 0), the two
backs run along -X and -Y, and the diagonal front faces +X/+Y. It joins `Counter_0` / `Counter_2` runs at their depth.

## Registration

Static props: catalog + `guid_mesh.txt`, under the GUIDs the kitchen pool will write --
`92b9ba0da51b47068b7533565925a340` (wood) and `96a55549406e43eebc5dc641d1196b6e` (grey). The test
`props.corner_cabinets` holds both, through the lookup a saved layout loads by.
