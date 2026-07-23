#!/usr/bin/env python3
"""extract_lightbar.py -- the police lightbar HOUSING is baked into the body mesh (Model_0), not a separate part.
Extract the body's tris in the ZONE around the two siren lenses (Siren_0/Siren_1) -> police_lightbar.txt, at the
same real position as the ripped siren meshes so it seats them. Z-flip + winding-reverse like extract_vehicle_part.py.

Usage: python extract_lightbar.py <out.txt>
"""
import UnityPy, numpy as np, sys, os
_BUNDLE = os.environ.get("UG_MASTERBUNDLE") or next((p for p in (
    os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle"),
    r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle",
) if os.path.exists(p)), None)
env = UnityPy.load(_BUNDLE)
by_id = {o.path_id: o for o in env.objects}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                  [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])
    M = np.eye(4); M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]]); M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

name = sys.argv[1]; out = sys.argv[2]   # <vehicle-name> <out.txt> -- e.g. police / ambulance / firetruck
prefab = next(o for p, o in env.container.items() if p.lower().endswith(f"vehicles/{name}/vehicle.prefab") and o.type.name == "GameObject")
root_trt = comp_of(prefab.read_typetree(), ("Transform",)).read_typetree()
root_inv = np.linalg.inv(trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"]))

bodies = []; sirens = []
def walk(pid, parentM, parent_name):
    go = by_id.get(pid)
    if not go: return
    tt = go.read_typetree(); go_name = tt.get("m_Name", "")
    tr = comp_of(tt, ("Transform",))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    mc = comp_of(tt, ("MeshFilter", "SkinnedMeshRenderer"))
    if mc:
        mp = mc.read_typetree().get("m_Mesh", {}).get("m_PathID")
        if mp in by_id:
            mname = by_id[mp].read_typetree().get("m_Name")
            if mname == "Model_0" and parent_name == "Vehicle": bodies.append((by_id[mp], M))
            if mname in ("Siren_0", "Siren_1"): sirens.append((by_id[mp], M))
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M, go_name)

walk(prefab.path_id, root_inv, None)
if not bodies or not sirens:
    sys.exit("missing body(%d) or sirens(%d)" % (len(bodies), len(sirens)))
body_mo, body_M = max(bodies, key=lambda bm: len(bm[0].read().export().splitlines()))  # largest Model_0 = the body

def world_verts(mo, M):
    vs = []
    for line in mo.read().export().splitlines():
        p = line.split()
        if p and p[0] == "v":
            w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); vs.append((w[0], w[1], -w[2]))
    return vs

sv = [v for mo, M in sirens for v in world_verts(mo, M)]
sx = [v[0] for v in sv]; sy = [v[1] for v in sv]; sz = [v[2] for v in sv]
# zone = the two lenses' combined bbox, expanded (down in Y to catch the housing base under the lenses)
zx = (min(sx) - 0.10, max(sx) + 0.10); zy = (min(sy) - 0.30, max(sy) + 0.06); zz = (min(sz) - 0.12, max(sz) + 0.12)
print("siren bbox x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]" % (min(sx), max(sx), min(sy), max(sy), min(sz), max(sz)))
print("zone x%s y%s z%s" % (zx, zy, zz))

R = body_M[:3, :3]; Rn = np.linalg.inv(R).T
rev = np.linalg.det(np.diag([1.0, 1.0, -1.0]) @ R) < 0
V, N, T = [], [], []
raw_v, raw_vt, raw_vn, raw_f = [], [], [], []
for line in body_mo.read().export().splitlines():
    p = line.split()
    if not p: continue
    if p[0] == "v":
        w = body_M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); raw_v.append((w[0], w[1], -w[2]))
    elif p[0] == "vn":
        n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])]); ln = np.linalg.norm(n)
        raw_vn.append(tuple(n/ln*[1,1,-1]) if ln>0 else (n[0],n[1],-n[2]))
    elif p[0] == "vt":
        raw_vt.append((p[1], p[2]))
    elif p[0] == "f":
        idx = []
        for tok in p[1:]:
            q = tok.split("/")
            idx.append((int(q[0]), (int(q[1]) if len(q)>1 and q[1] else None), (int(q[2]) if len(q)>2 and q[2] else None)))
        raw_f.append(idx)

def inzone(vi):
    x, y, z = raw_v[vi-1]
    return zx[0] <= x <= zx[1] and zy[0] <= y <= zy[1] and zz[0] <= z <= zz[1]

remap = {}; OV, OT, ON, OF = [], [], [], []
def gv(vi):
    if vi not in remap:
        remap[vi] = len(OV) + 1; OV.append(raw_v[vi-1])
    return remap[vi]
kept = 0
for f in raw_f:
    if all(inzone(vi) for (vi, ti, ni) in f):
        kept += 1
        nf = []
        for (vi, ti, ni) in f:
            ov = gv(vi)
            ot = None
            if ti: OT.append(raw_vt[ti-1]); ot = len(OT)
            on = None
            if ni: ON.append(raw_vn[ni-1]); on = len(ON)
            nf.append((ov, ot, on))
        OF.append(list(reversed(nf)) if rev else nf)

L = ["g Model_0"]
L += ["v %.6f %.6f %.6f" % v for v in OV]
L += ["vt %s %s" % t for t in OT]
L += ["vn %.6f %.6f %.6f" % n for n in ON]
for f in OF:
    s = "f"
    for (vi, ti, ni) in f:
        if ti and ni: s += " %d/%d/%d" % (vi, ti, ni)
        elif ni: s += " %d//%d" % (vi, ni)
        elif ti: s += " %d/%d" % (vi, ti)
        else: s += " %d" % vi
    L.append(s)
open(out, "w").write("\n".join(L) + "\n")
if OV:
    xs=[v[0] for v in OV]; ys=[v[1] for v in OV]; zs=[v[2] for v in OV]
    print("lightbar: %d faces kept, %d verts, bbox x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]" % (kept, len(OV), min(xs),max(xs),min(ys),max(ys),min(zs),max(zs)))
else:
    print("lightbar: EMPTY (no faces in zone)")
