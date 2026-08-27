#!/usr/bin/env python3
"""Extract the 5 retail menu camera anchors (Title/Play/Survivors/Configuration/Workshop) from the
harness scene Menu.unity -> Godot camera pos + look-at, in the same world space as the Menu_Base diorama."""
import re, math

txt = open('Menu_harness.unity', encoding='utf-8', errors='replace').read()
parts = re.split(r'^--- !u!(\d+) &(\d+).*$', txt, flags=re.M)
docs = [(int(parts[i]), int(parts[i+1]), parts[i+2]) for i in range(1, len(parts), 3)]

def vec3(b, k):
    m = re.search(re.escape(k) + r':\s*\{x:\s*([-\deE.]+),\s*y:\s*([-\deE.]+),\s*z:\s*([-\deE.]+)', b)
    return (float(m.group(1)), float(m.group(2)), float(m.group(3))) if m else None
def quat(b, k):
    m = re.search(re.escape(k) + r':\s*\{x:\s*([-\deE.]+),\s*y:\s*([-\deE.]+),\s*z:\s*([-\deE.]+),\s*w:\s*([-\deE.]+)', b)
    return (float(m.group(1)),float(m.group(2)),float(m.group(3)),float(m.group(4))) if m else None
def fid(b, k):
    m = re.search(re.escape(k) + r':\s*\{fileID:\s*(-?\d+)', b); return int(m.group(1)) if m else None

gos, tfs = {}, {}
for cls, f, b in docs:
    if cls == 1:
        nm = re.search(r'm_Name:\s*(.*)', b)
        gos[f] = nm.group(1).strip() if nm else ''
    elif cls == 4:
        tfs[f] = {'go': fid(b,'m_GameObject'), 'pos': vec3(b,'m_LocalPosition') or (0,0,0),
                  'rot': quat(b,'m_LocalRotation') or (0,0,0,1), 'scale': vec3(b,'m_LocalScale') or (1,1,1),
                  'father': fid(b,'m_Father')}
go_tf = {t['go']: f for f, t in tfs.items() if t['go'] is not None}

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
    t = tfs[tf]; loc = trs(t['pos'], t['rot'], t['scale']); f = t['father']
    w = mm(world(f), loc) if (f and f in tfs) else loc
    wc[tf] = w; return w
F = [1,0,0,0, 0,1,0,0, 0,0,-1,0, 0,0,0,1]

for name in ['Title','Play','Survivors','Configuration','Workshop']:
    hits = [f for f, nm in gos.items() if nm == name and f in go_tf]
    for gf in hits:
        g = mm(F, world(go_tf[gf]))   # Godot world = F * M
        origin = (g[3], g[7], g[11])
        xax = (g[0], g[4], g[8]); yax = (g[1], g[5], g[9]); zax = (g[2], g[6], g[10])
        look = (origin[0]+zax[0], origin[1]+zax[1], origin[2]+zax[2])   # Unity cam forward +Z -> +zaxis in Godot (F*M) space
        print(f"{name:14s} pos=({origin[0]:7.2f},{origin[1]:6.2f},{origin[2]:7.2f})  look=({look[0]:7.2f},{look[1]:6.2f},{look[2]:7.2f})  up=({yax[0]:.2f},{yax[1]:.2f},{yax[2]:.2f})")
