#!/usr/bin/env python3
"""Spike-free cactus geometry library; existing local foliage is the style reference.

Native Y-up; root datum Y=0; no game spawning/collision/registration changes.
"""
from pathlib import Path
import sys, math, json, hashlib, collections
sys.path.insert(0, '/home/ec2-user/.local/lib/python3.9/site-packages')
from PIL import Image

ROOT = Path(__file__).resolve().parent
MODELS = ROOT / 'models'
MODELS.mkdir(exist_ok=True)
REPO = ROOT.parent / 'ug-astra-wt'
PALETTE = [
    (54,109,55),   # Existing Bush_0/1 body green.
    (47,90,48),    # Existing Pine leading canopy green.
    (60,114,61),   # Existing Maple leading canopy green.
    (79,130,70),   # New neighbouring green, not a photo texture.
    (105,147,82),  # New muted crown highlight.
    (186,171,152), # Unused legacy pale swatch; no model samples it.
    (92,70,44),    # Existing Maple trunk brown, buried root end only.
    (111,140,76),  # Existing Birch leading canopy green.
]
atlas = Image.new('RGB', (32,4))
for k, rgb in enumerate(PALETTE):
    for y in range(4):
        for x in range(4*k, 4*k+4): atlas.putpixel((x,y),rgb)
atlas.save(MODELS / 'Cactus_Palette.png')

def add(a,b): return tuple(x+y for x,y in zip(a,b))
def sub(a,b): return tuple(x-y for x,y in zip(a,b))
def mul(a,s): return tuple(x*s for x in a)
def dot(a,b): return sum(x*y for x,y in zip(a,b))
def cross(a,b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def length(a): return math.sqrt(dot(a,a))
def norm(a):
    l=length(a)
    assert l>1e-10, a
    return mul(a,1/l)
def average(ps): return tuple(sum(p[k] for p in ps)/len(ps) for k in range(3))
def bounds(ps): return {'min':[min(p[k] for p in ps) for k in range(3)], 'max':[max(p[k] for p in ps) for k in range(3)]}
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()

class Model:
    def __init__(self,name,label):
        self.name=name; self.label=label; self.tris=[]; self.parts=[]
    def tri(self,a,b,c,color,part,out):
        n=cross(sub(b,a),sub(c,a))
        if dot(n,out)<0: b,c=c,b; n=mul(n,-1)
        n=norm(n)
        self.tris.append({'p':[a,b,c],'n':n,'color':color,'part':part})
    def tube(self,part,stations,sides=12):
        """Closed faceted ring tube. stations = (centreXYZ,radius), final tip has r=0."""
        assert all(r>0 for _,r in stations[:-1]) and stations[-1][1]==0
        cs=[tuple(c) for c,_ in stations]
        rings=[]
        for j,(centre,radius) in enumerate(stations[:-1]):
            tangent=norm(sub(cs[min(j+1,len(cs)-1)],cs[max(0,j-1)]))
            # All new stems are in XY plus a small Z offset; a stable Z reference avoids twists.
            u=norm(cross(tangent,(0,0,1)))
            v=norm(cross(tangent,u))
            ring=[]
            for k in range(sides):
                angle=2*math.pi*k/sides
                rr=radius*(1.0 if k%2==0 else .91)
                ring.append(add(centre,add(mul(u,rr*math.cos(angle)),mul(v,rr*math.sin(angle)))))
            rings.append(ring)
        for j in range(len(rings)-1):
            centre=average((cs[j],cs[j+1]))
            for k in range(sides):
                k1=(k+1)%sides
                a,b,c,d=rings[j][k],rings[j][k1],rings[j+1][k1],rings[j+1][k]
                colour=[0,2,1,2,0,3][k%6]
                for ps in ((a,b,c),(a,c,d)):
                    self.tri(*ps,colour,part,sub(average(ps),centre))
        root_normal=mul(norm(sub(cs[1],cs[0])),-1)
        for k in range(sides): self.tri(cs[0],rings[0][k],rings[0][(k+1)%sides],6,part,root_normal)
        apex=cs[-1]
        for k in range(sides):
            ps=(rings[-1][k],rings[-1][(k+1)%sides],apex)
            self.tri(*ps,4 if k%2 else 7,part,sub(average(ps),cs[-2]))
        self.parts.append({'name':part,'kind':'closed_ribbed_stem','stations':stations,'sides':sides})
    def pad(self,part,centre,width,height,depth,angle=0):
        """Closed flattened octagonal oval, raised middle on front and rear."""
        angle=math.radians(angle); ca,sa=math.cos(angle),math.sin(angle)
        def world(x,y,z): return add(centre,(ca*x-sa*y,sa*x+ca*y,z))
        front=[]; back=[]
        for k in range(8):
            a=math.pi/2+k*math.pi/4
            x=width*.5*math.cos(a); y=height*.5*math.sin(a)
            front.append(world(x,y,depth*.24)); back.append(world(x,y,-depth*.24))
        fc=world(0,0,depth*.5); bc=world(0,0,-depth*.5)
        for k in range(8):
            k1=(k+1)%8
            self.tri(fc,front[k],front[k1],3 if k%3 else 2,part,(0,0,1))
            self.tri(bc,back[k],back[k1],0 if k%3 else 1,part,(0,0,-1))
            a,b,c,d=front[k],back[k],back[k1],front[k1]
            for ps in ((a,b,c),(a,c,d)):
                self.tri(*ps,1 if k%2 else 0,part,sub(average(ps),centre))
        self.parts.append({'name':part,'kind':'closed_flattened_pad','centre':centre,'width':width,'height':height,'depth':depth,'angle':angle})
        return world
