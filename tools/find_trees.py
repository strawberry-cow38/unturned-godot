import os
from collections import Counter
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
species = ["pine", "birch", "maple", "oak", "fir", "spruce", "tree"]
treeg = {}
for l in open(os.path.join(OUT, "guid_mesh.txt")):
    p = l.split()
    if len(p) >= 2 and any(s in p[1].lower() for s in species) and "street" not in p[1].lower():
        treeg[p[0]] = p[1]
print("tree meshes:", sorted(set(treeg.values())))

pts = []; cnt = Counter()
for l in open(os.path.join(OUT, "placements.txt")):
    p = l.split()
    if p and p[0] in treeg:
        cnt[treeg[p[0]]] += 1
        pts.append((float(p[1]), float(p[2]), float(p[3])))
print("tree instances:", dict(cnt), "total", len(pts))

if pts:
    cell = Counter(); cellsum = {}
    for x, y, z in pts:
        c = (int(x // 96), int(z // 96)); cell[c] += 1
        cx, cy, cz = cellsum.get(c, (0, 0, 0)); cellsum[c] = (cx + x, cy + y, cz + z)
    best = cell.most_common(1)[0][0]; n = cell[best]; sx, sy, sz = cellsum[best]
    print(f"densest forest: {n} trees near unity ({sx/n:.0f},{sy/n:.0f},{sz/n:.0f}) -> godot spawn ({sx/n:.0f},{-sz/n:.0f})")

# mesh Y extent: is the base at origin (0) or are roots modeled below 0?
for mesh in ["Pine_0", "Birch_0", "Maple_0", "Pine_1"]:
    fp = os.path.join(OUT, mesh + ".obj")
    if os.path.exists(fp):
        ys = [float(l.split()[2]) for l in open(fp) if l.startswith("v ")]
        if ys: print(f"{mesh}.obj Y range: {min(ys):.3f} .. {max(ys):.3f}")
