import UnityPy
# Rip the blowtorch's REAL spark ParticleSystem from core.masterbundle -> content/torch_spark.png.
# Source (UseableMelee.cs): a Repeated tool's sparks come from a child GameObject named "Hit" on the weapon's
# first-person model, with a ParticleSystem; while the trigger is held it calls firstEmitter.Emit(4) per tick.
# The "Hit" node local pos in items/melee/blowtorch/item.prefab = (-0.1359, 0.4719, 0) -> port frame (-x,y,-z) =
# (0.1359, 0.4719, 0) (the nozzle tip). Its material's _MainTex = the blue spark sprite (startColor is white,
# so the colour is IN the sprite). ParticleSystem params: startSize 0.05-0.10, startSpeed 1-2, Sphere shape r=0.25,
# gravity x1, lifetime 1s (all world-scale -> scaled down in Viewmodel.SetTorchSparks for the close viewmodel view).
MB=r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
OUT=r"C:\claude-workspace\unturned-godot\game\content"
env=UnityPy.load(MB); cont=dict(env.container)
def rd(p):
    try: return p.read()
    except: return None
def tn(c):
    try: return c.object_reader.type.name
    except: return '?'
def comps(go):
    out=[]
    for c in (getattr(go,'m_Component',None) or []):
        p=getattr(c,'component',None) or (c if hasattr(c,'read') else None)
        cc=rd(p) if p else None
        if cc is not None: out.append(cc)
    return out
def get_tr(go):
    for c in comps(go):
        if tn(c) in ('Transform','RectTransform'): return c
    return None
root=None
for path,obj in cont.items():
    if "items/melee/blowtorch/item.prefab" in str(path).lower(): root=obj.read(); break
def find_hit(tr):
    go=rd(tr.m_GameObject)
    if go is not None and getattr(go,'m_Name','')=="Hit": return go,tr
    for ch in (getattr(tr,'m_Children',None) or []):
        ct=rd(ch)
        if ct is not None:
            r=find_hit(ct)
            if r: return r
    return None
hit,hittr=find_hit(get_tr(root))
lp=hittr.m_LocalPosition
print(f"Hit local pos = ({lp.X if hasattr(lp,'X') else lp.x}, {lp.Y if hasattr(lp,'Y') else lp.y}, {lp.Z if hasattr(lp,'Z') else lp.z})")
for c in comps(hit):
    if tn(c)=="ParticleSystemRenderer":
        for m in (getattr(c,'m_Materials',None) or []):
            mat=rd(m)
            if not mat: continue
            te=getattr(getattr(mat,'m_SavedProperties',None),'m_TexEnvs',None) or []
            for t in te:
                k=t[0] if isinstance(t,(list,tuple)) else t.get('first')
                e=t[1] if isinstance(t,(list,tuple)) else t.get('second')
                tp=getattr(e,'m_Texture',None) if e is not None else None
                tex=rd(tp) if tp else None
                if tex is not None and getattr(tex,'m_Name','') and k=='_MainTex':
                    tex.image.save(OUT+"\\torch_spark.png"); print("saved torch_spark.png <-",tex.m_Name,tex.image.size)
