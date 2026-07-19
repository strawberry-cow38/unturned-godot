#!/usr/bin/env python3
"""extract_item_fronts.py -- pull each item's "front" (detail-face normal) from its prefab's "Icon" child.

Unturned renders every item's inventory icon by parenting an "Icon" empty transform in the item prefab and
photographing the item from it (ItemTool.captureIcon: camera pose = Icon.transform; camera views the item along
Icon's +Z / Unity forward -- see IconUtils.getItemDefIcon -> ItemTool.getIcon -> `icon = item.Find("Icon")`).
So the face the icon shows -- the item's "front"/detail side (medkit cross, MRE text) -- has its normal pointing
BACK at the camera = -Icon.forward. That's the per-item "front" the store shelf needs to keep detail-side-up when
laying items flat, straight from the game data instead of hand-defining hundreds of items.

Convention matches extract_items.py's mesh rip (Unturned->Godot: negate Z), so the normal drops straight into the
port's item-mesh frame. Output: item_fronts.json { "<id>": [nx,ny,nz] } (Godot frame, unit). No mesh re-bake.
"""
import UnityPy, numpy as np, os, json

U = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
CORE = os.path.join(U, r"Bundles\core.masterbundle")
OUT = r"C:\claude-workspace\item_out"
os.makedirs(OUT, exist_ok=True)
loot = json.load(open(r"C:\claude-workspace\pei_loot_items.json"))["resolved"]
DEMO = {4, 13, 15, 95, 81, 14}   # store-shelf demo ids -> print for eyeball validation

print("loading core.masterbundle ...", flush=True)
env = UnityPy.load(CORE)
by_id = {o.path_id: o for o in env.objects}
go_by_path = {}
for p, o in env.container.items():
    if o.type.name == "GameObject":
        go_by_path[p.split("assets/coremasterbundle/")[-1]] = o

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co and co.type.name in names:
            return co
    return None

def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w),   2*(x*z+y*w)],
                  [2*(x*y+z*w),   1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w),   2*(y*z+x*w),   1-2*(x*x+y*y)]])
    M = np.eye(4)
    M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]])
    M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

def prefix_of(folder):
    p = folder.replace("\\", "/").lower().split("/bundles/items/")
    return "items/" + p[1] + "/item.prefab" if len(p) > 1 else None

def icon_front(prefab):
    """front normal (Godot frame) from the 'Icon' child camera pose, or None if no Icon child."""
    ptt = prefab.read_typetree()
    root_tr = comp_of(ptt, ("Transform", "RectTransform"))
    root_trt = root_tr.read_typetree()
    root_local = trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"])
    inv_root = np.linalg.inv(root_local)
    found = [None]
    def walk(go_pid, parentM):
        if found[0] is not None: return
        go = by_id.get(go_pid)
        if not go: return
        tt = go.read_typetree()
        tr = comp_of(tt, ("Transform", "RectTransform"))
        if not tr: return
        trt = tr.read_typetree()
        M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
        if tt.get("m_Name") == "Icon":
            viewdir = M[:3, :3] @ np.array([0.0, 0.0, 1.0])   # Unity camera looks +Z
            n = np.linalg.norm(viewdir)
            if n > 1e-6: viewdir = viewdir / n
            detail = -viewdir                                  # face toward camera = the shown/front side (Unity frame)
            found[0] = [round(float(detail[0]), 4), round(float(detail[1]), 4), round(float(-detail[2]), 4)]  # ->Godot (negate Z)
            return
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)
    walk(prefab.path_id, inv_root)
    return found[0]

fronts = {}
n_ok = n_none = n_fail = 0
for iid_s, meta in loot.items():
    iid = int(iid_s)
    pref = prefix_of(meta["folder"])
    prefab = go_by_path.get(pref) if pref else None
    if prefab is None:
        n_fail += 1; continue
    try:
        f = icon_front(prefab)
    except Exception as e:
        n_fail += 1
        if iid in DEMO: print(f"  id={iid} {meta['name']} ERR {e}")
        continue
    if f is None:
        n_none += 1
        if iid in DEMO: print(f"  id={iid} {meta['name']:22} NO_ICON_CHILD")
        continue
    fronts[iid_s] = f
    n_ok += 1
    if iid in DEMO: print(f"  id={iid:5} {meta['name'][:22]:22} front={f}")

json.dump(fronts, open(os.path.join(OUT, "item_fronts.json"), "w"), indent=0)
print(f"\n=== DONE: fronts={n_ok} no_icon={n_none} fail={n_fail} / {len(loot)} -> {OUT}\\item_fronts.json ===")
