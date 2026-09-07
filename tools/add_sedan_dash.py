#!/usr/bin/env python3
"""Build the sedan's front interior: the dash, a steering wheel that clears it, and a windscreen raked to match
the A-pillar.

master 2026-09-07, in two passes:
  "on the sedan model extend the hood area back into the cabin, to halfway the width of the a frame pillars. to
   simulate a 'dash'."
  "so youve got the top face of the dash, now add the front face in that white-ish color of the interior between
   that and the floor. then adjust the wheel to fit. then adjust the front glass to match the angle of the a
   pillar slope"

The ripped sedan had no dashboard at all -- and worse, a HOLE: the hood ran to the cowl seam, the cabin's front
bulkhead dropped away from there, and the band between them had no face on it, so from the driver's seat you
looked past the steering wheel straight down at the road.

FOUR PARTS, EACH SEPARATELY GUARDED so this can be re-run against a half-built mesh without doubling anything up.

⚠ THE PALETTE'S V IS FLIPPED ON LOAD. ContentProvider reads OBJ `vt` as `1 - v` ("Unity vt is V-up, Godot samples
V-down"), so the texel a face lands on is (int(u*4), int((1-v)*2)) -- NOT (int(u*4), int(v*2)). Getting this
backwards reads the palette a row out, which matters because the two rows mean opposite things: in
sedan_palette.png the only texel with alpha 0 is (0,0), and vehicle_paint.gdshader paints exactly the alpha-0
texels and leaves every other one its authored colour. So (0,0) is the car's PAINT and (1,0) rgb(166,166,166) is
the fixed light grey the cabin interior is already made of -- master's "white-ish color of the interior".
"""
import io
import re
import sys

BODY = "game/content/sedan_body.txt"
STEER = "game/content/sedan_steer.txt"
GLASS = "game/content/sedan_glass_windshield.txt"
SPEC = "game/Vehicle.cs"
EPS = 1e-3

# Where the wheel has to move to. Back, so it sits BEHIND the new dash face (toward the driver) instead of buried
# inside it; and up, so its top half clears the dash rather than peeping over by 16 cm.
WHEEL_DZ = 0.60
WHEEL_DY = 0.20


def load(path):
    V, VT, F = [], [], []
    for line in io.open(path, encoding="utf-8"):
        if line.startswith("v "): V.append([float(x) for x in line.split()[1:4]])
        elif line.startswith("vt "): VT.append([float(x) for x in line.split()[1:3]])
        elif line.startswith("f "): F.append([[int(k) for k in t.split("/")] for t in line.split()[1:]])
    return V, VT, F


def geometry(V):
    """The numbers master's ruler needs, all read off the mesh rather than typed in."""
    cowl = {}
    for i, v in enumerate(V):
        if v[2] > -1.0 or v[1] < 0.9: continue
        cowl.setdefault((round(v[1], 3), round(v[2], 3)), []).append(i)
    # Highest full-width horizontal line, and of the lines AT that height the FORWARD-most. The cowl seam and the
    # windscreen base share a height, so sorting on y alone picks the base -- which then measures the pillar
    # against itself and reports it as having no width at all.
    seam = max((k for k, ix in cowl.items() if len({round(V[i][0], 3) for i in ix}) >= 2), key=lambda k: (k[0], -k[1]))
    seamY, seamZ = seam
    innerX = max(abs(V[i][0]) for i in cowl[seam])
    behind = sorted({round(v[2], 3) for v in V if abs(v[1] - seamY) < EPS and round(v[2], 3) > seamZ + EPS})
    if not behind: sys.exit("no vertex behind the cowl seam -- not the mesh this tool was written for")
    screenZ = behind[0]
    outerX = max(abs(v[0]) for v in V if abs(v[1] - seamY) < EPS and abs(v[2] - screenZ) < EPS)
    return seamY, seamZ, screenZ, innerX, outerX


def texel(uv):
    return (int(uv[0] * 4) % 4, int((1.0 - uv[1]) * 2) % 2)


def uv_for(V, VT, F, want, pick):
    """A UV that lands on texel `want`, taken verbatim from a vertex `pick` accepts rather than invented. A corner
    of this mesh exists once per face that uses it, each copy carrying its own UV, and only some of them sample
    the texel you mean -- so "any vertex there" can hand back a different colour entirely."""
    for f in F:
        for t in f:
            i = t[0] - 1
            if texel(VT[i]) == want and pick(V[i]): return VT[i]
    return None


def append(path, lines):
    with io.open(path, "a", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")


def dash_top(V, VT, F, g):
    seamY, seamZ, screenZ, innerX, outerX = g
    for f in F:
        if all(abs(V[t[0] - 1][1] - seamY) < EPS for t in f) and any(abs(V[t[0] - 1][2] - seamZ) < EPS for t in f):
            return None, "dash top    already present"
    pillarW = outerX - innerX
    rearZ = screenZ + pillarW / 2.0
    uv = uv_for(V, VT, F, (0, 0), lambda p: p[1] > 0.9 and p[2] < seamZ + EPS)   # the hood's own paint UV
    if uv is None: return None, "no hood vertex on the paint texel"
    base = len(V) + 1
    out = ["v %.6f %.6f %.6f" % (x, seamY, z) for x, z in
           ((-innerX, seamZ), (innerX, seamZ), (innerX, rearZ), (-innerX, rearZ))]
    out += ["vt %.6f %.6f" % tuple(uv)] * 4
    out += ["vn 0.000000 1.000000 0.000000"] * 4
    fl, fr, rr, rl = base, base + 1, base + 2, base + 3
    # Wound to match the hood's own triangles: +Y by the right-hand rule. Backwards and it lights from underneath,
    # which reads as broken shading rather than a flipped triangle.
    out += ["f " + " ".join("%d/%d/%d" % (i, i, i) for i in tri) for tri in ((fl, rr, fr), (fl, rl, rr))]
    return out, "dash TOP    y=%.3f  z %.3f -> %.3f  (%.3f deep, %.3f past the glass line; pillar %.3f)" % (
        seamY, seamZ, rearZ, rearZ - seamZ, rearZ - screenZ, pillarW)


def dash_front(V, VT, F, g):
    """The fascia the driver actually looks at: the dash lip down to the cabin floor, in the interior's own grey."""
    seamY, seamZ, screenZ, innerX, outerX = g
    rearZ = screenZ + (outerX - innerX) / 2.0
    floorY = min(v[1] for v in V if abs(v[0]) <= innerX + EPS and -1.5 <= v[2] <= 1.5 and v[1] > 0.0)
    for f in F:
        if all(abs(V[t[0] - 1][2] - rearZ) < EPS for t in f): return None, "dash front  already present"
    uv = uv_for(V, VT, F, (1, 0), lambda p: abs(p[0]) <= innerX + EPS and p[1] < seamY + EPS)
    if uv is None: return None, "no interior vertex on the light-grey texel"
    base = len(V) + 1
    out = ["v %.6f %.6f %.6f" % c for c in
           ((-innerX, seamY, rearZ), (innerX, seamY, rearZ), (innerX, floorY, rearZ), (-innerX, floorY, rearZ))]
    out += ["vt %.6f %.6f" % tuple(uv)] * 4
    out += ["vn 0.000000 0.000000 1.000000"] * 4   # faces the driver; +Z is rearward on this model
    tl, tr, br, bl = base, base + 1, base + 2, base + 3
    out += ["f " + " ".join("%d/%d/%d" % (i, i, i) for i in tri) for tri in ((tl, br, tr), (tl, bl, br))]
    return out, "dash FRONT  z=%.3f  y %.3f -> %.3f  texel (1,0) rgb(166,166,166), the interior's own grey" % (
        rearZ, seamY, floorY)


def move_wheel():
    """The wheel sat at z -1.573..-1.261 -- entirely FORWARD of the new fascia, i.e. buried inside it. Nudge it
    back behind the dash and up, so it reads as a wheel rather than a rim peeping over a ledge."""
    src = io.open(STEER, encoding="utf-8").read().splitlines()
    zs = [float(l.split()[3]) for l in src if l.startswith("v ")]
    if max(zs) > -1.0: return None, "wheel       already moved"
    out = []
    for l in src:
        if l.startswith("v "):
            p = l.split()
            out.append("v %s %.6f %.6f" % (p[1], float(p[2]) + WHEEL_DY, float(p[3]) + WHEEL_DZ))
        else: out.append(l)
    io.open(STEER, "w", encoding="utf-8").write("\n".join(out) + "\n")

    # SteerPivot is the disc centroid the wheel spins about, and it has to move by the SAME delta or the wheel
    # turns about a point it no longer contains: it would ORBIT rather than spin, and only while steering, which
    # is the hardest moment to be looking at it.
    s = io.open(SPEC, encoding="utf-8").read()
    # ⚠ SCOPED TO THE SEDAN'S OWN SPEC BLOCK. Several cars share this line VERBATIM -- the jeep's is
    # `SteerPivot = new Vector3(-0.464f, 1.018f, -0.922f), SteerAxis = new Vector3(0f, 0.259f, 0.966f)` -- so a
    # bare search for the pattern finds whichever vehicle is declared FIRST. It did: the first run of this moved
    # the JEEP's pivot away from the jeep's wheel and left the sedan's untouched, which would have shown up as
    # the jeep's wheel orbiting a point in mid-air, in a car nobody was working on.
    pat = re.compile(r"^( *)SteerPivot = new Vector3\((-?[\d.]+)f, (-?[\d.]+)f, (-?[\d.]+)f\), SteerAxis = new Vector3\(0f, 0\.259f, 0\.966f\),.*$", re.M)
    lo = s.find('Body = "sedan_body.txt"')
    if lo < 0: return None, "wheel mesh moved but the sedan spec was not found -- FIX Vehicle.cs BY HAND"
    hi = s.find('Body = "', lo + 10)
    m = pat.search(s, lo, hi if hi > lo else len(s))
    if not m: return None, "wheel mesh moved but the sedan's SteerPivot line was not found -- FIX Vehicle.cs BY HAND"
    new = "%sSteerPivot = new Vector3(%sf, %.3ff, %.3ff), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // steer centroid + disc normal (PCA); moved with the mesh when the dash went in" % (
        m.group(1), m.group(2), float(m.group(3)) + WHEEL_DY, float(m.group(4)) + WHEEL_DZ)
    io.open(SPEC, "w", encoding="utf-8").write(s[:m.start()] + new + s[m.end():])
    return [], "wheel       +%.2f y, +%.2f z  (mesh AND SteerPivot -- the disc keeps its own centre)" % (WHEEL_DY, WHEEL_DZ)


def rerake_glass(g):
    """Rake the windscreen to the pillar's OUTSIDE SLANT (master 2026-09-07: "the glass should follow the outside
    slant of the pillars").

    ⚠ THAT IS NOT THE APERTURE'S FRONT EDGE, which is what I fitted first and what master corrected. The A-pillar
    is a WEDGE -- zero depth at the beltline, 0.25 at the top -- so it has three different "slants" and they are
    far apart:
        aperture front edge, beltline -> header      0.216   (fitted first; too upright, master pushed back)
        pillar REAR edge, against the side glass     0.549
        OUTSIDE SILHOUETTE, beltline -> roof front   0.324   <- the one you SEE from outside, and the one meant
    The original rip was 0.442, so raking to 0.216 made the screen stand UP relative to the body, which is what
    looked wrong. The outside silhouette runs past the header to the roof's leading edge, so the glass top lands
    at z=-0.880 -- BEHIND the aperture's front edge and under the header panel (which spans -0.961..-0.711 at
    y=1.875). Tucking under the header is how a windscreen is bonded anyway; it is not a gap."""
    seamY, seamZ, screenZ, innerX, outerX = g
    V, VT, F = load(BODY)
    tops = [v for v in V if abs(abs(v[0]) - innerX) < EPS and v[1] > seamY + 0.5 and v[2] < -0.9]
    if not tops: return None, "no A-pillar top found on the inner skin"
    topY = min(v[1] for v in tops)                       # the header, i.e. how tall the aperture is
    # THE PILLAR'S SLOPE IS ITS REAR EDGE, and the mesh already contains an independent measurement of it: the
    # FRONT DOOR GLASS is cut to the pillar, and sedan_glass_r_front's leading edge runs (1.2166, -1.1248) ->
    # (1.8166, -0.7948) -- slope 0.550. That is the diagonal you read as "the A-pillar" on a side profile, and
    # it is what the generator measured off this same body. The two lines I tried before are the wrong edges of
    # the same wedge: the aperture's front edge (0.216) is where the glass BUTTS IN, and the beltline-to-roof
    # silhouette (0.324) is the OUTER skin's leading corner. Both are shallower than the pillar reads, which is
    # why master kept seeing the screen as standing up.
    # ANGLE AND POSITION ARE SEPARATE, and conflating them cost a round. The ANGLE is the pillar's REAR EDGE --
    # corroborated by sedan_glass_r_front, the door pane cut to this same pillar, whose leading edge runs slope
    # 0.550. The POSITION then slides that line forward until it passes through the MIDDLE of the pillar rather
    # than lying on its back face (master: "center that glass in the middle of the a frame, as in bring it
    # forward"; then, when I re-derived the angle from the midline instead of translating: "you messed up the
    # angle on the latest one. last one was right. just pos off").
    #
    # Deriving a new slope from the midline is NOT the same thing: the pillar is a wedge (zero depth at the
    # beltline, 0.25 at the header), so its midline is SHALLOWER than either face -- it flattens the screen back
    # out, which is the complaint that started all this.
    zs = [v[2] for v in V if abs(v[1] - topY) < EPS and abs(abs(v[0]) - innerX) < EPS and v[2] < -0.5]
    if not zs: return None, "no pillar faces at the header height"
    slope = (max(zs) - screenZ) / (topY - seamY)         # the REAR edge: the pillar's own angle
    shift = (min(zs) + max(zs)) / 2.0 - max(zs)          # ...slid forward to the pillar's centre (negative = forward)
    botZ = screenZ + shift
    topZ = botZ + slope * (topY - seamY)
    gv = [[float(x) for x in l.split()[1:4]] for l in io.open(GLASS, encoding="utf-8") if l.startswith("v ")]
    gy = max(v[1] for v in gv) - min(v[1] for v in gv)
    if gy > EPS and abs((max(v[2] for v in gv) - min(v[2] for v in gv)) / gy - slope) < 5e-3 \
       and abs(min(v[2] for v in gv) - botZ) < 5e-3:
        return None, "glass       already on the pillar's angle, centred"
    xs = sorted({round(v[0], 6) for v in gv})
    head = [l.rstrip("\n") for l in io.open(GLASS, encoding="utf-8") if not (l.startswith("v ") or l.startswith("f "))]
    head.append("# RE-RAKED by tools/add_sedan_dash.py: the A-PILLAR'S REAR-EDGE ANGLE, slid forward to pass through")
    head.append("# the middle of the pillar. Angle and position are separate -- taking the angle off the midline")
    head.append("# instead flattens the screen, because a wedge's midline is shallower than either of its faces.")
    head.append("# Regenerating this pane with gen_vehicle_glass.py will undo that -- the rake lives HERE, not there.")
    for x, y, z in ((xs[0], seamY, botZ), (xs[-1], seamY, botZ), (xs[-1], topY, topZ), (xs[0], topY, topZ)):
        head.append("v %.6f %.6f %.6f" % (x, y, z))
    head += ["f 1 2 3", "f 1 3 4"]
    io.open(GLASS, "w", encoding="utf-8").write("\n".join(head) + "\n")
    return [], "glass       bottom (y %.3f, z %.3f) top (y %.3f, z %.3f) -- slope %.3f (pillar rear edge), slid %.3f forward" % (
        seamY, botZ, topY, topZ, slope, -shift)


def main():
    did = False
    for fn in (dash_top, dash_front):
        V, VT, F = load(BODY)          # re-read: the previous part may have appended to it
        lines, msg = fn(V, VT, F, geometry(V))
        print("  " + msg)
        if lines: append(BODY, lines); did = True
    V, VT, F = load(BODY)
    g = geometry(V)
    for fn in (move_wheel, lambda: rerake_glass(g)):
        lines, msg = fn()
        print("  " + msg)
        if lines is not None: did = True
    if not did: print("  (nothing to do -- the sedan interior is already built)")


if __name__ == "__main__":
    main()
