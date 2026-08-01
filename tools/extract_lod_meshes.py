"""Extract the LOD1/LOD2 meshes retail ships but the port has never used.

extract_objects_v2.py builds each prop's .obj from lod0_gos() -- the LODGroup's LEVEL 0 renderers,
i.e. the highest-detail mesh. Every other level in the bundle is discarded, so the port draws every
prop at full triangle density right up to its cull distance. Culling changes how MANY props draw;
this is what changes how much each one COSTS.

For every placed prop with more than one LOD level, this writes the lower levels beside the LOD0
mesh the other extractor already produced:

    Fridge.obj          (LOD0, existing, untouched)
    Fridge_lod1.obj     (this script)
    Fridge_lod2.obj     (this script, when a third level exists)

Transform handling is deliberately identical to extract_objects_v2.extract_combined -- same root-local
inverse, same per-part world matrix, and the same HALF POSITION SWAP (negating each part's world X
translation). If that quirk were applied to LOD0 and not to LOD1, a multi-part prop would flip its
parts left/right the instant it swapped level, which reads as geometry popping sideways.

LOD meshes reuse LOD0's material, so no texture work is needed here.

Run:  python3 tools/extract_lod_meshes.py [name-substring]
"""
import UnityPy, os, glob, re, struct, sys
import numpy as np
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OBJDAT = ug_paths.map_file("Level", "Objects.dat")
OUT = ug_paths.objects_out()

# --- GUIDs PEI places (same parse as extract_objects_v2) ---
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


def level_gos(prefab, gomap, level):
    """GameObjects rendered at LOD `level`. Returns None when the prop has no such level."""
    lg = find_lodgroup(prefab)
    if not lg: return None
    lods = lg.read_typetree().get("m_LODs", [])
    if level >= len(lods): return None
    gos = []
    for r in lods[level].get("renderers", lods[level].get("_renderers", [])):
        rp = (r.get("renderer") or {}).get("m_PathID"); rc = by_id.get(rp)
        if rc:
            gp = rc.read_typetree().get("m_GameObject", {}).get("m_PathID")
            if gp in gomap: gos.append(gp)
    return gos


def extract_level(prefab, level):
    rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
    root_local = trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])
    gomap = {}
    walk(prefab.path_id, np.linalg.inv(root_local), gomap)
    gos = level_gos(prefab, gomap, level)
    if not gos: return None
    Vs, Ns, Ts, Fs, used = [], [], [], [], []
    for gp in gos:
        M, mp = gomap[gp]
        M = M.copy(); M[0, 3] = -M[0, 3]   # HALF POSITION SWAP -- identical to LOD0's extraction. Applying it to
        #                                    one level and not the other would flip a multi-part prop's parts
        #                                    left/right at the exact moment it swaps LOD.
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
                n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])])
                L = (n[0]**2+n[1]**2+n[2]**2)**0.5; Ns.append(tuple(n/L if L > 0 else n))
            elif p[0] == "vt":
                Ts.append((p[1], p[2]))
            elif p[0] == "f":
                idx = []
                for tok in p[1:]:
                    q = tok.split("/")
                    vi = int(q[0])+vb
                    ti = (int(q[1])+tb) if len(q) > 1 and q[1] else None
                    ni = (int(q[2])+nb) if len(q) > 2 and q[2] else None
                    idx.append((vi, ti, ni))
                Fs.append(idx)
    if not Vs: return None
    return Vs, Ns, Ts, Fs, used


def mesh_name(mp):
    try: return by_id[mp].read_typetree().get("m_Name", "")
    except Exception: return ""


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
wrote = 0
tri_saving = []
for gid in TOP:
    info = guid2info.get(gid)
    if not info: continue
    name, cont = info
    prefab = prefabs.get(cont)
    if not prefab: continue
    if only and only not in name.lower(): continue
    base = None
    for level in (0, 1, 2):
        got = extract_level(prefab, level)
        if got is None:
            if level == 0: break
            continue
        Vs, Ns, Ts, Fs, used = got
        if level == 0:
            base = len(Fs)     # LOD0 face count, for the saving report only -- its .obj is NOT rewritten here
            continue
        write_obj(os.path.join(OUT, f"{name}_lod{level}.obj"), Vs, Ns, Ts, Fs)
        wrote += 1
        if base:
            tri_saving.append((name, level, base, len(Fs), 1 - len(Fs) / base))

print(f"[lodmesh] wrote {wrote} LOD meshes -> {OUT}")
if tri_saving:
    tri_saving.sort(key=lambda r: -r[4])
    avg = sum(r[4] for r in tri_saving) / len(tri_saving)
    print(f"[lodmesh] mean triangle reduction {avg*100:.1f}%  over {len(tri_saving)} levels")
    for n, lv, b, a, s in tri_saving[:6]:
        print(f"    {n:26} LOD{lv}  {b:6} -> {a:6} tris  ({s*100:.0f}% fewer)")
