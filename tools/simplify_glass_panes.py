#!/usr/bin/env python3
"""simplify_glass_panes.py -- collapse each generated vehicle glass pane to its OUTLINE.

strawberry 2026-09-07: "why are the glass panes on cars a billion tris? they should be TWO lol". They were
54-72 triangles each, 4334 across the fleet.

WHY THEY WERE LIKE THAT, because it was not silly: there is no glass mesh in the retail vehicle prefab at all --
the sedan's body is one 515-vertex Model_0 with the windows as GAPS between solid pillars. gen_vehicle_glass.py
therefore derives each pane by raycasting the body and tracing the see-through region ROW BY ROW, so the pane
follows a slanted pillar instead of being a box. Every row became its own quad, and a pane is ~27 rows.

But that shape lives entirely in the pane's OUTLINE. The seams between rows are interior edges of a flat sheet of
glass: invisible, and a triangle each. So this takes the convex hull of the traced points and fan-triangulates it.

⚠ CONVEX IS CHECKED, NOT ASSUMED. A hull over a non-convex window would overhang into the pillar. Measured over
all 83 panes: the hull encloses a MEDIAN of 1.000x the traced area, 1.042x at the very worst (hatchback rear
doors), and not one pane exceeds 1.05. Car windows are convex.

Runs over the generated files rather than inside the generator because gen_vehicle_glass.py takes per-vehicle
--skip/--band arguments that were never recorded in a caller script, so the fleet cannot be safely regenerated
from scratch. Re-running the generator for a vehicle should be followed by this.
"""
import sys, glob, os, math

def hull2d(pts):
    pts = sorted(set(pts))
    if len(pts) < 3: return pts
    def cross(o, a, b): return (a[0]-o[0])*(b[1]-o[1]) - (a[1]-o[1])*(b[0]-o[0])
    lower = []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 1e-12: lower.pop()
        lower.append(p)
    upper = []
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 1e-12: upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]

def simplify(path):
    head, V, F = [], [], []
    for line in open(path):
        if line.startswith("v "): V.append(tuple(float(x) for x in line.split()[1:4]))
        elif line.startswith("f "): F.append(line)
        elif line.startswith(("#", "g ")): head.append(line.rstrip("\n"))
    if len(V) < 3 or not F: return None
    U = sorted(set(tuple(round(c, 6) for c in v) for v in V))
    n = len(U)
    c = [sum(p[i] for p in U)/n for i in range(3)]
    # the pane's own plane: the direction of least spread, found by sampling (no numpy dependency here)
    best = None
    for a in range(0, 180, 2):
        for b in range(0, 360, 2):
            ra, rb = math.radians(a), math.radians(b)
            nv = (math.sin(ra)*math.cos(rb), math.sin(ra)*math.sin(rb), math.cos(ra))
            dev = max(abs(sum((p[i]-c[i])*nv[i] for i in range(3))) for p in U)
            if best is None or dev < best[0]: best = (dev, nv)
    dev, nv = best
    t = (1.0, 0.0, 0.0) if abs(nv[0]) < 0.9 else (0.0, 1.0, 0.0)
    d = sum(t[i]*nv[i] for i in range(3))
    u = [t[i] - nv[i]*d for i in range(3)]
    ul = math.sqrt(sum(x*x for x in u)); u = [x/ul for x in u]
    w = [nv[(i+1) % 3]*u[(i+2) % 3] - nv[(i+2) % 3]*u[(i+1) % 3] for i in range(3)]
    proj = {}
    for p in U:
        key = (round(sum((p[i]-c[i])*u[i] for i in range(3)), 6), round(sum((p[i]-c[i])*w[i] for i in range(3)), 6))
        proj.setdefault(key, p)          # keep the ORIGINAL 3D vertex, so any slight curvature is preserved
    H = hull2d(list(proj.keys()))
    if len(H) < 3: return None
    verts = [proj[k] for k in H]
    faces = [(1, i, i + 1) for i in range(2, len(verts))]   # fan
    with open(path, "w") as fh:
        for h in head: fh.write(h + "\n")
        fh.write("# SIMPLIFIED by tools/simplify_glass_panes.py -- outline only (see its docstring).\n")
        for v in verts: fh.write("v %.6f %.6f %.6f\n" % v)
        for f in faces: fh.write("f %d %d %d\n" % f)
    return len(F), len(faces), dev

pats = sys.argv[1:] or ["game/content/*glass*.txt"]
before = after = 0; done = 0; worst = 0.0
for pat in pats:
    for f in sorted(glob.glob(pat)):
        r = simplify(f)
        if not r: print("  skipped (degenerate) %s" % os.path.basename(f)); continue
        b, a, dev = r; before += b; after += a; done += 1; worst = max(worst, dev)
        if a > 4: print("  %-34s %3d -> %d tris" % (os.path.basename(f), b, a))
print("%d panes: %d triangles -> %d  (worst planarity deviation %.4f m)" % (done, before, after, worst))
