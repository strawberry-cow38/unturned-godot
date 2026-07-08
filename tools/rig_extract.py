#!/usr/bin/env python3
# Unturned character rig extractor: Model_0_84 mesh + resources.assets legacy clips
# -> rig.json for a Godot Skeleton3D + hand-built skinned mesh + AnimationPlayer.
# No shortcuts: real skeleton, real bind poses, real per-vertex skin, real clip keyframes.
import struct, zlib, json, math, re, sys, glob, os

MESH = os.environ.get('RIG_MESH', '/tmp/Model_0_84.asset')
CLIP_DIR = os.environ.get('RIG_CLIPDIR', '/tmp/clips')
OUT = os.environ.get('RIG_OUT', '/tmp/rig.json')

# bind-pose index -> bone path (recovered from the garbled hash blob, CRC32-validated, unique)
BONE_ORDER = [
 'Skeleton/Spine','Skeleton/Spine/Left_Shoulder','Skeleton/Spine/Left_Shoulder/Left_Arm',
 'Skeleton/Spine/Left_Shoulder/Left_Arm/Left_Hand','Skeleton/Spine/Left_Shoulder/Left_Arm/Left_Hand/Left_Hook',
 'Skeleton/Spine/Right_Shoulder','Skeleton/Spine/Right_Shoulder/Right_Arm',
 'Skeleton/Spine/Right_Shoulder/Right_Arm/Right_Hand','Skeleton/Spine/Right_Shoulder/Right_Arm/Right_Hand/Right_Hook',
 'Skeleton/Spine/Skull','Skeleton/Left_Hip','Skeleton/Left_Hip/Left_Leg','Skeleton/Left_Hip/Left_Leg/Left_Foot',
 'Skeleton/Right_Hip','Skeleton/Right_Hip/Right_Leg','Skeleton/Right_Hip/Right_Leg/Right_Foot']

# ---------- tiny 4x4 (row-major) linear algebra, no numpy ----------
def ident(): return [[1.0 if i==j else 0.0 for j in range(4)] for i in range(4)]
def matmul(A,B):
    return [[sum(A[i][k]*B[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
def mat3_inv(m):  # m: 3x3 list
    a,b,c=m[0]; d,e,f=m[1]; g,h,i=m[2]
    A= e*i-f*h; B=-(d*i-f*g); C= d*h-e*g
    D=-(b*i-c*h); E= a*i-c*g; F=-(a*h-b*g)
    G= b*f-c*e; H=-(a*f-c*d); I= a*e-b*d
    det=a*A+b*B+c*C
    if abs(det)<1e-20: raise ValueError('singular')
    inv=1.0/det
    return [[A*inv,D*inv,G*inv],[B*inv,E*inv,H*inv],[C*inv,F*inv,I*inv]]
def mat_inv_affine(M):
    L=[[M[r][c] for c in range(3)] for r in range(3)]
    t=[M[0][3],M[1][3],M[2][3]]
    Li=mat3_inv(L)
    nt=[-(Li[r][0]*t[0]+Li[r][1]*t[1]+Li[r][2]*t[2]) for r in range(3)]
    return [[Li[0][0],Li[0][1],Li[0][2],nt[0]],
            [Li[1][0],Li[1][1],Li[1][2],nt[1]],
            [Li[2][0],Li[2][1],Li[2][2],nt[2]],
            [0,0,0,1]]
S=[1,1,-1,1]  # Unity->Godot z-flip
def zflip(M):  # S M S
    return [[M[i][j]*S[i]*S[j] for j in range(4)] for i in range(4)]
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return [aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz]
def qinv(q):  # unit-quat inverse = conjugate
    x,y,z,w=q; return [-x,-y,-z,w]
def qrot(q,v):  # rotate vec3 by quat
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return [vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx)]
def decompose(M):
    t=[M[0][3],M[1][3],M[2][3]]
    col=[[M[0][k],M[1][k],M[2][k]] for k in range(3)]
    scale=[math.sqrt(sum(c*c for c in col[k])) or 1e-9 for k in range(3)]
    R=[[col[k][r]/scale[k] for k in range(3)] for r in range(3)]  # R[r][c]=axis c comp r
    # det check
    det=(R[0][0]*(R[1][1]*R[2][2]-R[1][2]*R[2][1])
        -R[0][1]*(R[1][0]*R[2][2]-R[1][2]*R[2][0])
        +R[0][2]*(R[1][0]*R[2][1]-R[1][1]*R[2][0]))
    if det<0:
        scale[0]=-scale[0]
        for r in range(3): R[r][0]=-R[r][0]
    q=quat_from_R(R)
    return t,q,scale
def quat_from_R(R):
    tr=R[0][0]+R[1][1]+R[2][2]
    if tr>0:
        s=math.sqrt(tr+1.0)*2; w=0.25*s
        x=(R[2][1]-R[1][2])/s; y=(R[0][2]-R[2][0])/s; z=(R[1][0]-R[0][1])/s
    elif R[0][0]>R[1][1] and R[0][0]>R[2][2]:
        s=math.sqrt(1.0+R[0][0]-R[1][1]-R[2][2])*2
        w=(R[2][1]-R[1][2])/s; x=0.25*s; y=(R[0][1]+R[1][0])/s; z=(R[0][2]+R[2][0])/s
    elif R[1][1]>R[2][2]:
        s=math.sqrt(1.0+R[1][1]-R[0][0]-R[2][2])*2
        w=(R[0][2]-R[2][0])/s; x=(R[0][1]+R[1][0])/s; y=0.25*s; z=(R[1][2]+R[2][1])/s
    else:
        s=math.sqrt(1.0+R[2][2]-R[0][0]-R[1][1])*2
        w=(R[1][0]-R[0][1])/s; x=(R[0][2]+R[2][0])/s; y=(R[1][2]+R[2][1])/s; z=0.25*s
    n=math.sqrt(x*x+y*y+z*z+w*w) or 1.0
    return [x/n,y/n,z/n,w/n]

# ---------- parse mesh ----------
mt=open(MESH).read()
def parse_bindposes(t):
    i=t.find('m_BindPose:'); j=t.find('m_BoneNameHashes',i)
    seg=t[i:j]
    mats=[]; cur={}
    for m in re.finditer(r'e(\d)(\d):\s*(-?[\d.eE+-]+)', seg):
        r,c,v=int(m.group(1)),int(m.group(2)),float(m.group(3))
        cur[(r,c)]=v
        if (r,c)==(3,3):
            mats.append([[cur[(rr,cc)] for cc in range(4)] for rr in range(4)]); cur={}
    return mats
bindposes=parse_bindposes(mt)  # Unity 4x4, len 16
assert len(bindposes)==16, len(bindposes)

# vertex typeless data
i=mt.find('_typelessdata:'); j=mt.find('\n', i)
hexdata=mt[i+len('_typelessdata:'):j].strip()
data=bytes.fromhex(hexdata)
VCOUNT=464
S0=40; S1=12; S2=16
b0=0; b1=VCOUNT*S0; b2=VCOUNT*S0+VCOUNT*S1
assert len(data)==VCOUNT*(S0+S1+S2), (len(data),VCOUNT*(S0+S1+S2))
positions=[]; normals=[]; uvs=[]; sk_idx=[]; sk_w=[]
for v in range(VCOUNT):
    o=b0+v*S0
    px,py,pz=struct.unpack_from('<3f',data,o)
    nx,ny,nz=struct.unpack_from('<3f',data,o+12)
    o1=b1+v*S1
    u,uv=struct.unpack_from('<2f',data,o1+4)
    o2=b2+v*S2
    w0,w1=struct.unpack_from('<2f',data,o2)
    i0,i1=struct.unpack_from('<2I',data,o2+8)
    # Unity->Godot: flip z on pos+normal
    positions.append([px,py,-pz]); normals.append([nx,ny,-nz]); uvs.append([u,1.0-uv])
    sk_idx.append([i0,i1]); sk_w.append([w0,w1])

# index buffer (uint16 LE)
i=mt.find('m_IndexBuffer:'); j=mt.find('\n', i)
ibhex=mt[i+len('m_IndexBuffer:'):j].strip()
ib=bytes.fromhex(ibhex)
tris=list(struct.unpack('<%dH'%(len(ib)//2), ib))
# reverse winding for Godot (z-flip mirrors handedness)
faces=[]
for k in range(0,len(tris),3):
    a,b,c=tris[k],tris[k+1],tris[k+2]
    faces.extend([a,c,b])

# ---------- skeleton (topological: parents before children) ----------
NODES=['Skeleton','Skeleton/Spine','Skeleton/Spine/Skull',
 'Skeleton/Spine/Left_Shoulder','Skeleton/Spine/Left_Shoulder/Left_Arm','Skeleton/Spine/Left_Shoulder/Left_Arm/Left_Hand','Skeleton/Spine/Left_Shoulder/Left_Arm/Left_Hand/Left_Hook',
 'Skeleton/Spine/Right_Shoulder','Skeleton/Spine/Right_Shoulder/Right_Arm','Skeleton/Spine/Right_Shoulder/Right_Arm/Right_Hand','Skeleton/Spine/Right_Shoulder/Right_Arm/Right_Hand/Right_Hook',
 'Skeleton/Left_Hip','Skeleton/Left_Hip/Left_Leg','Skeleton/Left_Hip/Left_Leg/Left_Foot',
 'Skeleton/Right_Hip','Skeleton/Right_Hip/Right_Leg','Skeleton/Right_Hip/Right_Leg/Right_Foot']
name_of={p:p.split('/')[-1] for p in NODES}
idx_of={p:k for k,p in enumerate(NODES)}
parent_of={p:('' if '/' not in p else p.rsplit('/',1)[0]) for p in NODES}

# Unity global rest per node: bone = inverse(bindpose); Skeleton root = identity
gu={'Skeleton':ident()}
for k,p in enumerate(BONE_ORDER):
    gu[p]=mat_inv_affine(bindposes[k])
# local rest (Unity) then zflip -> decompose
bones=[]
for p in NODES:
    par=parent_of[p]
    if par=='':
        loc=ident()
    else:
        loc=matmul(mat_inv_affine(gu[par]), gu[p])
    locg=zflip(loc)
    t,q,sc=decompose(locg)
    bones.append({'name':name_of[p],'parent':(idx_of[par] if par else -1),
                  'pos':t,'rot':q,'scale':sc})

# skin binds: bind j (mesh blend index j) -> skeleton bone + Godot bind pose
skin=[]
for k,p in enumerate(BONE_ORDER):
    bg=zflip(bindposes[k])  # mesh->bone, Godot space
    t,q,sc=decompose(bg)
    skin.append({'bone':idx_of[p],'pos':t,'rot':q,'scale':sc})

# ---------- parse legacy clips ----------
FLOAT=r'-?[\d.]+(?:[eE][+-]?\d+)?'
def parse_clip(path):
    lines=open(path).read().splitlines()
    fps=30.0
    tracks={}  # bonename -> {'rot':[],'pos':[],'scale':[]}
    section=None  # 'rot'/'pos'/'scale'
    keys=[]      # current curve's keyframes: list of (time, value-dict)
    curtime=None; curval=None
    def flush(bonepath):
        nonlocal keys
        if bonepath is None or not keys: keys=[]; return
        bn=bonepath.split('/')[-1]
        d=tracks.setdefault(bn,{'rot':[],'pos':[],'scale':[]})
        d[section]=keys
        keys=[]
    for ln in lines:
        s=ln.strip()
        if s=='m_RotationCurves:': section='rot'; continue
        if s=='m_PositionCurves:': section='pos'; continue
        if s=='m_ScaleCurves:': section='scale'; continue
        if s in ('m_EulerCurves:','m_FloatCurves:','m_PPtrCurves:','m_ClipBindingConstant:'):
            section=None; continue
        m=re.match(r'm_SampleRate:\s*('+FLOAT+')',s)
        if m: fps=float(m.group(1)); continue
        if section is None: continue
        mt_=re.match(r'-?\s*time:\s*('+FLOAT+')$',s)
        if s.startswith('- serializedVersion:'):  # new keyframe start
            curtime=None; curval=None; continue
        m=re.match(r'time:\s*('+FLOAT+')',s)
        if m: curtime=float(m.group(1)); continue
        m=re.match(r'value:\s*\{(.*)\}',s)
        if m and curtime is not None:
            vals=dict(re.findall(r'([xyzw]):\s*('+FLOAT+')', m.group(1)))
            curval={k:float(v) for k,v in vals.items()}
            keys.append((curtime,curval))
            continue
        m=re.match(r'path:\s*(\S*)',s)
        if m is not None and s.startswith('path:'):
            flush(m.group(1)); continue
    # convert keyframes Unity->Godot
    out={}
    for bn,d in tracks.items():
        o={}
        if d['rot']:
            o['rot']=[[t,-v['x'],-v['y'],v['z'],v['w']] for t,v in d['rot']]
        if d['pos']:
            o['pos']=[[t,v['x'],v['y'],-v['z']] for t,v in d['pos']]
        if d['scale']:
            o['scale']=[[t,v['x'],v['y'],v['z']] for t,v in d['scale']]
        out[bn]=o
    # ---- root-convention fix ----
    # Model_0_84's bind poses are baked with Skeleton=identity (rest render stands upright),
    # but the clips are authored with the Skeleton root tipped -90deg X. Neutralise the
    # Skeleton track's rotation (pre-multiply by inverse(frame0)) so the whole rig un-tips
    # into the mesh's bind convention; children inherit the correction and keep their motion.
    sk=out.get('Skeleton')
    if sk and sk.get('rot'):
        K=qinv(sk['rot'][0][1:5])
        sk['rot']=[[k[0]]+qmul(K,k[1:5]) for k in sk['rot']]
        if sk.get('pos'):
            sk['pos']=[[k[0]]+qrot(K,k[1:4]) for k in sk['pos']]
    length=0.0
    for d in out.values():
        for arr in d.values():
            if arr: length=max(length,arr[-1][0])
    return {'fps':fps,'length':length,'tracks':out}
anims={}
for p in sorted(glob.glob(os.path.join(CLIP_DIR, '*.yaml'))):
    name = os.path.splitext(os.path.basename(p))[0]
    c = parse_clip(p)
    sp = c['tracks'].get('Spine', {})
    if not sp.get('rot'):          # human biped clips animate Spine; skip animal/empty
        print('  skip non-human clip', name); continue
    c['loop'] = not (name.startswith('Attack_') or name.startswith('Startle_') or name == 'Jump')
    anims[name] = c

# ---- ragdoll spec (from Unturned's Ragdoll_Player prefab: box colliders + CharacterJoint limits + masses) ----
RAGDOLL_JSON = os.environ.get('RIG_RAGDOLL', '')
rag = {}
if RAGDOLL_JSON and os.path.exists(RAGDOLL_JSON):
    rag = json.load(open(RAGDOLL_JSON))
    for b in rag.values():
        bx = b.get('box')
        if bx and bx.get('center'):
            c = bx['center']; bx['center'] = [c[0], c[1], -c[2]]  # Unity->Godot z-flip
    print('  ragdoll bones:', len(rag))

rig={'vcount':VCOUNT,'positions':positions,'normals':normals,'uvs':uvs,
     'skin_index':sk_idx,'skin_weight':sk_w,'faces':faces,
     'bones':bones,'skin':skin,'anims':anims,'ragdoll':rag}
json.dump(rig,open(OUT,'w'))
# report
print('VERTS',VCOUNT,'FACES',len(faces)//3,'BONES',len(bones),'SKINBINDS',len(skin))
for nm,a in anims.items():
    nt=len(a['tracks']); nr=sum(len(t.get('rot',[])) for t in a['tracks'].values())
    print(f"  clip {nm:6} len={a['length']:.3f}s fps={a['fps']:.0f} tracks={nt} rotkeys={nr}")
import os
print('rig.json bytes', os.path.getsize(OUT))
# sanity: bounds
xs=[p[0] for p in positions]; ys=[p[1] for p in positions]; zs=[p[2] for p in positions]
print('bounds x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]'%(min(xs),max(xs),min(ys),max(ys),min(zs),max(zs)))
print('weight sanity (first 3):', sk_w[:3], 'idx', sk_idx[:3])
