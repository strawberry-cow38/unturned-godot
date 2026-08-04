"""Batch object-mesh extractor: rip many props in ONE masterbundle load (extract_object_named does one
per process, which reloads the ~GB bundle every time). Same LOD0/world-transform combine + albedo + the
guid_mesh.txt append, looped over a names file (default wash_missing_names.txt from wash_prop_scope.py).
  python batch_extract_objects.py [names_file]"""
import UnityPy, os, glob, re, numpy as np, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
BUND = ug_paths.bundles(); OUT = ug_paths.objects_out()
namesfile = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OUT, "wash_missing_names.txt")
targets = [l.strip() for l in open(namesfile) if l.strip()]
print("targets:", len(targets))

name2info = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    folder = os.path.basename(os.path.dirname(datp))
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    name2info[folder] = (m.group(1).lower(), "assets/coremasterbundle/" + rel + "/object.prefab")

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
go_by_cont = {p.lower(): o for p, o in env.container.items() if o.type.name == "GameObject"}

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

gmp = os.path.join(OUT, "guid_mesh.txt")
have = set()
if os.path.exists(gmp):
    for line in open(gmp):
        pp = line.split()
        if pp: have.add(pp[0])
gmf = open(gmp, "a")

def extract_one(TARGET):
    if TARGET not in name2info: return "no-info"
    guid, cont = name2info[TARGET]
    prefab = go_by_cont.get(cont)
    if not prefab: return "no-prefab"
    rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform"))
    if not rt: return "no-transform"
    rtt = rt.read_typetree()
    root_local = trs(rtt["m_LocalPosition"], rtt["m_LocalRotation"], rtt["m_LocalScale"])
    gomap = {}; walk(prefab.path_id, np.linalg.inv(root_local), gomap)
    Vs, Ns, Ts, Fs, used = [], [], [], [], []
    for gp in lod0_gos(prefab, gomap):
        M, mp = gomap[gp]
        M = M.copy(); M[0, 3] = -M[0, 3]
        if not mp or mp not in by_id: continue
        used.append(mesh_name(mp))
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
    if not Vs: return "no-geo"
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
    open(os.path.join(OUT, TARGET + ".obj"), "w").write("\n".join(L) + "\n")
    # albedo: biggest Texture2D reachable from the prefab's materials
    best = None
    for gp, (M, mp) in gomap.items():
        go = by_id.get(gp)
        if not go: continue
        mr = comp_of(go.read_typetree(), ("MeshRenderer",))
        if not mr: continue
        for matref in mr.read_typetree().get("m_Materials", []):
            mat = by_id.get(matref.get("m_PathID"))
            if not mat: continue
            for entry in mat.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
                if isinstance(entry, (list, tuple)) and len(entry) == 2: name, env_ = entry
                elif isinstance(entry, dict): name, env_ = entry.get("first", entry.get("Key", "?")), entry.get("second", entry.get("Value", {}))
                else: continue
                if not isinstance(env_, dict): continue
                tex = by_id.get((env_.get("m_Texture") or {}).get("m_PathID"))
                if tex and tex.type.name == "Texture2D":
                    try:
                        img = tex.read().image; area = img.width * img.height
                        if not best or area > best[0]: best = (area, img)
                    except Exception: pass
    if best: best[1].save(os.path.join(OUT, TARGET + "_tex.png"))
    if guid not in have:
        gmf.write("%s %s\n" % (guid, TARGET)); gmf.flush(); have.add(guid)
    return "ok parts=%d verts=%d tex=%s" % (len(used), len(Vs), "Y" if best else "N")

ok = 0; fails = []
for t in targets:
    try:
        r = extract_one(t)
    except Exception as e:
        r = "ERR " + str(e)[:70]
    if r.startswith("ok"): ok += 1
    else: fails.append((t, r))
    print("%-24s %s" % (t, r))
gmf.close()
print("DONE extracted=%d/%d fail=%d" % (ok, len(targets), len(fails)))
for t, r in fails: print("  FAIL", t, r)
