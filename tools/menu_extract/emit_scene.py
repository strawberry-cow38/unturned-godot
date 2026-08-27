import json
mt={}
for l in open('menu_mat_tex.txt'):
    l=l.strip()
    if '|' in l: n,g=l.split('|',1); mt[n]=g
idx={}
for l in open('menu_guid_index.txt'):
    l=l.strip()
    if '|' in l: g,p=l.split('|',1); idx[g]=p.split('\\')[-1].replace('.asset.meta','').replace('.meta','')
p=json.load(open('placements.json')); res=[o for o in p if o['mesh_path']]
scene=[]
for o in res:
    b=o['basis']
    tg=mt.get(o.get('mat_base')) if o.get('mat_base') else None
    tex=idx.get(tg)+'.png' if (tg and idx.get(tg)) else None
    if tex and not tex.endswith('.png.png'): pass
    scene.append({'mesh':o['mesh_base'],'origin':o['origin'],
                  'xaxis':[b[0],b[3],b[6]],'yaxis':[b[1],b[4],b[7]],'zaxis':[b[2],b[5],b[8]],
                  'mat':o.get('mat_base'),'tex':idx.get(tg) if tg else None,'name':o['name'],'parent':o['parent']})
# tex should carry the .png extension (idx already strips .asset.meta -> basename incl .png for textures)
for s in scene:
    if s['tex'] and not s['tex'].endswith('.png'): s['tex']=s['tex']+'.png'
json.dump(scene, open('/mnt/bigdisk/rubble-warmup/game/content/menu/menu_scene.json','w'))
print('emitted', len(scene), 'placements; textured:', sum(1 for s in scene if s['tex']))
