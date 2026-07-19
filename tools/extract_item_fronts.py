#!/usr/bin/env python3
"""extract_item_fronts.py -- pull each item's icon POSE (front + up axes) from its prefab's "Icon" child.

Unturned renders every item's inventory icon by parenting an "Icon" empty transform in the item prefab and
photographing the item from it (ItemTool.captureIcon: camera pose = Icon.transform; camera views along Icon +Z /
Unity forward, with Icon +Y as screen-up -- see IconUtils.getItemDefIcon -> ItemTool.getIcon `item.Find("Icon")`).
So:
  - the FRONT/detail face (medkit cross, MRE text, OJ label) has normal = -Icon.forward (faces the camera)
  - the item's UP (what's at the top of the icon: tomato stem, bottle cap) = +Icon.up (camera up)
Reproducing that pose on a shelf gives right-side-up, aisle-facing items straight from the game data -- no
hand-defining stand/lie/yaw for hundreds of items. Convention matches extract_items.py (Unturned->Godot: negate Z).

Output: item_poses.json { "<id>": {"f":[fx,fy,fz], "u":[ux,uy,uz]} } (Godot frame, unit). No mesh re-bake.
"""
import UnityPy, numpy as np, os, json

U = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
CORE = os.path.join(U, r"Bundles\core.masterbundle")
OUT = r"C:\claude-workspace\item_out"
os.makedirs(OUT, exist_ok=True)
loot = json.load(open(r"C:\claude-workspace\pei_loot_items.json"))["resolved"]
# master's art-direction callouts -> print for eyeball validation
KEYS = ("tomato", "orange", "juice", "maple", "syrup", "blowtorch", "tuna", "sardine", "bacon", "carrot",
        "potato", "wheat", "egg", "lettuce", "corn", "bread", "candy", "chips", "mre", "medkit", "bandage",
        "water", "bean", "soda", "cola", "milk")

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

def unit_godot(v):
    n = np.linalg.norm(v)
    if n > 1e-6: v = v / n
    return [round(float(v[0]), 4), round(float(v[1]), 4), round(float(-v[2]), 4)]   # Unity dir -> Godot (negate Z)

def icon_pose(prefab):
    """(front, up) Godot-frame unit vectors from the 'Icon' child camera pose, or None if no Icon child."""
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
            R = M[:3, :3]
            front = unit_godot(-(R @ np.array([0.0, 0.0, 1.0])))   # -camera-forward = shown/front face
            up = unit_godot(R @ np.array([0.0, 1.0, 0.0]))          # camera-up = icon's up (top of the thumbnail)
            found[0] = {"f": front, "u": up}
            return
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)
    walk(prefab.path_id, inv_root)
    return found[0]

poses = {}
n_ok = n_none = n_fail = 0
for iid_s, meta in loot.items():
    iid = int(iid_s); nm = meta["name"]
    pref = prefix_of(meta["folder"])
    prefab = go_by_path.get(pref) if pref else None
    if prefab is None:
        n_fail += 1; continue
    try:
        p = icon_pose(prefab)
    except Exception as e:
        n_fail += 1
        if any(k in nm.lower() for k in KEYS): print(f"  id={iid} {nm} ERR {e}")
        continue
    if p is None:
        n_none += 1
        if any(k in nm.lower() for k in KEYS): print(f"  id={iid} {nm:24} NO_ICON_CHILD")
        continue
    poses[iid_s] = p
    n_ok += 1
    if any(k in nm.lower() for k in KEYS):
        print(f"  id={iid:5} {nm[:24]:24} f={p['f']} u={p['u']}")

json.dump(poses, open(os.path.join(OUT, "item_poses.json"), "w"), indent=0)
print(f"\n=== DONE: poses={n_ok} no_icon={n_none} fail={n_fail} / {len(loot)} -> {OUT}\\item_poses.json ===")
