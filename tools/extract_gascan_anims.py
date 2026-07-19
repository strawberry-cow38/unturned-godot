#!/usr/bin/env python3
"""extract_gascan_anims.py -- rip the gas can's TWO-HANDED Equip/Use FP anim clips from its animations.prefab
into consumable_anims.json as Fuel_Equip / Fuel_Use (the arms library loads every key in that file, armsOnly).
The gas can is held with BOTH hands (its Equip clip poses Left_Arm+Right_Arm) -- UseableFuel plays "Use" to pour.
Reuses extract_consumable_anims.py's Unity->Godot convert()."""
import UnityPy, json
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
CANIMS = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.json"
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}

def qinv(q): x, y, z, w = q; return [-x, -y, -z, w]
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz]
def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx = 2*(y*vz-z*vy); ty = 2*(z*vx-x*vz); tz = 2*(x*vy-y*vx)
    return [vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx)]
def xyzw(v):
    if hasattr(v, "x"): return float(v.x), float(v.y), float(v.z), float(getattr(v, "w", 0.0))
    return float(v["x"]), float(v["y"]), float(v["z"]), float(v.get("w", 0.0))
def keyframes(cur):
    curve = getattr(cur, "curve", None) or getattr(cur, "m_Curve", None)
    kfs = getattr(curve, "m_Curve", None) if curve is not None else None
    return kfs or []
def convert(cl):
    fps = float(getattr(cl, "m_SampleRate", 30.0) or 30.0)
    tracks = {}
    for cur in (getattr(cl, "m_RotationCurves", None) or []):
        bn = str(cur.path).split("/")[-1]
        keys = [[float(kf.time)] + [(-x), (-y), z, w] for kf in keyframes(cur) for (x, y, z, w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["rot"] = keys
    for cur in (getattr(cl, "m_PositionCurves", None) or []):
        bn = str(cur.path).split("/")[-1]
        keys = [[float(kf.time), x, y, -z] for kf in keyframes(cur) for (x, y, z, _w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["pos"] = keys
    sk = tracks.get("Skeleton")
    if sk and sk.get("rot"):
        K = qinv(sk["rot"][0][1:5])
        sk["rot"] = [[k[0]] + qmul(K, k[1:5]) for k in sk["rot"]]
        if sk.get("pos"): sk["pos"] = [[k[0]] + qrot(K, k[1:4]) for k in sk["pos"]]
    if sk and sk.get("pos"): sk["pos"] = [[k[0], 0.0, 0.0, 0.0] for k in sk["pos"]]
    length = 0.0
    for d in tracks.values():
        for arr in d.values():
            if arr: length = max(length, arr[-1][0])
    return {"fps": fps, "length": length, "tracks": tracks, "loop": False}

prefab = next((o for p, o in env.container.items()
               if p.lower().endswith("/gas/animations.prefab") and o.type.name == "GameObject"), None)
clips = {}
for c in prefab.read_typetree().get("m_Component", []):
    cc = c.get("component", c) if isinstance(c, dict) else c
    co = by_id.get(cc.get("m_PathID"))
    if co and co.type.name == "Animation":
        for a in co.read_typetree().get("m_Animations", []):
            clo = by_id.get(a.get("m_PathID"))
            if clo:
                clips[clo.read_typetree().get("m_Name")] = clo.read()

def rnd(o):
    if isinstance(o, float): return round(o, 5)
    if isinstance(o, list): return [rnd(x) for x in o]
    if isinstance(o, dict): return {k: rnd(v) for k, v in o.items()}
    return o

canims = json.load(open(CANIMS))
for src, dst in (("Equip", "Fuel_Equip"), ("Use", "Fuel_Use")):
    if src in clips:
        canims[dst] = rnd(convert(clips[src]))
        t = canims[dst]["tracks"]
        print(f"{dst}: len={canims[dst]['length']:.3f} bones={sorted(t.keys())}")
json.dump(canims, open(CANIMS, "w"), separators=(",", ":"))
print("wrote", CANIMS)
