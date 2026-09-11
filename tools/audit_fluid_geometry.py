#!/usr/bin/env python3
"""Find gaps, holes and inconsistent edges in the authored fluid meshes.

strawberry 2026-09-10: "fill gaps and weird edges and inconsistent geometry, and any gaps/holes."

WHAT A HOLE ACTUALLY IS HERE, because the obvious check is the wrong one. These meshes are built as
overlapping SOLIDS -- a spigot pierces a barrel band, a pipe enters a cabinet -- and that is correct and
deliberate: welding two solids flush at a shared edge is what produces an edge with four faces on it, so
every joint is meant to interpenetrate. So "not watertight as one surface" is not the defect.

The defect is a solid that is not closed ON ITS OWN. Each mesh is therefore split into connected
components by shared position, and each COMPONENT is required to be closed: every edge used exactly
twice, in opposite directions. A component with edges used once has a literal hole you can see through.

Also reported, because they are what "weird edges" looks like in practice:
  - degenerate triangles (zero area) -- render as nothing, break normals
  - edges used 3+ times, i.e. a non-manifold seam, usually two solids welded instead of overlapped
  - faces whose stored normal does not OPPOSE their winding (these files are clockwise-front; see below)
  - vertices closer than 1 mm but not welded -- a crack that shows as a flickering seam

Exit 1 on any closure failure or wound-backwards face, so it can gate a build.
"""
import collections
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FLUID = ROOT / 'game/content/fluid'
WELD = 1e-4          # positions closer than this should have been welded by the author
AREA = 1e-9          # below this a triangle has no area


def load(path):
    """Parse exactly the way ContentProvider.ParseObj does: first three corners of every f line."""
    v, vn, tris = [], [], []
    group = '?'
    for line in path.read_text().splitlines():
        p = line.split()
        if not p:
            continue
        if p[0] == 'g':
            group = p[1] if len(p) > 1 else '?'
        elif p[0] == 'v':
            v.append(tuple(float(x) for x in p[1:4]))
        elif p[0] == 'vn':
            vn.append(tuple(float(x) for x in p[1:4]))
        elif p[0] == 'f':
            corner = []
            for c in p[1:4]:
                bits = c.split('/')
                idx = int(bits[0])
                ni = int(bits[2]) if len(bits) > 2 and bits[2] else None
                corner.append((idx - 1 if idx > 0 else len(v) + idx,
                               (ni - 1 if ni > 0 else len(vn) + ni) if ni else None))
            tris.append((corner, group))
    return v, vn, tris


def components(v, tris):
    """Connected components over shared VERTEX POSITION -- one per authored solid, near enough."""
    parent = list(range(len(v)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb

    for t, _ in tris:
        union(t[0][0], t[1][0])
        union(t[1][0], t[2][0])
    groups = collections.defaultdict(list)
    for t, g in tris:
        groups[find(t[0][0])].append((t, g))
    return list(groups.values())


def audit(path):
    v, vn, tris = load(path)
    out = []

    degen = 0
    backwards = 0
    for t, _g in tris:
        a, b, c = (v[i] for i, _ in t)
        ab = tuple(b[k] - a[k] for k in range(3))
        ac = tuple(c[k] - a[k] for k in range(3))
        n = (ab[1] * ac[2] - ab[2] * ac[1], ab[2] * ac[0] - ab[0] * ac[2], ab[0] * ac[1] - ab[1] * ac[0])
        area = math.sqrt(sum(x * x for x in n))
        if area < AREA:
            degen += 1
            continue
        # THESE FILES ARE CLOCKWISE-FRONT, so the stored outward normal OPPOSES the right-hand-rule
        # winding normal, and that is correct.
        #
        # ⚠ THIS CHECK CANNOT SEE THE LOADER, and that is its real limitation. It reads files; the loader
        # is C#. On 2026-09-11 ContentProvider.ParseObj was briefly changed to reverse every triangle --
        # right for the retail-derived .txt meshes that share it, and it would have rendered all 30 of
        # these inside out. This check would have gone on reporting 0 findings throughout, because nothing
        # in the files changed.
        #
        # So read the number below as a CONSISTENCY assertion -- "all 30 agree with each other and with
        # what was last confirmed by eye" -- and never as evidence the render is right. Only a render is
        # that. (catboy's c5aa8ec8 settled the loader by deciding corner order PER TRIANGLE from the
        # authored normal, so both families load correctly and neither generator has to move; these files
        # stay clockwise-front. That fix is also why this convention is still the right one to assert.) Do not compare this against the retail .obj props: they
        # load through ObjMesh.Load, which negates an axis and always reverses winding, while these load
        # through ContentProvider.ParseObj, which preserves it. I ran exactly that comparison, "found"
        # all 30 meshes wound backwards, flipped them, and shipped a set that rendered inside out.
        ni = t[0][1]
        if ni is not None and ni < len(vn):
            stored = vn[ni]
            if sum(stored[k] * n[k] for k in range(3)) / area > 0.2:
                backwards += 1
    if degen:
        out.append(f'{degen} degenerate (zero-area) triangles')
    if backwards:
        out.append(f'{backwards} faces whose stored normal opposes their winding')

    # near-but-unwelded positions
    seen = {}
    cracks = 0
    for p in v:
        key = tuple(round(x / WELD) for x in p)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    k = (key[0] + dx, key[1] + dy, key[2] + dz)
                    if k in seen and seen[k] != p and all(abs(seen[k][i] - p[i]) < WELD for i in range(3)):
                        cracks += 1
        seen[key] = p
    if cracks:
        out.append(f'{cracks} vertex pairs within {WELD} m but not welded')

    holes = seams = 0
    open_components = 0
    open_groups = set()
    for comp in components(v, tris):
        edges = collections.Counter()
        owner = {}
        for t, g in comp:
            i, j, k = (x for x, _ in t)
            for a, b in ((i, j), (j, k), (k, i)):
                edges[(a, b)] += 1
                owner[(a, b)] = g
        boundary = [e for e in edges if edges[(e[1], e[0])] == 0]
        over = [e for e, n in edges.items() if n > 1]
        if boundary:
            holes += len(boundary)
            open_components += 1
            open_groups.update(owner[e] for e in boundary)
        seams += len(over)
    if holes:
        out.append(f'NOT CLOSED: {holes} boundary edges across {open_components} solid(s) '
                   f'-- see-through holes in: {", ".join(sorted(open_groups))}')
    if seams:
        out.append(f'{seams} edges used twice in the SAME direction (non-manifold seam)')
    return out, len(tris)


def main():
    files = sorted(FLUID.glob('*.txt'))
    if not files:
        print('no fluid meshes found', file=sys.stderr)
        return 2
    bad = 0
    total = 0
    for f in files:
        problems, n = audit(f)
        total += n
        if problems:
            bad += 1
            print(f'FAIL {f.name} ({n} tris)')
            for p in problems:
                print(f'       {p}')
        else:
            print(f'ok   {f.name} ({n} tris)')
    print(f'\n{len(files)} meshes, {total} triangles, {bad} with findings')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
