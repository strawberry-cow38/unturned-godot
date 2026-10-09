#!/usr/bin/env python3
"""Split a BRIDGE prop into road surface / walls / underbelly.

Master 2026-10-09: "turning the bridge line/bridge line caps into broken-apart props (so like the walls,
underbelly, road surface as separate pieces.)" -- the modular pieces a bridge-building tool would tile, in
the week tinyclaw's highway pass started marking 59 bridge stretches on the 19 km map.

⭐ WHY THIS IS NOT split_prop_by_palette.py. That tool reads the authored per-triangle material assignment
straight out of the UVs, which is why the fence split was a measurement rather than an edit. It cannot do
this one: a bridge's parapet and its piers are the SAME concrete, so colour separates the asphalt and
nothing else. The remaining cut has to be structural -- but it can still be READ rather than invented.

⭐⭐ EVERY THRESHOLD HERE COMES OUT OF THE MESH. The roadway is not a height I picked: it is every
UPWARD-FACING face wearing the prop's road colour, and the road colour is itself read off as whichever
texel carries the most upward-facing area. Walls are then what stands above the deck and the underbelly is
what hangs below it. A rule like "the wall is the bit above z=0.5" is a rule somebody made up, and it is
wrong the first time a prop sits at a different datum. See [[feedback_read_the_data_dont_infer_it]].

⚠⚠ AND THE DECK IS NOT A PLANE. My first cut took the single Z level with the most upward area, which is
right for a mid-span and WRONG for both caps: Bridge_Line_Cap_1 is an approach ramp with no horizontal face
anywhere (it refused outright), and Bridge_Line_Cap_0's asphalt steps down 1 m at its end. A flat threshold
dumps every ramped piece of roadway into the underbelly. So the deck is sampled as a height PER STATION
along the span (DeckProfile) and each remaining face is judged against the deck directly above or below it.
A flat deck collapses to the old behaviour for free.

⚠ THESE PROPS ARE Z-UP. Ripped props carry their length on local +Y and their height on +Z (they are stood
up at placement with a baked ex=270). So "above the deck" is +Z here, NOT +Y, and a converter that assumes
Y-up will tip every piece on its side -- which is exactly how the ice item shipped lying down.

Usage:
  split_prop_by_structure.py Bridge_Line_0 [Bridge_Line_1 ...] [--register] [--dry-run]
"""
import argparse, os, shutil, subprocess, sys, uuid

OBJ_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'game', 'content', 'objects')
PARTS = ('Surface', 'Walls', 'Underbelly')


def read_obj(path):
    v, vt, vn, faces = [], [], [], []
    for line in open(path):
        t = line.split()
        if not t:
            continue
        if t[0] == 'v':
            v.append(tuple(t[1:4]))
        elif t[0] == 'vt':
            vt.append(tuple(t[1:3]))
        elif t[0] == 'vn':
            vn.append(tuple(t[1:4]))
        elif t[0] == 'f':
            faces.append([tuple(int(x) if x else 0 for x in c.split('/')) for c in t[1:]])
    return v, vt, vn, faces


def write_obj(path, v, vt, vn, faces, src_name, part):
    """Write the subset, reindexed so the part carries only the vertices it uses."""
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
        fh.write(f"# {part} of {src_name}, split on the measured deck plane "
                 f"(tools/split_prop_by_structure.py)\n")
        for x in ov:
            fh.write('v ' + ' '.join(x) + '\n')
        for x in ovt:
            fh.write('vt ' + ' '.join(x) + '\n')
        for x in ovn:
            fh.write('vn ' + ' '.join(x) + '\n')
        for row in of:
            fh.write('f ' + ' '.join(f"{a}/{b or ''}/{c or ''}" for a, b, c in row) + '\n')
    return len(ov), len(of)


def tri_area_and_normal(p):
    ax, ay, az = (p[1][i] - p[0][i] for i in range(3))
    bx, by, bz = (p[2][i] - p[0][i] for i in range(3))
    n = (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx)
    return 0.5 * (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5, n


def read_palette(png):
    """{(x, y): 'rrggbb'} via ImageMagick's txt: dump."""
    out = subprocess.run(['convert', png, '-depth', '8', 'txt:-'], capture_output=True, text=True).stdout
    px, w, h = {}, 0, 0
    for line in out.splitlines()[1:]:
        if '#' not in line:
            continue
        pos, rest = line.split(':', 1)
        x, y = (int(q) for q in pos.split(','))
        px[(x, y)] = rest.split('#')[1].split()[0][:6].lower()
        w, h = max(w, x + 1), max(h, y + 1)
    return px, w, h


def face_colour(f, vt, px, w, h):
    """The texel the face's UV corners land on, or None if they straddle two."""
    cols = set()
    for (_, b, _) in f:
        if not b:
            return None
        u, vv = (float(q) for q in vt[b - 1])
        cols.add(px[(min(w - 1, max(0, int(u * w))), min(h - 1, max(0, int((1.0 - vv) * h))))])
    return cols.pop() if len(cols) == 1 else None


def faces_up(v, faces):
    """Upward-facing faces, by DOMINANT axis rather than a slope cutoff -- a ramp is still roadway."""
    up = []
    for f in faces:
        p = [tuple(float(x) for x in v[c[0] - 1]) for c in f]
        area, n = tri_area_and_normal(p)
        if area <= 0:
            continue
        if abs(n[2]) >= max(abs(n[0]), abs(n[1])) and n[2] > 0:
            up.append((f, area, p))
    return up


def road_colour(v, vt, faces, px, w, h):
    """⭐ Whichever texel carries the most UPWARD-facing area. On every bridge here that is the asphalt or
    the deck planking by a wide margin -- the runner-up is the parapet cap. Returned with the runner-up so
    the caller can print the margin: a colour chosen by a hair is a colour that should not be trusted."""
    by_col = {}
    for f, area, _ in faces_up(v, faces):
        c = face_colour(f, vt, px, w, h)
        if c:
            by_col[c] = by_col.get(c, 0.0) + area
    if not by_col:
        return None, 0.0, 0.0
    rank = sorted(by_col.items(), key=lambda kv: -kv[1])
    return rank[0][0], rank[0][1], (rank[1][1] if len(rank) > 1 else 0.0)


class DeckProfile:
    """⭐ The deck's height as a function of position ALONG the span, sampled off the roadway faces
    themselves. On a flat mid-span every sample is the same number and this behaves exactly like a plane;
    on a ramped cap it follows the ramp, which is the whole reason it exists."""

    def __init__(self, surface_faces):
        rows = {}
        for _, area, p in surface_faces:
            y = round(sum(q[1] for q in p) / 3.0, 2)
            z = sum(q[2] for q in p) / 3.0
            a, zz = rows.get(y, (0.0, 0.0))
            rows[y] = (a + area, zz + z * area)
        self.pts = sorted((y, zz / a) for y, (a, zz) in rows.items())

    def at(self, y):
        pts = self.pts
        if y <= pts[0][0]:
            return pts[0][1]
        if y >= pts[-1][0]:
            return pts[-1][1]
        for i in range(1, len(pts)):
            if y <= pts[i][0]:
                (y0, z0), (y1, z1) = pts[i - 1], pts[i]
                t = 0.0 if y1 == y0 else (y - y0) / (y1 - y0)
                return z0 + (z1 - z0) * t
        return pts[-1][1]

    def span(self):
        return self.pts[0][1], self.pts[-1][1]


def classify(v, vt, faces, px, w, h, col, tol=1e-4):
    up = {id(f): True for f, _, _ in faces_up(v, faces)}
    surf = [(f, a, p) for f, a, p in faces_up(v, faces) if face_colour(f, vt, px, w, h) == col]
    if not surf:
        return None, None
    deck = DeckProfile(surf)
    surf_ids = {id(f) for f, _, _ in surf}
    out = {p: [] for p in PARTS}
    for f in faces:
        if id(f) in surf_ids:
            out['Surface'].append(f)
            continue
        p = [tuple(float(x) for x in v[c[0] - 1]) for c in f]
        yc = sum(q[1] for q in p) / 3.0
        zc = sum(q[2] for q in p) / 3.0
        out['Walls' if zc > deck.at(yc) + tol else 'Underbelly'].append(f)
    return out, deck


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('props', nargs='+')
    ap.add_argument('--objects-dir', default=OBJ_DIR)
    ap.add_argument('--register', action='store_true',
                    help='append the new names to guid_mesh.txt so the editor palette lists them')
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()

    made = []
    for prop in a.props:
        src = os.path.join(a.objects_dir, prop + '.obj')
        tex = os.path.join(a.objects_dir, prop + '_tex.png')
        for f in (src, tex):
            if not os.path.exists(f):
                sys.exit(f"missing {f}")
        v, vt, vn, faces = read_obj(src)
        px, w, h = read_palette(tex)
        col, area, runner = road_colour(v, vt, faces, px, w, h)
        if col is None:
            sys.exit(f"[split] REFUSING {prop}: no upward-facing textured face -- no roadway to split on")
        groups, deck = classify(v, vt, faces, px, w, h, col)
        if groups is None:
            sys.exit(f"[split] REFUSING {prop}: road colour #{col} matched no face")
        lo, hi = deck.span()
        print(f"[split] {prop}: {len(faces)} tris, road colour #{col} "
              f"({area:.0f} m2 of roadway vs {runner:.0f} m2 for the next surface up), "
              f"deck z {lo:+.2f}..{hi:+.2f} over {len(deck.pts)} station(s)")

        # ⚠ An empty part is a split that silently lost a piece, not a prop that happens to lack one: every
        # bridge section here has a deck, sides and an understructure. Refuse rather than ship two of three.
        empty = [p for p in PARTS if not groups[p]]
        if empty:
            sys.exit(f"[split] REFUSING {prop}: {', '.join(empty)} came out empty")

        for part in PARTS:
            name = f"{prop}_{part}"
            if a.dry_run:
                print(f"[split]   would write {name}.obj ({len(groups[part])} tris)")
                continue
            nv, nf = write_obj(os.path.join(a.objects_dir, name + '.obj'), v, vt, vn, groups[part], prop, part)
            shutil.copyfile(tex, os.path.join(a.objects_dir, name + '_tex.png'))
            pts = [tuple(map(float, v[c[0] - 1])) for f in groups[part] for c in f]
            sp = [(min(c), max(c)) for c in zip(*pts)]
            print(f"[split]   {name:34s} {nv:4d}v {nf:4d}t  "
                  f"X {sp[0][0]:+7.2f}..{sp[0][1]:+7.2f}  Y {sp[1][0]:+7.2f}..{sp[1][1]:+7.2f}  "
                  f"Z {sp[2][0]:+7.2f}..{sp[2][1]:+7.2f}")
            made.append(name)

    # ⚠ guid_mesh.txt is the editor's PLACEABLE CATALOG (EditorObjects.LoadCatalog) and nothing regenerates
    # it, so appending is safe and permanent. Opt-in: master asked to see the pieces before anything is wired.
    if a.register and made and not a.dry_run:
        gm = os.path.join(a.objects_dir, 'guid_mesh.txt')
        have = {l.split()[1] for l in open(gm) if len(l.split()) >= 2}
        with open(gm, 'a') as fh:
            for name in made:
                if name in have:
                    print(f"[split]   {name} already in guid_mesh.txt")
                    continue
                fh.write(f"{uuid.uuid4().hex} {name}\n")
                print(f"[split]   registered {name} in guid_mesh.txt")


if __name__ == '__main__':
    main()
