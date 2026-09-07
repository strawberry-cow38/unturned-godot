#!/usr/bin/env python3
"""Split a cardboard box into a body plus four hinged FLAP leaves, so opening it animates.

master 2026-09-07, on the mesh-swap version: "does it have the fridge open/close anim?" -- it did not, and a swap
can never have one: you cannot interpolate between two different meshes, so it is always a pop. To move, the flaps
have to be their own objects hinged on their own edges. That is the fridge/bin-lid pattern, four leaves instead of
one, and the door system already groups multiple leaves (the wardrobe swings both together).

Each box is 20 body faces + 4 faces per flap, and no face straddles two flaps, so the split is exact.

⚠ THE LEAVES ARE WRITTEN FOLDED, not splayed. The prop is modelled OPEN, but a door's REST pose is its unswung
one and a container swings on INVENTORY-OPEN -- so authoring the flaps splayed would have the box start open and
shut itself when you opened it. Folding them first and making the swing the UNFOLD puts rest = shut, swung = open,
which is what every other container here means by those words.
"""
import sys, os, math

def split(n):
    src = "game/content/objects/Cardboard_%d.obj" % n
    head, V, F = [], [], []
    for line in open(src):
        if line.startswith("v "): V.append([float(x) for x in line.split()[1:4]])
        elif line.startswith("f "): F.append([int(t.split("/")[0]) - 1 for t in line.split()[1:]])
        elif line.startswith(("#", "g ", "o ")): head.append(line.rstrip("\n"))
    # THE BODY WALLS ARE CLUSTERS, NOT UNIQUE VALUES. Cardboard_1/_3 carry their +x flap at x = 0.7253 AND
    # 0.7254 -- a tenth of a millimetre apart -- so "second-largest distinct x" picked the FLAP as the wall,
    # swallowed that flap into the body (three leaves instead of four) and put the hinge 23 cm above the real
    # rim. Merge anything within a millimetre first; then second-in / second-from-last really are the walls.
    def walls(vals, tol=1e-3):
        out = []
        for v in sorted(vals):
            if not out or v - out[-1] > tol: out.append(v)
        return out
    xs = walls({v[0] for v in V}); ys = walls({v[1] for v in V})
    x0, x1, y0, y1 = xs[1], xs[-2], ys[1], ys[-2]
    tol = 1e-3

    def side_of(v):
        if v[0] < x0 - tol: return "xlo"
        if v[0] > x1 + tol: return "xhi"
        if v[1] < y0 - tol: return "ylo"
        if v[1] > y1 + tol: return "yhi"
        return None

    groups = {"body": [], "xlo": [], "xhi": [], "ylo": [], "yhi": []}
    for f in F:
        s = {side_of(V[i]) for i in f} - {None}
        groups[s.pop() if len(s) == 1 else "body"].append(f)

    if not any(groups[k] for k in ("xlo", "xhi", "ylo", "yhi")):
        raise SystemExit("%s has no flap faces -- it is ALREADY split. `git checkout` it first." % src)

    def write(path, faces, verts):
        used = sorted({i for f in faces for i in f})
        remap = {old: k + 1 for k, old in enumerate(used)}
        with open(path, "w") as fh:
            for h in head: fh.write(h + "\n")
            fh.write("# SPLIT by tools/split_cardboard_flaps.py\n")
            for i in used: fh.write("v %.6f %.6f %.6f\n" % tuple(verts[i]))
            for f in faces: fh.write("f " + " ".join(str(remap[i]) for i in f) + "\n")

    write(src, groups["body"], V)   # the body keeps the prop's own name; its flaps are leaves now
    lines = []
    for s in ("xlo", "xhi", "ylo", "yhi"):
        # ⚠ not every box has four. Cardboard_1/_3 have THREE -- no xhi flap at all -- which is also why the
        # earlier fold tool moved 12 verts on one box and 16 on another.
        if not groups[s]: continue
        ax = 0 if s.startswith("x") else 1
        wall = {"xlo": x0, "xhi": x1, "ylo": y0, "yhi": y1}[s]
        folded = [list(v) for v in V]
        idx = {i for f in groups[s] for i in f}
        # THE HINGE HEIGHT IS THIS FLAP'S OWN, read off the vertices it shares with the wall -- not one `rim` for
        # the whole box. A single box-wide rim ("tallest vertex over the footprint") is only right when nothing
        # else on the body reaches higher, and on Cardboard_1 something does: its hinges sit at z=0.5694 while
        # that rule returned 0.7988, which folds every flap through the wall instead of over it.
        onwall = [V[i] for i in idx if abs(V[i][ax] - wall) < tol]
        rim = sum(v[2] for v in onwall) / len(onwall)
        if max(v[2] for v in onwall) - min(v[2] for v in onwall) > tol:
            raise SystemExit("%s %s: hinge edge is not level (z spread %.4f) -- not a single-axis fold"
                             % (src, s, max(v[2] for v in onwall) - min(v[2] for v in onwall)))
        tip = max((V[i] for i in idx), key=lambda v: abs(v[ax] - wall))   # the flap's far edge sets the swing
        run = math.hypot(tip[ax] - wall, tip[2] - rim)
        inward = -1.0 if tip[ax] > wall else 1.0
        # ⚠ FOLDED FLAPS OVERLAP. Four leaves flattened onto the same rim plane are coplanar where they cross,
        # which z-fights into a flickering mess on a shut box. Stack them 4 mm apart in the order real cardboard
        # interleaves. The hinge verts ride up with their own leaf and the pivot goes up with them, so each flap
        # still hinges on its own edge; open, 4 mm off the rim is not something you can see.
        lift = 0.004 * ("xlo", "xhi", "ylo", "yhi").index(s)
        for i in idx:
            v = folded[i]
            if abs(v[ax] - wall) < tol and abs(v[2] - rim) < tol:
                v[2] += lift                  # on the hinge line: stays on its wall, just rides up
                continue
            r = math.hypot(v[ax] - wall, v[2] - rim)
            v[ax] = wall + r * inward         # swing the flap down flat, inward over the mouth
            v[2] = rim + lift
        leaf = "Cardboard_%d_%s_door.obj" % (n, s)
        write("game/content/objects/" + leaf, groups[s], folded)

        # THE SWING ANGLE IS A SIGNED ROTATION, not an atan2 in the (across, up) plane. atan2 sweeps +across
        # toward +up, which IS the right-hand sense about +X but the exact opposite of the sense about +Y -- so
        # reading it off the plane gets one of the two flap families backwards, and the box opens by folding its
        # side flaps down through the floor. Both references in the catalog agree with the cross product below:
        # the bin lid hinges on its -Y edge, lies inward along +Y and opens at +100 about +X; the crossing arm
        # extends along +X and lifts at -90 about +Y.
        axv = [0.0, 0.0, 0.0]; axv[1 - ax] = 1.0                           # x-side flap hinges about Y, y-side about X
        u = [0.0, 0.0, 0.0]; u[ax] = run * inward                          # SHUT: folded flat, pointing inward
        w = [0.0, 0.0, 0.0]; w[ax] = tip[ax] - wall; w[2] = tip[2] - rim   # OPEN: splayed exactly as modelled
        cr = [u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0]]
        deg = math.degrees(math.atan2(sum(p * q for p, q in zip(axv, cr)), sum(p * q for p, q in zip(u, w))))
        axis = "0.000000 1.000000 0.000000" if ax == 0 else "1.000000 0.000000 0.000000"
        px = wall if ax == 0 else 0.0
        py = 0.0 if ax == 0 else wall
        lines.append("Cardboard_%d %s %.6f %.6f %.6f %s %.4f 0.4667 0 DoorHandle" % (n, leaf, px, py, rim + lift, axis, deg))
    return lines

out = []
for n in range(4): out += split(n)
print("\n".join(out))
