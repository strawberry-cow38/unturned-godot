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
TANK=.5-LIP  # reserve a barrel-band projection for unchanged ±0.5 hose anchors
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
        for i in range(1,len(ids)-1):
            self.faces.append(([ids[0],ids[i+1],ids[i]],colour,[normals[0],normals[i+1],normals[i]],group))
    def box(self, lo, hi, colour=0, group='frame'):
        x,y,z=lo; X,Y,Z=hi
        for p in [((x,y,z),(x,y,Z),(x,Y,Z),(x,Y,z)),((X,y,Z),(X,y,z),(X,Y,z),(X,Y,Z)),
                  ((x,y,Z),(x,y,z),(X,y,z),(X,y,Z)),((x,Y,z),(x,Y,Z),(X,Y,Z),(X,Y,z)),
                  ((X,y,z),(x,y,z),(x,Y,z),(X,Y,z)),((x,y,Z),(X,y,Z),(X,Y,Z),(x,Y,Z))]: self.face(p,colour,group)
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
        # The mouth plane is exactly the unchanged HosePort centre. Its dark back
        # is recessed one measured barrel lip; inside wall is modelled at LOD0.
        old=len(self.v)
        tmp=Mesh()
        if low:
            tmp.lathe([(-BAND,HEX),(0,HEX)],6,3,'hose_fitting',caps=True,end_colour=2)
        else:
            tmp.lathe([(-BAND,HEX),(0,HEX),(0,BORE),(-LIP,BORE)],6,3,'hose_fitting',caps=False)
            pts=[(BORE*math.cos(i*math.tau/6),-LIP,BORE*math.sin(i*math.tau/6)) for i in range(6)]
            tmp.face(pts[::-1],2,'socket_back')
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
               '# Y-up metres, explicit normals, triangle-only, clockwise front; palette V-up']
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

def barrel(source,low):
    m=Mesh(); n=6 if low else 12
    if low: cylinder(m,TANK,0,1.3,n,1,group='vessel')
    else:
        # Barrel's exact 24 + 3*44 topology, stretched to the functional envelope.
        m.lathe([(.05,TANK-LIP),(1.3,TANK-LIP)],12,1,'vessel',caps=False)
        for a in [0,.6,1.2]: cylinder(m,TANK,a,a+BAND,12,0,group='bands')
    if source:
        # Shoulder and hatch use the generator/propane 0.25 radius and band height.
        m.lathe([(1.3,TANK),(1.4,MOTOR)],n,1,'shoulder')
        cylinder(m,BORE,.25,.45,6,3,'z',(0,.7,0),'outlet_neck')
        m.socket((0,.7,.55),'z',low=low)
    else:
        cylinder(m,MOTOR,1.3,1.4,6,3,group='lid')
        m.socket((-.5,.7,0),'x',-1,low);m.socket((.5,.7,0),'x',1,low)
    return m

def pump(low):
    m=Mesh()
    # Original fitting envelope 0.9 m; ±0.32 are the existing trigger anchors.
    for z in [-.32,.32]: m.box((-.45,0,z-BAND/2),(.45,BAND,z+BAND/2),2,'skids')
    m.box((-.32,BAND,-.25),(.32,.6,.25),0,'pedestal')
    cylinder(m,MOTOR,-.4,.4,6 if low else 8,3,'x',(0,.6,0),'pump_chamber')
    m.box((-MOTOR,.6,-MOTOR+LIP),(MOTOR,1.25,MOTOR-LIP),0,'motor_mount')
    # Terminal boards touch the unchanged power / remote-signal anchors.
    m.box((-.32-BORE,1.25-BORE,-.42),(.32+BORE,1.25+BORE,-.25),2,'rear_terminals')
    m.box((-BORE,1.25-BORE,.25),(BORE,1.25+BORE,.42),2,'power_terminal')
    for x in [-.5,.5]: m.socket((x,.6,0),'x',1 if x>0 else -1,low)
    drum=Mesh(); cylinder(drum,MOTOR,-MOTOR,MOTOR if low else .15,6,0,'z',group='motor')
    if not low:
        # A 0.1 m end belt matches barrel bands; same six sides as the motor.
        cylinder(drum,MOTOR,.15,.25,6,3,'z',group='motor_end')
    return m,drum,(0,1.25,0)

def valve(low):
    m=Mesh();m.box((-.25,0,-.25),(.25,.1,.25),2,'base')
    m.box((-BORE,.1,-BORE),(BORE,.6,BORE),3,'pedestal')
    cylinder(m,BORE,-.4,.4,6,3,'x',(0,.6,0),'valve_pipe')
    cylinder(m,HEX,.6,.7,6,3,group='bonnet')
    m.box((-STEM/2,.7,-STEM/2),(STEM/2,1.4-STEM/2,STEM/2),3,'stem')
    for x in [-.5,.5]: m.socket((x,.6,0),'x',1 if x>0 else -1,low)
    for x in [-.32,.32]:
        m.box((x-BORE,1.2-BAND/2,-BORE),(x+BORE,1.2+BAND/2,BORE),2,'trigger_housing')
    m.box((-.32,1.2-BAND- STEM,-STEM/2),(.32,1.2-BAND,STEM/2),3,'trigger_support')
    wheel=Mesh()
    if low: cylinder(wheel,.3,-.05,.05,6,0,group='handle')
    else:
        wheel.lathe([(-.05,.3),(.05,.3),(.05,.3-STEM),(-.05,.3-STEM),(-.05,.3)],12,0,'handle',caps=False)
        wheel.box((-.3+STEM,-STEM/2,-STEM/2),(.3-STEM,STEM/2,STEM/2),0,'handle_spoke')
    return m,wheel,(0,1.4,0)  # two band heights clear the unchanged Y=1.2 trigger housings

def transformer(id,low):
    m=Mesh(); m.box((-.45,0,-.45),(.45,BAND,.45),2,'base')
    if id==9116:
        cylinder(m,MOTOR,.1,1.4,6 if low else 12,0,center=(.15,0,0),group='retort')
        cylinder(m,BORE,1.4,1.75,6,3,center=(.15,0,0),group='stack')
        m.box((-.4,.1,-.25),(-.1,.6,.25),1,'heater')
        if not low: cylinder(m,MOTOR+LIP,.6,.7,12,3,center=(.15,0,0),group='retort_band')
    elif id==9117:
        # Trough fall is twice the measured 0.1 band; ports stay at Y=.6.
        for x in [-.32,.32]:m.box((x-STEM/2,BAND,-.32),(x+STEM/2,.5,.32),3,'trestle')
        # Build sloping slabs by moving Y as a function of X (0.7 -> 0.5).
        tray=Mesh();tray.box((-.45,0,-.32),(.45,STEM,.32),0,'trough_floor')
        for z in [-.32,.32]:tray.box((-.45,STEM,z-STEM/2),(.45,BAND+STEM,z+STEM/2),1,'trough_wall')
        if not low:
            for x in [-.32,0,.32]:tray.box((x-STEM/2,STEM,-.32),(x+STEM/2,STEM+LIP,.32),3,'riffles')
        m.add(tray,transform=lambda p:(p[0],p[1]+.6-p[0]*(.2/.9),p[2]))
    else:
        for x in [-.25,.25]:
            m.lathe([(.1,HEX),(1.15,HEX),(1.25,BORE)],6 if low else 8,0,'filter',center=(x,0,0))
            if not low:cylinder(m,HEX+LIP,.6,.7,8,1,center=(x,0,0),group='filter_clamp')
        m.box((-.32,BAND,.25),(.32,1.25+BORE,.42),2,'control_cabinet')
    if id!=9117: cylinder(m,BORE,-.4,.4,6,3,'x',(0,.6,0),'cross_pipe')
    for x in [-.5,.5]:m.socket((x,.6,0),'x',1 if x>0 else -1,low)
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
        m.lathe([(0,TANK),(.1,TANK),(.1,TANK-LIP),(.05,TANK-LIP)],n,3,'drain_bowl',caps=False)
        cylinder(m,TANK-LIP,0,.05,n,2,group='bowl_bottom')
        if not low:
            for x in [-.25,0,.25]:m.box((x-STEM/2,.1,-.32),(x+STEM/2,.1+STEM,.32),3,'grate')
    else:
        cylinder(m,.25,0,BAND,n,3,group='strainer_base')
        cylinder(m,.25,.5-BAND,.5,n,0,group='strainer_top')
        if low:cylinder(m,.25,BAND,.5-BAND,6,2,group='strainer')
        else:
            for i in range(8):
                a=math.tau*i/8;x=.25*math.cos(a);z=.25*math.sin(a)
                m.box((x-STEM/2,BAND,z-STEM/2),(x+STEM/2,.5-BAND,z+STEM/2),3,'strainer_ribs')
    cylinder(m,BORE,.1 if drain else .5,.7,6,3,group='riser')
    cylinder(m,BORE,0,.45,6,3,'z',(0,.7,0),'outlet_neck')
    m.socket((0,.7,.55),'z',low=low)
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
        entry=dict(name=name,palette=palette,part=None)
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
                if not low:entry['part']=list(offset)
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
