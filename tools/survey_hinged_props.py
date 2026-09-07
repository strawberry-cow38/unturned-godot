"""Which OBJECT props have hinged leaves in the source that content/objects/doors.txt does not have?

    python survey_hinged_props.py            # everything
    python survey_hinged_props.py wardrobe   # only prefabs whose container path matches

A hinged leaf is a SkinnedMeshRenderer under an object prefab: extract_doors.py's whole reason for existing is
that a door leaf is a single-bone rig with NO MeshFilter, so the ordinary mesh extractors walk straight past it.
That also means a prop can lose its doors SILENTLY -- the body extracts fine and looks complete, and only the
opening is missing (master 2026-09-07: "some wardrobe variants dont have doors").

Read-only. Prints, per prop: how many leaves the source has, how many lines doors.txt carries, and a verdict.
"""
import UnityPy, os, sys, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
FILTER = (sys.argv[1].lower() if len(sys.argv) > 1 else "")

catalog = {}
cat_path = os.path.join(ug_paths.objects_out(), "doors.txt")
if os.path.isfile(cat_path):
    for ln in open(cat_path):
        p = ln.split()
        if p: catalog[p[0]] = catalog.get(p[0], 0) + 1

bundles = sorted(glob.glob(os.path.join(BUND, "**", "*.unity3d"), recursive=True)
                 + glob.glob(os.path.join(BUND, "**", "*.masterbundle"), recursive=True))
print(f"scanning {len(bundles)} bundles; doors.txt knows {len(catalog)} props")

found = {}
for bp in bundles:
    try: env = UnityPy.load(bp)
    except Exception: continue
    by_id = {o.path_id: o for o in env.objects}
    for path, obj in env.container.items():
        lp = path.lower()
        if not lp.endswith(".prefab") or "/objects/" not in lp: continue
        if FILTER and FILTER not in lp: continue
        if obj.type.name != "GameObject": continue
        # the prop NAME is the folder under objects/, matching how the extractors target it
        seg = [s for s in lp.split("/") if s]
        name = seg[-2] if len(seg) >= 2 else seg[-1]
        leaves = 0
        for o in env.objects:
            if o.type.name != "SkinnedMeshRenderer": continue
            leaves += 1
        if leaves:
            key = name
            found[key] = max(found.get(key, 0), leaves)

for name in sorted(found):
    have = catalog.get(name, 0)
    # match case-insensitively against the catalogue's capitalised prop names
    if not have:
        for k, v in catalog.items():
            if k.lower() == name: have = v; break
    verdict = "ok" if have else "*** NO DOORS IN doors.txt ***"
    print(f"  {name:28s} source leaves={found[name]:2d}  catalogued={have:2d}  {verdict}")
if not found:
    print("  (no prefabs with SkinnedMeshRenderers matched)")
