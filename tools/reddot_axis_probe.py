"""Authoritative optical axis for each red-dot sight: walk the sight.prefab transform tree and dump every
node (name, LOCAL pos, has-mesh). We want `Aim` (the eye/optical axis the game aligns ADS to -> its X,Z =
ring center) and `Reticule`/`Sight` (the authored dot spot). Model_0 local pos lets us map source->mesh frame."""
import UnityPy, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
try: sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception: pass
BUND = ug_paths.bundles()
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}; cont = env.container

def tt(o): return o.read_typetree()
def comp_of(gtt, names):
    for comp in gtt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names: return co
    return None
def has_mesh(gtt): return comp_of(gtt, ("MeshRenderer","SkinnedMeshRenderer")) is not None
def walk(tr_tt, depth):
    for ch in tr_tt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"));
        if not ct: continue
        ctt = tt(ct); go = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if not go: continue
        gtt = tt(go); lp = ctt.get("m_LocalPosition", {})
        name = gtt.get("m_Name")
        mark = " [MESH]" if has_mesh(gtt) else ""
        print(f"    {'  '*depth}{name}  local=({lp.get('x',0):+.5f},{lp.get('y',0):+.5f},{lp.get('z',0):+.5f})  port(x,y,-z)=({lp.get('x',0):+.5f},{lp.get('y',0):+.5f},{-lp.get('z',0):+.5f}){mark}")
        walk(ctt, depth+1)

for folder in ["red_dot_sight", "red_halo_sight", "red_kobra_sight"]:
    prefab = next((o for pa, o in cont.items() if f"/sights/{folder}/sight.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    print("="*66)
    if not prefab: print(f"{folder}: NO PREFAB"); continue
    gtt = tt(prefab); root_tr = comp_of(gtt, ("Transform",))
    print(f"{folder}: root '{gtt.get('m_Name')}'")
    if root_tr: walk(tt(root_tr), 0)
