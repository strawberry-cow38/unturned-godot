"""Split-extract red-dot / halo / kobra sights:
  content/<folder>.txt        = HOUSING only (Model_0) -- drops the Reticule so it stops rendering as a black disc
  content/<base>_reticle.png  = the Reticule's _MainTex (32x32 glowing-dot texture)
and prints the Reticule's baked local offset / bbox / _EmissionColor for the billboard render config.
Verts are RAW Unity (ParseObj applies the (x,y,-z) gun-frame convention on load, same as the other sight .txt)."""
import UnityPy, numpy as np, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
try: sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception: pass
BUND = ug_paths.bundles()
CONTENT = os.path.dirname(ug_paths.objects_out())   # .../game/content
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
cont = env.container
FOLDERS = sys.argv[1:] or ["red_dot_sight", "red_halo_sight", "red_kobra_sight"]

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

def mat_of(node_tt):
    mr = comp_of(node_tt, ("MeshRenderer",))
    if not mr: return None
    mats = mr.read_typetree().get("m_Materials", [])
    return by_id.get(mats[0].get("m_PathID")) if mats else None

def emission_and_tex(mat):
    mt = mat.read_typetree(); emi = None; tex = None
    for e in mt.get("m_SavedProperties", {}).get("m_Colors", []):
        cn, cv = (e if isinstance(e, (list, tuple)) else (e.get("first"), e.get("second")))
        if cn == "_EmissionColor" and isinstance(cv, dict): emi = (cv["r"], cv["g"], cv["b"])
    for e in mt.get("m_SavedProperties", {}).get("m_TexEnvs", []):
        tn, tv = (e if isinstance(e, (list, tuple)) else (e.get("first"), e.get("second")))
        if tn == "_MainTex" and isinstance(tv, dict):
            tx = by_id.get((tv.get("m_Texture") or {}).get("m_PathID"))
            if tx and tx.type.name == "Texture2D": tex = tx
    return emi, tex

for folder in FOLDERS:
    prefab = next((o for pa, o in cont.items() if f"/sights/{folder}/sight.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not prefab:
        print(f"{folder}: prefab not found"); continue
    root_tt = prefab.read_typetree()
    rtr = comp_of(root_tt, ("Transform",)).read_typetree()
    rootM = trs(rtr["m_LocalPosition"], rtr["m_LocalRotation"], rtr["m_LocalScale"])
    invRoot = np.linalg.inv(rootM)
    housing = {"V": [], "N": [], "T": [], "F": []}
    ret_info = None
    def walk(pid, parentM):
        go = by_id.get(pid)
        if not go: return
        tt = go.read_typetree(); nm = tt.get("m_Name", "")
        tr = comp_of(tt, ("Transform",))
        if not tr: return
        trt = tr.read_typetree()
        M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
        mf = comp_of(tt, ("MeshFilter",))
        mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
        if mp and mp in by_id:
            if nm == "Model_0":          # housing LOD0
                Vs, Ns, Ts, Fs = housing["V"], housing["N"], housing["T"], housing["F"]
                Rn = np.linalg.inv(M[:3, :3]).T
                vb, tb, nb = len(Vs), len(Ts), len(Ns)
                for line in by_id[mp].read().export().splitlines():
                    p = line.split()
                    if not p: continue
                    if p[0] == "v":
                        w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); Vs.append((w[0], w[1], w[2]))
                    elif p[0] == "vn":
                        n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])]); L = (n@n)**0.5; Ns.append(tuple(n/L if L else n))
                    elif p[0] == "vt": Ts.append((p[1], p[2]))
                    elif p[0] == "f":
                        idx = []
                        for tok in p[1:]:
                            q = tok.split("/"); vi = int(q[0])+vb; ti = (int(q[1])+tb) if len(q) > 1 and q[1] else None; ni = (int(q[2])+nb) if len(q) > 2 and q[2] else None
                            idx.append((vi, ti, ni))
                        Fs.append(idx)
            elif nm == "Reticule":       # the glowing dot: texture + baked center + bbox + emission
                global ret_info
                verts = []
                for line in by_id[mp].read().export().splitlines():
                    p = line.split()
                    if p and p[0] == "v":
                        w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); verts.append(w[:3])
                verts = np.array(verts); ctr = verts.mean(0); mn = verts.min(0); mx = verts.max(0)
                emi, tex = emission_and_tex(mat_of(tt))
                if tex is not None:
                    tex.read().image.save(os.path.join(CONTENT, folder.replace("_sight", "") + "_reticle.png"))
                ret_info = dict(center=ctr, size=(mx-mn), emission=emi)
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)
    walk(prefab.path_id, invRoot)
    # write housing .txt
    Vs, Ns, Ts, Fs = housing["V"], housing["N"], housing["T"], housing["F"]
    L = [f"# {folder} housing"] + ["v %.6f %.6f %.6f" % v for v in Vs] + ["vt %s %s" % t for t in Ts] + ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if ti and ni else (" %d//%d" % (vi, ni)) if ni else (" %d/%d" % (vi, ti)) if ti else (" %d" % vi)
        L.append(s)
    open(os.path.join(CONTENT, folder + ".txt"), "w").write("\n".join(L) + "\n")
    ri = ret_info or {}
    ctr = ri.get("center"); size = ri.get("size")
    print(f"{folder}: housing verts={len(Vs)} | reticle center(Unity)={None if ctr is None else tuple(round(c,4) for c in ctr)} "
          f"size={None if size is None else tuple(round(s,4) for s in size)} emission={ri.get('emission')}")
