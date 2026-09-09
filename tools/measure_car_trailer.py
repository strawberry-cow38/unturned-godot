#!/usr/bin/env python3
"""Fresh rear sections/spec audit only; reuse the wagon job's fleet dimensions."""
from pathlib import Path
import math
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


def truck_bed():
    """The truck's cargo bed, measured -- strawberry named it as the reference for how thick and how
    big this trailer's walls should be ("the truck bed walls/body measure the thickness and size").

    Isolated by face normal rather than by eye: the bed is everything behind the cab (Z > .294, where
    the cab's rear plane sits), and its side walls are the X-facing faces in that region. Their outer
    and inner planes give the thickness; the floor plane and the wall top give the height."""
    V=[];F=[]
    for line in (CONTENT/'truck_body.txt').read_text().splitlines():
        c=line.split()
        if line.startswith('v '): V.append([float(x) for x in c[1:4]])
        elif line.startswith('f '): F.append([int(x.split('/')[0])-1 for x in c[1:4]])
    bed=[f for f in F if sum(V[i][2] for i in f)/3 > .294]
    planes={}
    for f in bed:
        a,b,c=[V[i] for i in f]
        u=[b[k]-a[k] for k in range(3)]; v=[c[k]-a[k] for k in range(3)]
        n=(u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0])
        L=math.sqrt(sum(x*x for x in n)) or 1.
        if abs(n[0]/L) < .9: continue                       # side walls only
        ps=[V[i] for i in f]
        key=round(sum(p[0] for p in ps)/3, 3)
        lo,hi,cnt=planes.get(key,(1e9,-1e9,0))
        planes[key]=(min(lo,min(p[1] for p in ps)), max(hi,max(p[1] for p in ps)), cnt+1)
    # The wall proper is the tallest pair of planes; the short outboard patches are step/flare trim.
    tall=sorted((k for k,(lo,hi,c) in planes.items() if hi-lo > .5), key=abs)
    inner,outer = abs(tall[0]), abs(tall[-1])
    floor=min(planes[k][0] for k in planes if abs(k)==inner)   # the wall's own base = the bed floor
    top  =max(planes[k][1] for k in planes)
    return dict(wall_t=outer-inner, wall_h=top-floor, outer_w=2*outer, inner_w=2*inner,
                floor=floor, top=top)

# THREE SIZES OFF ONE DERIVATION (strawberry: "thats its name. dinky trailer. the new one is small
# trailer. do a medium one which is longer and wider. and 4 wheels."). Nothing here is a typed
# dimension: `length` is a fraction of the Golf's body length and `width_steps` counts HALF TRUCK WALL
# SECTIONS added to the Golf track before the box is solved out of it, so every size still traces to
# the same two fleet measurements the single trailer did.
#   dinky  .60 / +0 -> 3.137 x 2.100, the one already in the game
#   small  .75 / +1 -> 3.921 x 2.225, the previous pass
#   medium .95 / +2 -> 4.967 x 2.350, and two axles because one axle under five metres of deck is a
#                     wheelbarrow; the fleet's own long bodies (truck, van) all carry four wheels.
#   TWO WIDTH MODES. The small classes solve the box out of the track so the tyre lands flush against
#   the sideboard. The big ones do NOT (strawberry: "wider! ignore the limit on width") -- their box is
#   set directly and the wheels end up UNDER it, which is the only arrangement that removes the limit
#   rather than restating it. That forces the deck up over the tyre; see `deck_y` below.
CLASSES = {
    'dinky':  dict(length=.60, width_steps=0, axles=1, wide=False, display='Dinky Trailer'),
    'small':  dict(length=.75, width_steps=1, axles=1, wide=False, display='Small Trailer'),
    'medium': dict(length=.95, width_steps=2, axles=2, wide=True,  display='Medium Trailer'),
    'large':  dict(length=1.15, width_steps=4, axles=2, wide=True, display='Large Trailer'),
}


def design(specs, rear, cls='small'):
    golf, quad = specs['golf'], specs['quad']
    # These dimensions are present in the saved fleet note; no new fleet size statistics.
    row = next(l for l in (ROOT/'notes/vehicle_measurements.md').read_text().splitlines() if l.startswith('| golf /'))
    cols = [x.strip() for x in row.split('|')]
    length, car_width = float(cols[2]),float(cols[3])
    # THE CAR'S WHEEL (strawberry: "scale the wheels to be the same size as the car's"). Every road
    # vehicle runs jeep_wheel.txt at WheelRadius .600; the trailer was on the quad's .450.
    radius = golf['WheelRadius']
    wheel_mesh = golf['fields']['Wheel']; wheel_tex = golf['fields']['WheelTex']
    tyre_width = obj(CONTENT/wheel_mesh.strip('"'))['size'][0]
    bed = truck_bed()
    # t was (car radius - quad radius)/3, which is meaningless once the trailer runs the car's wheel --
    # it would be zero. Same .050 value, now taken off the section that is actually load-bearing here.
    t = bed['wall_t']/5
    # SIZE AND WALL SECTION FROM THE TRUCK'S BED (strawberry: "the truck bed walls/body measure the
    # thickness and size ... should also be bigger"). Its walls are .250 thick and stand 1.000 above
    # the floor; mine were t = .050 and .450, five times too thin and under half the height.
    wall_t, wall_h = bed['wall_t'], bed['wall_h']
    # BOX FIRST, THEN THE TRACK (strawberry: "remove the axle and have the wheels flush with the
    # trailer walls"). The box keeps the width the previous pass sized it to -- the Golf track plus
    # half a truck wall section, less the tyre and its clearance -- but the wheels no longer stand
    # outboard of it. The track is now solved so the TYRE'S OUTER FACE lands exactly on the sideboard's
    # outer face, which is what "flush" means here and what removes the outrigger look the axle bar
    # was drawing attention to.
    C = CLASSES[cls]
    # NO WIDTH LIMIT ON THE BIG TWO -- but the wheels still mount on the SIDES (strawberry: "the medium
    # and large trailers are sitting on TOP of the wheels. wheels should attach to the sides"). Lifting
    # the limit means the box stops being capped by the tyre; the TRACK simply follows it outward. The
    # first attempt kept the track on the Golf's and put the deck over the wheels, which is a different
    # vehicle -- a flatbed -- and not what was asked for.
    budget = golf['tracks'][-1]+wall_t/2*C['width_steps']
    deck_w = budget if C['wide'] else budget-tyre_width-2*t
    # FLUSH = the tyre sits AGAINST the sideboard's outer face, not inside it. Aligning the tyre's
    # OUTER face with the wall instead puts the wheel through the box: the tyre tops out .620 above
    # the deck floor, so at that track its inner half rises into the cargo bay. Nothing about removing
    # the axle changes that -- the only way to have a wheel inboard of the wall is to raise the whole
    # bed over it, which needs deck_y 1.100 against today's -.023, i.e. a lorry-height floor.
    # So the wheel is hard against the wall with the gap closed, which is what the axle bar was
    # spanning and why it can go.
    track = deck_w+tyre_width
    deck_l = length*C['length']
    front,back = -deck_l/2,deck_l/2
    draw = car_width/2+radius
    king = (0.,rear['golf']['y'],front-draw)
    hitch_projection = 3*t   # was radius/3, which would now drag all eight cars' tow balls back 50 mm
    ground = rear['golf']['ground']
    wheel_y = ground+radius+.25
    wheel_center_y = wheel_y-.25        # the RESTING centre: the tyre touches `ground` here
    axle_z = deck_l/10  # 60% of deck length from its front; COM at Z=0 is ahead of axle
    # TANDEM SPACING is derived, not chosen: two tyres of radius r on one side must not intersect in
    # Z, so their centres are at least 2r apart; plus t of clearance. Single-axle classes ignore it.
    axle_spacing = 2*radius+t
    axle_zs = [axle_z] if C['axles']==1 else [axle_z-axle_spacing/2, axle_z+axle_spacing/2]
    # WHEELS SIT HIGHER ON THE BODY, at the fleet's own relationship (strawberry: "move the wheels
    # higher up on the trailer"). Measured: golf, sedan, hatchback, jeep, truck and van ALL rest their
    # wheel centre .2734 above the body's lowest point. The trailer's sat at .0000 -- centre exactly
    # level with the deck's underside, which is what made it look like a box on stilts. Raising the
    # wheel and lowering the body are the same edit, and the body is the one that can move: the tyres
    # have to keep meeting the ground.
    ride = (golf['Wheels'][0][1]-.25) - obj(CONTENT/golf['fields']['Body'].strip('"'))['lo'][1]
    deck_y = wall_t-ride
    return dict(t=t, radius=radius, tyre_width=tyre_width, track=track,
                deck_l=deck_l,deck_w=deck_w,front=front,back=back,draw=draw,
                king=king,ground=ground,wheel_y=wheel_y,wheel_center_y=wheel_center_y,
                axle_z=axle_z,deck_y=deck_y,rail_y=deck_y+wall_h,wall_t=wall_t,wall_h=wall_h,bed=bed,
                hitch_projection=hitch_projection, mass=quad['Mass'], ride=ride,
                cls=cls, axles=C['axles'], axle_zs=axle_zs, axle_spacing=axle_spacing,
                display=C['display'], key=cls+'_trailer',
                lamp_inset=obj(CONTENT/'sedan_body.txt')['size'][0]/2-obj(CONTENT/'sedan_taillights.txt')['hi'][0],
                wheel_mesh=wheel_mesh, wheel_tex=wheel_tex)

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
