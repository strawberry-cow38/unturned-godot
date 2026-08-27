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

# NO origin-dedup. Retail stacks DISTINCT meshes at one transform -- a tree is trunk Model_0 + canopy
# Foliage_0; a fence section is Alive + Dead. Origin-keying kept one arbitrarily and dropped the other,
# losing 34 objects (19 tree halves + 15 fences). Instead keep every visible LOD0 object, filter by role:
def is_higher_lod(o):
    n = o['name'] or ''
    return n in ('Model_1', 'Model_2', 'Foliage_1', 'Foliage_2') or bool(re.search(r'(?:_|LOD)[1-9]$', n))
def keep(o):
    mb = o.get('mesh_base') or ''
    # destroyed fence variant -> show Alive. The GO name is inconsistent (Model_0/Alive/Dead), so gate on the
    # MESH name, which reliably ends _Dead vs _Alive.
    if o['name'] == 'Dead' or '_Dead' in mb: return False
    if o.get('active', 1) == 0:              return False   # honor m_IsActive (drops the hidden Engine; Dead is active=1)
    if is_higher_lod(o):                     return False   # keep LOD0 only
    return True
placements = [o for o in objs if keep(o)]
json.dump(placements, open('placements.json', 'w'), indent=0)

resolved = [o for o in placements if o['mesh_path']]
meshes = sorted(set(o['mesh_path'] for o in resolved))
open('mesh_pull.txt', 'w').write('\n'.join(meshes))
print(f"unique-object placements: {len(placements)} | mesh-resolved: {len(resolved)} | unresolved: {len(placements)-len(resolved)}")
print(f"unique mesh assets to pull: {len(meshes)}")
print("=== object inventory (mesh_base x count) ===")
for n, c in Counter(o['mesh_base'] for o in resolved).most_common(60):
    print(f"  {c:3d}  {n}")
