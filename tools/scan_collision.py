import UnityPy, os, glob, re
from collections import Counter
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"

# rendered props (have an extracted mesh)
rendered = {}
for l in open(os.path.join(OUT, "guid_mesh.txt")):
    p = l.split()
    if len(p) >= 2: rendered[p[0]] = p[1]

# guid -> object.prefab container path (same convention as extract_objects_v2)
guid2cont = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    guid2cont[m.group(1).lower()] = "assets/coremasterbundle/" + rel + "/object.prefab"

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
prefabs = {}
for path, obj in env.container.items():
    if obj.type.name == "GameObject" and path.lower().endswith("/object.prefab"):
        prefabs[path.lower()] = obj

COLL = {"MeshCollider", "BoxCollider", "CapsuleCollider", "SphereCollider", "WheelCollider"}
def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co: yield co
def go_name(tt): return (tt.get("m_Name", "") or "")
def has_player_collider(prefab):
    # walk hierarchy; a Collider on a GameObject NOT named Nav/Clip/Trigger = player/world collision
    stack = [(prefab.path_id, False)]; seen = set()
    while stack:
        pid, undernav = stack.pop()
        if pid in seen: continue
        seen.add(pid)
        go = by_id.get(pid)
        if not go: continue
        try: tt = go.read_typetree()
        except Exception: continue
        nm = go_name(tt).lower()
        skip = undernav or ("nav" in nm) or ("clip" in nm) or ("trigger" in nm)
        for co in comps(tt):
            if (not skip) and co.type.name in COLL:
                return True
            if co.type.name in ("Transform", "RectTransform"):
                try: kids = co.read_typetree().get("m_Children", [])
                except Exception: kids = []
                for ch in kids:
                    ct = by_id.get(ch.get("m_PathID"))
                    if ct:
                        try: gp = ct.read_typetree().get("m_GameObject", {}).get("m_PathID")
                        except Exception: gp = None
                        if gp: stack.append((gp, skip))
    return False

nocoll = []; checked = 0
for g, name in rendered.items():
    pref = prefabs.get(guid2cont.get(g, ""))
    if not pref: continue
    checked += 1
    if not has_player_collider(pref):
        nocoll.append(g)
with open(os.path.join(OUT, "no_collision.txt"), "w") as f:
    for g in sorted(nocoll): f.write(g + "\n")
print(f"checked {checked} rendered props; {len(nocoll)} have NO player collider -> no_collision.txt")
print("NO-collision examples:", [n for n, _ in Counter(rendered[g] for g in nocoll).most_common(25)])
print("HAS-collision examples:", [rendered[g] for g in list(rendered)[:400] if g not in set(nocoll)][:15])
