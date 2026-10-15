#!/usr/bin/env python3
"""Derive a prop OBJ from another by a rigid change of frame -- the art itself untouched.

    derive_prop.py SRC.obj DST.obj --scale S      every position times S about the origin (normals as they were)
    derive_prop.py SRC.obj DST.obj --hang         turned upside down about its front axis, to hang from a ceiling

--hang is a half turn about the OBJ's Y (front) axis, (x, y, z) -> (-x, y, -z): a rotation, not a mirror, so faces keep
their winding and the doors still face the room. It then moves the result into the hung-décor convention (the Clock_0
convention, FurnitureCatalog: back at Y 0, centred across and up and down), so a counter hangs as a wall cabinet does.
A floor piece is drawn with no bottom (nobody sees under it), which turned over is an open top: --hang closes it with a
lid over the whole outline, one quad facing up, in the texture's carcass colour (LID_UV), appended after the art.

Round 12 (strawberry): the potted plants at --scale 2, and Upper_Counter_0 = Counter_0 --hang. Re-run on the source when
its art changes; BuildingGenTests.UpperCounterIsTheCounterHung holds the derived mesh to its source.
"""
import sys

LID_UV = (0.25, 0.75)   # the 2x2 counter texture's darkest cell, the carcass



def derive(src, dst, scale=1.0, hang=False):
    lines = open(src).read().split('\n')
    pos = [tuple(map(float, l.split()[1:4])) for l in lines if l.startswith('v ')]
    y0 = min(p[1] for p in pos)
    z0, z1 = min(p[2] for p in pos), max(p[2] for p in pos)
    out = []
    for l in lines:
        p = l.split()
        if p and p[0] == 'v':
            x, y, z = (float(t) for t in p[1:4])
            if hang:
                x, y, z = -x, y - y0, (z0 + z1) * 0.5 - z
            x, y, z = x * scale, y * scale, z * scale
            out.append('v %.6f %.6f %.6f' % (x, y, z))
        elif p and p[0] == 'vn' and hang:
            x, y, z = (float(t) for t in p[1:4])
            out.append('vn %.6f %.6f %.6f' % (-x, y, -z))
        else:
            out.append(l)
    if hang:
        out += lid(out)
    open(dst, 'w').write('\n'.join(out))


def lid(lines):
    """A quad over the turned piece's outline at its top, facing up: four corners, one uv, one normal, two faces wound
    counter-clockwise seen from above (the OBJ's own winding), indexed after everything already in `lines`."""
    pos = [tuple(map(float, l.split()[1:4])) for l in lines if l.startswith('v ')]
    nt = sum(1 for l in lines if l.startswith('vt '))
    nn = sum(1 for l in lines if l.startswith('vn '))
    x0, x1 = min(p[0] for p in pos), max(p[0] for p in pos)
    y0, y1 = min(p[1] for p in pos), max(p[1] for p in pos)
    z = max(p[2] for p in pos)
    n = len(pos)
    c = [n + 1, n + 2, n + 3, n + 4]
    f = lambda a, b, d: 'f ' + ' '.join('%d/%d/%d' % (i, nt + 1, nn + 1) for i in (a, b, d))
    return (['v %.6f %.6f %.6f' % (x, y, z) for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
            + ['vt %.6f %.6f' % LID_UV, 'vn 0.000000 0.000000 1.000000', f(c[0], c[1], c[2]), f(c[0], c[2], c[3])])


if __name__ == '__main__':
    a = sys.argv[1:]
    if len(a) < 3 or a[2] not in ('--scale', '--hang'):
        sys.exit(__doc__)
    derive(a[0], a[1], scale=float(a[3]) if a[2] == '--scale' else 1.0, hang=a[2] == '--hang')
