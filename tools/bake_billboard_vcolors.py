#!/usr/bin/env python3
"""Bake each billboard sub-mesh's palette colour into per-vertex colours so the multi-material ad sign (banana/text
geometry uses a colored palette, frame uses a gray one) keeps its real colours over the merged mesh -- instead of every
sub-mesh sharing one palette. De-indexes (one vertex per face-corner) so each corner samples its own palette cell.
Output: content/objects/<Billboard_N>.obj (v x y z r g b) + a white 1x1 <Billboard_N>_tex.png."""
import UnityPy, os, re, numpy as np
from PIL import Image
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\objects"
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co: yield co
def comp_of(tt, names):
    for co in comps(tt):
        if co.type.name in names: return co
    return None
def trs(pos, q, s):
    x,y,z,w = q["x"],q["y"],q["z"],q["w"]
    R = np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],[2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],[2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
    M=np.eye(4); M[:3,:3]=R@np.diag([s["x"],s["y"],s["z"]]); M[:3,3]=[pos["x"],pos["y"],pos["z"]]; return M
SKIP={"dead","ragdoll","effect","nav","block","trap"}
def find_lodgroup(go,depth=0):
    if not go or depth>10: return None
    tt=go.read_typetree(); kids=[]
    for co in comps(tt):
        if co.type.name=="LODGroup": return co
        if co.type.name=="Transform": kids=co.read_typetree().get("m_Children",[])
    for ch in kids:
        ct=by_id.get(ch.get("m_PathID"))
        if ct:
            r=find_lodgroup(by_id.get(ct.read_typetree().get("m_GameObject",{}).get("m_PathID")),depth+1)
            if r: return r
    return None
def walk(go_pid,parentM,gomap):
    go=by_id.get(go_pid)
    if not go: return
    tt=go.read_typetree()
    if (tt.get("m_Name","") or "").lower() in SKIP: return
    tr=comp_of(tt,("Transform","RectTransform"))
    if not tr: return
    trt=tr.read_typetree()
    M=parentM@trs(trt["m_LocalPosition"],trt["m_LocalRotation"],trt["m_LocalScale"])
    mf=comp_of(tt,("MeshFilter",))
    mp=mf.read_typetree().get("m_Mesh",{}).get("m_PathID") if mf else None
    gomap[go_pid]=(M,mp)
    for ch in trt.get("m_Children",[]):
        ct=by_id.get(ch.get("m_PathID"))
        if ct: walk(ct.read_typetree().get("m_GameObject",{}).get("m_PathID"),M,gomap)
def lod0_gos(prefab,gomap):
    lg=find_lodgroup(prefab)
    if lg:
        lods=lg.read_typetree().get("m_LODs",[])
        if lods:
            gos=[]
            for r in lods[0].get("renderers",lods[0].get("_renderers",[])):
                rp=(r.get("renderer") or {}).get("m_PathID"); rc=by_id.get(rp)
                if rc:
                    gp=rc.read_typetree().get("m_GameObject",{}).get("m_PathID")
                    if gp in gomap: gos.append(gp)
            return gos
    return [g for g,(M,mp) in gomap.items() if mp]
def pptr(v): return by_id.get(v.get("m_PathID")) if isinstance(v,dict) else None
_pal={}
def palette_of(go_pid):
    tt=by_id.get(go_pid).read_typetree(); mr=comp_of(tt,("MeshRenderer",))
    if not mr: return None
    mats=mr.read_typetree().get("m_Materials",[]); mo=pptr(mats[0]) if mats else None
    if not mo: return None
    for pr in mo.read_typetree().get("m_SavedProperties",{}).get("m_TexEnvs",[]):
        fn=pr[0] if isinstance(pr,(list,tuple)) else pr.get('first'); val=pr[1] if isinstance(pr,(list,tuple)) else pr.get('second')
        if fn=="_MainTex":
            t=pptr(val.get("m_Texture",{}))
            if t and t.path_id in _pal: return _pal[t.path_id]
            if t:
                try: im=t.read().image.convert("RGB")
                except Exception: im=None
                _pal[t.path_id]=im; return im
    return None
def samp(im,u,v):
    if im is None: return (0.78,0.72,0.62)
    w,h=im.size; px=min(w-1,max(0,int(u*w))); py=min(h-1,max(0,int((1-v)*h)))
    r,g,b=im.getpixel((px,py))[:3]; return (r/255.0,g/255.0,b/255.0)
def write(name,Vs,Cs,Ns,Fs):
    L=["# %s billboard (per-vertex palette baked)"%name]
    for (v,c) in zip(Vs,Cs): L.append("v %.6f %.6f %.6f %.4f %.4f %.4f"%(v[0],v[1],v[2],c[0],c[1],c[2]))
    for n in Ns: L.append("vn %.6f %.6f %.6f"%(n[0],n[1],n[2]))
    for f in Fs: L.append("f "+" ".join("%d//%d"%(a,a) for a in f))
    open(os.path.join(OUT,name+".obj"),"w").write("\n".join(L)+"\n")
    Image.new("RGB",(1,1),(255,255,255)).save(os.path.join(OUT,name+"_tex.png"))
done=0
for path,prefab in list(env.container.items()):
    pl=str(path).lower(); m=re.search(r"objects/large/utilities/(billboard_\d+)/object\.prefab$",pl)
    if not (m and prefab.type.name=="GameObject"): continue
    name="Billboard_"+m.group(1).split("_")[1]
    rt=comp_of(prefab.read_typetree(),("Transform","RectTransform")).read_typetree()
    gomap={}; walk(prefab.path_id,np.linalg.inv(trs(rt["m_LocalPosition"],rt["m_LocalRotation"],rt["m_LocalScale"])),gomap)
    Vs,Cs,Ns,Fs=[],[],[],[]
    for gp in lod0_gos(prefab,gomap):
        M,mp=gomap[gp]; M=M.copy(); M[0,3]=-M[0,3]
        if not mp or mp not in by_id: continue
        if "dead" in by_id[mp].read_typetree().get("m_Name","").lower(): continue
        pal=palette_of(gp)
        V,VT,VN=[],[],[]
        for line in by_id[mp].read().export().splitlines():
            p=line.split()
            if not p: continue
            if p[0]=="v": V.append((float(p[1]),float(p[2]),float(p[3])))
            elif p[0]=="vt": VT.append((float(p[1]),float(p[2])))
            elif p[0]=="vn": VN.append((float(p[1]),float(p[2]),float(p[3])))
            elif p[0]=="f":
                corners=[]
                for tok in p[1:]:
                    q=tok.split("/"); vi=int(q[0])-1; ti=(int(q[1])-1) if len(q)>1 and q[1] else -1; ni=(int(q[2])-1) if len(q)>2 and q[2] else -1
                    corners.append((vi,ti,ni))
                for i in range(1,len(corners)-1):
                    for (vi,ti,ni) in (corners[0],corners[i+1],corners[i]):   # reverse winding to match ObjMesh
                        wv=M@np.array([V[vi][0],V[vi][1],V[vi][2],1.0]); Vs.append((wv[0],wv[1],wv[2]))
                        Cs.append(samp(pal,VT[ti][0],VT[ti][1]) if 0<=ti<len(VT) else (0.8,0.8,0.8))
                        Rn=np.linalg.inv(M[:3,:3]).T
                        if 0<=ni<len(VN): nn=Rn@np.array(VN[ni]); nl=np.linalg.norm(nn); Ns.append(tuple(nn/nl if nl>0 else nn))
                        else: Ns.append((0,1,0))
                        Fs.append(len(Vs))
    if Vs: write(name,Vs,Cs,Ns,[Fs[i:i+3] for i in range(0,len(Fs),3)]); done+=1
print(f"baked {done} billboards with per-vertex palette colours")
