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

    def face(points, bone, color=0):
        p = np.array(points, dtype=float)
        normal = np.cross(p[1] - p[0], p[2] - p[0])
        normal /= np.linalg.norm(normal)
        start = len(rig['positions'])
        for v in p:
            rig['positions'].append(np.round(v, 8).tolist())
            rig['normals'].append(normal.tolist())
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

    def prism(name, profile, width, bone, color=0, z=0, edge_colors=None):
        start = len(rig['positions'])
        a = np.array(profile, float)
        area = sum(a[(i+1) % len(a), 0]*a[i, 1]-a[(i+1) % len(a), 1]*a[i, 0] for i in range(len(a)))
        if area > 0:
            a = a[::-1]
        front = [(x, y, z+width/2) for x, y in a]
        back = [(x, y, z-width/2) for x, y in a]
        face(front, bone, color)
        face(back[::-1], bone, color)
        for i in range(len(a)):
            j = (i+1) % len(a)
            face([back[i], back[j], front[j], front[i]], bone,
                 edge_colors.get(i, color) if edge_colors else color)
        parts[name] = [start, len(rig['positions'])]

    L, W, H, leg = d['body'], d['width'], d['withers'], d['leg']
    # Octagonal barrel: top bevel = 1/4 width and 1/5 depth; rear top
    # drops 1/12 of depth. The flat underside spans 90% of width (the source
    # barrels are flat across 100%): the leg outer edges then fit UNDER it,
    # so the visible leg/clearance stays exactly `leg`, including in end views.
    depth = H-leg
    rings = []
    for x, top in [(-L/2, H), (L/2, H-depth/12)]:
        rings.append([(x, y, z) for y, z in [
            (leg, -W*.45), (leg, W*.45), (leg+depth/5, W/2),
            (top-depth/5, W/2), (top, W/4), (top, -W/4),
            (top-depth/5, -W/2), (leg+depth/5, -W/2)]])
    start = len(rig['positions'])
    face(rings[0], 'Spine')
    face(rings[1][::-1], 'Spine')
    for i in range(8):
        j = (i+1) % 8
        face([rings[1][i], rings[1][j], rings[0][j], rings[0][i]], 'Spine')
    parts['body'] = [start, len(rig['positions'])]

    # Four separate one-bone legs, matching the fleet's joint budget. Front
    # knee is straight; the hock offsets 1/2 hoof-length to form a rear bend.
    for front in (True, False):
        x = (-1 if front else 1) * L * .40
        h = d['hoof_length']/2
        knee = 0 if front else h
        # The barrel's underside is bevelled: at the legs' lateral position it
        # starts above `leg`. Bury the upper third of barrel depth inside it,
        # preserving the visible ground-to-belly clearance while closing end-view gaps.
        root_y = leg + depth/3
        profile = ([(x-h, root_y), (x+h, root_y), (x+h, 0), (x-h, 0)] if front else
                   [(x-h, root_y), (x+h, root_y), (x+h+knee, leg/2),
                    (x+h, 0), (x-h, 0), (x-h+knee, leg/2)])
        for side in ('Left', 'Right'):
            bone = side + ('_Front' if front else '_Back')
            z = (1 if side == 'Left' else -1) * W/3
            prism(bone, profile, d['hoof_width'], bone, 2, z)

    poll = d['height']-d['ear']
    hx = -L/2-d['head_length']*.45
    # Neck reaches the poll; six outline landmarks are fractions of measured
    # barrel/head dimensions. The whole neck uses Skull, like the fleet.
    neck = [(-L*.48, leg+depth*.25), (-L*.64, H),
            (hx-d['head_length']*.12, poll-d['head_height']*.35),
            (hx+d['head_length']*.18, poll),
            (-L*.27, H-depth*.08), (-L*.32, leg+depth*.35)]
    prism('neck', neck, d['head_width']*1.1, 'Skull')
    hl, hh = d['head_length'], d['head_height']
    head = [(hx, poll), (hx-hl*.35, poll-hh*.12),
            (hx-hl, poll-hh*.75), (hx-hl*.90, poll-hh),
            (hx-hl*.30, poll-hh*.75), (hx, poll-hh*.35)]
    prism('head', head, d['head_width'], 'Skull', 1)
    # Thin mane follows the rear crest; it is a solid prism, not a billboard.
    mane = [neck[3], (neck[3][0]+d['mane_width'], poll),
            (-L*.27+d['mane_width'], H-depth*.08), neck[4]]
    prism('mane', mane, d['mane_width'], 'Skull', 2)

    # Tapered triangular ears (four faces each); no antler-shaped appendages.
    start = len(rig['positions'])
    for sign in (-1, 1):
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
    parts['ears'] = [start, len(rig['positions'])]
    tail = [(L/2, H-depth*.18), (L/2+d['hoof_length']*.7, H-depth*.25),
            (L/2+d['hoof_length']*1.6, H-depth*.18-d['tail_length']),
            (L/2+d['hoof_length']*.7, H-depth*.18-d['tail_length']*.90)]
    prism('tail', tail, d['mane_width']*1.5, 'Spine', 2)
    # Eyes sit 1 mm above each cheek to avoid z-fighting, = eye-size / 40.
    start = len(rig['positions'])
    for sign in (-1, 1):
        x, y, e = hx-hl*.28, poll-hh*.30, d['eye']/2
        z = sign*(d['head_width']/2+d['eye']/40)
        points = [(x-e,y-e,z),(x+e,y-e,z),(x+e,y+e,z),(x-e,y+e,z)]
        face(points if sign > 0 else points[::-1], 'Skull', 2)
    parts['eyes'] = [start, len(rig['positions'])]
    # Ivory forehead diamond on the sloping front face, not a texture gradient.
    a, b = np.array(head[1]), np.array(head[2])
    center = a*.75+b*.25
    delta = (b-a)*.14
    outward = np.array([(b-a)[1], -(b-a)[0]])
    outward /= np.linalg.norm(outward)
    center += outward*d['eye']/40
    p = [(center[0]-delta[0],center[1]-delta[1],0),
         (center[0],center[1],d['eye']),
         (center[0]+delta[0],center[1]+delta[1],0),
         (center[0],center[1],-d['eye'])]
    face(p[::-1], 'Skull', 3)

    rig['vcount'] = len(rig['positions'])
    (ROOT/'game/content/horse_rig.json').write_text(json.dumps(rig, separators=(',', ':'), allow_nan=False)+'\n')
    im = Image.new('RGBA', (2, 2))
    im.putdata(palette)
    im.save(ROOT/'game/content/objects/Animal_Horse_tex.png')
    return rig, d, parts


if __name__ == '__main__':
    rig, dims, parts = build()
    print(json.dumps(dict(dimensions=dims, parts=parts, vertices=rig['vcount'], triangles=len(rig['faces'])//3), indent=2))
