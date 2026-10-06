#!/usr/bin/env python3
"""extract_carjack_anims.py -- rip the carjack's FP "Equip"/"Use" clips out of its own
animations.prefab into consumable_anims.json as Jack_Equip / Jack_Use (the arms library loads every key in
that file, armsOnly -- the route extract_spraypaint_anims.py and extract_gascan_anims.py already use).

SOURCE (U3-SDK, UseableCarjack.cs -- read, not guessed):
    equip()    -> player.animator.play("Equip", true);  useTime = GetAnimationLength("Use")
    pull()     -> player.animator.play("Use", false);   plays ItemToolAsset.use;  AlertTool.alert(pos, 8)
    isJackable -> elapsed > useTime * 0.75   <-- THE LAUNCH LANDS AT 75%, not at the end
    isUseable  -> elapsed > useTime          <-- and the hand stays busy for the whole clip

⚠ WHY THIS SCRIPT EXISTS AT ALL. PlayerController used to build the carjack an EmptyHands viewmodel with a
comment claiming it had "no 1P carry model in the rip, same as the spraypaint and the gas can" -- and there
are extractors for BOTH of those sitting next to this file. The clips were never missing; they live in each
item's OWN animations.prefab rather than in rig.json, which is the exact trap extract_throwable_anims.py was
written for ("a search of rig.json's 569 clips found nothing and I wrongly concluded none had come through").
master 2026-10-05: "we have the gamefiles and source code. figure out the anim".

The container path is DISCOVERED rather than asserted: the carjack is a TOOL but the folder name is not
guaranteed, so this matches any */carjack*/animations.prefab and prints every carjack-ish container it saw
when that finds nothing, instead of failing with a path nobody can check.

Run on the box:  python tools/extract_carjack_anims.py"""
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

found = {}
for path, obj in cont.items():
    p = str(path).lower()
    if "carjack" in p and p.endswith("animations.prefab") and obj.type.name == "GameObject":
        found[str(path)] = item_clips(obj.read())
if not found:
    # Don't fail with an unverifiable path -- show what IS there under that name.
    near = sorted(str(p) for p in cont if "carjack" in str(p).lower())
    print("no carjack animations.prefab found. carjack-ish containers in the bundle:")
    for p in near[:40]: print("   ", p)
    raise SystemExit(f"({len(near)} carjack entries, none an animations.prefab)")

for p, cl in sorted(found.items()):
    print(f"  {p}: {sorted(cl.keys())}")

clips = sorted(found.items())[0][1]
out = {}
if "Equip" in clips: out["Jack_Equip"] = convert(clips["Equip"])
if "Use" in clips:   out["Jack_Use"]   = convert(clips["Use"])
if "Jack_Use" not in out:
    raise SystemExit(f"no Use clip on the carjack -- got {sorted(clips.keys())}; Use IS the crank, so there is nothing to ship without it")
# The Equip clip LOOPS in source (play("Equip", true)) -- it is the carry hold, not a one-shot.
if "Jack_Equip" in out: out["Jack_Equip"]["loop"] = True

def rnd(o):
    if isinstance(o, float): return round(o, 5)
    if isinstance(o, list): return [rnd(x) for x in o]
    if isinstance(o, dict): return {k: rnd(v) for k, v in o.items()}
    return o

anims = json.load(open(CANIMS))
for k, v in out.items(): anims[k] = rnd(v)
json.dump(anims, open(CANIMS, "w"), separators=(",", ":"))
for k, v in out.items():
    print(f"  {k}: {v['length']:.3f}s @ {v['fps']}fps loop={v['loop']} bones {sorted(v['tracks'].keys())}")
print(f"consumable_anims.json now {os.path.getsize(CANIMS)} bytes, {len(anims)} clips")
print(f"SOURCE SAYS: launch at 75% of Jack_Use = {out['Jack_Use']['length'] * 0.75:.3f}s, hand busy {out['Jack_Use']['length']:.3f}s")
