#!/usr/bin/env python3
"""Read-only OBJ survey; run BEFORE author_fluid_art.py. Never derives stats from notes.

Counts raw v records (seams included), unique positions and first-three-corner
triangles, like ContentProvider.ParseObj. Ring detection follows actual edges in
3D, including tilted rings: equal adjacent edges + regular-polygon angle propose
a circle; accept only a complete, connected, equally spaced perimeter (n >= 5).
Ellipses, partial arcs and irregular organic sections are not reported as rings.
"""
from pathlib import Path
from collections import defaultdict, Counter
from itertools import combinations
import math
import json
import statistics
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
CONTENT = ROOT / 'game/content'

def sub(a,b): return tuple(x-y for x,y in zip(a,b))
def dot(a,b): return sum(x*y for x,y in zip(a,b))
def cross(a,b): return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def length(a): return math.sqrt(dot(a,a))
def unit(a):
    n=length(a)
    return tuple(x/n for x in a)

def parse(path):
    v=[]; vt=[]; vn=[]; faces=[]; widths=[]
    for line in path.read_text(errors='replace').splitlines():
        t=line.split()
        if not t: continue
        if t[0]=='v': v.append(tuple(map(float,t[1:4])))
        elif t[0]=='vt': vt.append(tuple(map(float,t[1:3])))
        elif t[0]=='vn': vn.append(tuple(map(float,t[1:4])))
        elif t[0]=='f':
            widths.append(len(t)-1)
            faces.append([tuple(int(x)-1 if x else -1 for x in c.split('/')) for c in t[1:4]])
    return v,vt,vn,faces,widths

def rings(v, faces):
    # Weld export seam duplicates to 10 micrometres, below the recorded feature scale.
    keys={}; points=[]; remap=[]
    for p in v:
        k=tuple(round(x,5) for x in p)
        if k not in keys: keys[k]=len(points); points.append(p)
        remap.append(keys[k])
    adj=defaultdict(set)
    for f in faces:
        ids=[remap[c[0]] for c in f]
        for a,b in zip(ids,ids[1:]+ids[:1]):
            if a!=b: adj[a].add(b); adj[b].add(a)
    candidates=set(); found={}
    for b,neighbors in adj.items():
        for a,c in combinations(neighbors,2):
            u=sub(points[a],points[b]); w=sub(points[c],points[b]); lu=length(u); lw=length(w)
            if lu<1e-5 or abs(lu-lw)>max(2e-5,lu*0.001): continue
            theta=math.acos(max(-1,min(1,dot(u,w)/(lu*lw))))
            if math.pi-theta<0.08: continue
            nf=2*math.pi/(math.pi-theta); n=round(nf)
            if n<5 or n>64 or abs(nf-n)>.025: continue
            normal=unit(cross(u,w)); bis=unit(tuple(x/lu+y/lw for x,y in zip(u,w)))
            radius=(lu+lw)/4/math.sin(math.pi/n)
            center=tuple(x+y*radius for x,y in zip(points[b],bis))
            key=(n,*(round(x,4) for x in center),round(radius,4),*(round(abs(x),3) for x in normal))
            if key in candidates: continue
            candidates.add(key)
            tol=max(3e-5,radius*.0005)
            on=[i for i,p in enumerate(points) if abs(dot(sub(p,center),normal))<tol and abs(length(sub(p,center))-radius)<tol]
            if len(on)!=n: continue
            ax=unit(sub(points[on[0]],center)); ay=cross(normal,ax)
            on.sort(key=lambda i:math.atan2(dot(sub(points[i],center),ay),dot(sub(points[i],center),ax)))
            if not all(j in adj[i] and abs(length(sub(points[i],points[j]))-lu)<tol*3 for i,j in zip(on,on[1:]+on[:1])): continue
            found[tuple(sorted(on))]={'n':n,'center':center,'radius':radius,'normal':normal}
    return list(found.values())

def stats(path, detect=True):
    v,vt,vn,f,widths=parse(path)
    if not v or not f: return None
    lo=[min(p[i] for p in v) for i in range(3)]; hi=[max(p[i] for p in v) for i in range(3)]
    tex=path.with_name(path.stem+'_tex.png')
    sampled=[]; colours=[]; centers=None; dims=None
    if tex.exists():
        im=Image.open(tex).convert('RGB'); w,h=im.size; dims=[w,h]
        used={c[1] for face in f for c in face if len(c)>1 and 0<=c[1]<len(vt)}
        sampled=sorted({(min(w-1,int((vt[i][0]%1)*w)),min(h-1,int(((1-vt[i][1])%1)*h))) for i in used})
        colours=sorted({im.getpixel(p) for p in sampled})
        centers=sum(abs((vt[i][0]*w)%1-.5)<1e-4 and abs(((1-vt[i][1])*h)%1-.5)<1e-4 for i in used)
        centers=[centers,len(used)]
    return dict(path=str(path.relative_to(CONTENT)),v=len(v),unique=len(set(v)),vn=len(vn),vt=len(vt),tri=len(f),
        lo=lo,hi=hi,size=[b-a for a,b in zip(lo,hi)],nontri=sum(x!=3 for x in widths),
        rings=rings(v,f) if detect else [],tex=dims,texels=sampled,colours=colours,centers=centers)

def fmt(v): return ', '.join(f'{x:.6f}' for x in v)

def row(s):
    rr=Counter(r['n'] for r in s['rings'])
    rs=', '.join(f'{n}×{k}' for n,k in sorted(rr.items())) or '0 detected'
    return f"| {s['path']} | {s['v']} | {s['unique']} | {s['tri']} | {fmt(s['size'])} | {rs} | {str(s['tex']) if s['tex'] else 'no paired PNG'} | {len(s['texels']) if s['tex'] else '—'} | {len(s['colours']) if s['tex'] else '—'} | {s['centers'] or '—'} |"

def main():
    # Every pre-existing object OBJ (including deployables and LODs); every root TXT
    # containing geometry. No filename/name-based search for "round" objects.
    files=sorted((CONTENT/'objects').glob('*.obj'))+sorted(CONTENT.glob('*.txt'))
    result=[]
    for i,p in enumerate(files):
        if p.stem.startswith('Fluid_'): continue
        s=stats(p)
        if s: result.append(s)
        if i%100==0: print(f'{i}/{len(files)}',flush=True)
    (ROOT/'.verify').mkdir(exist_ok=True)
    (ROOT/'.verify/fluid_art_survey.json').write_text(json.dumps(result,indent=2)+'\n')
    catalog={x.split()[1] for x in (CONTENT/'objects/guid_mesh.txt').read_text().splitlines() if x and not x.startswith('#')}
    primary=[s for s in result if Path(s['path']).stem in catalog and s['path'].startswith('objects/')]
    near=[s for s in primary if .75<=sorted(s['size'])[-1]<=1.75 and sorted(s['size'])[0]>=.35]
    summary=['| Population | Meshes | v min / median / max | tris min / median / max |','|---|---:|---|---|']
    for name,ss in [('Catalog primary props',primary),('Catalog props: max dimension 0.75–1.75 m, min dimension ≥0.35 m',near),('All parsed OBJ/TXT (includes LODs, parts, vehicles, weapons)',result)]:
        summary.append(f"| {name} | {len(ss)} | "+' | '.join(f"{min(s[k] for s in ss)} / {statistics.median(s[k] for s in ss):g} / {max(s[k] for s in ss)}" for k in ['v','tri'])+' |')
    header=['| Mesh | v records | Unique positions | Tris | Local AABB size X,Y,Z (m) | Ring segments × rings | PNG W,H | Sampled texels | RGB colours | Centre UVs / referenced UVs |','|---|---:|---:|---:|---|---|---|---:|---:|---|']
    names=['Barrel_0','Barrel_1','Barrel_2','Propane_0','Fire_Hydrant_0','Generator_0','Gas_Pump_0','Canister_0','Canister_1','Tank_Fuel_0','Tower_Water_0','Pipe_0','Sink_0','Sink_1']
    comp=[s for s in result if Path(s['path']).stem in names]
    out=['# Fluid art measurements — before authoring','',
         '| Measurement | Method |','|---|---|',
         '| Source | Worktree content at 827516d6; `python3 tools/measure_fluid_art.py`; OBJ records parsed directly |',
         '| Units | 1 mesh unit = 1 m; table extents are local XYZ, not guessed world orientation |',
         '| Vertices | Raw `v` includes seam duplicates; unique positions also listed; runtime ParseObj emits 3 vertices per triangle |',
         '| Faces | First 3 corners, matching ParseObj; no triangulation of quads assumed |',
         '| Rings | Exhaustive edge scan of all parsed meshes, any orientation; regular closed 5–64 segment polygons; 0 means none detected, not proof of no curved surface |',
         '| Ring exclusions | Partial arcs, deformed/elliptical sections, fewer than 5 sides, missing perimeter edges; organic curved surfaces do not have a single radial count |',
         '| Palette samples | Referenced corner UVs, repeat addressing, floor(U×W), floor((1−V)×H); PNG row 0 is top; distinct coordinates and RGB values counted separately |',
         '| Texel centres | OBJ UV ((x+0.5)/W, 1−(y+0.5)/H); existing UVs need not be exact centres |','']+summary+['','## Comparables','']+header+[row(s) for s in comp]+['','## Catalog near-size population','']+header+[row(s) for s in near]+['','## All parsed meshes (one row per mesh)','']+header+[row(s) for s in result]
    extra = ROOT/'tools/fluid_art_measurement_details.md'
    out[out.index(summary[0]):out.index(summary[0])] = [extra.read_text(), '']
    (ROOT/'notes/FLUID_ART_MEASUREMENTS.md').write_text('\n'.join(out)+'\n')
    print('\n'.join(summary+header+[row(s) for s in comp]))

if __name__=='__main__': main()
