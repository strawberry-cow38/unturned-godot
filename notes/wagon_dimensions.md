# Original wagon construction coordinates

Metres; +Y up, forward −Z. This replaces the old sedan-extension derivation. The unchanged `vehicle_measurements.md` still contains a historical copy of that old derivation. Fleet references and the reasoning for the main dimensions are in [WAGON_REPORT.md](WAGON_REPORT.md) and the pre-authoring [vehicle_style.md](vehicle_style.md).

Let **t=0.25 m**, the original sampled longitudinal pillar/roof section. Revision 3 widens the lateral wall/post span to 0.28 m so every outer face is flush at X = ±1.26. Decimal rounding to 0.01–0.02 m is deliberate. Panel-detail choices below are subdivisions of that section and the chosen envelope, not additional empirical fleet statistics.

| Part | Coordinates / construction | Source / rule |
| --- | --- | --- |
| Body envelope | X ±1.26, Y −0.27..2.17, Z −2.807996..2.68 | Fascia plane extended to the flat floor; body bumpers removed. Body length 5.487996 m. |
| Side shell ends | Floor front Z −2.807996, hood nose Z −2.757828, rear gate Z 2.68 | Outer X ±1.26 throughout; donor bumpers are separate parts. |
| Side wall | Outer X ±1.26; bottom Y −0.27 throughout, meeting the hood bevel/A-post and belt rail at Y1.10; inner cabin X ±0.98 | Vertical side walls meet the full-width flat floor. |
| Axles | (±1.30,0.25,−1.56), (±1.30,0.25,1.46); front steering | 3.02 wheelbase from three-specimen mean ratio, 2.60 median track, modal ride −0.35. |
| Underfloor | X ±1.26, Y −0.27, Z −2.807996..2.68; passenger floor X ±0.98, Y −0.12, Z −1.25..1.36 | Flat external sheet; old underside bevel and front lip stations removed. |
| Load floor | X ±0.98, Y0.18, Z1.36..2.53; vertical step from Y−0.12 at Z1.36 | Retains start after seatback maximum Z1.357307 and clear run 1.17 to gate. |
| Sedan hood | Centre X ±0.98, (Y,Z): (0.999953,−2.757828) → (1.125,−1.25), directly to glass. Planar outer strips reach X ±1.26: (0.874953,−2.762766) → (0.994273,−1.323997) | Sedan nose height/drop and longitudinal nose scale 0.964159181; shelf removed. Bevel 24.352857° at the wider 0.28 m span. Both front and rear trim edges remain in the fascia and windscreen planes. |
| Rear gate lower panel | Inner Z2.53, outer Z2.68; top Y1.10; inner lower edge Y0.18; exterior down to Y−0.27 | Full width X ±1.26; painted closed tailgate, without latch or baked bumper. |
| Bumpers | Separate hatchback donor parts, X ±1.26; front Y−0.12..0.139451, Z−2.949005..−2.791821; rear Y−0.12..0.139450, Z2.68..2.826938 | Extract 28 triangles/end, refit width and translate, trim buried backs and cap; final 44 triangles/end. |
| Grille | Removed | Entire fascia uses the painted UV swatch. |
| Latch | Removed | Its former cell is an ordinary painted tailgate surface. |
| Roof | X ±1.26, Y1.92..2.17; front Z−0.80 at underside, −0.658491 at top; rear Z2.56 at underside, 2.523415 at top | Continue both A-post and D-post planes through roof thickness; top remains horizontal. |
| All pillar lateral spans | X ±[0.98,1.26] | Shared 0.28 lateral section; all outer faces on the wall plane; bases/roof junctions share edges, with no interior mating caps. |
| A pillar (each side) | Inner YZ (1.125,−1.25), (1.92,−0.80), (1.92,−0.55), (1.10,−1.00); outer front foot (0.994273,−1.323997), other outer endpoints equal inner | Outer foot extends down the same windscreen plane to the planar bevel. Side-window opening, glass and upper endpoints retained. |
| B pillar | Y 1.10..1.92, Z 0.12..0.37 | 0.25 section; begins t/2 rounded behind the origin, between shared seat rows. |
| C pillar | Y 1.10..1.92, Z 1.35..1.55 | 0.20=0.8t section; aligns with rear seatbacks near Z=1.357. |
| D pillar | YZ (1.10,2.48), (1.92,2.36), (1.92,2.56), (1.10,2.68) | 0.20=0.8t section; 0.12 rake toward vertical rear gate. |
| Belt rails | X ±[0.98,1.26], Y1.10, Z−1.00..2.68 | Joined wall top exists between posts; front side wall meets the A-post's sloping base directly. |
| Windshield | X ±0.98; bottom (Y,Z)=(1.125,−1.25), top (1.92,−0.80) | Lower edge raised 25 mm to meet cowl, with unchanged Z and top edge. |
| Rear pane | X ±0.98; bottom (1.10,2.68), top (1.92,2.56) | D-post inner edges; 8.326° from vertical. |
| Side front pane | Absolute X=1.256; bottom Z −1.00..0.12 at Y1.10; top Z −0.55..0.12 at Y1.92 | A-to-B aperture; common 0.004 side inset. |
| Side rear-row pane | Same X/Y; Z 0.37..1.35 | B-to-C aperture. |
| Side cargo pane (`mid1`) | Same X/Y; bottom Z 1.55..2.48; top Z 1.55..2.36 | C-to-D aperture; base/top lengths 0.93/0.81. |
| Headlight mesh transform | golf_headlights + (0,0,−0.215180) | All 40 triangles retained; no scaling; outward lens vertices clear fascia. |
| Taillight mesh transform | golf_taillights + (0,0,+0.332254) | All 20 triangles retained; no scaling; outward faces 2 mm past gate. |
| Spot / omni / tail anchors | Spots (±0.765,0.708,−2.803180), omni (0,0.841,−2.779180); tails (±0.765,0.787,2.756254) | Golf emitter anchors translated by the same deltas as their lenses. |
| Full-body collider | Size (2.52,2.44,5.776); centre (0,0.95,−0.061034) | Now encloses the entire body and donor bumpers, as requested; Z bounds rounded outward. |
| Roof fitted collider | Size (2.52,0.25,3.36); centre (0,2.045,0.88) | Existing cabin/roof registration retained; encloses the revised slanted roof slab. |

The full-body box replaces the historical lower-body-only box, which did not enclose the old body. Its effects on driving, suspension and impacts have not been evaluated. Static geometry and targeted runtime results are recorded in [WAGON_REPORT.md](WAGON_REPORT.md).
