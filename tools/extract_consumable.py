#!/usr/bin/env python3
"""extract_consumable.py <ItemName> [OUTDIR] -- rip a consumable's 1st-person held mesh + albedo from
core.masterbundle. Same viewmodel convention as extract_gun.py (Model_0, negate X+Z, winding reverse,
so it holds correctly in-hand). Category-agnostic (finds items/food|medical|.../<name>/item.prefab).
Outputs <name>.txt (Model_0 mesh) + <name>_albedo.png (Model_0 _MainTex)."""
import UnityPy, sys, os

env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
by_id = {o.path_id: o for o in env.objects}
NAME = sys.argv[1]
OUTDIR = sys.argv[2] if len(sys.argv) > 2 else r"C:\claude-workspace\consumeout"
os.makedirs(OUTDIR, exist_ok=True)
nl = NAME.lower()

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def pptr(v):
    return by_id.get(v.get("m_PathID")) if isinstance(v, dict) else None

prefab = next((o for p, o in env.container.items()
               if p.lower().endswith("/" + nl + "/item.prefab") and o.type.name == "GameObject"), None)
if not prefab:
    print("NO item.prefab for", NAME); sys.exit(1)
item_tr = comp_of(prefab.read_typetree(), ("Transform",))
model0_go = None
for ch in item_tr.read_typetree().get("m_Children", []):
    ct = by_id.get(ch.get("m_PathID"))
    if not ct:
        continue
    cgo = by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
    if cgo and cgo.read_typetree().get("m_Name") == "Model_0":
        model0_go = cgo
if not model0_go:
    print("NO Model_0 for", NAME); sys.exit(1)
m0tt = model0_go.read_typetree()

mf = comp_of(m0tt, ("MeshFilter",))
mesh = by_id.get(mf.read_typetree().get("m_Mesh", {}).get("m_PathID")) if mf else None
if not mesh:
    print("NO Model_0 mesh for", NAME); sys.exit(1)
txt = mesh.read().export()
Vs, Ns, Ts, Fs = [], [], [], []
for line in txt.splitlines():
    p = line.split()
    if not p:
        continue
    if p[0] == "v":
        Vs.append((-float(p[1]), float(p[2]), -float(p[3])))   # negate X + Z (gun viewmodel convention)
    elif p[0] == "vn":
        Ns.append((-float(p[1]), float(p[2]), -float(p[3])))
    elif p[0] == "vt":
        Ts.append((p[1], p[2]))
    elif p[0] == "f":
        idx = []
        for tok in p[1:]:
            q = tok.split("/")
            idx.append((int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)))
        Fs.append(list(reversed(idx)))
L = ["# Model_0 (%s consumable rip -> Godot, X+Z negated + winding reversed)" % NAME]
L += ["v %.6f %.6f %.6f" % v for v in Vs]
L += ["vt %s %s" % t for t in Ts]
L += ["vn %.6f %.6f %.6f" % n for n in Ns]
for f in Fs:
    s = "f"
    for (vi, ti, ni) in f:
        s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
    L.append(s)
open(os.path.join(OUTDIR, nl + ".txt"), "w").write("\n".join(L) + "\n")

alb = "NONE"
mr = comp_of(m0tt, ("MeshRenderer",))
if mr:
    mats = mr.read_typetree().get("m_Materials", [])
    mo = pptr(mats[0]) if mats else None
    if mo:
        for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
            nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
            if nm == "_MainTex" and isinstance(val, dict):
                to = pptr(val.get("m_Texture", {}))
                if to:
                    to.read().image.convert("RGBA").save(os.path.join(OUTDIR, nl + "_albedo.png"))
                    alb = nl + "_albedo.png"
print(f"{NAME}: {len(Vs)} verts, {len(Fs)} tris, albedo={alb}")
