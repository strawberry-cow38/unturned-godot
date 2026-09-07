#!/usr/bin/env python3
"""Split the OPEN gun case (Crate_1 / Crate_4) into a tray body and a hinged lid leaf.

strawberry 2026-09-07: "split the gun case (one of the crate models) [the open one] into its two halves
and make it open/close like a fridge with smart storage container".

Crate_1 and Crate_4 are the same gun case in two finishes, and both are ALREADY smart containers (the
2026-09-04 material-variants block in WorldBuilder.ContainerShelf). What they never had is a door: with
no doors.txt entry StoreShelf.TrySpawnDoors returns false, so the lid is welded into the body and the
case stands permanently open. This produces the two halves that entry needs.

WHY NO PREDICATE. The toaster's lever had to be cut out with a bounding box because it shared vertices
with the body. This mesh does not: welding by position and walking the faces gives exactly TWO connected
components, so the split is read off the geometry instead of guessed at, and a change to the model would
show up as a component count that is no longer 2 rather than as a lid with a slice missing.

    lid   16 verts  y[-0.660,-0.352] z[0.145,0.917]   standing up, leaning back
    tray  16 verts  y[-0.375, 0.375] z[0.000,0.200]

THE HINGE IS DERIVED, NOT TUNED. The lid is a 0.75 x 0.2 slab whose long axis runs (-0.1464, 0.9892) --
98.419 degrees off flat. Rotating by exactly that angle lays it flat whatever the pivot, so the angle is
fixed by the geometry and only the pivot is free; the pivot is then SOLVED from the requirement that the
closed lid land on the tray, P = (I-R)^-1 * T. That gives (y,z) = (-0.3522, 0.1978): 2 mm under the tray
lip, 23 mm in from the back wall, which is where a real case hinge sits. Its y matches the lid's own
inner-bottom edge to 0.2 mm, which nothing in the derivation forced.

The cross-check that settles it: the closed lid occupies z 0.200..0.400 over a tray of 0.000..0.200, and
Crate_2/Crate_5 -- the CLOSED gun case, a prop this script never looks at -- is a box of exactly
z 0.000..0.400. The two halves fold into the shipped closed variant.

ObjectDoor authors its leaf CLOSED (_swing 0 applies no rotation, and the collider is sized from "the
leaf's OWN closed-pose AABB"), so the lid is written already folded down and doors.txt opens it by
+98.419 about +X -- positive, because opening carries +Y toward +Z.

AND THE LOD. Crate_1_lod1.obj carries the same two components, so the lid is dropped from it too. A leaf
split out of the body but left in the LOD draws TWICE at range, the door hanging open through a lid that
is still welded shut -- the exact failure recorded for the door LODs. Crate_4 ships no lod1 and has no
lods.txt line, so it needs no LOD pass; that asymmetry is why this reports what it touched.
"""
import math
import os
import sys

DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "game", "content", "objects")

HINGE_Y, HINGE_Z = -0.3522, 0.1978
OPEN_DEG = 98.419


def load(path):
    """Return (lines, verts) keeping every original line so vt/vn survive untouched."""
    lines = open(path).read().split("\n")
    verts = []
    for l in lines:
        t = l.split()
        if t and t[0] == "v":
            verts.append(tuple(float(x) for x in t[1:4]))
    return lines, verts


def components(lines, verts):
    """Weld by rounded position, union faces, return a list of vertex-index sets (1-based obj indices)."""
    key, rep = {}, []
    for v in verts:
        k = tuple(round(c, 4) for c in v)
        if k not in key:
            key[k] = len(key)
        rep.append(key[k])
    par = list(range(len(key)))

    def find(a):
        while par[a] != a:
            par[a] = par[par[a]]
            a = par[a]
        return a

    faces = []
    for l in lines:
        t = l.split()
        if t and t[0] == "f":
            idx = [int(w.split("/")[0]) - 1 for w in t[1:]]
            faces.append(idx)
            for k in range(1, len(idx)):
                a, b = find(rep[idx[0]]), find(rep[idx[k]])
                if a != b:
                    par[a] = b
    groups = {}
    for i in range(len(verts)):
        groups.setdefault(find(rep[i]), set()).add(i)
    return list(groups.values()), faces


def bbox(verts, idxs):
    pts = [verts[i] for i in idxs]
    return tuple((min(p[a] for p in pts), max(p[a] for p in pts)) for a in range(3))


def emit(lines, keep, out, rotate=False):
    """Write an .obj containing only faces whose vertices are all in `keep`, renumbering v/vt/vn.

    Every v/vt/vn line is re-emitted in order, so texture and normal indices stay paired with their
    vertex -- dropping a face must not shift the UVs of the ones that remain.
    """
    vmap, tmap, nmap = {}, {}, {}
    vs, vts, vns, fs = [], [], [], []
    vi = ti = ni = 0
    raw_v, raw_vt, raw_vn = [], [], []
    for l in lines:
        t = l.split()
        if not t:
            continue
        if t[0] == "v":
            raw_v.append(l)
        elif t[0] == "vt":
            raw_vt.append(l)
        elif t[0] == "vn":
            raw_vn.append(l)

    c = math.cos(math.radians(-OPEN_DEG))
    s = math.sin(math.radians(-OPEN_DEG))

    def put_v(i):
        nonlocal vi
        if i not in vmap:
            vi += 1
            vmap[i] = vi
            t = raw_v[i].split()
            x, y, z = float(t[1]), float(t[2]), float(t[3])
            if rotate:
                dy, dz = y - HINGE_Y, z - HINGE_Z
                y, z = dy * c - dz * s + HINGE_Y, dy * s + dz * c + HINGE_Z
            vs.append(f"v {x:.6f} {y:.6f} {z:.6f}")
        return vmap[i]

    for l in lines:
        t = l.split()
        if not t or t[0] != "f":
            continue
        idx = [w.split("/") for w in t[1:]]
        if not all(int(w[0]) - 1 in keep for w in idx):
            continue
        parts = []
        for w in idx:
            v = put_v(int(w[0]) - 1)
            piece = str(v)
            if len(w) > 1 and w[1]:
                j = int(w[1]) - 1
                if j not in tmap:
                    ti += 1
                    tmap[j] = ti
                    vts.append(raw_vt[j])
                piece += "/" + str(tmap[j])
            elif len(w) > 2:
                piece += "/"
            if len(w) > 2 and w[2]:
                j = int(w[2]) - 1
                if j not in nmap:
                    ni += 1
                    nmap[j] = ni
                    vns.append(raw_vn[j])
                piece += "/" + str(nmap[j])
            parts.append(piece)
        fs.append("f " + " ".join(parts))

    with open(out, "w") as fh:
        fh.write(f"# generated by tools/split_gun_case.py from the combined open gun case\n")
        fh.write("\n".join(vs + vts + vns + fs) + "\n")
    return len(vs), len(fs)


def split(name, make_leaf):
    path = os.path.join(DIR, name + ".obj")
    if not os.path.exists(path):
        print(f"  {name}: MISSING, skipped")
        return False
    lines, verts = load(path)
    comps, _ = components(lines, verts)
    if len(comps) != 2:
        print(f"  {name}: expected 2 components, found {len(comps)} -- REFUSING (the model changed)")
        return False
    # the lid is the component sitting behind the tray (most negative y)
    comps.sort(key=lambda g: bbox(verts, g)[1][0])
    lid, tray = comps[0], comps[1]
    lb, tb = bbox(verts, lid), bbox(verts, tray)
    print(f"  {name}: lid y[{lb[1][0]:.3f},{lb[1][1]:.3f}] z[{lb[2][0]:.3f},{lb[2][1]:.3f}] | "
          f"tray y[{tb[1][0]:.3f},{tb[1][1]:.3f}] z[{tb[2][0]:.3f},{tb[2][1]:.3f}]")
    nv, nf = emit(lines, tray, path)
    print(f"     body -> {name}.obj  ({nv} v, {nf} f)")
    if make_leaf:
        leaf = os.path.join(DIR, name + "_door.obj")
        nv, nf = emit(lines, lid, leaf, rotate=True)
        _, lv = load(leaf)
        ys = [v[1] for v in lv]
        zs = [v[2] for v in lv]
        print(f"     leaf -> {name}_door.obj ({nv} v, {nf} f)  CLOSED pose y[{min(ys):.3f},{max(ys):.3f}] z[{min(zs):.3f},{max(zs):.3f}]")
    return True


if __name__ == "__main__":
    print("splitting the open gun case into tray + hinged lid")
    ok = True
    ok &= split("Crate_1", True)
    ok &= split("Crate_4", True)
    ok &= split("Crate_1_lod1", False)   # LOD keeps the tray only; the swinging leaf is drawn by ObjectDoor at all ranges
    print("\ndoors.txt lines to add (pivot xyz, axis xyz, angle, duration, flag, sound):")
    for n in ("Crate_1", "Crate_4"):
        print(f"  {n} {n}_door.obj 0.000000 {HINGE_Y:.6f} {HINGE_Z:.6f} 1.000000 0.000000 0.000000 {OPEN_DEG:.4f} 0.4667 1 DoorHandle")
    sys.exit(0 if ok else 1)
