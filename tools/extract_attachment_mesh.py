#!/usr/bin/env python3
"""extract_attachment_mesh.py -- transform-aware prefab mesh extractor.

Unlike extract_bundle_mesh.py (which grabs raw mesh verts and ignores the prefab
hierarchy), this walks the whole prefab tree, accumulates each mesh's world TRS
(position * rotation * scale) relative to the prefab root, and BAKES it into the
verts. Needed for attachments whose parts sit under scaled/rotated child transforms
(e.g. the red dot's reticle disc authored at radius 1 then scaled down in a child).

Then applies the gun-space placement: Z negate + hook offset + winding reverse, same
convention as the rest of the Unturned->Godot rip (RH Y-up, V-down).

Usage: python extract_attachment_mesh.py <prefab-subpath> <ox> <oy> <oz> <out.txt>
"""
import UnityPy, numpy as np, sys

bundle = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(bundle)
by_id = {o.path_id: o for o in env.objects}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co and co.type.name in names:
            return co
    return None

def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w),   2*(x*z+y*w)],
                  [2*(x*y+z*w),   1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w),   2*(y*z+x*w),   1-2*(x*x+y*y)]])
    M = np.eye(4)
    M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]])
    M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

meshes = []
def walk(go_pid, parentM):
    go = by_id.get(go_pid)
    if not go:
        return
    tt = go.read_typetree()
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr:
        return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    for nm in ("MeshFilter", "SkinnedMeshRenderer"):
        mc = comp_of(tt, (nm,))
        if mc:
            mp = mc.read_typetree().get("m_Mesh", {}).get("m_PathID")
            if mp and mp in by_id:
                meshes.append((by_id[mp], M))
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)

sub = sys.argv[1]
ox, oy, oz = float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
out = sys.argv[5]
prefab = next(o for p, o in env.container.items() if p.lower().endswith(sub) and o.type.name == "GameObject")
# the prefab root transform sits at a scene position; cancel it so meshes come out root-relative
root_tr = comp_of(prefab.read_typetree(), ("Transform", "RectTransform"))
root_trt = root_tr.read_typetree()
root_local = trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"])
walk(prefab.path_id, np.linalg.inv(root_local))

Vs, Ns, Ts, Fs = [], [], [], []
for mo, M in meshes:
    txt = mo.read().export()
    Rn = np.linalg.inv(M[:3, :3]).T
    vb, tb, nb = len(Vs), len(Ts), len(Ns)
    for line in txt.splitlines():
        p = line.split()
        if not p:
            continue
        if p[0] == "v":
            w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0])
            Vs.append((w[0] + ox, w[1] + oy, -w[2] + oz))
        elif p[0] == "vn":
            n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])])
            ln = np.linalg.norm(n)
            if ln > 0:
                n = n / ln
            Ns.append((n[0], n[1], -n[2]))
        elif p[0] == "vt":
            Ts.append((p[1], p[2]))
        elif p[0] == "f":
            idx = []
            for tok in p[1:]:
                q = tok.split("/")
                vi = int(q[0]) + vb
                ti = (int(q[1]) + tb) if len(q) > 1 and q[1] else None
                ni = (int(q[2]) + nb) if len(q) > 2 and q[2] else None
                idx.append((vi, ti, ni))
            Fs.append(list(reversed(idx)))   # reverse winding to compensate the Z flip

L = ["g Model_0"]
L += ["v %.6f %.6f %.6f" % v for v in Vs]
L += ["vt %s %s" % t for t in Ts]
L += ["vn %.6f %.6f %.6f" % n for n in Ns]
for f in Fs:
    s = "f"
    for (vi, ti, ni) in f:
        if ti and ni:   s += " %d/%d/%d" % (vi, ti, ni)
        elif ni:        s += " %d//%d" % (vi, ni)
        elif ti:        s += " %d/%d" % (vi, ti)
        else:           s += " %d" % vi
    L.append(s)
open(out, "w").write("\n".join(L) + "\n")

print("meshes:", len(meshes), "verts:", len(Vs), "tris:", len(Fs))
if Vs:
    xs = [v[0] for v in Vs]; ys = [v[1] for v in Vs]; zs = [v[2] for v in Vs]
    print("bbox x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]" % (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
