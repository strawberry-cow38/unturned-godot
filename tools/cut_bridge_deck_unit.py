#!/usr/bin/env python3
"""Cut a bridge span into a TILEABLE deck unit plus its pier.

Master 2026-10-09, after the layer split: "idea is to turn these into splines", and then, asked whether
bridges have to curve: "yes they need to bend". That settles it -- a whole 48 m span cannot chord a bend.
Two rigid tiles meeting at a turn of Pitch/R part at their outer corners by HalfWidth*Pitch/R, and at 8.5 m
half-width over a 48 m span that is 2.04 m at R=200. tinyclaw measured the real highway: 355 bridge
stretches, tightest radius 301 m, median 1979 m, only 78 straight enough for a whole span. So the deck has
to become a short repeat unit.

⭐ THE UNIT LENGTH IS READ OFF THE PAINT, NOT CHOSEN. tinyclaw and I had settled on a round 4 m from the
geometry alone. Then the texture said otherwise: 12 of its 256 columns vary DOWN the image, which is the
dashed lane marking, and its run lengths are 32 texels painted / 32 blank -- a 64-texel period. The surface
quad maps v 0.2158..0.9816 over 48 m, i.e. 8.17 texels/m, so one dash period is 7.835 m on the ground.
Identical tiles each showing 4 m of that pattern do not reproduce it; they HALVE it. A unit therefore spans
a whole number of dash periods or the road markings stutter at every joint.
[[feedback_read_the_data_dont_infer_it]]

⭐ AND THE CUT IS EXACT, NOT RE-AUTHORED. Bridge_Line_1's deck carries vertices at Y -24 and +24 and NOWHERE
between -- a pure prism -- so shortening it is arithmetic on a cross-section that is already constant, and
this refuses on any prop where that is not true. Bridge_Line_0's pier is welded into its deck with a top
plate at Y +/-3.0, and Bridge_Line_Cap_1 is a 98 m ramp with 16 Z levels; neither is a prism and neither is
cut here.

⚠ v IS SCALED WITH THE GEOMETRY. Shortening the deck without shortening its v range would stretch the same
48 m of paint over the unit's few metres, so the centre line would come out coarse by the ratio. The unit's
v range is pinned to a dash-period boundary (multiples of 64 texels) so consecutive tiles continue the
rhythm instead of restarting mid-dash -- which the shipped 48 m prop actually does, since 392 texels is
6.127 periods. Retail never tiled these, so there was nothing to get right.

⚠ THESE PROPS ARE Z-UP with their length on +Y. Height is +Z.

Usage:
  cut_bridge_deck_unit.py Bridge_Line_1 [--periods 1] [--dry-run]
"""
import argparse, math, os, shutil, subprocess, sys


OBJ_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'game', 'content', 'objects')


def read_obj(path):
    v, vt, vn, faces = [], [], [], []
    for line in open(path):
        t = line.split()
        if not t:
            continue
        if t[0] == 'v':
            v.append(tuple(float(x) for x in t[1:4]))
        elif t[0] == 'vt':
            vt.append(tuple(float(x) for x in t[1:3]))
        elif t[0] == 'vn':
            vn.append(tuple(float(x) for x in t[1:4]))
        elif t[0] == 'f':
            faces.append([tuple(int(x) if x else 0 for x in c.split('/')) for c in t[1:]])
    return v, vt, vn, faces


def components(v, faces):
    """Weld by POSITION and union-find the faces: an OBJ split per-face for normals still shares geometry."""
    key, rep = {}, []
    for p in v:
        k = tuple(round(c, 4) for c in p)
        key.setdefault(k, len(key))
        rep.append(key[k])
    par = list(range(len(key)))

    def find(a):
        while par[a] != a:
            par[a] = par[par[a]]
            a = par[a]
        return a

    for f in faces:
        r = [rep[c[0] - 1] for c in f]
        for i in range(1, len(r)):
            a, b = find(r[0]), find(r[i])
            if a != b:
                par[a] = b
    out = {}
    for f in faces:
        out.setdefault(find(rep[f[0][0] - 1]), []).append(f)
    return list(out.values())


def write_obj(path, v, vt, vn, faces, header):
    iv, ivt, ivn = {}, {}, {}
    ov, ovt, ovn, of = [], [], [], []
    for f in faces:
        row = []
        for (a, b, c) in f:
            if a not in iv:
                iv[a] = len(ov) + 1
                ov.append(v[a - 1])
            if b and b not in ivt:
                ivt[b] = len(ovt) + 1
                ovt.append(vt[b - 1])
            if c and c not in ivn:
                ivn[c] = len(ovn) + 1
                ovn.append(vn[c - 1])
            row.append((iv[a], ivt.get(b, 0), ivn.get(c, 0)))
        of.append(row)
    with open(path, 'w') as fh:
        fh.write(f"# {header} (tools/cut_bridge_deck_unit.py)\n")
        for x in ov:
            fh.write('v %.6f %.6f %.6f\n' % x)
        for x in ovt:
            fh.write('vt %.6f %.6f\n' % x)
        for x in ovn:
            fh.write('vn %.6f %.6f %.6f\n' % x)
        for row in of:
            fh.write('f ' + ' '.join(f"{a}/{b or ''}/{c or ''}" for a, b, c in row) + '\n')
    return len(ov), len(of)


def dash_period_texels(png, height):
    """Run lengths down the first column that varies along v -- the dashed lane marking."""
    out = subprocess.run(['convert', png, '-depth', '8', 'txt:-'], capture_output=True, text=True).stdout
    px = {}
    for line in out.splitlines()[1:]:
        if '#' not in line:
            continue
        pos, rest = line.split(':', 1)
        x, y = (int(q) for q in pos.split(','))
        px[(x, y)] = rest.split('#')[1].split()[0][:6].lower()
    w = max(k[0] for k in px) + 1
    for x in range(w):
        col = [px[(x, y)] for y in range(height)]
        if len(set(col)) < 2:
            continue
        runs, cur, n = [], col[0], 0
        for c in col:
            if c == cur:
                n += 1
            else:
                runs.append(n)
                cur, n = c, 1
        runs.append(n)
        if len(runs) >= 4:
            return runs[1] + runs[2]          # one mark plus one gap, away from the partial first run
    return None


def end_loop(v, faces, yplane, tol=1e-3):
    """The closed boundary loop the deck's open end leaves behind.

    ⭐ Both retail's Bridge_Line_1 and the unit cut from it are open-ended TUBES -- zero faces at either end
    plane -- because retail closes a run with its Cap props rather than capping each span. Tiled on its own
    that leaves a hole you see straight into, which is what master meant by "close up the ends".

    Every edge lying in the end plane bounds exactly one side face, so the whole set IS the loop; chaining
    them is just bookkeeping, not a guess about which edges count."""
    key, rep = {}, []
    for q in v:
        k = tuple(round(c, 4) for c in q)
        key.setdefault(k, len(key))
        rep.append(key[k])
    pos = {i: k for k, i in key.items()}
    adj = {}
    for f in faces:
        r = [rep[c[0] - 1] for c in f]
        for a, b in ((r[0], r[1]), (r[1], r[2]), (r[2], r[0])):
            if abs(pos[a][1] - yplane) <= tol and abs(pos[b][1] - yplane) <= tol and a != b:
                adj.setdefault(a, set()).add(b)
                adj.setdefault(b, set()).add(a)
    if not adj:
        return None
    start = next(iter(adj))
    loop, prev, cur = [start], None, start
    while True:
        nxt = [w for w in adj[cur] if w != prev]
        if not nxt:
            return None
        nxt = nxt[0]
        if nxt == start:
            break
        loop.append(nxt)
        prev, cur = cur, nxt
        if len(loop) > len(adj) + 2:
            return None
    return [(pos[i][0], pos[i][2]) for i in loop]


def ear_clip(poly):
    """Triangulate a simple polygon. ⚠ A FAN WOULD BE WRONG: the profile is concave at the two points where
    the roadway meets the inside of each parapet, and a fan there spills triangles outside the section."""
    pts = list(poly)
    n = len(pts)
    area2 = sum(pts[i][0] * pts[(i + 1) % n][1] - pts[(i + 1) % n][0] * pts[i][1] for i in range(n))
    if area2 < 0:
        pts.reverse()                                  # work CCW
    idx = list(range(len(pts)))
    out = []
    guard = 0
    while len(idx) > 3 and guard < 10000:
        guard += 1
        for j in range(len(idx)):
            a, b, c = idx[j - 1], idx[j], idx[(j + 1) % len(idx)]
            ax, ay = pts[a]; bx, by = pts[b]; cx, cy = pts[c]
            cross = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)
            if cross <= 0:
                continue                               # reflex, not an ear
            bad = False
            for m in idx:
                if m in (a, b, c):
                    continue
                px, py = pts[m]
                d1 = (bx - ax) * (py - ay) - (by - ay) * (px - ax)
                d2 = (cx - bx) * (py - by) - (cy - by) * (px - bx)
                d3 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx)
                if d1 >= 0 and d2 >= 0 and d3 >= 0:
                    bad = True
                    break
            if not bad:
                out.append((pts[a], pts[b], pts[c]))
                idx.pop(j)
                break
        else:
            return None
    if len(idx) == 3:
        out.append((pts[idx[0]], pts[idx[1]], pts[idx[2]]))
    return out


def write_cap(path, tris, uv, src_name):
    """A flat cap in the XZ plane at Y=0, facing +Y, pinned to one concrete texel.

    Authored at Y=0 rather than at the unit's end so the tool can drop it straight on the run's end plane,
    and ParseObj decides winding per triangle off the authored normal, so emitting vn=+Y is enough."""
    with open(path, 'w') as fh:
        fh.write(f"# end cap for {src_name}, section ear-clipped from its open end "
                 f"(tools/cut_bridge_deck_unit.py)\n")
        for t in tris:
            for (x, z) in t:
                fh.write('v %.6f 0.000000 %.6f\n' % (x, z))
        fh.write('vt %.6f %.6f\n' % uv)
        fh.write('vn 0.000000 1.000000 0.000000\n')
        for i in range(len(tris)):
            a, b, c = 3 * i + 1, 3 * i + 2, 3 * i + 3
            fh.write(f"f {a}/1/1 {b}/1/1 {c}/1/1\n")
    return len(tris)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('prop')
    ap.add_argument('--periods', type=int, default=1, help='dash periods per deck unit')
    ap.add_argument('--objects-dir', default=OBJ_DIR)
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()

    src = os.path.join(a.objects_dir, a.prop + '.obj')
    tex = os.path.join(a.objects_dir, a.prop + '_tex.png')
    for f in (src, tex):
        if not os.path.exists(f):
            sys.exit(f"missing {f}")
    v, vt, vn, faces = read_obj(src)
    h = int(subprocess.run(['identify', '-format', '%h', tex], capture_output=True, text=True).stdout)

    # DECK vs PIER by connected component, not by a height I picked: the deck is the component that reaches
    # the top, the piers are whatever hangs separately below it.
    comps = components(v, faces)
    top = max(v[c[0] - 1][2] for f in faces for c in f)
    deck, pier = [], []
    for comp in comps:
        (deck if max(v[c[0] - 1][2] for g in comp for c in g) >= top - 1e-3 else pier).extend(comp)
    if not pier:
        sys.exit(f"[cut] REFUSING {a.prop}: no pier hangs free of the deck -- it is welded, and this tool "
                 f"would cut through it")

    ys = sorted({round(v[c[0] - 1][1], 3) for f in deck for c in f})
    if len(ys) != 2:
        sys.exit(f"[cut] REFUSING {a.prop}: the deck is not a prism along Y (levels {ys}) -- shortening it "
                 f"would move real features, not just its length")
    y0, y1 = ys
    span = y1 - y0

    # ⚠ ONLY THE FACES THAT ACTUALLY CARRY A v SPAN. Every non-roadway face on these props is pinned to a
    # single palette texel at v ~ 0.016, so taking the min over the whole deck returns that pin instead of
    # the roadway's own start -- which read 494 texels over the span instead of 392 and sized the unit at
    # 6.21 m instead of 7.83. A min is only as good as the set it is over.
    # [[feedback_an_instrument_must_name_what_it_measured]]
    def vspan(f):
        return (max(vt[c[1] - 1][1] for c in f) - min(vt[c[1] - 1][1] for c in f)) if all(c[1] for c in f) else 0.0
    # ⚠ Relative to the LARGEST span, not an absolute epsilon: the pinned faces are not exactly pinned --
    # they sit on two texels 0.0002 apart, which sails through any fixed 1e-4 and dragged the roadway's
    # start from 0.2158 down to 0.0160.
    widest = max((vspan(f) for f in deck), default=0.0)
    painted = [f for f in deck if vspan(f) > widest * 0.5] if widest > 1e-3 else []
    if not painted:
        sys.exit(f"[cut] REFUSING {a.prop}: no face carries a v span -- there is no roadway paint to scale")
    vs = [vt[c[1] - 1][1] for f in painted for c in f]
    vlo, vhi = min(vs), max(vs)
    texels = (vhi - vlo) * h
    per = dash_period_texels(tex, h)
    if not per:
        sys.exit(f"[cut] REFUSING {a.prop}: no dashed column in the texture -- cannot read a unit length")
    mpt = span / texels
    unit = per * a.periods * mpt

    print(f"[cut] {a.prop}: span {span:.1f} m, deck {len(deck)} tris (prism, Y {y0:+.1f}..{y1:+.1f}), "
          f"pier {len(pier)} tris")
    print(f"[cut]   paint: v {vlo:.4f}..{vhi:.4f} = {texels:.1f} texels over {span:.0f} m "
          f"= {1/mpt:.2f} texel/m; dash period {per} texels = {per*mpt:.4f} m")
    print(f"[cut]   -> unit {unit:.4f} m ({a.periods} dash period(s)); the span is {texels/per:.3f} periods, "
          f"so the shipped prop never tiled cleanly with itself")
    if a.dry_run:
        return

    # ⚠ v anchored to a dash-period BOUNDARY (a multiple of `per` texels) so tiles continue the rhythm.
    nv0 = math.floor(vlo * h / per) * per
    vu0, vu1 = nv0 / h, (nv0 + per * a.periods) / h
    sy = unit / span

    nv = list(v)
    nvt = list(vt)
    for f in deck:
        for (ai, bi, _) in f:
            nv[ai - 1] = (v[ai - 1][0], v[ai - 1][1] * sy, v[ai - 1][2])
        if f in painted:
            for (_, bi, _) in f:
                t = (vt[bi - 1][1] - vlo) / (vhi - vlo)
                nvt[bi - 1] = (vt[bi - 1][0], vu0 + t * (vu1 - vu0))

    for nm, g, src_v, src_vt, note in (
            ('Deck_Unit', deck, nv, nvt, f"{unit:.4f} m tileable deck unit cut from {a.prop}"),
            ('Pier', pier, v, vt, f"pier of {a.prop}, placed independently of the deck")):
        name = f"{a.prop}_{nm}"
        out = os.path.join(a.objects_dir, name + '.obj')
        cv, cf = write_obj(out, src_v, src_vt, vn, g, note)
        shutil.copyfile(tex, os.path.join(a.objects_dir, name + '_tex.png'))
        pts = [src_v[c[0] - 1] for f in g for c in f]
        bb = [(min(c), max(c)) for c in zip(*pts)]
        print(f"[cut]   {name:28s} {cv:4d}v {cf:4d}t  X {bb[0][0]:+7.2f}..{bb[0][1]:+7.2f}  "
              f"Y {bb[1][0]:+7.2f}..{bb[1][1]:+7.2f}  Z {bb[2][0]:+7.2f}..{bb[2][1]:+7.2f}")

    # ---- END CAP. Master, on the first bridge render: "close up the ends."
    loop = end_loop(nv, deck, nv[deck[0][0][0] - 1][1] if False else max(nv[c[0] - 1][1] for f in deck for c in f))
    if loop is None:
        print("[cut]   ⚠ could not chain the deck's end profile -- no cap written")
    else:
        tris = ear_clip(loop)
        if tris is None:
            print(f"[cut]   ⚠ could not triangulate the {len(loop)}-point end profile -- no cap written")
        else:
            # the texel the deck's own CONCRETE faces are pinned to, so the cap matches rather than guessing
            pin = min(((vspan(f), f) for f in deck if vspan(f) <= 1e-4 and all(c[1] for c in f)),
                      default=(None, None))[1]
            uv = tuple(vt[pin[0][1] - 1]) if pin else (0.99, 0.016)
            cname = f"{a.prop}_Deck_Cap"
            ct = write_cap(os.path.join(a.objects_dir, cname + '.obj'), tris, uv, a.prop)
            shutil.copyfile(tex, os.path.join(a.objects_dir, cname + '_tex.png'))
            xs = [x for t in tris for (x, _) in t]; zs = [z for t in tris for (_, z) in t]
            print(f"[cut]   {cname:28s} {ct*3:4d}v {ct:4d}t  {len(loop)}-point section, "
                  f"X {min(xs):+7.2f}..{max(xs):+7.2f}  Z {min(zs):+7.2f}..{max(zs):+7.2f}")


if __name__ == '__main__':
    main()
