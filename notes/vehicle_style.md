# Fleet style inputs

Authored before the replacement body. Fleet dimensions/counts/anchors come only from the retained `vehicle_measurements.md`; its old wagon rows are excluded. The only new geometry sampling is the sedan, hatchback and van greenhouse requested for this revision. Units: metres, +Y up, forward −Z.

## Complexity distribution

Population: all 20 road specs in the retained survey, including tank, APC and trailer. Counts mean OBJ v records / loaded triangles; percentiles use statistics.quantiles(method="exclusive").

| Vehicle | Vertices | Triangles |
| --- | --- | --- |
| ambulance | 642 | 436 |
| apc | 372 | 182 |
| bus | 1936 | 986 |
| firetruck | 570 | 406 |
| golf | 504 | 452 |
| hatchback | 408 | 346 |
| humvee | 481 | 278 |
| jeep | 364 | 232 |
| offroader | 574 | 434 |
| police | 537 | 372 |
| quad | 730 | 370 |
| roadster | 457 | 294 |
| sedan | 515 | 366 |
| semi | 817 | 404 |
| tank | 229 | 118 |
| tractor | 628 | 342 |
| trailer | 268 | 111 |
| truck | 530 | 390 |
| ural | 470 | 288 |
| van | 589 | 422 |

| Metric | Minimum | Q1 | Median | Q3 | Maximum |
| --- | --- | --- | --- | --- | --- |
| vertices | 229 | 420.25 | 522.5 | 618.25 | 1936 |
| triangles | 111 | 280.5 | 368.0 | 418.0 | 986 |


Vertex serialization varies: the source meshes duplicate positions at panel seams. Use face-normal/UV seams for the new OBJ position records and report unique positions separately; do not inflate the count with unused vertices.

## Greenhouse construction

All three roofs are thick top/underside skins in the body asset (no separate roof asset). Sedan and hatchback have A/B/C pillars per side and two open side apertures; van has A/B around the front aperture, a solid B-to-rear side panel, and a rear corner post. Their pillar longitudinal sections are 0.250 m and side shell X thickness is 0.250 m (outer |X|≈1.23096, inner |X|≈0.98096). Roof skin-to-underside thickness is 0.250 m. The belt is the top edge of the thick lower side wall; apertures are actual absent body faces, filled by independent glass meshes at |X|≈1.22697 (about 0.004 m inside the outer skin).

Body landmark examples, (Y,Z): sedan B edges (1.125,0.0797)/(1.125,0.3297), roof underside (1.9101,0.0797); hatch B edges (1.8759,0.7756)/(1.8759,1.0256); van B edges (1.125,−0.0186)/(1.125,0.2314), roof underside Y=1.875. These establish section dimensions; no coordinates are passed into the body generator.

Roof rise / height = (roof top Y − belt Y) / body AABB height. Slab / height = 0.25 / body height. Glasshouse length is the longitudinal envelope from windshield minimum Z to rear pane maximum Z (including the van solid cargo sides). Roof run is the top skin Z envelope. Tail slope is rear-pane inclination from vertical, using ΔZ/ΔY. These are mesh-local ratios, not loaded road heights.

| Body | Belt Y | Roof rise/H | Slab/H | Glasshouse L / L-body | Roof run | Roof crown ΔY | Tail slope | Front/rear overhang | Wheelbase/L | Ride Y−r |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| sedan | 1.125000 | 0.425345 | 0.102732 | 2.906854 / 0.488367 | 2.004629 | 0.035082 | 23.249° | 1.389812 / 1.562378 | 0.504016 | -0.350000 |
| hatchback | 1.005861 | 0.466805 | 0.104197 | 3.516813 / 0.637307 | 2.457724 | 0.120001 | 28.452° | 1.314326 / 1.403911 | 0.507408 | -0.350000 |
| van | 1.125000 | 0.416939 | 0.104235 | 3.654159 / 0.715050 | 3.267730 | 0.000001 | 0.000° | 1.117115 / 1.193243 | 0.547907 | -0.350000 |

| Body | Pane (opposite side symmetric) | Outline vertices | Triangles | Y span | Z span |
| --- | --- | --- | --- | --- | --- |
| sedan | l_front | 10 | 8 | 1.101569 .. 1.961569 | -1.154812 .. 0.125188 |
| sedan | l_rear | 7 | 5 | 1.101569 .. 1.961569 | 0.285188 .. 1.475188 |
| sedan | rear | 4 | 2 | 1.093874 .. 1.884039 | 1.280949 .. 1.620418 |
| sedan | windshield | 4 | 2 | 1.022629 .. 1.891451 | -1.286436 .. -0.902186 |
| hatchback | l_front | 9 | 7 | 1.001569 .. 1.921569 | -0.899326 .. 0.890674 |
| hatchback | l_rear | 9 | 7 | 1.001569 .. 1.921569 | 0.990674 .. 2.270674 |
| hatchback | rear | 4 | 2 | 0.975451 .. 1.757954 | 2.032486 .. 2.456502 |
| hatchback | windshield | 4 | 2 | 0.935186 .. 1.761317 | -1.060311 .. -0.591262 |
| van | l_front | 6 | 4 | 1.071568 .. 1.901568 | -1.262115 .. 0.017885 |
| van | rear | 4 | 2 | 1.086568 .. 1.916568 | 2.317885 .. 2.317885 |
| van | windshield | 4 | 2 | 1.013027 .. 1.906694 | -1.336274 .. -1.014001 |


The many pane outline vertices include near-collinear points from the glass extraction tool; they are not extra pillars. A new four-corner planar aperture can therefore use two triangles without discarding a styling feature.

## Proportions across the road population

| Vehicle | Wheelbase/L | Front track/W | Front overhang | Rear overhang | Front/rear ratio | Front ride Y−r |
| --- | --- | --- | --- | --- | --- | --- |
| ambulance | 0.523047 | 1.014863 | 1.253591 | 1.299655 | 0.964557 | -0.350000 |
| apc | 0.641707 | 1.092316 | 1.595554 | 1.168241 | 1.365775 | -0.640000 |
| bus | 0.508682 | 0.964505 | 2.301398 | 1.764894 | 1.303987 | -0.520000 |
| firetruck | 0.628922 | 1.030891 | 1.267231 | 1.429168 | 0.886691 | -0.350000 |
| golf | 0.573773 | 1.030887 | 1.049350 | 1.179203 | 0.889881 | -0.350000 |
| hatchback | 0.507408 | 1.030888 | 1.314326 | 1.403911 | 0.936189 | -0.350000 |
| humvee | 0.522353 | 1.030887 | 1.242115 | 1.318243 | 0.942250 | -0.350000 |
| jeep | 0.547907 | 1.030887 | 1.117115 | 1.193243 | 0.936201 | -0.350000 |
| offroader | 0.547907 | 1.030887 | 1.117115 | 1.193243 | 0.936201 | -0.350000 |
| police | 0.504016 | 1.030889 | 1.409812 | 1.542378 | 0.914051 | -0.350000 |
| quad | 0.600409 | 0.637867 | 0.676332 | 0.541589 | 1.248792 | -0.250000 |
| roadster | 0.504016 | 1.030889 | 1.389812 | 1.562378 | 0.889549 | -0.375000 |
| sedan | 0.504016 | 1.030889 | 1.389812 | 1.562378 | 0.889549 | -0.350000 |
| semi | 0.751943 | 0.917049 | 0.955001 | 0.800000 | 1.193751 | -0.100000 |
| tank | 0.634709 | 0.693234 | 1.726577 | 1.726576 | 1.000001 | -0.184000 |
| tractor | 0.553142 | 0.692518 | 1.205003 | 1.141003 | 1.056091 | -0.450000 |
| trailer | 0.099379 | 0.866667 | 13.400001 | 1.100000 | 12.181819 | -0.100000 |
| truck | 0.547907 | 1.030887 | 1.117115 | 1.193243 | 0.936201 | -0.350000 |
| ural | 0.683775 | 1.030890 | 0.947115 | 1.143243 | 0.828446 | -0.350000 |
| van | 0.547907 | 1.030887 | 1.117115 | 1.193243 | 0.936201 | -0.350000 |


The trailer wheelbase describes its rear bogie, and tank length includes its barrel; neither is an appropriate passenger-car axle template. Use the three enclosed car/van specimens for cabin and axle ratios, while using the whole road distribution for overall size and mesh budget.

## Design targets fixed before authoring

- Length 5.80: between road median 5.735214 and sedan 5.952190.
- Width 2.52 and height 2.44: rounded road medians 2.522099 and 2.446013.
- Wheelbase 3.02: 5.80 × mean(sedan, hatchback, van wheelbase/L) = 3.014707, rounded to 0.02 m grid.
- Track 2.60: road per-axle median; track/width 1.031746 matches the sampled ≈1.030889.
- Front/rear overhang 1.34/1.44: front/rear 0.930556 inside the sample 0.889549–0.936201.
- Body min Y −0.27, axle Y 0.25, tyre radius 0.60: rounded dominant lower bound and modal ride −0.35.
- Belt Y 1.10, roof underside 1.92, top 2.17: belt inside 1.005861–1.125; slab 0.25; roof rise/H 0.438525 inside the sample.
- Greenhouse Z −1.25..2.68: length fraction 0.677586, between hatchback and van.
- Roof Z −0.80..2.56: flat run 3.36, length fraction 0.579310, between hatchback and van roof-run fractions.
- Three side apertures separated by four pillars per side; rear pane ΔZ=0.12 over ΔY=0.82 (8.326° from vertical).
- Two existing seat rows retained; a new unobstructed load floor behind the rear seatbacks.
- Aim near the 368-triangle median, with both counts within the central road spread; every lower and upper panel authored anew.
