#!/usr/bin/env python3
"""Give the sedan a DASH by carrying its hood surface back into the cabin.

master 2026-09-07: "on the sedan model extend the hood area back into the cabin, to halfway the width of the a
frame pillars. to simulate a 'dash'."

The ripped sedan has no dashboard at all. Its hood runs up to the cowl seam and the cabin's front bulkhead drops
away from there, so the whole band between the cowl and the base of the windscreen is an OPEN SLOT -- from the
driver's seat there is nothing under the glass but the firewall falling away below. This adds the one surface that
was missing: the hood's own plane, continued rearward past the glass line.

EVERY NUMBER IS READ OFF THE MESH, none typed in. The three that matter:
  cowl seam    -- the hood's rear edge, the highest full-width horizontal line forward of the cabin
  screen base  -- where the windscreen starts, one step back from the cowl at the same height
  pillar width -- the A-pillar's thickness across the car: outer skin minus inner skin at the screen base.
                  That is master's ruler: the dash ends HALF a pillar-width behind the glass line.

It reuses the cowl vertices' own UVs, so the new surface samples the same texel of the 4x2 paint palette that the
hood does and takes the car's colour rather than an arbitrary one. Normals are straight up; the body material is
CullMode.Disabled, so one plane reads from above and below and no thickness needs faking.
"""
import io
import sys

SRC = "game/content/sedan_body.txt"
EPS = 1e-3


def load(path):
    head, V, VT, VN, F = [], [], [], [], []
    for line in io.open(path, encoding="utf-8"):
        if line.startswith("v "): V.append([float(x) for x in line.split()[1:4]])
        elif line.startswith("vt "): VT.append([float(x) for x in line.split()[1:3]])
        elif line.startswith("vn "): VN.append([float(x) for x in line.split()[1:4]])
        elif line.startswith("f "): F.append([[int(k) for k in t.split("/")] for t in line.split()[1:]])
        else: head.append(line.rstrip("\n"))
    return head, V, VT, VN, F


def main():
    head, V, VT, VN, F = load(SRC)

    # THE COWL SEAM: the hood's rear edge. Found as the pair of vertices sharing the highest y that appears at a
    # mirrored +-x on the FORWARD half of the car -- the hood is the only full-width horizontal line up there.
    cowl = {}
    for i, v in enumerate(V):
        if v[2] > -1.0 or v[1] < 0.9: continue          # cabin, or below the beltline
        cowl.setdefault((round(v[1], 3), round(v[2], 3)), []).append(i)
    # Highest such line, and of the lines AT that height the FORWARD-most one. Both the cowl seam and the
    # windscreen base sit at the same y and both are mirrored full-width, so sorting on y alone picks the screen
    # base -- which then measures the pillar against itself and reports it as having no width at all.
    seam = max((k for k, ix in cowl.items() if len({round(V[i][0], 3) for i in ix}) >= 2), key=lambda k: (k[0], -k[1]))
    seamY, seamZ = seam
    seamIdx = cowl[seam]
    innerX = max(abs(V[i][0]) for i in seamIdx)

    # THE SCREEN BASE: at the cowl's height, the next distinct z BEHIND the seam. That is where the glass starts
    # and where the A-pillar's foot sits.
    behind = sorted({round(v[2], 3) for v in V if abs(v[1] - seamY) < EPS and round(v[2], 3) > seamZ + EPS})
    if not behind: sys.exit("no vertex behind the cowl seam at y=%.3f -- not the mesh this tool was written for" % seamY)
    screenZ = behind[0]

    # THE PILLAR WIDTH: outer skin minus inner skin, measured across the car at the screen base.
    atBase = [abs(v[0]) for v in V if abs(v[1] - seamY) < EPS and abs(v[2] - screenZ) < EPS]
    outerX = max(atBase)
    pillarW = outerX - innerX
    if pillarW <= EPS: sys.exit("A-pillar has no width at the screen base (inner %.3f outer %.3f)" % (innerX, outerX))

    rearZ = screenZ + pillarW / 2.0   # master's ruler: halfway across the pillar

    # ALREADY RUN? Signature = a horizontal face at the cowl height that TOUCHES THE SEAM ITSELF. "reaches behind
    # the glass" is not enough: the door-top sills sit at exactly this height and run the length of the cabin, so
    # that test fires on the untouched mesh and the tool refuses to do anything.
    for f in F:
        if all(abs(V[t[0] - 1][1] - seamY) < EPS for t in f) and any(abs(V[t[0] - 1][2] - seamZ) < EPS for t in f):
            sys.exit("sedan_body.txt already has a dash at y=%.3f -- `git checkout` it before re-running" % seamY)

    print("cowl seam    y=%.3f z=%.3f  (hood's rear edge, half-width %.3f)" % (seamY, seamZ, innerX))
    print("screen base  z=%.3f" % screenZ)
    print("pillar width %.3f  (inner %.3f -> outer %.3f), half = %.3f" % (pillarW, innerX, outerX, pillarW / 2.0))
    print("dash spans   z %.3f -> %.3f  (%.3f deep, %.3f of it behind the glass line)"
          % (seamZ, rearZ, rearZ - seamZ, rearZ - screenZ))

    # The new corners take a HOOD vertex's UV, so the quad lands in the same texel of the 4x2 paint palette that
    # the hood does and comes out the colour of the car.
    #
    # ⚠ WHICH seam vertex is not a detail. A corner of this mesh appears once per face that uses it, each copy
    # carrying its own UV, and the palette's texels are not all paint -- there is trim and black in there too. So
    # "any vertex at the seam" can hand back a UV from a different texel and paint the dash the wrong colour. Take
    # the UV from a vertex a HOOD face actually uses, and check the texel afterwards rather than trusting it.
    hoodVerts = set()
    for f in F:
        if all(V[t[0] - 1][2] <= seamZ + EPS for t in f): hoodVerts.update(t[0] - 1 for t in f)
    texel = lambda uv: (int(uv[0] * 4) % 4, int(uv[1] * 2) % 2)   # the palette is 4x2
    uvOf = {}
    for i in seamIdx:
        if i in hoodVerts: uvOf.setdefault(round(V[i][0], 3), VT[i])
    if len(uvOf) < 2: sys.exit("could not find a hood-face UV on both sides of the cowl seam")
    left, right = min(uvOf), max(uvOf)
    want = {texel(uvOf[left]), texel(uvOf[right])}
    if len(want) != 1: sys.exit("the two sides of the hood sample different palette texels %s -- pick one by hand" % want)
    print("paint texel  %s of the 4x2 palette (from the hood's own UVs)" % (want.pop(),))

    lines = []
    base = len(V) + 1
    for x, z in ((left, seamZ), (right, seamZ), (right, rearZ), (left, rearZ)):
        lines.append("v %.6f %.6f %.6f" % (x, seamY, z))
    for x, z in ((left, seamZ), (right, seamZ), (right, rearZ), (left, rearZ)):
        lines.append("vt %.6f %.6f" % tuple(uvOf[x]))
    for _ in range(4):
        lines.append("vn 0.000000 1.000000 0.000000")
    # Wound to match the hood's own faces: front-left, rear-right, front-right gives +Y by the right-hand rule,
    # which is what the existing cowl triangle does. Getting this backwards would light the dash from underneath.
    fl, fr, rr, rl = base, base + 1, base + 2, base + 3
    for tri in ((fl, rr, fr), (fl, rl, rr)):
        lines.append("f " + " ".join("%d/%d/%d" % (i, i, i) for i in tri))

    with io.open(SRC, "a", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    print("appended 4 verts + 2 faces to %s" % SRC)


if __name__ == "__main__":
    main()
