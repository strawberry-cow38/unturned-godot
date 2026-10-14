"""Vanilla Ceramic Bowl (1930) -> Dish Bowl (9303): upright, scaled to the Dish Bowl footprint, household palette."""
# Usage: python3 tools/vanilla_bowl_to_dish_bowl.py game/content/items/1930.txt game/content/items/9303.txt 0.4
# Keeps 9303.png (the household palette strip shared with the Cup 9300 / Plate 9302): outside, rim and base take
# texel (0,0) white 220; the inside wall and floor take texel (1,0) grey 174, as the old Dish Bowl did.
import math, sys
SRC, OUT, WIDTH = sys.argv[1], sys.argv[2], float(sys.argv[3])
v, vt, vn, faces = [], [], [], []
for line in open(SRC):
    q = line.split()
    if not q: continue
    if q[0] == "v": v.append(tuple(map(float, q[1:4])))
    elif q[0] == "vn": vn.append(tuple(map(float, q[1:4])))
    elif q[0] == "f": faces.append([tuple(int(a) - 1 for a in c.split("/")) for c in q[1:]])
rot = lambda p: (p[0], -p[2], p[1])                       # +90 deg about X: the rip's Z axis becomes up (rendered upright)
v = [rot(p) for p in v]; vn = [rot(p) for p in vn]
lo = [min(p[i] for p in v) for i in range(3)]; hi = [max(p[i] for p in v) for i in range(3)]
s = WIDTH / max(hi[0] - lo[0], hi[2] - lo[2])
cx, cz = (lo[0] + hi[0]) / 2, (lo[2] + hi[2]) / 2
v = [((p[0] - cx) * s, (p[1] - lo[1]) * s, (p[2] - cz) * s) for p in v]
h = max(p[1] for p in v)
WHITE, GREY = 1, 2                                         # vt indices: palette texel (0,0) 220 white, (1,0) 174 grey
counts = {}
out_f = []
for f in faces:
    c = [sum(v[a[0]][i] for a in f) / len(f) for i in range(3)]
    n = [sum(vn[a[2]][i] for a in f) for i in range(3)]; l = math.sqrt(sum(x * x for x in n)) or 1; n = [x / l for x in n]
    if n[1] > 0.7: kind = "rim" if c[1] > 0.85 * h else "floor"
    elif n[1] < -0.7: kind = "base"
    else: kind = "outer" if n[0] * c[0] + n[2] * c[2] > 0 else "inner"
    col = GREY if kind in ("inner", "floor") else WHITE
    counts[kind] = counts.get(kind, 0) + 1
    out_f.append(" ".join(f"{a[0]+1}/{col}/{a[2]+1}" for a in f))
with open(OUT, "w") as o:
    o.write(f"# Unturned item rip 1930 (Ceramic Bowl) -> Godot, stood upright (+90 X), scaled x{s:.6f} to the Dish Bowl's "
            f"{WIDTH} m footprint, base at y=0, recoloured to the household palette (X+Z negated, winding reversed, RH Y-up)\n")
    for p in v: o.write("v %.6f %.6f %.6f\n" % p)
    o.write("vt 0.125000 0.750000\nvt 0.375000 0.750000\n")
    for p in vn: o.write("vn %.6f %.6f %.6f\n" % p)
    for f in out_f: o.write(f"f {f}\n")
print("scale", round(s, 6), "box", [round(max(p[i] for p in v) - min(p[i] for p in v), 4) for i in range(3)], "faces", counts)
