#!/usr/bin/env python3
"""Audit actual triangles after skinning, independent of the horse generator.

Hard-normal/UV duplicates are welded for topology only (1 micrometre export
precision); distinct positions less than 0.1 mm apart are reported as cracks.
Each position-connected solid needs opposite edge pairs. Shared bind vertices
must also stay together after posing. Attachment tests require an interior
witness in BOTH triangle meshes, never just intersecting bounding boxes/hulls.

The retail fleet has a sewn body/legs/head surface, buried open ear/antler
roots, and two eye decals. These are measured constructions, not closed
prisms. Buried rims must remain inside the closed core; planar two-triangle
decals must remain within 7 mm of it (cow's measured maximum is 6.804 mm).
All such surfaces are explicitly counted.
Horse parts are required closed, including markings. No fleet asset is edited.
"""
from collections import Counter, defaultdict
from dataclasses import dataclass
import json

import numpy as np
from scipy.spatial import cKDTree

from measure_animals import NAMES, owners, posed, read

EXPORT_EPS = 1e-6
NEAR = 1e-4
INTERIOR = 1e-5


@dataclass
class Part:
    name: str
    faces: np.ndarray
    vertices: np.ndarray
    boundary: np.ndarray
    seams: int


def split(d):
    p = np.array(d['positions'])
    parent = list(range(len(p)))

    def root(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for a, b in sorted(cKDTree(p).query_pairs(EXPORT_EPS)):
        parent[root(b)] = root(a)
    weld = np.array([root(i) for i in range(len(p))])
    groups = defaultdict(list)
    for i, w in enumerate(weld):
        groups[int(w)].append(i)
    # Component membership uses shared positions, not the generator's labels.
    tri = np.array(d['faces']).reshape(-1, 3)
    for a, b, c in weld[tri]:
        parent[root(b)] = root(a)
        parent[root(c)] = root(a)
    components = defaultdict(list)
    for t in tri:
        components[root(t[0])].append(t)
    parts = []
    own = owners(d)
    legacy = {0: 'body', 48: 'Left_Front', 72: 'Right_Front', 96: 'Left_Back',
              132: 'Right_Back', 168: 'neck', 204: 'head', 240: 'mane',
              264: 'ear_right', 276: 'ear_left', 288: 'tail',
              312: 'eye_right', 316: 'eye_left', 320: 'blaze'}
    labels = d.get('geometry_parts', {})
    for ts in components.values():
        f = np.array(ts)
        vs = np.unique(f)
        edge = Counter((int(a), int(b)) for t in weld[f] for a, b in zip(t, np.roll(t, -1)))
        boundary = [(a, b) for (a, b), n in edge.items() if not edge[(b, a)]]
        seams = sum(n > 1 for n in edge.values())
        name = '+'.join(sorted(set(own[vs]))) + f'@{vs.min()}'
        if labels:
            matched = [k for k, (lo, hi) in labels.items() if np.all((vs >= lo) & (vs < hi))]
            if len(matched) == 1:
                name = matched[0]
        elif d['vcount'] == 324 and len(tri) == 170:
            name = legacy.get(int(vs.min()), name)
        parts.append(Part(name, f, vs, np.array(boundary, dtype=int).reshape(-1, 2), seams))
    return parts, groups


def inside(points, triangles):
    """Generalized winding number on the real (possibly concave) surface."""
    points = np.atleast_2d(points)
    v = triangles[None, :, :, :] - points[:, None, None, :]
    a, b, c = v[:, :, 0], v[:, :, 1], v[:, :, 2]
    la, lb, lc = (np.linalg.norm(x, axis=-1) for x in (a, b, c))
    dot = lambda x, y: np.einsum('...i,...i->...', x, y)
    det = dot(a, np.cross(b, c))
    den = la*lb*lc + dot(a, b)*lc + dot(b, c)*la + dot(c, a)*lb
    winding = np.arctan2(det, den).sum(axis=1) / (2*np.pi)
    return np.abs(winding) > .99


def distance(points, triangles):
    """Minimum point/triangle distance, including triangle edges and vertices."""
    q = np.atleast_2d(points)[:, None, :]
    a, b, c = triangles[:, 0], triangles[:, 1], triangles[:, 2]
    ab, ac = b-a, c-a
    n = np.cross(ab, ac)
    n /= np.maximum(np.linalg.norm(n, axis=1)[:, None], 1e-30)
    h = np.einsum('qti,ti->qt', q-a, n)
    proj = q-h[:, :, None]*n
    den = np.einsum('ti,ti->t', np.cross(ab, ac), n)
    u = np.einsum('qti,ti->qt', np.cross(proj-a, ac), n) / den
    v = np.einsum('qti,ti->qt', np.cross(ab, proj-a), n) / den
    dist = np.where((u >= 0) & (v >= 0) & (u+v <= 1), np.abs(h), np.inf)
    for x, y in ((a, b), (b, c), (c, a)):
        edge = y-x
        t = np.clip(np.einsum('qti,ti->qt', q-x, edge) / np.einsum('ti,ti->t', edge, edge), 0, 1)
        dist = np.minimum(dist, np.linalg.norm(q-(x+t[:, :, None]*edge), axis=-1))
    return dist.min(axis=1)


def strict_inside(points, triangles, eps=INTERIOR):
    return inside(points, triangles) & (distance(points, triangles) > eps)


def overlap(a, b):
    """Find positive-volume intersection witnesses; reject touching/floating solids.

    A small interior lattice plus inward face samples handles containment. Edge /
    triangle intersections add candidates for thin crossed prisms with no vertex
    in the other solid. Every witness is verified against both actual meshes.
    Returns a conservative lower bound on interior clearance, zero if none found.
    """
    lo = np.maximum(a.min(axis=(0, 1)), b.min(axis=(0, 1)))
    hi = np.minimum(a.max(axis=(0, 1)), b.max(axis=(0, 1)))
    if np.any(hi-lo <= 2*INTERIOR):
        return 0.
    candidates = [np.array(np.meshgrid(*[np.linspace(l, h, 5)[1:-1] for l, h in zip(lo, hi)])).reshape(3, -1).T]
    for mesh in (a, b):
        n = np.cross(mesh[:, 1]-mesh[:, 0], mesh[:, 2]-mesh[:, 0])
        n /= np.maximum(np.linalg.norm(n, axis=1)[:, None], 1e-30)
        candidates.extend([mesh.mean(axis=1)-sign*n*INTERIOR*4 for sign in (-1, 1)])
    q = np.concatenate(candidates)
    ok = strict_inside(q, a) & strict_inside(q, b)
    if ok.any():
        return float(np.minimum(distance(q[ok], a), distance(q[ok], b)).max())
    # Moller-Trumbore along all edges, then probe either side of each face.
    for mesh, other in ((a, b), (b, a)):
        n = np.cross(mesh[:, 1]-mesh[:, 0], mesh[:, 2]-mesh[:, 0])
        n /= np.maximum(np.linalg.norm(n, axis=1)[:, None], 1e-30)
        for k in range(3):
            x, y = mesh[:, k], mesh[:, (k+1) % 3]
            ray = y-x
            e1, e2 = other[:, 1]-other[:, 0], other[:, 2]-other[:, 0]
            h = np.cross(ray[:, None, :], e2)
            det = np.einsum('ti,eti->et', e1, h)
            inv = np.divide(1., det, out=np.zeros_like(det), where=np.abs(det) > 1e-12)
            s = x[:, None, :]-other[:, 0]
            u = inv*np.einsum('eti,eti->et', s, h)
            cross = np.cross(s, e1)
            v = inv*np.einsum('ei,eti->et', ray, cross)
            t = inv*np.einsum('ti,eti->et', e2, cross)
            hit = (np.abs(det) > 1e-12) & (u >= 0) & (v >= 0) & (u+v <= 1) & (t >= 0) & (t <= 1)
            probes = []
            for i in range(len(mesh)):
                cuts = sorted({0., 1., *t[i, hit[i]].tolist()})
                for l, r in zip(cuts, cuts[1:]):
                    mid = x[i]+ray[i]*(l+r)/2
                    probes.extend([mid+n[i]*INTERIOR*4, mid-n[i]*INTERIOR*4])
            if probes:
                q = np.array(probes)
                ok = strict_inside(q, a) & strict_inside(q, b)
                if ok.any():
                    return float(np.minimum(distance(q[ok], a), distance(q[ok], b)).max())
    return 0.


def samples(d):
    yield None, 0.
    for name, clip in d['anims'].items():
        # All source keys, midpoints between keys, plus 25 uniform samples.
        keys = sorted({0., clip['length'], *(float(k[0]) for track in clip['tracks'].values()
                       for channel in track.values() for k in channel)})
        times = sorted({*keys, *((a+b)/2 for a, b in zip(keys, keys[1:])),
                        *np.linspace(0, clip['length'], 25)})
        for t in times:
            yield name, t


def audit(d, fleet=False, animate=True):
    parts, weld = split(d)
    findings = set()
    notes = set()
    closed = [s for s in parts if not len(s.boundary) and not s.seams]
    core = max(parts, key=lambda s: len(s.faces))
    if fleet:
        core.name = 'sewn_body_head_legs'
        for s in parts:
            if s is not core:
                side = 'left' if posed(d)[s.vertices, 2].mean() > 0 else 'right'
                s.name = ('eye_' if len(s.faces) == 2 else 'ear_or_antler_') + side
    # Export precision does not hide near-miss cracks.
    rest = posed(d)
    unique = np.array([rest[v[0]] for v in weld.values()])
    representatives = [v[0] for v in weld.values()]
    for a, b in cKDTree(unique).query_pairs(NEAR):
        va, vb = representatives[a], representatives[b]
        names = sorted({s.name for s in parts if va in s.vertices or vb in s.vertices})
        findings.add(f'{" / ".join(names)}: unwelded positions {va}/{vb} separated by {np.linalg.norm(unique[a]-unique[b]):.8f} m')
    for s in parts:
        if s.seams:
            findings.add(f'{s.name}: {s.seams} non-manifold/doubled directed edges')
        if len(s.boundary) and not (fleet and s is not core):
            findings.add(f'{s.name}: NOT CLOSED, {len(s.boundary)} boundary edges')
    attachments = []
    if not fleet:
        byname = {s.name: s for s in parts}
        for child, parent in [('Left_Front', 'body'), ('Right_Front', 'body'), ('Left_Back', 'body'),
                              ('Right_Back', 'body'), ('neck', 'body'), ('head', 'neck'), ('mane', 'neck'),
                              ('tail', 'body'), ('ear_left', 'head'), ('ear_right', 'head'),
                              ('eye_left', 'head'), ('eye_right', 'head'), ('blaze', 'head')]:
            if child not in byname or parent not in byname:
                if child not in d.get('geometry_inlays', []) or parent != 'head':
                    findings.add(f'{child} -> {parent}: missing named attachment')
                else:
                    notes.add(f'{child}: colour inlay in closed head surface')
            elif byname[child] in closed and byname[parent] in closed:
                attachments.append((byname[child], byname[parent]))
    worst = {}
    caps = {}
    for child, parent in attachments:
        if child.name in ('Left_Front', 'Right_Front', 'Left_Back', 'Right_Back'):
            # The whole root cap must stay buried. A sliver of intersection can
            # pass the volume test while most of the swinging root is exposed.
            y = rest[child.vertices, 1]
            caps[child.name] = child.vertices[np.isclose(y, y.max(), atol=EXPORT_EPS)]
        elif child.name == 'neck':
            y = rest[child.vertices, 1]
            caps[child.name] = child.vertices[y <= y.min()+np.ptp(y)*.15]
    count = 0
    for clip, t in (samples(d) if animate else [(None, 0.)]):
        count += 1
        p = posed(d, clip, t)
        when = f'{clip or "rest"}@{t:.6f}'
        for vs in weld.values():
            if np.max(np.linalg.norm(p[vs]-p[vs[0]], axis=1)) > EXPORT_EPS*3:
                findings.add(f'posed seam splits at bind vertex {vs[0]} ({when})')
        for s in parts:
            tris = p[s.faces]
            area = np.linalg.norm(np.cross(tris[:, 1]-tris[:, 0], tris[:, 2]-tris[:, 0]), axis=1)
            if np.any(area < 1e-10):
                findings.add(f'{s.name}: posed degenerate triangle ({when})')
            if fleet and len(s.boundary) and s is not core:
                support = p[core.faces]
                if len(s.faces) == 2:
                    q = np.concatenate([p[s.vertices], tris.mean(axis=1)])
                    if distance(q, support).max() > .007:
                        findings.add(f'{s.name}: unsupported eye decal ({when})')
                    notes.add(f'{s.name}: supported two-triangle decal (not a solid)')
                else:
                    edges = p[s.boundary]
                    q = np.concatenate([edges[:, 0], edges.mean(axis=1)])
                    if not strict_inside(q, support, EXPORT_EPS).all():
                        findings.add(f'{s.name}: exposed open root ({when})')
                    notes.add(f'{s.name}: open root buried in closed core')
        for child, parent in attachments:
            key = f'{child.name} -> {parent.name}'
            if child.name in caps:
                cap = p[caps[child.name]]
                if not strict_inside(cap, p[parent.faces]).all():
                    findings.add(f'{key}: exposed attachment root ({clip or "rest"})')
            margin = overlap(p[child.faces], p[parent.faces])
            if key not in worst or margin < worst[key]['clearance_m']:
                worst[key] = dict(clearance_m=margin, pose=when)
            if margin == 0:
                # Aggregate by joint/clip rather than burying useful names in hundreds of lines.
                findings.add(f'{key}: NO VOLUME OVERLAP ({clip or "rest"})')
    return dict(findings=sorted(findings), notes=sorted(notes), solids=len(closed),
                components=len(parts), poses=count, attachments=worst)


def main():
    results = {name: audit(read(name), fleet=name in NAMES) for name in (*NAMES, 'horse')}
    print(json.dumps(results, indent=2))
    return int(any(r['findings'] for r in results.values()))


if __name__ == '__main__':
    raise SystemExit(main())
