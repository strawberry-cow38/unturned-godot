#!/usr/bin/env python3
"""batch_consumables.py -- extract the held mesh + albedo for EVERY food/medical consumable in core.masterbundle.
Held mesh = the Model_0 child (food/most items) OR, if there's no Model_0, the ROOT Item node's own MeshFilter
(8 medical items -- medkit/morphine/etc. store the held mesh on the root, not a child).
Output: <OUT>/<name>.txt + <name>_albedo.png + consumables.txt (name list)."""
import UnityPy, os, re
CORE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
OUT = r"C:\claude-workspace\consumeout"
os.makedirs(OUT, exist_ok=True)
env = UnityPy.load(CORE)
by_id = {o.path_id: o for o in env.objects}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None
def pptr(v):
    return by_id.get(v.get("m_PathID")) if isinstance(v, dict) else None

def rip(name, prefab):
    tr = comp_of(prefab.read_typetree(), ("Transform",))
    m0 = None
    for ch in (tr.read_typetree().get("m_Children", []) if tr else []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct: continue
        cgo = by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
        if cgo and cgo.read_typetree().get("m_Name") == "Model_0":
            m0 = cgo
    src = m0 if m0 else prefab   # no Model_0 child -> the held mesh is on the root Item node (medkit-style)
    src_tt = src.read_typetree()
    mf = comp_of(src_tt, ("MeshFilter",))
    mesh = by_id.get(mf.read_typetree().get("m_Mesh", {}).get("m_PathID")) if mf else None
    if not mesh: return False
    txt = mesh.read().export(); Vs, Ns, Ts, Fs = [], [], [], []
    for line in txt.splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v": Vs.append((-float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vn": Ns.append((-float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vt": Ts.append((p[1], p[2]))
        elif p[0] == "f":
            idx = [(int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)) for tok in p[1:] for q in [tok.split("/")]]
            Fs.append(list(reversed(idx)))
    L = ["# %s consumable held mesh (X+Z neg + winding rev)" % name]
    L += ["v %.6f %.6f %.6f" % v for v in Vs] + ["vt %s %s" % t for t in Ts] + ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
        L.append(s)
    open(os.path.join(OUT, name + ".txt"), "w").write("\n".join(L) + "\n")
    mr = comp_of(src_tt, ("MeshRenderer",))
    if mr:
        mats = mr.read_typetree().get("m_Materials", [])
        mo = pptr(mats[0]) if mats else None
        if mo:
            for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
                nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
                if nm == "_MainTex" and isinstance(val, dict):
                    to = pptr(val.get("m_Texture", {}))
                    if to:
                        try: to.read().image.convert("RGBA").save(os.path.join(OUT, name + "_albedo.png"))
                        except Exception: pass
    return True

seen = set(); names = []; root_meshed = []
for path, o in env.container.items():
    m = re.search(r"items/(food|medical|water|refills)/([^/]+)/item\.prefab$", str(path).lower())
    if m and o.type.name == "GameObject" and m.group(2) not in seen:
        nm = m.group(2); seen.add(nm)
        if rip(nm, o): names.append(nm)
open(os.path.join(OUT, "consumables.txt"), "w").write("\n".join(sorted(names)) + "\n")
print(f"extracted {len(names)}/{len(seen)} consumables")
