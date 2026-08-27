#!/usr/bin/env python3
import re, json, sys, math
from collections import Counter

path = sys.argv[1] if len(sys.argv) > 1 else 'Menu_Base.unity'
txt = open(path, encoding='utf-8', errors='replace').read()

parts = re.split(r'^--- !u!(\d+) &(\d+).*$', txt, flags=re.M)
docs = []
for i in range(1, len(parts), 3):
    docs.append((int(parts[i]), int(parts[i+1]), parts[i+2]))

def vec3(body, key):
    m = re.search(re.escape(key) + r':\s*\{x:\s*([-\deE.]+),\s*y:\s*([-\deE.]+),\s*z:\s*([-\deE.]+)', body)
    return (float(m.group(1)), float(m.group(2)), float(m.group(3))) if m else None
def quat(body, key):
    m = re.search(re.escape(key) + r':\s*\{x:\s*([-\deE.]+),\s*y:\s*([-\deE.]+),\s*z:\s*([-\deE.]+),\s*w:\s*([-\deE.]+)', body)
    return (float(m.group(1)), float(m.group(2)), float(m.group(3)), float(m.group(4))) if m else None
def fileid_of(body, key):
    m = re.search(re.escape(key) + r':\s*\{fileID:\s*(-?\d+)', body)
    return int(m.group(1)) if m else None

gameobjects, transforms, meshfilters, meshrenderers = {}, {}, {}, {}
for cls, fid, body in docs:
    if cls == 1:
        nm = re.search(r'm_Name:\s*(.*)', body)
        comps = re.findall(r'component:\s*\{fileID:\s*(\d+)\}', body)
        act = re.search(r'm_IsActive:\s*(-?\d+)', body)
        gameobjects[fid] = {'name': nm.group(1).strip() if nm else '', 'comps': [int(c) for c in comps],
                            'active': int(act.group(1)) if act else 1}
    elif cls == 23:  # MeshRenderer -> material guids on this GameObject
        go = fileid_of(body, 'm_GameObject')
        ms = re.search(r'm_Materials:\s*((?:\s*-\s*\{fileID:[^\n]*\n)+)', body)
        mats = re.findall(r'guid:\s*([0-9a-f]{32})', ms.group(1)) if ms else []
        if go is not None: meshrenderers[go] = mats
    elif cls == 4:
        transforms[fid] = {'go': fileid_of(body, 'm_GameObject'),
                           'pos': vec3(body, 'm_LocalPosition') or (0,0,0),
                           'rot': quat(body, 'm_LocalRotation') or (0,0,0,1),
                           'scale': vec3(body, 'm_LocalScale') or (1,1,1),
                           'father': fileid_of(body, 'm_Father')}
    elif cls == 33:
        m = re.search(r'm_Mesh:\s*\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-f]+)', body)
        meshfilters[fid] = {'go': fileid_of(body, 'm_GameObject'), 'mesh': (int(m.group(1)), m.group(2)) if m else None}

go_to_tf = {tf['go']: tfid for tfid, tf in transforms.items() if tf['go'] is not None}

def mat_mul(a, b):
    r = [0]*16
    for i in range(4):
        for j in range(4):
            r[i*4+j] = sum(a[i*4+k]*b[k*4+j] for k in range(4))
    return r
def mat_trs(pos, q, s):
    x,y,z,w = q; n = math.sqrt(x*x+y*y+z*z+w*w) or 1.0; x,y,z,w = x/n,y/n,z/n,w/n
    sx,sy,sz = s
    return [ (1-2*(y*y+z*z))*sx, (2*(x*y-w*z))*sy, (2*(x*z+w*y))*sz, pos[0],
             (2*(x*y+w*z))*sx, (1-2*(x*x+z*z))*sy, (2*(y*z-w*x))*sz, pos[1],
             (2*(x*z-w*y))*sx, (2*(y*z+w*x))*sy, (1-2*(x*x+y*y))*sz, pos[2],
             0,0,0,1 ]

world_cache = {}
def world_of(tfid):
    if tfid in world_cache: return world_cache[tfid]
    tf = transforms[tfid]
    local = mat_trs(tf['pos'], tf['rot'], tf['scale'])
    f = tf['father']
    w = mat_mul(world_of(f), local) if (f and f in transforms) else local
    world_cache[tfid] = w
    return w

F = [1,0,0,0, 0,1,0,0, 0,0,-1,0, 0,0,0,1]
# Port convention: meshes stay RAW Unity (ObjMesh CONV=1) and the Z-flip lives in the placement.
# world_godot = F * M_unity  (negate-Z on the whole world matrix; det becomes -1, which ObjMesh's
# unconditional winding-reverse compensates so faces point outward -- identical to every content OBJ).
def to_godot(m): return mat_mul(F, m)

out = []
for mf in meshfilters.values():
    go = mf['go']
    if go is None or mf['mesh'] is None: continue
    tfid = go_to_tf.get(go)
    if tfid is None: continue
    wm = to_godot(world_of(tfid))
    tf = transforms[tfid]
    pfath = tf['father']
    pgo = transforms[pfath]['go'] if (pfath and pfath in transforms) else None
    ggo = None
    if pgo is not None:
        gpf = transforms[go_to_tf.get(pgo,-1)]['father'] if go_to_tf.get(pgo) else None
        ggo = transforms[gpf]['go'] if (gpf and gpf in transforms) else None
    mats = meshrenderers.get(go, [])
    out.append({'name': gameobjects.get(go,{}).get('name','?'),
                'parent': gameobjects.get(pgo,{}).get('name','') if pgo else '',
                'grandparent': gameobjects.get(ggo,{}).get('name','') if ggo else '',
                'active': gameobjects.get(go,{}).get('active',1),
                'guid': mf['mesh'][1], 'meshFileID': mf['mesh'][0],
                'mat': mats[0] if mats else None, 'mats': mats,
                'origin': [round(v,4) for v in (wm[3], wm[7], wm[11])],
                'basis': [round(v,5) for v in (wm[0],wm[1],wm[2], wm[4],wm[5],wm[6], wm[8],wm[9],wm[10])]})

json.dump(out, open('menu_objects.json','w'), indent=0)
lod0 = [o for o in out if o['name'] not in ('Model_1','Model_2','Foliage_1','Foliage_2')]
print("meshed objects:", len(out), "| unique guids:", len(set(o['guid'] for o in out)))
print("LOD0-ish placements:", len(lod0), "| unique guids in LOD0:", len(set(o['guid'] for o in lod0)))
json.dump(lod0, open('menu_lod0.json','w'), indent=0)
# write the unique LOD0 guids for resolution
open('lod0_guids.txt','w').write('\n'.join(sorted(set(o['guid'] for o in lod0))))
print("=== name histogram (all meshed) ===")
for n,c in Counter(o['name'] for o in out).most_common(25): print(f"  {c:4d}  {n}")
