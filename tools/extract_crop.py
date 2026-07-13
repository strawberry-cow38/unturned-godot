#!/usr/bin/env python3
"""extract_crop.py -- rip a farm crop's growth-stage meshes from its Barricade prefab.

A crop (ItemFarmAsset : ItemBarricadeAsset) renders as a "Barricade" prefab with named
child meshes: Model_0 (soil base, always shown), Foliage_0 (growing) and Foliage_1 (grown)
toggled by InteractableFarm.SetModelGrown. This walks the prefab, bakes each mesh's world
TRS relative to the root (same as extract_attachment_mesh.py), but outputs ONE .txt PER
top-level child so the port can show base+Foliage_0 while growing, base+Foliage_1 when grown.

Same gun/world-space convention as the rest of the rip: Z negate + winding reverse (RH Y-up).

Usage: python extract_crop.py <prefab-subpath> <out-base>
  e.g. python extract_crop.py seed_carrot/barricade.prefab C:\claude-workspace\crop_carrot
  -> crop_carrot_Foliage_0.txt / crop_carrot_Foliage_1.txt / crop_carrot_Model_0.txt
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

groups = {}   # top-level child name -> list of (meshobj, M)

def walk(go_pid, parentM, group):
    go = by_id.get(go_pid)
    if not go:
        return
    tt = go.read_typetree()
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr:
        return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    if group is not None:
        for nm in ("MeshFilter", "SkinnedMeshRenderer"):
            mc = comp_of(tt, (nm,))
            if mc:
                mp = mc.read_typetree().get("m_Mesh", {}).get("m_PathID")
                if mp and mp in by_id:
                    groups.setdefault(group, []).append((by_id[mp], M))
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            cgo = ct.read_typetree().get("m_GameObject", {}).get("m_PathID")
            cname = by_id[cgo].read_typetree().get("m_Name", "?") if cgo in by_id else group
            walk(cgo, M, cname if group is None else group)

sub = sys.argv[1]
outbase = sys.argv[2]
prefab = next(o for p, o in env.container.items() if p.lower().endswith(sub) and o.type.name == "GameObject")
root_tr = comp_of(prefab.read_typetree(), ("Transform", "RectTransform"))
root_trt = root_tr.read_typetree()
root_local = trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"])
walk(prefab.path_id, np.linalg.inv(root_local), None)

def export(meshes, out):
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
                Vs.append((w[0], w[1], -w[2]))
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
                Fs.append(list(reversed(idx)))
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
    return len(Vs), len(Fs)

for name, meshes in groups.items():
    out = f"{outbase}_{name}.txt"
    nv, nf = export(meshes, out)
    print(f"{name}: {len(meshes)} mesh(es) -> {nv} verts {nf} tris -> {out}")
