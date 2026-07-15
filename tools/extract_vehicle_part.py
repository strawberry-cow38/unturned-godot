#!/usr/bin/env python3
"""extract_vehicle_part.py -- pull detail part meshes (seats, headlights, taillights, steering wheel) out of a
vehicle prefab at their REAL position relative to the vehicle root (root-cancelled but translation KEPT, unlike
extract_vehicle_mesh.py which centers). Combines all meshes matching <mesh-name> and (optional) <node-filter>.
Z-flip + winding-reverse (conditional on the transform determinant), UVs kept as-is (palette shader samples direct).

Place the result at origin as a child of the vehicle -- it's already positioned.

Usage: python extract_vehicle_part.py <prefab-sub> <mesh-name> <out.txt> [node-filter]
  node-filter matches when the mesh's GameObject name == filter, starts with filter, or its parent's name == filter.
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

sub, mesh_name, out = sys.argv[1], sys.argv[2], sys.argv[3]
node_filter = sys.argv[4] if len(sys.argv) > 4 else None
prefab = next(o for p, o in env.container.items() if p.lower().endswith(sub) and o.type.name == "GameObject")
root_tr = comp_of(prefab.read_typetree(), ("Transform", "RectTransform"))
root_trt = root_tr.read_typetree()
root_inv = np.linalg.inv(trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"]))

parts = []
def walk(pid, parentM, parent_name):
    go = by_id.get(pid)
    if not go: return
    tt = go.read_typetree(); go_name = tt.get("m_Name", "")
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    mc = comp_of(tt, ("MeshFilter", "SkinnedMeshRenderer"))
    if mc:
        mp = mc.read_typetree().get("m_Mesh", {}).get("m_PathID")
        if mp in by_id and by_id[mp].read_typetree().get("m_Name") == mesh_name:
            ok = node_filter is None or go_name == node_filter or go_name.startswith(node_filter) or parent_name == node_filter
            if ok:
                parts.append((by_id[mp], M))
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M, go_name)

walk(prefab.path_id, root_inv, None)
if not parts:
    print("NO part for", mesh_name, node_filter); sys.exit(1)

V, N, T, F = [], [], [], []
for mo, M in parts:
    R = M[:3, :3]; Rn = np.linalg.inv(R).T
    rev = np.linalg.det(np.diag([1.0, 1.0, -1.0]) @ R) < 0
    vb, tb, nb = len(V), len(T), len(N)
    for line in mo.read().export().splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v":
            w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0])
            V.append((w[0], w[1], -w[2]))
        elif p[0] == "vn":
            n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])]); ln = np.linalg.norm(n)
            N.append(tuple(n / ln * [1, 1, -1]) if ln > 0 else (n[0], n[1], -n[2]))
        elif p[0] == "vt":
            T.append((p[1], p[2]))
        elif p[0] == "f":
            idx = []
            for tok in p[1:]:
                q = tok.split("/")
                idx.append((int(q[0]) + vb,
                            (int(q[1]) + tb) if len(q) > 1 and q[1] else None,
                            (int(q[2]) + nb) if len(q) > 2 and q[2] else None))
            F.append(list(reversed(idx)) if rev else idx)

L = ["g Model_0"]
L += ["v %.6f %.6f %.6f" % v for v in V]
L += ["vt %s %s" % t for t in T]
L += ["vn %.6f %.6f %.6f" % (n[0], n[1], n[2]) for n in N]
for f in F:
    s = "f"
    for (vi, ti, ni) in f:
        if ti and ni: s += " %d/%d/%d" % (vi, ti, ni)
        elif ni: s += " %d//%d" % (vi, ni)
        elif ti: s += " %d/%d" % (vi, ti)
        else: s += " %d" % vi
    L.append(s)
open(out, "w").write("\n".join(L) + "\n")
xs = [v[0] for v in V]; ys = [v[1] for v in V]; zs = [v[2] for v in V]
print("%s: %d part(s), %d verts, bbox x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]" % (mesh_name, len(parts), len(V), min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
