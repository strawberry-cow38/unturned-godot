"""Extract retail LOD/cull data for RESOURCES (trees, bushes, rocks) -- the other half of "LODs from source".

Props got this treatment in extract_lods.py; resources were still culled by two hand-picked numbers in
ResourceField (320m for trees, 180m for everything else). Retail governs them by the same two rules:

  1. render layer -- LayerMasks.RESOURCE = defaultCullDistance = 256 + drawDistance*256 -> [256, 512]
  2. the asset's own Unity LODGroup, converted the way LODUtility does:
         distance = size / (2 * screenRelativeHeight * tan(fov/2)) * lodBias      lodBias = [2,5]

For a TREE the LODGroup never binds -- a 21m birch computes to ~3000m, so the 512m layer cull is what
actually stops it. For a small bush or rock the reverse is true. Hence both rules, tighter wins, exactly
as the props path does.

Resources live at Bundles/Trees/<Name>/ in the .dat tree and
assets/coremasterbundle/trees/<name>/resource.prefab in core.masterbundle. Only `resource.prefab` is the
live asset -- each tree also ships debris/stump/skybox prefabs (skybox is retail's distant-billboard
system, which this port does not implement) and those must not be sampled instead.

Output: game/content/resources/lods.txt
    <Name> <size> <h0>[,<h1>[,<h2>]]
'-' heights = no LODGroup; the runtime falls back to the layer cull alone.

Run:  python3 tools/extract_resource_lods.py
"""
import UnityPy, os, sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = os.path.join(ug_paths.REPO, "game", "content", "resources")

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
cont = dict(env.container.items())


def comps(tt):
    for c in tt.get("m_Component", []):
        cc = c.get("component", c)
        o = by_id.get(cc.get("m_PathID") if isinstance(cc, dict) else None)
        if o:
            yield o


def find_lodgroup(go, depth=0):
    if not go or depth > 10:
        return None
    tt = go.read_typetree()
    kids = []
    for co in comps(tt):
        if co.type.name == "LODGroup":
            return co
        if co.type.name == "Transform":
            kids = co.read_typetree().get("m_Children", [])
    for ch in kids:
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            r = find_lodgroup(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")), depth + 1)
            if r:
                return r
    return None


rows, levels, missing = [], Counter(), 0
for path, obj in sorted(cont.items()):
    p = path.lower()
    if not (p.startswith("assets/coremasterbundle/trees/") and p.endswith("/resource.prefab")):
        continue
    if obj.type.name != "GameObject":
        continue
    name = path.split("/")[-2]
    lg = find_lodgroup(obj)
    if not lg:
        missing += 1
        rows.append((name, 0.0, []))
        continue
    d = lg.read_typetree()
    size = float(d.get("m_Size", 0.0))
    hs = []
    for l in d.get("m_LODs", []):
        h = l.get("screenRelativeHeight", l.get("screenRelativeTransitionHeight"))
        if h is not None:
            hs.append(round(float(h), 5))
    levels[len(hs)] += 1
    rows.append((name, round(size, 3), hs))

os.makedirs(OUT, exist_ok=True)
dst = os.path.join(OUT, "lods.txt")
with open(dst, "w") as f:
    f.write("# name size h0[,h1[,h2]] -- retail LODGroup for RESOURCES, via tools/extract_resource_lods.py\n")
    f.write("# effective cull = min(RESOURCE layer 256-512m, size/(2*h_last*tan(fov/2))*lodBias). '-' = no LODGroup.\n")
    for name, size, hs in rows:
        f.write(f"{name} {size} {','.join(str(h) for h in hs) if hs else '-'}\n")

print(f"[reslod] wrote {len(rows)} resources -> {dst}")
print(f"[reslod] levels: {dict(sorted(levels.items()))} | no LODGroup: {missing}")
