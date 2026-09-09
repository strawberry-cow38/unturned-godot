#!/usr/bin/env python3
"""Build the accepted wagon silhouette with joined surfaces and a sedan hood.

The sedan front stations are measured from its body; the cabin and axle rig
retain the independently authored wagon coordinates. No Boolean overlapping
boxes: each panel bounds either the outside or the open passenger/load space.
"""
import math
from pathlib import Path
import shutil
import struct
from measure_vehicles import CONTENT, ROOT, obj, fmt, read_specs

PAINT = (0.125, 0.75)
DARK = (0.375, 0.25)
HALF_WIDTH = 1.26  # One outer wall plane from bumper to bumper, including every post.

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
                lower=mapped(station(-.125)),
                cowl=station(1.125),
                bumper_lo=round(min(p[1] for p in vs if p[2]<-2.9),6),
                bumper_hi=round(max(p[1] for p in vs if p[2]<-2.9),6))


def build():
    assert (ROOT/'notes/vehicle_style.md').exists()
    body = Mesh()
    panel = body.panel
    hood = sedan_front()
    yn,zn=hood['nose']; yc,zc=hood['crease']
    ys,_=hood['shoulder']; yv,zv=hood['valance']
    yl,zl=hood['lower']; blo,bhi=hood['bumper_lo'],hood['bumper_hi']
    # Where the sedan's lower valance meets the top of its bumper.
    zj = round(zl+(zv-zl)*(bhi-yl)/(yv-yl),6)
    front = [(yl,zn),(blo,-2.90),(bhi,-2.90),(bhi,zj),(yv,zv)]

    def hood_y(z):
        return yn+(yc-yn)*(z-zn)/(zc-zn)

    # Longitudinal side stations have no wheel-dependent coordinates at all.
    zs = [zn,-2.32,zc,-1.25,-1.00,2.32,2.68]
    def shoulder(z):
        return hood_y(z)-.125 if z <= zc else 1.0
    def rail(z):
        return 1.125 if z == -1.25 else 1.10
    def sill(z):
        return yl if z == zn else -.12

    for sign in (-1,1):
        side=(sign,0,0)
        # Full-width nose side and stepped sedan bumper profile.
        panel([(sign*HALF_WIDTH,y,z) for y,z in front]+[(sign*HALF_WIDTH,ys,zn)],side)
        # The old -2.32 shoulder split forced a thin fan across the hood.
        # Keep its lower sill corner, but run the upper edge straight to the
        # crease so the inner hood edge has no unnecessary junction vertex.
        upper_zs = [z for z in zs if z != -2.32]
        for z0,z1 in zip(upper_zs,upper_zs[1:]):
            x0=x1=sign*HALF_WIDTH
            panel([(x0,sill(z),z) for z in zs if z0 <= z <= z1]+
                  [(x1,shoulder(z1),z1),(x0,shoulder(z0),z0)],side)
            if z1 <= zc:
                # Sedan's transverse 0.125 m hood shoulder, at full wagon width.
                a,b=(sign*.98,hood_y(z0),z0),(x0,shoulder(z0),z0)
                c,d=(x1,shoulder(z1),z1),(sign*.98,hood_y(z1),z1)
                panel([a,b,c],(0,1,0))
                panel([a,c,d],(0,1,0))
            elif z0 == zc:
                # Split the shoulder-to-cowl transition into planar facets.
                # Its outer rise is now on the wall, so it cannot be projected
                # as one roof-facing polygon without collapsing an edge.
                a,b,c=(x0,1.,z0),(x1,1.,z1),(x1,1.125,z1)
                d,e=(sign*.98,1.125,z1),(sign*.98,yc,z0)
                panel([a,b,c],side)
                panel([a,c,e],(0,1,0))
                panel([c,d,e],(0,1,0))
            else:
                panel([(x0,1.,z0),(x1,1.,z1),
                       (sign*HALF_WIDTH,rail(z1),z1),(sign*HALF_WIDTH,rail(z0),z0)],side)
        # A single underside bevel joins the sill to the bottom sheet.
        for z0,z1 in zip(zs,zs[1:]):
            panel([(sign*HALF_WIDTH,sill(z0),z0),(sign*.99,-.27,z0),
                   (sign*.99,-.27,z1),(sign*HALF_WIDTH,sill(z1),z1)],(sign,-1,0),DARK)
        for z0,z1 in ((2.68,2.76),(2.76,2.90)):
            w0=w1=HALF_WIDTH
            panel([(sign*w0,-.12,z0),(sign*w1,-.12,z1),
                   (sign*w1,.16,z1),(sign*w0,.16,z0)],side,DARK)
            panel([(sign*w0,-.12,z0),(sign*.99,-.27,z0),
                   (sign*.99,-.27,z1),(sign*w1,-.12,z1)],(sign,-1,0),DARK)

    # Across the stepped front. The hood centre is one longitudinal slope
    # followed by the sedan's level cowl (shortened to the accepted A-post).
    for (y0,z0),(y1,z1) in zip(front,front[1:]):
        panel([(-HALF_WIDTH,y0,z0),(HALF_WIDTH,y0,z0),(HALF_WIDTH,y1,z1),(-HALF_WIDTH,y1,z1)],(0,z1-z0,y0-y1),DARK if y0 in (blo,bhi) and y1 in (blo,bhi) else PAINT)
    panel([(-.99,-.27,zn),(.99,-.27,zn),(HALF_WIDTH,yl,zn),(-HALF_WIDTH,yl,zn)],(0,0,-1),DARK)
    # The central fascia is partitioned around the grille, which is a material
    # patch on this surface; it has no coincident hidden backing or floating box.
    def fascia(y):
        return zv+(zn-zv)*(y-yv)/(yn-yv)
    for x0,x1 in zip((-.98,-.48,.48),(-.48,.48,.98)):
        for y0,y1 in zip((yv,.36),(.36,.54)):
            panel([(x0,y0,fascia(y0)),(x1,y0,fascia(y0)),
                   (x1,y1,fascia(y1)),(x0,y1,fascia(y1))],(0,0,-1),
                  DARK if x0==-.48 and y0==.36 else PAINT)
    # Retriangulate only the adjoining upper fascia to weld the hood's new
    # nose midpoint; grille corners below it and the fascia surface stay fixed.
    panel([(-.48,.54,fascia(.54)),(.48,.54,fascia(.54)),(0,yn,zn)],(0,0,-1))
    for sign in (-1,1):
        a,b=(sign*.98,.54,fascia(.54)),(sign*.48,.54,fascia(.54))
        c,d=(0,yn,zn),(sign*.98,yn,zn)
        panel([a,b,c],(0,0,-1))
        panel([a,c,d],(0,0,-1))
    for sign in (-1,1):
        panel([(sign*.98,yv,zv),(sign*HALF_WIDTH,yv,zv),(sign*HALF_WIDTH,ys,zn),(sign*.98,yn,zn)],(0,0,-1))
        # Explicit mirrored triangles avoid ear-clipping a fan from one side.
        a,b=(0,yn,zn),(sign*.98,yn,zn)
        c=(sign*.98,yc,zc)
        panel([a,b,c],(0,1,0))
    # Meet the existing cowl edge without subdividing its narrow strip.
    panel([(-.98,yc,zc),(.98,yc,zc),(0,yn,zn)],(0,1,0))
    panel([(-.98,yc,zc),(.98,yc,zc),(.98,1.125,-1.25),(-.98,1.125,-1.25)],(0,1,0))
    # Firewall, passenger floor, raised load deck, and inside of the tailgate.
    panel([(-.98,-.12,-1.25),(.98,-.12,-1.25),(.98,1.125,-1.25),(-.98,1.125,-1.25)],(0,0,1),DARK)
    panel([(-.98,-.12,-1.25),(.98,-.12,-1.25),(.98,-.12,1.36),(-.98,-.12,1.36)],(0,1,0),DARK)
    panel([(-.98,-.12,1.36),(.98,-.12,1.36),(.98,.18,1.36),(-.98,.18,1.36)],(0,0,-1),DARK)
    panel([(-.98,.18,1.36),(.98,.18,1.36),(.98,.18,2.53),(-.98,.18,2.53)],(0,1,0),DARK)
    panel([(-.98,.18,2.53),(.98,.18,2.53),(.98,1.10,2.53),(-.98,1.10,2.53)],(0,0,-1))
    panel([(-.98,1.10,2.53),(.98,1.10,2.53),(.98,1.10,2.68),(-.98,1.10,2.68)],(0,1,0))
    panel([(-.99,-.27,zn),(.99,-.27,zn),(.99,-.27,2.90),(-.99,-.27,2.90)],(0,-1,0),DARK)

    # Wagon-specific full-width tailgate and bumper: no sedan boot-lid recess.
    # The latch joins a partitioned rear surface, with its back face omitted.
    for z0,z1 in ((2.68,2.76),(2.76,2.90)):
        w0=w1=HALF_WIDTH
        panel([(-w0,.16,z0),(w0,.16,z0),(w1,.16,z1),(-w1,.16,z1)],(0,1,0),DARK)
    panel([(-HALF_WIDTH,-.12,2.90),(HALF_WIDTH,-.12,2.90),(HALF_WIDTH,.16,2.90),(-HALF_WIDTH,.16,2.90)],(0,0,1),DARK)
    panel([(-.99,-.27,2.90),(.99,-.27,2.90),(HALF_WIDTH,-.12,2.90),(-HALF_WIDTH,-.12,2.90)],(0,0,1),DARK)
    for x0,x1 in zip((-.98,-.23,.23),(-.23,.23,.98)):
        for y0,y1 in zip((.16,.89,.96),(.89,.96,1.10)):
            if x0==-.23 and y0==.89:
                base=[(x0,y0,2.68),(x1,y0,2.68),(x1,y1,2.68),(x0,y1,2.68)]
                tip=[(x,y,2.70) for x,y,z in base]
                panel(tip,(0,0,1),DARK)
                for a,b,c,d in zip(base,base[1:]+base[:1],tip[1:]+tip[:1],tip):
                    panel([a,b,c,d],(a[0]+b[0],a[1]+b[1]-1.85,0),DARK)
            else:
                panel([(x0,y0,2.68),(x1,y0,2.68),(x1,y1,2.68),(x0,y1,2.68)],(0,0,1))
    for sign in (-1,1):
        panel([(sign*.98,.16,2.68),(sign*HALF_WIDTH,.16,2.68),
               (sign*HALF_WIDTH,1.,2.68),(sign*HALF_WIDTH,1.10,2.68),(sign*.98,1.10,2.68)],(0,0,1))

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
    for sign in (-1,1):
        xo,xi=sign*HALF_WIDTH,sign*.98
        for post in posts:
            panel([(xo,y,z) for y,z in post],(sign,0,0))
            panel([(xi,y,z) for y,z in post],(-sign,0,0))
            for a,b in ((post[0],post[1]),(post[2],post[3])):
                panel([(xo,*a),(xo,*b),(xi,*b),(xi,*a)],(0,b[1]-a[1],a[0]-b[0]))
        for a,b in zip(posts,posts[1:]):
            z0,z1=a[3][1],b[0][1]
            panel([(xi,1.10,z0),(xo,1.10,z0),(xo,1.10,z1),(xi,1.10,z1)],(0,1,0))
            z0,z1=a[2][1],b[1][1]
            panel([(xi,1.92,z0),(xo,1.92,z0),(xo,1.92,z1),(xi,1.92,z1)],(0,-1,0))
        panel([(xi,-.12,-1.25),(xi,-.12,1.36),(xi,.18,1.36),(xi,.18,2.53),
               (xi,1.10,2.53),(xi,1.10,-1.),(xi,1.125,-1.25)],(-sign,0,0),DARK)
        panel([(xo,1.92,-.80),(xo,2.17,roof_front_z),(xo,2.17,2.56),(xo,1.92,2.56)],(sign,0,0))
    panel([(-HALF_WIDTH,2.17,roof_front_z),(HALF_WIDTH,2.17,roof_front_z),
           (HALF_WIDTH,2.17,2.56),(-HALF_WIDTH,2.17,2.56)],(0,1,0))
    panel([(-.98,1.92,-.80),(.98,1.92,-.80),(.98,1.92,2.56),(-.98,1.92,2.56)],(0,-1,0))
    panel([(-HALF_WIDTH,1.92,-.80),(HALF_WIDTH,1.92,-.80),
           (HALF_WIDTH,2.17,roof_front_z),(-HALF_WIDTH,2.17,roof_front_z)],(0,1,-1))
    panel([(-HALF_WIDTH,1.92,2.56),(HALF_WIDTH,1.92,2.56),
           (HALF_WIDTH,2.17,2.56),(-HALF_WIDTH,2.17,2.56)],(0,0,1))
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

    # Keep the lamp approach: existing lenses and colour, each translated to
    # the new fascia, paired with the same delta in SpotPos/TailPos in the spec.
    for lamp,delta in [('headlights',.150),('taillights',.012)]:
        source=obj(CONTENT/('sedan_'+lamp+'.txt'))
        lamps=Mesh()
        for face in source['faces']:
            cs=[tuple(int(i)-1 for i in c.split('/')) for c in face]
            lamps.tri([(source['vertices'][v][0],source['vertices'][v][1],source['vertices'][v][2]+delta) for v,t,n in cs],
                      [(source['uvs'][t][0],1-source['uvs'][t][1]) for v,t,n in cs])
        lamps.write('wagon_'+lamp+'.txt')
    saved=obj(CONTENT/'wagon_body.txt')
    assert saved['lo']==(-1.26,-.27,-2.9) and saved['hi']==(1.26,2.17,2.9), saved
    print('Body:',len(body.v),'v /',len(body.f),'triangles;',len(set(body.v)),'unique positions')
    print('AABB:',saved['lo'],saved['hi'])


if __name__=='__main__':
    build()
