"""Split-extract Biodome_0 into TWO objs by source node:
  Biodome_0.obj        = Model_0 node (the ORANGE frame, opaque)
  Biodome_0_glass.obj  = Glass_0 node (the translucent glass panels)
Geometry only (both materials are solid _Color, no albedo texture -> driven in the port's MatFor).
Reuses extract_objects_v2's LOD0 / world-transform combine math verbatim."""
import UnityPy, os, glob, re, numpy as np, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
try: sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception: pass
BUND = ug_paths.bundles(); OUT = ug_paths.objects_out(); TARGET = "Biodome_0"

name2info = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    folder = os.path.basename(os.path.dirname(datp))
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    name2info[folder] = (m.group(1).lower(), "assets/coremasterbundle/" + rel + "/object.prefab")
guid, cont = name2info[TARGET]
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
prefab = None
for path, obj in env.container.items():
    if obj.type.name == "GameObject" and path.lower() == cont: prefab = obj; break

def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co: yield co
def comp_of(tt, names):
    for co in comps(tt):
        if co.type.name in names: return co
    return None
def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                  [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])
    M = np.eye(4); M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]]); M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M
def find_lodgroup(go, depth=0):
    if not go or depth > 10: return None
    tt = go.read_typetree(); kids = []
    for co in comps(tt):
        if co.type.name == "LODGroup": return co
        if co.type.name == "Transform": kids = co.read_typetree().get("m_Children", [])
    for ch in kids:
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            r = find_lodgroup(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")), depth + 1)
            if r: return r
    return None
def walk(go_pid, parentM, gomap):
    go = by_id.get(go_pid)
    if not go: return
    tt = go.read_typetree()
    if (tt.get("m_Name", "") or "").lower() in {"dead", "ragdoll", "effect", "nav", "block", "trap"}: return
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    mf = comp_of(tt, ("MeshFilter",))
    mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
    gomap[go_pid] = (M, mp)
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M, gomap)
def mesh_name(mp):
    try: return by_id[mp].read_typetree().get("m_Name", "")
    except Exception: return ""
def lod0_gos(prefab, gomap):
    lg = find_lodgroup(prefab)
    if lg:
        lods = lg.read_typetree().get("m_LODs", [])
        if lods:
            gos = []
            for r in lods[0].get("renderers", lods[0].get("_renderers", [])):
                rp = (r.get("renderer") or {}).get("m_PathID"); rc = by_id.get(rp)
                if rc:
                    gp = rc.read_typetree().get("m_GameObject", {}).get("m_PathID")
                    if gp in gomap: gos.append(gp)
            return gos
    return [g for g, (M, mp) in gomap.items() if mp]

rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
root_local = trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])
gomap = {}; walk(prefab.path_id, np.linalg.inv(root_local), gomap)

# two buffers: frame (Model_*) and glass (Glass_*)
bufs = {"frame": [[], [], [], []], "glass": [[], [], [], []]}  # Vs, Ns, Ts, Fs
for gp in lod0_gos(prefab, gomap):
    M, mp = gomap[gp]
    if not mp or mp not in by_id: continue
    nm = mesh_name(mp)
    key = "glass" if nm.lower().startswith("glass") else "frame"
    Vs, Ns, Ts, Fs = bufs[key]
    M = M.copy(); M[0, 3] = -M[0, 3]
    txt = by_id[mp].read().export()
    Rn = np.linalg.inv(M[:3, :3]).T
    vb, tb, nb = len(Vs), len(Ts), len(Ns)
    for line in txt.splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v":
            w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); Vs.append((w[0], w[1], w[2]))
        elif p[0] == "vn":
            n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])]); L = (n[0]**2+n[1]**2+n[2]**2)**0.5; Ns.append(tuple(n/L if L > 0 else n))
        elif p[0] == "vt": Ts.append((p[1], p[2]))
        elif p[0] == "f":
            idx = []
            for tok in p[1:]:
                q = tok.split("/"); vi = int(q[0])+vb; ti = (int(q[1])+tb) if len(q) > 1 and q[1] else None; ni = (int(q[2])+nb) if len(q) > 2 and q[2] else None
                idx.append((vi, ti, ni))
            Fs.append(idx)
    print(f"  node mesh '{nm}' -> {key}  (+{len(txt.splitlines())} lines)")

def write_obj(path, buf):
    Vs, Ns, Ts, Fs = buf
    if not Vs: print("EMPTY", path); return False
    L = ["v %.6f %.6f %.6f" % v for v in Vs]
    L += ["vt %s %s" % t for t in Ts]
    L += ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            if ti and ni: s += " %d/%d/%d" % (vi, ti, ni)
            elif ni: s += " %d//%d" % (vi, ni)
            elif ti: s += " %d/%d" % (vi, ti)
            else: s += " %d" % vi
        L.append(s)
    open(path, "w").write("\n".join(L) + "\n")
    print("wrote", os.path.basename(path), "verts=%d faces=%d" % (len(Vs), len(Fs)))
    return True

write_obj(os.path.join(OUT, "Biodome_0.obj"), bufs["frame"])
write_obj(os.path.join(OUT, "Biodome_0_glass.obj"), bufs["glass"])
