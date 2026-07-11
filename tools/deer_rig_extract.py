import UnityPy, os, json, struct, math, sys
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT = r"C:\claude-workspace\unturned-godot\game\content"
NAME = sys.argv[1] if len(sys.argv) > 1 else "deer"

# ---------- rig_extract.py math (verbatim) ----------
def ident(): return [[1.0 if i==j else 0.0 for j in range(4)] for i in range(4)]
def matmul(A,B): return [[sum(A[i][k]*B[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
def mat3_inv(m):
    a,b,c=m[0]; d,e,f=m[1]; g,h,i=m[2]
    A=e*i-f*h; B=-(d*i-f*g); C=d*h-e*g; D=-(b*i-c*h); E=a*i-c*g; F=-(a*h-b*g); G=b*f-c*e; H=-(a*f-c*d); I=a*e-b*d
    det=a*A+b*B+c*C
    if abs(det)<1e-20: raise ValueError('singular')
    inv=1.0/det
    return [[A*inv,D*inv,G*inv],[B*inv,E*inv,H*inv],[C*inv,F*inv,I*inv]]
def mat_inv_affine(M):
    L=[[M[r][c] for c in range(3)] for r in range(3)]; t=[M[0][3],M[1][3],M[2][3]]; Li=mat3_inv(L)
    nt=[-(Li[r][0]*t[0]+Li[r][1]*t[1]+Li[r][2]*t[2]) for r in range(3)]
    return [[Li[0][0],Li[0][1],Li[0][2],nt[0]],[Li[1][0],Li[1][1],Li[1][2],nt[1]],[Li[2][0],Li[2][1],Li[2][2],nt[2]],[0,0,0,1]]
S=[1,1,-1,1]
def zflip(M): return [[M[i][j]*S[i]*S[j] for j in range(4)] for i in range(4)]
def quat_from_R(R):
    tr=R[0][0]+R[1][1]+R[2][2]
    if tr>0:
        s=math.sqrt(tr+1.0)*2; w=0.25*s; x=(R[2][1]-R[1][2])/s; y=(R[0][2]-R[2][0])/s; z=(R[1][0]-R[0][1])/s
    elif R[0][0]>R[1][1] and R[0][0]>R[2][2]:
        s=math.sqrt(1.0+R[0][0]-R[1][1]-R[2][2])*2; w=(R[2][1]-R[1][2])/s; x=0.25*s; y=(R[0][1]+R[1][0])/s; z=(R[0][2]+R[2][0])/s
    elif R[1][1]>R[2][2]:
        s=math.sqrt(1.0+R[1][1]-R[0][0]-R[2][2])*2; w=(R[0][2]-R[2][0])/s; x=(R[0][1]+R[1][0])/s; y=0.25*s; z=(R[1][2]+R[2][1])/s
    else:
        s=math.sqrt(1.0+R[2][2]-R[0][0]-R[1][1])*2; w=(R[1][0]-R[0][1])/s; x=(R[0][2]+R[2][0])/s; y=(R[1][2]+R[2][1])/s; z=0.25*s
    n=math.sqrt(x*x+y*y+z*z+w*w) or 1.0
    return [x/n,y/n,z/n,w/n]
def decompose(M):
    t=[M[0][3],M[1][3],M[2][3]]; col=[[M[0][k],M[1][k],M[2][k]] for k in range(3)]
    scale=[math.sqrt(sum(c*c for c in col[k])) or 1e-9 for k in range(3)]
    R=[[col[k][r]/scale[k] for k in range(3)] for r in range(3)]
    det=(R[0][0]*(R[1][1]*R[2][2]-R[1][2]*R[2][1])-R[0][1]*(R[1][0]*R[2][2]-R[1][2]*R[2][0])+R[0][2]*(R[1][0]*R[2][1]-R[1][1]*R[2][0]))
    if det<0:
        scale[0]=-scale[0]
        for r in range(3): R[r][0]=-R[r][0]
    return t,quat_from_R(R),scale

# ---------- find SMR + mesh ----------
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
def cl(go): return getattr(go,"m_Components",None) or getattr(go,"m_Component",[])
def rd(c):
    cp=c.component if hasattr(c,"component") else c; return cp.read()
def tn(co):
    try: return co.object_reader.type.name
    except: return type(co).__name__
def gtf(go):
    for c in cl(go):
        if tn(rd(c)) in ("Transform","RectTransform"): return rd(c)
    return None
def find_smr(t,depth=0):
    if t is None or depth>9: return None
    go=t.m_GameObject.read()
    for c in cl(go):
        if tn(rd(c))=="SkinnedMeshRenderer": return rd(c)
    for ch in getattr(t,"m_Children",[]):
        r=find_smr(ch.read(),depth+1)
        if r: return r
    return None
obj=next(o for p,o in env.container.items() if p.lower().endswith("animals/%s/animal_client.prefab"%NAME) and o.type.name=="GameObject")
smr=find_smr(gtf(obj.read())); mesh=smr.m_Mesh.read()
NB=len(smr.m_Bones); VC=mesh.m_VertexData.m_VertexCount
print("bones=%d verts=%d bindposes=%d"%(NB,VC,len(mesh.m_BindPose)))

# ---------- bindposes (Unity 4x4 row-major, e_rc) ----------
def mat_of(bp):
    if hasattr(bp,'e00'): return [[getattr(bp,'e%d%d'%(r,c)) for c in range(4)] for r in range(4)]
    d=list(bp); return [[d[r*4+c] for c in range(4)] for r in range(4)]
bindposes=[mat_of(bp) for bp in mesh.m_BindPose]

# ---------- bone hierarchy from m_Bones fathers ----------
bone_pid=[pp.path_id for pp in smr.m_Bones]
father_pid=[]; bone_name=[]
for pp in smr.m_Bones:
    t=pp.read(); fp=getattr(t,'m_Father',None); father_pid.append(getattr(fp,'path_id',0) if fp else 0)
    bone_name.append(t.m_GameObject.read().m_Name)   # REAL bone name (clips reference bones by this)
pid2i={pid:i for i,pid in enumerate(bone_pid)}
parent_orig=[pid2i.get(father_pid[j],-1) for j in range(NB)]
# topological sort (roots first)
order=[]; placed=set()
while len(order)<NB:
    for j in range(NB):
        if j in placed: continue
        if parent_orig[j]==-1 or parent_orig[j] in placed:
            order.append(j); placed.add(j)
remap={orig:new for new,orig in enumerate(order)}

# ---------- skeleton rest from bindposes ----------
gu=[mat_inv_affine(bindposes[j]) for j in range(NB)]  # global rest per orig bone
bones=[]
for new,orig in enumerate(order):
    par=parent_orig[orig]
    loc=gu[orig] if par==-1 else matmul(mat_inv_affine(gu[par]),gu[orig])
    t,q,sc=decompose(zflip(loc))
    bones.append({'name':bone_name[orig],'parent':(remap[par] if par>=0 else -1),'pos':t,'rot':q,'scale':sc})
# skin binds stay in ORIGINAL blend-index order j; skin[j].bone = skeleton index of orig bone j
skin=[]
for j in range(NB):
    t,q,sc=decompose(zflip(bindposes[j]))
    skin.append({'bone':remap[j],'pos':t,'rot':q,'scale':sc})

# ---------- geometry + skin from packed m_VertexData ----------
vd=mesh.m_VertexData; data=bytes(vd.m_DataSize)
FSZ={0:4,1:2,2:1,3:1,4:2,5:2,6:1,7:1,8:2,9:2,10:4,11:4}
def chd(ch): return getattr(ch,'dimension',getattr(ch,'m_Dimension',getattr(ch,'m_RawDimension',0)))&0xF if isinstance(getattr(ch,'dimension',getattr(ch,'m_Dimension',getattr(ch,'m_RawDimension',0))),int) else 0
chans=vd.m_Channels
def cget(ch,a,*alts):
    for nm in (a,)+alts:
        if hasattr(ch,nm): return getattr(ch,nm)
    return 0
strides={}
for ch in chans:
    dim=chd(ch)
    if dim==0: continue
    s=cget(ch,'stream','m_Stream'); off=cget(ch,'offset','m_Offset'); fmt=cget(ch,'format','m_Format')
    strides[s]=max(strides.get(s,0),off+dim*FSZ[fmt])
def align(x,a=16): return (x+a-1)//a*a
starts={}; cur=0
for s in sorted(strides):
    starts[s]=cur; cur=align(cur+strides[s]*VC)
def choff(idx): c=chans[idx]; return cget(c,'stream','m_Stream'),cget(c,'offset','m_Offset')
s0,o0=choff(0); s1n,o1=choff(1); s4s,o4=choff(4); s12s,o12=choff(12); s13s,o13=choff(13)
positions=[]; normals=[]; uvs=[]; sk_idx=[]; sk_w=[]
for v in range(VC):
    b0=starts[s0]+v*strides[s0]; px,py,pz=struct.unpack_from('<3f',data,b0+o0); nx,ny,nz=struct.unpack_from('<3f',data,b0+o1)
    b1=starts[s4s]+v*strides[s4s]; u,uv=struct.unpack_from('<2f',data,b1+o4)
    b2=starts[s12s]+v*strides[s12s]; w0,w1=struct.unpack_from('<2f',data,b2+o12); i0,i1=struct.unpack_from('<2I',data,b2+o13)
    positions.append([px,py,-pz]); normals.append([nx,ny,-nz]); uvs.append([u,1.0-uv])
    sk_idx.append([i0,i1]); sk_w.append([w0,w1])
# validate
wsum=sum(a+b for a,b in sk_w)/VC; maxi=max(max(a,b) for a,b in sk_idx)
print("validate: avg weight-sum=%.3f (want ~1.0), max blend index=%d (want <=%d)"%(wsum,maxi,NB-1))

# ---------- triangles (uint16) + reverse winding ----------
ib=bytes(mesh.m_IndexBuffer); tris=list(struct.unpack('<%dH'%(len(ib)//2),ib))
faces=[]
for k in range(0,len(tris),3):
    a,b,c=tris[k],tris[k+1],tris[k+2]; faces.extend([a,c,b])

rig={'vcount':VC,'positions':positions,'normals':normals,'uvs':uvs,'skin_index':sk_idx,'skin_weight':sk_w,
     'faces':faces,'bones':bones,'skin':skin,'anims':{},'ragdoll':{},'arms':None}
open(os.path.join(OUT,'%s_rig.json'%NAME),'w').write(json.dumps(rig))
print("wrote %s_rig.json: %d verts, %d bones, %d skin binds, %d tris"%(NAME,VC,len(bones),len(skin),len(faces)//3))
print("bone tree:",[(b['name'],b['parent']) for b in bones])
