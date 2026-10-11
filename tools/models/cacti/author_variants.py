#!/usr/bin/env python3
"""Approved 14 spike-free cactus models; low/broad Barrel_1 explicitly excluded."""
from pathlib import Path
import json,math,hashlib,collections
from geometry import Model,ROOT,MODELS,PALETTE,REPO,bounds,sha

OLD=ROOT.parent/'ug-cactus-study'
STYLE=ROOT/'source_style.json'
if not STYLE.exists():STYLE.write_text((OLD/'manifest.json').read_text())
OLD_MAN=json.loads(STYLE.read_text())
protected={str(p):sha(p) for p in [OLD/'manifest.json',*sorted((OLD/'models').iterdir())]} if OLD.exists() else {}
BASELINE=ROOT/'baseline_bodies.json'
frozen=json.loads(BASELINE.read_text()) if BASELINE.exists() else {}
families=[('Column','Short columns'),('Branched','Tall branching'),('Barrel','Round barrels'),('Paddles','Paddle fans'),('Cluster','Column clumps')]
models=[]
for rejected_suffix in ('.obj','.mtl'):
    rejected=MODELS/('Cactus_Barrel_1'+rejected_suffix)
    if rejected.exists():rejected.unlink()

def base_body(family,label):
    name='Cactus_'+family+'_0';path=OLD/'models'/(name+'.obj')
    m=Model(name,label);m.family=family;m.variant=0
    if family in frozen:
        m.tris=frozen[family]['triangles'];m.parts=frozen[family]['parts'];m.base_removal=frozen[family]['base_removal']
        return m
    ps=[];uv=[];ns=[];part='';removed=0
    for line in path.read_text().splitlines():
        q=line.split()
        if not q:continue
        if q[0]=='v':ps.append(tuple(map(float,q[1:4])))
        elif q[0]=='vt':uv.append(tuple(map(float,q[1:3])))
        elif q[0]=='vn':ns.append(tuple(map(float,q[1:4])))
        elif q[0]=='o':part=q[1]
        elif q[0]=='f':
            if 'spine' in part.lower():removed+=1;continue
            corners=[tuple(int(v)-1 for v in c.split('/')) for c in q[1:]]
            assert len(corners)==3
            colour=int(math.floor(uv[corners[0][1]][0]*8))
            assert all(int(math.floor(uv[c[1]][0]*8))==colour for c in corners)
            assert all(ns[c[2]]==ns[corners[0][2]] for c in corners)
            m.tris.append({'p':[ps[c[0]] for c in corners],'n':ns[corners[0][2]],'color':colour,'part':part})
    asset=next(e for e in OLD_MAN['assets'] if e['name']==name)
    m.parts=[p for p in asset['parts'] if 'spine' not in p['name'].lower()]
    m.base_removal={'source':str(path),'source_sha256':sha(path),'removed_spike_triangles':removed,
                    'retained_body_triangles':len(m.tris),'retained_body_corner_attributes_exact':True}
    return m

def variant(family,index,label):
    m=Model(f'Cactus_{family}_{index}',label);m.family=family;m.variant=index
    return m

# Same five bodies, no resculpting while removing their spikes.
bases={f:base_body(f,label) for f,label in [('Column','Straight'),('Branched','Two arms'),('Barrel','Round'),('Paddles','Five-pad fan'),('Cluster','Three stems')]}
if not BASELINE.exists():
    BASELINE.write_text(json.dumps({f:{'triangles':m.tris,'parts':m.parts,'base_removal':m.base_removal} for f,m in bases.items()}))

variants={f:[] for f,_ in families}
m=variant('Column',1,'Stout')
m.tube('stem',[((0,-.05,0),.34),((0,.17,0),.44),((-.025,.87,0),.43),((-.045,1.44,0),.34),((-.045,1.59,0),.19),((-.045,1.66,0),0)],12)
variants['Column'].append(m)
m=variant('Column',2,'Leaning')
m.tube('stem',[((0,-.05,0),.23),((0,.13,0),.28),((.12,1.38,.04),.27),((.31,2.27,.09),.22),((.37,2.46,.10),.12),((.39,2.54,.10),0)],12)
variants['Column'].append(m)

m=variant('Branched',1,'Single high arm')
m.tube('trunk',[((0,-.08,0),.34),((0,.15,0),.42),((-.05,2.60,0),.40),((-.18,4.63,.02),.32),((-.18,4.89,.02),.18),((-.18,4.98,.02),0)],12)
m.tube('arm',[((.20,2.64,0),.23),((.83,2.68,0),.28),((1.20,3.01,0),.26),((1.24,4.09,0),.22),((1.24,4.27,0),.12),((1.24,4.33,0),0)],8)
variants['Branched'].append(m)
m=variant('Branched',2,'Three unequal arms')
m.tube('trunk',[((0,-.08,0),.39),((0,.17,0),.51),((0,2.70,0),.47),((.04,3.63,0),.37),((.04,3.91,0),.21),((.04,4.01,0),0)],12)
m.tube('arm_left',[((.25,1.24,0),.25),((1.04,1.31,0),.30),((1.39,1.65,0),.28),((1.42,2.51,0),.23),((1.42,2.76,0),.12),((1.42,2.82,0),0)],8)
m.tube('arm_right',[((-.27,2.11,.035),.25),((-.95,2.20,.035),.27),((-1.20,2.53,.035),.25),((-1.20,3.38,.035),.21),((-1.20,3.59,.035),.11),((-1.20,3.65,.035),0)],8)
m.tube('arm_forward',[((.015,1.93,.24),.21),((.02,2.03,.83),.25),((.07,2.30,1.12),.23),((.07,3.01,1.15),.19),((.07,3.19,1.15),.10),((.07,3.25,1.15),0)],8)
variants['Branched'].append(m)

m=variant('Barrel',2,'Tall offset crown')
m.tube('barrel',[((0,-.04,0),.31),((0,.10,0),.47),((.035,.66,-.01),.59),((.075,1.24,-.025),.52),((.105,1.56,-.035),.26),((.12,1.67,-.04),0)],12)
variants['Barrel'].append(m)

def make_pad_fan(index,label,pads):
    m=variant('Paddles',index,label)
    m.tube('root_stem',[((0,-.04,0),.10),((0,.17,0),.12),((0,.28,0),0)],8)
    for j,(c,w,h,d,a) in enumerate(pads):m.pad(f'pad_{j}',c,w,h,d,a)
    return m
variants['Paddles'].append(make_pad_fan(1,'Low spreading fan',[
    ((0,.46,0),.91,.98,.25,-5),
    ((.02,1.12,0),.80,1.03,.24,8),
    ((-.52,.78,.01),.87,1.01,.22,43),
    ((-1.07,1.08,.02),.81,.92,.21,55),
    ((.57,.84,-.01),.88,.97,.23,-41),
    ((1.13,1.13,-.015),.78,.88,.20,-53),
]))
variants['Paddles'].append(make_pad_fan(2,'Narrow upright fan',[
    ((0,.52,0),.75,1.10,.23,-5),
    ((.10,1.40,0),.76,1.23,.23,-8),
    ((.22,2.39,.01),.68,1.17,.21,-9),
    ((.60,1.67,.015),.63,.94,.20,-37),
]))

def make_clump(index,label,stems):
    m=variant('Cluster',index,label)
    for j,(x,z,h,r,lean) in enumerate(stems):
        m.tube(f'stem_{j}',[((x,-.05,z),r*.82),((x,.12,z),r),((x+lean*.65,h*.74,z),r*.92),((x+lean,h*.95,z),r*.51),((x+lean,h,z),0)],8)
    return m
variants['Cluster'].append(make_clump(1,'Twin stems',[
    (-.23,0,1.58,.31,-.04),(.27,.10,.99,.29,.06),
]))
variants['Cluster'].append(make_clump(2,'Four stems',[
    (0,0,2.10,.31,.01),(-.48,.055,1.54,.28,-.045),(.40,.13,1.24,.25,.04),(.13,-.37,.77,.24,-.01),
]))

for family,_ in families:models.extend([bases[family],*variants[family]])
assert len(models)==14
assert all('spine' not in t['part'].lower() and t['color']!=5 for m in models for t in m.tris)

def obj_text(model):
    lines=['# Spike-free cactus model. Native +Y up, metre scale; ground datum Y=0.',
           '# Slight buried root skirt; no auto recentre / rescale.',
           'mtllib '+model.name+'.mtl','usemtl cactus_palette','s off']
    for t in model.tris:
        for p in t['p']:lines.append('v '+' '.join(f'{x:.9f}' for x in p))
    for t in model.tris:
        for _ in range(3):lines.append(f"vt {(t['color']*4+2)/32:.9f} 0.500000000")
    for t in model.tris:
        for _ in range(3):lines.append('vn '+' '.join(f'{x:.9f}' for x in t['n']))
    part=None
    for j,t in enumerate(model.tris):
        if part!=t['part']:part=t['part'];lines.append('o '+part)
        ids=[3*j+k+1 for k in range(3)]
        lines.append('f '+' '.join(f'{k}/{k}/{k}' for k in ids))
    return '\n'.join(lines)+'\n'

def render_data(model):
    ps=[];ns=[];uv=[];ids=[]
    for t in model.tris:
        i=len(ps);ps.extend(t['p']);ns.extend([t['n']]*3)
        uv.extend([((t['color']*4+2)/32,.5)]*3);ids.extend([i,i+2,i+1])
    return {'vertices':ps,'normals':ns,'uvs':uv,'triangle_indices':ids}

entries=[]
for m in models:
    p=MODELS/(m.name+'.obj');p.write_text(obj_text(m))
    (MODELS/(m.name+'.mtl')).write_text('newmtl cactus_palette\nKd 1 1 1\nKa 0 0 0\nKs 0 0 0\nNs 1\nd 1\nmap_Kd Cactus_Palette.png\n')
    b=bounds([p for t in m.tris for p in t['p']])
    entry={'name':m.name,'family':m.family,'variant':m.variant,'label':m.label,'triangles':len(m.tris),
           'height_above_ground_m':b['max'][1],'bounds':b,'parts':m.parts,'mesh_sha256':sha(p),
           'used_palette_indices':sorted({t['color'] for t in m.tris}),'spikes':0}
    if m.variant==0:entry['base_removal']=m.base_removal
    entries.append(entry)
preview={'models':[{**e,'mesh':render_data(m)} for e,m in zip(entries,models)],'atlas':str(MODELS/'Cactus_Palette.png'),
         'families':[{'key':f,'label':label,'model_names':[m.name for m in models if m.family==f]} for f,label in families]}
(ROOT/'preview_data.json').write_text(json.dumps(preview))
manifest={'schema':'astraclaw.spike-free-cactus-variants.v3','scope':'Approved 14-model asset library; low/broad Barrel variant A removed; no spawning integration',
          'coordinates':OLD_MAN['coordinates'],'palette':PALETTE,
          'palette_source':'Existing foliage greens and two neighbouring designed greens. Pale legacy tile stays unused; no spine texture.',
          'assets':entries,'families':preview['families'],'reference_hashes':OLD_MAN['reference_hashes'],
          'files':{str(p.relative_to(ROOT)):{'bytes':p.stat().st_size,'sha256':sha(p)} for p in sorted(MODELS.iterdir())},
          'excluded_models':['Cactus_Barrel_1'],'checks':{'models':14,'spike_parts':0,'no_spine_colour_sampled':True,'original_base_body_geometry_attributes_preserved':True,
                    'source_study_untouched':True,'variants_are_authored_geometry_not_instance_scaling':True}}
(ROOT/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
assert all(sha(Path(p))==h for p,h in protected.items())
print(json.dumps({e['name']:{'label':e['label'],'triangles':e['triangles'],'height':e['height_above_ground_m']} for e in entries},indent=2))