#!/usr/bin/env python3
"""extract_otter_tex.py -- pull the Otter floatplane's albedo atlas out of the vehicle prefab.
Walks the Otter prefab tree, collects every MeshRenderer material's Texture2D, saves the biggest
(the body albedo) as game/content/otter_body_tex.png. Same texture logic as extract_object_named.py."""
import UnityPy, os
_BUNDLE = os.environ.get("UG_MASTERBUNDLE") or r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(_BUNDLE)
by_id = {o.path_id: o for o in env.objects}
OUT = r"C:\claude-workspace\unturned-godot\game\content"

def tt(o):
    try: return o.read_typetree()
    except Exception: return {}
def comp_of(t, names):
    for comp in t.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names: return co
    return None

# ---- find the Otter prefab root
root = None
for p, o in env.container.items():
    if o.type.name == "GameObject" and "otter" in p.lower() and tt(o).get("m_Name", "").lower() == "otter":
        root = o; break
if not root:
    for p, o in env.container.items():
        if o.type.name == "GameObject" and "otter" in p.lower(): root = o; break
if not root:
    print("NO OTTER PREFAB"); raise SystemExit

# ---- collect every GameObject in the tree
gos = []
def walk(pid):
    go = by_id.get(pid)
    if not go: return
    gos.append(go)
    tr = comp_of(tt(go), ("Transform", "RectTransform"))
    for ch in (tt(tr).get("m_Children") or []):
        cht = by_id.get(ch.get("m_PathID"))
        if cht: walk(tt(cht).get("m_GameObject", {}).get("m_PathID"))
walk(root.path_id)

# ---- biggest Texture2D reachable from any mesh renderer's materials
best = None; seen = []
for go in gos:
    mr = comp_of(tt(go), ("MeshRenderer", "SkinnedMeshRenderer"))
    if not mr: continue
    for matref in tt(mr).get("m_Materials", []):
        mat = by_id.get(matref.get("m_PathID"))
        if not mat: continue
        for entry in tt(mat).get("m_SavedProperties", {}).get("m_TexEnvs", []):
            if isinstance(entry, (list, tuple)) and len(entry) == 2: name, env_ = entry
            elif isinstance(entry, dict): name, env_ = entry.get("first", entry.get("Key", "?")), entry.get("second", entry.get("Value", {}))
            else: continue
            if not isinstance(env_, dict): continue
            tex = by_id.get((env_.get("m_Texture") or {}).get("m_PathID"))
            if tex and tex.type.name == "Texture2D":
                try:
                    img = tex.read().image; area = img.width * img.height
                    seen.append((tt(mat).get("m_Name", "?"), name, img.width, img.height))
                    if not best or area > best[0]: best = (area, img, name, tt(mat).get("m_Name", "?"))
                except Exception: pass
print("materials/textures seen:", seen)
if best:
    best[1].save(os.path.join(OUT, "otter_body_tex.png"))
    print("wrote otter_body_tex.png", best[1].width, "x", best[1].height, "prop", best[2], "mat", best[3])
else:
    print("NO TEXTURE found")
