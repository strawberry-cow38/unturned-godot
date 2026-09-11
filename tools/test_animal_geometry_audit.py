#!/usr/bin/env python3
"""Adversarial instrument checks: these defects passed the old horse verifier."""
import copy
import unittest
from unittest.mock import patch

import numpy as np

from audit_animal_geometry import audit, overlap, split, strict_inside
from measure_animals import read


def box(lo=(0, 0, 0), hi=(1, 1, 1)):
    p = np.array([[x, y, z] for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])], float)
    f = np.array([[0, 1, 3], [0, 3, 2], [4, 6, 7], [4, 7, 5],
                  [0, 4, 5], [0, 5, 1], [2, 3, 7], [2, 7, 6],
                  [0, 2, 6], [0, 6, 4], [1, 5, 7], [1, 7, 3]])
    # Every face corner has its own render vertex, as with flat normals/UV seams.
    vertices = p[f].reshape(-1, 3).tolist()
    return dict(positions=vertices, faces=list(range(36)), vcount=36,
                skin_index=[[0, 0]]*36, skin_weight=[[1, 0]]*36,
                skin=[dict(bone=0)], bones=[dict(name='box')])


class GeometryAuditTests(unittest.TestCase):
    def test_flat_normal_duplicates_form_one_closed_solid(self):
        parts, _ = split(box())
        self.assertEqual(len(parts), 1)
        self.assertEqual(len(parts[0].boundary), 0)
        self.assertEqual(parts[0].seams, 0)

    def test_missing_fan_triangle_and_open_box_are_detected(self):
        for count in (3, 6):
            d = box()
            d['faces'] = d['faces'][count:]
            parts, _ = split(d)
            self.assertEqual(len(parts[0].boundary), 3 if count == 3 else 4)

    def test_reversed_triangle_and_duplicate_face(self):
        d = box()
        d['faces'][:3] = d['faces'][:3][::-1]
        self.assertGreater(split(d)[0][0].seams, 0)
        d = box()
        d['faces'] += d['faces'][:3]
        self.assertGreater(split(d)[0][0].seams, 0)

    def test_small_crack_is_not_welded_away(self):
        d = box()
        d['positions'][0][0] += .00005
        self.assertGreater(len(split(d)[0][0].boundary), 0)

    def test_overlap_rejects_touch_and_one_mm_gap(self):
        d = box()
        a = np.array(d['positions']).reshape(-1, 3, 3)
        self.assertGreater(overlap(a, a+[.99, 0, 0]), 0)
        self.assertEqual(overlap(a, a+[1, 0, 0]), 0)
        self.assertEqual(overlap(a, a+[1.001, 0, 0]), 0)

    def test_crossed_solids_without_contained_vertices(self):
        a = np.array(box((-2, -.1, -.1), (2, .1, .1))['positions']).reshape(-1, 3, 3)
        b = np.array(box((-.1, -2, -.1), (.1, 2, .1))['positions']).reshape(-1, 3, 3)
        self.assertFalse(strict_inside(a.reshape(-1, 3), b).any())
        self.assertFalse(strict_inside(b.reshape(-1, 3), a).any())
        self.assertGreater(overlap(a, b), 0)

    def test_overlapping_bounds_are_not_intersection(self):
        # Parallel diagonal slabs: AABBs overlap, the actual surfaces do not.
        a = np.array(box((-2, -.1, -.1), (2, .1, .1))['positions']).reshape(-1, 3, 3)
        rot = np.array([[1, -1, 0], [1, 1, 0], [0, 0, 2**.5]])/2**.5
        a = a @ rot.T
        b = a+[-.3, .3, 0]
        self.assertTrue((np.maximum(a.min((0, 1)), b.min((0, 1))) < np.minimum(a.max((0, 1)), b.max((0, 1)))).all())
        self.assertEqual(overlap(a, b), 0)

    def test_horse_near_duplicate_reports_named_crack(self):
        d = copy.deepcopy(read('horse'))
        d['positions'][0][0] += .00005
        findings = audit(d, animate=False)['findings']
        self.assertTrue(any('body' in f and 'unwelded positions' in f for f in findings), findings)

    def test_animated_root_escape_is_not_hidden_by_remaining_overlap(self):
        d = copy.deepcopy(read('horse'))
        lo, hi = d['geometry_parts']['Left_Front']
        # Reintroduce the original rigid root binding; Run pulls its outer
        # corners out even though the remaining leg still overlaps the body.
        slot = next(i for i, s in enumerate(d['skin']) if d['bones'][s['bone']]['name'] == 'Left_Front')
        for i in range(lo, hi):
            d['skin_index'][i] = [slot, 0]
            d['skin_weight'][i] = [1., 0.]
        with patch('audit_animal_geometry.samples', return_value=[(None, 0.), ('Run', .133333)]):
            findings = audit(d)['findings']
        self.assertIn('Left_Front -> body: exposed attachment root (Run)', findings)


if __name__ == '__main__':
    unittest.main()
