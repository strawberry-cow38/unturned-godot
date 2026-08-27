import json, os
os.system('python3 parse_menu.py Menu_NoHoliday.unity >/dev/null 2>&1')
objs = json.load(open('menu_objects.json'))
idx = {}
for l in open('menu_guid_index.txt'):
    l = l.strip()
    if '|' in l:
        g, p = l.split('|', 1)
        idx[g] = p.split('\\')[-1].replace('.asset.meta', '').replace('.mat.meta', '').replace('.meta', '')
mt = {}
for l in open('menu_mat_tex.txt'):
    parts = l.strip().split('|')
    if len(parts) >= 2:
        mt[parts[0]] = {'tex': parts[1], 'color': parts[2] if len(parts) > 2 else '1,1,1,1',
                        'cutoff': parts[3] if len(parts) > 3 else ''}
sp = '/mnt/bigdisk/rubble-warmup/game/content/menu/menu_scene.json'
scene = json.load(open(sp))
before = len(scene)
added = 0
for o in objs:
    mesh_base = idx.get(o['guid'])
    if not mesh_base:
        continue
    mat_base = idx.get(o['mat']) if o.get('mat') else None
    m = mt.get(mat_base, {})
    tg = m.get('tex')
    tex = idx.get(tg) if tg else None
    if tex and not tex.endswith('.png'):
        tex = tex + '.png'
    col = [float(x) for x in m.get('color', '1,1,1,1').split(',')]
    cutoff = float(m['cutoff']) if m.get('cutoff') else None
    b = o['basis']
    scene.append({'mesh': mesh_base, 'origin': o['origin'],
                  'xaxis': [b[0], b[3], b[6]], 'yaxis': [b[1], b[4], b[7]], 'zaxis': [b[2], b[5], b[8]],
                  'tex': tex, 'color': [round(c, 4) for c in col[:3]], 'cutoff': cutoff,
                  'name': o['name'], 'parent': 'NoHoliday'})
    added += 1
    org = [round(v, 1) for v in o['origin']]
    print('  flag: mesh=%s tex=%s color=%s @ %s' % (mesh_base, tex, col[:3], org))
json.dump(scene, open(sp, 'w'))
print('added %d NoHoliday obj(s); scene %d -> %d' % (added, before, len(scene)))
os.system('python3 parse_menu.py Menu_Base.unity >/dev/null 2>&1')
have = set(os.listdir('/mnt/bigdisk/rubble-warmup/game/content/menu/tex'))
miss = [o['tex'] for o in scene if o['parent'] == 'NoHoliday' and o['tex'] and o['tex'] not in have]
print('flag tex missing from repo:', miss)
