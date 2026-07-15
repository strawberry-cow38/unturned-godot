#!/usr/bin/env python3
"""extract_vehicle_mesh.py -- pull a named mesh out of a vehicle prefab, oriented as it sits
on the vehicle (bakes the mesh node's world ROTATION+SCALE, root-cancelled) but CENTERED at
origin (translation zeroed), Z-flipped + winding-reversed for Godot RH Y-up.

The body's Model_0 node carries a rotation (mesh authored in its own axes); we bake that so the
body faces the right way, and zero the translation so Godot's VehicleBody3D / VehicleWheel3D can
place it. When several meshes share m_Name (jeep body + seats + steering-wheel are all "Model_0")
we take the one with the most verts.

Usage: python extract_vehicle_mesh.py <prefab-subpath> <mesh-name> <out.txt>
"""
import UnityPy, numpy as np, sys

import os
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
prefab = next(o for p, o in env.container.items() if p.lower().endswith(sub) and o.type.name == "GameObject")
root_tr = comp_of(prefab.read_typetree(), ("Transform", "RectTransform"))
root_trt = root_tr.read_typetree()
root_inv = np.linalg.inv(trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"]))

found = []   # (mesh_obj, worldM, vcount)
def walk(pid, parentM):
    go = by_id.get(pid)
    if not go:
        return
    tt = go.read_typetree()
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr:
        return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    mf = comp_of(tt, ("MeshFilter", "SkinnedMeshRenderer"))
    if mf:
        mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID")
        if mp in by_id and by_id[mp].read_typetree().get("m_Name") == mesh_name:
            vc = by_id[mp].read_typetree().get("m_VertexData", {}).get("m_VertexCount", 0)
            found.append((by_id[mp], M, vc))
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)

walk(prefab.path_id, root_inv)
if not found:
    print("NO mesh named", mesh_name); sys.exit(1)
mo, M, vc = max(found, key=lambda t: t[2])
R = M[:3, :3]                       # world rotation+scale (root-cancelled); translation dropped -> centered
Rn = np.linalg.inv(R).T
# faces stay outward iff we reverse winding ONLY when the full position map (R then Z-negate) is a reflection.
# a node with negative scale is already a reflection, so blindly reversing would turn it inside-out.
rev = np.linalg.det(np.diag([1.0, 1.0, -1.0]) @ R) < 0
print(f"{mesh_name}: {len(found)} candidate(s), picked vcount={vc}, det={np.linalg.det(R):.3f}, reverse_winding={rev}")

txt = mo.read().export()
V, N, T, F = [], [], [], []
for line in txt.splitlines():
    p = line.split()
    if not p:
        continue
    if p[0] == "v":
        w = R @ np.array([float(p[1]), float(p[2]), float(p[3])])
        V.append((w[0], w[1], -w[2]))
    elif p[0] == "vn":
        n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])])
        ln = np.linalg.norm(n); n = n / ln if ln > 0 else n
        N.append((n[0], n[1], -n[2]))
    elif p[0] == "vt":
        T.append((p[1], p[2]))
    elif p[0] == "f":
        F.append(list(reversed(p[1:])) if rev else list(p[1:]))

L = ["g Model_0"]
L += ["v %.6f %.6f %.6f" % v for v in V]
L += ["vt %s %s" % t for t in T]
L += ["vn %.6f %.6f %.6f" % n for n in N]
L += ["f " + " ".join(f) for f in F]
open(out, "w").write("\n".join(L) + "\n")
xs = [v[0] for v in V]; ys = [v[1] for v in V]; zs = [v[2] for v in V]
print("wrote %s  verts:%d  bbox x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]" % (out, len(V), min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
