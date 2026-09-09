# Car trailer — measured against this repository

## Measurements first

Based on `1f7f106d`, branch `astra-cartrailer`. I reread [vehicle_measurements.md](vehicle_measurements.md), retained its fleet dimension tables rather than regenerating them, and added [car_trailer_measurements.md](car_trailer_measurements.md). That supplement contains **all 21 existing road rear ends**, including the semi trailer, with body/Parts rear Z, the centreline rear surface's Y interval, candidate mounting Y, nominal ground coordinate, tracks and effective wheel radii.

| Reference | Number taken from the repository | Use |
| --- | --- | --- |
| Golf body, saved fleet table / `golf_body.txt` | 5.228553 long × 2.522099 wide | Half-length deck; half-width contributes to tongue clearance |
| Golf / hatchback / sedan / SUV / jeep / offroader / truck / van wheel anchors | X ±1.300000; track 2.600000 | Trailer wheel centres follow their tracks |
| Ordinary car radii / rear mount Y | Radius 0.600000; wheel anchor Y 0.250000 | Nominal rear ground is −0.600000 with 0.250000 suspension rest drop |
| `quad_wheel.txt` / `_quad` | Radius 0.450000, width 0.400004; mass 300 kg; health 450 | Native smaller wheels, body mass and health |
| Golf rear centreline surface | Z 2.483066, Y −0.158646..0.100804 | Hitch mounting Y is the midpoint, −0.028921 |
| Golf complete original rear | Z 2.559203 | Hitch Z = 2.559203 + 0.150000 = 2.709203; it clears even the off-centre rear extremity |
| Current SUV | Body rear Z 2.680000; separate bumper Z 2.826938 | The old wagon row's 3.192378 rear is obsolete; use the separate bumper |
| `trailer_0.txt` / full `_trailer` spec | 3.000000 × 2.499998 × 16.100001; 268 v / 111 tris; mass 6000; Kingpin (0,.62,−6.6); BoxSize (3,1.10,12.35); `jeep_wheel.txt` | Existing coupling/landing-gear/passive-wheel pattern; no semi-scale geometry copied |

The radius premise needed correcting: **quad uses 0.450**, semi and semi trailer effectively use **0.650** through `WheelRadii` overrides, APC/tank use **0.740**, and tractor uses **0.900/1.050**. Ordinary cars use 0.600. Boats' unused 0.300 values are not smaller road wheels. The fresh wheel mesh is the quad's actual native asset, without a mismatched radius-only shrink.

Coordinate confirmation comes from the real Golf: headlights occupy Z −2.565942..−2.440543, taillights +2.224357..+2.349755. Body coordinates are **X lateral, +Y up, −Z forward**, unchanged by ParseObj. No gun-frame transform is used.

All “above ground” heights below mean **nominal uncompressed/rest suspension geometry**: ground = rear wheel anchor Y − effective radius − rest length. Loaded springs, road pitch and terrain were not measured. The table does not invent an observed driving ride height.

## Dimensions and their derivations

The fractions below are explicit design choices applied to measured donors, not claims that the fleet already contains a car trailer. Let `R = 0.600` (car), `r = 0.450` (quad), and `t = (R−r)/3 = 0.050000 m` (the construction module). Generated coordinates are rounded to six decimals; half the Golf's length is 2.6142765 before rounding. The saved deck endpoints give 2.614276 m; the difference is sub-micrometre rounding.

| Feature | Authored value | Traceable rule |
| --- | --- | --- |
| Deck outer length | 2.614276 m saved | Golf body length / 2; under half the sedan and far below the semi's 16.100001 |
| Deck outer width | 2.099996 m | Car track − native quad tyre width − 2t = 2.6 − .400004 − .1 |
| Deck Z range | −1.307138..+1.307138 | Centred half-Golf deck |
| Deck finished local Y / nominal height | .250000 / .850000 m | Golf wheel mount Y; add the common .600000 origin-to-ground distance |
| Deck total thickness | .087500 m | 2t less t/4, so the slab's top sits under the sideboards' base rather than exactly on it |
| Timber floor | One slab, full deck width | Matches the truck's own bed, whose floor is a single 1.616 m2 quad in two triangles with no board lines. Nine modelled boards cost 72 tris to say what a flat surface and a palette colour already say |
| Sideboards | .450000 total height above deck; top Y .700000 | Quad radius r, reached by the board itself. The separate capping rail and the six stake posts are gone: 96 tris of trim at a scale nothing else in the fleet models |
| End boards / tailgate | Width 1.999996, thickness .050000; inset .010000 from deck ends | Deck width − 2t; t; t/5. Fixed in gameplay; the hinge blocks and latches that used to stand proud of the deck are removed, so the rear datum is now the deck at L/2 |
| Wheel centres | (±1.300000,.100000,.261428) | Car half-track; Y adjusted by r−R from car mount Y; axle at 60% of deck length from its front (Z = length/10) |
| Wheel centre at rest | Local Y −.150000 | Mount Y − .250000 rest drop; bottom = −.600000, matching cars |
| Physical tyre lateral envelope | X ±1.500002 | Car half-track + quad tyre half-width; car tyres reach ±1.500003 |
| Mudguard lateral envelope | X ±1.550002; each .500004 wide | Native tyre width + 2t clearance. The tyres stay in car tracks; guards add .050000 per side beyond the tyres |
| Mudguard crown | Inner .750000 / outer .800000 radial envelope about rest wheel centre | r + full .250000 rest compression + t clearance; add t sheet thickness; six half-circle facets |
| Fender compression clearance | At least .013279 m under the conservative enclosing circle | Segment count is DERIVED: the smallest n whose facets clear, at the standoff the fleet already uses. Five here. Coarsening to three pulls the facet midpoints 5 cm INSIDE the envelope even though no vertex moves, and buying that back by inflating the radius (.750 to .858) turned the fender into three plates tented over the wheel |
| Axle beam | .100000 × .100000, across 2.600000 track | 2t cross-section; beam centred on wheel rest Y and measured-design axle Z |
| Chassis beams | .100000 × .100000 along deck | 2t sections; X centres ±(deck half-width−2t), Y .100 = deck Y−3t |
| Free tongue: pin to deck front | 1.711050 m | Golf half-body width + quad radius = 1.2610495+.450 |
| Two inclined A-frame beams | .100000 square | 2t; start X ±t at pin Z+4t, end X ±(deck half-width−2t) at deck front+length/4; rise from pin Y to deck Y−3t |
| Kingpin (ball/socket pivot) | (0,−.028921,−3.018188) | Golf bumper Y midpoint; deck front − free tongue |
| Socket body | .200000 wide × .100000 high × .300000 long | 4t × 2t × 6t; Z pin−2t..pin+4t; latch/handle sections t and t/2 |
| Car receiver and octagonal ball | .100000 receiver section, .050000 ball radius | 2t / t; receiver starts t inside each measured rear centreline section and ends t beyond its own tow point |
| Parking stand | Foot .200000 square, .050000 thick; support top Y −.078921 | 4t foot, t thickness; bottom at common nominal ground −.600000; top at pin Y−t; X=3t, Z midway between pin and deck front |
| Support collider | (.200000,.521079,.200000) at (.150000,−.339460,−2.162663) | Exact bounding box of shaft + foot; matching mesh split zone retracts on coupling |
| Mudflaps | Native guard width; .025000 thick; Y −.375000..−.050000 | t/2 thickness; rest wheel centre−r/2 through centre+2t |
| Hinges / latches / reflectors / lamps | Multiples of t | Hinges 4t × t × t at ±.65 deck half-width; latches t × 2t × t/2; amber reflectors 2t × 2t × t/2; tail lenses 4t × 2t × t at X ±(deck half-width−3t) |
| Full saved body AABB | (−1.550002,−.600000,−3.118188)..(1.550002,.700000,1.357138) | Guards set X; support foot and rail set Y; pin−2t and deck rear+t set Z |
| Full body X × Y × Z | **3.100004 × 1.300000 × 4.475326 m** | Difference of those saved extrema; includes hardware/parked stand, excludes separate wheels |
| Empty mass / health | 300 kg / 450 | Quad spec; 1/20 the semi trailer's mass, 27.3% of the 1100 kg hatchback. Design tuning, not a payload/tow rating |

The mesh is an open green-sided utility trailer with a timber deck, single axle, A-frame drawbar, coupler/locking handle, faceted mudguards, mudflaps, rear lamps, hinges and latches. It is not a trailer for carrying a whole car.

The body is an assembly of closed solid components, with ordinary overlapping structural joints. It is not a Boolean-unioned single skin. It has **96 position records, 144 triangles, zero boundary edges, zero non-manifold geometric edges and consistent opposite edge winding**. Rear lights are 16 v / 24 tris. Each of the eight hitch meshes is 24 v / 40 tris. All authored files have explicit per-corner UVs/normals and exactly three corners per face. No zero-area triangles or out-of-range indices; smallest body triangle area approximately .000625 m².

All 11 authored mesh/palette assets were also regenerated and compared byte for byte; there was no drift.

Main collider BoxSize is (2.099996,.100000,2.614276), centre (0,.200000,0). HullBoxes retain the deck and two yaw-aligned drawbar beams; beam Y conservatively encloses their rise. ExtraBoxes cover side/end walls and the socket, leaving the load space open. The thin end panels are inset .010 m relative to their conservative collider planes. Mudguards, small hardware and car receiver Parts are visual geometry rather than individually fitted collision shapes.

## Coupling decisions

Enable **golf, hatchback, sedan, wagon/SUV, jeep, offroader, truck and van**. All share the confirmed 2.600 track, .600 tyres and −.600 nominal rear ground datum; their bumper sections give effectively the same ball height, so the trailer can sit level. The SUV's independently measured bumper is .000354 m lower; that tiny nominal offset is explicit.

Retain the semi's existing fifth wheel. Leave quad (1.000 track), tractor (1.806/3.010 tracks and differing axle datums), bus (3.000 track), APC/tank (4.000/4.522 tracks and high rear sections), military and emergency vehicles, and roadster unchanged. Track/height rule out several cleanly. Humvee, police, ambulance and roadster share suitable geometry: excluding those is a scope/vehicle-role choice, **not** evidence of a measured inability to tow. There are no manufacturer tow ratings in these meshes. Ordinary hatchback/sedan/SUV and utility vehicles are the enabled set.

Every point is **(0, its own rear-section Y midpoint, its own original Body+Parts rear Z + quad radius/3)**:

| Tow car | FifthWheel local XYZ | Nominal height |
| --- | --- | --- |
| Golf | (0,−.028921,2.709203) | .571079 |
| Hatchback | (0,−.028921,2.943911) | .571079 |
| Sedan | (0,−.028921,3.092378) | .571079 |
| SUV (`wagon`) | (0,−.029275,2.976938) | .570725 |
| Jeep | (0,−.028921,2.743243) | .571079 |
| Offroader | (0,−.028921,2.743243) | .571079 |
| Truck | (0,−.028921,2.743243) | .571079 |
| Van | (0,−.028921,2.743243) | .571079 |

Identical values on the jeep-derived bodies reflect identical measured geometry. No common Z was applied across the fleet. Each car has its own generated receiver/ball Part, registered on both existing rendering paths. The coupler nose sits .050000 m behind the complete towing-body rear plane when aligned; the pivot itself is .150000 beyond it.

The existing `Kingpin` mechanism supplies `IsTrailer`, passive wheels, zero powered-wheel traction, the cab-owned PinJoint3D, attach translation to coincident pins, ghosting, support retraction and tail/brake-light pass-through. Engine, steering, speeds and brakes are zero; the semi's inert gears and positive nominal Fuel=1000 avoid null/division problems. Trailer entry remains blocked by existing `IsTrailer` handling; no passenger seats were invented.

One small system extension is necessary for this shape: `Spec.HitchYawLimit`, default zero meaning the existing 90° limit. The car trailer supplies **57.860160° = atan2(free tongue length, outer front-rail half-width)**. The runtime clamp uses the trailer's limit. At that angle the rail's closest corner remains behind the pin; a sweep of all body vertices over the full ±limit keeps them beyond every towing car's original rear plane. The socket's diagonal corners retain at least approximately .0086 m of that conservative clearance. This is a geometric yaw guarantee for level bodies, not a pitch/roll/dynamic-contact guarantee. The semi retains 90°.

## Registration and verification

Spawn with the canonical vehicle name **`car_trailer`** wherever the existing vehicle command accepts `golf` or `wagon`. Added `BuildCarTrailer`, `BuildByName`, `SpecFor` (including the MP puppet path), and an **append at SpecNames TypeId 33**. Every old key and index from `1f7f106d` remains unchanged, including SUV at 32. It is command-only; the natural spawn pool is unchanged. The wagon verifier was updated to allow later appended vehicles while still comparing its entire original index prefix.

- `dotnet build game/UnturnedGodot.csproj`: passed. Both the final build and a control build with the original `1f7f106d` Vehicle.cs (new test omitted) reported **22 identical pre-existing warnings, zero errors**. Warning messages were compared as sets; there are no new warnings.
- `python3 tools/verify_car_trailer.py --mutation-test`: **51 named checks, 203 deliberately failing real-file regressions**. I actually reverted/corrupted each checked behavior (including all eight own-rear tow points, mesh corner/index/normal/UV/topology failures, palette V compensation, registrations, TypeId insertion, radius/track/axle, support, and yaw spec/transfer/clamp). Each failed the corresponding check. The verifier restores exact original bytes in `finally`, then reruns the unmodified checks. No check is presented without an exercised failing mutation.
- `python3 tools/verify_wagon.py`: passed, retaining the original 32 pre-wagon TypeIds and wagon TypeId 32.
- Godot 4.6 `--headless --path game -- --tests=vehicle.car_trailer`: **46 checks passed** in the construction/coupling fixture: actual server/replica mesh loading, canonical identity, two passive wheels, all eight hitch Parts, attach/detach and PinJoint presence, coincident anchors, retracted/redeployed support, and imported yaw limit. Headless was used for this fixture only, not for the renders.

The static verifier reads saved meshes under ParseObj's float32, three-corner and V-flip rules. Expected dimensions come from donor files/specs independently of the geometry generator. It checks relationships, not just a second list of the authored dimensions.

## Render evidence

I rendered and inspected all three images through Godot 4.6 Vulkan/llvmpipe under Xvfb, **without `--headless`**, with `UG_ISO=1`. [Beside the Golf](car_trailer_beside_golf.png) and [coupled to the Golf](car_trailer_coupled_golf.png) are each a **single combined OBJ** containing the original, unscaled car body and both vehicles' actual wheel meshes. Only translation, left-wheel reflection and atlas UV remapping are applied. The coupled image translates the trailer by Golf FifthWheel minus trailer Kingpin and hides its stand. These are static poses, not driving screenshots. The direct bake uses an opaque atlas, so the Golf's paint/glass appearance is not a gameplay material test.

![Trailer beside the unscaled Golf](car_trailer_beside_golf.png)

![Static coupled pose](car_trailer_coupled_golf.png)

[Standalone detail render](car_trailer_alone.png). Its automatically fitted camera is useful for examining geometry; it is not scale evidence on its own.

Reproduce the generated meshes and render inputs with:

```sh
python3 tools/build_car_trailer.py
python3 tools/preview_car_trailer.py
UG_ISO=1 xvfb-run -a /home/ec2-user/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64 \
  --path game --rendering-driver vulkan --render-thread safe -- \
  --bakeicon=car_trailer_preview_beside.txt:car_trailer_preview_atlas.png \
  --shot=/tmp/car_trailer_beside.png
```

Substitute `alone` or `coupled` for the other inputs. The `--` separator is required because Main reads `OS.GetCmdlineUserArgs()`. Render inputs are generated scratch files; the PNG evidence is committed. The first beside render saved successfully but emitted a renderer-thread shutdown error; subsequent renders used the supported `--render-thread safe` option.

## What I could not verify

**No sustained driving test was performed.** Snaking, loaded suspension heights, braking/reversing behavior, hill starts, pitch/roll articulation and long-run stability are unverified. The positive axle offset is a deliberate nose-load bias, not proof that the trailer tows well. No payload capacity or real-world tow rating is claimed. Cargo retention is untested; there is no animated/opening tailgate or new cargo system.

Server and puppet construction were exercised, but actual network spawning, multiplayer hitch interaction and replication of the coupled relationship were not. The existing system does not distinguish ball hitches from semi fifth wheels, so it still permits geometrically incompatible car/semi pairings if brought within coupling reach. I did not add a hitch-class protocol or change that existing behavior.

The geometry sweep is level/static; the existing pair ghosting and anti-clip behavior under real contact remains unverified. The attachment test exercises construction and lifecycle, not a car driving with a trailer. No result above should be read as a successful road test.

## Detail pass (strawberry: "redo. too much detail. look at the truck bed etc")

The first body was **572 triangles** -- 5th busiest of the fleet's 31 bodies, above every car, above
the truck's whole 390 and the van's 422, for an object that is a flat deck on one axle. The reference
named in the note settles what the fleet's vocabulary actually is: the truck's bed floor is **two
triangles**, one 1.616 m2 quad at Y=0.125, with no board lines anywhere.

Removed, with what each cost: nine modelled deck boards (72), six stake posts (72), two capping rails
(24), two gate rails (24), tailgate hinges (24) and latches (24), mudflaps (24), amber reflectors (24),
the coupler's latch (12) and handle (12), the landing foot pad (12), and six fender segments cut to
five (16). Everything on that list is a box under about 8 cm, which is below the scale anything else in
the fleet models -- they read as noise on the silhouette rather than as detail.

**572 -> 232 triangles, 366 -> 144 vertices.** That lands between the wagon (226) and the jeep (232),
in the fleet's lower third, which is where a trailer belongs.

Two things this pass changed that are not just deletions. The deck is one slab whose top sits t/4 under
the sideboards' base: coplanar faces with coincident corners get welded by `save(weld_positions=True)`
and the shared edge then carries four faces, which the verifier correctly calls non-manifold. And the
fender's segment count is now derived rather than chosen -- see the clearance row above for why three
segments is wrong at this radius and why inflating the radius to rescue it is worse.

## Second pass: arches out, truck-bed wall section, sedan lamps, bigger

strawberry: *"remove the wheel arches. the truck bed walls/body measure the thickness and size. the tail
lights should be off the sedan. should also be bigger"*.

**The truck bed's wall section, measured.** Isolating the bed (Z > .294, behind the cab) and selecting
its side walls by face normal gives outer/inner planes at |X| 1.231 / .981 and a wall running from the
floor plane at Y .125 to Y 1.125. So **.250 thick, 1.000 tall, 2.462 outer width**. Mine were t = .050
and .450 -- five times too thin and under half the height, which is what made the box read as a tray
rather than as bodywork. Both numbers now come from `truck_bed()` and neither is typed.

`expected()` re-derives the same section by a **different route** (a Z slab plus vertex-column
clustering, anchored on the bed's own floor plane) precisely so a bug in one shows up as a
disagreement. It earned that twice while being written: clustering without excluding the cab's rear
wall reported the wall as 2.250 tall, and anchoring the floor on the wall column's lowest vertex
reported 1.250. **Both are real numbers answering a different question** -- the second is the wall
panel including the .250 of under-frame below the deck, which is not a height you can stand a crate
in. They agree to 3.3e-07 m now.

**Arches removed**, and with them the derived segment-count machinery from the previous pass. The
wheels tuck flush under the deck edge instead: track is `deck_w - tyre_width` = 2.062, so the tyres end
exactly at |X| 1.231. Leaving them on the Golf's 2.600 track would have hung them 270 mm outboard of a
deck with nothing above them. The clearance check went with the part, replaced by its negative: an
assertion that nothing arch-shaped exists in either the mesh or the generator.

**Tail lamps are the sedan's mesh**, not a pair of boxes shaped like it -- 56 v / 20 tris, its exact
2.2269 x .3291 x .1899 section, translated so the front face meets the trailer's rear and the lenses
centre on the tailgate. X is untouched, which is the point: at +/-1.1135 they already sit inside a
2.462 deck. The palette's red is now the sedan's own measured (142,32,32) texel. A verifier check pins
this as a **rigid translation** -- one distinct offset across every vertex, dX = 0 -- which a lookalike
box of the same bounds would fail.

Note `sedan_taillights.txt` is itself **two open shells** (8 unpaired edges in the fleet's own asset),
so the closure requirement is waived for that one mesh and replaced by the translation check above.
Everything else -- ParseObj rules, indices, per-corner normals and UVs, degenerate and duplicate faces
-- still applies to it.

**Bigger:** deck **3.1371 x 2.4620** (was 2.6143 x 2.1000), from `golf length * .6` and the truck bed's
own outer width. Body is **96 v / 144 tris**, down again from 232.
