#!/usr/bin/env python3
# Extract the Wire supply item's 1st-person held mesh (Model_0 + Model_1) + palette -> content/wire_hold.obj + _tex.png
# Viewmodel convention (extract_deployable_hold): negate X+Z, reverse winding.
import UnityPy, os
MB="/home/ec2-user/unturned-bundles/Bundles/core.masterbundle"; OUT="/home/ec2-user/projects/unturned-godot/game/content"
env=UnityPy.load(MB); cont=dict(env.container); byid={o.path_id:o for o in env.objects}
def comp(tt,names):
    for c in tt.get("m_Component",[]):
        cc=c.get("component",c) if isinstance(c,dict) else c
        co=byid.get(cc.get("m_PathID")) if isinstance(cc,dict) else None
        if co and co.type.name in names: return co
def pptr(v): return byid.get(v.get("m_PathID")) if isinstance(v,dict) else None
go=None
for p,o in cont.items():
    if str(p).lower()=="assets/coremasterbundle/items/supplies/wire/item.prefab": go=o
tr=comp(go.read_typetree(),("Transform",))
V,T,N,F=[],[],[],[]; base=0; palette=None
for ch in tr.read_typetree().get("m_Children",[]):
    ct=byid.get(ch.get("m_PathID"))
    cgo=byid.get(ct.read_typetree().get("m_GameObject",{}).get("m_PathID")) if ct else None
    if not cgo: continue
    gt=cgo.read_typetree()
    if not gt.get("m_Name","").startswith("Model_"): continue
    mf=comp(gt,("MeshFilter",)); mr=comp(gt,("MeshRenderer",))
    mesh=byid.get(mf.read_typetree().get("m_Mesh",{}).get("m_PathID")) if mf else None
    if not mesh: continue
    # local offset of this Model child (usually 0)
    mtr=comp(gt,("Transform",)).read_typetree().get("m_LocalPosition",{})
    ox,oy,oz=float(mtr.get("x",0)),float(mtr.get("y",0)),float(mtr.get("z",0))
    nv=0
    for line in mesh.read().export().splitlines():
        p=line.split()
        if not p: continue
        if p[0]=="v": V.append((-(float(p[1])+ox),float(p[2])+oy,-(float(p[3])+oz))); nv+=1
        elif p[0]=="vn": N.append((-float(p[1]),float(p[2]),-float(p[3])))
        elif p[0]=="vt": T.append((p[1],p[2]))
        elif p[0]=="f":
            idx=[]
            for tok in p[1:]:
                q=tok.split("/"); idx.append(tuple((int(x)+base if x else None) if i==0 else (int(x)+base if x else None) for i,x in enumerate(q)))
            F.append(list(reversed(idx)))
    base+=nv
    if palette is None and mr:
        mats=mr.read_typetree().get("m_Materials",[]); mo=pptr(mats[0]) if mats else None
        if mo:
            for pair in mo.read_typetree().get("m_SavedProperties",{}).get("m_TexEnvs",[]):
                nm,val=(pair[0],pair[1]) if isinstance(pair,(list,tuple)) else (pair.get("first"),pair.get("second"))
                if nm=="_MainTex" and isinstance(val,dict):
                    to=pptr(val.get("m_Texture",{}))
                    if to: to.read().image.convert("RGBA").save(os.path.join(OUT,"wire_hold_tex.png")); palette="wire_hold_tex.png"
L=["# wire supply item.prefab (Model_0+Model_1, held) -> Godot X+Z negated, winding reversed"]
L+=["v %.6f %.6f %.6f"%v for v in V]; L+=["vt %s %s"%t for t in T]; L+=["vn %.6f %.6f %.6f"%n for n in N]
for f in F:
    s="f"
    for tok in f:
        vi,ti,ni=(tok+ (None,None,None))[:3] if len(tok)<3 else tok
        s+=(" %d/%d/%d"%(vi,ti,ni)) if (ti and ni) else ((" %d//%d"%(vi,ni)) if ni else ((" %d/%d"%(vi,ti)) if ti else " %d"%vi))
    L.append(s)
open(os.path.join(OUT,"wire_hold.obj"),"w").write("\n".join(L)+"\n")
print(f"wire held: {len(V)} verts, {len(F)} tris, palette={palette}")
