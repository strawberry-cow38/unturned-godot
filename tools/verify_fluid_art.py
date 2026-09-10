#!/usr/bin/env python3
"""Strict ParseObj-compatible validation of every delivered mesh and hose mouth.

Uses first-three-corner semantics, V flip, and separate per-corner indices; rejects
anything the permissive runtime could silently truncate or render incompletely.
"""
from pathlib import Path
import json
import math
from PIL import Image
from measure_fluid_art import parse, sub, dot, cross, length, rings
from audit_fluid_geometry import audit, load, components

ROOT=Path(__file__).resolve().parents[1]
DIR=ROOT/'game/content/fluid'
MANIFEST=ROOT/'notes/FLUID_MESH_BOUNDS.json'

def inside(point, vertices, comp):
    """Ray parity inside one closed solid; oblique ray avoids axial cap triangulation."""
    direction=(1.,.173,.297); hits=[]
    for t,_ in comp:
        a,b,c=[vertices[k[0]] for k in t]
        edge1=sub(b,a); edge2=sub(c,a); h=cross(direction,edge2); det=dot(edge1,h)
        if abs(det)<1e-10: continue
        s=sub(point,a); u=dot(s,h)/det
        if u<0 or u>1: continue
        q=cross(s,edge1); v=dot(direction,q)/det
        if v<0 or u+v>1: continue
        distance=dot(edge2,q)/det
        if distance>1e-8 and all(abs(distance-d)>1e-7 for d in hits): hits.append(distance)
    return len(hits)%2==1

def main():
    claimed=json.loads(MANIFEST.read_text())
    assert {p.name for p in DIR.glob('*.txt')}=={s['file'] for s in claimed}
    total=0
    for spec in claimed:
        p=DIR/spec['file']; v,vt,vn,fs,widths=parse(p)
        assert all(n==3 for n in widths),p
        assert len(v)==spec['vertices'] and len(fs)==spec['triangles'],p
        assert all(math.isfinite(x) for a in v+vt+vn for x in a),p
        assert len(vt)==4
        im=Image.open(DIR/f'{p.stem[:4]}_palette.png')
        assert im.size==(2,2)
        for u,t in vt:
            # Check exact texel centres AFTER ParseObj's 1-v transform.
            assert 0<u<1 and 0<t<1
            assert (u*2)%1==.5 and ((1-t)*2)%1==.5
        for f in fs:
            assert len(f)==3 and all(len(c)==3 for c in f),p
            assert all(0<=c[0]<len(v) and 0<=c[1]<len(vt) and 0<=c[2]<len(vn) for c in f),p
            a,b,c=[v[k[0]] for k in f]; geometric=cross(sub(b,a),sub(c,a))
            assert length(geometric)>1e-9,(p,f,'degenerate')
            for k in f:
                normal=vn[k[2]]
                assert abs(length(normal)-1)<2e-5,(p,'nonunit normal')
                # Smooth 8/12-sided shells preserve radial corner normals; the
                # measured barrel uses these, while caps and hex fittings are flat.
                # Smooth 8/12-sided shells preserve radial corner normals; the measured barrel uses
                # these, while caps and hex fittings are flat. CLOCKWISE front: ParseObj preserves file
                # winding, unlike ObjMesh.Load (retail props) which reverses it -- so the retail files
                # are NOT a valid comparison here. Flipping this to match them rendered everything
                # inside out (strawberry 2026-09-10: "all are rendering inside out").
                assert dot(geometric,normal)/length(geometric)<-.9,(p,'clockwise outward normal mismatch')
            assert len({k[1] for k in f})==1,(p,'triangle interpolates across palette cells')
        lo=[min(x[i] for x in v) for i in range(3)];hi=[max(x[i] for x in v) for i in range(3)]
        assert max(abs(x-y) for x,y in zip(lo+hi,spec['min']+spec['max']))<1e-6,(p,lo,hi)
        assert {r['n'] for r in rings(v,fs)} <= {6,8,12},p
        total+=len(fs)
        print(f"PASS {p.name}: {len(v)} v, {len(fs)} tris; AABB {lo} -> {hi}")
    # THE FITTING MUST BE WHERE THE PORT IS. The four machines strawberry enlarged moved their hose
    # anchors outward (their fittings did not grow), so the old hard-coded table is gone and this reads
    # the catalog the game itself reads -- which is the point: if the mesh and the port ever disagree,
    # the spigot ends up floating in mid-air detached from the body, which is exactly what the first
    # render of the enlarged pump showed. The spigot now PROTRUDES, so at the anchor plane the body
    # carries the collar ring at radius HEX rather than a bore.
    catalog=json.loads((DIR/'catalog.json').read_text())
    checked=0
    for id in [9110,9111,9112,9113,9114,9115,9116,9117,9119,9120,9121]:
        spec=catalog[str(id)]; px=spec['portX']; py=spec['portY']
        if id in [9111,9119,9120]: ports=[(0,py,.55)]
        elif id==9112: ports=[(-px,py,0)]+[(px,py,z) for z in spec['branchZ']]
        elif id==9113: ports=[(-px,py,z) for z in spec['branchZ']]+[(px,py,0)]
        else: ports=[(-px,py,0),(px,py,0)]
        for suffix in ['', '_lod1']:
            v,_,ts=load(DIR/f'{id}_body{suffix}.txt')
            backing=components(v,[(t,g) for t,g in ts if g!='hose_fitting'])
            for port in ports:
                axis=2 if id in [9111,9119,9120] else 0
                ring=[q for q in v if abs(q[axis]-port[axis])<1e-6 and abs(length(sub(q,port))-.15249)<2e-6]
                assert len(ring)==6,(id,suffix,port,len(ring),'no spigot collar at the port anchor')
                # A collar is not a connection if the pipe stops short of it. Sample its centre and
                # six bore points 12 mm INSIDE the collar, requiring a non-fitting solid behind them.
                axes=[k for k in range(3) if k!=axis]; sign=1 if port[axis]>0 else -1
                for i in range(7):
                    p=list(port); p[axis]+=.012*sign
                    if i:
                        p[axes[0]]+=.07*math.cos(math.tau*(i-1)/6)
                        p[axes[1]]+=.07*math.sin(math.tau*(i-1)/6)
                    assert any(inside(p,v,comp) for comp in backing),(id,suffix,port,'floating collar',p)
                if not suffix:checked+=1

    # Match the reference's component colour assignment through the actual V flip,
    # not just the list of RGBs in the PNG (which would miss swapped shell/bands).
    for id,reference in [(9110,'Barrel_0'),(9111,'Barrel_1')]:
        original=Image.open(ROOT/f'game/content/objects/{reference}_tex.png').convert('RGB')
        palette=Image.open(DIR/f'{id}_palette.png').convert('RGB')
        for suffix in ['', '_lod1']:
            path=DIR/f'{id}_body{suffix}.txt'; _,vt,_,_,_=parse(path); group=None
            for line in path.read_text().splitlines():
                fields=line.split()
                if fields[0]=='g':group=fields[1]
                elif fields[0]=='f' and group in ['vessel','bands']:
                    expected=original.getpixel((1 if group=='vessel' else 0,0))
                    for corner in fields[1:]:
                        u,v=vt[int(corner.split('/')[1])-1]
                        assert palette.getpixel((int(u*2),int((1-v)*2)))==expected,(id,group,'reference palette mismatch')
    items=json.loads((ROOT/'game/content/items/items_manifest.json').read_text())
    for id in range(9110,9122):
        spec=items[str(id)]; path=ROOT/'game/content/items'/spec['obj']
        assert path.suffix=='.obj' and path.is_file(),path
        assert not audit(path)[0],(path,audit(path)[0])
        v,vt,vn,fs,widths=parse(path)
        assert set(widths)=={3} and all(len(c)==3 for f in fs for c in f),path
        lo=[min(p[i] for p in v) for i in range(3)]; hi=[max(p[i] for p in v) for i in range(3)]
        assert max(hi[i]-lo[i] for i in range(3))<=.872201,(id,'oversized drop')
        assert all(abs(spec['box'][i]-(hi[i]-lo[i]))<2e-6 for i in range(3)),id
        assert all(abs(spec['center'][i]-(hi[i]+lo[i])/2)<2e-6 for i in range(3)),id
        palette=Image.open(path.with_name(spec['tex'])); assert palette.size==(2,2)
        assert vt==[(.25,.75),(.75,.75),(.25,.25),(.75,.25)],(id,'item palette UVs')
        icon=Image.open(ROOT/f'game/content/items/icons/{id}.png')
        assert icon.mode=='RGBA' and icon.size==(256,256),(id,'icon convention')
        alpha=icon.getchannel('A'); bbox=alpha.getbbox()
        assert alpha.getextrema()==(0,255) and bbox and min(bbox)>0 and max(bbox)<256,(id,'icon transparency/framing')
    print(f'PASS: {len(claimed)} meshes / {total} triangles, {checked} connected hose anchors at both LODs; 12 item meshes and icons')

if __name__=='__main__':main()
