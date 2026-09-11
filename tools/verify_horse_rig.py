#!/usr/bin/env python3
"""Re-read the committed asset, independently of build_horse's in-memory output.

python3 tools/verify_horse_rig.py (numpy/scipy/Pillow)
Bounds below are the documented visible rest geometry, not Godot's culling AABB.
"""
import json
import math
import statistics

import numpy as np
from PIL import Image

from measure_animals import ROOT, NAMES, owners, posed, read
from audit_animal_geometry import audit


def finite_tree(value):
    if isinstance(value, dict):
        for v in value.values():
            finite_tree(v)
    elif isinstance(value, list):
        for v in value:
            finite_tree(v)
    elif isinstance(value, (int, float)):
        assert math.isfinite(value), value


def validate(d):
    finite_tree(d)
    vc = d['vcount']
    for field, width in [('positions', 3), ('normals', 3), ('uvs', 2),
                         ('skin_index', 2), ('skin_weight', 2)]:
        assert np.shape(d[field]) == (vc, width), field
    assert len(d['faces']) % 3 == 0
    assert all(type(i) is int and 0 <= i < vc for i in d['faces'])
    # These are indices INTO SKIN, including the zero-weight secondary slots.
    assert all(type(i) is int and 0 <= i < len(d['skin']) for row in d['skin_index'] for i in row)
    assert all(0 <= s['bone'] < len(d['bones']) for s in d['skin'])
    assert all(-1 <= b['parent'] < i for i, b in enumerate(d['bones']))
    assert len({b['name'] for b in d['bones']}) == len(d['bones'])
    w = np.array(d['skin_weight'])
    assert (w >= 0).all() and np.allclose(w.sum(1), 1, atol=1e-6)
    assert np.allclose(np.linalg.norm(d['normals'], axis=1), 1, atol=1e-5)


def main():
    fleet = {n: read(n) for n in NAMES}
    horse = read('horse')
    for d in [*fleet.values(), horse]:
        validate(d)
    findings = []
    for name, d in [*fleet.items(), ('horse', horse)]:
        result = audit(d, fleet=name in NAMES)
        print(f"Geometry audit {name}: {len(result['findings'])} findings; "
              f"{result['solids']} closed solids, {result['components']} components, {result['poses']} poses", flush=True)
        for note in result['notes']:
            print('  construction:', note)
        for finding in result['findings']:
            print('  FAIL:', finding)
        findings.extend(result['findings'])
    assert not findings, 'closure / seam / attachment audit failed'
    # Byte-for-byte data equality: existing deer clips apply without retargeting.
    assert horse['bones'] == fleet['deer']['bones']
    assert horse['anims'] == fleet['deer']['anims']
    assert [s['bone'] for s in horse['skin']] == [3, 4, 5, 6, 1, 2]
    # 380/200 against a fleet max of 329/188. Raised from 360/188 on 2026-09-11 when the neck and head
    # became ONE lofted solid instead of two interpenetrating prisms -- which is how the fleet is built and
    # the reason three rounds of "the head looks like a separate part" kept being right. A loft costs more
    # vertices than two boxes because consecutive rings do not share corners; what it buys is the absence of
    # a seam, which no amount of seating depth or palette matching could remove. Stated here rather than
    # quietly relaxed: this is the largest animal in the fleet and the only one with a real neck.
    assert horse['vcount'] <= 380 and len(horse['faces'])//3 <= 200, 'fleet geometry budget'
    lo, hi = horse['geometry_parts']['body']
    body = posed(horse)[lo:hi]
    assert np.allclose([body[:, 1].min(), body[:, 1].max()], [1.07, 1.81], atol=2e-6)
    assert np.allclose(np.ptp(body, axis=0)[[0, 2]], [1.80, .73], atol=2e-6)
    claimed_min = np.array([-1.828, 0, -.365])
    claimed_max = np.array([1.188, 2.44, .365])
    for clip in (None, 'Idle'):
        p = posed(horse, clip)
        assert np.allclose(p.min(0), claimed_min, atol=2e-6), p.min(0)
        assert np.allclose(p.max(0), claimed_max, atol=2e-6), p.max(0)
        for limb in ('Left_Front', 'Right_Front', 'Left_Back', 'Right_Back'):
            assert abs(p[owners(horse) == limb, 1].min()) < 2e-6
    p = np.array(horse['positions'])
    f = np.array(horse['faces']).reshape(-1, 3)
    cross = np.cross(p[f[:, 1]]-p[f[:, 0]], p[f[:, 2]]-p[f[:, 0]])
    assert (np.linalg.norm(cross, axis=1) > 1e-10).all(), 'degenerate triangle'
    assert (np.einsum('ij,ij->i', cross, np.array(horse['normals'])[f[:, 0]]) > 0).all()
    for name, clip in horse['anims'].items():
        for t in np.linspace(0, clip['length'], 25):
            assert np.isfinite(posed(horse, name, t)).all()
    im = Image.open(ROOT/'game/content/objects/Animal_Horse_tex.png').convert('RGBA')
    assert im.size == (2, 2) and len(im.getcolors()) == 4
    assert im.getchannel('A').getextrema() == (255, 255)
    for field, value in [('vertices', lambda d: d['vcount']), ('triangles', lambda d: len(d['faces'])//3),
                         ('bones', lambda d: len(d['bones'])), ('skin binds', lambda d: len(d['skin']))]:
        vals = [value(d) for d in fleet.values()]
        print(f'{field}: horse={value(horse)}; fleet min/median/max={min(vals)}/{statistics.median(vals)}/{max(vals)}; mean={statistics.mean(vals):.3f}')
    print('PASS: per-solid closure, posed seams and attachment overlap, arrays, skin mapping, finite data, weights/normals,')
    print('      exact deer skeleton/clips, 175 sampled clip poses, 4 grounded feet, four-colour 2x2 texture.')
    print('Rest/Idle geometry min', claimed_min.tolist(), 'max', claimed_max.tolist(),
          '; L/W/H', (claimed_max-claimed_min)[[0, 2, 1]].tolist())


if __name__ == '__main__':
    main()
