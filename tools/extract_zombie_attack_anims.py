import UnityPy, json, os, glob
RIG = r"C:\claude-workspace\unturned-godot\game\content\rig.json"
base = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Unturned_Data"
files = [os.path.join(base, "resources.assets")] + sorted(glob.glob(os.path.join(base, "sharedassets*.assets")))

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
    # KEEP the Skeleton pos -- this is the 3rd-person body (not the FP viewmodel).
    length = 0.0
    for d in tracks.values():
        for arr in d.values():
            if arr: length = max(length, arr[-1][0])
    return {"fps": fps, "length": length, "tracks": tracks, "loop": False}

want = set([f"Attack_{i}" for i in range(3, 9)] + [f"Startle_{i}" for i in range(2, 7)])
found = {}
for ra in files:
    if found.keys() >= want: break
    try: env = UnityPy.load(ra)
    except Exception: continue
    for obj in env.objects:
        if obj.type.name != "AnimationClip": continue
        cl = obj.read()
        nm = str(getattr(cl, "m_Name", ""))
        if nm in want and nm not in found:
            found[nm] = convert(cl)
            c = found[nm]
            print(f"{nm}: len={c['length']:.3f} fps={c['fps']:.0f} tracks={len(c['tracks'])} ({os.path.basename(ra)})")
print("FOUND:", sorted(found))
print("MISSING:", sorted(want - set(found)))
rig = json.load(open(RIG))
for nm, c in found.items(): rig["anims"][nm] = c
json.dump(rig, open(RIG, "w"))
print("rig Attack/Startle now:", sorted([k for k in rig["anims"] if 'Attack' in k or 'Startle' in k]))
