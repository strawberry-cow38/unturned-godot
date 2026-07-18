"""Force-extract a single Unturned object (mesh + albedo) by its Bundles/Objects folder name,
bypassing extract_objects_v2's PEI-top-500 filter. For props that aren't placed on PEI (e.g. store
shelves) so they can still be placed in the map editor. Reuses v2's LOD0/world-transform mesh combine.

  python extract_object_named.py Shelf_2
-> content/objects/Shelf_2.obj (+ Shelf_2_tex.png) + a guid_mesh.txt line if missing."""
import UnityPy, os, glob, re, numpy as np, sys

BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
TARGET = (sys.argv[1] if len(sys.argv) > 1 else "Shelf_2")

# GUID (netguid form, as in guid_mesh.txt) + prefab container path, keyed by folder name.
name2info = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    folder = os.path.basename(os.path.dirname(datp))
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    name2info[folder] = (m.group(1).lower(), "assets/coremasterbundle/" + rel + "/object.prefab")

if TARGET not in name2info:
    print("NOT FOUND in Bundles/Objects:", TARGET); sys.exit(1)
guid, cont = name2info[TARGET]
print("target", TARGET, "guid", guid, "prefab", cont)

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
prefab = None
for path, obj in env.container.items():
    if obj.type.name == "GameObject" and path.lower() == cont:
        prefab = obj; break
if not prefab:
    print("prefab not in core.masterbundle:", cont); sys.exit(1)

# ---- mesh combine (verbatim math from extract_objects_v2) ----
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
Vs, Ns, Ts, Fs, used = [], [], [], [], []
for gp in lod0_gos(prefab, gomap):
    M, mp = gomap[gp]
    M = M.copy(); M[0, 3] = -M[0, 3]
    if not mp or mp not in by_id: continue
    nm = mesh_name(mp)
    if "dead" in nm.lower(): continue
    used.append(nm)
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

if not Vs:
    print("NO GEOMETRY extracted"); sys.exit(1)
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
print("wrote", TARGET + ".obj", "parts=%d" % len(used), used, "verts=%d" % len(Vs))

# ---- albedo: biggest Texture2D reachable from the prefab's materials ----
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
                    img = tex.read().image
                    area = img.width * img.height
                    if not best or area > best[0]: best = (area, img, name)
                except Exception: pass
if best:
    best[1].save(os.path.join(OUT, TARGET + "_tex.png"))
    print("wrote", TARGET + "_tex.png", best[1].width, "x", best[1].height, "prop", best[2])
else:
    print("NO TEXTURE found")

# ensure a guid_mesh.txt entry
gmp = os.path.join(OUT, "guid_mesh.txt")
have = set()
if os.path.exists(gmp):
    for line in open(gmp):
        pp = line.split()
        if pp: have.add(pp[0])
if guid not in have:
    open(gmp, "a").write("%s %s\n" % (guid, TARGET))
    print("added guid_mesh entry", guid, TARGET)
