#!/usr/bin/env python3
"""extract_spraypaint_anims.py -- rip the vehicle spraypaint's FP "Equip"/"Use" clips out of
items/tools/vehicle_spraypaint_*/animations.prefab into consumable_anims.json as Paint_Equip / Paint_Use
(the arms library loads every key in that file, armsOnly -- same route extract_gascan_anims.py uses).

Source: UseableVehiclePaint.equip() plays the item's own "Equip" and caches GetAnimationLength("Use");
replace() plays "Use" once per spray. There is no separate spray clip -- "Use" IS the spray, and the paint
lands at 85% of it (isReplaceable), the same shape as the throwable's 60% release.

All 32 cans are one item in 32 colours, so the clips are expected to be SHARED (one path_id each); the
script asserts that rather than assuming it, and prints the per-folder clip names so a colour that ships
its own animation would show up instead of being silently averaged away.

Reuses extract_throwable_anims.py's Unity->Godot convert(). Run on the box:
    python tools/extract_spraypaint_anims.py"""
import UnityPy, json, os, re
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
CANIMS = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.json"
env = UnityPy.load(MB)
cont = dict(env.container)

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

def item_clips(go):
    out = {}
    for entry in (getattr(go, "m_Component", None) or []):
        pptr = getattr(entry, "component", None)
        if pptr is None: continue
        comp = pptr.read()
        tn = comp.object_reader.type.name if hasattr(comp, "object_reader") else ""
        if "Animation" not in tn: continue
        for cp in (getattr(comp, "m_Animations", None) or []):
            cl = cp.read(); out[cl.m_Name] = cl
    return out

items = {}
for path, obj in cont.items():
    m = re.search(r"items/tools/(vehicle_spraypaint_[^/]+)/animations\.prefab$", str(path).lower())
    if m and obj.type.name == "GameObject":
        items[m.group(1)] = item_clips(obj.read())
if not items:
    raise SystemExit("no vehicle_spraypaint_*/animations.prefab in the bundle -- path layout changed?")

eq_ids, use_ids = set(), set()
for nm, cl in sorted(items.items()):
    print(f"  {nm}: {sorted(cl.keys())}")
    if "Equip" in cl: eq_ids.add(cl["Equip"].object_reader.path_id)
    if "Use" in cl: use_ids.add(cl["Use"].object_reader.path_id)
print(f"cans={len(items)} distinct Equip={len(eq_ids)} distinct Use={len(use_ids)}")
if len(eq_ids) != 1 or len(use_ids) != 1:
    # NOT fatal, but it means one colour animates differently and a single shared pair is the wrong model.
    print("!! the 32 cans do NOT share one Equip/Use pair -- per-item clips needed, do not ship this as shared")

first = sorted(items.items())[0][1]
out = {}
if "Equip" in first: out["Paint_Equip"] = convert(first["Equip"])
if "Use" in first: out["Paint_Use"] = convert(first["Use"])
if "Paint_Use" not in out: raise SystemExit("no Use clip -- the spray animation is what this is for")

def rnd(o):
    if isinstance(o, float): return round(o, 5)
    if isinstance(o, list): return [rnd(x) for x in o]
    if isinstance(o, dict): return {k: rnd(v) for k, v in o.items()}
    return o

anims = json.load(open(CANIMS))
for k, v in out.items(): anims[k] = rnd(v)
json.dump(anims, open(CANIMS, "w"), separators=(",", ":"))
for k, v in out.items():
    print(f"  {k}: {v['length']:.3f}s @ {v['fps']}fps, bones {sorted(v['tracks'].keys())}")
print(f"consumable_anims.json now {os.path.getsize(CANIMS)} bytes, {len(anims)} clips")
