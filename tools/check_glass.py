import os
from PIL import Image
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
glass = {}
for l in open(os.path.join(OUT, "guid_mesh.txt")):
    p = l.split()
    if len(p) >= 2 and any(s in p[1].lower() for s in ["glass", "window", "shard"]):
        glass[p[0]] = p[1]
print("glass meshes:", sorted(set(glass.values())))
placed = {}
for l in open(os.path.join(OUT, "placements.txt")):
    q = l.split()
    if q and q[0] in glass:
        placed[q[0]] = placed.get(q[0], 0) + 1
print("placed glass:", {glass[g]: c for g, c in placed.items()})
for g, name in glass.items():
    tp = os.path.join(OUT, name + "_tex.png")
    if os.path.exists(tp):
        img = Image.open(tp).convert("RGBA")
        a = img.getchannel("A")
        lo, hi = a.getextrema()
        # fraction of semi-transparent (not 0, not 255) texels
        data = list(a.getdata())
        semi = sum(1 for v in data if 10 < v < 245)
        print(f"{name}_tex.png  alpha {lo}..{hi}  semi-transparent texels: {100*semi/len(data):.1f}%  size {img.size}")
    else:
        print(f"{name}: NO _tex.png")
