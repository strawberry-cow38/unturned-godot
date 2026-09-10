#!/usr/bin/env python3
"""Deterministic authored fluid meshes. Measurement gate precedes all vertex creation.

Art constants below cite notes/FLUID_ART_MEASUREMENTS.md. Layout coordinates
come from the existing hose/power anchors and primitive envelopes, not a claim
that a retail prop contains this machine. Run measure_fluid_art.py first.
"""
from pathlib import Path
import math
import json
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
PORTX={9114:.80, 9116:.80, 9117:.92, 9121:.85}   # the four machines strawberry asked to enlarge; everything else stays .5
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
    def socket(self, anchor, axis, sign=1, low=False):
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
        if low:
            tmp.lathe([(0,HEX),(COLLAR+SPOUT,HEX)],n,3,'hose_fitting',caps=True,end_colour=2)
        else:
            tmp.lathe([(0,HEX),(COLLAR,HEX),(COLLAR,spout),(COLLAR+SPOUT,spout)],
                      n,3,'hose_fitting',caps=True,end_colour=2)
        def tr(p):
            x,y,z=p
            if axis=='x': x,y,z=sign*y,-sign*x,z
            else: x,y,z=x,-sign*z,sign*y
            return (x+anchor[0],y+anchor[1],z+anchor[2])
        self.add(tmp,transform=tr)
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

def stub(m,frm,to,y=None,r=None,group='stub'):
    """Pipe along X from the body wall out to a hose anchor. The four enlarged machines moved their
    anchors outward and their fittings did not grow, so without these the spigots float unattached --
    which is exactly what the first render of the bigger pump showed."""
    y=PORTY if y is None else y; r=BORE*1.45 if r is None else r
    cylinder(m,r,min(frm,to),max(frm,to),6,3,'x',(0,y,0),group)

def cylinder(m,r,a,b,n=6,c=3,axis='y',center=(0,0,0),group='pipe'):
    m.lathe([(a,r),(b,r)],n,c,group,axis=axis,center=center)

def barrel(source,low):
    """Barrel_0's own profile, not an approximation of it: .5 shell, .543261 bands, ends straddled."""
    m=Mesh(); n=6 if low else 12
    if low:
        cylinder(m,BARREL_BAND_R,0,BARREL_TOP,n,1,group='vessel')
    else:
        # CLOSED shell (caps=True). The old one was an open tube relying on the bands to hide its
        # ends, which left it counted as a hole and showed through wherever a band did not cover.
        m.lathe([(BARREL_SHELL[0],BARREL_R),(BARREL_SHELL[1],BARREL_R)],12,1,'vessel',caps=True)
        for a,b in BARREL_BANDS:
            m.lathe([(a,BARREL_BAND_R),(b,BARREL_BAND_R)],12,0,'bands',caps=True)
    if source:
        cylinder(m,BORE,.25,.45,6,3,'z',(0,BARREL_PORTY,0),'outlet_neck')
        m.socket((0,BARREL_PORTY,.55),'z',low=low)
    else:
        m.socket((-.5,BARREL_PORTY,0),'x',-1,low);m.socket((.5,BARREL_PORTY,0),'x',1,low)
    return m

def pump(low):
    """A skid-mounted centrifugal set: volute on the hose line, motor above, belt between them.

    strawberry: "the pump should have some part of it that spins, but making sense." What spins is the
    PULLEY on the motor shaft, and it is connected by a belt to a sheave on the volute shaft, so it is
    visibly driving the casing the fluid crosses. The first attempt made the motor and the volute the
    same size and the same yellow, which read as two drums rather than a machine, and left the moved
    hose anchors floating unattached -- both fixed here."""
    m=Mesh(); px=PORTX[9114]; n=6 if low else 8
    for z in [-.46,.46]: m.box((-.66,0,z-.09),(.66,.13,z+.09),2,'skids')
    m.box((-.56,.13,-.38),(.56,.26,.38),2,'bedplate')
    # VOLUTE -- the casing the fluid actually crosses, on the hose axis, plus its suction/discharge.
    m.box((-.24,.26,-.24),(.24,PORTY-.24,.24),2,'volute_pedestal')
    cylinder(m,.30,-.20,.20,n,1,'x',(0,PORTY,0),'volute')
    cylinder(m,.34,-.06,.06,n,3,'x',(0,PORTY,0),'volute_band')
    stub(m,.16,px); stub(m,-.16,-px)
    # MOTOR -- smaller than the volute and the only yellow thing here, so the two read apart.
    m.box((-.30,.26,-.26),(.30,.84,.26),2,'motor_stool')
    cylinder(m,.24,-.40,.40,n,0,'x',(0,1.10,0),'motor')
    if not low:
        for x in [-.26,.26]: cylinder(m,.265,x-.04,x+.04,n,3,'x',(0,1.10,0),'motor_band')
    m.box((-.18,1.28,-.13),(.18,1.40,.13),2,'terminal_box')
    m.box((-.30,1.28-BORE,-.40),(.30,1.28+BORE,-.28),2,'rear_terminals')
    # BELT: a sheave on the volute shaft and two straps up to the pulley, so the pulley drives something.
    cylinder(m,.15,.42,.52,6,2,'x',(0,PORTY,0),'sheave')
    for z in [-.20,.20]:
        m.box((.44,PORTY,z-.028),(.50,1.10,z+.028),2,'belt')
    for x in [-px,px]: m.socket((x,PORTY,0),'x',1 if x>0 else -1,low)
    # THE TURNING PART: authored Y-UP like the valve handwheel and stood upright by the catalog's part
    # rotation, so both moving parts spin about their own local Y and there is one animation path.
    pulley=Mesh()
    if low:
        cylinder(pulley,.24,-.05,.05,6,3,group='pulley')
    else:
        pulley.lathe([(-.06,.20),(-.06,.26),(-.025,.26),(-.025,.225),(.025,.225),(.025,.26),
                      (.06,.26),(.06,.20)],12,3,'pulley_rim',caps=True)
        cylinder(pulley,.085,-.045,.045,8,0,group='pulley_hub')
        for i in range(4):
            pulley.box_oriented((.07,-.022,-.022),(.215,.022,.022),math.tau*i/4,0,'pulley_spoke')
    return m,pulley,(.47,1.10,0)

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
    for x in [-.5,.5]: m.socket((x,PORTY,0),'x',1 if x>0 else -1,low)
    for x in [-.32,.32]:
        m.box((x-BORE,1.2-BAND/2,-BORE),(x+BORE,1.2+BAND/2,BORE),2,'trigger_housing')
    m.box((-.32,1.2-BAND-STEM,-STEM/2),(.32,1.2-BAND,STEM/2),3,'trigger_support')
    # THE HANDWHEEL. Colour 1 is the palette's red texel, in both states.
    wheel=Mesh()
    if low:
        cylinder(wheel,.26,-.04,.04,6,1,group='handle')
    else:
        wheel.lathe([(-.045,.175),(-.045,.26),(.045,.26),(.045,.175)],12,1,'handle_rim',caps=True)
        cylinder(wheel,.075,-.05,.05,8,1,group='handle_hub')   # a lathe to radius 0 makes n zero-area faces
        for i in range(5):
            wheel.box_oriented((.06,-.024,-.024),(.245,.024,.024),math.tau*i/5,1,'handle_spoke')
    return m,wheel,(0,1.44,0)

def transformer(id,low):
    """Refinery, sluice and purifier -- the three strawberry asked to enlarge, ~1.8x the old envelope.

    "the purifier, refinery, pump and sluice should be much bigger (keeping the Io pipes the same
    size.)" So every HEX/BORE fitting here is untouched and the hose anchors travel outward to the new
    body sides instead (PORTX), because a spigot left at +-0.5 would now be buried inside the hull."""
    m=Mesh(); px=PORTX[id]
    n=6 if low else 8
    if id==9116:
        # RETORT: a tall vessel over a firebox, venting through a stack. 12 sides like the barrel.
        m.box((-.72,0,-.52),(.72,BAND,.52),2,'base')
        cylinder(m,.44,.16,2.05,6 if low else 12,0,center=(.16,0,0),group='retort')
        if not low:
            for h in [.62,1.42]: cylinder(m,.44+LIP,h,h+BAND,12,3,center=(.16,0,0),group='retort_band')
        cylinder(m,.40,2.05,2.16,6 if low else 12,3,center=(.16,0,0),group='retort_cap')
        cylinder(m,BORE*1.6,2.16,2.78,6,3,center=(.16,0,0),group='stack')
        m.box((-.66,BAND,-.34),(-.18,.86,.34),1,'firebox')
        m.box((-.60,.86,-.26),(-.24,1.02,.26),2,'firebox_hood')
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
    # ONE STUB PER PORT, off the body wall. A single pipe across the whole width ran straight THROUGH
    # the refinery's vessel and out the far side, which read as a skewer rather than plumbing.
    if id==9116: stub(m,.60,px); stub(m,-.28,-px)          # off the retort wall (r .44 about x .16)
    elif id==9121: stub(m,.70,px); stub(m,-.70,-px)        # off the outer filter columns
    else: stub(m,.74,px); stub(m,-.74,-px)                 # sluice: through the end headers below
    for x in [-px,px]: m.socket((x,PORTY,0),'x',1 if x>0 else -1,low)
    return m

def manifold(combine,low):
    m=Mesh();m.box((-.45,0,-.45),(.45,BAND,.45),2,'base')
    m.box((-STEM/2,BAND,-.32+LIP),(STEM/2,.6,.32-LIP),3,'support')
    cylinder(m,HEX,-.32,.32,6,0,'z',(0,.6,0),'manifold')
    anchors=[(-.5,0),(.5,-.32),(.5,.32)]
    if combine:anchors=[(-x,z) for x,z in anchors]
    for x,z in anchors:
        cylinder(m,BORE,min(0,x*.8),max(0,x*.8),6,3,'x',(0,.6,z),'branch')
        m.socket((x,.6,z),'x',1 if x>0 else -1,low)
    return m

def endpoint(drain,low):
    m=Mesh(); n=6 if low else 8
    if drain:
        # ONE closed solid. It used to be an open lathe plus a separate disc welded flush to it,
        # which left the bowl rim open (8 boundary edges) AND put four faces on the weld ring.
        m.lathe([(0,TANK),(.1,TANK),(.1,TANK-LIP),(.05,TANK-LIP)],n,3,'drain_bowl',caps=True)
        if not low:
            for x in [-.25,0,.25]:m.box((x-STEM/2,.1,-.32),(x+STEM/2,.1+STEM,.32),3,'grate')
    else:
        cylinder(m,.25,0,BAND,n,3,group='strainer_base')
        cylinder(m,.25,.5-BAND,.5,n,0,group='strainer_top')
        # OVERLAP the cage into the end caps rather than butting it flush against them: a shared ring
        # is an edge with four faces on it, which is the non-manifold seam the audit was counting.
        if low:cylinder(m,.25-STEM/4,BAND/2,.5-BAND/2,6,2,group='strainer')
        else:
            for i in range(8):
                a=math.tau*i/8;x=.25*math.cos(a);z=.25*math.sin(a)
                m.box((x-STEM/2,BAND,z-STEM/2),(x+STEM/2,.5-BAND,z+STEM/2),3,'strainer_ribs')
    # ONE hose height for these too. They used to sit at .7 while everything else was at .6; the
    # catalog now publishes a single height per device and the mesh has to agree with it, or the
    # spigot and the port drift apart (verify_fluid_art.py asserts they don't).
    cylinder(m,BORE,.1 if drain else .5,PORTY,6,3,group='riser')
    cylinder(m,BORE,0,.45,6,3,'z',(0,PORTY,0),'outlet_neck')
    m.socket((0,PORTY,.55),'z',low=low)
    return m

def main():
    OUT.mkdir(exist_ok=True)
    catalog={}; files=[]
    for id,name in [(9110,'Tank'),(9111,'Water source'),(9114,'Pump'),(9115,'Valve'),(9116,'Refinery'),(9117,'Sluice'),(9121,'Purifier'),(9112,'Splitter'),(9113,'Combiner'),(9119,'Inlet'),(9120,'Drain')]:
        palette=([BLUE,BLUE_LIGHT,DARK,METAL] if id in [9110,9112] else
                 [GREEN,GREEN_LIGHT,DARK,METAL] if id in [9111,9113,9119] else
                 [YELLOW,BLUE_LIGHT,DARK,METAL] if id==9114 else
                 [GREEN_LIGHT,RED,DARK,METAL] if id==9115 else
                 [WHITE,BLUE_LIGHT,DARK,METAL] if id==9121 else [RUST,RUST_LIGHT,DARK,METAL])
        im=Image.new('RGB',(2,2));im.putdata(palette);im.save(OUT/f'{id}_palette.png')
        entry=dict(name=name,palette=palette,part=None,partRot=None,portX=PORTX.get(id,.5),
                   portY=BARREL_PORTY if id in (9110,9111) else PORTY)
        for low in [False,True]:
            part=None; offset=None; suffix='_lod1' if low else ''
            if id in [9110,9111]:m=barrel(id==9111,low)
            elif id==9114:m,part,offset=pump(low)
            elif id==9115:m,part,offset=valve(low)
            elif id in [9116,9117,9121]:m=transformer(id,low)
            elif id in [9112,9113]:m=manifold(id==9113,low)
            else:m=endpoint(id==9120,low)
            body=m.write(OUT/f'{id}_body{suffix}.txt');files.append(body)
            if part:
                ps=part.write(OUT/f'{id}_part{suffix}.txt');files.append(ps)
                preview=Mesh();preview.add(m);preview.add(part,offset)
                pr=preview.write(OUT/f'{id}_preview{suffix}.txt');files.append(pr)
                if not low:
                    entry['part']=list(offset)
                    # the pump's pulley is authored flat and stood upright onto the motor shaft here
                    entry['partRot']=[0,0,-90] if id==9114 else [0,0,0]
            else: pr=body
            entry['lod1' if low else 'mesh']=pr
        lo=entry['mesh']['min'];hi=entry['mesh']['max']
        entry['boundsMin']=lo
        entry['boundsSize']=[round(b-a+(0.01 if id==9114 and i==1 else 0),6) for i,(a,b) in enumerate(zip(lo,hi))]
        entry['offset']=entry['boundsSize'][1]/2
        entry['radius']=min(.5,entry['boundsSize'][0]/2,entry['boundsSize'][2]/2,entry['offset']-STEM)
        catalog[str(id)]=entry
    (OUT/'catalog.json').write_text(json.dumps(catalog,indent=2)+'\n')
    (ROOT/'notes/FLUID_MESH_BOUNDS.json').write_text(json.dumps(files,indent=2)+'\n')
    print('\n'.join(f"{k} {v['name']}: {v['mesh']['triangles']} / {v['lod1']['triangles']} tris, bounds {v['boundsSize']}" for k,v in catalog.items()))

if __name__=='__main__':main()
