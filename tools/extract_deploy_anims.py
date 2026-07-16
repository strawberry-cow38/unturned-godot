#!/usr/bin/env python3
# Extract generator_small's Equip/Use arms clips -> rig.json as Deploy_Equip / Deploy_Use.
# Same Unity->Godot conversion as extract_consumable_anims.py.
import UnityPy, json, os
MB = "/home/ec2-user/unturned-bundles/Bundles/core.masterbundle"
RIG = "/home/ec2-user/projects/unturned-godot/game/content/rig.json"
PREFAB = "assets/coremasterbundle/items/barricades/generator_small/animations.prefab"
env = UnityPy.load(MB); cont = dict(env.container)
def qinv(q): x,y,z,w=q; return [-x,-y,-z,w]
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return [aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz]
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return [vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx)]
def xyzw(v):
    if hasattr(v,"x"): return float(v.x),float(v.y),float(v.z),float(getattr(v,"w",0.0))
    return float(v["x"]),float(v["y"]),float(v["z"]),float(v.get("w",0.0))
def keyframes(cur):
    curve=getattr(cur,"curve",None) or getattr(cur,"m_Curve",None)
    kfs=getattr(curve,"m_Curve",None) if curve is not None else None
    return kfs or []
def convert(cl):
    fps=float(getattr(cl,"m_SampleRate",30.0) or 30.0); tracks={}
    for cur in (getattr(cl,"m_RotationCurves",None) or []):
        bn=str(cur.path).split("/")[-1]
        keys=[[float(kf.time)]+[(-x),(-y),z,w] for kf in keyframes(cur) for (x,y,z,w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn,{})["rot"]=keys
    for cur in (getattr(cl,"m_PositionCurves",None) or []):
        bn=str(cur.path).split("/")[-1]
        keys=[[float(kf.time),x,y,-z] for kf in keyframes(cur) for (x,y,z,_w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn,{})["pos"]=keys
    sk=tracks.get("Skeleton")
    if sk and sk.get("rot"):
        K=qinv(sk["rot"][0][1:5]); sk["rot"]=[[k[0]]+qmul(K,k[1:5]) for k in sk["rot"]]
        if sk.get("pos"): sk["pos"]=[[k[0]]+qrot(K,k[1:4]) for k in sk["pos"]]
    if sk and sk.get("pos"): sk["pos"]=[[k[0],0.0,0.0,0.0] for k in sk["pos"]]
    length=0.0
    for d in tracks.values():
        for arr in d.values():
            if arr: length=max(length,arr[-1][0])
    return {"fps":fps,"length":length,"tracks":tracks,"loop":False}
# read the clips
out={}
go=cont[PREFAB].read()
for entry in (getattr(go,"m_Component",None) or []):
    pptr=getattr(entry,"component",None)
    if pptr is None: continue
    comp=pptr.read(); tn=comp.object_reader.type.name if hasattr(comp,"object_reader") else ""
    if "Animation" not in tn: continue
    for cp in (getattr(comp,"m_Animations",None) or []):
        cl=cp.read(); out[cl.m_Name]=cl
rig=json.load(open(RIG)); added=[]
for src,dst in [("Equip","Deploy_Equip"),("Use","Deploy_Use")]:
    if src in out:
        c=convert(out[src]); rig["anims"][dst]=c; added.append(f"{dst}(len={c['length']:.2f},tracks={len(c['tracks'])})")
json.dump(rig,open(RIG,"w"))
print("clips present:",list(out.keys()))
print("added:",added)
print("rig.json bytes:",os.path.getsize(RIG))
