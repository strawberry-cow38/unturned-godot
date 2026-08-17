"""Dump the Otter vehicle prefab's node hierarchy from core.masterbundle -> identify subparts
(body / propeller / wheels / control surfaces) for extraction. Child-tree dump, same idea as
tinyclaw's heli discovery."""
import UnityPy, os, sys
_BUNDLE = os.environ.get("UG_MASTERBUNDLE") or r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(_BUNDLE)
by_id = {o.path_id: o for o in env.objects}

def tt_of(o):
    try: return o.read_typetree()
    except Exception: return {}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def mesh_info(tt):
    mf = comp_of(tt, ("MeshFilter", "SkinnedMeshRenderer"))
    if not mf:
        return None
    mft = tt_of(mf)
    mref = mft.get("m_Mesh")
    mo = by_id.get(mref.get("m_PathID")) if isinstance(mref, dict) else None
    if not mo:
        return None
    md = tt_of(mo)
    vc = md.get("m_VertexData", {}).get("m_VertexCount", "?")
    return (md.get("m_Name", "?"), vc)

# find every GameObject whose container path mentions 'otter'
cands = [(p, o) for p, o in env.container.items() if o.type.name == "GameObject" and ("vehicles/" + sys.argv[1].lower()) in p.lower()]
print("vehicle prefab containers:")
for p, o in cands:
    print("  ", p)
# pick the ROOT: named 'Otter'
root = None
for p, o in cands:
    if tt_of(o).get("m_Name", "").lower() == sys.argv[1].lower():
        root = o; print("ROOT prefab:", p); break
if root is None and cands:
    root = cands[0][1]

def walk(go_pid, depth):
    go = by_id.get(go_pid)
    if not go:
        return
    t = tt_of(go)
    tr = comp_of(t, ("Transform", "RectTransform"))
    trt = tt_of(tr) if tr else {}
    pos = trt.get("m_LocalPosition", {"x": 0, "y": 0, "z": 0})
    mi = mesh_info(t)
    line = "  " * depth + t.get("m_Name", "?") + f"  pos=({pos.get('x',0):.2f},{pos.get('y',0):.2f},{pos.get('z',0):.2f})"
    if mi:
        line += f"   MESH={mi[0]} v={mi[1]}"
    print(line)
    for ch in (trt.get("m_Children") or []):
        cht = by_id.get(ch.get("m_PathID"))
        if cht:
            walk(tt_of(cht).get("m_GameObject", {}).get("m_PathID"), depth + 1)

if root:
    print("=== OTTER TREE ===")
    walk(root.path_id, 0)
else:
    print("Otter root prefab not found")
