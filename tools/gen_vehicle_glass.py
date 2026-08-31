#!/usr/bin/env python3
"""Generate window glass panes for a vehicle body mesh, deriving every constant from THAT body.

strawberry 2026-08-31: "derive it from each vehicle's window opening. vehicles are each
fundamentally different." Nothing here is tuned to a particular car -- the beltline, the side
plane, the cabin band and both raked apertures are measured off the mesh handed in.

WHY RAYCASTS AND NOT TOPOLOGY: a greenhouse is modelled as watertight solid pillars plus a roof
slab, so the windows are gaps BETWEEN geometry, not holes IN a surface. A boundary-edge test finds
4 edges on a whole sedan and would report that the car has no windows.

  beltline    scan y-slices; the greenhouse is where the body's z-extent collapses (the cabin is
              much shorter than the hood+boot). Taken as the first slice under a fraction of the
              full extent, which is a shape fact rather than a number for one car.
  side glass  cast along X over (y,z); flood-fill the see-through cells; keep only components that
              do NOT touch the scan edge -- one that does is running off into open air, not a
              window. Trace each ROW so the pane follows the pillar slant instead of being a box.
  raked glass cast along the PANE'S OWN NORMAL, not along Z. A front-to-back ray finds where you can
              see through the entire cabin, which is the intersection of the windscreen and rear
              apertures -- a rectangle that loses both shapes, and whose corners clip the A-pillar
              because a z-ray threads past a slanted pillar where the raked pane cannot.

Usage: python3 tools/gen_vehicle_glass.py <body.txt> <out_base.txt>
Writes <out_base>_<label>.txt per pane: windshield, rear, l_front, r_front, l_rear, r_rear.
"""
import sys, os, numpy as np
from collections import deque

SRC = sys.argv[1] if len(sys.argv) > 1 else 'game/content/sedan_body.txt'
OUT = sys.argv[2] if len(sys.argv) > 2 else 'game/content/sedan_glass.txt'
STEP, INSET, INSET_F = 0.03, 0.004, 0.045
# The traced span runs first-open-cell..last-open-cell, so every edge stops up to a grid step SHORT
# of the real aperture and you get a gap of daylight all the way round the glass. Real glazing sits
# in a seal and tucks UNDER the frame, so grow each pane outward past the opening; the frame hides
# the overlap, and the pane is inset behind it so the overlap cannot z-fight. (strawberry 2026-08-31:
# "theres a gap around all of the windows".)
GROW = 0.055

V, F = [], []
for ln in open(SRC):
    p = ln.split()
    if not p: continue
    if p[0] == 'v': V.append([float(x) for x in p[1:4]])
    elif p[0] == 'f':
        vi = [int(t.split('/')[0]) - 1 for t in p[1:]]
        for i in range(1, len(vi) - 1): F.append((vi[0], vi[i], vi[i + 1]))
V = np.array(V); T = V[np.array(F)]
A, B, C = T[:, 0], T[:, 1], T[:, 2]; E1, E2 = B - A, C - A

def nhits(o, d, tmax=None):
    """Triangles crossed along the ray. tmax bounds it to a segment, which is what "can I reach this
    point from outside" needs -- an unbounded ray also counts everything BEHIND the target."""
    d = d / np.linalg.norm(d); P = np.cross(d, E2); det = (E1 * P).sum(1)
    ok = np.abs(det) > 1e-9; inv = np.where(ok, 1.0 / np.where(ok, det, 1), 0)
    Tv = o - A; u = (Tv * P).sum(1) * inv; Q = np.cross(Tv, E1)
    v = (d * Q).sum(1) * inv; t = (E2 * Q).sum(1) * inv
    m = ok & (u >= -1e-6) & (v >= -1e-6) & (u + v <= 1 + 1e-6) & (t > 1e-4)
    if tmax is not None: m &= (t < tmax)
    return int(m.sum())

# ---- 1. BELTLINE + CABIN BAND, from the body's own silhouette.
ylo, yhi = V[:, 1].min(), V[:, 1].max()
slices = []
for y in np.arange(ylo, yhi, 0.05):
    m = (V[:, 1] >= y) & (V[:, 1] < y + 0.05)
    if m.sum() < 3: continue
    slices.append((y, np.ptp(V[m, 2]), np.abs(V[m, 0]).max()))
if not slices: sys.exit("no geometry")
full_z = max(s[1] for s in slices)
cabin = [s for s in slices if s[1] < full_z * 0.72]        # greenhouse: markedly shorter than the body
cabin = [s for s in cabin if s[0] > (ylo + yhi) * 0.5]     # ...and in the upper half, not the floor pan
if not cabin: sys.exit("no greenhouse found (no slice is much shorter than the body)")
belt = min(s[0] for s in cabin)
roof = max(s[0] for s in cabin) + 0.05
XS = max(s[2] for s in cabin)                              # side plane = widest point of the cabin
band_lo, band_hi = belt + 0.06, roof - 0.06
print(f"  derived: belt {belt:.2f}  roof {roof:.2f}  cabin band {band_lo:.2f}..{band_hi:.2f}  side |x| {XS:.2f}")

# ---- 1b. CAB Z-SPAN. The greenhouse sits over ONE stretch of the body. A pickup is the case that
# breaks a whole-body scan: a tall cab between a LOW nose and a LOW bed. Tracing the frontmost solid
# across every z at cabin height then picks up the NOSE on the rows just above the beltline and the
# real windscreen on the rows above, and the least-squares plane splits the difference -- it leaves
# the cab entirely and hangs the pane in open air over the bonnet. So keep only the z where the body
# actually reaches greenhouse height, which is a shape fact like the beltline above.
# Span by FIRST..LAST qualifying slice, never a contiguous run: these are low-poly bodies, most thin
# z-slices hold no vertices at all, and treating "no vertices here" as "cab ends here" cut the sedan
# greenhouse to 0.8 m and dropped four of its six panes.
zres = 0.10
need = belt + 0.55 * (roof - belt)
zq = [z0 for z0 in np.arange(V[:, 2].min(), V[:, 2].max(), zres)
      if ((V[:, 2] >= z0) & (V[:, 2] < z0 + zres)).sum() >= 1
      and V[(V[:, 2] >= z0) & (V[:, 2] < z0 + zres), 1].max() >= need]
if not zq: sys.exit("no cab: no z-slice reaches greenhouse height")
cab_z0, cab_z1 = min(zq) - 0.20, max(zq) + zres + 0.20
print(f"  cab z-span {cab_z0:.2f}..{cab_z1:.2f}  (body z {V[:,2].min():.2f}..{V[:,2].max():.2f})")
# Applied to the RAKED fit only. Narrowing the side-glass grid to the cab makes the front and rear
# side apertures touch the scan edge, and the 'must not run off into open air' test then discards
# them -- measured: it cost the golf both rear side panes and moved its rear screen 0.59 m.

ys = np.arange(band_lo - 0.10, band_hi + 0.10, STEP)
zs = np.arange(V[:, 2].min() - 0.1, V[:, 2].max() + 0.1, STEP)
g = np.array([[nhits(np.array([-(XS + 5), y, z]), np.array([1.0, 0, 0])) for z in zs] for y in ys])

panes = []
def add(label, quads): panes.append((label, quads))

# ---- 2. SIDE GLASS: enclosed see-through components, traced per row.
band = (ys > band_lo) & (ys < band_hi); ysb = ys[band]; sub = (g == 0)[band]
lab = -np.ones(sub.shape, int); cur = 0
for i in range(sub.shape[0]):
    for j in range(sub.shape[1]):
        if not sub[i, j] or lab[i, j] >= 0: continue
        q = deque([(i, j)]); lab[i, j] = cur
        while q:
            a, b = q.popleft()
            for da, db in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                na, nb = a + da, b + db
                if 0 <= na < sub.shape[0] and 0 <= nb < sub.shape[1] and sub[na, nb] and lab[na, nb] < 0:
                    lab[na, nb] = cur; q.append((na, nb))
        cur += 1
comps = []
for c in range(cur):
    m = lab == c
    if m.sum() < 30: continue
    zi = np.where(m)[1]
    if zi.min() == 0 or zi.max() == sub.shape[1] - 1: continue
    rows = []
    for i in range(sub.shape[0]):
        js = np.where(m[i])[0]
        if len(js) >= 2: rows.append((ysb[i], zs[js.min()] - GROW, zs[js.max()] + GROW))
    if len(rows) >= 2:
        rows = [(rows[0][0] - GROW, rows[0][1], rows[0][2])] + rows + [(rows[-1][0] + GROW, rows[-1][1], rows[-1][2])]
        comps.append(rows)
comps.sort(key=lambda r: (min(x[1] for x in r) + max(x[2] for x in r)) / 2)   # ascending z = FRONT -> rear
for n, rows in enumerate(comps):
    # ...and since front is -z, the LOWEST-z component is the front door glass. This was inverted too.
    # A 2-door has ONE side aperture and it is the DOOR glass, i.e. 'front'. Falling through to the
    # n == len-1 branch names it 'rear', which then coexists with a stale l_front from an earlier run
    # and the car reports six panes with two of them phantom.
    if len(comps) == 1:      which = 'front'
    elif n == 0:             which = 'front'
    elif n == len(comps) - 1: which = 'rear'
    else:                    which = f'mid{n}'
    for sx, side in ((+1, 'r'), (-1, 'l')):
        qs = []; x = sx * (XS - INSET)
        for k in range(len(rows) - 1):
            y1, a1, b1 = rows[k]; y2, a2, b2 = rows[k + 1]
            qs.append([np.array([x, y1, a1]), np.array([x, y1, b1]),
                       np.array([x, y2, b2]), np.array([x, y2, a2])])
        add(f"{side}_{which}", qs)

# ---- 3. RAKED GLASS: fit the pillar edge, then re-scan ALONG THE PANE'S OWN NORMAL.
solid = g > 0
zmid = (cab_z0 + cab_z1) / 2           # split front/rear about the CAB, not the whole body
# FRONT IS -Z on every vehicle in this game -- verified against the specs, not assumed: every
# SpotPos (headlights) sits at negative z and every TailPos at positive z, all 11 road cars.
# So the frontmost aperture is the MINIMUM z. take_max=True walks the +z end, which is the REAR
# screen. The geometry was always fitted correctly per aperture; only the two labels were
# swapped -- which is not cosmetic, because a shot that breaks 'windshield' was breaking the
# rear window, and Vehicle.ResolveHitGlass maps a hit to a pane by name.
for take_max, label in ((True, 'rear'), (False, 'windshield')):
    e = []
    for i, y in enumerate(ys):
        if not (band_lo < y < band_hi): continue
        js = [j for j, z in enumerate(zs)
              if solid[i, j] and cab_z0 <= z <= cab_z1 and ((z > zmid) if take_max else (z < zmid))]
        if js: e.append((y, zs[max(js)] if take_max else zs[min(js)]))
    if len(e) < 3: continue
    e = np.array(e)
    m_, c_ = np.linalg.lstsq(np.vstack([e[:, 0], np.ones(len(e))]).T, e[:, 1], rcond=None)[0]
    nrm = np.array([0.0, -m_, 1.0]); nrm /= np.linalg.norm(nrm)
    if not take_max: nrm = -nrm
    ax = np.array([1.0, 0.0, 0.0])                      # in-plane: across the car
    ay = np.cross(nrm, ax); ay /= np.linalg.norm(ay)    # in-plane: up the rake
    org = np.array([0.0, e[:, 0].mean(), m_ * e[:, 0].mean() + c_])
    us = np.arange(-XS, XS + 1e-9, STEP)
    vs = np.arange(-(band_hi - band_lo), (band_hi - band_lo), STEP)
    open_ = np.zeros((len(vs), len(us)), bool)
    for iv, vv in enumerate(vs):
        for iu, uu in enumerate(us):
            p = org + ax * uu + ay * vv
            if not (band_lo - 0.05 < p[1] < band_hi + 0.05): continue
            # From OUTSIDE the car along -nrm, stopping AT the plane: "is this point of the pane
            # reachable from outside without passing through bodywork". Casting from p - nrm*4 starts
            # inside the cabin and shoots out through the floor, which finds nothing anywhere.
            # Test at the pane's ACTUAL inset position, not on the fitted plane: the fit passes through
            # the pillar's front face, so a point exactly on it hits the pillar at t == tmax and the
            # comparison drops it -- the pillars vanish and the "aperture" becomes the whole car width.
            pt = p - nrm * INSET_F
            open_[iv, iu] = nhits(pt + nrm * 4.0, -nrm, tmax=4.0 - 1e-3) == 0
    if not open_.any(): print(f"  !! {label}: no aperture along its normal"); continue
    rows = []
    for iv in range(len(vs)):
        js = np.where(open_[iv])[0]
        if len(js) < 2: continue
        runs = np.split(js, np.where(np.diff(js) != 1)[0] + 1)
        runs = [r for r in runs if r[0] > 0 and r[-1] < len(us) - 1]   # must be FRAMED both sides
        if not runs: continue
        r = max(runs, key=len)
        if us[r[-1]] - us[r[0]] < 0.25: continue
        rows.append((vs[iv], us[r[0]] - GROW, us[r[-1]] + GROW))
    if len(rows) < 2: print(f"  !! {label}: aperture too small"); continue
    rows = [(rows[0][0] - GROW, rows[0][1], rows[0][2])] + rows + [(rows[-1][0] + GROW, rows[-1][1], rows[-1][2])]
    qs = []
    for k in range(len(rows) - 1):
        v1, a1, b1 = rows[k]; v2, a2, b2 = rows[k + 1]
        P = lambda u, v: org + ax * u + ay * v - nrm * INSET_F
        qs.append([P(a1, v1), P(b1, v1), P(b2, v2), P(a2, v2)])
    add(label, qs)
    print(f"  {label}: {len(rows)} rows, width {max(b - a for _, a, b in rows):.2f}")

base = OUT[:-4] if OUT.endswith('.txt') else OUT
# Clear this vehicle's old panes first. Pane files are per-label, so a run that derives FEWER panes
# than the last one leaves the extras on disk and Vehicle loads them: the roadster reported 6 panes,
# 2 of them from a previous naming scheme, and the count assertion was the only thing that noticed.
for _lbl in ('windshield', 'rear', 'l_front', 'r_front', 'l_rear', 'r_rear',
             'l_mid1', 'r_mid1', 'l_mid2', 'r_mid2'):
    _f = f"{base}_{_lbl}.txt"
    if os.path.exists(_f): os.remove(_f)
for label, quads in panes:
    verts, faces = [], []
    for q in quads:
        b = len(verts) + 1; verts.extend(q)
        faces.append((b, b + 1, b + 2)); faces.append((b, b + 2, b + 3))
    with open(f"{base}_{label}.txt", 'w') as fh:
        fh.write(f"# GENERATED by tools/gen_vehicle_glass.py -- pane '{label}'. Do not hand-edit.\n")
        fh.write("# One file per pane so each breaks independently (Vehicle.GlassPaneLabels).\n")
        fh.write(f"g {label}\n")
        for v in verts: fh.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
        for f in faces: fh.write(f"f {f[0]} {f[1]} {f[2]}\n")
    print(f"  {label:<12} {len(quads):>3} quads -> {os.path.basename(base)}_{label}.txt")
print(f"{len(panes)} panes")
