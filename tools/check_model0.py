#!/usr/bin/env python3
"""check_model0.py [gun...] -- print each gun's Model_0 LOCAL rotation+position under the Item prefab.
extract_gun.py assumes Model_0 is at identity; if pistols have a non-identity rotation, that's the tilt bug."""
import UnityPy, sys
env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
by_id = {o.path_id: o for o in env.objects}
def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names: return co
    return None
for gun in (sys.argv[1:] or ["cobra", "colt", "ace", "desert_falcon", "avenger", "eaglefire", "augewehr"]):
    prefab = next((o for p, o in env.container.items() if f"/guns/{gun}/item.prefab" in p.lower() and o.type.name == "GameObject"), None)
    if not prefab: print(f"{gun:14} NO PREFAB"); continue
    tr = comp_of(prefab.read_typetree(), ("Transform",))
    found = False
    for ch in tr.read_typetree().get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct: continue
        ctt = ct.read_typetree(); cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if cgo and cgo.read_typetree().get("m_Name") == "Model_0":
            r = ctt.get("m_LocalRotation", {}); p = ctt.get("m_LocalPosition", {})
            print(f"{gun:14} rot=({r.get('x',0):+.3f},{r.get('y',0):+.3f},{r.get('z',0):+.3f},{r.get('w',1):+.3f})  pos=({p.get('x',0):+.3f},{p.get('y',0):+.3f},{p.get('z',0):+.3f})")
            found = True; break
    if not found: print(f"{gun:14} NO Model_0")
