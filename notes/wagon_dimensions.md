# Original wagon construction coordinates

Metres; +Y up, forward −Z. This replaces the old sedan-extension derivation. The unchanged `vehicle_measurements.md` still contains a historical copy of that old derivation. Fleet references and the reasoning for the main dimensions are in [WAGON_REPORT.md](WAGON_REPORT.md) and the pre-authoring [vehicle_style.md](vehicle_style.md).

Let **t=0.25 m**, the sampled pillar/wall/roof section. Decimal rounding to 0.01–0.02 m is deliberate. Panel-detail choices below are subdivisions of that section and the chosen envelope, not additional empirical fleet statistics.

| Part | Coordinates / construction | Source / rule |
| --- | --- | --- |
| Body envelope | X ±1.26, Y −0.27..2.17, Z ±2.90 | Rounded road dimensional medians; length between road median and sedan. |
| Side shell ends | Z ±2.76; straight outer X ±1.26 until |Z|=2.32, taper to ±1.16 at ends | Bumper depth 0.14≈0.55t; end taper 0.10=0.4t; taper starts 0.44≈1.75t before ends. |
| Side wall | Outer lower contour to Y=1.00; inner X = outer X minus t, up to Y=1.10 | Shared t-thick shell with 0.10 shoulder bevel. |
| Four-edge arch at each axle | Relative (Z,Y): (−0.70,−0.12), (−0.50,0.50), (0,0.72), (0.50,0.50), (0.70,−0.12) | Shared wheel r=0.60: longitudinal radius +0.10 bevel; peak r+0.12≈t/2 above rest-centred wheel. Side shoulders at 2t; bottom −t/2 rounded. Static rest clearance only. |
| Axles | (±1.30,0.25,−1.56), (±1.30,0.25,1.46); front steering | 3.02 wheelbase from three-specimen mean ratio, 2.60 median track, modal ride −0.35. |
| Underfloor | X ±0.99, Y −0.27..−0.12, Z −2.58..2.62 | Inboard of side returns by 0.02; thickness 0.15=0.6t; longitudinal insets ≈t beyond end structure. |
| Load floor | X ±0.99, Y −0.12..0.18, Z 1.36..2.65 | Start after actual shared seatback maximum 1.357307; floor thickness 0.30=1.2t. Back terminates inside gate. Clear run to gate inner face 2.53 is 1.17. |
| Bonnet prism | X ±1.01; YZ corners (−0.12,−2.76), (0.91,−2.76), (1.10,−1.25), (−0.12,−1.25) | Width = body half-width−t. Rear meets belt and greenhouse envelope; front top is 0.19≈0.75t lower, lower face meets floor/sill Y−0.12. |
| Rear gate lower panel | X ±1.01, Y 0.18..1.10, Z 2.53..2.68 | Meets deck and belt; thickness 0.15=0.6t. |
| Bumpers | X ±1.16, Y −0.12..0.16; Z −2.90..−2.76 and 2.76..2.90 | End taper width; height 0.28≈1.125t, depth 0.14≈0.55t. Define length endpoints. |
| Grille | X ±0.48, Y 0.36..0.54, Z −2.79..−2.76 | Width ≈4t; height ≈0.75t; projection 0.03≈t/8. Below the lenses. |
| Latch | X ±0.23, Y 0.89..0.96, Z 2.68..2.70 | Width ≈2t; height≈t/4; projection≈t/12. Below rear glass. |
| Roof | X ±1.23, Y 1.92..2.17, Z −0.80..2.56 | Sampled rounded roof span, t thickness; roof-run fraction between hatch and van. |
| All pillar lateral spans | X ±[0.98,1.23] | Shared t section. Ends meet roof/rail; hidden mating caps omitted. |
| A pillar (each side) | YZ (1.10,−1.25), (1.92,−0.80), (1.92,−0.55), (1.10,−1.00) | 0.25 section; 0.45 rake over 0.82 height between selected greenhouse/roof endpoints. |
| B pillar | Y 1.10..1.92, Z 0.12..0.37 | 0.25 section; begins t/2 rounded behind the origin, between shared seat rows. |
| C pillar | Y 1.10..1.92, Z 1.35..1.55 | 0.20=0.8t section; aligns with rear seatbacks near Z=1.357. |
| D pillar | YZ (1.10,2.48), (1.92,2.36), (1.92,2.56), (1.10,2.68) | 0.20=0.8t section; 0.12 rake toward vertical rear gate. |
| Belt rails | X ±[0.98,1.23], Y 0.98..1.10, Z −1.25..2.68 | t-wide rail, t/2 thickness rounded; joins the greenhouse endpoints. |
| Windshield | X ±0.98; bottom (Y,Z)=(1.10,−1.25), top (1.92,−0.80) | A-post inner edges; 0.45/0.82 rake. |
| Rear pane | X ±0.98; bottom (1.10,2.68), top (1.92,2.56) | D-post inner edges; 8.326° from vertical. |
| Side front pane | |X|=1.226; bottom Z −1.00..0.12 at Y1.10; top Z −0.55..0.12 at Y1.92 | A-to-B aperture; common 0.004 side inset. |
| Side rear-row pane | Same X/Y; Z 0.37..1.35 | B-to-C aperture. |
| Side cargo pane (`mid1`) | Same X/Y; bottom Z 1.55..2.48; top Z 1.55..2.36 | C-to-D aperture; base/top lengths 0.93/0.81. |
| Headlight mesh transform | sedan_headlights + (0,0,0.150) | Minimum lens Z becomes −2.789950, outside fascia Z −2.76 by 0.029950≈t/8. |
| Taillight mesh transform | sedan_taillights + (0,0,0.012) | Maximum lens Z becomes 2.800012, outside side-wall end Z 2.76 by 0.040012≈t/6. |
| Spot / omni / tail anchors | Spots (±0.765,0.708,−2.819), omni (0,0.841,−2.795); tails (±0.979,0.688,2.853) | Exactly the same translations as each shared lens mesh. |
| Lower fitted collider | Size (2.50,0.98,5.52); centre (0,0.59,0) | X inset 0.01 each side; upper Y1.08 just below belt; Z matches shell ends ±2.76. Height 0.98 lies between sampled main-box heights 0.916/1.046. |
| Roof fitted collider | Size (2.46,0.25,3.36); centre (0,2.045,0.88) | Exact roof slab bounds. |

The main fitted box is intentionally lower-body-only, as on the sampled fleet. It does not enclose the full body: shortfalls are X 0.01 on each side, Y− 0.37, Y+ 1.09, and Z 0.14 on each end. Roof registration adds the roof slab. Runtime convex decomposition can replace these fitted boxes for active physics; see the verification limits in the report.
