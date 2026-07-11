import glob, re, os
from collections import Counter
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"

rendered = {}
for l in open(os.path.join(OUT, "guid_mesh.txt")):
    p = l.split()
    if len(p) >= 2: rendered[p[0]] = p[1]

guid2type = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    mg = re.search(r"\bGUID\s+([0-9a-fA-F]{32})", txt)
    mt = re.search(r"^\s*Type\s+(\w+)", txt, re.MULTILINE | re.IGNORECASE)
    if mg and mt:
        guid2type[mg.group(1).lower()] = mt.group(1).upper()

with open(os.path.join(OUT, "obj_type.txt"), "w") as f:
    for g in rendered:
        f.write(f"{g} {guid2type.get(g, '?')}\n")

by_type = Counter(guid2type.get(g, "?") for g in rendered)
print("rendered props by Type:", dict(by_type))
small = [rendered[g] for g in rendered if guid2type.get(g) == "SMALL"]
print(f"\nSMALL rendered props ({len(small)}):")
print(", ".join(sorted(small)))
print()
for kw in ["bush", "wheat", "crop", "grass", "plant", "flower", "fern", "reed"]:
    hits = sorted({(rendered[g], guid2type.get(g, "?")) for g in rendered if kw in rendered[g].lower()})
    if hits: print(f"{kw}: {hits}")
