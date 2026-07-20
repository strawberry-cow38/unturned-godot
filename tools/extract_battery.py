#!/usr/bin/env python3
"""extract_battery.py -- rip the Vehicle Battery (item 1450) WORLD model + albedo into content/objects/
so the placeable Battery deployable can drop the ProcBox gray-box for the real mesh. Reuses extract_objects_v2's
LOD0 / world-transform / X-negate convention (verbatim) so it matches Generator_0 / Gas_Pump_0. Item prefab is
found by container path (like extract_gascan.py), NOT the Bundles/Objects GUID map (a battery is an item, not a
placed object). Filters to Model_0 (LOD0) only -- rendering Model_0+Model_1 = the candy-bar pink-rect bug."""
import UnityPy, os, numpy as np, sys

env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
by_id = {o.path_id: o for o in env.objects}
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
TARGET = "Battery_0"

# ---- find the battery item prefab by container path ----
cands = [(p, o) for p, o in env.container.items()
         if o.type.name == "GameObject" and "batter" in p.lower() and p.lower().endswith("item.prefab")]
print("battery item.prefab candidates:", [p for p, _ in cands])
if not cands:
    # fallback: any container path mentioning battery (help me find the folder name)
    hint = sorted({p for p in env.container if "batter" in p.lower()})
    print("no item.prefab; paths mentioning 'batter':", hint[:40])
    sys.exit(1)
prefab = cands[0][1]

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
def walk(go_pid, parentM, gomap):
    go = by_id.get(go_pid)
    if not go: return
    tt = go.read_typetree()
    nm = (tt.get("m_Name", "") or "")
    if nm.lower() in {"dead", "ragdoll", "effect", "nav", "block", "trap"}: return
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    mf = comp_of(tt, ("MeshFilter",))
    mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
    gomap[go_pid] = (M, mp, nm)
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M, gomap)

rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
root_local = trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])
gomap = {}; walk(prefab.path_id, np.linalg.inv(root_local), gomap)
print("meshes in prefab:", [(nm, mp) for gp, (M, mp, nm) in gomap.items() if mp])

# LOD0 only: prefer GameObjects named Model_0; else the item root mesh; else any single mesh
model0 = [gp for gp, (M, mp, nm) in gomap.items() if mp and nm.lower() == "model_0"]
if model0:
    pick = model0
else:
    pick = [gp for gp, (M, mp, nm) in gomap.items() if mp and "model_1" not in nm.lower()]
print("picked GOs:", [gomap[gp][2] for gp in pick])

Vs, Ns, Ts, Fs, used = [], [], [], [], []
for gp in pick:
    M, mp, nm = gomap[gp]
    if not mp or mp not in by_id: continue
    M = M.copy(); M[0, 3] = -M[0, 3]
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

# ---- albedo: biggest Texture2D reachable from the picked meshes' materials ----
best = None
for gp in pick:
    go = by_id.get(gp)
    if not go: continue
    mr = comp_of(go.read_typetree(), ("MeshRenderer", "SkinnedMeshRenderer"))
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
                    if not best or area > best[0]: best = (area, img, name)
                except Exception: pass
if best:
    best[1].save(os.path.join(OUT, TARGET + "_tex.png"))
    print("wrote", TARGET + "_tex.png", best[1].width, "x", best[1].height, "prop", best[2])
else:
    print("NO TEXTURE found")
