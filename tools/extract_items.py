#!/usr/bin/env python3
"""extract_items.py -- batch-extract every PEI loot-table item's DROPPED world model.

Generalizes extract_attachment_mesh.py to all 402 PEI loot items. For each item:
  - resolves items/<cat>/<name>/item.prefab in core.masterbundle
  - walks the prefab tree, bakes each active MeshFilter's TRS relative to the root
    (root-cancelled), combines all parts into ONE Wavefront .obj
  - same Unturned->Godot convention as the rest of the rip: negate Z on pos+normal,
    reverse winding (RH Y-up), V-down UVs handled later by ContentProvider.ParseObj
  - grabs the primary part's _MainTex -> <id>.png (rarity-tint fallback if none)
  - computes the best-fit AABB box (size + center) -> the RigidBody collider

Outputs: <OUT>/<id>.obj, <OUT>/<id>.png, <OUT>/items_manifest.json
"""
import UnityPy, numpy as np, sys, os, json, io

U = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
CORE = os.path.join(U, r"Bundles\core.masterbundle")
OUT = r"C:\claude-workspace\item_out"
os.makedirs(OUT, exist_ok=True)
loot = json.load(open(r"C:\claude-workspace\pei_loot_items.json"))["resolved"]

print("loading core.masterbundle ...", flush=True)
env = UnityPy.load(CORE)
by_id = {o.path_id: o for o in env.objects}
# container GameObject prefabs by normalized path
go_by_path = {}
for p, o in env.container.items():
    if o.type.name == "GameObject":
        go_by_path[p.split("assets/coremasterbundle/")[-1]] = o

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
    M = np.eye(4)
    M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]])
    M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

def main_tex_pathid(mat_obj):
    if not mat_obj: return None
    try:
        mt = mat_obj.read_typetree()
        tex = mt.get("m_SavedProperties", {}).get("m_TexEnvs", [])
        for entry in tex:
            # entry is (name, texenv) or {"first":name,"second":{...}}
            if isinstance(entry, (list, tuple)) and len(entry) == 2:
                name, te = entry
            elif isinstance(entry, dict):
                name, te = entry.get("first"), entry.get("second")
            else:
                continue
            if name in ("_MainTex", "_AlbedoBase", "_Albedo") and isinstance(te, dict):
                pid = te.get("m_Texture", {}).get("m_PathID")
                if pid: return pid
    except Exception:
        pass
    return None

def mat_color(mat_obj):
    """flat _Color of a material (its real look when it has NO _MainTex albedo -- rope/bricks/suppressor...)."""
    if not mat_obj: return None
    try:
        cols = mat_obj.read_typetree().get("m_SavedProperties", {}).get("m_Colors", [])
        for e in cols:
            if isinstance(e, (list, tuple)) and len(e) == 2: nm, cv = e
            elif isinstance(e, dict): nm, cv = e.get("first"), e.get("second")
            else: continue
            if nm == "_Color" and isinstance(cv, dict):
                return [round(cv.get("r", 1), 4), round(cv.get("g", 1), 4), round(cv.get("b", 1), 4)]
    except Exception:
        pass
    return None

def prefix_of(folder):
    p = folder.replace("\\", "/").lower().split("/bundles/items/")
    return "items/" + p[1] + "/item.prefab" if len(p) > 1 else None

def comp_all(tt, name):
    out = []
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name == name: out.append(co)
    return out

def lod0_gos(prefab):
    """If the item has an LODGroup, return the SET of GameObject pathIDs whose renderer is in LOD0 (highest detail);
    else None (no LODGroup -> take all meshes). Model_0/Model_1 are LOD0/LOD1, NOT skin surfaces -- rendering both
    put LOD1 over LOD0 (candy bar's crude LOD1 = the pink rectangle, master)."""
    result, has = set(), [False]
    def walk(pid):
        go = by_id.get(pid)
        if not go: return
        tt = go.read_typetree()
        for lg in comp_all(tt, "LODGroup"):
            has[0] = True
            lods = lg.read_typetree().get("m_LODs", [])
            if lods:
                for r in lods[0].get("renderers", []):
                    ro = by_id.get(r.get("renderer", {}).get("m_PathID"))
                    if ro:
                        gp = ro.read_typetree().get("m_GameObject", {}).get("m_PathID")
                        if gp: result.add(gp)
        tr = comp_of(tt, ("Transform", "RectTransform"))
        if tr:
            for ch in tr.read_typetree().get("m_Children", []):
                ct = by_id.get(ch.get("m_PathID"))
                if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
    walk(prefab.path_id)
    return result if has[0] else None

def extract(prefab):
    """returns (parts=[(mesh_obj, M, mat_obj, vcount)], root_local)"""
    ptt = prefab.read_typetree()
    root_tr = comp_of(ptt, ("Transform", "RectTransform"))
    root_trt = root_tr.read_typetree()
    root_local = trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"])
    lod0 = lod0_gos(prefab)   # only extract LOD0 meshes (skip LOD1+); None = no LODGroup, take all
    parts = []
    def walk(go_pid, parentM):
        go = by_id.get(go_pid)
        if not go: return
        tt = go.read_typetree()
        if not tt.get("m_IsActive", True): return          # skip inactive branches (they don't render)
        tr = comp_of(tt, ("Transform", "RectTransform"))
        if not tr: return
        trt = tr.read_typetree()
        M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
        mf = comp_of(tt, ("MeshFilter",))
        smr = comp_of(tt, ("SkinnedMeshRenderer",))
        mr = comp_of(tt, ("MeshRenderer",))
        renderer = mr or smr
        for mc in (mf, smr):
            if not mc: continue
            mp = mc.read_typetree().get("m_Mesh", {}).get("m_PathID")
            if mp and mp in by_id and (lod0 is None or go_pid in lod0):   # LOD0 only (skip LOD1 overlaps)
                mat_obj = None
                if renderer:
                    mats = renderer.read_typetree().get("m_Materials", [])
                    if mats:
                        mat_obj = by_id.get(mats[0].get("m_PathID"))
                parts.append((by_id[mp], M, mat_obj))
                break
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct:
                walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)
    walk(prefab.path_id, np.linalg.inv(root_local))
    return parts

def bake_obj(parts):
    Vs, Ns, Ts, Fs = [], [], [], []
    part_vcounts = []
    for (mo, M, _mat) in parts:
        try: txt = mo.read().export()
        except Exception: continue
        Rn = np.linalg.inv(M[:3, :3]).T
        vb, tb, nb = len(Vs), len(Ts), len(Ns)
        pv = 0
        for line in txt.splitlines():
            p = line.split()
            if not p: continue
            if p[0] == "v":
                w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0])
                Vs.append((w[0], w[1], -w[2])); pv += 1
            elif p[0] == "vn":
                n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])])
                ln = np.linalg.norm(n); n = n/ln if ln > 0 else n
                Ns.append((n[0], n[1], -n[2]))
            elif p[0] == "vt":
                Ts.append((p[1], p[2]))
            elif p[0] == "f":
                idx = []
                for tok in p[1:]:
                    q = tok.split("/")
                    vi = int(q[0]) + vb
                    ti = (int(q[1]) + tb) if len(q) > 1 and q[1] else None
                    ni = (int(q[2]) + nb) if len(q) > 2 and q[2] else None
                    idx.append((vi, ti, ni))
                Fs.append(list(reversed(idx)))
        part_vcounts.append(pv)
    return Vs, Ns, Ts, Fs, part_vcounts

def write_obj(path, Vs, Ns, Ts, Fs):
    L = ["# Unturned item rip -> Godot (Z negated, winding reversed, RH Y-up)"]
    L += ["v %.6f %.6f %.6f" % v for v in Vs]
    L += ["vt %s %s" % t for t in Ts]
    L += ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            if ti and ni: s += " %d/%d/%d" % (vi, ti, ni)
            elif ni:      s += " %d//%d" % (vi, ni)
            elif ti:      s += " %d/%d" % (vi, ti)
            else:         s += " %d" % vi
        L.append(s)
    open(path, "w").write("\n".join(L) + "\n")

manifest = {}
n_ok = n_tex = n_multi = n_fail = 0
report = []
for iid_s, meta in loot.items():
    iid = int(iid_s); name = meta["name"]; typ = meta["type"]
    pref = prefix_of(meta["folder"])
    prefab = go_by_path.get(pref) if pref else None
    if prefab is None:
        n_fail += 1; report.append((iid, name, typ, "NO_PREFAB", 0, 0)); continue
    try:
        parts = extract(prefab)
        Vs, Ns, Ts, Fs, pvc = bake_obj(parts)
    except Exception as e:
        n_fail += 1; report.append((iid, name, typ, f"ERR:{e}", 0, 0)); continue
    if not Vs:
        n_fail += 1; report.append((iid, name, typ, "NO_MESH", len(parts), 0)); continue
    xs=[v[0] for v in Vs]; ys=[v[1] for v in Vs]; zs=[v[2] for v in Vs]
    box=[max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs)]
    center=[(max(xs)+min(xs))/2, (max(ys)+min(ys))/2, (max(zs)+min(zs))/2]
    write_obj(os.path.join(OUT, f"{iid}.obj"), Vs, Ns, Ts, Fs)

    # primary texture = material of the biggest part; if it has no albedo, fall back to its flat _Color
    tex_name = None; flat = None
    if parts and pvc:
        bigi = max(range(len(pvc)), key=lambda i: pvc[i]) if pvc else 0
        mat_obj = parts[bigi][2] if bigi < len(parts) else None
        tpid = main_tex_pathid(mat_obj)
        if tpid and tpid in by_id:
            try:
                img = by_id[tpid].read().image
                if img:
                    img.save(os.path.join(OUT, f"{iid}.png"))
                    tex_name = f"{iid}.png"; n_tex += 1
            except Exception:
                pass
        if tex_name is None:
            flat = mat_color(mat_obj) or next((mat_color(p[2]) for p in parts if mat_color(p[2])), None)
    if len([p for p in pvc if p>0]) > 1: n_multi += 1
    manifest[iid_s] = {"name": name, "type": typ, "obj": f"{iid}.obj",
                       "tex": tex_name, "color": flat, "box": [round(b,4) for b in box],
                       "center": [round(c,4) for c in center], "parts": len(parts)}
    n_ok += 1
    report.append((iid, name, typ, "ok", len(parts), max(box)))

json.dump(manifest, open(os.path.join(OUT, "items_manifest.json"), "w"), indent=0)
print(f"\n=== DONE: ok={n_ok} tex={n_tex} multipart={n_multi} fail={n_fail} / {len(loot)} ===")
print("failures / multipart / oversize:")
for iid,name,typ,st,npart,mx in report:
    if st!="ok" or npart>1 or mx>4:
        print(f"  id={iid:5} {name[:22]:22} {typ:10} {st:12} parts={npart} maxdim={mx:.2f}")
