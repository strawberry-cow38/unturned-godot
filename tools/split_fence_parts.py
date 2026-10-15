#!/usr/bin/env python3
"""Split a fence prop into its POST and one BAY of infill, so a fence can be solved along a spline in engine.

Master 2026-10-14: "work on getting fences as splines, might wanna split the models into the individual pieces
first and then solving them in engine?"

A fence is two different kinds of thing, and only one of them can stretch. A POST stands plumb at a point and
never changes shape; the INFILL between two posts -- rails, boards, pickets, a chain-link panel, a stone wall --
is a pattern that can be fitted to whatever distance the posts end up apart, and racked to follow the ground.
Shipped as one mesh they cannot be told apart: tile the whole prop along a curve and every joint either doubles a
post or leaves a hole, which is the guardrail lesson (EditorFenceRoad) all over again.

⭐ THE CUT IS READ OFF THE MESH, NOT CHOSEN. Every fence here is a handful of disconnected solids -- each post,
board, rail and panel is its own closed piece -- so the parts are the CONNECTED COMPONENTS, welded by position.
⭐ AND THE POSTS ARE THE ENDS. Every fence prop is built post-to-post, so the left-most and right-most components
are posts by construction; any other component the same size as an end post (to a centimetre) is a post too --
the middle post of Fence_Wood_0 and _1, which carry two bays each. The pitch is then the distance between
neighbouring post CENTRES, which is also exactly the joint retail butts its own segments on.

The bay is everything between two neighbouring post centres, recentred on x = 0: the +X bay where a prop has more
than one. ⚠ RAILS RUN THROUGH THE MIDDLE POST, so they are CLIPPED at the two post centres (Sutherland-Hodgman per
triangle, positions, UVs and normals interpolated). The cut faces sit inside the posts that will stand there, so
the open ends never show -- no cap is needed. Nothing else is edited: every surviving triangle keeps its authored
UVs, so each piece wears the source prop's palette texture unchanged.

⚠ THESE PROPS ARE Z-UP WITH THEIR LENGTH ON LOCAL +X (Fence_Road_0, the guardrail, is the odd one: its length is on
+Y, and it was split by colour, see split_prop_by_palette.py). So a post is narrow in X and Y and tall in Z.

⭐ A WRECK IS SPLIT THE SAME WAY, BAY ONLY (--bay-only). Fence_Wood_Broken_0, _Broken_3, Fence_Metal_Broken_0 and _1 span
post-to-post exactly like their intact props (4 / 6 / 4 / 6 m), so their infill drops into a run between two of ITS posts
and lines up with the bays either side (strawberry 2026-10-14: "add support for the fence broken props to be inserted along
the spline (lining up with the fences ofc)"). Their own end posts are thrown away -- the run stands its own there, so the
joint is the same post as everywhere else. Each wears the same palette PNG as its intact prop (byte-identical).

⭐ AND IN HALVES (--halves), strawberry 2026-10-15: "could you also split the broken fence prop in half down the middle,
so that we could have a bigger broken gap?". A wreck's LEFT half hangs off its left post and its RIGHT half off its right
one, so with whole bays left out between them the gap opens as wide as wanted. The halves are cut BY COMPONENT -- each
broken board, rail stub and torn panel goes whole to the side its middle is on -- not by a plane through x = 0, which
would leave every piece it crossed open-ended. Neither is recentred: laid together they ARE the wreck.

Usage:
  split_fence_parts.py Fence_Wood_0 [Fence_Metal_0 ...] [--register] [--dry-run] [--bay-only] [--halves]
"""
import argparse, os, shutil, sys, uuid

OBJ_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'game', 'content', 'objects')
GUID_FILE = os.path.join(OBJ_DIR, 'guid_mesh.txt')


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
            corners = []
            for c in t[1:]:
                p = c.split('/')
                corners.append(tuple(int(p[i]) - 1 if len(p) > i and p[i] else -1 for i in range(3)))
            faces.append(corners)
    return v, vt, vn, faces


def components(v, faces):
    """Faces grouped into connected solids, welding vertices that share a position (to 0.1 mm): a ripped mesh splits
    every hard edge into separate vertices, so index sharing alone would cut each box into its six faces."""
    parent = list(range(len(v)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    first = {}
    for i, p in enumerate(v):
        k = (round(p[0], 4), round(p[1], 4), round(p[2], 4))
        if k in first:
            parent[find(i)] = find(first[k])
        else:
            first[k] = i
    for f in faces:
        for c in f[1:]:
            parent[find(c[0])] = find(f[0][0])
    groups = {}
    for fi, f in enumerate(faces):
        groups.setdefault(find(f[0][0]), []).append(fi)
    return list(groups.values())


def bbox(v, faces, fis):
    pts = [v[c[0]] for fi in fis for c in faces[fi]]
    return tuple(min(p[a] for p in pts) for a in range(3)), tuple(max(p[a] for p in pts) for a in range(3))


def clip(poly, axis, value, keep_below):
    """One Sutherland-Hodgman pass: keep the part of a polygon on one side of the plane `axis` = value. A vertex is
    (pos, uv, nrm); the new ones are interpolated along the edge they cut."""
    def inside(p):
        return p[0][axis] <= value + 1e-9 if keep_below else p[0][axis] >= value - 1e-9

    def cut(a, b):
        da, db = a[0][axis] - value, b[0][axis] - value
        t = da / (da - db)
        lerp = lambda x, y: tuple(x[i] + (y[i] - x[i]) * t for i in range(len(x)))
        pos = list(lerp(a[0], b[0])); pos[axis] = value
        uv = lerp(a[1], b[1]) if a[1] is not None and b[1] is not None else None
        n = lerp(a[2], b[2]) if a[2] is not None and b[2] is not None else None
        if n is not None:
            ln = sum(c * c for c in n) ** 0.5 or 1.0
            n = tuple(c / ln for c in n)
        return (tuple(pos), uv, n)
    out = []
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        if inside(a):
            out.append(a)
            if not inside(b):
                out.append(cut(a, b))
        elif inside(b):
            out.append(cut(a, b))
    return out


def write_obj(path, tris, src, part, note):
    """Triangles of (pos, uv, nrm) corners, written one vertex per corner (the shipped files' own layout)."""
    lines = [f'# {part} of {src}, cut by tools/split_fence_parts.py -- {note}']
    k = 0
    vs, vts, vns, fs = [], [], [], []
    for tri in tris:
        idx = []
        for pos, uv, n in tri:
            k += 1
            vs.append('v %.6f %.6f %.6f' % pos)
            vts.append('vt %.6f %.6f' % (uv if uv is not None else (0.0, 0.0)))
            vns.append('vn %.6f %.6f %.6f' % (n if n is not None else (0.0, 0.0, 1.0)))
            idx.append(k)
        fs.append('f ' + ' '.join(f'{i}/{i}/{i}' for i in idx))
    with open(path, 'w', newline='\n') as fh:
        fh.write('\n'.join(lines + vs + vts + vns + fs) + '\n')


def corner(v, vt, vn, c):
    return (v[c[0]], vt[c[1]] if c[1] >= 0 else None, vn[c[2]] if c[2] >= 0 else None)


def split(name, dry, register, bay_only=False, halves=False):
    src = os.path.join(OBJ_DIR, name + '.obj')
    v, vt, vn, faces = read_obj(src)
    comps = components(v, faces)
    boxes = [bbox(v, faces, c) for c in comps]
    lo_x = min(b[0][0] for b in boxes)
    hi_x = max(b[1][0] for b in boxes)
    ends = [i for i, b in enumerate(boxes) if abs(b[0][0] - lo_x) < 1e-4 or abs(b[1][0] - hi_x) < 1e-4]
    if len(ends) != 2:
        sys.exit(f'{name}: expected exactly two end components (the end posts), found {len(ends)} -- refusing')
    size = lambda b: tuple(b[1][a] - b[0][a] for a in range(3))
    ref = size(boxes[ends[0]])
    posts = sorted((i for i, b in enumerate(boxes)
                    if i in ends or all(abs(size(b)[a] - ref[a]) < 0.01 for a in range(3))),
                   key=lambda i: boxes[i][0][0] + boxes[i][1][0])
    centres = [(boxes[i][0][0] + boxes[i][1][0]) * 0.5 for i in posts]
    pitches = [centres[k + 1] - centres[k] for k in range(len(centres) - 1)]
    if max(pitches) - min(pitches) > 0.01:
        sys.exit(f'{name}: posts are not evenly spaced ({pitches}) -- refusing')
    pitch = sum(pitches) / len(pitches)
    # the bay: the +X one where there are several (between the last two posts' centres), recentred on x = 0
    c0, c1 = centres[-2], centres[-1]
    mid = (c0 + c1) * 0.5
    post_set = set(posts)
    bay_tris, clipped = [], 0
    for ci, comp in enumerate(comps):
        if ci in post_set:
            continue
        for fi in comp:
            poly = [corner(v, vt, vn, c) for c in faces[fi]]
            xs = [p[0][0] for p in poly]
            if max(xs) <= c0 + 1e-6 or min(xs) >= c1 - 1e-6:
                continue                      # wholly in another bay
            if min(xs) < c0 - 1e-6 or max(xs) > c1 + 1e-6:
                clipped += 1
                poly = clip(clip(poly, 0, c0, False), 0, c1, True)
            if len(poly) < 3:
                continue
            for k in range(1, len(poly) - 1):  # fan back into triangles, winding as authored
                bay_tris.append([poly[0], poly[k], poly[k + 1]])
    bay_tris = [[((p[0] - mid, p[1], p[2]), uv, n) for p, uv, n in t] for t in bay_tris]
    # the halves: whole components, each to the side of the bay's middle its own middle is on (a component in two bays
    # was clipped above, so this pass works off the components again)
    half_tris = {'BayL': [], 'BayR': []}
    for ci, comp in enumerate(comps):
        if ci in post_set:
            continue
        cx = (boxes[ci][0][0] + boxes[ci][1][0]) * 0.5
        if cx <= c0 or cx >= c1:
            continue
        side = 'BayL' if cx < mid else 'BayR'
        for fi in comp:
            half_tris[side].append([((p[0] - mid, p[1], p[2]), uv, n) for p, uv, n in (corner(v, vt, vn, c) for c in faces[fi])])
    # the post: the middle one where there is one (both of its faces are in-bay faces), else the right-hand end
    pi = posts[len(posts) // 2] if len(posts) > 2 else posts[-1]
    pc = centres[posts.index(pi)]
    post_tris = [[((p[0] - pc, p[1], p[2]), uv, n) for p, uv, n in (corner(v, vt, vn, c) for c in faces[fi])]
                 for fi in comps[pi]]
    pb = boxes[pi]
    bay_pts = [p for t in bay_tris for p, _, _ in t]
    bay_lo = tuple(min(p[a] for p in bay_pts) for a in range(3)) if bay_pts else (0, 0, 0)
    bay_hi = tuple(max(p[a] for p in bay_pts) for a in range(3)) if bay_pts else (0, 0, 0)
    print(f'{name}: {len(comps)} parts, {len(posts)} posts at x = {", ".join("%.3f" % c for c in centres)} '
          f'-> pitch {pitch:.4f} m')
    print(f'   post   {pb[1][0]-pb[0][0]:.3f} x {pb[1][1]-pb[0][1]:.3f} m, z {pb[0][2]:.3f}..{pb[1][2]:.3f}, '
          f'y {pb[0][1]:.3f}..{pb[1][1]:.3f}  ({len(post_tris)} tris)')
    print(f'   bay    x {bay_lo[0]:.3f}..{bay_hi[0]:.3f}, y {bay_lo[1]:.3f}..{bay_hi[1]:.3f}, '
          f'z {bay_lo[2]:.3f}..{bay_hi[2]:.3f}  ({len(bay_tris)} tris, {clipped} clipped at a post centre)')
    if dry:
        return []
    tex = os.path.join(OBJ_DIR, name + '_tex.png')
    out = []
    parts = [('Post', post_tris, f'one post, recentred on x = 0 (pitch {pitch:.4f} m)'),
             ('Bay', bay_tris, f'the infill between two post centres {pitch:.4f} m apart, x = 0 midway')]
    if bay_only:
        parts = parts[1:]
    if halves:
        print(f'   halves: left {len(half_tris["BayL"])} tris, right {len(half_tris["BayR"])} tris')
        parts += [(h, half_tris[h], f'the {"left" if h == "BayL" else "right"} half of the bay, by component, in the bay\'s own frame')
                  for h in ('BayL', 'BayR')]
    for part, tris, note in parts:
        pn = f'{name}_{part}'
        write_obj(os.path.join(OBJ_DIR, pn + '.obj'), tris, name, part, note)
        if os.path.exists(tex):
            shutil.copyfile(tex, os.path.join(OBJ_DIR, pn + '_tex.png'))
        out.append(pn)
    if register:
        have = open(GUID_FILE).read().split('\n')
        names = {l.split()[1] for l in have if len(l.split()) >= 2}
        add = [f'{uuid.uuid4().hex} {pn}' for pn in out if pn not in names]
        if add:
            body = '\n'.join(l for l in have if l.strip())
            with open(GUID_FILE, 'w', newline='\n') as fh:
                fh.write(body + '\n' + '\n'.join(add) + '\n')
            print(f'   registered {len(add)} in guid_mesh.txt')
    return out


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('names', nargs='+')
    ap.add_argument('--register', action='store_true')
    ap.add_argument('--dry-run', action='store_true')
    ap.add_argument('--bay-only', action='store_true', help='a wreck: write its bay, not its (leaning) post')
    ap.add_argument('--halves', action='store_true', help='also write the bay\'s left and right halves (a wreck that can open wider)')
    a = ap.parse_args()
    for n in a.names:
        split(n, a.dry_run, a.register, a.bay_only, a.halves)
