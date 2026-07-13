#!/usr/bin/env python3
"""batch_crops.py -- extract ALL Seed_* farm crops (meshes + textures + dirt color) + emit crops.tsv.

Loops every seed_*/barricade.prefab in core.masterbundle, rips Model_0/Foliage_0/Foliage_1 as RAW
Unity meshes (ObjMesh.Load converts), the Foliage _MainTex PNGs, and the Model_0 dirt _Color, and
cross-references the retail .dat for the seed item ID. Output: <OUT>/crop_<name>_*.{txt,png} + crops.tsv
(name<TAB>seed_id<TAB>dirt_r,g,b<TAB>parts) so the port maps a Type-Farm seed -> its crop assets.
"""
import UnityPy, numpy as np, os, re, glob

CORE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
BARR = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items\Barricades"
OUT  = r"C:\claude-workspace\crops_out"
os.makedirs(OUT, exist_ok=True)

env = UnityPy.load(CORE)
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
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                  [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])
    M = np.eye(4); M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]]); M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

def kv_find(props, key, names):
    for e in props.get(key, []):
        if isinstance(e, (list, tuple)) and len(e) == 2: n, v = e
        elif isinstance(e, dict): n, v = e.get("first"), e.get("second")
        else: continue
        if n in names and isinstance(v, dict): return v
    return None

def export_raw(meshes, out):
    Vs, Ns, Ts, Fs = [], [], [], []
    for mo, M in meshes:
        txt = mo.read().export(); Rn = np.linalg.inv(M[:3, :3]).T
        vb, tb, nb = len(Vs), len(Ts), len(Ns)
        for line in txt.splitlines():
            p = line.split()
            if not p: continue
            if p[0] == "v":
                w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); Vs.append((w[0], w[1], w[2]))
            elif p[0] == "vn":
                n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])]); L = np.linalg.norm(n) or 1; Ns.append((n[0]/L, n[1]/L, n[2]/L))
            elif p[0] == "vt": Ts.append((p[1], p[2]))
            elif p[0] == "f":
                idx = []
                for tok in p[1:]:
                    q = tok.split("/"); idx.append((int(q[0])+vb, (int(q[1])+tb) if len(q)>1 and q[1] else None, (int(q[2])+nb) if len(q)>2 and q[2] else None))
                Fs.append(idx)
    L = ["g Model_0"] + ["v %.6f %.6f %.6f" % v for v in Vs] + ["vt %s %s" % t for t in Ts] + ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if ti and ni else (" %d//%d" % (vi, ni)) if ni else (" %d/%d" % (vi, ti)) if ti else (" %d" % vi)
        L.append(s)
    open(out, "w").write("\n".join(L) + "\n")
    return len(Vs)

def dat_id(name):
    # Seed_<Name>/Seed_<Name>.dat -> ID line. match folder case-insensitively.
    for d in glob.glob(os.path.join(BARR, "Seed_*")):
        if os.path.basename(d).lower() == "seed_" + name:
            for dat in glob.glob(os.path.join(d, "*.dat")):
                if "english" in os.path.basename(dat).lower(): continue
                for ln in open(dat, encoding="utf-8-sig", errors="ignore"):
                    m = re.match(r"\s*ID\s+(\d+)", ln)
                    if m: return int(m.group(1))
    return 0

seeds = [(p, o) for p, o in env.container.items()
         if re.search(r"barricades/seed_[^/]+/barricade\.prefab$", p.lower()) and o.type.name == "GameObject"]
rows = []
for path, prefab in sorted(seeds):
    name = re.search(r"seed_([^/]+)/barricade", path.lower()).group(1)
    root_tr = comp_of(prefab.read_typetree(), ("Transform",))
    if not root_tr: continue
    rtt = root_tr.read_typetree()
    # root is cancelled to IDENTITY (like extract_crop.py's walk(root, inv(root_local))): direct-child meshes
    # bake with just trs(child_local). (The old inv_root @ trs(child) double-applied the root -> corrupt meshes.)
    dirt = "0.4,0.3,0.15"; parts = []
    for ch in rtt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct: continue
        cgo_pid = ct.read_typetree().get("m_GameObject", {}).get("m_PathID"); cgo = by_id.get(cgo_pid)
        if not cgo: continue
        ctt = cgo.read_typetree(); cname = ctt.get("m_Name", "?")
        ctr = comp_of(ctt, ("Transform",))
        M = trs(*[ctr.read_typetree()[k] for k in ("m_LocalPosition", "m_LocalRotation", "m_LocalScale")])
        mf = comp_of(ctt, ("MeshFilter", "SkinnedMeshRenderer"))
        if not mf: continue
        mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID")
        if not (mp and mp in by_id): continue
        export_raw([(by_id[mp], M)], os.path.join(OUT, f"crop_{name}_{cname}.txt")); parts.append(cname)
        # material: texture + color
        rend = comp_of(ctt, ("MeshRenderer", "SkinnedMeshRenderer"))
        if rend:
            mats = rend.read_typetree().get("m_Materials", [])
            if mats:
                mo = by_id.get(mats[0].get("m_PathID"))
                if mo:
                    mtt = mo.read_typetree(); props = mtt.get("m_SavedProperties", {})
                    te = kv_find(props, "m_TexEnvs", ("_MainTex",))
                    to = by_id.get(te.get("m_Texture", {}).get("m_PathID")) if te else None
                    if to:
                        try: to.read().image.save(os.path.join(OUT, f"crop_{name}_{cname}.png"))
                        except Exception as e: print(f"  {name}/{cname} tex fail: {e}")
                    col = kv_find(props, "m_Colors", ("_Color",))
                    if col and cname == "Model_0":
                        dirt = "%.3f,%.3f,%.3f" % (col.get("r", 0.4), col.get("g", 0.3), col.get("b", 0.15))
    sid = dat_id(name)
    rows.append((name, sid, dirt, "+".join(parts)))
    print(f"{name}: id={sid} dirt={dirt} parts={parts}")

with open(os.path.join(OUT, "crops.tsv"), "w") as f:
    for name, sid, dirt, parts in rows:
        f.write(f"{name}\t{sid}\t{dirt}\t{parts}\n")
print(f"\n{len(rows)} crops -> crops.tsv")
