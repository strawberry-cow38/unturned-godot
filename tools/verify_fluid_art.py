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

ROOT=Path(__file__).resolve().parents[1]
DIR=ROOT/'game/content/fluid'
MANIFEST=ROOT/'notes/FLUID_MESH_BOUNDS.json'

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
        elif id==9112: ports=[(-px,py,0),(px,py,-.32),(px,py,.32)]
        elif id==9113: ports=[(-px,py,-.32),(-px,py,.32),(px,py,0)]
        else: ports=[(-px,py,0),(px,py,0)]
        v,_,_,_,_=parse(DIR/f'{id}_body.txt')
        for port in ports:
            axis=2 if id in [9111,9119,9120] else 0
            ring=[q for q in v if abs(q[axis]-port[axis])<1e-6 and abs(length(sub(q,port))-.15249)<2e-6]
            assert len(ring)==6,(id,port,len(ring),'no spigot collar at the port anchor')
            checked+=1

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
    print(f'PASS: {len(claimed)} meshes / {total} triangles, {checked} hose anchors carry a modelled spigot')

if __name__=='__main__':main()
