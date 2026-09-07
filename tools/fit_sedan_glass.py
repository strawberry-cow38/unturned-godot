#!/usr/bin/env python3
"""Sit the sedan's WINDSCREEN flush in its opening, and keep the rear screen raked to the C-pillar.

master 2026-09-07, after a long thread of rakes that chased the A-pillar's various edges:
  "revert all this stuff. just redo the glass to fit flush with the opening of the windscreen. forget the dash etc."
  "rear windshield can stay"

So the windscreen is no longer fitted to any pillar EDGE -- it is fitted to the HOLE. The body's windscreen
aperture is bounded by the cowl at the bottom and the header at the top, and the pane simply spans between them:
that is what "flush with the opening" means and it needs no choice about which edge of a wedge to follow, which is
what made the earlier attempts go round in circles.

The REAR screen keeps what it earned: the C-pillar's angle, measured off sedan_glass_r_rear's trailing edge (that
door pane is cut to the same pillar), sat an eighth of the pillar's depth outboard. Master kept that one.

Both parts are guarded, so re-running is a no-op.
"""
import io
import sys

BODY = "game/content/sedan_body.txt"
GLASS = "game/content/sedan_glass_windshield.txt"
REAR = "game/content/sedan_glass_rear.txt"
REARDOOR = "game/content/sedan_glass_r_rear.txt"   # cut to the C-pillar: its trailing edge is that pillar's angle
EPS = 1e-3


def verts(path):
    return [[float(x) for x in l.split()[1:4]] for l in io.open(path, encoding="utf-8") if l.startswith("v ")]


def head(path):
    return [l.rstrip("\n") for l in io.open(path, encoding="utf-8") if not (l.startswith("v ") or l.startswith("f "))]


def aperture(V):
    """The windscreen HOLE: its bottom edge on the cowl and its top edge on the header, both read off the body.

    Found the same way the dash tool found the cowl seam, because the landmarks are the same: the highest
    full-width horizontal line forward of the cabin is the cowl, the next distinct z behind it at that height is
    where the glass starts, and the pillar top above that is the header."""
    cowl = {}
    for i, v in enumerate(V):
        if v[2] > -1.0 or v[1] < 0.9: continue
        cowl.setdefault((round(v[1], 3), round(v[2], 3)), []).append(i)
    seam = max((k for k, ix in cowl.items() if len({round(V[i][0], 3) for i in ix}) >= 2), key=lambda k: (k[0], -k[1]))
    seamY, seamZ = seam
    innerX = max(abs(V[i][0]) for i in cowl[seam])
    # ⚠ THE OPENING'S BOTTOM EDGE IS THE COWL SEAM, not the next line behind it. At the beltline the inner skin
    # carries TWO z values -- the cowl seam at -1.461 and the top of the cabin's SIDE WALL at -1.123 -- and
    # NOTHING spans the band between them, so the hole runs forward all the way to the cowl. Fitting the pane to
    # -1.123 (which I did first) is fitting it to the side wall: it comes out at 12 degrees from vertical, which
    # is what master saw as "straight vertical". The real opening is 33.7 degrees, a normal windscreen rake.
    baseZ = seamZ
    tops = [v for v in V if abs(abs(v[0]) - innerX) < EPS and v[1] > seamY + 0.5 and v[2] < -0.9]
    if not tops: sys.exit("no header found above the windscreen base")
    topY = min(v[1] for v in tops)
    topZ = min(v[2] for v in tops)   # the header's FRONT edge: the top of the hole
    return seamY, baseZ, topY, topZ, innerX


def fit_windscreen():
    V = verts(BODY)
    seamY, baseZ, topY, topZ, innerX = aperture(V)
    gv = verts(GLASS)
    # Compare ALL FOUR corners, not three. The first guard checked the two heights and the top z and skipped the
    # BOTTOM -- so when the bottom edge moved from the side wall to the cowl seam it declared the pane already
    # correct and changed nothing, which reads as the fix having been applied.
    if (abs(min(v[1] for v in gv) - seamY) < EPS and abs(max(v[1] for v in gv) - topY) < EPS
            and abs(max(v[2] for v in gv) - topZ) < EPS
            and abs(min(v[2] for v in gv) - baseZ) < EPS): return "windscreen  already flush in the opening"
    xs = sorted({round(v[0], 6) for v in gv})
    out = head(GLASS)
    out.append("# FITTED FLUSH by tools/fit_sedan_glass.py: the pane spans the body's windscreen APERTURE -- cowl")
    out.append("# edge to header edge -- rather than following any one edge of the wedge-shaped A-pillar.")
    for x, y, z in ((xs[0], seamY, baseZ), (xs[-1], seamY, baseZ), (xs[-1], topY, topZ), (xs[0], topY, topZ)):
        out.append("v %.6f %.6f %.6f" % (x, y, z))
    out += ["f 1 2 3", "f 1 3 4"]
    io.open(GLASS, "w", encoding="utf-8").write("\n".join(out) + "\n")
    return "windscreen  bottom (y %.3f, z %.3f) top (y %.3f, z %.3f) -- slope %.3f, the opening itself" % (
        seamY, baseZ, topY, topZ, (topZ - baseZ) / (topY - seamY))


def rear_glass():
    """Kept from the earlier pass at master's word. Angle = the chord of the rear door pane's trailing edge (cut
    to this same C-pillar); position = an eighth of the pillar's depth outboard of where the pane's mid-height
    originally sat. The target is ABSOLUTE -- taken off the pillar, not off the pane's current position -- or
    re-running the tool would walk the pane further out every time."""
    V = verts(BODY)
    dv = [v for v in verts(REARDOOR) if v[1] <= 1.93]
    if len(dv) < 2: return "rear glass  no pillar run to measure"
    zmax = max(v[2] for v in dv)
    startY = max(v[1] for v in dv if abs(v[2] - zmax) < EPS)
    top = max(dv, key=lambda v: v[1])
    if top[1] - startY < EPS: return "rear glass  trailing edge has no rise"
    slope = (top[2] - zmax) / (top[1] - startY)
    innerX = 0.981
    foot = [v[2] for v in V if abs(v[1] - 1.125) < EPS and abs(abs(v[0]) - innerX) < EPS and v[2] > 0.9]
    pz = [v[2] for v in V if abs(v[1] - 1.875) < EPS and abs(abs(v[0]) - innerX) < EPS and v[2] > 0.9]
    if not foot or len(pz) < 2: return "rear glass  no C-pillar found"
    midZ = min(foot) + (max(pz) - min(pz)) * 0.125
    gv = verts(REAR)
    yLo, yHi = min(v[1] for v in gv), max(v[1] for v in gv)
    zLo, zHi = min(v[2] for v in gv), max(v[2] for v in gv)
    half = -slope * (yHi - yLo) / 2.0
    botZ, topZ = midZ + half, midZ - half
    if abs((zLo - zHi) / (yHi - yLo) - slope) < 5e-3 and abs((zLo + zHi) / 2.0 - midZ) < 5e-3:
        return "rear glass  already raked and out"
    xs = sorted({round(v[0], 6) for v in gv})
    out = head(REAR)
    out.append("# RE-RAKED by tools/fit_sedan_glass.py to the C-PILLAR, measured off sedan_glass_r_rear's trailing")
    out.append("# edge (that door pane is cut to this same pillar), then set an eighth of the pillar's depth out.")
    for x, y, z in ((xs[0], yLo, botZ), (xs[-1], yLo, botZ), (xs[-1], yHi, topZ), (xs[0], yHi, topZ)):
        out.append("v %.6f %.6f %.6f" % (x, y, z))
    out += ["f 1 2 3", "f 1 3 4"]
    io.open(REAR, "w", encoding="utf-8").write("\n".join(out) + "\n")
    return "rear glass  bottom (y %.3f, z %.3f) top (y %.3f, z %.3f) -- slope %.3f" % (yLo, botZ, yHi, topZ, slope)


if __name__ == "__main__":
    print("  " + fit_windscreen())
    print("  " + rear_glass())
