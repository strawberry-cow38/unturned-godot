#!/usr/bin/env python3
"""extract_crop_tex.py -- rip each growth-stage mesh's _MainTex (plant billboard / dirt) PNG.

Walks the crop Barricade prefab, and for each top-level child (Model_0/Foliage_0/Foliage_1)
finds its MeshRenderer -> material -> _MainTex -> Texture2D and saves it as a PNG next to the
meshes. The Foliage quads are billboards whose plant SHAPE is in the texture alpha.

Usage: python extract_crop_tex.py <prefab-subpath> <out-base>
"""
import UnityPy, sys

bundle = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(bundle)
by_id = {o.path_id: o for o in env.objects}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co and co.type.name in names:
            return co
    return None

def main_color(mat_tt):
    props = mat_tt.get("m_SavedProperties", {})
    for ce in props.get("m_Colors", []):
        if isinstance(ce, (list, tuple)) and len(ce) == 2:
            name, val = ce
        elif isinstance(ce, dict):
            name, val = ce.get("first"), ce.get("second")
        else:
            continue
        if name in ("_Color", "_MainColor") and isinstance(val, dict):
            return (round(val.get("r", 1), 3), round(val.get("g", 1), 3), round(val.get("b", 1), 3), round(val.get("a", 1), 3))
    return None

def maintex_pid(mat_tt):
    props = mat_tt.get("m_SavedProperties", {})
    for te in props.get("m_TexEnvs", []):
        # te is usually [name, {m_Texture:{m_PathID}}] or {"first":name,"second":{...}}
        if isinstance(te, (list, tuple)) and len(te) == 2:
            name, val = te
        elif isinstance(te, dict):
            name, val = te.get("first"), te.get("second")
        else:
            continue
        if name == "_MainTex" and isinstance(val, dict):
            return val.get("m_Texture", {}).get("m_PathID")
    return None

def save_tex_for(go_pid, outpng):
    go = by_id.get(go_pid)
    if not go:
        return False
    tt = go.read_typetree()
    rend = comp_of(tt, ("MeshRenderer", "SkinnedMeshRenderer"))
    if not rend:
        return False
    mats = rend.read_typetree().get("m_Materials", [])
    for m in mats:
        mo = by_id.get(m.get("m_PathID"))
        if not mo:
            continue
        col = main_color(mo.read_typetree())
        if col:
            print(f"  _Color = {col}")
        tp = maintex_pid(mo.read_typetree())
        to = by_id.get(tp) if tp else None
        if to:
            try:
                img = to.read().image
                img.save(outpng)
                print(f"  saved {to.read_typetree().get('m_Name','?')} -> {outpng} ({img.size})")
                return True
            except Exception as e:
                print(f"  TEX read failed: {e}")
    return False

sub = sys.argv[1]
outbase = sys.argv[2]
prefab = next(o for p, o in env.container.items() if p.lower().endswith(sub) and o.type.name == "GameObject")
root_tr = comp_of(prefab.read_typetree(), ("Transform", "RectTransform"))
for ch in root_tr.read_typetree().get("m_Children", []):
    ct = by_id.get(ch.get("m_PathID"))
    if not ct:
        continue
    cgo_pid = ct.read_typetree().get("m_GameObject", {}).get("m_PathID")
    cgo = by_id.get(cgo_pid)
    cname = cgo.read_typetree().get("m_Name", "?") if cgo else "?"
    print(f"child {cname}:")
    if not save_tex_for(cgo_pid, f"{outbase}_{cname}.png"):
        print("  (no _MainTex)")
