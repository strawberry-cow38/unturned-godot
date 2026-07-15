import UnityPy, sys, os
_BUNDLE = os.environ.get("UG_MASTERBUNDLE") or next((p for p in (
    os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle"),
    r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle",
) if os.path.exists(p)), None)
env = UnityPy.load(_BUNDLE)
by_id = {o.path_id: o for o in env.objects}

def comps(tt, name):
    out = []
    for c in tt.get("m_Component", []):
        cc = c.get("component", c) if isinstance(c, dict) else c
        co = by_id.get(cc.get("m_PathID") if isinstance(cc, dict) else None)
        if co and co.type.name == name:
            out.append(co)
    return out

sub, out = sys.argv[1], sys.argv[2]
prefab = next(o for p, o in env.container.items() if p.lower().endswith(sub) and o.type.name == "GameObject")

best = [None]  # (vcount, MeshRenderer)
def walk(pid):
    go = by_id.get(pid)
    if not go: return
    tt = go.read_typetree()
    mf, mr = comps(tt, "MeshFilter"), comps(tt, "MeshRenderer")
    if mf and mr:
        mp = mf[0].read_typetree().get("m_Mesh", {}).get("m_PathID")
        if mp in by_id and by_id[mp].read_typetree().get("m_Name") == "Model_0":
            vc = by_id[mp].read_typetree().get("m_VertexData", {}).get("m_VertexCount", 0)
            if best[0] is None or vc > best[0][0]:
                best[0] = (vc, mr[0])
    tr = comps(tt, "Transform")
    if tr:
        for ch in tr[0].read_typetree().get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))

walk(prefab.path_id)
mats = best[0][1].read_typetree().get("m_Materials", [])
mat = by_id[mats[0].get("m_PathID")].read_typetree()
for te in mat.get("m_SavedProperties", {}).get("m_TexEnvs", []):
    if te[0] == "_MainTex":
        tp = (te[1].get("m_Texture") or {}).get("m_PathID")
        img = by_id[tp].read().image
        img.save(out)
        print("saved", out, "= pathID", tp, "name", by_id[tp].read_typetree().get("m_Name"), img.size)
