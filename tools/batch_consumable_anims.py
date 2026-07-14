#!/usr/bin/env python3
"""Extract the DISTINCT consumable Equip/Use FP anim clips (36 each, shared archetypes) into rig.json, and write
content/consumable_anims.tsv (mesh<TAB>equipClip<TAB>useClip<TAB>useLen). Source: UseableConsumeable plays each
item's own "Equip"/"Use" (useTime = Use clip length). Reuses extract_consumable_anims.py's Unity->Godot convert()."""
import UnityPy, json, os, re
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
CANIMS = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.json"
TSV = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.tsv"
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

# gather each item's Equip/Use clip objects (keyed by clip path_id for dedupe)
items = {}
for path, obj in cont.items():
    m = re.search(r"items/(food|medical|water|refills)/([^/]+)/animations\.prefab$", str(path).lower())
    if m and obj.type.name == "GameObject":
        items[m.group(2)] = item_clips(obj.read())

canims = {}
use_idx = {}; eq_idx = {}; rows = []
def keyfor(cl, table, prefix):
    pid = cl.object_reader.path_id
    if pid not in table:
        idx = len(table); table[pid] = (idx, convert(cl))
    return f"{prefix}{table[pid][0]}"
for nm, cl in sorted(items.items()):
    ek = keyfor(cl["Equip"], eq_idx, "CE_") if "Equip" in cl else ""
    uk = keyfor(cl["Use"], use_idx, "CU_") if "Use" in cl else ""
    ulen = use_idx[cl["Use"].object_reader.path_id][1]["length"] if "Use" in cl else 0.0
    rows.append((nm, ek, uk, round(ulen, 3)))
for pid, (idx, data) in use_idx.items(): canims[f"CU_{idx}"] = data
for pid, (idx, data) in eq_idx.items(): canims[f"CE_{idx}"] = data
def rnd(o):
    if isinstance(o, float): return round(o, 5)
    if isinstance(o, list): return [rnd(x) for x in o]
    if isinstance(o, dict): return {k: rnd(v) for k, v in o.items()}
    return o
json.dump(rnd(canims), open(CANIMS, "w"), separators=(",", ":"))
with open(TSV, "w") as f:
    for nm, ek, uk, ul in rows: f.write(f"{nm}\t{ek}\t{uk}\t{ul}\n")
print(f"distinct Use={len(use_idx)} Equip={len(eq_idx)} | {len(rows)} items mapped | consumable_anims.json bytes={os.path.getsize(CANIMS)}")
print("sample:", rows[:3], "| e.g. bottled_water:", next((r for r in rows if r[0]=='bottled_water'), None), "| medkit:", next((r for r in rows if r[0]=='medkit'), None))
