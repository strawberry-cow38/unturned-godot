#!/usr/bin/env python3
"""Split a ripped gun OBJ into its connected components, writing one part out on its own.

WHY THIS EXISTS. Unturned models a gun as ONE mesh: fury/item.prefab is Model_0 (LOD0) and Model_1 (LOD1),
one submesh, one material, and the only children are attachment HOOKS -- `Barrel` there is an empty transform
where a suppressor mounts, not the spinning assembly. There is no separately-named barrel object anywhere in
the install, and searching mesh names for one cannot work: every mesh in this game is called `Model_N`.

But the piece IS separable, because it is a disconnected COMPONENT of that one mesh. The rip duplicates
vertices per face, so components have to be found after welding by POSITION -- welding first turns the fury's
228 raw verts into 88, and 35 meaningless per-quad fragments into 4 real islands:

    island 0:  40v  X +/-0.116   Y -0.308..0.153   Z -0.332..0.110    receiver / body
    island 1:  16v  X +/-0.250   Y +/-0.200        Z -0.183..0.317    THE BARREL ASSEMBLY
    island 2:  16v  X +/-0.025   Y -0.402..-0.264                     grip, hanging below
    island 3:  16v  X -0.235..-0.128 (off-axis)                       feed / drum mount

Island 1 is identified by two properties nothing else has: it is symmetric about BOTH X and Y (i.e. about the
bore) and it reaches furthest forward, to Z +0.317 where every other island stops at +0.110. That is a barrel
cluster mounted through the receiver, and its symmetry gives the spin axis for free: Z, through (0, 0).

    python split_gun_islands.py <in.txt> <island-index[,index...]> <part-out.txt> <rest-out.txt>

Both outputs together contain every face of the input exactly once -- the script asserts it rather than
trusting it, because a split that quietly drops geometry looks like a modelling choice.
"""
import sys, collections

src, want, part_out, rest_out = sys.argv[1], [int(x) for x in sys.argv[2].split(",")], sys.argv[3], sys.argv[4]
lines = open(src).read().splitlines()
vs, faces = [], []                       # faces: (line index, [vertex indices])
for li, l in enumerate(lines):
    p = l.split()
    if not p: continue
    if p[0] == "v": vs.append((round(float(p[1]), 4), round(float(p[2]), 4), round(float(p[3]), 4)))
    elif p[0] == "f":
        idx = [int(t.split("/")[0]) for t in p[1:]]
        faces.append((li, [i - 1 if i > 0 else len(vs) + i for i in idx]))

pos, weld = {}, []
for v in vs:
    pos.setdefault(v, len(pos)); weld.append(pos[v])
par = list(range(len(pos)))
def find(a):
    while par[a] != a: par[a] = par[par[a]]; a = par[a]
    return a
def uni(a, b):
    ra, rb = find(a), find(b)
    if ra != rb: par[ra] = rb
for _, f in faces:
    for i in range(1, len(f)): uni(weld[f[0]], weld[f[i]])

groups = collections.defaultdict(list)
for li, f in faces: groups[find(weld[f[0]])].append(li)
order = sorted(groups.values(), key=len, reverse=True)
for w in want: assert 0 <= w < len(order), f"island {w} out of range (found {len(order)})"
chosen = set().union(*(set(order[w]) for w in want))

def emit(path, keep):
    out = []
    for li, l in enumerate(lines):
        if l.split()[:1] == ["f"] and li not in keep: continue
        out.append(l)
    open(path, "w").write("\n".join(out) + "\n")
    return sum(1 for li, _ in faces if li in keep)

a = emit(part_out, chosen)
b = emit(rest_out, {li for li, _ in faces} - chosen)
assert a + b == len(faces), f"faces lost: {a} + {b} != {len(faces)}"
print(f"[split] {src}: {len(vs)} raw verts, {len(pos)} welded, {len(order)} islands")
print(f"[split]   islands {want} -> {part_out}  ({a} faces)")
print(f"[split]   the rest   -> {rest_out}  ({b} faces)")
print(f"[split]   {a} + {b} == {len(faces)} faces, none lost")
