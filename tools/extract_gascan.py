#!/usr/bin/env python3
"""extract_gascan.py [OUTDIR] -- rip the Portable Gas Can 1st-person held mesh + albedo, BAKING the item
prefab's own Transform rotation into the verts. The gas can hangs its MeshFilter on the Item ROOT (no Model_0
child) AND the Item Transform carries a -90deg-about-X localRotation (quat -0.707,0,0,0.707) that orients the
raw mesh upright (raw mesh is short-in-Y / lying down; after the bake it's tall+thin = a jerry can). The generic
extract_consumable.py drops that rotation (root-mesh = identity assumption) -> the can renders flipped/wrong.
Bake happens in UNITY space, THEN the gun-viewmodel convention (negate X+Z + winding reverse)."""
import UnityPy, sys, os

env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
by_id = {o.path_id: o for o in env.objects}
OUTDIR = sys.argv[1] if len(sys.argv) > 1 else r"C:\claude-workspace\consumeout"
os.makedirs(OUTDIR, exist_ok=True)

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def pptr(v):
    return by_id.get(v.get("m_PathID")) if isinstance(v, dict) else None

def qrot(q, v):
    # rotate vec3 v by quaternion q=(x,y,z,w): v' = v + 2*w*(qv x v) + 2*(qv x (qv x v))
    qx, qy, qz, qw = q
    # t = 2 * cross(qv, v)
    tx = 2.0 * (qy * v[2] - qz * v[1])
    ty = 2.0 * (qz * v[0] - qx * v[2])
    tz = 2.0 * (qx * v[1] - qy * v[0])
    # v' = v + qw*t + cross(qv, t)
    rx = v[0] + qw * tx + (qy * tz - qz * ty)
    ry = v[1] + qw * ty + (qz * tx - qx * tz)
    rz = v[2] + qw * tz + (qx * ty - qy * tx)
    return (rx, ry, rz)

prefab = next((o for p, o in env.container.items()
               if p.lower().endswith("/gas/item.prefab") and o.type.name == "GameObject"), None)
if not prefab:
    print("NO gas/item.prefab"); sys.exit(1)
ptt = prefab.read_typetree()
tr = comp_of(ptt, ("Transform",))
r = tr.read_typetree().get("m_LocalRotation", {})
Q = (r.get("x", 0.0), r.get("y", 0.0), r.get("z", 0.0), r.get("w", 1.0))
print("Item localRotation quat:", Q)

mf = comp_of(ptt, ("MeshFilter",))
mesh = by_id.get(mf.read_typetree().get("m_Mesh", {}).get("m_PathID")) if mf else None
if not mesh:
    print("NO mesh on Item root"); sys.exit(1)
txt = mesh.read().export()
Vs, Ns, Ts, Fs = [], [], [], []
for line in txt.splitlines():
    p = line.split()
    if not p:
        continue
    if p[0] == "v":
        x, y, z = qrot(Q, (float(p[1]), float(p[2]), float(p[3])))   # BAKE the item Transform rotation (Unity space)
        Vs.append((-x, y, -z))                                       # then gun-viewmodel convention: negate X + Z
    elif p[0] == "vn":
        x, y, z = qrot(Q, (float(p[1]), float(p[2]), float(p[3])))
        Ns.append((-x, y, -z))
    elif p[0] == "vt":
        Ts.append((p[1], p[2]))
    elif p[0] == "f":
        idx = []
        for tok in p[1:]:
            q = tok.split("/")
            idx.append((int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)))
        Fs.append(list(reversed(idx)))                              # reverse winding (negate flips handedness)
L = ["# gas can 1P held mesh (item Transform rot BAKED, then X+Z negated + winding reversed)"]
L += ["v %.6f %.6f %.6f" % v for v in Vs]
L += ["vt %s %s" % t for t in Ts]
L += ["vn %.6f %.6f %.6f" % n for n in Ns]
for f in Fs:
    s = "f"
    for (vi, ti, ni) in f:
        s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
    L.append(s)
open(os.path.join(OUTDIR, "gascan.txt"), "w").write("\n".join(L) + "\n")

alb = "NONE"
mr = comp_of(ptt, ("MeshRenderer",))
if mr:
    mats = mr.read_typetree().get("m_Materials", [])
    mo = pptr(mats[0]) if mats else None
    if mo:
        for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
            nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
            if nm == "_MainTex" and isinstance(val, dict):
                to = pptr(val.get("m_Texture", {}))
                if to:
                    to.read().image.convert("RGBA").save(os.path.join(OUTDIR, "gascan_albedo.png"))
                    alb = "gascan_albedo.png"
# report baked extents
if Vs:
    xs = [v[0] for v in Vs]; ys = [v[1] for v in Vs]; zs = [v[2] for v in Vs]
    print("baked extents  X %.3f  Y %.3f  Z %.3f" % (max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs)))
print("gascan: %d verts, %d tris, albedo=%s" % (len(Vs), len(Fs), alb))
