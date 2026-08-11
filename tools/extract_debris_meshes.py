#!/usr/bin/env python3
"""Extract each destructible prop's authored DEAD (permanent broken husk) + RAGDOLL (physics debris) meshes
from core.masterbundle -> OBJs, in the SAME coordinate convention as extract_objects_v2.py (so they align
with the prop's <name>.obj alive mesh).

extract_objects_v2 SKIPs the `dead`/`ragdoll` nodes (its SKIP set) -- it only ever pulled the Alive mesh.
This tool targets exactly those subtrees instead:
  - Dead   -> <name>_Debris.obj   (WorldBuilder.DebrisMeshFor already reads this + swaps it in on destroy; 0 code)
  - Ragdoll-> <name>_Ragdoll.obj  (the real physics pieces, to feed the break drop instead of a whole-model clone)
Only the TOP-LEVEL Dead/Ragdoll is taken (Sections/ pruned) so the husk is one clean mesh, not doubled with
per-section copies -- sectioned props (fences breaking panel-by-panel) are a separate phase.

    python extract_debris_meshes.py <BUNDLES_DIR> <rubble.txt> <OUT_DIR>
"""
import UnityPy, os, glob, re, numpy as np, sys, json

BUND, RUBBLE, OUT = sys.argv[1], sys.argv[2], sys.argv[3]

# guid -> (name, object.prefab container path)
guid2info = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    guid2info[m.group(1).lower()] = (os.path.basename(os.path.dirname(datp)),
                                     "assets/coremasterbundle/" + rel + "/object.prefab")

destr = set(l.split()[0].lower() for l in open(RUBBLE) if l.split())

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

# collect every mesh inside a subtree named `target`; prune the rest (alive/fx/nav + the OTHER state + sections)
def walk_target(go_pid, parentM, gomap, target, inside):
    go = by_id.get(go_pid)
    if not go: return
    tt = go.read_typetree()
    nm = (tt.get("m_Name", "") or "").lower()
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    now_inside = inside or (nm == target)
    if now_inside:
        mf = comp_of(tt, ("MeshFilter",))
        mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
        if mp: gomap[go_pid] = (M, mp)
    elif nm in ("effect", "nav", "block", "trap", "alive", "sections") or (nm in ("dead", "ragdoll") and nm != target):
        return   # not the target subtree -> prune
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct: walk_target(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M, gomap, target, now_inside)

def mesh_name(mp):
    try: return by_id[mp].read_typetree().get("m_Name", "")
    except Exception: return ""

def extract_target(prefab, target):
    rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
    root_local = trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])
    gomap = {}; walk_target(prefab.path_id, np.linalg.inv(root_local), gomap, target, False)
    Vs, Ns, Ts, Fs, used = [], [], [], [], []
    for gp, (M, mp) in gomap.items():
        if not mp or mp not in by_id: continue
        M = M.copy(); M[0, 3] = -M[0, 3]   # HALF POSITION SWAP -- match extract_objects_v2 (meshes pre-mirrored, part X offset isn't)
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

os.makedirs(OUT, exist_ok=True)
manifest = {}; nd = nr = 0
for gid in destr:
    info = guid2info.get(gid)
    if not info: continue
    name, cont = info
    pf = prefabs.get(cont)
    if not pf: continue
    rec = {}
    dv, dn, dt, df, du = extract_target(pf, "dead")
    if dv:
        write_obj(os.path.join(OUT, name + "_Debris.obj"), dv, dn, dt, df)
        rec["dead"] = {"file": name + "_Debris.obj", "parts": len(du), "verts": len(dv)}; nd += 1
    rv, rn, rt2, rf, ru = extract_target(pf, "ragdoll")
    if rv:
        write_obj(os.path.join(OUT, name + "_Ragdoll.obj"), rv, rn, rt2, rf)
        rec["ragdoll"] = {"file": name + "_Ragdoll.obj", "parts": len(ru), "verts": len(rv)}; nr += 1
    if rec: manifest[name] = rec
json.dump(manifest, open(os.path.join(OUT, "debris_manifest.json"), "w"), indent=1, sort_keys=True)
print(f"[debris] {len(manifest)} props with debris: {nd} Dead objs, {nr} Ragdoll objs -> {OUT}")
for k, v in list(manifest.items())[:12]:
    print("   ", k, v)
