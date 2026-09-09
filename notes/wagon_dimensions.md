# Original wagon construction coordinates

Metres; +Y up, forward −Z. This replaces the old sedan-extension derivation. The unchanged `vehicle_measurements.md` still contains a historical copy of that old derivation. Fleet references and the reasoning for the main dimensions are in [WAGON_REPORT.md](WAGON_REPORT.md) and the pre-authoring [vehicle_style.md](vehicle_style.md).

Let **t=0.25 m**, the original sampled longitudinal pillar/roof section. Revision 3 widens the lateral wall/post span to 0.28 m so every outer face is flush at X = ±1.26. Decimal rounding to 0.01–0.02 m is deliberate. Panel-detail choices below are subdivisions of that section and the chosen envelope, not additional empirical fleet statistics.

| Part | Coordinates / construction | Source / rule |
| --- | --- | --- |
| Body envelope | X ±1.26, Y −0.27..2.17, Z ±2.90 | Rounded road dimensional medians; length between road median and sedan. |
| Side shell ends | Hood nose Z −2.757828, rear gate Z 2.68; outer X ±1.26 throughout, including both bumper tips at Z ±2.90 | Constant 2.520 m plan width; front nose retains the measured sedan overhang scale. |
| Side wall | Continuous sill at Y −0.12 through both axles; outer skin at X ±1.26 rises to Y1.00, then to rail Y1.10; inner cabin X ±0.98 | Joined shell with no arch openings, recess returns or interior backing solids. |
| Axles | (±1.30,0.25,−1.56), (±1.30,0.25,1.46); front steering | 3.02 wheelbase from three-specimen mean ratio, 2.60 median track, modal ride −0.35. |
| Underfloor | X ±0.99, Y −0.27, Z −2.757828..2.90; bevels join the outer sill. Passenger floor X ±0.98, Y −0.12, Z −1.25..1.36 | One connected underside and cabin floor; no overlapping boxes or sill gaps. |
| Load floor | X ±0.98, Y0.18, Z1.36..2.53; vertical step from Y−0.12 at Z1.36 | Retains start after seatback maximum Z1.357307 and clear run 1.17 to gate. |
| Sedan hood | Across X ±1.26, (Y,Z): (0.999953,−2.757828) → (1.125,−1.406856), then level to Z −1.25; the 0.875 and 1.000 stations remain as flat side-wall seams | Source mesh profile, longitudinal scale 0.964159181; see measured comparison in WAGON_REPORT.md. |
| Rear gate lower panel | Inner Z2.53, outer Z2.68; top Y1.10; inner lower edge Y0.18; exterior joins bumper at Y0.16 | Full width X ±1.26; joined directly to deck, side walls and bumper; no sedan boot-lid recess. |
| Bumpers | Front X ±1.26, Y−0.158648..0.100803, tip Z−2.90; rear X ±1.26, Y−0.12..0.16, Z2.76..2.90 | Sedan front height levels; rear Y/Z silhouette retained with full width. Both join the shell. |
| Grille | X ±0.48, Y0.36..0.54, on the sloping front fascia (Z approximately −2.7831..−2.7760) | Dark material patch partitioned into the fascia; no overlapping backing face. |
| Latch | X ±0.23, Y 0.89..0.96, Z 2.68..2.70 | Width ≈2t; height≈t/4; projection≈t/12. Below rear glass. |
| Roof | X ±1.26, Y 1.92..2.17; front Z −0.80 at underside, −0.658491 at top; rear Z2.56 | Front face continues the unchanged windscreen/A-post plane; top stays flat. |
| All pillar lateral spans | X ±[0.98,1.26] | Shared 0.28 lateral section; all outer faces on the wall plane; bases/roof junctions share edges, with no interior mating caps. |
| A pillar (each side) | YZ (1.125,−1.25), (1.92,−0.80), (1.92,−0.55), (1.10,−1.00) | Front foot raised 25 mm for sedan cowl; side-window opening and upper endpoints retained. |
| B pillar | Y 1.10..1.92, Z 0.12..0.37 | 0.25 section; begins t/2 rounded behind the origin, between shared seat rows. |
| C pillar | Y 1.10..1.92, Z 1.35..1.55 | 0.20=0.8t section; aligns with rear seatbacks near Z=1.357. |
| D pillar | YZ (1.10,2.48), (1.92,2.36), (1.92,2.56), (1.10,2.68) | 0.20=0.8t section; 0.12 rake toward vertical rear gate. |
| Belt rails | X ±[0.98,1.26], Y1.10, Z−1.00..2.68; front transition reaches Y1.125 at Z−1.25 | Joined wall top exists between posts; removes buried overlapping rail solids. |
| Windshield | X ±0.98; bottom (Y,Z)=(1.125,−1.25), top (1.92,−0.80) | Lower edge raised 25 mm to meet cowl, with unchanged Z and top edge. |
| Rear pane | X ±0.98; bottom (1.10,2.68), top (1.92,2.56) | D-post inner edges; 8.326° from vertical. |
| Side front pane | Absolute X=1.256; bottom Z −1.00..0.12 at Y1.10; top Z −0.55..0.12 at Y1.92 | A-to-B aperture; common 0.004 side inset. |
| Side rear-row pane | Same X/Y; Z 0.37..1.35 | B-to-C aperture. |
| Side cargo pane (`mid1`) | Same X/Y; bottom Z 1.55..2.48; top Z 1.55..2.36 | C-to-D aperture; base/top lengths 0.93/0.81. |
| Headlight mesh transform | sedan_headlights + (0,0,0.150) | Unchanged accepted lamp coordinates; all outward lens faces clear the revised fascia. |
| Taillight mesh transform | sedan_taillights + (0,0,0.012) | Unchanged accepted lamp coordinates; all outward lens faces clear the gate. |
| Spot / omni / tail anchors | Spots (±0.765,0.708,−2.819), omni (0,0.841,−2.795); tails (±0.979,0.688,2.853) | Exactly the same translations as each shared lens mesh. |
| Lower fitted collider | Size (2.50,0.98,5.52); centre (0,0.59,0) | Accepted fitted box retained: X inset 0.01 each side, upper Y1.08, Z extent ±2.76. Height 0.98 lies between sampled main-box heights 0.916/1.046. |
| Roof fitted collider | Size (2.52,0.25,3.36); centre (0,2.045,0.88) | Roof AABB; box includes the small wedge ahead of the slanted front face. |

The main fitted box is intentionally lower-body-only, as on the sampled fleet. It does not enclose the full body: shortfalls are X 0.01 on each side, Y− 0.37, Y+ 1.09, and Z 0.14 on each end. Roof registration adds the roof slab. Runtime convex decomposition can replace these fitted boxes for active physics; see the verification limits in the report.
