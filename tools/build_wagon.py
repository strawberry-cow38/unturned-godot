#!/usr/bin/env python3
"""Build the wagon shell, sedan interior/lamps and extracted hatchback bumpers.

The sedan front stations are measured from its body; the cabin and axle rig
retain the independently authored wagon coordinates. No Boolean overlapping
boxes: each panel bounds either the outside or the open passenger/load space.
"""
import math
from pathlib import Path
import shutil
import struct
from measure_vehicles import CONTENT, ROOT, obj, fmt, read_specs, vector

PAINT = (0.125, 0.75)
# DARK = the SEDAN'S interior grey (strawberry 2026-09-09: "change the interior color to the sedan's").
# Both cars share sedan_palette.png byte for byte, so this was never a palette difference -- it was the
# TEXEL. Colour histogram of each body, by the texel its faces actually sample:
#     sedan   (166,166,166,a=0) x272 paintable | (82,82,82) x56 | (166,166,166) x20 | (97,96,96) x10 |
#             (142,142,142) x8
#     SUV     (166,166,166,a=0) x186 paintable | (58,58,58) x40
# (58,58,58) at uv (0.375,0.25) is darker than ANY dark the sedan puts on its body, which is why the
# cabin read flatter and blacker. (0.625,0.25) is the sedan's own dominant interior grey.
# The sedan spends three further shades on top of that; this uses the one, so the cabin matches its
# main tone rather than inventing a scheme.
DARK = (0.625, 0.25)
HALF_WIDTH = 1.26  # One outer wall plane, including every post and bumper tip.
# FLOOR_Y = the OUTER sill height, and it must match the fleet's visible flank, not its hidden belly.
# Measured lowest point by width band: sedan, hatchback and golf all bottom at -0.159 on the outer
# flank (|x| 1.1-1.3) -- the -0.273 they also have is INBOARD, tucked under the car where it is never
# seen. The first flat-floor pass ran the sheet out to the wall at -0.270, so the wagon's visible sill
# hung 0.111 lower than any other car and the bumper read as floating above it (strawberry: "the
# bumpers need to be lower or the bottom needs to be higher. measure vs a sedan"). Still completely
# flat and full width, just at the fleet's outer height, which also takes ground clearance 0.080 -> 0.191.
FLOOR_Y = -.159
REAR_Z = 2.68


def translate_asset(source, target, offset):
    """Translate position records only; keep donor topology, UVs and normals."""
    lines = []
    for line in (CONTENT/source).read_text().splitlines():
        if line.startswith('v '):
            p = tuple(map(float, line.split()[1:]))
            delta = offset(p)
            if any(delta):
                line = 'v ' + ' '.join(fmt(x+d) for x,d in zip(p,delta))
        elif line.startswith('g '):
            line = 'g ' + Path(target).stem
        lines.append(line)
    (CONTENT/target).write_text('\n'.join(lines)+'\n')

def sub(a, b):
    return tuple(x-y for x, y in zip(a, b))


def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


class Mesh:
    def __init__(self):
        self.v, self.vt, self.vn, self.f = [], [], [], []
        self.maps = [{}, {}, {}]
        self.panels = []

    def panel(self, points, direction, uv=None):
        points = [tuple(round(x, 6) for x in p) for p in points]
        n = tuple(sum(cross(a,b)[i] for a,b in zip(points,points[1:]+points[:1])) for i in range(3))
        if sum(a*b for a,b in zip(n,direction)) < 0:
            points.reverse()
        self.panels.append((points, uv or PAINT))

    def finish_panels(self):
        # Split shared polygon edges at every authored corner BEFORE ear
        # clipping. This preserves the weld across rail/post/floor junctions.
        corners = sorted({p for points,_ in self.panels for p in points})
        for points,uv in self.panels:
            loop = []
            for a,b in zip(points,points[1:]+points[:1]):
                d = sub(b,a); length2 = sum(x*x for x in d)
                cuts = [(0,a)]
                for p in corners:
                    t = sum(x*y for x,y in zip(sub(p,a),d))/length2
                    if 1e-7 < t < 1-1e-7 and sum((p[i]-a[i]-t*d[i])**2 for i in range(3)) < 1e-12:
                        cuts.append((t,p))
                loop.extend(p for _,p in sorted(cuts))
            normal = tuple(sum(cross(a,b)[i] for a,b in zip(loop,loop[1:]+loop[:1])) for i in range(3))
            axis = max(range(3), key=lambda i: abs(normal[i]))
            axes = [i for i in range(3) if i != axis]
            def area(a,b,c):
                u,v=axes
                return (b[u]-a[u])*(c[v]-a[v])-(b[v]-a[v])*(c[u]-a[u])
            orientation = 1 if sum(area(loop[0],loop[i],loop[i+1]) for i in range(1,len(loop)-1)) > 0 else -1
            while len(loop)>3:
                for i,b in enumerate(loop):
                    a,c=loop[i-1],loop[(i+1)%len(loop)]
                    if orientation*area(a,b,c) <= 1e-12:
                        continue
                    if any(all(orientation*area(u,v,p)>=-1e-12 for u,v in ((a,b),(b,c),(c,a)))
                           for j,p in enumerate(loop) if j not in ((i-1)%len(loop),i,(i+1)%len(loop))):
                        continue
                    self.tri([a,b,c],[uv]*3)
                    del loop[i]
                    break
                else:
                    raise AssertionError(('cannot triangulate panel',loop))
            self.tri(loop,[uv]*3)
        self.panels.clear()

    def index(self, value, kind):
        value = tuple(round(x, 6) for x in value)
        values = (self.v, self.vt, self.vn)[kind]
        if value not in self.maps[kind]:
            values.append(value)
            self.maps[kind][value] = len(values)
        return self.maps[kind][value]

    def position(self, point, normal, uv):
        # Source-like seams: position records split only at a flat normal/UV boundary.
        key = (point, tuple(round(x, 4) for x in normal), uv)
        if key not in self.maps[0]:
            self.v.append(point)
            self.maps[0][key] = len(self.v)
        return self.maps[0][key]

    def tri(self, points, uvs=None):
        points = [tuple(round(x, 6) for x in p) for p in points]
        # Compute normals from the float32 positions ParseObj actually passes to Godot.
        loaded = [tuple(struct.unpack("f", struct.pack("f", x))[0] for x in p) for p in points]
        n = cross(sub(loaded[1], loaded[0]), sub(loaded[2], loaded[0]))
        length = math.sqrt(sum(x*x for x in n))
        assert length > 1e-10, ("degenerate authored triangle", points)
        normal = tuple(x/length for x in n)
        # OBJ V is up. This samples the sedan palette's alpha-zero (0,0) texel AFTER ParseObj's V flip.
        uvs = uvs or [(0.125, 0.75)] * 3
        self.f.append(tuple((self.position(p, normal, uv), self.index(uv, 1), self.index(normal, 2)) for p, uv in zip(points, uvs)))

    def quad(self, points, uv=None):
        for ids in ((0, 1, 2), (0, 2, 3)):
            self.tri([points[i] for i in ids], [uv]*3 if uv else None)

    def write(self, name, weld_positions=False):
        self.finish_panels()
        if weld_positions:
            # ParseObj applies vn/vt PER CORNER. Position records can be shared
            # across hard edges without changing flat shading or palette seams.
            positions = list(dict.fromkeys(self.v))
            ids = {p:i+1 for i,p in enumerate(positions)}
            self.f = [tuple((ids[self.v[v-1]],t,n) for v,t,n in face) for face in self.f]
            self.v = positions
        lines = ["# Generated by tools/build_wagon.py; metres, +Y up, forward -Z.",
                 "# Triangles only; explicit v/vt/vn; vt V is flipped by ContentProvider.ParseObj.", f"g {Path(name).stem}"]
        for tag, data in (("v", self.v), ("vt", self.vt), ("vn", self.vn)):
            lines.extend(tag + " " + " ".join(fmt(x) for x in value) for value in data)
        lines.extend("f " + " ".join("/".join(map(str, corner)) for corner in face) for face in self.f)
        (CONTENT / name).write_text("\n".join(lines) + "\n")



def build_exhaust():
    """THE SEDAN'S OWN EXHAUST, lifted out of its body mesh (strawberry: "REMOVE the exhaust! take the
    one from the sedan and put it where it should go").

    No vehicle ships an exhaust as a part file -- the sedan's is baked into sedan_body.txt: a tapered
    square duct, 0.197 x 0.197 in section, running 2.0 m from z 0.942 under the car back to a tip at
    z 2.942, on the right at x 0.695..0.892. Ten triangles. Its tip is flush with the sedan's rearmost
    point (2.942378), so the wagon's is placed flush with the wagon's rearmost point in turn.

    Y and X are carried over untouched. On the sedan the duct's lowest point (-0.273) sits below its
    own bumper lip (-0.159), so keeping Y gives the wagon the same relationship and the same silhouette
    rather than a re-invented one.
    """
    sedan = obj(CONTENT/'sedan_body.txt')
    verts = sedan['vertices']
    seed = {i for i,(x,y,z) in enumerate(verts) if .68 <= x <= .90 and -.28 <= y <= -.06 and z >= 2.75}
    faces = [f for f in sedan['faces']
             if any(int(c.split('/')[0])-1 in seed for c in f)]
    assert len(faces) == 10, ('sedan exhaust is not 10 triangles', len(faces))
    src_tip = max(z for _,_,z in verts)
    dst_tip = max(max(z for _,_,z in obj(CONTENT/f'wagon_{n}.txt')['vertices'])
                  for n in ('body','bumper_rear'))
    dz = dst_tip - src_tip
    mesh = Mesh()
    for f in faces:
        tri = [verts[int(c.split('/')[0])-1] for c in f]
        moved = [(x, y, z+dz) for x,y,z in tri]
        n = cross(sub(moved[1],moved[0]), sub(moved[2],moved[0]))
        mesh.panel(moved, n, DARK)
    mesh.write('wagon_exhaust.txt', weld_positions=True)
    out = obj(CONTENT/'wagon_exhaust.txt')
    tip = ((out['lo'][0]+out['hi'][0])/2, 0, out['hi'][2])
    at_tip = [v for v in out['vertices'] if abs(v[2]-out['hi'][2]) < 1e-6]
    tip = (tip[0], sum(v[1] for v in at_tip)/len(at_tip), tip[2])
    print(f'Exhaust: sedan duct, {len(mesh.f)} triangles, Z {dz:+.6f} -> tip flush at {dst_tip:.6f}; '
          f'outlet centre {tuple(round(c,4) for c in tip)}')
    return tip


def body_surface_z(mesh, x, y, front):
    """Z of the body surface a ray down Z hits at (x,y) -- the nearest face if `front`, else the furthest.

    Vertex proximity will not do: these bodies are low-poly and there is usually no vertex anywhere near a
    lamp centre, only a face spanning past it."""
    V, hits = mesh['vertices'], []
    for f in mesh['faces']:
        a, b, c = (V[int(i.split('/')[0])-1] for i in f)
        den = (b[1]-c[1])*(a[0]-c[0]) + (c[0]-b[0])*(a[1]-c[1])
        if abs(den) < 1e-12:
            continue
        u = ((b[1]-c[1])*(x-c[0]) + (c[0]-b[0])*(y-c[1])) / den
        v = ((c[1]-a[1])*(x-c[0]) + (a[0]-c[0])*(y-c[1])) / den
        w = 1 - u - v
        if u < -1e-9 or v < -1e-9 or w < -1e-9:
            continue
        hits.append(u*a[2] + v*b[2] + w*c[2])
    assert hits, ('no body surface behind the lamp', x, y)
    return min(hits) if front else max(hits)


def outward_quad(mesh, points, direction, uv=None):
    n = cross(sub(points[1], points[0]), sub(points[2], points[0]))
    if sum(a*b for a,b in zip(n,direction)) < 0:
        points = list(reversed(points))
    mesh.quad(points, uv)


def sedan_front():
    """Average the bilateral export jitter, keeping the measured Y stations."""
    sedan = obj(CONTENT/'sedan_body.txt')
    vs = sorted(set(sedan['vertices']))
    def station(y, inner=False):
        ps = [p for p in vs if p[2] < -1.1 and abs(p[1]-y)<.0001
              and ((.97 < abs(p[0]) < .99) if inner else (1.22 < abs(p[0]) < 1.24))]
        z = min(p[2] for p in ps)
        ps = [p for p in ps if abs(p[2]-z)<.0001]
        return tuple(sum(p[i] for p in ps)/len(ps) for i in (1,2))
    specs = read_specs(('sedan','wagon'))
    axle_s = min(w[2] for w in specs['sedan']['Wheels'])
    axle_w = min(w[2] for w in specs['wagon']['Wheels'])
    scale = (axle_w+2.90)/(axle_s-sedan['lo'][2])
    def mapped(yz):
        y,z=yz
        if abs(y-round(y,3)) < .000002:
            y=round(y,3)
        return (round(y,6),round(axle_w+(z-axle_s)*scale,6))
    return dict(scale=scale, source=sedan,
                nose=mapped(station(.999953,True)),
                shoulder=mapped(station(.874953)),
                crease=mapped(station(1.125,True)),
                valance=mapped(station(.125)),
                cowl=station(1.125))


def bumper_band(name, sign):
    """The measured, already closed 28-triangle bumper in each donor body."""
    source = obj(CONTENT/f'{name}_body.txt')
    faces = []
    for face in source['faces']:
        points = [source['vertices'][int(c.split('/')[0])-1] for c in face]
        if all(sign*z > 2.1 and
               min(abs(y+.158647),abs(y-.100803)) < .000003 for x,y,z in points):
            assert len(face) == 3
            faces.append(points)
    assert len(faces) == 28, (name, sign, 'donor bumper band changed')
    return faces


def fit_bumpers(fascia):
    for label,sign in [('front',-1),('rear',1)]:
        faces = bumper_band('hatchback',sign)
        points = {p for f in faces for p in f}
        lo = tuple(min(p[i] for p in points) for i in range(3))
        hi = tuple(max(p[i] for p in points) for i in range(3))
        # Fit to the wall width by shrinking 0.074%, never bridge an inset part.
        xscale = 2*HALF_WIDTH/(hi[0]-lo[0])
        dy = -.12-lo[1]  # retain donor height; lower face sits at the old sill Y.
        # Keep the complete outward band exposed, with the back entering the
        # closed body. Retain the angled outer contour; trim buried depth below.
        front_points = [p for p in points if sign*p[2] > sign*(lo[2]+hi[2])/2]
        dz = (min(fascia(y+dy)-z for x,y,z in front_points)-.08 if sign < 0
              else REAR_Z-min(p[2] for p in front_points)+.08)
        part = Mesh()
        cut = set()
        for tri in faces:
            fitted = [((x-(lo[0]+hi[0])/2)*xscale,y+dy,z+dz) for x,y,z in tri]
            # Remove only the donor's micrometre export jitter at outer tips.
            fitted = [(math.copysign(HALF_WIDTH,x) if abs(abs(x)-HALF_WIDTH)<.000003 else x,y,z)
                      for x,y,z in fitted]
            # Trim the buried portion at the attachment plane. This avoids
            # coplanar overlapping side walls where the donor wraps backward.
            def distance(p):
                return sign*(p[2]-(fascia(p[1]) if sign < 0 else REAR_Z))
            clipped = []
            for a,b in zip(fitted,fitted[1:]+fitted[:1]):
                da,db = distance(a),distance(b)
                if da >= 0:
                    clipped.append(a)
                if da*db < 0:
                    t = da/(da-db)
                    p = tuple(a[i]+t*(b[i]-a[i]) for i in range(3))
                    clipped.append(p)
                    cut.add(tuple(round(v,6) for v in p))
            if len(clipped) >= 3:
                part.panel(clipped,cross(sub(fitted[1],fitted[0]),sub(fitted[2],fitted[0])),DARK)
        # Only the cut closure is new; all exposed surfaces come from the donor.
        cx = sum(p[0] for p in cut)/len(cut)
        cy = sum(p[1] for p in cut)/len(cut)
        part.panel(sorted(cut,key=lambda p:math.atan2(p[1]-cy,p[0]-cx)),(0,0,-sign),DARK)
        part.write(f'wagon_bumper_{label}.txt', weld_positions=True)
        # Lower the finished part, including its attachment cap. Re-fitting at
        # the new Y would also change Z against the sloped fascia. Keep Z fixed.
        name = f'wagon_bumper_{label}.txt'
        # Sit the lip FLUSH with the floor. On the sedan/hatchback/golf the bumper lip IS the lowest
        # point of the outer flank -- same height, no step -- so derive the drop from FLOOR_Y rather
        # than carrying a hand-tuned constant that silently goes wrong when the floor moves.
        lip = min(v[1] for v in obj(CONTENT/name)['vertices'])
        drop = FLOOR_Y - lip
        translate_asset(name, name, lambda p, d=drop: (0, d, 0))
        print(f'Hatchback {label}: X scale {xscale:.9f}; Y {dy:+.6f}; Z {dz:+.6f}')
        print(f'  Lip {lip:+.6f} -> {FLOOR_Y:+.6f} (drop {drop:+.6f}), flush with the floor; Z unchanged')


def build():
    assert (ROOT/'notes/vehicle_style.md').exists()
    body = Mesh()
    panel = body.panel
    hood = sedan_front()
    yn,zn=hood['nose']; yc,zc=1.125,-1.25  # Hood ends at the windscreen, with no shelf.
    ys,_=hood['shoulder']; yv,zv=hood['valance']
    def hood_y(z):
        return yn+(yc-yn)*(z-zn)/(zc-zn)

    def fascia(y):
        return zv+(zn-zv)*(y-yv)/(yn-yv)

    # Both leading edges share the fascia plane. Dropping Y at constant Z
    # would twist the outer fascia into the old forward-facing wedges.
    shoulder_nose_z = fascia(ys)
    hood_slope = (yc-yn)/(zc-zn)
    bevel_slope = (hood_y(shoulder_nose_z)-ys)/(HALF_WIDTH-.98)
    # Intersect the planar bevel with the existing windscreen/A-post plane.
    # The outer foot extends down that plane; the inner foot and glass stay put.
    screen_slope = (1.92-yc)/(-.80-zc)
    shoulder_cowl_z = zc-bevel_slope*(HALF_WIDTH-.98)/(screen_slope-hood_slope)
    shoulder_cowl_y = yc+screen_slope*(shoulder_cowl_z-zc)

    # Continue the painted fascia to the flat full-width floor, with no lip.
    floor_front_z = round(fascia(FLOOR_Y), 6)
    zs = [floor_front_z,-2.32,shoulder_cowl_z,zc,-1.00,2.32,REAR_Z]

    for sign in (-1,1):
        side=(sign,0,0)
        # Side walls drop vertically to the same Y across the whole length.
        upper_zs = [floor_front_z,shoulder_cowl_z,-1.00,2.32,REAR_Z]
        upper = [(ys,shoulder_nose_z),(shoulder_cowl_y,shoulder_cowl_z),
                 (1.10,-1.00),(1.10,2.32),(1.10,2.68)]
        for i,(z0,z1) in enumerate(zip(upper_zs,upper_zs[1:])):
            x0=x1=sign*HALF_WIDTH
            panel([(x0,FLOOR_Y,z) for z in zs if z0 <= z <= z1]+
                  [(x1,*upper[i+1]),(x0,*upper[i])],side)
        a,b=(sign*.98,yn,zn),(sign*HALF_WIDTH,ys,shoulder_nose_z)
        c,d=(sign*HALF_WIDTH,shoulder_cowl_y,shoulder_cowl_z),(sign*.98,yc,zc)
        panel([a,b,c],(0,1,0))
        panel([a,c,d],(0,1,0))
    # A single painted plane covers the former grille and stepped bumper.
    # Retain the measured valance station as a coplanar subdivision.
    panel([(-HALF_WIDTH,FLOOR_Y,floor_front_z),(HALF_WIDTH,FLOOR_Y,floor_front_z),
           (HALF_WIDTH,yv,zv),(-HALF_WIDTH,yv,zv)],(0,0,-1))
    panel([(-HALF_WIDTH,yv,zv),(HALF_WIDTH,yv,zv),
           (HALF_WIDTH,ys,shoulder_nose_z),(.98,yn,zn),
           (-.98,yn,zn),(-HALF_WIDTH,ys,shoulder_nose_z)],(0,0,-1))
    for sign in (-1,1):
        # Explicit mirrored triangles avoid ear-clipping a fan from one side.
        a,b=(0,yn,zn),(sign*.98,yn,zn)
        c=(sign*.98,yc,zc)
        panel([a,b,c],(0,1,0))
    # Meet the windscreen directly; there is no horizontal cowl strip.
    panel([(-.98,yc,zc),(.98,yc,zc),(0,yn,zn)],(0,1,0))
    # Firewall, passenger floor, raised load deck, and inside of the tailgate.
    panel([(-.98,-.12,-1.25),(.98,-.12,-1.25),(.98,1.125,-1.25),(-.98,1.125,-1.25)],(0,0,1),DARK)
    panel([(-.98,-.12,-1.25),(.98,-.12,-1.25),(.98,-.12,1.36),(-.98,-.12,1.36)],(0,1,0),DARK)
    panel([(-.98,-.12,1.36),(.98,-.12,1.36),(.98,.18,1.36),(-.98,.18,1.36)],(0,0,-1),DARK)
    panel([(-.98,.18,1.36),(.98,.18,1.36),(.98,.18,2.53),(-.98,.18,2.53)],(0,1,0),DARK)
    panel([(-.98,.18,2.53),(.98,.18,2.53),(.98,1.10,2.53),(-.98,1.10,2.53)],(0,0,-1))
    panel([(-.98,1.10,2.53),(.98,1.10,2.53),(.98,1.10,2.68),(-.98,1.10,2.68)],(0,1,0))
    panel([(-HALF_WIDTH,FLOOR_Y,floor_front_z),(HALF_WIDTH,FLOOR_Y,floor_front_z),
           (HALF_WIDTH,FLOOR_Y,REAR_Z),(-HALF_WIDTH,FLOOR_Y,REAR_Z)],(0,-1,0),DARK)

    # Close the entire rear down to the floor; the old latch cell is paint.
    panel([(-HALF_WIDTH,FLOOR_Y,REAR_Z),(HALF_WIDTH,FLOOR_Y,REAR_Z),
           (HALF_WIDTH,1.10,REAR_Z),(-HALF_WIDTH,1.10,REAR_Z)],(0,0,1))

    # The accepted four posts, three apertures, and flat roof. Their bases and
    # tops join the wall/roof directly; there are no mating caps inside solids.
    posts = [[(1.125,-1.25),(1.92,-.80),(1.92,-.55),(1.10,-1.00)],
             [(1.10,.12),(1.92,.12),(1.92,.37),(1.10,.37)],
             [(1.10,1.35),(1.92,1.35),(1.92,1.55),(1.10,1.55)],
             [(1.10,2.48),(1.92,2.36),(1.92,2.56),(1.10,2.68)]]
    # Continue the actual A-post/windscreen plane through the roof thickness.
    # The glass and post endpoints stay fixed; only the former vertical cap moves.
    (base_y,base_z),(top_y,top_z)=posts[0][:2]
    roof_front_z=top_z+(2.17-top_y)*(top_z-base_z)/(top_y-base_y)
    (top_y,top_z),(base_y,base_z)=posts[3][2:]
    roof_rear_z=top_z+(2.17-top_y)*(top_z-base_z)/(top_y-base_y)
    for sign in (-1,1):
        xo,xi=sign*HALF_WIDTH,sign*.98
        for post_id,post in enumerate(posts):
            outer = ([(shoulder_cowl_y,shoulder_cowl_z)]+post[1:]) if post_id == 0 else post
            panel([(xo,y,z) for y,z in outer],(sign,0,0))
            panel([(xi,y,z) for y,z in post],(-sign,0,0))
            for i,j in ((0,1),(2,3)):
                a,b=post[i],post[j]
                panel([(xo,*outer[i]),(xo,*outer[j]),(xi,*b),(xi,*a)],(0,b[1]-a[1],a[0]-b[0]))
        for a,b in zip(posts,posts[1:]):
            z0,z1=a[3][1],b[0][1]
            panel([(xi,1.10,z0),(xo,1.10,z0),(xo,1.10,z1),(xi,1.10,z1)],(0,1,0))
            z0,z1=a[2][1],b[1][1]
            panel([(xi,1.92,z0),(xo,1.92,z0),(xo,1.92,z1),(xi,1.92,z1)],(0,-1,0))
        panel([(xi,-.12,-1.25),(xi,-.12,1.36),(xi,.18,1.36),(xi,.18,2.53),
               (xi,1.10,2.53),(xi,1.10,-1.),(xi,1.125,-1.25)],(-sign,0,0),DARK)
        panel([(xo,1.92,-.80),(xo,2.17,roof_front_z),(xo,2.17,roof_rear_z),(xo,1.92,2.56)],(sign,0,0))
    panel([(-HALF_WIDTH,2.17,roof_front_z),(HALF_WIDTH,2.17,roof_front_z),
           (HALF_WIDTH,2.17,roof_rear_z),(-HALF_WIDTH,2.17,roof_rear_z)],(0,1,0))
    panel([(-.98,1.92,-.80),(.98,1.92,-.80),(.98,1.92,2.56),(-.98,1.92,2.56)],(0,-1,0))
    panel([(-HALF_WIDTH,1.92,-.80),(HALF_WIDTH,1.92,-.80),
           (HALF_WIDTH,2.17,roof_front_z),(-HALF_WIDTH,2.17,roof_front_z)],(0,1,-1))
    panel([(-HALF_WIDTH,1.92,2.56),(HALF_WIDTH,1.92,2.56),
           (HALF_WIDTH,2.17,roof_rear_z),(-HALF_WIDTH,2.17,roof_rear_z)],(0,0,1))
    body.write('wagon_body.txt', weld_positions=True)
    shutil.copyfile(CONTENT/'sedan_palette.png',CONTENT/'wagon_palette.png')

    panes = {
        'windshield': [(-.98,1.125,-1.25),(.98,1.125,-1.25),(.98,1.92,-.80),(-.98,1.92,-.80)],
        'rear': [(-.98,1.10,2.68),(-.98,1.92,2.56),(.98,1.92,2.56),(.98,1.10,2.68)],
    }
    for side,sign in [('l',-1),('r',1)]:
        x=sign*(HALF_WIDTH-.004)
        panes[side+'_front']=[(x,1.10,-1.00),(x,1.10,.12),(x,1.92,.12),(x,1.92,-.55)]
        panes[side+'_rear']=[(x,1.10,.37),(x,1.10,1.35),(x,1.92,1.35),(x,1.92,.37)]
        panes[side+'_mid1']=[(x,1.10,1.55),(x,1.10,2.48),(x,1.92,2.36),(x,1.92,1.55)]
    for label,points in panes.items():
        mesh=Mesh()
        direction = (0,0,-1) if label=='windshield' else (0,0,1) if label=='rear' else (-1,0,0) if label.startswith('l_') else (1,0,0)
        outward_quad(mesh,points,direction)
        mesh.write('wagon_glass_'+label+'.txt')

    # MATCH THE SEDAN'S PROTRUSION, not a remembered Z offset (strawberry 2026-09-09: "reduce the
    # thickness of tail lights to match sedan. and thicken headlights? to match sedan on the suv").
    #
    # The lenses ARE the sedan's meshes, identical to the micrometre -- what he was reading as thickness
    # is how far they stand PROUD of the bodywork behind them, and this car's nose and tailgate are not
    # where the sedan's are. Raycast down Z through each lens centre, on the sedan and on this body:
    #     headlights  sedan 0.0641 proud   suv 0.0191   (sunk almost flush)
    #     taillights  sedan 0.0288 proud   suv 0.1200   (stood off four times too far)
    # So the offsets are DERIVED here rather than carried as .150/.012 constants: whatever the fascia or
    # the tailgate does next, the lamps keep the sedan's stand-off instead of quietly drifting again.
    for lamp, front in (('headlights', True), ('taillights', False)):
        src = obj(CONTENT/f'sedan_{lamp}.txt')
        sedan_body = obj(CONTENT/'sedan_body.txt')
        xs = [abs(v[0]) for v in src['vertices']]; ys = [v[1] for v in src['vertices']]
        x, y = (min(xs)+max(xs))/2, (min(ys)+max(ys))/2
        lo_z, hi_z = min(v[2] for v in src['vertices']), max(v[2] for v in src['vertices'])
        want = (body_surface_z(sedan_body, x, y, front) - lo_z) if front else \
               (hi_z - body_surface_z(sedan_body, x, y, front))
        here = body_surface_z(obj(CONTENT/'wagon_body.txt'), x, y, front)
        delta = (here - want - lo_z) if front else (here + want - hi_z)
        translate_asset(f'sedan_{lamp}.txt', f'wagon_{lamp}.txt', lambda p, d=delta: (0, 0, d))
        got = obj(CONTENT/f'wagon_{lamp}.txt')
        gz = min(v[2] for v in got['vertices']) if front else max(v[2] for v in got['vertices'])
        actual = (here - gz) if front else (gz - here)
        assert abs(actual - want) < 1e-6, (lamp, actual, want)
        print(f'Sedan {lamp}: Z {delta:+.6f} -> {actual:.4f} m proud, matching the sedan\'s {want:.4f}')
    translate_asset('sedan_steer.txt', 'wagon_steer.txt', lambda p: (0,0,.205))
    seats = obj(CONTENT/'sedan_seats.txt')
    assert all(len({seats['vertices'][int(c.split('/')[0])-1][2] < 0 for c in f}) == 1
               for f in seats['faces']), 'source seat triangle crosses row split'
    translate_asset('sedan_seats.txt', 'wagon_seats.txt', lambda p: (0,0,.205 if p[2]<0 else 0))
    fit_bumpers(fascia)
    build_exhaust()
    saved=obj(CONTENT/'wagon_body.txt')
    assert saved['lo']==(-HALF_WIDTH,FLOOR_Y,floor_front_z) and saved['hi']==(HALF_WIDTH,2.17,REAR_Z)
    print(f'Roof rear Z: {roof_rear_z:.9f}')
    print('Body:',len(body.v),'v /',len(body.f),'triangles;',len(set(body.v)),'unique positions')
    print('AABB:',saved['lo'],saved['hi'])


if __name__=='__main__':
    build()
