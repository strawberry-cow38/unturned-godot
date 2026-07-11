import os
from collections import Counter
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
hol = {}
for l in open(os.path.join(OUT, "holiday_props.txt")):
    q = l.split()
    if len(q) >= 2: hol[q[0]] = q[1]
gm = set()
for l in open(os.path.join(OUT, "guid_mesh.txt")):
    p = l.split()
    if p: gm.add(p[0])
placed = Counter()
for l in open(os.path.join(OUT, "placements.txt")):
    p = l.split()
    if p: placed[p[0]] += 1
rendered = Counter()
for g, h in hol.items():
    if g in gm:
        rendered[h] += placed.get(g, 0)
notrendered = sum(placed.get(g, 0) for g, h in hol.items() if g not in gm)
print("holiday props WITH a mesh (rendered -> should be gated):", dict(rendered), "total", sum(rendered.values()))
print("holiday props placed but NO extracted mesh (already invisible):", notrendered)
