#!/usr/bin/env python3
import json, re
from collections import Counter

idx = {}
for line in open('menu_guid_index.txt', encoding='utf-8', errors='replace'):
    line = line.strip()
    if '|' not in line: continue
    g, p = line.split('|', 1)
    idx[g] = {'path': p[:-5] if p.endswith('.meta') else p,
              'base': p.split('\\')[-1].replace('.asset.meta','').replace('.mat.meta','').replace('.meta','')}

objs = json.load(open('menu_objects.json'))   # all 251 meshed placements
# Drop editor gizmos BEFORE the origin-dedup -- a Radius/Icon gizmo shares an origin with a real fixture mesh
# (e.g. the Spotlight barricade's build-radius sphere sits on the lamp), and if the gizmo won the dedup the real
# prop was lost (the "missing light props"). Filtering here lets the fixture mesh survive.
GIZMOS = {'Radius', 'Icon', 'Icon2', 'Target', 'Effect', 'Skeleton'}
objs = [o for o in objs if o['name'] not in GIZMOS]
for o in objs:
    mi = idx.get(o['guid']); o['mesh_base'] = mi['base'] if mi else None; o['mesh_path'] = mi['path'] if mi else None
    ma = idx.get(o['mat']) if o.get('mat') else None
    o['mat_base'] = ma['base'] if ma else None; o['mat_path'] = ma['path'] if ma else None

def lodnum(o):
    # LOD level comes from the GameObject name suffix (Model_0/1/2, Foliage_0/1/2, Wheel_LOD0/1), not the mesh file
    m = re.search(r'(?:_|LOD)(\d)$', o['name'] or '')
    return int(m.group(1)) if m else 0

# dedup by rounded world origin, keep the lowest LOD (LOD0)
groups = {}
for o in objs:
    key = tuple(round(v, 2) for v in o['origin'])
    if key not in groups or lodnum(o) < lodnum(groups[key]):
        groups[key] = o
placements = list(groups.values())
json.dump(placements, open('placements.json', 'w'), indent=0)

resolved = [o for o in placements if o['mesh_path']]
meshes = sorted(set(o['mesh_path'] for o in resolved))
open('mesh_pull.txt', 'w').write('\n'.join(meshes))
print(f"unique-object placements: {len(placements)} | mesh-resolved: {len(resolved)} | unresolved: {len(placements)-len(resolved)}")
print(f"unique mesh assets to pull: {len(meshes)}")
print("=== object inventory (mesh_base x count) ===")
for n, c in Counter(o['mesh_base'] for o in resolved).most_common(60):
    print(f"  {c:3d}  {n}")
