#!/usr/bin/env python3
"""Deterministic authored fluid meshes. Measurement gate precedes all vertex creation.

Art constants below cite notes/FLUID_ART_MEASUREMENTS.md. Layout coordinates
come from the existing hose/power anchors and primitive envelopes, not a claim
that a retail prop contains this machine. Run measure_fluid_art.py first.
"""
from pathlib import Path
import math
import json
import re
from PIL import Image
from measure_fluid_art import parse, sub, cross, unit

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'game/content/fluid'
assert (ROOT/'notes/FLUID_ART_MEASUREMENTS.md').exists(), 'Measure before authoring'
SURVEY=json.loads((ROOT/'.verify/fluid_art_survey.json').read_text())
REF={Path(s['path']).stem:s for s in SURVEY}
assert REF['Barrel_0']['tri']==156 and {r['n'] for r in REF['Barrel_0']['rings']}=={12}
# Measurements: Barrel_0 bands, Generator_0 motor, Propane_0 stem, hydrant hex rings.
BAND=.1
LIP=.043261
STEM=.04116
HEX=.15249
BORE=.08847
MOTOR=.25
PORTY=.6        # the standard hose height for machines, so hoses between them run level
BARREL_PORTY=.45   # on a real barrel .6-.7 is where the middle rolling hoop is; the fitting goes below it
PORTX={9114:.80, 9116:1.10, 9117:.92, 9121:.85}
FAN_Z=[-.32,0.,.32]   # three branches; mirrored by FluidContainer.Fan and published in the catalog
COLLAR=.06      # spigot collar length; SPOUT is how far the bore stands past it
SPOUT=.12
# Barrel_0's ACTUAL profile, read off the retail OBJ (source Z-up, translated to sit on Y=0):
# a .5-radius shell over a 1.25 m span, with three .1-high bands at radius .543261, the end pair
# STRADDLING the caps so they overhang .046040 at each end. Note .543261-.043261 == .500000
# exactly -- which is why the unchanged +-0.5 hose anchors land precisely on a true barrel's shell.
# The previous drum was TANK=.5-LIP, one lip too narrow, and read as a slim drum next to the
# reference barrel. (strawberry: "the water tank should just be a barrel with those hose
# connections.")
BARREL_R=.5
BARREL_BAND_R=.543261
BARREL_BANDS=[(0.,.1),(.641859,.741859),(1.2218,1.3218)]
BARREL_SHELL=(.04604,1.29604)
BARREL_TOP=1.3218
TANK=BARREL_R
DARK=(59,59,59); METAL=(138,138,138)
BLUE=(87,104,118); BLUE_LIGHT=(99,119,135)
GREEN=(87,119,90); GREEN_LIGHT=(99,135,102)
RED=(162,32,32); RUST=(112,68,68); RUST_LIGHT=(131,87,87)
YELLOW=(213,167,44); WHITE=(219,219,219)
# C# owns the electrical anchors; do not maintain a second coordinate table here.
PANEL_ANCHORS={name:float(value) for name,value in re.findall(
    r'public const float (\w+) = (-?[\d.]+)f;', (ROOT/'game/FluidElectricalPanel.cs').read_text())}

def panel_origin(id):
    kind='Pump' if id==9114 else 'Valve' if id==9115 else 'Powered'
    return (PANEL_ANCHORS.get(kind+'X',0),PANEL_ANCHORS[kind+'Y'],PANEL_ANCHORS[kind+'Z'])

class Mesh:
    def __init__(self): self.v=[]; self.faces=[]; self.groups=[]
    def face(self, points, colour, group, normals=None):
        # Caller supplies outward CCW; OBJ emits Godot's clockwise front winding
        # with explicit outward normals. No triangulation is left to ParseObj.
        ids=[]
        for p in points:
            p=tuple(round(x,6) for x in p)
            try: i=self.v.index(p)
            except ValueError: i=len(self.v); self.v.append(p)
            ids.append(i)
        normal=unit(cross(sub(points[1],points[0]),sub(points[2],points[0])))
        if normals is None: normals=[normal]*len(points)
        # CLOCKWISE FRONT, and do not "fix" this against the retail art -- I did, and it rendered every
        # device inside out. The retail .obj props go through ObjMesh.Load, which negates an axis AND
        # "ALWAYS reverse[s] winding: Unity(LH) verts in Godot(RH) face" (its own comment), so their
        # FILE winding is the opposite of their RUNTIME winding. These files go through
        # ContentProvider.ParseObj, which does neither -- file winding IS runtime winding. Comparing the
        # two on disk measures a real number and answers a different question.
        for i in range(1,len(ids)-1):
            self.faces.append(([ids[0],ids[i+1],ids[i]],colour,[normals[0],normals[i+1],normals[i]],group))
    def box(self, lo, hi, colour=0, group='frame'):
        x,y,z=lo; X,Y,Z=hi
        for p in [((x,y,z),(x,y,Z),(x,Y,Z),(x,Y,z)),((X,y,Z),(X,y,z),(X,Y,z),(X,Y,Z)),
                  ((x,y,Z),(x,y,z),(X,y,z),(X,y,Z)),((x,Y,z),(x,Y,Z),(X,Y,Z),(X,Y,z)),
                  ((X,y,z),(x,y,z),(x,Y,z),(X,Y,z)),((x,y,Z),(X,y,Z),(X,Y,Z),(x,Y,Z))]: self.face(p,colour,group)
    def box_oriented(self, lo, hi, yaw, colour=0, group='frame'):
        """A box built along +X then yawed about Y. For spokes, ribs, anything that radiates."""
        x,y,z=lo; X,Y,Z=hi
        c,sn=math.cos(yaw),math.sin(yaw)
        def r(p): return (p[0]*c-p[2]*sn, p[1], p[0]*sn+p[2]*c)
        for quad in [((x,y,z),(x,y,Z),(x,Y,Z),(x,Y,z)),((X,y,Z),(X,y,z),(X,Y,z),(X,Y,Z)),
                     ((x,y,Z),(x,y,z),(X,y,z),(X,y,Z)),((x,Y,z),(x,Y,Z),(X,Y,Z),(X,Y,z)),
                     ((X,y,z),(x,y,z),(x,Y,z),(X,Y,z)),((x,y,Z),(X,y,Z),(X,Y,Z),(x,Y,Z))]:
            self.face([r(q) for q in quad],colour,group)

    def lathe(self, profile, n, colour=0, group='shell', center=(0,0,0), axis='y', caps=True, end_colour=None):
        # profile is ordered (axial height,radius); repeated height makes an annular face.
        def transform(x,y,z):
            if axis=='x': x,y,z=y,-x,z
            elif axis=='z': x,y,z=x,-z,y
            return (x+center[0],y+center[1],z+center[2])
        rings=[[transform(r*math.cos(i*math.tau/n),h,r*math.sin(i*math.tau/n)) for i in range(n)] for h,r in profile]
        for j,(a,b) in enumerate(zip(rings,rings[1:])):
            h0,r0=profile[j]; h1,r1=profile[j+1]
            for i in range(n):
                normals=None
                # Barrel_0 has 96 interpolated-normal side triangles and 60
                # constant-normal cap triangles. Propane_0's 8-sided body likewise
                # interpolates. Hexagonal motors/fittings and LOD1 stay flat.
                if n>=8 and abs(h1-h0)>1e-6:
                    slope=-(r1-r0)/(h1-h0)
                    def norm(k):
                        p=transform(math.cos(k*math.tau/n),slope,math.sin(k*math.tau/n))
                        return unit(sub(p,center))
                    normals=[norm(i),norm(i),norm((i+1)%n),norm((i+1)%n)]
                    if h1<h0: normals=[tuple(-x for x in v) for v in normals]
                self.face([a[i],b[i],b[(i+1)%n],a[(i+1)%n]],colour,group,normals)
        if caps:
            self.face(rings[0],colour,group)
            self.face(rings[-1][::-1],colour if end_colour is None else end_colour,group)
    def socket(self, anchor, axis, sign=1, low=False, seat=0):
        """A hose connection that STICKS OUT of the body, and is a closed solid.

        It used to be a mouth RECESSED into the shell at the port anchor. Two things were wrong with
        that. The barrel's own rolling bands stand .043261 proud at exactly the port height, so on a
        real barrel they sit in front of the mouth and cover it -- a connection you cannot see into.
        And it was built open (caps=False plus a back face), which audit_fluid_geometry.py counts as
        6 boundary edges per socket: a literal see-through hole, the single most common finding in the
        whole set. A real drum fitting protrudes past the hoop, so this one does too.

        The SIZE is unchanged -- HEX collar, BORE spout, both measured off Fire_Hydrant_0 -- because
        the machines around it grew and these did not. (strawberry: "keeping the Io pipes the same
        size.") The dark end cap reads as the bore without opening the mesh."""
        tmp=Mesh()
        n=6
        spout=BORE*1.45
        # Optional buried continuation is the SAME hex, with no round/lofted boss.
        # Keep a ring at zero for the published anchor, and preserve the exact frozen
        # emitter when seat=0 (tank, valve and hose tool use that path).
        back=[(-seat,HEX)] if seat else []
        if low:
            tmp.lathe(back+[(0,HEX),(COLLAR+SPOUT,HEX)],n,3,'hose_fitting',caps=True,end_colour=2)
        else:
            tmp.lathe(back+[(0,HEX),(COLLAR,HEX),(COLLAR,spout),(COLLAR+SPOUT,spout)],
                      n,3,'hose_fitting',caps=True,end_colour=2)
        def tr(p):
            x,y,z=p
            if axis=='x': x,y,z=sign*y,-sign*x,z
            else: x,y,z=x,-sign*z,sign*y
            return (x+anchor[0],y+anchor[1],z+anchor[2])
        self.add(tmp,transform=tr)
    def loft(self, rings, colour=3, group='boss', caps=True):
        """Closed axial loft, including unequal segment counts (12/8 body -> hex fitting).

        Rings use lathe's outward order. Zip by angular fraction so the transition
        has actual triangles connecting both polygons, without a butted seam.
        """
        for a,b in zip(rings,rings[1:]):
            i=j=0
            while i<len(a) or j<len(b):
                ai=a[i%len(a)]; bj=b[j%len(b)]
                an=(i+1)/len(a) if i<len(a) else 2
                bn=(j+1)/len(b) if j<len(b) else 2
                if abs(an-bn)<1e-8:
                    self.face([ai,bj,b[(j+1)%len(b)]],colour,group)
                    self.face([ai,b[(j+1)%len(b)],a[(i+1)%len(a)]],colour,group)
                    i+=1; j+=1
                elif an<bn:
                    self.face([ai,bj,a[(i+1)%len(a)]],colour,group); i+=1
                else:
                    self.face([ai,bj,b[(j+1)%len(b)]],colour,group); j+=1
        if caps:
            self.face(rings[0],colour,group)
            self.face(rings[-1][::-1],colour,group)

    def elbow(self, bottom, height=PORTY, radius=.18, end=.575, low=False):
        """One capped six-sided pipe swept from +Y into +Z, with tangent end runs."""
        sections=[((0,bottom,0),0,BORE)]
        steps=3 if low else 6
        for i in range(steps+1):
            a=math.pi/2*i/steps
            sections.append(((0,height-radius+radius*math.sin(a),radius*(1-math.cos(a))),a,BORE))
        sections += [((0,height,.46),math.pi/2,BORE),
                     ((0,height,.52),math.pi/2,BORE*1.45),
                     ((0,height,end),math.pi/2,BORE*1.45)]
        rings=[]
        for p,a,r in sections:
            rings.append([(p[0]+r*math.cos(t),p[1]-r*math.sin(t)*math.sin(a),p[2]+r*math.sin(t)*math.cos(a))
                          for t in [math.tau*i/6 for i in range(6)]])
        self.loft(rings,3,'swept_elbow')
    def add(self, other, offset=(0,0,0), transform=None):
        for ids,c,n,g in other.faces:
            # Stored faces are clockwise; reconstruct outward CCW for face().
            ps=[other.v[i] for i in ids[::-1]]
            ps=[transform(p) if transform else tuple(x+y for x,y in zip(p,offset)) for p in ps]
            ns=None
            if len(set(n))>1:
                ns=[unit(sub(transform(a),transform((0,0,0)))) if transform else a for a in n[::-1]]
            self.face(ps,c,g,ns)
    def write(self,path):
        normals=[]
        for _,_,ns,_ in self.faces:
            for n in ns:
                n=tuple(round(x,6) for x in n)
                if n not in normals: normals.append(n)
        lines=['# Authored by tools/author_fluid_art.py after notes/FLUID_ART_MEASUREMENTS.md',
               '# Y-up metres, explicit normals, triangle-only, CLOCKWISE front (ParseObj preserves file winding); palette V-up']
        lines += ['v '+' '.join(f'{x:.6f}' for x in p) for p in self.v]
        lines += ['vt 0.25 0.75','vt 0.75 0.75','vt 0.25 0.25','vt 0.75 0.25']
        lines += ['vn '+' '.join(f'{x:.6f}' for x in p) for p in normals]
        last=None
        for ids,c,ns,g in self.faces:
            if g!=last: lines.append('g '+g); last=g
            nis=[normals.index(tuple(round(x,6) for x in n))+1 for n in ns]
            lines.append('f '+' '.join(f'{i+1}/{c+1}/{ni}' for i,ni in zip(ids,nis)))
        path.write_text('\n'.join(lines)+'\n')
        lo=[min(p[i] for p in self.v) for i in range(3)]; hi=[max(p[i] for p in self.v) for i in range(3)]
        return dict(file=path.name,vertices=len(self.v),triangles=len(self.faces),min=lo,max=hi,
                    groups={g:sum(f[3]==g for f in self.faces) for g in sorted({f[3] for f in self.faces})})

def cylinder(m,r,a,b,n=6,c=3,axis='y',center=(0,0,0),group='pipe'):
    m.lathe([(a,r),(b,r)],n,c,group,axis=axis,center=center)

def pipe(m, sections, radius=BORE*1.45, n=8, colour=3, group='pipe', normal=(0,1,0)):
    """Closed pipe sweep. Sections are (centre, tangent); transport the ring frame.

    Every bend is sampled on a circular centreline with tangent straight runs.
    Clockwise-front emission remains entirely in Mesh.face, as for the frozen art.
    """
    rings=[]; u=normal
    for p,t in sections:
        t=unit(t); along=sum(a*b for a,b in zip(u,t))
        u=unit(tuple(a-along*b for a,b in zip(u,t)))
        v=cross(u,t)
        rings.append([tuple(p[k]+radius*(u[k]*math.cos(a)+v[k]*math.sin(a)) for k in range(3))
                      for a in [math.tau*i/n for i in range(n)]])
    m.loft(rings,colour,group)

def rod(m,a,b,r=STEM/2,colour=3,group='support_rod'):
    t=unit(sub(b,a)); normal=(0,1,0) if abs(t[1])<.95 else (1,0,0)
    pipe(m,[(a,t),(b,t)],r,6,colour,group,normal)

def hex_wall_port(m,anchor,sign,wall,body_radius=None,low=False):
    """Seat the hex itself into the wall, retaining the published anchor and bore.

    Round bodies need circular + polygon sag, as measured in the second pass;
    the radius used for that calculation is now HEX, not an oversized boss.
    """
    n=6 if low else 8
    depth=.03 if body_radius is None else (body_radius-math.sqrt(body_radius**2-HEX**2)
                                         +body_radius*(1-math.cos(math.pi/n))+.025)
    m.socket(anchor,'x',sign,low,seat=sign*anchor[0]-wall+depth)

def seated_port(m,anchor,axis,sign,wall,n,body_radius=None,low=False):
    """FROZEN legacy seats: called only by the signed-off tank and valve.

    wall is positive distance from the origin along sign*axis. The extra 25 mm
    seat depth includes polygon sag, so even LOD1 seats penetrate over their full
    perimeter. The end overlaps the collar by 25 mm; no pipe ends on its back plane.
    """
    n=6 if low else n; outer=HEX+LIP
    depth=.025 if body_radius is None else (body_radius-math.sqrt(body_radius**2-outer**2)
                                           +body_radius*(1-math.cos(math.pi/n))+.025)
    ai=0 if axis=='x' else 2
    distance=sign*anchor[ai]-wall
    profile=[(wall-depth,outer,n),(wall+.012,outer,n)]
    if distance>.10: profile.append((wall+.075,BORE*1.45,6))
    profile.append((sign*anchor[ai]+.025,BORE*1.45 if distance>.10 else HEX,6))
    rings=[]
    for h,r,count in profile:
        ring=[]
        for i in range(count):
            a=math.tau*i/count; x,z=r*math.cos(a),r*math.sin(a)
            ring.append((sign*h,anchor[1]-sign*x,anchor[2]+z) if axis=='x'
                        else (anchor[0]+x,anchor[1]-sign*z,sign*h))
        rings.append(ring)
    m.loft(rings,3,'seated_boss')
    m.socket(anchor,axis,sign,low)

def tank(low):
    """FROZEN 9110 builder. Do not share vessel construction with the rain catcher."""
    m=Mesh(); n=6 if low else 12
    if low:
        cylinder(m,BARREL_BAND_R,0,BARREL_TOP,n,1,group='vessel')
    else:
        m.lathe([(BARREL_SHELL[0],BARREL_R),(BARREL_SHELL[1],BARREL_R)],12,1,'vessel',caps=True)
        for a,b in BARREL_BANDS:
            m.lathe([(a,BARREL_BAND_R),(b,BARREL_BAND_R)],12,0,'bands',caps=True)
    for sign in [-1,1]: seated_port(m,(sign*.5,BARREL_PORTY,0),'x',sign,.5,n,.5,low)
    return m

def rain_catcher(low):
    """Open green drum with four metal arms and a folded, draining tarp."""
    m=Mesh(); n=6 if low else 12
    # First cap is UNDER the drum; last cap is its internal floor. There is no lid.
    # The inner wall returns from the lip to the floor in the same closed solid.
    # Body lip sits 21.8 mm below the upper hoop; overlapping top faces at the same
    # height made the rim visibly speckle in the first oblique capture.
    m.lathe([(.04604,.5),(1.276,.5),(1.30,.52),(1.30,.456),(.09,.456)],
            n,1,'vessel',caps=True)
    for a,b in BARREL_BANDS:
        # Annular bands, with their cross-section closed and NO disc caps.
        m.lathe([(a,.484),(a,BARREL_BAND_R),(b,BARREL_BAND_R),(b,.484),(a,.484)],
                n,0,'bands',caps=False)
    m.socket((0,BARREL_PORTY,.55),'z',low=low,seat=.14 if low else .105)
    # Alternate edge midpoints and corners around a square. Corners hang from rods;
    # edge midpoints droop 220 mm, then the cloth falls to a 1.58 m central drain.
    outer=[(.95,2.16,0),(.95,2.38,.95),(0,2.16,.95),(-.95,2.38,.95),
           (-.95,2.16,0),(-.95,2.38,-.95),(0,2.16,-.95),(.95,2.38,-.95)]
    middle=[(x*.49,1.90+(.06 if i%2 else 0),z*.49) for i,(x,y,z) in enumerate(outer)]
    drain=[(.06*math.cos(math.tau*i/8),1.58,.06*math.sin(math.tau*i/8)) for i in range(8)]
    top=[outer,drain] if low else [outer,middle,drain]
    underside=[[(x,y-.012,z) for x,y,z in ring] for ring in top[::-1]]
    m.loft(top+underside+[outer],0,'sagging_tarp',caps=False)
    m.lathe([(1.40,.06),(1.60,.06),(1.60,.042),(1.40,.042),(1.40,.06)],
            8,3,'tarp_drain',caps=False)
    for x in [-1,1]:
        for z in [-1,1]:
            rod(m,(x*.34,1.265,z*.34),(x*.95,2.385,z*.95))
    return m

def electrical_panel(m,id,low):
    """Same 880 x 260 x 140 mm enclosure, face and ON / power / OFF row everywhere.

    Each active wire cube is 130 mm wide (ConnectionPort.CubeSize): its rear
    25 mm intersects a socket, with the remaining 105 mm exposed for wiring.
    Devices without a given input get a flat blank in that slot, never fake IO.
    Only the mounting bracket belongs to the individual device.
    """
    panel=Mesh()
    panel.box((-.44,-.13,-.18),(.44,.13,-.04),2,'electrical_panel')
    panel.box((-.425,-.115,-.05),(.425,.115,-.035),3,'panel_face')
    for slot,x in [('on',-PANEL_ANCHORS['TriggerX']),('power',0),('off',PANEL_ANCHORS['TriggerX'])]:
        active=id==9114 or (id==9115 and slot!='power') or (id==9121 and slot=='power')
        panel.box((x-.085,-.085,-.06),(x+.085,.085,-.025 if active else -.03),2,
                  'panel_socket_'+slot if active else 'panel_blank_'+slot)
        if active:
            # Socket liners and permanent I / lightning / O marks stay legible
            # with the wire-tool overlay hidden. All use the shared metal texel.
            cylinder(panel,.046,-.045,-.022,6,3,'z',(x,0,0),'panel_contact_'+slot)
            if slot=='on': panel.box((x-.009,-.063,-.028),(x+.009,-.034,-.020),3,'panel_on_mark')
            elif slot=='off':
                ring=Mesh()
                ring.lathe([(-.029,.013),(-.029,.023),(-.020,.023),(-.020,.013),(-.029,.013)],
                           8,3,'panel_off_mark',axis='z',center=(x,-.052,0),caps=False)
                panel.add(ring)
            else:
                for a,b in [((x+.015,-.030,-.020),(x-.007,-.050,-.020)),
                            ((x-.007,-.050,-.020),(x+.008,-.050,-.020)),
                            ((x+.008,-.050,-.020),(x-.015,-.072,-.020))]:
                    rod(panel,a,b,.004,3,'panel_power_mark')
    if not low:
        for x in [-.405,.405]:
            for y in [-.095,.095]:
                cylinder(panel,.012,-.045,-.026,6,2,'z',(x,y,0),'panel_screw')
    m.add(panel,offset=panel_origin(id))


def pump(low):
    """End suction centrifugal set: front axial eye, motor behind, low side discharge.

    The shaft runs along Z, leaving the +X hose side clear. A two-bend suction
    riser meets the eye on-axis; the scroll's bottom tangent feeds a level S-jog
    to the outlet. Neither pipe crosses the motor or the visible coupling.
    """
    m=Mesh(); px=PORTX[9114]; n=6 if low else 8; steps=3 if low else 6
    cx=-.48; cy=.92; forward=.11
    # The plate follows the motor/panel and casing; the fixed hose ends overhang it.
    for x in [-.83,-.12]: m.box((x-.06,0,-1.15),(x+.06,.12,.13),2,'skids')
    m.box((-.95,.12,-1.15),(0,.22,.13),2,'bedplate')
    unit=Mesh()
    unit.box((cx-.20,.21,-.43),(cx+.20,.68,-.20),1,'volute_pedestal')
    # Bottom cutwater: radius .19 -> .32 m, zero radial slope at each end so
    # the final tangent is exactly +X at Y=.60, the unchanged hose height.
    count=12 if low else 24
    outline=[]
    for i in range(count+1):
        t=i/count; r=.19+.13*t*t*(3-2*t)
        outline.append((cx+r*math.sin(math.tau*t),cy-r*math.cos(math.tau*t)))
    rings=[[(x,y,z) for x,y in outline] for z in [-.18,-.44]]
    unit.loft(rings,1,'spiral_volute',caps=False)
    for j,ring in enumerate(rings):
        order=ring if j==0 else ring[::-1]
        centre=(cx-.025,cy,ring[0][2])
        for a,b in zip(order,order[1:]+order[:1]): unit.face([centre,a,b],1,'volute_face')
    cylinder(unit,.19,-.20,-.154,n,3,'z',(cx,cy,0),'suction_cover')
    if not low:
        for i in range(6):
            a=math.tau*(i+.5)/6
            cylinder(unit,.021,-.169,-.142,6,2,'z',(cx+.169*math.cos(a),cy+.169*math.sin(a),0),'cover_bolt')
    # Two tangent quarter bends: +X -> +Y -> -Z into the axial eye. The
    # .22 + .10 m lift is fixed by .92 shaft height minus .60 hose height.
    # Bringing the unit 100 mm left and 110 mm forward removes 210 mm of run
    # without changing either published hose anchor or the axial entry tangent.
    sections=[((-.825,PORTY,0),(1,0,0)),((cx-.22,PORTY,0),(1,0,0))]
    for i in range(1,steps+1):
        a=math.pi/2*i/steps
        sections.append(((cx-.22+.22*math.sin(a),.82-.22*math.cos(a),0),(math.cos(a),math.sin(a),0)))
    for i in range(1,steps+1):
        a=math.pi/2*i/steps
        sections.append(((cx,.82+.10*math.sin(a),-.10+.10*math.cos(a)),(0,math.cos(a),-math.sin(a))))
    sections.append(((cx,cy,-.22+forward),(0,0,-1)))
    pipe(m,sections,BORE,n,1,'axial_suction',normal=(0,0,1))
    m.socket((-px,PORTY,0),'x',-1,low,seat=.03)
    # A horizontal run off the BOTTOM tangent of the volute. Two shallow 45-deg
    # bends shift Z by .20 m onto the anchor plane, all at Y=.60. The gentler jog
    # also shortens this outlet despite moving the casing toward the inlet.
    turn=math.pi/4; radius=.10/(1-math.cos(turn))
    start=.61-2*radius*math.sin(turn)
    sections=[((cx-.04,PORTY,-.20),(1,0,0)),((start,PORTY,-.20),(1,0,0))]
    for i in range(1,steps+1):
        a=turn*i/steps
        sections.append(((start+radius*math.sin(a),PORTY,-.20+radius*(1-math.cos(a))),
                         (math.cos(a),0,math.sin(a))))
    for i in range(1,steps+1):
        a=turn*(1-i/steps)
        sections.append(((.61-radius*math.sin(a),PORTY,-radius*(1-math.cos(a))),
                         (math.cos(a),0,math.sin(a))))
    sections.append(((.825,PORTY,0),(1,0,0)))
    pipe(m,sections,BORE,n,1,'tangential_discharge')
    m.socket((px,PORTY,0),'x',1,low,seat=.03)
    # Behind the casing: bearing, exposed coupling, motor, fan. One shaft axis.
    cylinder(unit,.105,-.565,-.42,n,3,'z',(cx,cy,0),'bearing_housing')
    unit.box((cx-.09,.21,-.55),(cx+.09,.87,-.46),3,'bearing_foot')
    cylinder(unit,.043,-.81,-.53,6,3,'z',(cx,cy,0),'drive_shaft')
    # Clock the polygon around the existing shaft, so both LODs have horizontal
    # top/bottom faces. The motor and fan are static; only the local-Y part spins.
    motor=Mesh()
    cylinder(motor,.24,-1.18,-.76,n,0,'z',group='motor')
    cylinder(motor,.25,-1.23,-1.14,n,2,'z',group='motor_fan_cover')
    phase=math.pi/2-math.pi/n; c=math.cos(phase); s=math.sin(phase)
    unit.add(motor,transform=lambda p:(cx+c*p[0]-s*p[1],cy+s*p[0]+c*p[1],p[2]))
    for z in [-1.11,-.83]:
        unit.box((cx-.22,.21,z-.055),(cx+.22,.73,z+.055),2,'motor_feet')
    if not low:
        # Two broad, shallow cast ribs replace eight thin rods around the shell.
        for sign in [-1,1]:
            x=cx+sign*.225
            unit.box((x-.012,cy-.026,-1.12),(x+.012,cy+.026,-.81),0,'motor_fin')
    # Two small guard rails leave the keyed rotating coupling visible from
    # either side. Turn inward only AFTER the rotor's front end (Z=-.57 before
    # the unit translation below);
    # a diagonal rail to the bearing cut through the keyed rotor's swept volume.
    for sign in [-1,1]:
        rod(unit,(cx+sign*.16,.86,-.80),(cx+sign*.16,.86,-.53),.018,2,'coupling_guard')
        rod(unit,(cx+sign*.16,.86,-.545),(cx+sign*.07,.86,-.50),.018,2,'coupling_guard')
    m.add(unit,offset=(0,0,forward))
    # Enclosure bottom overlaps the flat motor crown, with no free-standing legs.
    electrical_panel(m,9114,low)
    coupling=Mesh()
    cylinder(coupling,.112,-.09,.09,n,3,group='coupling')
    coupling.box((-.025,-.078,.095),(.025,.078,.122),2,'coupling_key')
    return m,coupling,(cx,cy,-.66+forward)

def valve(low):
    """A gate valve: flanged body, bonnet, rising stem, and a RED handwheel on top.

    strawberry: "change the valve handle to be red and more accurate to a real valve. also make sure
    it has an animated turn on/off animation."

    The old handle was a plain green disc with a single bar across it, and it showed state by changing
    COLOUR. A real handwheel is a rim on a hub with spokes between, and it is red because that is what
    valve wheels are painted -- so it is red in both states now, and the state is shown by TURNING it,
    which is the animation asked for. FluidContainer drives the spin (RefreshValveVisual)."""
    m=Mesh()
    m.box((-.30,0,-.30),(.30,.13,.30),2,'base')
    m.box((-.11,.13,-.11),(.11,.44,.11),3,'pedestal')
    # BODY on the hose axis, with a raised flange either side -- a valve body is not a bare pipe.
    cylinder(m,BORE*1.45,-.42,.42,6,3,'x',(0,PORTY,0),'valve_pipe')
    for x in [-.26,.26]:
        cylinder(m,HEX,x-.035,x+.035,6 if low else 8,2,'x',(0,PORTY,0),'flange')
    cylinder(m,.21,-.17,.17,6 if low else 8,3,'x',(0,PORTY,0),'valve_body')
    cylinder(m,.185,PORTY+.14,PORTY+.30,6 if low else 8,3,group='bonnet')
    cylinder(m,.075,PORTY+.30,1.30,6,3,group='yoke')
    m.box((-STEM/2,1.30,-STEM/2),(STEM/2,1.44,STEM/2),3,'stem')
    for sign in [-1,1]: seated_port(m,(sign*.5,PORTY,0),'x',sign,.17,6 if low else 8,low=low)
    # Rear saddle joins the common panel to the existing yoke; wire anchors
    # remain at (+-.32, 1.20, 0), below the handwheel's swept volume.
    m.box((-.12,1.10,-.16),(.12,1.26,.025),3,'panel_mount')
    electrical_panel(m,9115,low)
    # THE HANDWHEEL. Colour 1 is the palette's red texel, in both states.
    wheel=Mesh()
    if low:
        cylinder(wheel,.26,-.04,.04,6,1,group='handle')
    else:
        # A RIM WITH A HOLE IN IT. This profile runs .175 -> .26 -> .26 -> .175, and with caps=True the
        # lathe puts a disc across BOTH open faces -- which filled the middle and buried the hub and all
        # five spokes inside a solid plate. It rendered as exactly the thing strawberry asked it to stop
        # being ("a flat disc"), and I had already looked at it and not seen it. Closing the cross-section
        # back on itself instead (and dropping the caps) gives a real annulus you can see through.
        wheel.lathe([(-.045,.175),(-.045,.26),(.045,.26),(.045,.175),(-.045,.175)],12,1,'handle_rim',caps=False)
        cylinder(wheel,.075,-.05,.05,8,1,group='handle_hub')   # a lathe to radius 0 makes n zero-area faces
        for i in range(5):
            wheel.box_oriented((.06,-.024,-.024),(.245,.024,.024),math.tau*i/5,1,'handle_spoke')
    return m,wheel,(0,1.44,0)

def transformer(id,low,port_y):
    """Refinery, sluice and purifier -- the three strawberry asked to enlarge, ~1.8x the old envelope.

    "the purifier, refinery, pump and sluice should be much bigger (keeping the Io pipes the same
    size.)" So every HEX/BORE fitting here is untouched and the hose anchors travel outward to the new
    body sides instead (PORTX), because a spigot left at +-0.5 would now be buried inside the hull."""
    m=Mesh(); px=PORTX[id]
    n=6 if low else 8
    if id==9116:
        # 2.56 x 3.64 x 1.40 overall, up from 1.96 x 2.78 x 1.04.
        # Wider retort, three .12 m bands, tapered shoulder and a flared stack foot.
        m.box((-.98,0,-.70),(.98,.14,.70),2,'base')
        m.lathe([(.12,.58),(2.58,.58),(2.82,.42)],6 if low else 12,0,
                'retort',center=(.20,0,0))
        if not low:
            for h in [.94,1.72,2.48]: cylinder(m,.58+LIP,h,h+.12,12,3,center=(.20,0,0),group='retort_band')
        cylinder(m,.435,2.77,2.90,6 if low else 12,3,center=(.20,0,0),group='retort_cap')
        m.lathe([(2.87,.25),(3.02,.18),(3.58,.18),(3.58,.215),(3.64,.215)],6,3,
                'stack',center=(.20,0,0))
        m.box((-.90,.12,-.45),(-.25,1.14,.45),1,'firebox')
        m.box((-.86,1.11,-.39),(-.29,1.30,.39),2,'firebox_hood')
        m.box((-.80,.30,.44),(-.36,.86,.49),2,'firebox_door')
    elif id==9117:
        # SLUICE: a long open trough on trestles, falling left to right so water runs through it.
        m.box((-.86,0,-.50),(.86,BAND,.50),2,'base')
        for x in [-.56,.56]:
            m.box((x-.06,BAND,-.40),(x+.06,.52,.40),3,'trestle')
        tray=Mesh()
        tray.box((-.86,0,-.42),(.86,.07,.42),0,'trough_floor')
        for z in [-.42,.42]:
            tray.box((-.86,.07,z-.05),(.86,.07+.24,z+.05),1,'trough_wall')
        if not low:
            for x in [-.52,-.17,.17,.52]:
                tray.box((x-.035,.07,-.42),(x+.035,.07+LIP*2,.42),3,'riffles')
        # slab Y falls with X: .74 at the inlet end to .50 at the outlet
        m.add(tray,transform=lambda q:(q[0],q[1]+.62-q[0]*(.24/1.72),q[2]))
        m.box((-.86,.52,-.30),(-.68,.86,.30),3,'inlet_header')    # feeds the high end
        m.box((.68,.40,-.30),(.86,.70,.30),3,'outlet_header')     # takes the low end's discharge
    else:
        # PURIFIER: two filter columns on a skid with the control cabinet between them.
        m.box((-.78,0,-.52),(.78,BAND,.52),2,'base')
        for x in [-.42,.42]:
            m.lathe([(BAND,.28),(1.78,.28),(1.95,BORE*1.6)],n,0,'filter',center=(x,0,0))
            if not low:
                for h in [.60,1.34]:
                    cylinder(m,.28+LIP,h,h+BAND,n,1,center=(x,0,0),group='filter_clamp')
        m.box((-.20,BAND,-.30),(.20,1.36,.30),2,'control_cabinet')
        m.box((-.24,1.36,-.34),(.24,1.50,.34),3,'cabinet_hood')
        electrical_panel(m,9121,low)
    # ONE STUB PER PORT, off the body wall. A single pipe across the whole width ran straight THROUGH
    # the refinery's vessel and out the far side, which read as a skewer rather than plumbing.
    for sign in [-1,1]:
        anchor=(sign*px,port_y,0)
        if id==9116:
            # Left inlet on the firebox, right discharge on the retort. No cross-vessel skewer.
            hex_wall_port(m,anchor,sign,.78 if sign>0 else .90,.58 if sign>0 else None,low)
        elif id==9121: hex_wall_port(m,anchor,sign,.70,.28,low)
        else: hex_wall_port(m,anchor,sign,.86,low=low)
    return m

def manifold(combine,low):
    """Three branches on a tubular header with two return elbows, mirrored for 3:1."""
    m=Mesh(); n=6 if low else 8; steps=3 if low else 6
    # Two narrow feet and legs leave air under and between the actual pipework.
    for z in [-.24,.24]:
        m.box((-.24,0,z-.07),(.29,.08,z+.07),2,'feet')
        rod(m,(.03,.06,z),(.03,.52,z),.035,3,'pipe_stand')
    # The header is a Z pipe, turning into +X at each end. Middle branch is a tee.
    sections=[((.525,PORTY,-.32),(-1,0,0)),((.21,PORTY,-.32),(-1,0,0))]
    for i in range(1,steps+1):
        a=math.pi/2*i/steps
        sections.append(((.21-.18*math.sin(a),PORTY,-.14-.18*math.cos(a)),(-math.cos(a),0,math.sin(a))))
    sections.append(((.03,PORTY,.14),(0,0,1)))
    for i in range(1,steps+1):
        a=math.pi/2*i/steps
        sections.append(((.21-.18*math.cos(a),PORTY,.14+.18*math.sin(a)),(math.sin(a),0,math.cos(a))))
    sections.append(((.525,PORTY,.32),(1,0,0)))
    pipe(m,sections,BORE*1.45,n,0,'header_elbows')
    cylinder(m,BORE*1.45,-.525,.525,n,0,'x',(0,PORTY,0),'through_tee')
    for x,z in [(-.5,0)]+[(.5,z) for z in FAN_Z]:
        sign=1 if x>0 else -1
        # Plain hex unions; all runs penetrate 25 mm beyond the anchor plane.
        m.socket((x,PORTY,z),'x',sign,low,seat=.035)
    if not combine:return m
    mirrored=Mesh()
    mirrored.add(m,transform=lambda p:(-p[0],p[1],-p[2]))
    return mirrored

def endpoint(drain,low):
    m=Mesh(); n=6 if low else 8
    if drain:
        # ONE closed solid. It used to be an open lathe plus a separate disc welded flush to it,
        # which left the bowl rim open (8 boundary edges) AND put four faces on the weld ring.
        m.lathe([(0,TANK),(.1,TANK),(.1,TANK-LIP),(.05,TANK-LIP)],n,3,'drain_bowl',caps=True)
        if not low:
            for x in [-.25,0,.25]:
                reach=.48 if x==0 else .385
                m.box((x-STEM/2,.075,-reach),(x+STEM/2,.075+STEM,reach),3,'grate')
        cylinder(m,HEX,.025,.145,6,3,group='riser_foot')
    else:
        cylinder(m,.25,0,.085,n,3,group='strainer_base')
        cylinder(m,.25,.295,.38,n,0,group='strainer_top')
        # OVERLAP the cage into the end caps rather than butting it flush against them: a shared ring
        # is an edge with four faces on it, which is the non-manifold seam the audit was counting.
        if low:cylinder(m,.22,.065,.315,6,2,group='strainer')
        else:
            for i in range(8):
                m.box_oriented((.188,.065,-STEM/2),(.228,.315,STEM/2),
                               math.tau*(i+.5)/8,3,'strainer_ribs')
    # ONE hose height for these too. They used to sit at .7 while everything else was at .6; the
    # catalog now publishes a single height per device and the mesh has to agree with it, or the
    # spigot and the port drift apart (verify_fluid_art.py asserts they don't).
    m.elbow(.08 if drain else .33,low=low)
    m.socket((0,PORTY,.55),'z',low=low)
    return m

def hose_tool():
    """Two closed hose coils and metal couplers; an item, not a twelfth deployable."""
    m=Mesh()
    for z in [-.043,.043]:
        rings=[]
        for i in range(12):
            a=math.tau*i/12
            rings.append([((.24+.038*math.cos(t))*math.cos(a),(.24+.038*math.cos(t))*math.sin(a),
                           z+.038*math.sin(t)) for t in [math.tau*j/6 for j in range(6)]])
        m.loft(rings+[rings[0]],2,'hose_coil',caps=False)
    # Metal couplings sink into each coil, with the same hex silhouette as the device fittings.
    for sign in [-1,1]:
        coupling=Mesh(); coupling.socket((0,0,0),'x',sign)
        m.add(coupling,transform=lambda p:(p[0]*.42+sign*.215,p[1]*.42+.08,p[2]*.42+sign*.043))
    return m

def assembled(body,part,offset,id):
    m=Mesh(); m.add(body)
    if part:
        # Match catalog partRot, including the pump's axial coupling in ghosts and dropped art.
        m.add(part,transform=lambda p:tuple(a+b for a,b in zip(
            (p[0],-p[2],p[1]) if id==9114 else p,offset)))
    return m

def write_item(id,name,mesh,palette,manifest):
    items=ROOT/'game/content/items'
    lo=[min(p[i] for p in mesh.v) for i in range(3)]; hi=[max(p[i] for p in mesh.v) for i in range(3)]
    center=[(a+b)/2 for a,b in zip(lo,hi)]
    # Portable Generator 458 longest dropped dimension is .8722 m; Rain Barrel 1208 is .75 m.
    # Its scale is baked into the OBJ, as in the existing item manifest (no scale field).
    scale=min(.5,.8722/max(b-a for a,b in zip(lo,hi))) if id!=9118 else 1.
    dropped=Mesh(); dropped.add(mesh,transform=lambda p:tuple((a-b)*scale for a,b in zip(p,center)))
    spec=dropped.write(items/f'{id}.obj')
    im=Image.new('RGB',(2,2)); im.putdata(palette); im.save(items/f'{id}.png')
    manifest[str(id)]=dict(name=name,type='Generic',obj=f'{id}.obj',tex=f'{id}.png',color=None,
                          box=[round(b-a,6) for a,b in zip(spec['min'],spec['max'])],center=[0.,0.,0.],parts=1)

def main():
    OUT.mkdir(exist_ok=True)
    catalog={}; files=[]
    published=json.loads((OUT/'catalog.json').read_text())
    item_path=ROOT/'game/content/items/items_manifest.json'
    items=json.loads(item_path.read_text())
    for id,name in [(9110,'Tank'),(9111,'Water source'),(9114,'Pump'),(9115,'Valve'),(9116,'Refinery'),(9117,'Sluice'),(9121,'Purifier'),(9112,'Splitter'),(9113,'Combiner'),(9119,'Inlet'),(9120,'Drain')]:
        palette=([BLUE,BLUE_LIGHT,DARK,METAL] if id in [9110,9112] else
                 [GREEN,GREEN_LIGHT,DARK,METAL] if id in [9111,9113,9119] else
                 [YELLOW,BLUE_LIGHT,DARK,METAL] if id==9114 else
                 [GREEN_LIGHT,RED,DARK,METAL] if id==9115 else
                 [WHITE,BLUE_LIGHT,DARK,METAL] if id==9121 else [RUST,RUST_LIGHT,DARK,METAL])
        im=Image.new('RGB',(2,2));im.putdata(palette);im.save(OUT/f'{id}_palette.png')
        entry=dict(name=name,palette=palette,part=None,partRot=None,portX=PORTX.get(id,.5),
                   portY=published[str(id)]['portY'])
        if id in [9112,9113]: entry['branchZ']=FAN_Z
        for low in [False,True]:
            part=None; offset=None; suffix='_lod1' if low else ''
            if id==9110:m=tank(low)
            elif id==9111:m=rain_catcher(low)
            elif id==9114:m,part,offset=pump(low)
            elif id==9115:m,part,offset=valve(low)
            elif id in [9116,9117,9121]:m=transformer(id,low,entry['portY'])
            elif id in [9112,9113]:m=manifold(id==9113,low)
            else:m=endpoint(id==9120,low)
            body=m.write(OUT/f'{id}_body{suffix}.txt');files.append(body)
            if part:
                ps=part.write(OUT/f'{id}_part{suffix}.txt');files.append(ps)
                preview=assembled(m,part,offset,id)
                pr=preview.write(OUT/f'{id}_preview{suffix}.txt');files.append(pr)
                if not low:
                    entry['part']=list(offset)
                    # the coupling's local Y axis is stood onto the pump's Z shaft here
                    entry['partRot']=[90,0,0] if id==9114 else [0,0,0]
            else: pr=body
            if not low: write_item(id,'Fluid '+name,assembled(m,part,offset,id),palette,items)
            entry['lod1' if low else 'mesh']=pr
        lo=entry['mesh']['min'];hi=entry['mesh']['max']
        entry['boundsMin']=lo
        entry['boundsSize']=[round(b-a+(0.01 if id==9114 and i==1 else 0),6) for i,(a,b) in enumerate(zip(lo,hi))]
        entry['offset']=entry['boundsSize'][1]/2
        entry['radius']=min(.5,entry['boundsSize'][0]/2,entry['boundsSize'][2]/2,entry['offset']-STEM)
        catalog[str(id)]=entry
    (OUT/'catalog.json').write_text(json.dumps(catalog,indent=2)+'\n')
    (ROOT/'notes/FLUID_MESH_BOUNDS.json').write_text(json.dumps(files,indent=2)+'\n')
    write_item(9118,'Hose Tool',hose_tool(),[BLUE,BLUE_LIGHT,DARK,METAL],items)
    # Preserve the existing compact manifest convention, changing only our twelve entries.
    item_path.write_text(json.dumps(items,separators=(',',':')))
    print('\n'.join(f"{k} {v['name']}: {v['mesh']['triangles']} / {v['lod1']['triangles']} tris, bounds {v['boundsSize']}" for k,v in catalog.items()))

if __name__=='__main__':main()
