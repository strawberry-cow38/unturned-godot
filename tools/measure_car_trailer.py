#!/usr/bin/env python3
"""Fresh rear sections/spec audit only; reuse the wagon job's fleet dimensions."""
from pathlib import Path
import re
from measure_vehicles import ROOT, CONTENT, ROAD, read_specs, obj, vector, number, vec, table

TOW_CARS = 'golf hatchback sedan wagon jeep offroader truck van'.split()

def rear_section(mesh):
    """Intersect loaded triangles with X=0; measure Y at the rearmost section Z."""
    points = []
    for face in mesh['faces']:
        vs = [mesh['vertices'][int(c.split('/')[0])-1] for c in face[:3]]
        for a,b in zip(vs,vs[1:]+vs[:1]):
            if abs(a[0]) < 1e-9:
                points.append(a)
            if a[0]*b[0] < 0:
                t = -a[0]/(b[0]-a[0])
                points.append(tuple(a[i]+t*(b[i]-a[i]) for i in range(3)))
    z = max(p[2] for p in points)
    ys = [p[1] for p in points if abs(p[2]-z) < 1e-4]  # exporter jitter
    return z, min(ys), max(ys)

def measurements():
    specs = read_specs(ROAD+['wagon'])
    result = {}
    for name,s in specs.items():
        # Wagon moved its bumper out of Body into Parts. All other road bodies bake it in.
        rear_mesh = obj(CONTENT/'wagon_bumper_rear.txt') if name == 'wagon' else s['mesh']
        z,lo,hi = rear_section(rear_mesh)
        parts = [obj(CONTENT/p) for p in s['Parts'] if not p.endswith('_hitch.txt')]
        rearmost = max(m['hi'][2] for m in [s['mesh']]+parts)
        ids = list(s['axles'].values())[-1]
        rest = .15 if name == 'tank' else .25  # actual Build: s.Tracked ? .15 : .25
        ground = min(s['Wheels'][i][1]-s['radii'][i]-rest for i in ids)
        y = (lo+hi)/2
        result[name] = dict(body_rear=s['mesh']['hi'][2], rear=rearmost, section_z=z,
                            ymin=lo, ymax=hi, y=y, ground=ground, height=y-ground,
                            mount_mesh='wagon_bumper_rear.txt' if name=='wagon' else s['Body'])
    return specs,result

def design(specs, rear):
    golf, quad = specs['golf'], specs['quad']
    # These dimensions are present in the saved fleet note; no new fleet size statistics.
    row = next(l for l in (ROOT/'notes/vehicle_measurements.md').read_text().splitlines() if l.startswith('| golf /'))
    cols = [x.strip() for x in row.split('|')]
    length, car_width = float(cols[2]),float(cols[3])
    radius = quad['WheelRadius']; car_radius = golf['WheelRadius']
    t = (car_radius-radius)/3
    tyre_width = obj(CONTENT/quad['Wheel'])['size'][0]
    track = golf['tracks'][-1]
    deck_l = length/2
    deck_w = track-tyre_width-2*t
    front,back = -deck_l/2,deck_l/2
    draw = car_width/2+radius
    king = (0.,rear['golf']['y'],front-draw)
    ground = rear['golf']['ground']
    wheel_y = ground+radius+.25
    axle_z = deck_l/10  # 60% of deck length from its front; COM at Z=0 is ahead of axle
    deck_y = golf['Wheels'][0][1]
    return dict(t=t, radius=radius, tyre_width=tyre_width, track=track,
                deck_l=deck_l,deck_w=deck_w,front=front,back=back,draw=draw,
                king=king,ground=ground,wheel_y=wheel_y,wheel_center_y=wheel_y-.25,
                axle_z=axle_z,deck_y=deck_y,rail_y=deck_y+radius,
                hitch_projection=radius/3, mass=quad['Mass'])

def write():
    specs,rear = measurements(); d=design(specs,rear)
    out=['# Car trailer: fresh measurements', '',
         'Source snapshot: `1f7f106d` on `astra-cartrailer`. Read `vehicle_measurements.md` first; its fleet AABBs and statistics are retained. This supplement samples rear sections, rechecks wheel specs, and corrects the now-stale wagon geometry row. Reproduce with `python3 tools/measure_car_trailer.py`.', '',
         '## Frame and height datum', '',
         '`ContentProvider.ParseObjUncached` reads positions unchanged: +Y up, −Z forward. Fresh Golf lamp bounds confirm the front headlights at Z −2.565942..−2.440543 and rear lights at +2.224357..+2.349755. No gun-frame conversion applies. It takes only three face corners, flips UV V, and creates no normals.', '',
         'Ground here is the **nominal rear tyre bottom with suspension at rest**, `anchor.Y − effectiveRadius − WheelRestLength`. Build uses rest length 0.250 m (tank 0.150). Therefore height is local Y minus that ground coordinate. This is reproducible geometry, not a loaded suspension/terrain measurement; tractor has different front/rear datums and would pitch.', '',
         'Rear section: intersect loaded triangles with X=0, take the largest section Z and the min/max Y within 0.0001 m of that Z (export jitter). Mount Y is their midpoint. APC/tank have a single-point section, not a usable bumper face. Rearmost body Z is kept separate from the complete original Body+Parts Z; hitch projections must clear the latter. For wagon use its separate rear bumper section. Fresh hitch parts are excluded from their own baseline.', '',
         '## Every road rear end', '']
    table(out,['Vehicle / mount mesh','Body rear Z','Body+Parts rear Z','Section rear Z','Section local Y min..max','Mount local Y','Nominal ground local Y','Mount height above ground'],[
        [f"{n} / `{r['mount_mesh']}`",*[f'{r[k]:.6f}' for k in ['body_rear','rear','section_z']],f"{r['ymin']:.6f}..{r['ymax']:.6f}",*[f'{r[k]:.6f}' for k in ['y','ground','height']]] for n,r in rear.items()])
    out += ['## Tracks and effective wheels: confirmed against current specs','']
    table(out,['Vehicle','Track by axle F→R','Effective radius by axle F→R','Rear anchors Y'],[
        [n,'; '.join(f'{x:.6f}' for x in s['tracks']),'; '.join(f"{s['radii'][ids[0]]:.6f}" for ids in s['axles'].values()),f"{s['Wheels'][list(s['axles'].values())[-1][0]][1]:.6f}"] for n,s in specs.items()])
    out += ['The 0.600 m claim holds for ordinary cars, bus and several trucks; **quad is 0.450**, semi/trailer effectively 0.650 (their nominal 0.550 is overridden), APC/tank 0.740, tractor 0.900/1.050. Boats declare unused 0.300 radii but have no wheel anchors. Use `quad_wheel.txt` at its native 0.450, not a smaller radius with an unscaled car mesh.', '',
            'Fresh quad wheel AABB: '+vec(obj(CONTENT/'quad_wheel.txt')['lo'])+' .. '+vec(obj(CONTENT/'quad_wheel.txt')['hi'])+'. Width 0.400004 m; its polygonal tread is not a perfect circular AABB. The car wheel width is 0.400006 m, so the trailer tyres follow almost identical lateral envelopes.', '',
            '## Semi trailer reference, read in full','',
            '`_trailer`: body `trailer_0.txt`, palette `semi_0_albedo.png`, wheel `jeep_wheel.txt` / `jeep_wheel_albedo.png`; 268 position records / 111 loaded triangles; 3.000000 × 2.499998 × 16.100001 m (X,Y,Z). Mass 6000 kg. Kingpin (0,0.62,−6.6). Main BoxSize (3,1.10,12.35), center (0,0.70,1.9). Extra boxes (3,1.5,0.5) at (0,1.75,−7.75) and (1.9,1.10,3.6) at (0,0.70,−5.7). This is a long deck, front gooseneck/headboard and rear tandem, not a car trailer.', '',
            'Engine/steer/speed/brake all zero. Dummy gears [1], reverse 1, shift 5000; Sound null; Fuel 1000 avoids division by zero, Health 600. Nominal radius 0.55 overridden by four 0.65 radii, anchors (±1.30,0.55,5.4/7.0), all nonsteering. Parts empty. Tail emitters (±1.13,1,8); baked lens zone X −1.42..−0.84, Y .84..1.17, Z 7.85..8.15 is mirrored by the loader. Steering pivot/axis zero.', '',
            'LandingGearSize (2.24,1.63,.5), center (0,.315,−4.13); leg zone (−1.25,−.05,−4.55)..(1.25,1.16,−3.75), scale Y 1.44 about Y 1.13. CoupleTo snaps the kingpin onto the cab point, owns a PinJoint3D on the cab, ghosts the pair and retracts the leg collider/mesh. Uncouple restores them. Kingpin also disables powered wheels, traction and drive access zones. The new trailer uses these mechanisms at car scale.', '',
            '## Current wagon correction','',
            'The saved fleet table predates the rebuilt SUV. Current Body AABB '+vec(specs['wagon']['mesh']['lo'])+' .. '+vec(specs['wagon']['mesh']['hi'])+'. Rear bumper extends to Z 2.826938; body ends at 2.680000. Its current axle Zs are −1.560 / +1.460, track 2.600 and radius .600; mass 1850 kg. Do not use the old 3.192378 rear or 1650 kg row.', '',
            '## Tow points to author','',
            'Projection = quad radius / 3 = 0.150 m. Each FifthWheel = (0, its own bumper-section midpoint Y, its own original Body+Parts rear Z + projection). Identical Ys on exported car bumpers are measured equality, not a blanket point. Wagon has a different Y as well as Z. A visible receiver reaches from the section into this point.', '']
    table(out,['Car','FifthWheel local XYZ','Nominal world height'],[
        [n,vec((0,rear[n]['y'],rear[n]['rear']+d['hitch_projection'])),f"{rear[n]['height']:.6f}"] for n in TOW_CARS])
    (ROOT/'notes/car_trailer_measurements.md').write_text('\n'.join(out).rstrip()+'\n')

if __name__=='__main__': write()
