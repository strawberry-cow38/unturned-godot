import UnityPy, json, os
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
RIG = r"C:\claude-workspace\unturned-godot\game\content\rig.json"
env = UnityPy.load(MB)
cont = dict(env.container)

def qinv(q): x, y, z, w = q; return [-x, -y, -z, w]
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz]
def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx = 2*(y*vz-z*vy); ty = 2*(z*vx-x*vz); tz = 2*(x*vy-y*vx)
    return [vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx)]

def find_clip(gun, clipname):
    for path, obj in cont.items():
        if f"items/guns/{gun}/animations.prefab" in str(path).lower() and obj.type.name == "GameObject":
            go = obj.read()
            for entry in (getattr(go, "m_Component", None) or []):
                pptr = getattr(entry, "component", None)
                if pptr is None: continue
                comp = pptr.read()
                tn = comp.object_reader.type.name if hasattr(comp, "object_reader") else ""
                if "Animation" not in tn: continue
                for cp in (getattr(comp, "m_Animations", None) or []):
                    cl = cp.read()
                    if cl.m_Name == clipname:
                        return cl
    return None

def xyzw(v):
    if hasattr(v, "x"):
        return float(v.x), float(v.y), float(v.z), float(getattr(v, "w", 0.0))
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
        if keys:
            tracks.setdefault(bn, {})["rot"] = keys
    for cur in (getattr(cl, "m_PositionCurves", None) or []):
        bn = str(cur.path).split("/")[-1]
        keys = [[float(kf.time), x, y, -z] for kf in keyframes(cur) for (x, y, z, _w) in [xyzw(kf.value)]]
        if keys:
            tracks.setdefault(bn, {})["pos"] = keys
    sk = tracks.get("Skeleton")
    if sk and sk.get("rot"):
        K = qinv(sk["rot"][0][1:5])
        sk["rot"] = [[k[0]] + qmul(K, k[1:5]) for k in sk["rot"]]
        if sk.get("pos"):
            sk["pos"] = [[k[0]] + qrot(K, k[1:4]) for k in sk["pos"]]
    # First-person: ignore the clip's body/root TRANSLATION (that's 3rd-person body motion); the arm bones carry
    # the inspect gesture. Zero the Skeleton position so only the arms animate (same as the reload extractor).
    if sk and sk.get("pos"):
        sk["pos"] = [[k[0], 0.0, 0.0, 0.0] for k in sk["pos"]]
    length = 0.0
    for d in tracks.values():
        for arr in d.values():
            if arr:
                length = max(length, arr[-1][0])
    return {"fps": fps, "length": length, "tracks": tracks, "loop": False}

rig = json.load(open(RIG))
for gun, label in (("eaglefire", "Eaglefire_Inspect"), ("maplestrike", "Maplestrike_Inspect"), ("masterkey", "Masterkey_Inspect")):
    cl = find_clip(gun, "Inspect")
    if not cl:
        print("NO Inspect for", gun); continue
    c = convert(cl)
    rig["anims"][label] = c
    nt = len(c["tracks"]); nr = sum(len(t.get("rot", [])) for t in c["tracks"].values())
    print(f"{label}: len={c['length']:.3f}s fps={c['fps']:.0f} tracks={nt} rotkeys={nr} bones={sorted(c['tracks'])[:6]}")
json.dump(rig, open(RIG, "w"))
print("rig.json updated bytes:", os.path.getsize(RIG))
