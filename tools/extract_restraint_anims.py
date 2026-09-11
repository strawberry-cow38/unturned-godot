#!/usr/bin/env python3
"""extract_restraint_anims.py -- rip the FP "Equip"/"Use" clips for the handcuffs, the cable tie and the
handcuffs key into consumable_anims.json as Cuff_Equip / Cuff_Use / Key_Equip / Key_Use (the arms library
loads every key in that file, armsOnly -- the route extract_gascan_anims.py and the spraypaint both take).

Source: UseableArrestStart.equip() plays "Equip" and caches GetAnimationLength("Use"); arrest() plays "Use"
once per attempt, and the cuffs go on when that clip ENDS (isUseable), not when you click. Same shape on the
key (UseableArrestEnd).

The two Arrest_Starts are expected to share a pair (they are one animation in two items) and the key is its
own; the script COUNTS distinct clip path_ids and says so rather than assuming it, so a cable tie that turns
out to animate differently shows up instead of being silently replaced by the handcuffs'.

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
    m = re.search(r"items/(arrest_starts|arrest_ends)/([^/]+)/animations\.prefab$", str(path).lower())
    if m and obj.type.name == "GameObject":
        items[m.group(1) + "/" + m.group(2)] = item_clips(obj.read())
if not items:
    raise SystemExit("no arrest_starts|arrest_ends animations.prefab in the bundle -- path layout changed?")

eq_ids, use_ids = set(), set()
for nm, cl in sorted(items.items()):
    print(f"  {nm}: {sorted(cl.keys())}")
    if "Equip" in cl: eq_ids.add(cl["Equip"].object_reader.path_id)
    if "Use" in cl: use_ids.add(cl["Use"].object_reader.path_id)
print(f"items={len(items)} distinct Equip={len(eq_ids)} distinct Use={len(use_ids)}")

starts = {k: v for k, v in items.items() if k.startswith("arrest_starts/")}
ends = {k: v for k, v in items.items() if k.startswith("arrest_ends/")}
if not starts or not ends:
    raise SystemExit(f"expected BOTH halves; got starts={sorted(starts)} ends={sorted(ends)}")
# The two Arrest_Starts are one animation in two items, so a shared pair is the expectation -- COUNTED, not
# assumed. A cable tie that turns out to animate differently must show up here rather than be silently
# replaced by the handcuffs', which is the mistake the spraypaint check was written to avoid.
start_eq = {cl["Equip"].object_reader.path_id for cl in starts.values() if "Equip" in cl}
start_use = {cl["Use"].object_reader.path_id for cl in starts.values() if "Use" in cl}
if len(start_eq) > 1 or len(start_use) > 1:
    print("!! the handcuffs and the cable tie do NOT share one Equip/Use pair -- per-item clips needed here")

out = {}
def take(group, prefix):
    src = sorted(group.items())[0][1]
    if "Equip" in src: out[prefix + "_Equip"] = convert(src["Equip"])
    if "Use" in src: out[prefix + "_Use"] = convert(src["Use"])
take(starts, "Cuff")
take(ends, "Key")
if "Cuff_Use" not in out: raise SystemExit("no Use clip on the restraints -- that motion is what this is for")

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
