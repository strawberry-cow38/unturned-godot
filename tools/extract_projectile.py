#!/usr/bin/env python3
"""extract_projectile.py [PREFAB_HINT] [OUTNAME] -- rip a prefab's Model_0 mesh + albedo from core.masterbundle.
Same mesh/albedo pipeline as extract_gun.py but for ANY item/projectile/throwable prefab (finds the child named
'Model_0' under whatever root -- 'Projectile', 'Item', ...). Defaults to the rocket launcher's projectile.
  extract_projectile.py "launcher_rocket/projectile.prefab" rocket_projectile
  extract_projectile.py "throwables/grenade/item.prefab" grenade
-> <OUTNAME>.txt (mesh, Unturned->Godot: Z-neg + winding reverse) + <OUTNAME>_albedo.png (if the material has _MainTex)."""
import UnityPy, sys, os
env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
by_id = {o.path_id: o for o in env.objects}
PREFAB_HINT = sys.argv[1] if len(sys.argv) > 1 else "launcher_rocket/projectile.prefab"
OUTNAME = sys.argv[2] if len(sys.argv) > 2 else "rocket_projectile"
OUTDIR = r"C:\claude-workspace\gunout"
os.makedirs(OUTDIR, exist_ok=True)

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
               if PREFAB_HINT in p.lower() and o.type.name == "GameObject"), None)
if not prefab:
    print("NO prefab for", PREFAB_HINT); sys.exit(1)
tr = comp_of(prefab.read_typetree(), ("Transform",))
model0 = None
for ch in tr.read_typetree().get("m_Children", []):
    ct = by_id.get(ch.get("m_PathID"))
    if not ct: continue
    ctt = ct.read_typetree()
    cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
    if cgo and cgo.read_typetree().get("m_Name") == "Model_0":
        model0 = cgo
if not model0:
    print("NO Model_0 in", PREFAB_HINT); sys.exit(1)
m0tt = model0.read_typetree()
mf = comp_of(m0tt, ("MeshFilter",))
mesh = by_id.get(mf.read_typetree().get("m_Mesh", {}).get("m_PathID")) if mf else None
if not mesh:
    print("NO mesh"); sys.exit(1)
txt = mesh.read().export()
Vs, Ns, Ts, Fs = [], [], [], []
for line in txt.splitlines():
    p = line.split()
    if not p: continue
    if p[0] == "v": Vs.append((float(p[1]), float(p[2]), -float(p[3])))
    elif p[0] == "vn": Ns.append((float(p[1]), float(p[2]), -float(p[3])))
    elif p[0] == "vt": Ts.append((p[1], p[2]))
    elif p[0] == "f":
        idx = []
        for tok in p[1:]:
            q = tok.split("/")
            idx.append((int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)))
        Fs.append(list(reversed(idx)))
L = ["# %s Model_0 -> Godot (Z negated + winding reversed)" % OUTNAME]
L += ["v %.6f %.6f %.6f" % v for v in Vs]
L += ["vt %s %s" % t for t in Ts]
L += ["vn %.6f %.6f %.6f" % n for n in Ns]
for f in Fs:
    s = "f"
    for (vi, ti, ni) in f:
        s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
    L.append(s)
open(os.path.join(OUTDIR, OUTNAME + ".txt"), "w").write("\n".join(L) + "\n")
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
                    to.read().image.convert("RGBA").save(os.path.join(OUTDIR, OUTNAME + "_albedo.png"))
                    alb = OUTNAME + "_albedo.png"
        # also report the flat _Color (for meshes with no _MainTex, like the rocket/grenade)
        for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_Colors", []):
            nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
            if nm == "_Color" and isinstance(val, dict):
                print("COLOR _Color %.3f %.3f %.3f" % (val["r"], val["g"], val["b"]))
print("PREFAB", OUTNAME, "verts", len(Vs), "tris", len(Fs), "albedo", alb)
