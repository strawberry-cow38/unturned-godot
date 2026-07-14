import UnityPy, json, os
# Rip EVERY melee weapon's own 1st-person animation set from core.masterbundle into rig.json, one clip per
# label (same per-item convention as the guns: "Eaglefire_Reload" -> here "Sledgehammer_Weak", "Blowtorch_Start_Swing").
# Each items/melee/<name>/animations.prefab holds an Animation component whose m_Animations are the clips:
#   normal melees: Equip / Weak / Strong / Inspect
#   REPEATED tools (blowtorch, chainsaw, jackhammer, xmas_saw): Equip / Start_Swing / Stop_Swing / Inspect
# The generic Melee_Weak/Strong/Equip (ripped from knife_military) stay as the fallback the Viewmodel uses when a
# per-melee clip is missing. Quat/pos conversion is byte-identical to the old single-knife extractor.
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
RIG = r"C:\claude-workspace\unturned-godot\game\content\rig.json"
env = UnityPy.load(MB)
cont = dict(env.container)
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
    fps=float(getattr(cl,"m_SampleRate",30.0) or 30.0)
    tracks={}
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
        K=qinv(sk["rot"][0][1:5])
        sk["rot"]=[[k[0]]+qmul(K,k[1:5]) for k in sk["rot"]]
        if sk.get("pos"): sk["pos"]=[[k[0]]+qrot(K,k[1:4]) for k in sk["pos"]]
    if sk and sk.get("pos"): sk["pos"]=[[k[0],0.0,0.0,0.0] for k in sk["pos"]]
    length=0.0
    for d in tracks.values():
        for arr in d.values():
            if arr: length=max(length,arr[-1][0])
    return {"fps":fps,"length":length,"tracks":tracks,"loop":False}

# ONE container pass: bucket every melee's clips by weapon name
by_name={}
for path,obj in cont.items():
    p=str(path).lower()
    if "items/melee/" in p and "animations.prefab" in p and obj.type.name=="GameObject":
        name=p.split("items/melee/")[1].split("/")[0]
        go=obj.read()
        for entry in (getattr(go,"m_Component",None) or []):
            pptr=getattr(entry,"component",None)
            if pptr is None: continue
            comp=pptr.read()
            tn=comp.object_reader.type.name if hasattr(comp,"object_reader") else ""
            if "Animation" not in tn: continue
            d=by_name.setdefault(name,{})
            for cp in (getattr(comp,"m_Animations",None) or []):
                cl=cp.read()
                d[cl.m_Name]=cl

WANT=["Equip","Weak","Strong","Start_Swing","Stop_Swing","Inspect"]
rig=json.load(open(RIG))
added=0
# keep the generic knife-based fallbacks in sync too (Melee_Weak/Strong/Equip)
if "knife_military" in by_name:
    for cn,label in (("Weak","Melee_Weak"),("Strong","Melee_Strong"),("Equip","Melee_Equip")):
        if cn in by_name["knife_military"]: rig["anims"][label]=convert(by_name["knife_military"][cn])
for name in sorted(by_name):
    cap=name[0].upper()+name[1:]
    have=[]
    for cn in WANT:
        if cn not in by_name[name]: continue
        rig["anims"][f"{cap}_{cn}"]=convert(by_name[name][cn])
        have.append(cn); added+=1
    print(f"{name:20s} {cap:20s} -> {have}")
json.dump(rig, open(RIG,"w"))
print(f"added {added} per-melee clips across {len(by_name)} weapons; rig.json bytes:", os.path.getsize(RIG))
