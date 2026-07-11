import UnityPy, os, glob, re, struct, numpy as np, sys
from collections import Counter
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OBJDAT = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Level\Objects.dat"
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"

# --- TOP object GUIDs from PEI Objects.dat (same parse as the original) ---
_d = open(OBJDAT, "rb").read(); _p = [0]
def _u8(): v = _d[_p[0]]; _p[0] += 1; return v
def _u16(): v = struct.unpack_from("<H", _d, _p[0])[0]; _p[0] += 2; return v
def _u32(): v = struct.unpack_from("<I", _d, _p[0])[0]; _p[0] += 4; return v
def _skf(n=1): _p[0] += 4 * n
def _g():
    ln = struct.unpack_from("<H", _d, _p[0])[0]; _p[0] += 2; g = _d[_p[0]:_p[0]+ln]; _p[0] += ln; return g
def _ng(g): return (g[0:4][::-1]+g[4:6][::-1]+g[6:8][::-1]+g[8:16]).hex() if len(g) == 16 else g.hex()
_u8(); _u32(); _cnt = Counter()
for _x in range(64):
    for _y in range(64):
        for _i in range(_u16()):
            _skf(9); _u16(); _gg = _g(); _u8(); _u32(); _g(); _u32(); _u8()
            _cnt[_ng(_gg)] += 1
TOP = [g for g, _ in _cnt.most_common(500) if len(g) == 32]

# --- GUID -> (name, prefab_path) ---
guid2info = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    guid2info[m.group(1).lower()] = (os.path.basename(os.path.dirname(datp)),
                                     "assets/coremasterbundle/" + rel + "/object.prefab")

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
prefabs = {}
for path, obj in env.container.items():
    if obj.type.name == "GameObject" and path.lower().endswith("/object.prefab"):
        prefabs[path.lower()] = obj

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

SKIP = {"dead", "ragdoll", "effect", "nav", "block", "trap"}
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
    if (tt.get("m_Name", "") or "").lower() in SKIP: return
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

def extract_combined(prefab):
    rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
    root_local = trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])
    gomap = {}; walk(prefab.path_id, np.linalg.inv(root_local), gomap)
    Vs, Ns, Ts, Fs, used = [], [], [], [], []
    for gp in lod0_gos(prefab, gomap):
        M, mp = gomap[gp]
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
    return Vs, Ns, Ts, Fs, used

def write_obj(path, Vs, Ns, Ts, Fs):
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

only = sys.argv[1].lower() if len(sys.argv) > 1 else None
regen = 0; multi = 0
for gid in TOP:
    info = guid2info.get(gid)
    if not info: continue
    name, cont = info
    obj = prefabs.get(cont)
    if not obj: continue
    if only and only not in name.lower() and only not in cont: continue
    Vs, Ns, Ts, Fs, used = extract_combined(obj)
    if not Vs: continue
    if not only and len(used) <= 1: continue   # full run only fixes multi-part props; leave single-mesh OBJs untouched
    write_obj(os.path.join(OUT, name + ".obj"), Vs, Ns, Ts, Fs)
    regen += 1
    if len(used) > 1:
        multi += 1
        print("MULTI %-24s parts=%d %s" % (name, len(used), used))
print(f"regenerated {regen} OBJs ({multi} multi-part)")
