#!/usr/bin/env python3
"""Generate window glass panes for a vehicle body mesh (sedan_glass.txt).

WHY THIS EXISTS: the greenhouse is modelled as watertight solid pillars + a roof slab, so the
windows are GAPS BETWEEN geometry, not holes IN a surface -- a boundary-edge test finds 4 edges on
the whole body and would tell you the car has no windows. So we raycast instead and take the
regions you can actually see through.

  side windows : cast along X over a (y,z) grid, flood-fill the see-through cells, and keep only
                 components that do NOT touch the grid edge (one that does is running off the
                 frame into open air, not a window). Trace each component ROW BY ROW so the pane
                 follows the A/C-pillar slant instead of being a bounding rectangle.
  windshield   : cast along Z but count hits only inside that window's own z band -- an unbanded
  / rear glass   ray is open only where windshield AND rear are both open, whose intersection is a
                 rectangle and loses the shape. Emit ONE quad on the least-squares rake plane: the
                 rake is linear to ~21mm (windshield) / ~34mm (rear) against a 30mm grid step, and
                 a strip of ~26 thin alpha quads visibly BANDS whatever is behind it.

Panes are inset inside the frame so the pillars occlude the glass, not the reverse.
Usage: python3 tools/gen_vehicle_glass.py [body.txt] [out.txt]
"""
import sys, numpy as np
from collections import deque

SRC = sys.argv[1] if len(sys.argv) > 1 else 'game/content/sedan_body.txt'
OUT = sys.argv[2] if len(sys.argv) > 2 else 'game/content/sedan_glass.txt'
STEP, XS, INSET, XW, INSET_F, XF = 0.03, 1.230, 0.004, 0.940, 0.045, 1.25

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

def hits(o, d):
    d = d / np.linalg.norm(d); P = np.cross(d, E2); det = (E1 * P).sum(1)
    ok = np.abs(det) > 1e-9; inv = np.where(ok, 1.0 / np.where(ok, det, 1), 0)
    Tv = o - A; u = (Tv * P).sum(1) * inv; Q = np.cross(Tv, E1)
    v = (d * Q).sum(1) * inv; t = (E2 * Q).sum(1) * inv
    m = ok & (u >= -1e-6) & (v >= -1e-6) & (u + v <= 1 + 1e-6) & (t > 1e-4)
    return o[2] + t[m] if d[2] else m.sum()

ys = np.arange(1.00, 2.25, STEP); zs = np.arange(-1.70, 1.95, STEP)
g = np.array([[hits(np.array([-6.0, y, z]), np.array([1.0, 0, 0])) for z in zs] for y in ys])

panes = []   # (label, quads)

def quad(qs, a, b, c, d): qs.append([a, b, c, d])

band = (ys > 1.11) & (ys < 1.92); ysb = ys[band]; sub = (g == 0)[band]
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
for c in range(cur):
    m = lab == c
    if m.sum() < 40: continue
    zi = np.where(m)[1]
    if zi.min() == 0 or zi.max() == sub.shape[1] - 1: continue
    rows = []
    for i in range(sub.shape[0]):
        js = np.where(m[i])[0]
        if len(js) >= 2: rows.append((ysb[i], zs[js.min()], zs[js.max()]))
    if len(rows) < 2: continue
    zmid = (min(r[1] for r in rows) + max(r[2] for r in rows)) / 2
    which = 'rear' if zmid < 0.2 else 'front'
    for sx, side in ((+1, 'r'), (-1, 'l')):
        qs = []; x = sx * (XS - INSET)
        for k in range(len(rows) - 1):
            y1, a1, b1 = rows[k]; y2, a2, b2 = rows[k + 1]
            quad(qs, np.array([x, y1, a1]), np.array([x, y1, b1]),
                     np.array([x, y2, b2]), np.array([x, y2, a2]))
        panes.append((f"{side}_{which}", qs))

solid = g > 0
for zr, take_max, label in (((0.80, 1.80), True, 'windshield'), ((-1.80, -0.60), False, 'rear')):
    e = []
    for i, y in enumerate(ys):
        if not (1.11 < y < 1.92): continue
        js = [j for j, z in enumerate(zs) if zr[0] <= z <= zr[1] and solid[i, j]]
        if js: e.append((y, zs[max(js)] if take_max else zs[min(js)]))
    if len(e) < 2: continue
    e = np.array(e)
    m_, c_ = np.linalg.lstsq(np.vstack([e[:, 0], np.ones(len(e))]).T, e[:, 1], rcond=None)[0]
    ylo, yhi = e[:, 0].min(), e[:, 0].max(); zlo, zhi = m_ * ylo + c_, m_ * yhi + c_
    nrm = np.array([0.0, -m_, 1.0]); nrm /= np.linalg.norm(nrm)
    off = -nrm * INSET_F * (1.0 if take_max else -1.0)
    qs = []
    quad(qs, np.array([-XW, ylo, zlo]) + off, np.array([XW, ylo, zlo]) + off,
             np.array([XW, yhi, zhi]) + off, np.array([-XW, yhi, zhi]) + off)
    panes.append((label, qs))

import os
base = OUT[:-4] if OUT.endswith('.txt') else OUT
for label, quads in panes:
    verts = []; faces = []
    for q in quads:
        b = len(verts) + 1; verts.extend(q)
        faces.append((b, b + 1, b + 2)); faces.append((b, b + 2, b + 3))
    path = f"{base}_{label}.txt"
    with open(path, 'w') as fh:
        fh.write(f"# GENERATED by tools/gen_vehicle_glass.py -- pane '{label}'. Do not hand-edit.\n")
        fh.write("# One file per pane so each breaks independently (Vehicle.GlassPaneLabels).\n")
        fh.write(f"g {label}\n")
        for v in verts: fh.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
        for f in faces: fh.write(f"f {f[0]} {f[1]} {f[2]}\n")
    print(f"  {label:<12} {len(quads):>3} quads -> {os.path.basename(path)}")
print(f"{len(panes)} panes")
