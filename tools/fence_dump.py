import UnityPy, os
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}

def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID"))
        if co: yield co
def comp_of(tt, names):
    for co in comps(tt):
        if co.type.name in names: return co
    return None
def walk(go_pid, indent):
    go = by_id.get(go_pid)
    if not go: return
    tt = go.read_typetree(); nm = tt.get("m_Name", "")
    tr = comp_of(tt, ("Transform",))
    if not tr: return
    trt = tr.read_typetree(); lp = trt["m_LocalPosition"]; ls = trt["m_LocalScale"]
    meshnm = ""
    mf = comp_of(tt, ("MeshFilter",))
    if mf:
        mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID")
        if mp in by_id:
            try: meshnm = by_id[mp].read_typetree().get("m_Name", "")
            except Exception: pass
    print(f"{'  '*indent}{nm}  localpos=({lp['x']:.3f},{lp['y']:.3f},{lp['z']:.3f})  scale=({ls['x']:.2f},{ls['y']:.2f},{ls['z']:.2f})  mesh={meshnm}")
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), indent + 1)

for want in ["fence_wood_0", "fence_wood_3"]:
    prefab = None
    for path, obj in env.container.items():
        if obj.type.name == "GameObject" and f"{want}/object.prefab" in path.lower():
            prefab = obj; break
    print(f"===== {want} =====")
    if prefab: walk(prefab.path_id, 0)
    else: print("  NOT FOUND")
    print()
