"""Extract retail LOD data (Unity LODGroup) for every prop PEI actually places.

extract_objects_v2.py already walks GUID -> object.prefab -> LODGroup, but it only keeps LOD0's
renderer list and throws the LOD data itself away -- so the port renders every prop at full detail
at every distance, forever. Retail authors this per prop: 5271 LODGroups ship in core.masterbundle
(3646 with a single level, 1433 with two, 192 with three).

What a LODGroup carries, and what it means:
  m_Size    the group's world-space size (bounding-sphere diameter) at unit scale
  m_LODs[]  each with screenRelativeHeight = the fraction of the SCREEN HEIGHT this object must
            cover to still be drawn at that level. Below the LAST entry, Unity culls it entirely.

So a single-level group is not "no LOD" -- it is a pure CULL threshold, which is the majority case
and the biggest win: small props stop rendering once they are a few pixels tall.

Screen-relative height converts to a world distance the same way Unity's LODUtility does:

    distance = size / (2 * screenRelativeHeight * tan(fov / 2))

The port applies that at load with its own live camera FOV (see LodTable.cs), so the numbers stay
faithful if the FOV changes -- which is why this file emits SIZE + HEIGHTS and not baked distances.

But the LODGroup is NOT the primary cull. Retail's dominant rule is a per-RENDER-LAYER distance
(GraphicsSettings.layerCullDistances), and the Objects/{Large,Medium,Small} bundle folder a prop
ships in IS that layer:

    defaultCullDistance = 256 + normalizedDrawDistance * 256     -> [256, 512]
    LARGE  = defaultCullDistance      MEDIUM = x0.5      SMALL = x0.125

so a small prop dies at 32-64m no matter what its LODGroup says. Both rules ship here; the runtime
takes the tighter of the two, which is what Unity does (layer cull and LOD cull are independent).

Output: game/content/objects/lods.txt, one line per GUID:
    <guid> <name> <LARGE|MEDIUM|SMALL> <size> <h0>[,<h1>[,<h2>]]
Props with no LODGroup are emitted with size 0 and no heights -- the runtime falls back to the layer
cull for those rather than inventing a per-prop threshold.

Run:  python3 tools/extract_lods.py
"""
import UnityPy, os, glob, re, sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()
PLACEMENTS = os.path.join(OUT, "placements.txt")

# GUIDs PEI actually places -- no point carrying LOD data for props that never appear.
placed = Counter()
with open(PLACEMENTS, "r") as f:
    for line in f:
        p = line.split()
        if p:
            placed[p[0].lower()] += 1
print(f"[lods] {len(placed)} distinct GUIDs across {sum(placed.values())} placements")

# GUID -> prop dir name + prefab container path (same derivation extract_objects_v2 uses, so the
# names line up with the .obj files already on disk).
guid2info = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try:
        txt = open(datp, "r", errors="ignore").read()
    except Exception:
        continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m:
        continue
    rel = os.path.relpath(os.path.dirname(datp), BUND).replace("\\", "/").lower()
    # The Objects/{Large,Medium,Small} folder IS the prop's render layer, and retail's PRIMARY cull is
    # per-layer, not per-prop: LARGE = defaultCullDistance, MEDIUM = *0.5, SMALL = *0.125
    # (GraphicsSettings layerCullDistances). Carry it through -- the LODGroup distance is capped by it.
    parts = rel.split("/")
    cat = parts[1].upper() if len(parts) > 1 and parts[0] == "objects" else "MEDIUM"
    if cat not in ("LARGE", "MEDIUM", "SMALL"):
        cat = "MEDIUM"   # Mooki/Promo and anything unexpected: retail's middle layer, not a guess at zero
    guid2info[m.group(1).lower()] = (os.path.basename(os.path.dirname(datp)),
                                     "assets/coremasterbundle/" + rel + "/object.prefab", cat)

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
prefabs = {}
for path, obj in env.container.items():
    if obj.type.name == "GameObject" and path.lower().endswith("/object.prefab"):
        prefabs[path.lower()] = obj


def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co:
            yield co


def find_lodgroup(go, depth=0):
    """The LODGroup is not always on the prefab root -- props nest it under a child."""
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


rows, missing_prefab, no_lodgroup = [], 0, 0
levels = Counter()
for guid in sorted(placed):
    info = guid2info.get(guid)
    if not info:
        missing_prefab += 1
        continue
    name, path, cat = info
    prefab = prefabs.get(path)
    if not prefab:
        missing_prefab += 1
        continue
    lg = find_lodgroup(prefab)
    if not lg:
        no_lodgroup += 1
        rows.append((guid, name, cat, 0.0, []))
        continue
    d = lg.read_typetree()
    size = float(d.get("m_Size", 0.0))
    hs = []
    for l in d.get("m_LODs", []):
        h = l.get("screenRelativeHeight", l.get("screenRelativeTransitionHeight"))
        if h is not None:
            hs.append(round(float(h), 5))
    levels[len(hs)] += 1
    rows.append((guid, name, cat, round(size, 3), hs))

os.makedirs(OUT, exist_ok=True)
dst = os.path.join(OUT, "lods.txt")
with open(dst, "w") as f:
    f.write("# guid name layer size h0[,h1[,h2]] -- retail LODGroup + render layer, via tools/extract_lods.py\n")
    f.write("# distance = size/(2*h*tan(fov/2)) * lodBias, CAPPED by the layer cull (LARGE 256-512m, MEDIUM x0.5, SMALL x0.125).\n")
    f.write("# last h = cull threshold; earlier h = mesh-swap points. size 0 / '-' = no LODGroup, runtime uses the layer cull alone.\n")
    for guid, name, cat, size, hs in rows:
        f.write(f"{guid} {name} {cat} {size} {','.join(str(h) for h in hs) if hs else '-'}\n")

print(f"[lods] wrote {len(rows)} rows -> {dst}")
print(f"[lods] levels per prop: {dict(sorted(levels.items()))}")
print(f"[lods] no LODGroup: {no_lodgroup} | unresolved prefab: {missing_prefab}")
from collections import Counter as _C
print('[lods] by render layer:', dict(_C(r[2] for r in rows)))
