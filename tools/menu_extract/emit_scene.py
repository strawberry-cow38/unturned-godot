import json
# material name -> {texguid, color rgba, cutoff}   (menu_mat_tex.txt = name|texguid|r,g,b,a|cutoff)
mt = {}
for l in open('menu_mat_tex.txt'):
    parts = l.rstrip('\n').split('|')
    if len(parts) >= 2:
        mt[parts[0]] = {'tex': parts[1],
                        'color': parts[2] if len(parts) > 2 and parts[2] else '1,1,1,1',
                        'cutoff': parts[3] if len(parts) > 3 and parts[3] else ''}
idx = {}
for l in open('menu_guid_index.txt'):
    l = l.strip()
    if '|' in l:
        g, p = l.split('|', 1)
        idx[g] = p.split('\\')[-1].replace('.asset.meta', '').replace('.meta', '')
p = json.load(open('placements.json'))
res = [o for o in p if o['mesh_path']]
scene = []
for o in res:
    b = o['basis']
    m = mt.get(o.get('mat_base'), {})
    tg = m.get('tex')
    tex = idx.get(tg) if tg else None
    if tex and not tex.endswith('.png'):
        tex = tex + '.png'
    col = [float(x) for x in m.get('color', '1,1,1,1').split(',')] if m.get('color') else [1, 1, 1, 1]
    cutoff = float(m['cutoff']) if m.get('cutoff') else None
    scene.append({'mesh': o['mesh_base'], 'origin': o['origin'],
                  'xaxis': [b[0], b[3], b[6]], 'yaxis': [b[1], b[4], b[7]], 'zaxis': [b[2], b[5], b[8]],
                  'tex': tex, 'color': [round(c, 4) for c in col[:3]], 'cutoff': cutoff,
                  'name': o['name'], 'parent': o['parent']})
json.dump(scene, open('/mnt/bigdisk/rubble-warmup/game/content/menu/menu_scene.json', 'w'))
print('emitted', len(scene), '| textured:', sum(1 for s in scene if s['tex']),
      '| non-white _Color:', sum(1 for s in scene if s['color'] != [1, 1, 1]))
