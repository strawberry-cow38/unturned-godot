# Original station wagon: measured style and implementation

The road fleet's body budget is **229–1,936 OBJ vertices / 111–986 triangles**, with medians **522.5 / 368**. Its common enclosed-body vocabulary is **0.25 m thick pillars and roof skins, open window apertures, a belt near Y=1.01–1.13 m, and a roof rising 0.417–0.467 of body height above that belt**. The replacement wagon was authored from these proportions. Its **648 vertices / 380 triangles** describe new lower panels, arches, bonnet, roof, pillars and tailgate.

All dimensions below are metres, in the files' existing frame: **forward −Z, +Y up**. Body height excludes wheels. [vehicle_measurements.md](vehicle_measurements.md) is unchanged and was not regenerated. Its old wagon entries and embedded sedan-extension discussion are historical; this report and [wagon_dimensions.md](wagon_dimensions.md) supersede those wagon-only values. The current brief replaces the old sedan-extension/ambulance-ceiling brief.

## Style analysis, established before authoring

[vehicle_style.md](vehicle_style.md) contains the complete 20-road-vehicle count distribution, derived axle/overhang ratios for every road vehicle, selected body landmarks, and per-pane counts and extents. `tools/analyze_vehicle_style.py` calculates fleet statistics from the **retained table**, then reads only sedan/hatchback/van geometry for the additional greenhouse analysis. It does not rerun the fleet survey.

| Body complexity | Minimum | Q1 | Median | Q3 | Maximum | New wagon |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| OBJ `v` records | 229, tank | 420.25 | 522.5 | 618.25 | 1,936, bus | **648** |
| Loaded triangles | 111, trailer | 280.5 | 368 | 418 | 986, bus | **380** |

Population includes the 20 baseline road specs, including APC, tank and trailer; wagon, aircraft and boats are excluded. Quartiles use Python's exclusive method. The wagon is inside both distributions: its triangles are in the middle 50%; its vertices are 29.75 above Q3, with quad (730), semi (817) and bus (1,936) above it. The body has **232 unique positions**. Its OBJ position records split at flat-normal/UV seams, all are referenced, and none were added to pad the count. OBJ vertex count is sensitive to exporter serialization; it is not the expanded GPU buffer size.

The three enclosed bodies share measurable construction conventions:

- Sedan and hatchback have **three pillars per side (A/B/C), two open side apertures per side, and six panes total**. The van has **one front side aperture per side, a solid cargo side, a rear corner post, and four panes total**. Its rear cargo volume is not a second side-window aperture.
- Pillar longitudinal sections and lateral wall sections measure **0.250 m**. Outer side skins are near |X|=1.23096, inner skins near 0.98096. The independently attached side glass is about **0.004 m inside** the outer skin. Window apertures are missing body faces, with thick edge returns; glass is not painted onto a solid box.
- Each roof has a **top skin and an underside 0.250 m apart inside the body asset**. No separate roof asset is referenced. Sedan and hatchback use several longitudinal roof facets; their top-skin height variations are **0.035082 / 0.120001 m**. The van roof is flat within **0.000001 m**. Thus a thick flat roof belongs to the sampled vocabulary; making every roof a thin flat plane would not.
- The belt is the upper edge of the thick lower side wall. Sedan/van belt Y is **1.125**, hatchback **1.005861**. Sedan B-pillar Z edges are **0.0797/0.3297**, hatchback **0.7756/1.0256**, van **−0.0186/0.2314**: each is a **0.25 m** section. These landmarks establish dimensions, not copied panel coordinates.
- The extracted side glass outlines have **10/7 vertices** for sedan front/rear, **9/9** for hatchback, and **6** for van front; windshield and rear panes each have **4**. Near-collinear outline points explain the larger side counts. The new planar apertures use four corners/two triangles each.

Definitions: roof rise/H = (roof-top Y − belt Y)/body AABB height; slab/H = roof thickness/body height. Glasshouse length is the envelope from windshield minimum Z to rear-glass maximum Z, including the van's solid cargo sides. Roof run is the top-skin Z envelope. Tail slope is rear-pane inclination from vertical. Ride is **anchor Y − radius**, not loaded ground clearance.

| Relationship | Sedan | Hatchback | Van | Wagon |
| --- | ---: | ---: | ---: | ---: |
| Roof rise/H | 0.425345 | 0.466805 | 0.416939 | **0.438525** |
| Roof slab/H | 0.102732 | 0.104197 | 0.104235 | **0.102459** |
| Glasshouse length/body length | 0.488367 | 0.637307 | 0.715050 | **0.677586** |
| Roof run/body length | 0.336788 | 0.445382 | 0.639433 | **0.579310** |
| Wheelbase/body length | 0.504016 | 0.507408 | 0.547907 | **0.520690** |
| Front/rear overhang ratio | 0.889549 | 0.936189 | 0.936201 | **0.930556** |
| Anchor Y − radius | −0.350000 | −0.350000 | −0.350000 | **−0.350000** |
| Rear pane slope from vertical | 23.249° | 28.452° | 0.000° | **8.326°** |

The complete road table is useful for the budget and dimensional medians, but its tank barrel and trailer rear bogie make the extremes unsuitable passenger-car axle templates. The three enclosed specimens supply the cabin and axle proportions. These are observed ranges, not a claim that all road vehicles have one universal silhouette.

## Wagon dimensions and their sources

| Dimension | Authored value | Fleet statistic or construction rule |
| --- | --- | --- |
| Body length | **5.80**, Z −2.90..2.90 | Between road median **5.735214** and sedan **5.952190**; no source panel stretched. |
| Body width | **2.52**, X −1.26..1.26 | Road median **2.522099**, rounded to 0.01 m. |
| Body height | **2.44**, Y −0.27..2.17 | Road median **2.446013**, rounded to a 0.02 m grid. Minimum Y rounds the dominant **−0.273431/−0.273432** lower bound. |
| Wheelbase | **3.02** | 5.80 × mean of sedan/hatchback/van wheelbase-length ratios = **3.014707**, rounded to 0.02 m. Road median wheelbase is **2.952**. |
| Front/rear overhang | **1.34 / 1.44** | Sum = length − wheelbase = **2.78**; ratio **0.930556** is inside sampled **0.889549–0.936201**. Front axle Z **−1.56**, rear **1.46** follow from those overhangs. |
| Front and rear track | **2.60** | Road per-axle median **2.60**; track/width **1.031746**, close to the three bodies' **1.030889**. Anchors are |X|=1.30, **0.04** outboard of body sides, matching the fleet relationship. |
| Wheel radius / axle Y | **0.60 / 0.25** | Same sedan wheel asset/radius and modal enclosed-car ride **−0.35**. With shared rest drop **0.25**, static preview wheel centres are at Y=0.00 and tyre bottoms −0.60. Suspension travel is not a clearance guarantee. |
| Belt / shoulder Y | **1.10 / 1.00** | Belt lies inside sampled **1.005861–1.125**; **0.10** shoulder bevel is 0.4 × the shared 0.25 section. |
| Roof underside / top Y | **1.92 / 2.17** | Top follows total height and minimum Y; subtract the sampled **0.25** roof section. Roof rise/H **0.438525** lies within **0.416939–0.466805**. |
| Roof width / run | **2.46 / 3.36**, Z **−0.80..2.56** | Width rounds sampled outer roof span **≈2.46192**. Run/body length **0.579310** lies between hatchback **0.445382** and van **0.639433**. Flatness uses the van roof vocabulary. |
| Glasshouse envelope | **3.93**, Z **−1.25..2.68** | Fraction **0.677586** lies between hatchback **0.637307** and van **0.715050**. Front bonnet allocation **1.65/5.80=0.284483** remains near sedan **0.289536**, above van **0.231068**. |
| Glazing vertical span / side X | Y **1.10..1.92**, |X|=**1.226** | Span is between belt and roof underside; side glass is **0.004** inside the 1.23 outer pillar face, matching the sampled inset. |
| A/B/C/D sections | A/B **0.25**, C/D **0.20** in Z; **0.25** in X | A/B use sampled section; cargo posts use **0.8 × 0.25**, an authored variation to preserve the third aperture. See construction coordinates below. |
| Tailgate slope | **0.12** rearward run over **0.82** rise | **8.326°** is between van vertical and hatchback **28.452°**, toward the vertical end for a wagon tail. Run is about half the shared 0.25 section. |
| Seat rows | 4 seats; Z **−0.625 / 0.772**, Y **−0.079**, X **±0.50** | Retain the shared sedan/police two-row interior and body pose. Seats are shared parts, independent of body design. |
| Load area | Floor Y **0.18**, begins Z **1.36**; clear run **1.17** to inner tailgate Z **2.53** | Retained rear seat mesh ends at **1.357307**; floor begins after it. The new tailgate is **0.15** thick (0.6 × common section). A clear width **1.88** fits inside tapered side returns. No third seat row. |

These choices define the **body AABB X [−1.26,1.26], Y [−0.27,2.17], Z [−2.90,2.90]**. The shared wheels are allowed to extend beyond it as on the fleet; it is not the complete assembled-vehicle envelope. Finer dimensions are deliberate subdivisions of the shared section, not independent fleet measurements. [wagon_dimensions.md](wagon_dimensions.md) records every panel, aperture, lamp transform and fitted box for review.

## How it differs from hatchback and van

| Feature | Hatchback | Original wagon | Van |
| --- | --- | --- | --- |
| Length | 5.518237 | **5.80** | 5.110358 |
| Width / body height | 2.522097 / 2.399293 | **2.52 / 2.44** | 2.522099 / 2.398433 |
| Roof run / height variation | 2.457724 / 0.120001 | **3.36 / 0.00** | 3.267730 / 0.000001 |
| Roof run as length fraction | 0.445382 | **0.579310** | 0.639433 |
| Rear pane inclination | 28.452° | **8.326°** | 0.000° |
| Side glass apertures per side | 2 | **3: front row, rear row, load area** | 1; cargo sides solid |
| Seats | 4; rear row Z 1.240 | **4; rear row Z 0.772, deck behind seatbacks** | 5; rear-most seat Z 1.707 |
| Front/rear overhang | 1.314326 / 1.403911 | **1.34 / 1.44** | 1.117115 / 1.193243 |

The wagon adds **0.902276 m of flat roof run** over the hatchback, reduces tail slope by **20.126°**, and dedicates a separately glazed load area behind the second row. Against the van, it has a lower roof-run fraction, a longer bonnet allocation, and three open side apertures instead of a solid cargo wall. It remains in the same height/width class; making it much taller would undermine that distinction. These are geometric differences, not a claim that its rendered appearance is correct.

## Original authorship, shared parts and registration

`tools/build_wagon.py` no longer loads any source body or spec/axle array. It constructs the side walls, four-edge arch openings, wedge bonnet, underfloor, raised load floor, belt rails, eight pillar sections, flat roof and rear gate from new coordinates. It neither clips nor transforms sedan panels. The verifier also finds **zero matching complete triangles** between the saved wagon and sedan bodies.

The palette stays byte-identical to `sedan_palette.png`. Painted faces use OBJ UV **(0.125,0.75)**, loaded as **(0.125,0.25)**; fixed dark faces use **(0.375,0.25)**, loaded as **(0.375,0.75)**. All faces are explicit `f v/vt/vn` triangles with normals calculated from the float32 positions the loader consumes. The eight glass assets retain the existing tint **(0.62,0.73,0.78,0.26)** and independent breakable-pane labels.

`Wheel=sedan_wheel.txt`, `WheelTex=jeep_wheel_albedo.png`: both references exactly match the sedan. Seat and steering meshes, seat registrations, driving coefficients, capacities, sounds and colours remain shared as before. Mass remains **1,650 kg**, midway between retained sedan **1,500** and police **1,800**; fuel **50 L**, Spec.Health **600**, engine coefficient **700**, SpeedMax **16.5 m/s**. These are configured choices, not measured driving performance.

The existing lens meshes remain shared by translation: new `wagon_headlights.txt` is sedan headlights **+0.150 Z**, and wagon taillights are sedan taillights **+0.012 Z**, with matching light-anchor translations. Those deltas place outward lens faces beyond the new fascia/side-wall ends. This revision needed its own front-lens asset because the new nose has different dimensions.

Registration is preserved in `_wagon`, `BuildWagon`, `BuildByName`, `SpecNames`, `SpecFor`, `SeatTable`, `SeatOf`, `HandTunedSeatOf`, `RoofBox` and existing pane labels. **Wagon remains TypeId 32; all previous 32 IDs retain their order.** DevConsole uses the existing catalogue (`vehicle wagon` or `veh wagon`). MP factories resolve the wagon body. WorldBuilder still treats it as command-only, like Golf. `VehicleWagonTests.cs` is retained; only its body-size and fitted-box expectations change.

## Verification and limitations

| Check | Result |
| --- | --- |
| `dotnet build game/UnturnedGodot.csproj` | PASS, **0 errors / 22 warnings**. Final build after lamp-anchor correction: **1 min 11.43 s**. |
| `python3 tools/verify_wagon.py` | PASS for **11 OBJ assets**: triangles only, required UV/normal fields, indices in range, finite float32 coordinates, valid UVs, unit normals aligned to winding, no unused vertices or duplicate/degenerate triangles. |
| AABB / minimum triangle | Claimed bounds agree within **0.000001 m**. Smallest body triangle **0.000699999261 m²**. |
| Aperture/load/lamp probes | **64** glass-interior probes and **9** vertical load-area probes encounter no body face. **4 outward faces per lamp mesh** have clear paths past the body. These are static sample checks, not an exhaustive intersection proof. |
| Shared assets and integration | Exact wheel/texture/seat references, palette bytes, changed axle positions, lamp shifts, fitted boxes, glass labels and prior TypeIds verified. |
| `./test.sh --l1 --only 'vehicle.wagon*'` | PASS, **1 test / 13 checks**: real and replica factories, actual ParseObj resource/AABB, four wheels, four seats, cabin, eight panes, capacities and mass. Final run: **0.99 s**, Godot 4.6 in headless mode. |
| Geometry preview | [wagon_geometry.png](wagon_geometry.png) regenerated and inspected: fleet side views and three wagon views. Matplotlib geometry inspection with approximate paint/lighting, **not a Godot render**. |
| Reproducibility | Generator outputs checked byte-for-byte after a second run; retained measurement notes checked against `f9970da8`. |

**Not verified:** final appearance in Godot, interactive driving, suspension through its travel, road/contact behavior, occupant clipping in play, nighttime lighting, glass damage, or a live multiplayer session. The actual render of the rejected body (`wagon_godot.png`) was removed to avoid presenting it as evidence for this mesh. The headless factory test exercises assembly, not visual judgment. The final headless run generated **7 convex hulls** and disabled **2 fitted boxes** for physics (retaining them for look-focus); the fitted boxes documented in the dimension sheet are not a complete description of active collision. The existing MP puppet path omits the separate glass/roof attachments; this shared behavior remains outside this body task.

Reproduce without resurveying the fleet:

```sh
python3 tools/analyze_vehicle_style.py
python3 tools/build_wagon.py
python3 tools/verify_wagon.py
dotnet build game/UnturnedGodot.csproj
./test.sh --l1 --only 'vehicle.wagon*'
python3 tools/preview_wagon.py
```

The analysis/build/verifier use the Python standard library. Preview additionally uses numpy, matplotlib and Pillow. Changes are committed on `astra-wagon`; nothing is pushed. Pre-existing tracked build-artifact edits are preserved and excluded from the commit.
