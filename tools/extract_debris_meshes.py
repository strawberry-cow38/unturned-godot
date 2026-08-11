#!/usr/bin/env python3
"""Extract each destructible prop's authored DEAD (permanent broken husk) + RAGDOLL (physics debris) meshes
from core.masterbundle -> OBJs, in the SAME coordinate convention as extract_objects_v2.py (so they align
with the prop's <name>.obj alive mesh).

extract_objects_v2 SKIPs the `dead`/`ragdoll` nodes -- it only ever pulled the Alive mesh. This targets them:
  - Dead    -> <name>_Debris.obj      (WorldBuilder.DebrisMeshFor reads it + swaps it in on destroy; combined husk)
  - Ragdoll -> <name>_Ragdoll_<i>.obj (ONE obj PER PIECE -- retail Instantiates each Ragdoll/Model_x as its own
                                       Rigidbody, so the port spawns each as a separate physics body = scattering)
Only the TOP-LEVEL Dead/Ragdoll is taken (Sections/ pruned) -- sectioned props (fences panel-by-panel) are a
separate phase.

    python extract_debris_meshes.py <BUNDLES_DIR> <rubble.txt> <OUT_DIR>
"""
import UnityPy, os, glob, re, numpy as np, sys, json

BUND, RUBBLE, OUT = sys.argv[1], sys.argv[2], sys.argv[3]

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
def local_M(go):
    tr = comp_of(go.read_typetree(), ("Transform", "RectTransform"))
    if not tr: return None
    t = tr.read_typetree()
    return trs(t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"])
def child_gos(go):
    tr = comp_of(go.read_typetree(), ("Transform", "RectTransform"))
    if not tr: return []
    out = []
    for ch in tr.read_typetree().get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            g = by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
            if g: out.append(g)
    return out

# find the top-level node named `target` (pruning alive/fx/sections/the-other-state); return (go, M_at_node)
def find_node(go, parentM, target):
    lm = local_M(go)
    if lm is None: return None
    M = parentM @ lm
    nm = (go.read_typetree().get("m_Name", "") or "").lower()
    if nm == target: return (go, M)
    if nm in ("effect", "nav", "block", "trap", "alive", "sections") or nm in ("dead", "ragdoll"):
        return None
    for c in child_gos(go):
        r = find_node(c, M, target)
        if r: return r
    return None

# find the `Sections` node (fences etc.) -- like find_node but does NOT prune sections
def find_sections(go, parentM):
    lm = local_M(go)
    if lm is None: return None
    M = parentM @ lm
    nm = (go.read_typetree().get("m_Name", "") or "").lower()
    if nm == "sections": return (go, M)
    if nm in ("effect", "nav", "block", "trap", "alive", "dead", "ragdoll"): return None
    for c in child_gos(go):
        r = find_sections(c, M)
        if r: return r
    return None

# collect every mesh at/under `go`, given go's already-computed world matrix M
def collect(go, M, gomap):
    tt = go.read_typetree()
    mf = comp_of(tt, ("MeshFilter",))
    mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
    if mp: gomap[go.path_id] = (M, mp)
    for c in child_gos(go):
        lm = local_M(c)
        if lm is not None: collect(c, M @ lm, gomap)

def combine(gomap):
    Vs, Ns, Ts, Fs, used = [], [], [], [], []
    for gp, (M, mp) in gomap.items():
        if not mp or mp not in by_id: continue
        M = M.copy(); M[0, 3] = -M[0, 3]   # HALF POSITION SWAP -- match extract_objects_v2 (pre-mirrored meshes, part X offset isn't)
        try: used.append(by_id[mp].read_typetree().get("m_Name", ""))
        except Exception: used.append("")
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

def root_start(prefab):
    rlm = local_M(prefab)
    return np.linalg.inv(rlm) if rlm is not None else np.eye(4)

os.makedirs(OUT, exist_ok=True)
manifest = {}; nd = nr = npieces = 0
for gid in destr:
    info = guid2info.get(gid)
    if not info: continue
    name, cont = info
    pf = prefabs.get(cont)
    if not pf: continue
    start = root_start(pf)
    rec = {}
    # DEAD: whole subtree combined -> one husk obj
    dn = find_node(pf, start, "dead")
    if dn:
        gm = {}; collect(dn[0], dn[1], gm)
        Vs, Ns, Ts, Fs, used = combine(gm)
        if Vs:
            write_obj(os.path.join(OUT, name + "_Debris.obj"), Vs, Ns, Ts, Fs)
            rec["dead"] = {"file": name + "_Debris.obj", "parts": len(used), "verts": len(Vs)}; nd += 1
    # RAGDOLL: one obj PER PIECE. A prop with a Ragdoll node -> each of its pieces. A SECTIONED prop (fences: no
    # Ragdoll node) -> each Section_i's Alive mesh is a falling PANEL (master: "a bunch of fence panels falling").
    rn = find_node(pf, start, "ragdoll")
    files = []
    if rn:
        rgo, rM = rn
        kids = child_gos(rgo)
        pieces = [(c, rM @ local_M(c)) for c in kids if local_M(c) is not None] if kids else [(rgo, rM)]
        for pgo, pM in pieces:
            gm = {}; collect(pgo, pM, gm)
            Vs, Ns, Ts, Fs, used = combine(gm)
            if not Vs: continue
            fn = f"{name}_Ragdoll_{len(files)}.obj"; write_obj(os.path.join(OUT, fn), Vs, Ns, Ts, Fs); files.append(fn); npieces += 1
    else:
        sn = find_sections(pf, start)
        if sn:
            sgo, sM = sn
            for sec in child_gos(sgo):                      # Section_0, Section_1, ...
                lm = local_M(sec)
                if lm is None: continue
                secM = sM @ lm
                for a in child_gos(sec):                    # the section's Alive panel
                    if (a.read_typetree().get("m_Name", "") or "").lower() == "alive":
                        alm = local_M(a)
                        if alm is None: continue
                        gm = {}; collect(a, secM @ alm, gm)
                        Vs, Ns, Ts, Fs, used = combine(gm)
                        if not Vs: continue
                        fn = f"{name}_Ragdoll_{len(files)}.obj"; write_obj(os.path.join(OUT, fn), Vs, Ns, Ts, Fs); files.append(fn); npieces += 1
    if files:
        rec["ragdoll"] = {"files": files, "pieces": len(files)}; nr += 1
    if rec: manifest[name] = rec
json.dump(manifest, open(os.path.join(OUT, "debris_manifest.json"), "w"), indent=1, sort_keys=True)
print(f"[debris] {len(manifest)} props: {nd} Dead husks, {nr} Ragdoll sets ({npieces} pieces) -> {OUT}")
for k, v in list(manifest.items())[:14]:
    print("   ", k, v)
