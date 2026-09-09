# Car trailer: fresh measurements

Source snapshot: `1f7f106d` on `astra-cartrailer`. Read `vehicle_measurements.md` first; its fleet AABBs and statistics are retained. This supplement samples rear sections, rechecks wheel specs, and corrects the now-stale wagon geometry row. Reproduce with `python3 tools/measure_car_trailer.py`.

## Frame and height datum

`ContentProvider.ParseObjUncached` reads positions unchanged: +Y up, −Z forward. Fresh Golf lamp bounds confirm the front headlights at Z −2.565942..−2.440543 and rear lights at +2.224357..+2.349755. No gun-frame conversion applies. It takes only three face corners, flips UV V, and creates no normals.

Ground here is the **nominal rear tyre bottom with suspension at rest**, `anchor.Y − effectiveRadius − WheelRestLength`. Build uses rest length 0.250 m (tank 0.150). Therefore height is local Y minus that ground coordinate. This is reproducible geometry, not a loaded suspension/terrain measurement; tractor has different front/rear datums and would pitch.

Rear section: intersect loaded triangles with X=0, take the largest section Z and the min/max Y within 0.0001 m of that Z (export jitter). Mount Y is their midpoint. APC/tank have a single-point section, not a usable bumper face. Rearmost body Z is kept separate from the complete original Body+Parts Z; hitch projections must clear the latter. For wagon use its separate rear bumper section. Fresh hitch parts are excluded from their own baseline.

## Every road rear end

| Vehicle / mount mesh | Body rear Z | Body+Parts rear Z | Section rear Z | Section local Y min..max | Mount local Y | Nominal ground local Y | Mount height above ground |
| --- | --- | --- | --- | --- | --- | --- | --- |
| jeep / `jeep_body.txt` | 2.593243 | 2.593243 | 2.517113 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| semi / `semi_0.txt` | 4.500000 | 4.500000 | 4.500000 | 0.207420..0.957420 | 0.582420 | -0.350000 | 0.932420 |
| trailer / `trailer_0.txt` | 8.100000 | 8.100000 | 8.000000 | 0.500008..1.250008 | 0.875008 | -0.350000 | 1.225008 |
| quad / `quad_body.txt` | 1.981589 | 1.981589 | 1.824307 | 0.532530..0.771323 | 0.651926 | -0.500000 | 1.151926 |
| bus / `bus_body.txt` | 4.454894 | 4.454894 | 4.351417 | -0.238703..0.020747 | -0.108978 | -0.770000 | 0.661022 |
| sedan / `sedan_body.txt` | 2.942378 | 2.942378 | 2.866247 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| wagon / `wagon_bumper_rear.txt` | 2.680000 | 2.826938 | 2.826935 | -0.159000..0.100450 | -0.029275 | -0.600000 | 0.570725 |
| hatchback / `hatchback_body.txt` | 2.793911 | 2.793911 | 2.717781 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| humvee / `humvee_body.txt` | 2.718243 | 2.718243 | 2.642113 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| roadster / `roadster_body.txt` | 2.942378 | 2.942378 | 2.866247 | -0.158646..0.100804 | -0.028921 | -0.625000 | 0.596079 |
| ambulance / `ambulance_body.txt` | 2.699655 | 2.699655 | 2.623525 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| firetruck / `firetruck_body.txt` | 3.669168 | 3.669168 | 3.593037 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| tractor / `tractor_body.txt` | 2.500003 | 2.500003 | 2.500001 | -0.284477..0.323869 | 0.019696 | -0.775000 | 0.794696 |
| ural / `ural_body.txt` | 3.343243 | 3.343243 | 3.267113 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| police / `police_body.txt` | 2.942378 | 2.942378 | 2.866247 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| offroader / `offroad_body.txt` | 2.593243 | 2.593243 | 2.517113 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| truck / `truck_body.txt` | 2.593243 | 2.593243 | 2.517113 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| van / `van_body.txt` | 2.593243 | 2.593243 | 2.517113 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| golf / `golf_body.txt` | 2.559203 | 2.559203 | 2.483066 | -0.158646..0.100804 | -0.028921 | -0.600000 | 0.571079 |
| apc / `apc_body.txt` | 3.718241 | 3.718241 | 3.677584 | 0.970972..0.970972 | 0.970972 | -0.890000 | 1.860972 |
| tank / `tank_hull.txt` | 4.726576 | 4.726576 | 4.726562 | 1.289064..1.289064 | 1.289064 | -0.334000 | 1.623064 |

## Tracks and effective wheels: confirmed against current specs

| Vehicle | Track by axle F→R | Effective radius by axle F→R | Rear anchors Y |
| --- | --- | --- | --- |
| jeep | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| semi | 2.920000; 2.560000; 2.560000 | 0.650000; 0.650000; 0.650000 | 0.550000 |
| trailer | 2.600000; 2.600000 | 0.650000; 0.650000 | 0.550000 |
| quad | 1.000000; 1.000000 | 0.450000; 0.450000 | 0.200000 |
| bus | 3.000000; 3.000000 | 0.600000; 0.600000 | 0.080000 |
| sedan | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| wagon | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| hatchback | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| humvee | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| roadster | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.225000 |
| ambulance | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| firetruck | 2.600000; 2.600000; 2.600000 | 0.600000; 0.600000; 0.600000 | 0.250000 |
| tractor | 1.806000; 3.010000 | 0.900000; 1.050000 | 0.525000 |
| ural | 2.600000; 2.600000; 2.600000 | 0.600000; 0.600000; 0.600000 | 0.250000 |
| police | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| offroader | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| truck | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| van | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| golf | 2.600000; 2.600000 | 0.600000; 0.600000 | 0.250000 |
| apc | 4.000000; 4.000000; 4.000000; 4.000000 | 0.740000; 0.740000; 0.740000; 0.740000 | 0.100000 |
| tank | 4.522000; 4.522000; 4.522000; 4.522000 | 0.740000; 0.740000; 0.740000; 0.740000 | 0.556000 |

The 0.600 m claim holds for ordinary cars, bus and several trucks; **quad is 0.450**, semi/trailer effectively 0.650 (their nominal 0.550 is overridden), APC/tank 0.740, tractor 0.900/1.050. Boats declare unused 0.300 radii but have no wheel anchors. Use `quad_wheel.txt` at its native 0.450, not a smaller radius with an unscaled car mesh.

Fresh quad wheel AABB: (-0.200002, -0.450000, -0.446719) .. (0.200002, 0.436924, 0.446719). Width 0.400004 m; its polygonal tread is not a perfect circular AABB. The car wheel width is 0.400006 m, so the trailer tyres follow almost identical lateral envelopes.

## Semi trailer reference, read in full

`_trailer`: body `trailer_0.txt`, palette `semi_0_albedo.png`, wheel `jeep_wheel.txt` / `jeep_wheel_albedo.png`; 268 position records / 111 loaded triangles; 3.000000 × 2.499998 × 16.100001 m (X,Y,Z). Mass 6000 kg. Kingpin (0,0.62,−6.6). Main BoxSize (3,1.10,12.35), center (0,0.70,1.9). Extra boxes (3,1.5,0.5) at (0,1.75,−7.75) and (1.9,1.10,3.6) at (0,0.70,−5.7). This is a long deck, front gooseneck/headboard and rear tandem, not a car trailer.

Engine/steer/speed/brake all zero. Dummy gears [1], reverse 1, shift 5000; Sound null; Fuel 1000 avoids division by zero, Health 600. Nominal radius 0.55 overridden by four 0.65 radii, anchors (±1.30,0.55,5.4/7.0), all nonsteering. Parts empty. Tail emitters (±1.13,1,8); baked lens zone X −1.42..−0.84, Y .84..1.17, Z 7.85..8.15 is mirrored by the loader. Steering pivot/axis zero.

LandingGearSize (2.24,1.63,.5), center (0,.315,−4.13); leg zone (−1.25,−.05,−4.55)..(1.25,1.16,−3.75), scale Y 1.44 about Y 1.13. CoupleTo snaps the kingpin onto the cab point, owns a PinJoint3D on the cab, ghosts the pair and retracts the leg collider/mesh. Uncouple restores them. Kingpin also disables powered wheels, traction and drive access zones. The new trailer uses these mechanisms at car scale.

## Current wagon correction

The saved fleet table predates the rebuilt SUV. Current Body AABB (-1.260000, -0.159000, -2.803611) .. (1.260000, 2.170000, 2.680000). Rear bumper extends to Z 2.826938; body ends at 2.680000. Its current axle Zs are −1.560 / +1.460, track 2.600 and radius .600; mass 1850 kg. Do not use the old 3.192378 rear or 1650 kg row.

## Tow points to author

Projection = quad radius / 3 = 0.150 m. Each FifthWheel = (0, its own bumper-section midpoint Y, its own original Body+Parts rear Z + projection). Identical Ys on exported car bumpers are measured equality, not a blanket point. Wagon has a different Y as well as Z. A visible receiver reaches from the section into this point.

| Car | FifthWheel local XYZ | Nominal world height |
| --- | --- | --- |
| golf | (0.000000, -0.028921, 2.709203) | 0.571079 |
| hatchback | (0.000000, -0.028921, 2.943911) | 0.571079 |
| sedan | (0.000000, -0.028921, 3.092378) | 0.571079 |
| wagon | (0.000000, -0.029275, 2.976938) | 0.570725 |
| jeep | (0.000000, -0.028921, 2.743243) | 0.571079 |
| offroader | (0.000000, -0.028921, 2.743243) | 0.571079 |
| truck | (0.000000, -0.028921, 2.743243) | 0.571079 |
| van | (0.000000, -0.028921, 2.743243) | 0.571079 |
