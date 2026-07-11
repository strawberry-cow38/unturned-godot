import os
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
glass = set()
for l in open(os.path.join(OUT, "guid_mesh.txt")):
    p = l.split()
    if len(p) >= 2 and p[1] in ("Glass_0", "Glass_1"):
        glass.add(p[0])
locs = []
for l in open(os.path.join(OUT, "placements.txt")):
    q = l.split()
    if q and q[0] in glass:
        locs.append((float(q[1]), float(q[2]), float(q[3])))
# cluster to find a building with several glass panes (godot x, -z)
from collections import Counter
cell = Counter()
for x, y, z in locs:
    cell[(round(x / 6) * 6, round(z / 6) * 6)] += 1
for (cx, cz), n in cell.most_common(6):
    print(f"{n} glass panes near godot ({cx:.0f},{-cz:.0f})")
