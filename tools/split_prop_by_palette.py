#!/usr/bin/env python3
"""Split a prop OBJ into one prop per PALETTE COLOUR it uses.

The port's prop textures are palette strips, not UV art -- Fence_Road_0's is 2x2 and holds exactly two
colours, 584433 brown and A4A4A4 silver. So "which material is this triangle" is already written down in
the mesh: it is whichever texel the triangle's UVs land on. Nothing has to be guessed, drawn or re-authored.

⭐ WHICH MAKES THE SPLIT A MEASUREMENT RATHER THAN AN EDIT. Master 2026-10-09: "split the prop of fence road
and fence road broken into the posts (brown wood) and the guardrail (metal, silver)". A split done by
eyeballing the geometry -- "the rail is the bit above z=0.5" -- is a rule somebody invented, and it is wrong
the first time a prop has a brown part up high. This reads the authored assignment back out.

⚠ IT REFUSES ON A MIXED TRIANGLE, and that check is the whole safety of the method. A triangle whose three
UVs straddle two texels belongs to neither part, and silently putting it in one of them is how a split
leaves a hole in the mesh. Both fences came back with ZERO mixed triangles out of 114 and 116, so the
authored assignment really is per-triangle here; a prop that does not pass that test needs a different
approach, not a tie-break.

Each part keeps the ORIGINAL texture, unchanged. Its UVs still point at its own colour, so the part draws
correctly with no new art -- and the two parts can then be given different material treatment (a tighter
specular on the metal, say) because they are now separate props with separate names.

Usage:
  split_prop_by_palette.py Fence_Road_0 --part 584433=Posts --part A4A4A4=Rail [--register] [--dry-run]
"""
import argparse, os, shutil, subprocess, sys, uuid

OBJ_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'game', 'content', 'objects')


def read_palette(png):
    """{(x, y): 'rrggbb'} for a small palette texture, via ImageMagick's txt: dump."""
    out = subprocess.run(['convert', png, '-depth', '8', 'txt:-'], capture_output=True, text=True).stdout
    w = h = 0
    px = {}
    for line in out.splitlines()[1:]:
        if '#' not in line:
            continue
        pos, rest = line.split(':', 1)
        x, y = (int(v) for v in pos.split(','))
        px[(x, y)] = rest.split('#')[1].split()[0][:6].lower()
        w, h = max(w, x + 1), max(h, y + 1)
    return px, w, h


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
            # keep the raw v/vt/vn triples; a prop OBJ here is already triangulated
            faces.append([tuple(int(x) if x else 0 for x in c.split('/')) for c in t[1:]])
    return v, vt, vn, faces


def write_obj(path, v, vt, vn, faces, src_name, colour):
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
        fh.write(f"# split from {src_name} by palette colour #{colour} (tools/split_prop_by_palette.py)\n")
        for x in ov:
            fh.write('v ' + ' '.join(x) + '\n')
        for x in ovt:
            fh.write('vt ' + ' '.join(x) + '\n')
        for x in ovn:
            fh.write('vn ' + ' '.join(x) + '\n')
        for row in of:
            fh.write('f ' + ' '.join(f"{a}/{b or ''}/{c or ''}" for a, b, c in row) + '\n')
    return len(ov), len(of)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('prop')
    ap.add_argument('--part', action='append', required=True, metavar='RRGGBB=Suffix',
                    help='a palette colour and the name suffix its triangles become')
    ap.add_argument('--objects-dir', default=OBJ_DIR)
    ap.add_argument('--register', action='store_true',
                    help='append the new names to guid_mesh.txt so the editor palette lists them')
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()

    want = {}
    for p in a.part:
        col, _, suffix = p.partition('=')
        want[col.lower().lstrip('#')] = suffix

    src = os.path.join(a.objects_dir, a.prop + '.obj')
    tex = os.path.join(a.objects_dir, a.prop + '_tex.png')
    for f in (src, tex):
        if not os.path.exists(f):
            sys.exit(f"missing {f}")

    px, w, h = read_palette(tex)
    v, vt, vn, faces = read_obj(src)

    def texel(uv_i):
        u, uu = float(vt[uv_i - 1][0]), None
        vv = float(vt[uv_i - 1][1])
        x = min(w - 1, max(0, int(u * w)))
        y = min(h - 1, max(0, int((1.0 - vv) * h)))
        return px[(x, y)]

    groups, mixed = {}, []
    for f in faces:
        cols = {texel(b) for (_, b, _) in f if b}
        if len(cols) != 1:
            mixed.append(f)
            continue
        groups.setdefault(cols.pop(), []).append(f)

    print(f"[split] {a.prop}: {len(faces)} tris, palette {w}x{h} = {sorted(set(px.values()))}")
    for col, fs in sorted(groups.items()):
        print(f"[split]   #{col}: {len(fs)} tris -> {want.get(col, '(not requested)')}")
    if mixed:
        # ⚠ see the module docstring: a mixed triangle has no correct home, so this is fatal rather than a
        # warning. Shipping the split anyway puts a hole in one part and a stray face in the other.
        sys.exit(f"[split] REFUSING: {len(mixed)} triangle(s) span more than one palette colour")

    missing = [c for c in want if c not in groups]
    if missing:
        sys.exit(f"[split] REFUSING: asked for colour(s) {missing} that no triangle uses")

    made = []
    for col, suffix in want.items():
        name = f"{a.prop}_{suffix}"
        out = os.path.join(a.objects_dir, name + '.obj')
        if a.dry_run:
            print(f"[split]   would write {name}.obj ({len(groups[col])} tris)")
            continue
        nv, nf = write_obj(out, v, vt, vn, groups[col], a.prop, col)
        shutil.copyfile(tex, os.path.join(a.objects_dir, name + '_tex.png'))
        vs = [tuple(map(float, v[x[0] - 1])) for f in groups[col] for x in f]
        sp = [(min(c), max(c)) for c in zip(*vs)]
        print(f"[split]   wrote {name}.obj  {nv}v {nf}t  "
              f"bbox X {sp[0][0]:+.3f}..{sp[0][1]:+.3f} Y {sp[1][0]:+.3f}..{sp[1][1]:+.3f} Z {sp[2][0]:+.3f}..{sp[2][1]:+.3f}")
        made.append(name)

    # ⚠ guid_mesh.txt is the editor's PLACEABLE CATALOG (EditorObjects.LoadCatalog), and nothing regenerates
    # it -- gen_placements.py only reads it -- so appending is safe and permanent. A fresh uuid4 cannot
    # collide with a retail guid in any way worth worrying about.
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
