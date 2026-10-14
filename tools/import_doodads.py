#!/usr/bin/env python3
"""Import strawberry's approved doodad kit (round 6i, 48/48 locked) into the game, byte for byte.

    import_doodads.py [--kit DIR] [--extra DIR ...] [--glass]

The kit (/home/ec2-user/doodads/review) ships each doodad as a native OBJ (Z-up, metres, front +Y -- the props'
own convention, FurnitureCatalog) with a 4x4 palette PNG, a composed GLB, and a placement row in
doodad_placement.tsv. The game reads OBJ + `<Name>_tex.png` out of game/content/objects (EditorObjects.MeshFor/MatFor),
so the GLBs are not what is imported: the OBJ and the PNG are, copied unchanged. The GLB is the kit's PREVIEW of the
same art composed with its stock children, which the kit says to use "once, or authored art plus stock_children; do
not add children to a composed export".

So the stock children (a ceramic bowl under the fruit, the knives in the block, the cups of the mug pair) are composed
at load from the game's OWN item rips -- game/content/items/<id>.txt and .png, byte-identical to the kit's
stock_items (checked here) -- by the transform each row gives: item rips first map (x, y, z) to (x, -z, y), then a
uniform scale, an XYZ Euler rotation (Rz Ry Rx) and a translation in the doodad's native frame
(stock_children.schema.json). Three doodads are nothing BUT children (Mug_Pair_0, Plate_Stack_0,
Leaning_Garden_Tools_0): they have no OBJ of their own.

Writes:
  game/content/objects/<Name>.obj, <Name>_tex.png    copied from the kit's models/ (the MTL is not: nothing reads it)
  game/content/objects/guid_mesh.txt                 a "<guid> <Name>" row per doodad, the TSV's GUID (appended once)
  game/content/objects/doodad_kit_children.json      the stock children by doodad (game/DoodadKitView composes them)
  game/content/objects/doodad_kit_materials.json     the translucent ones (glass: EditorObjects.MatFor honours it)
  core/UnturnedSim/DoodadKit.cs                      the placement rows, the TSV's columns as data
  core/UnturnedSim/Furnish.cs                        the composed bounds in FurnitureCatalog.Sizes, between markers
With --glass, the Drinking Glass item the kit also delivers (ITEM_GLASS.md): item_glass/9301.txt and .png into
game/content/items, and its manifest entry merged into items_manifest.json (sorted, indent 0, as that file is kept).

Re-run after a kit change; it rewrites what it owns and appends nothing twice.
"""
import argparse, csv, hashlib, json, math, os, shutil, subprocess, sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OBJ_DIR = os.path.join(REPO, 'game', 'content', 'objects')
ITEM_DIR = os.path.join(REPO, 'game', 'content', 'items')
SIZES_BEGIN = '            // BEGIN doodad kit sizes (tools/import_doodads.py)'
SIZES_END = '            // END doodad kit sizes'


def rows_of(kit):
    with open(os.path.join(kit, 'doodad_placement.tsv'), newline='') as f:
        return list(csv.DictReader(f, delimiter='\t'))


def head(kit):
    try:
        return subprocess.check_output(['git', '-C', kit, 'rev-parse', '--short', 'HEAD'], text=True).strip()
    except Exception:
        return 'unknown'


def verts(path):
    return [tuple(map(float, l.split()[1:4])) for l in open(path) if l.startswith('v ')]


def child_matrix(rot, scale):
    """The kit's own composition (scripts/check_round5b.py compose_vertices): Rz Ry Rx times the item-rip turn."""
    x, y, z = (math.radians(a) for a in rot)
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]
    ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]
    rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]
    native = [[1, 0, 0], [0, 0, -1], [0, 1, 0]]

    def mm(a, b):
        return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

    m = mm(mm(mm(rz, ry), rx), native)
    return [[m[i][j] * scale for j in range(3)] for i in range(3)]


def composed_bounds(kit, row):
    pts = []
    if row.get('mesh'):
        pts += verts(os.path.join(kit, 'models', row['mesh']))
    for c in json.loads(row.get('stock_children') or '[]'):
        m = child_matrix(c['rotation_deg'], c['scale'])
        t = c['position']
        for v in verts(os.path.join(ITEM_DIR, c['obj'])):
            pts.append(tuple(sum(m[i][j] * v[j] for j in range(3)) + t[i] for i in range(3)))
    return [min(p[i] for p in pts) for i in range(3)], [max(p[i] for p in pts) for i in range(3)]


def lst(s):
    """A TSV list column: JSON for the review kit, the same for lane-trash."""
    if not s:
        return []
    v = json.loads(s)
    return v if isinstance(v, list) else [v]


def num(s, default='float.NaN'):
    if s in (None, '', 'null', 'None'):
        return default
    return f'{float(s):g}f'


def cs_str_array(xs):
    return 'new[] { ' + ', '.join(f'"{x}"' for x in xs) + ' }' if xs else 'System.Array.Empty<string>()'


MOUNTS = {'surface': 'Surface', 'floor': 'Floor', 'wall': 'Wall', 'ceiling': 'Ceiling', 'ground': 'Ground', 'lean': 'Lean'}
YAWS = {'any': 'Any', 'face_room': 'FaceRoom', 'align_to_host_edge': 'AlignToHostEdge', 'back_to_wall': 'BackToWall'}
RARITY = {'common': 'Common', 'uncommon': 'Uncommon', 'rare': 'Rare'}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--kit', default='/home/ec2-user/doodads/review')
    ap.add_argument('--extra', action='append', default=None, help='another approved lane (its TSV and models/), e.g. lane-trash')
    ap.add_argument('--glass', action='store_true', help='also install the Drinking Glass item 9301 (ITEM_GLASS.md)')
    a = ap.parse_args()
    extras = a.extra if a.extra is not None else ['/home/ec2-user/doodads/lane-trash']

    sources = [(a.kit, r) for r in rows_of(a.kit)]
    for e in extras:
        sources += [(e, r) for r in rows_of(e)]
    names = [r['name'] for _, r in sources]
    assert len(names) == len(set(names)), 'a doodad named twice across the lanes'

    # the stock children's rips must be the game's own, byte for byte: the kit composed against its copies
    for kit, r in sources:
        for c in json.loads(r.get('stock_children') or '[]'):
            mine, theirs = os.path.join(ITEM_DIR, c['obj']), os.path.join(kit, 'stock_items', c['obj'])
            for ext in ('', '.png'):
                p, q = (mine, theirs) if not ext else (mine[:-4] + '.png', theirs[:-4] + '.png')
                if open(p, 'rb').read() != open(q, 'rb').read():
                    sys.exit(f'{r["name"]}: the game\'s {os.path.basename(p)} is not the kit\'s -- the composition would differ')

    # 1. the art, unchanged
    copied = 0
    for kit, r in sources:
        if not r.get('mesh'):
            continue
        for src, dst in ((r['mesh'], r['name'] + '.obj'), (r['texture'], r['name'] + '_tex.png')):
            shutil.copyfile(os.path.join(kit, 'models', src), os.path.join(OBJ_DIR, dst))
            copied += 1

    # 2. the GUIDs, the TSV's (a saved placement is a GUID: a fresh one would load as nothing)
    gm = os.path.join(OBJ_DIR, 'guid_mesh.txt')
    have = {l.split()[1] for l in open(gm) if len(l.split()) >= 2}
    with open(gm, 'a') as f:
        for _, r in sources:
            if r['name'] not in have:
                f.write(f"{r['guid']} {r['name']}\n")

    # 3. the stock children and the glass, as sidecars
    source = {os.path.basename(k.rstrip('/')): head(k) for k in [a.kit] + extras}
    children = {'_source': source, '_schema': 'item rip (x,y,z) -> (x,-z,y); then scale, Rz Ry Rx (rotation_deg, XYZ), then position; '
                                                'all in the doodad\'s native frame (Z-up, front +Y). game/DoodadKitView.cs composes them.'}
    for _, r in sources:
        ch = json.loads(r.get('stock_children') or '[]')
        if ch:
            children[r['name']] = [{'item': int(c['obj'].split('.')[0]), 'name': c.get('item', ''), 'position': c['position'],
                                    'rotation_deg': c['rotation_deg'], 'scale': c['scale']} for c in ch]
    with open(os.path.join(OBJ_DIR, 'doodad_kit_children.json'), 'w') as f:
        json.dump(children, f, indent=2)
        f.write('\n')
    mats = {}
    for _, r in sources:
        if (r.get('translucent') or '').lower() == 'true':
            mi = json.loads(r.get('material_integration') or '{}')
            mats[r['name']] = {'translucent': True, 'transparency': mi.get('Transparency', 'AlphaDepthPrePass'),
                               'note': 'the kit\'s glass swatch is RGBA alpha 112: an alpha-SCISSOR material (the RGBA default) would cut it away'}
    with open(os.path.join(OBJ_DIR, 'doodad_kit_materials.json'), 'w') as f:
        json.dump(mats, f, indent=2)
        f.write('\n')

    # 4. the rows, as data
    out = ['// GENERATED by tools/import_doodads.py from the doodad kit\'s doodad_placement.tsv -- '
           + ', '.join(f'{k} @ {v}' for k, v in source.items()) + '.',
           '// Do not edit by hand: change the kit and re-run the script. FurnishDoodads.cs reads these rows; the mapping of',
           '// the kit\'s hosts and room kinds onto the generator\'s is there, not here.',
           'using System.Collections.Generic;', '', 'namespace UnturnedSim', '{', '    public static partial class BuildingGen', '    {',
           '        /// <summary>strawberry\'s approved doodad kit, one row per doodad, in the TSV\'s order and words.</summary>',
           '        public static readonly IReadOnlyList<DoodadRow> DoodadKit = new DoodadRow[]', '        {']
    for _, r in sources:
        cl = json.loads(r['cluster']) if r.get('cluster') else None
        cmin = int(r.get('cluster_min') or (cl['size'][0] if cl else 1))
        cmax = int(r.get('cluster_max') or (cl['size'][1] if cl else 1))
        csp = float(r.get('cluster_spacing_m') or (cl['spacing_m'] if cl else 0))
        out.append('            new("{}", DoodadMount.{}, {}, {}, {}, {}, {}, DoodadYaw.{}, {}, {}, {}, {}, {}, {}, DoodadRarity.{}, {}, {}, {}, {}, {}, {}),'.format(
            r['name'], MOUNTS[r['mount']], 'true' if r['exposure'] == 'outdoor' else 'false',
            cs_str_array(lst(r['room_kinds'])), cs_str_array(lst(r['hosts'])),
            num(r.get('suggested_origin_height')), num(r.get('hang_drop_m')), YAWS[r['yaw']],
            'true' if r['against_wall'].lower() == 'true' else 'false',
            num(r['clearance_front_m'], '0f'), num(r['min_spacing_m'], '0f'),
            cs_str_array(lst(r['near'])), cs_str_array(lst(r['avoid'])), int(r['max_per_room']), RARITY[r['rarity']],
            'true' if r['collision'] == 'solid' else 'false', num(r.get('host_edge_margin_m'), '0.02f'), num(r.get('host_min_long_top_m'), '0f'),
            cmin, cmax, num(str(csp), '0f')))
    out += ['        };', '    }', '}', '']
    with open(os.path.join(REPO, 'core', 'UnturnedSim', 'DoodadKit.cs'), 'w') as f:
        f.write('\n'.join(out))

    # 5. the sizes: the COMPOSED bounds (the art and its children), in the catalog's conventions -- floor, top and
    # ground pieces stand on their base (H their height), wall pieces have their back at Y 0 and are centred up and down
    # (H the half height, the Clock_0 convention), ceiling pieces hang from their origin (H 0, the ceiling lamps')
    lines = [SIZES_BEGIN,
             '            // strawberry\'s doodad kit (' + ', '.join(f'{k} @ {v}' for k, v in source.items())
             + '): its composed bounds, the art and its stock children']
    for kit, r in sources:
        lo, hi = composed_bounds(kit, r)
        if r.get('bounds_min_x'):
            want = [float(r[k]) for k in ('bounds_min_x', 'bounds_min_y', 'bounds_min_z', 'bounds_max_x', 'bounds_max_y', 'bounds_max_z')]
            got = lo + hi
            if max(abs(p - q) for p, q in zip(got, want)) > 1e-3:
                sys.exit(f'{r["name"]}: composed {got} but the kit measured {want}')
        lines.append(f'            ["{r["name"]}"] = new({lo[0]:.3f}f, {hi[0]:.3f}f, {lo[1]:.3f}f, {hi[1]:.3f}f, {hi[2]:.3f}f),'.replace('-0.000f', '0.000f'))
    lines.append(SIZES_END)
    fp = os.path.join(REPO, 'core', 'UnturnedSim', 'Furnish.cs')
    src = open(fp).read().split('\n')
    if SIZES_BEGIN in src:
        i, j = src.index(SIZES_BEGIN), src.index(SIZES_END)
        src[i:j + 1] = lines
    else:
        k = next(n for n, l in enumerate(src) if l.strip().startswith('["Craft_01"]'))
        src[k + 1:k + 1] = lines
    with open(fp, 'w') as f:
        f.write('\n'.join(src))

    # 6. the drinking glass (ITEM_GLASS.md: copy the rip and its texture, merge the one manifest entry)
    if a.glass:
        g = os.path.join(a.kit, 'item_glass')
        for n in ('9301.txt', '9301.png'):
            shutil.copyfile(os.path.join(g, n), os.path.join(ITEM_DIR, n))
        mp = os.path.join(ITEM_DIR, 'items_manifest.json')
        text = open(mp).read()
        man = json.loads(text)
        assert json.dumps(man, indent=0, sort_keys=True) == text, 'items_manifest.json is not in its usual form: not rewriting it'
        man.update(json.load(open(os.path.join(g, 'manifest_entry.json'))))
        with open(mp, 'w') as f:
            f.write(json.dumps(man, indent=0, sort_keys=True))
    print(f'{len(sources)} doodads ({copied} files copied), {len(children) - 2} with stock children, {len(mats)} translucent; '
          f'sources {source}{"; glass 9301 installed" if a.glass else ""}')


if __name__ == '__main__':
    main()
