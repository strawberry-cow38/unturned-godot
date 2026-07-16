#!/usr/bin/env python3
# Extract generator_small item.prefab (1st-person held) mesh + palette tex -> content/generator_hold.obj + _tex.png
# Viewmodel convention (extract_consumable.py): negate X+Z, reverse winding so it holds correctly in-hand.
import UnityPy, os
MB="/home/ec2-user/unturned-bundles/Bundles/core.masterbundle"
OUT="/home/ec2-user/projects/unturned-godot/game/content"
env=UnityPy.load(MB); cont=dict(env.container); byid={o.path_id:o for o in env.objects}
def get(path):
    for p,o in cont.items():
        if str(p).lower()==path: return o
def comp(tt,names):
    for c in tt.get("m_Component",[]):
        cc=c.get("component",c) if isinstance(c,dict) else c
        co=byid.get(cc.get("m_PathID")) if isinstance(cc,dict) else None
        if co and co.type.name in names: return co
def pptr(v): return byid.get(v.get("m_PathID")) if isinstance(v,dict) else None
go=get("assets/coremasterbundle/items/barricades/generator_small/item.prefab")
tt=go.read_typetree()  # root "Item" holds the MeshFilter/MeshRenderer
mf=comp(tt,("MeshFilter",)); mr=comp(tt,("MeshRenderer",))
mesh=byid.get(mf.read_typetree().get("m_Mesh",{}).get("m_PathID"))
txt=mesh.read().export()
Vs,Ns,Ts,Fs=[],[],[],[]
for line in txt.splitlines():
    p=line.split()
    if not p: continue
    if p[0]=="v": Vs.append((-float(p[1]),float(p[2]),-float(p[3])))
    elif p[0]=="vn": Ns.append((-float(p[1]),float(p[2]),-float(p[3])))
    elif p[0]=="vt": Ts.append((p[1],p[2]))
    elif p[0]=="f":
        idx=[]
        for tok in p[1:]:
            q=tok.split("/"); idx.append((int(q[0]),(int(q[1]) if len(q)>1 and q[1] else None),(int(q[2]) if len(q)>2 and q[2] else None)))
        Fs.append(list(reversed(idx)))
L=["# generator_small item.prefab (held) -> Godot, X+Z negated, winding reversed"]
L+=["v %.6f %.6f %.6f"%v for v in Vs]; L+=["vt %s %s"%t for t in Ts]; L+=["vn %.6f %.6f %.6f"%n for n in Ns]
for f in Fs:
    s="f"
    for (vi,ti,ni) in f:
        s+=(" %d/%d/%d"%(vi,ti,ni)) if (ti and ni) else ((" %d//%d"%(vi,ni)) if ni else ((" %d/%d"%(vi,ti)) if ti else " %d"%vi))
    L.append(s)
open(os.path.join(OUT,"generator_hold.obj"),"w").write("\n".join(L)+"\n")
# palette texture
alb="NONE"
if mr:
    mats=mr.read_typetree().get("m_Materials",[]); mo=pptr(mats[0]) if mats else None
    if mo:
        for pair in mo.read_typetree().get("m_SavedProperties",{}).get("m_TexEnvs",[]):
            nm,val=(pair[0],pair[1]) if isinstance(pair,(list,tuple)) else (pair.get("first"),pair.get("second"))
            if nm=="_MainTex" and isinstance(val,dict):
                to=pptr(val.get("m_Texture",{}))
                if to: to.read().image.convert("RGBA").save(os.path.join(OUT,"generator_hold_tex.png")); alb="generator_hold_tex.png"
print(f"held mesh: {len(Vs)} verts, {len(Fs)} tris, palette={alb}")
