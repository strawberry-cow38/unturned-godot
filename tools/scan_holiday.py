import glob, re, os
from collections import Counter
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"

guid2hol = {}          # raw .dat GUID (lower) -> holiday name
guid2name = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try:
        txt = open(datp, "r", errors="ignore").read()
    except Exception:
        continue
    mg = re.search(r"\bGUID\s+([0-9a-fA-F]{32})", txt)
    mh = re.search(r"Holiday_Restriction\s+(\w+)", txt, re.IGNORECASE)
    if mg and mh:
        g = mg.group(1).lower()
        guid2hol[g] = mh.group(1).upper()
        guid2name[g] = os.path.basename(os.path.dirname(datp))

# which are actually placed in PEI?
placed = Counter()
try:
    for line in open(os.path.join(OUT, "placements.txt")):
        p = line.split()
        if p:
            placed[p[0].lower()] += 1
except Exception as e:
    print("no placements.txt:", e)

by_hol = Counter()
placed_examples = {}
for g, h in guid2hol.items():
    if g in placed:
        by_hol[h] += placed[g]
        placed_examples.setdefault(h, []).append((guid2name[g], placed[g]))

print("holiday-restricted OBJECT ASSETS in bundles:", len(guid2hol))
print("PLACED-in-PEI holiday props by holiday (instance counts):", dict(by_hol))
for h, ex in placed_examples.items():
    ex.sort(key=lambda x: -x[1])
    print(f"  {h}: " + ", ".join(f"{n}x{c}" for n, c in ex[:12]))

with open(os.path.join(OUT, "holiday_props.txt"), "w") as f:
    for g, h in sorted(guid2hol.items()):
        f.write(f"{g} {h}\n")
print("wrote holiday_props.txt with", len(guid2hol), "guid->holiday entries")
