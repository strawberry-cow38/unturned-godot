#!/usr/bin/env python3
"""Extract the 6 real menu lamps (Light docs) from Menu_Base.unity -> Godot lights in the diorama's world space."""
import re, math, json

txt = open('Menu_Base.unity', encoding='utf-8', errors='replace').read()
parts = re.split(r'^--- !u!(\d+) &(\d+).*$', txt, flags=re.M)
docs = [(int(parts[i]), int(parts[i+1]), parts[i+2]) for i in range(1, len(parts), 3)]

def vec3(b, k):
    m = re.search(re.escape(k) + r':\s*\{x:\s*([-\deE.]+),\s*y:\s*([-\deE.]+),\s*z:\s*([-\deE.]+)', b)
    return (float(m.group(1)), float(m.group(2)), float(m.group(3))) if m else None
def quat(b, k):
    m = re.search(re.escape(k) + r':\s*\{x:\s*([-\deE.]+),\s*y:\s*([-\deE.]+),\s*z:\s*([-\deE.]+),\s*w:\s*([-\deE.]+)', b)
    return (float(m.group(1)),float(m.group(2)),float(m.group(3)),float(m.group(4))) if m else None
def color(b, k):
    m = re.search(re.escape(k) + r':\s*\{r:\s*([-\deE.]+),\s*g:\s*([-\deE.]+),\s*b:\s*([-\deE.]+)', b)
    return (float(m.group(1)), float(m.group(2)), float(m.group(3))) if m else (1,1,1)
def fnum(b, k, d=0.0):
    m = re.search(re.escape(k) + r':\s*([-\deE.]+)', b); return float(m.group(1)) if m else d
def fid(b, k):
    m = re.search(re.escape(k) + r':\s*\{fileID:\s*(-?\d+)', b); return int(m.group(1)) if m else None

tfs = {}; go_tf = {}; lights = []
for cls, f, b in docs:
    if cls == 4:
        tfs[f] = {'go': fid(b,'m_GameObject'), 'pos': vec3(b,'m_LocalPosition') or (0,0,0),
                  'rot': quat(b,'m_LocalRotation') or (0,0,0,1), 'scale': vec3(b,'m_LocalScale') or (1,1,1),
                  'father': fid(b,'m_Father')}
    elif cls == 108:
        lights.append({'go': fid(b,'m_GameObject'), 'type': int(fnum(b,'m_Type')),
                       'color': color(b,'m_Color'), 'intensity': fnum(b,'m_Intensity',1),
                       'range': fnum(b,'m_Range',10), 'spot': fnum(b,'m_SpotAngle',45)})
for f, t in tfs.items():
    if t['go'] is not None: go_tf[t['go']] = f

def mm(a, b):
    r=[0]*16
    for i in range(4):
        for j in range(4):
            r[i*4+j]=sum(a[i*4+k]*b[k*4+j] for k in range(4))
    return r
def trs(p, q, s):
    x,y,z,w=q; n=math.sqrt(x*x+y*y+z*z+w*w) or 1; x,y,z,w=x/n,y/n,z/n,w/n; sx,sy,sz=s
    return [(1-2*(y*y+z*z))*sx,(2*(x*y-w*z))*sy,(2*(x*z+w*y))*sz,p[0],
            (2*(x*y+w*z))*sx,(1-2*(x*x+z*z))*sy,(2*(y*z-w*x))*sz,p[1],
            (2*(x*z-w*y))*sx,(2*(y*z+w*x))*sy,(1-2*(x*x+y*y))*sz,p[2], 0,0,0,1]
wc = {}
def world(tf):
    if tf in wc: return wc[tf]
    t = tfs[tf]; loc = trs(t['pos'], t['rot'], t['scale']); ff = t['father']
    w = mm(world(ff), loc) if (ff and ff in tfs) else loc
    wc[tf] = w; return w
F = [1,0,0,0, 0,1,0,0, 0,0,-1,0, 0,0,0,1]

out = []
for L in lights:
    tf = go_tf.get(L['go'])
    if tf is None: continue
    g = mm(F, world(tf))
    origin = (g[3], g[7], g[11])
    zax = (g[2], g[6], g[10])
    fwd = (zax[0], zax[1], zax[2])   # spot forward = +zaxis (Unity light aims +Z)
    out.append({'type': L['type'], 'pos': [round(v,3) for v in origin], 'fwd': [round(v,3) for v in fwd],
                'color': [round(c,4) for c in L['color']], 'intensity': round(L['intensity'],4),
                'range': round(L['range'],3), 'spot': round(L['spot'],2)})
json.dump(out, open('/mnt/bigdisk/rubble-warmup/game/content/menu/menu_lamps.json','w'), indent=0)
print('lamps:', len(out))
for L in out: print(f"  type={L['type']} pos={L['pos']} range={L['range']} intensity={L['intensity']} color={L['color']}")
