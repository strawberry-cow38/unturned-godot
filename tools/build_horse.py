#!/usr/bin/env python3
"""Build a four-colour bay horse in the existing animal rig format.

All metre dimensions come from measure_animals.render_calipers; dimensionless profile fractions
are explicit modelling choices, not claimed measurements of an existing horse.
No extra material, bones, loader or animation format is needed.
"""
import copy
import json

import numpy as np
from PIL import Image
from scipy.spatial.transform import Rotation
from scipy.spatial import Delaunay

from measure_animals import ROOT, globals_for, read, render_calipers


def dimensions():
    calipers = render_calipers()
    deer, pig, cow = (calipers[n] for n in ('deer', 'pig', 'cow'))
    return dict(withers=round(cow['withers'] * 1.15, 2),
                leg=round(deer['leg'] * 1.50, 2),
                height=round(deer['height'] * 1.08, 2),
                body=round(cow['body'] * 1.25, 2),
                width=round(cow['width'] * .80, 2),
                head_length=round(cow['head_length'] * 1.20, 2),
                head_height=round(deer['head_height'] * .70, 2),
                head_width=round(pig['head_width'] * .90, 2),
                hoof_length=round(deer['body'] * .125, 2),
                hoof_width=round(deer['width'] * .25, 2),
                ear=round(pig['head_height'] * .50, 2),
                mane_width=round(pig['width'] * .20, 2),
                tail_length=round(deer['leg'] * .90, 2),
                eye=round(pig['width'] * .12, 2))


def build():
    d = dimensions()
    rig = copy.deepcopy(read('deer'))
    # Keep all seven deer clips and all seven bone local transforms verbatim.
    # New positions are already Y-up; inverse GLOBAL rests bind them to the same
    # six skin slots. Do not mistake a slot for a bone index.
    g = globals_for(rig)
    for s in rig['skin']:
        inv = np.linalg.inv(g[s['bone']])
        # Source scale noise is <1e-6. Preserve the inverse exactly to that tolerance.
        s['pos'] = inv[:3, 3].tolist()
        s['rot'] = Rotation.from_matrix(inv[:3, :3]).as_quat().tolist()
        s['scale'] = np.linalg.norm(inv[:3, :3], axis=1).tolist()
    for k in ('positions', 'normals', 'uvs', 'skin_index', 'skin_weight', 'faces'):
        rig[k] = []
    slots = {rig['bones'][s['bone']]['name']: i for i, s in enumerate(rig['skin'])}
    parts = {}
    # Exactly four texels, same resolution/palette cardinality as deer and pig.
    # Bay = brown coat, warmer muzzle/inner-ear, black points, ivory blaze.
    palette = [(119, 65, 35, 255), (158, 100, 57, 255), (35, 28, 25, 255), (212, 212, 203, 255)]
    uv = [(0.25, .25), (.75, .25), (.25, .75), (.75, .75)]

    def face(points, bone, color=0, inset=None, inset_color=2):
        p = np.array(points, dtype=float)
        # Newell normal also works when the first corner of a concave face
        # turns inward; the old first-three-corners normal could point backward.
        normal = np.cross(p, np.roll(p, -1, axis=0)).sum(axis=0)
        normal /= np.linalg.norm(normal)
        corner_normals = [normal]*len(p)
        if len(p) == 4 and np.max(np.abs((p-p[0]) @ normal)) > 1e-7:
            # A tapered shoulder is a bilinear patch. Continuous corner normals
            # avoid an artificial diagonal crease across its two triangles.
            corner_normals = [np.cross(p[(i+1) % 4]-p[i], p[(i-1) % 4]-p[i]) for i in range(4)]
            corner_normals = [n/np.linalg.norm(n) for n in corner_normals]
        if inset is not None:
            # Inlaid colour is part of the closed surface, with shared POSITION
            # edges and separate UV corners. No floating eye/blaze billboards.
            all_p = np.concatenate([p, np.array(inset)])
            axes = [i for i in range(3) if i != np.argmax(np.abs(normal))]
            projected = all_p[:, axes]
            triangles = Delaunay(projected).simplices
            def within(q):
                poly = projected[:len(p)]
                crossings = 0
                for a, b in zip(poly, np.roll(poly, -1, axis=0)):
                    if (a[1] > q[1]) != (b[1] > q[1]):
                        crossings += q[0] < (b[0]-a[0])*(q[1]-a[1])/(b[1]-a[1])+a[0]
                return crossings % 2 == 1
            triangles = [t for t in triangles if within(projected[t].mean(0))]
            edges = {tuple(sorted((a, b))) for t in triangles for a, b in zip(t, np.roll(t, -1))}
            for ring in (list(range(len(p))), list(range(len(p), len(all_p)))):
                assert all(tuple(sorted((a, b))) in edges for a, b in zip(ring, ring[1:]+ring[:1]))
            cache = {}
            for t in triangles:
                c = inset_color if np.all(t >= len(p)) else color
                if np.dot(np.cross(all_p[t[1]]-all_p[t[0]], all_p[t[2]]-all_p[t[0]]), normal) < 0:
                    t = t[::-1]
                for i in t:
                    key = (int(i), c)
                    if key not in cache:
                        cache[key] = len(rig['positions'])
                        rig['positions'].append(np.round(all_p[i], 8).tolist())
                        rig['normals'].append(normal.tolist())
                        rig['uvs'].append(uv[c])
                        rig['skin_index'].append([slots[bone], 0])
                        rig['skin_weight'].append([1., 0.])
                    rig['faces'].append(cache[key])
            return
        start = len(rig['positions'])
        for v, n in zip(p, corner_normals):
            rig['positions'].append(np.round(v, 8).tolist())
            rig['normals'].append(n.tolist())
            rig['uvs'].append(uv[color])
            rig['skin_index'].append([slots[bone], 0])
            rig['skin_weight'].append([1., 0.])
        # Ear clipping preserves the concave hock outline. A fan from vertex 0
        # emitted reversed/zero-area triangles on the bent and straight legs.
        remaining = list(range(len(p)))
        def turn(a, b, c):
            return float(np.dot(np.cross(p[b]-p[a], p[c]-p[a]), normal))
        while len(remaining) > 3:
            for j, b in enumerate(remaining):
                a, c = remaining[j-1], remaining[(j+1) % len(remaining)]
                if turn(a, b, c) <= 1e-10:
                    continue
                if any(turn(a, b, k) >= -1e-10 and turn(b, c, k) >= -1e-10
                       and turn(c, a, k) >= -1e-10 for k in remaining if k not in (a, b, c)):
                    continue
                rig['faces'].extend([start+a, start+b, start+c])
                remaining.pop(j)
                break
            else:
                raise ValueError('Cannot triangulate polygon')
        rig['faces'].extend(start+i for i in remaining)

    def prism(name, profile, width, bone, color=0, z=0, edge_colors=None, inlays=None):
        start = len(rig['positions'])
        a = np.array(profile, float)
        area = sum(a[(i+1) % len(a), 0]*a[i, 1]-a[(i+1) % len(a), 1]*a[i, 0] for i in range(len(a)))
        if area > 0:
            a = a[::-1]
        half = lambda x, y: (width(x, y) if callable(width) else width)/2
        front = [(x, y, z+half(x, y)) for x, y in a]
        back = [(x, y, z-half(x, y)) for x, y in a]
        face(front, bone, color, **(inlays or {}).get('front', {}))
        face(back[::-1], bone, color, **(inlays or {}).get('back', {}))
        for i in range(len(a)):
            j = (i+1) % len(a)
            face([back[i], back[j], front[j], front[i]], bone,
                 edge_colors.get(i, color) if edge_colors else color,
                 **(inlays or {}).get(i, {}))
        parts[name] = [start, len(rig['positions'])]

    L, W, H, leg = d['body'], d['width'], d['withers'], d['leg']
    # Retain measured withers, clearance and width. Taper the lower end faces
    # by 1/3 hoof length in X to remove the overhanging socket ledge. The shallow
    # lower bevel is a light belly panel using the existing warm palette texel.
    depth = H-leg
    rings = []
    for x, top in [(-L/2, H), (L/2, H-depth/12)]:
        shoulder = W-d['head_width']*.8 if x < 0 else W
        rings.append([(x-np.sign(x)*d['hoof_length']/3*(top-y)/(top-leg), y, z) for y, z in [
            (leg, -W*.45), (leg, W*.45), (leg+depth/10, W/2),
            (top-depth/5, shoulder/2), (top, W/4), (top, -W/4),
            (top-depth/5, -shoulder/2), (leg+depth/10, -W/2)]])
    start = len(rig['positions'])
    face(rings[0], 'Spine')
    face(rings[1][::-1], 'Spine')
    for i in range(8):
        j = (i+1) % 8
        face([rings[1][i], rings[1][j], rings[0][j], rings[0][i]], 'Spine', 1 if i in (0, 1, 7) else 0)
    parts['body'] = [start, len(rig['positions'])]

    # Four closed legs: narrow black points, wider shoulders. Centres remain at
    # constant Z; the small rear hock displaces only along X. Anchor each hidden
    # top cap to Spine so the inherited Run swing cannot pull it out of its socket.
    for front in (True, False):
        x = (-1 if front else 1) * L * .40
        h = d['hoof_length']/2
        knee = 0 if front else h/4
        # The barrel's underside is bevelled: at the legs' lateral position it
        # starts above `leg`. Bury the upper third of barrel depth inside it,
        # preserving the visible ground-to-belly clearance while closing end-view gaps.
        root_y = leg + depth/3
        profile = ([(x-h*1.5, root_y), (x+h*1.5, root_y), (x+h, leg*.55),
                    (x+h, 0), (x-h, 0), (x-h, leg*.55)] if front else
                   [(x-h*1.5, root_y), (x+h*1.5, root_y), (x+h+knee, leg*.55),
                    (x+h, 0), (x-h, 0)])
        for side in ('Left', 'Right'):
            bone = side + ('_Front' if front else '_Back')
            z = (1 if side == 'Left' else -1) * W/3
            # Affine Z width keeps the two broad sides planar; hoof width stays
            # at its measured 0.15 m and only the upper shoulder broadens.
            prism(bone, profile, lambda x, y: d['hoof_width']*(1+y/root_y*.10), bone, 2, z)
            for i in range(*parts[bone]):
                # Nearest sampling crosses the existing coat/black texel boundary
                # halfway up the leg. Affine UVs keep that boundary consistent
                # across cap triangulations, with no new vertices or palette colours.
                rig['uvs'][i] = [.25, .75-.5*rig['positions'][i][1]/root_y]
                if abs(rig['positions'][i][1]-root_y) < 1e-7:
                    rig['skin_index'][i] = [slots['Spine'], slots[bone]]

    poll = d['height']-d['ear']
    hx = -L/2-d['head_length']*.45
    # Five landmarks, with a broad base tapering in Z toward the poll. Both
    # buried base landmarks use Spine; the head end retains the deer Skull.
    # neck[2] is the neck's FORWARD-LOWER corner and it has to sit WELL INSIDE the head, not on its
    # rear edge. At .25/.35 it landed at (-1.348, 2.110) -- effectively ON the head's rear diagonal, so
    # the two solids met along a line instead of interpenetrating, and the head's whole lower-rear face
    # from y=1.85 to 2.11 was open air. That is the gap strawberry saw ("the head isnt attached
    # properly, big gaps"), and the audit's plain volume-overlap test passed it because the bounding
    # boxes DO overlap -- see the buried-cap rule this now gets in audit_animal_geometry.py, the same
    # one the legs already had for the same reason.
    # .55/.50 puts it at (-1.54, 2.05): inside the head's top edge (y=2.12 there) and above its lower
    # edge (y=1.91), with the swept neck width 0.161 against the head's 0.29.
    neck = [(-L*.45, leg+depth*.25), (-L*.64, H),
            (hx-d['head_length']*.70, poll-d['head_height']*.55),
            (hx+d['head_length']*.18, poll),
            (-L*.27, H-depth*.22)]
    # CLAMPED at head_width. The sweep tapers the neck from the shoulder forward, and past hx it kept
    # right on tapering -- 0.126 m at the head end against the head's 0.29 -- so a wide head met a narrow
    # neck and you saw the step, however deeply the two interpenetrated. Seating it fixed the gap and not
    # the SILHOUETTE (strawberry: "it still looks like a separate component"). Holding the forward half
    # at head width makes the neck run into the head as one mass.
    # ...at .92 of head width, NOT flush at 1.0. Flush makes the neck's side faces coplanar with the
    # head's, which fails the buried-root rule for a real reason -- coplanar faces z-fight. .92 leaves
    # ~6 mm of clearance a side on a 290 mm head: invisible, and the cap stays strictly inside.
    prism('neck', neck, lambda x, y: max(d['head_width']*.92,
                                         d['head_width']+(x-hx)/(-L*.27-hx)*(W*.75-d['head_width'])), 'Skull')
    for i in range(*parts['neck']):
        if any(np.allclose(rig['positions'][i][:2], a, atol=1e-7) for a in (neck[0], neck[4])):
            rig['skin_index'][i] = [slots['Spine'], slots['Skull']]
    hl, hh = d['head_length'], d['head_height']
    head = [(hx, poll), (hx-hl*.35, poll-hh*.12),
            (hx-hl, poll-hh*.75), (hx-hl*.90, poll-hh),
            (hx-hl*.30, poll-hh*.75)]
    x, y, e = hx-hl*.28, poll-hh*.30, d['eye']/2
    eyes = {side: dict(inset=[(x-e,y-e,z),(x+e,y-e,z),(x+e,y+e,z),(x-e,y+e,z)])
            for side, z in [('front', d['head_width']/2), ('back', -d['head_width']/2)]}
    a, b = np.array(head[1]), np.array(head[2])
    center, delta = a*.75+b*.25, (b-a)*.14
    blaze = [(center[0]-delta[0],center[1]-delta[1],0),
             (center[0],center[1],d['eye']),
             (center[0]+delta[0],center[1]+delta[1],0),
             (center[0],center[1],-d['eye'])]
    # profile is counterclockwise; edge 1 is the sloping forehead.
    # BASE COLOUR 0 -- the body/neck brown, not palette 1. The head was drawn entirely in the warm
    # muzzle texel, which is most of why it read as a bolted-on part regardless of geometry
    # (strawberry: "unify the head-body color"). Palette 1 now covers only edges 2 and 3, the muzzle
    # front and the lower jaw, which is the marking a bay horse actually has.
    prism('head', head, d['head_width'], 'Skull', 0, edge_colors={2: 1, 3: 1},
          inlays={**eyes, 1: dict(inset=blaze, inset_color=3)})
    # Thin mane follows the rear crest; it is a solid prism, not a billboard.
    # The mane straddles the neck crest, and it must sit DEEP enough to stay buried when the head turns:
    # it is skinned to Spine while the neck's forward half is Skull, so under Glance_0/Glance_1 the two
    # move apart and a shallow mane peels off the crest. At -mane_width/3 the audit measured 0.04 mm of
    # remaining overlap mid-glance -- floating, the same defect the first pass had at rest.
    mane = [(neck[3][0]-d['mane_width']*1.2, poll-d['mane_width']*1.2),
            (neck[3][0]+d['mane_width'], poll),
            (neck[4][0]+d['mane_width'], neck[4][1]),
            (neck[4][0]-d['mane_width']*1.2, neck[4][1]-d['mane_width']*1.2)]
    prism('mane', mane, d['mane_width'], 'Skull', 2)
    for i in range(*parts['mane']):
        if rig['positions'][i][1] < H:
            rig['skin_index'][i] = [slots['Spine'], slots['Skull']]

    # Tapered triangular ears (four faces each); no antler-shaped appendages.
    for sign in (-1, 1):
        start = len(rig['positions'])
        z = sign*d['head_width']/3
        a = np.array([hx-hl*.15, poll-hh*.10, z-d['mane_width']/2])
        b = np.array([hx+hl*.02, poll-hh*.10, z])
        c = np.array([hx-hl*.15, poll-hh*.10, z+d['mane_width']/2])
        tip = np.array([hx-hl*.04, d['height'], z])
        center = (a+b+c+tip)/4
        for f in ([a,b,c], [a,tip,b], [b,tip,c], [c,tip,a]):
            if np.dot(np.cross(f[1]-f[0], f[2]-f[0]), np.mean(f, axis=0)-center) < 0:
                f = f[::-1]
            face(f, 'Skull', 0)
        parts['ear_left' if sign > 0 else 'ear_right'] = [start, len(rig['positions'])]
    # The tail ROOT sits deep in the rump, not just inside its rear face. At L/2-hoof/3 it seated
    # 0.04 mm -- the audit's seat rule now wants 10 mm for a part this size, and 0.04 mm is a tail
    # resting against the horse rather than growing out of it. Only the buried end moves; the
    # visible silhouette outside the body is unchanged.
    tail = [(L/2-d['hoof_length']*1.2, H-depth*.18), (L/2+d['hoof_length']*.7, H-depth*.25),
            (L/2+d['hoof_length']*1.6, H-depth*.18-d['tail_length']),
            (L/2+d['hoof_length']*.7, H-depth*.18-d['tail_length']*.90)]
    prism('tail', tail, d['mane_width']*1.5, 'Spine', 2)
    rig['geometry_parts'] = parts
    rig['geometry_inlays'] = ['eye_left', 'eye_right', 'blaze']
    rig['vcount'] = len(rig['positions'])
    (ROOT/'game/content/horse_rig.json').write_text(json.dumps(rig, separators=(',', ':'), allow_nan=False)+'\n')
    im = Image.new('RGBA', (2, 2))
    im.putdata(palette)
    im.save(ROOT/'game/content/objects/Animal_Horse_tex.png')
    return rig, d, parts


if __name__ == '__main__':
    rig, dims, parts = build()
    print(json.dumps(dict(dimensions=dims, parts=parts, vertices=rig['vcount'], triangles=len(rig['faces'])//3), indent=2))
