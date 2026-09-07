#!/usr/bin/env python3
"""extract_mags.py -- every gun's MAGAZINE: the mounted model + the hook it mounts on.

Two outputs, one bundle load (opening core.masterbundle is the slow part):
  content/mag_<folder>.txt   the magazine.prefab model, transform-baked, port frame -- one per distinct magazine,
                             not per gun, since guns share magazines (Military_30 serves the eaglefire and the
                             maplestrike, Shells_8 three shotguns).
  content/guns_maghook.tsv   <gun>\t<x,y,z>  the Magazine child hook off each gun's item.prefab, port frame.

Conventions copied from extract_attachment_mesh.py / extract_sightview_hooks.py rather than reinvented: UnityPy's
OBJ export has ALREADY flipped X, so only Z is negated here, and the winding is reversed to match.
"""
import UnityPy, numpy as np, os, re, sys

MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
CONTENT = r"C:\claude-workspace\unturned-godot\game\content"
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont = env.container

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
    M = np.eye(4); M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]]); M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

def find_prefab(sub):
    for p, o in cont.items():
        if p.lower().endswith(sub) and o.type.name == "GameObject":
            return o
    return None

def extract_mesh(prefab, out_path):
    meshes = []
    def walk(go_pid, parentM):
        go = by_id.get(go_pid)
        if not go: return
        tt = go.read_typetree()
        tr = comp_of(tt, ("Transform", "RectTransform"))
        if not tr: return
        trt = tr.read_typetree()
        M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
        for nm in ("MeshFilter", "SkinnedMeshRenderer"):
            mc = comp_of(tt, (nm,))
            if mc:
                mp = mc.read_typetree().get("m_Mesh", {}).get("m_PathID")
                if mp and mp in by_id: meshes.append((by_id[mp], M))
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)
    root_tr = comp_of(prefab.read_typetree(), ("Transform", "RectTransform"))
    root_local = trs(*[root_tr.read_typetree()[k] for k in ("m_LocalPosition", "m_LocalRotation", "m_LocalScale")])
    walk(prefab.path_id, np.linalg.inv(root_local))
    if not meshes: return 0, 0
    Vs, Ts, Fs = [], [], []
    for mo, M in meshes:
        txt = mo.read().export()
        vb, tb = len(Vs), len(Ts)
        for line in txt.splitlines():
            p = line.split()
            if not p: continue
            if p[0] == "v":
                w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0])
                Vs.append((w[0], w[1], -w[2]))
            elif p[0] == "vt":
                Ts.append((float(p[1]), float(p[2])))
            elif p[0] == "f":
                idx = []
                for tok in p[1:]:
                    a = tok.split("/")
                    vi = int(a[0]) + vb
                    ti = (int(a[1]) + tb) if len(a) > 1 and a[1] else None
                    idx.append((vi, ti))
                idx.reverse()   # Z negate flips handedness -> reverse the winding
                Fs.append(idx)
    with open(out_path, "w") as f:
        f.write("g Model_0\n")
        for v in Vs: f.write("v %.6f %.6f %.6f\n" % v)
        for t in Ts: f.write("vt %.6f %.6f\n" % t)
        for face in Fs:
            f.write("f " + " ".join((("%d/%d" % (vi, ti)) if ti else str(vi)) for vi, ti in face) + "\n")
    return len(Vs), len(Fs)

# ---- which magazine each gun uses -------------------------------------------------------------
mag_folder = {}
for ln in open(os.path.join(CONTENT, "magazines.tsv")):
    c = ln.rstrip("\n").split("\t")
    if len(c) > 1 and c[0].isdigit(): mag_folder[int(c[0])] = c[1]
guns = [l.split("\t")[0].strip() for l in open(os.path.join(CONTENT, "guns_visual.tsv")) if l.strip()]

gun_mag, need = {}, set()
for g in guns:
    dat = os.path.join(CONTENT, g + ".dat")
    if not os.path.exists(dat): continue
    m = re.search(r"^Magazine\s+(\d+)", open(dat, errors="ignore").read(), re.M)
    if not m: continue
    folder = mag_folder.get(int(m.group(1)))
    if not folder: continue          # ids 5004/914x are port-invented rounds with no retail prefab
    gun_mag[g] = folder; need.add(folder)

print("guns with a retail magazine: %d ; distinct magazine models: %d" % (len(gun_mag), len(need)))

# ---- 1) the models -----------------------------------------------------------------------------
ok = fail = 0
for folder in sorted(need):
    pre = find_prefab("magazines/%s/magazine.prefab" % folder.lower())
    out = os.path.join(CONTENT, "mag_%s.txt" % folder.lower())
    if pre is None:
        print("  MISSING PREFAB %s" % folder); fail += 1; continue
    nv, nf = extract_mesh(pre, out)
    if nv == 0:
        print("  NO MESH        %s" % folder); fail += 1
    else:
        print("  %-22s %4d verts %4d faces" % (folder, nv, nf)); ok += 1
print("models: %d written, %d failed" % (ok, fail))

# ---- 2) the per-gun Magazine hook ---------------------------------------------------------------
def child_local(gun, child):
    p = find_prefab("/guns/%s/item.prefab" % gun)
    if not p: return None
    tr = comp_of(p.read_typetree(), ("Transform",))
    for ch in tr.read_typetree().get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct: continue
        ctt = ct.read_typetree()
        cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if cgo and cgo.read_typetree().get("m_Name") == child:
            lp = ctt["m_LocalPosition"]; return (lp["x"], lp["y"], lp["z"])
    return None

lines, nh = [], 0
for g in guns:
    h = child_local(g, "Magazine")
    hp = "%.4f,%.4f,%.4f" % (h[0], h[1], -h[2]) if h else ""   # mount: z-neg, same as the Sight hook
    if hp: nh += 1
    lines.append("%s\t%s\t%s" % (g, hp, gun_mag.get(g, "")))
open(os.path.join(CONTENT, "guns_maghook.tsv"), "w").write("\n".join(lines) + "\n")
print("guns_maghook.tsv: %d rows, %d with a Magazine hook" % (len(lines), nh))
