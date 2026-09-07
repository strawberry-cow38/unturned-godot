#!/usr/bin/env python3
"""Find where you can sit on a prop, and write game/content/objects/seats.txt.

master 2026-09-07: "wire up sitting on couches, chairs, benches, etc". There is no seat data in the rip --
retail's furniture is scenery, not interactable -- so the anchors are MEASURED off the meshes here, the same
way doors.txt is generated rather than hand-typed.

HOW A SEAT IS FOUND. A seat is a horizontal surface you can put a body on, so: keep only up-facing triangles
(face normal z > 0.85 -- the meshes are Z-up), bucket them by height, and take the biggest plane in the
0.40..0.95 m band. That band is the whole trick -- it excludes a picnic table's top (1.19) and a couch's back
(1.50) without needing to know which prop is which. Then split that plane into ISLANDS separated by a gap
wider than 0.25 m, because a picnic table's two benches are one plane and two seats.

WHICH WAY YOU FACE is per-prop and deliberately NOT inferred. Every backrest prop here has its back at -y and
so faces +y, which a rule could find; the picnic table has no backrest and you face the TABLE, which is the
same geometry as facing a backrest and means the opposite. Rather than a rule fitted to ten props and wrong
on the eleventh, FACING below is explicit, and this script PRINTS the measurement beside it so a wrong entry
is visible rather than buried.

Output is one line per seat, obj-space, the same convention doors.txt uses (raw ObjMesh coordinates, which
WorldBuilder maps to the world with the placement basis):

    <prop> <x> <y> <z> <fx> <fy> <fz>

    python3 tools/extract_seats.py [--dry]
"""
import math, os, sys

D = os.path.join(os.path.dirname(__file__), "..", "game", "content", "objects")
OUT = os.path.join(D, "seats.txt")

# Facing, in obj space, per prop, and how many people fit. SPACING is what turns a 2.5 m bench into 3 seats;
# a prop whose seat island is shorter than one spacing still gets one seat rather than none.
#   +y  = the back is at -y (every chair/couch/bench here)
#   inward = a picnic table: no backrest, and you face the table, not away from it
FACING = {
    "Bench_Wood_0":  ("+y", 0.85),
    "Bench_Wood_1":  ("inward", 0.85),   # picnic table: two bench planks either side of the top
    "Chair_Beach_0": ("+y", 9.99),       # a lounger is one person however wide the model is
    "Chair_Beach_1": ("+y", 9.99),
    "Chair_Beach_2": ("+y", 9.99),
    "Chair_Metal_0": ("+y", 9.99),
    "Chair_Wood_0":  ("+y", 9.99),
    "Chair_Wood_1":  ("+y", 9.99),
    "Couch_0":       ("+y", 0.95),
    "Couch_1":       ("+y", 0.95),
}
BAND = (0.40, 0.95)
GAP = 0.25


def load(path):
    V, F = [], []
    for l in open(path):
        t = l.split()
        if not t:
            continue
        if t[0] == "v":
            V.append(tuple(float(x) for x in t[1:4]))
        elif t[0] == "f":
            idx = [int(p.split("/")[0]) - 1 for p in t[1:]]
            for k in range(1, len(idx) - 1):
                F.append((idx[0], idx[k], idx[k + 1]))
    return V, F


def up_faces(V, F):
    """Up-facing triangles, as (area, height, [corners]). Normal from the winding, not from vn -- the caps I
    write elsewhere carry a vn and a ripped mesh's may disagree with its own winding; the geometry cannot."""
    out = []
    for a, b, c in F:
        p, q, r = V[a], V[b], V[c]
        u = [q[i] - p[i] for i in range(3)]
        v = [r[i] - p[i] for i in range(3)]
        n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        m = math.sqrt(sum(x * x for x in n))
        if m < 1e-9 or n[2] / m < 0.85:
            continue
        out.append((m / 2.0, (p[2] + q[2] + r[2]) / 3.0, (p, q, r)))
    return out


def islands(tris, axis):
    """Split a plane's triangles into groups separated along `axis` by more than GAP."""
    spans = sorted(((min(c[axis] for c in t[2]), max(c[axis] for c in t[2]), t) for t in tris),
                   key=lambda s: s[0])
    groups, cur, edge = [], [], None
    for lo, hi, t in spans:
        if cur and lo > edge + GAP:
            groups.append(cur); cur = []
        cur.append(t); edge = hi if edge is None else max(edge, hi)
    if cur:
        groups.append(cur)
    return groups


def bbox(tris, axis):
    return (min(c[axis] for t in tris for c in t[2]), max(c[axis] for t in tris for c in t[2]))


def main():
    rows, report = [], []
    for prop, (facing, spacing) in sorted(FACING.items()):
        V, F = load(os.path.join(D, prop + ".obj"))
        planes = {}
        for a, z, c in up_faces(V, F):
            if not (BAND[0] <= z <= BAND[1]):
                continue
            planes.setdefault(round(z, 2), []).append((a, z, c))
        assert planes, f"{prop}: no up-facing surface in the {BAND[0]}..{BAND[1]} m band"
        seatz = max(planes, key=lambda z: sum(t[0] for t in planes[z]))
        tris = planes[seatz]
        # A picnic table's benches are separated across X; a couch's cushions are one island. Split on
        # whichever axis actually HAS a gap -- trying only one would merge the benches on the other prop.
        groups = islands(tris, 0)
        if len(groups) == 1:
            groups = islands(tris, 1)
        for g in groups:
            gx, gy = bbox(g, 0), bbox(g, 1)
            cx, cy = (gx[0] + gx[1]) / 2.0, (gy[0] + gy[1]) / 2.0
            long_axis = 0 if (gx[1] - gx[0]) >= (gy[1] - gy[0]) else 1
            length = (gx[1] - gx[0]) if long_axis == 0 else (gy[1] - gy[0])
            n = max(1, int(round(length / spacing)))
            if facing == "+y":
                f = (0.0, 1.0, 0.0)
            elif facing == "inward":
                f = (-1.0 if cx > 0 else 1.0, 0.0, 0.0)   # face the middle of the prop, i.e. the table
            else:
                raise SystemExit(f"{prop}: unknown facing {facing!r}")
            for i in range(n):
                t = (i + 0.5) / n - 0.5                      # -0.5..+0.5 across the island's long axis
                x = cx + (length * t if long_axis == 0 else 0.0)
                y = cy + (length * t if long_axis == 1 else 0.0)
                rows.append(f"{prop} {x:.6f} {y:.6f} {seatz:.6f} {f[0]:.6f} {f[1]:.6f} {f[2]:.6f}")
            report.append(f"  {prop:<14} island x {gx[0]:6.2f}..{gx[1]:6.2f}  y {gy[0]:6.2f}..{gy[1]:6.2f}"
                          f"  z {seatz:.2f}  -> {n} seat(s), facing {facing}")
    print(f"seat planes in the {BAND[0]}..{BAND[1]} m band, islands split at a {GAP} m gap:")
    print("\n".join(report))
    print(f"\n{len(rows)} seats over {len(FACING)} props")
    if "--dry" in sys.argv:
        print("--dry: nothing written")
        return
    with open(OUT, "w") as f:
        f.write("# generated by tools/extract_seats.py -- <prop> <x> <y> <z> <facing x y z>, obj space\n")
        f.write("\n".join(rows) + "\n")
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
