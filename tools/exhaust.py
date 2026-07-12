import UnityPy, os
env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
by_id = {o.path_id: o for o in env.objects}
OUT = r"C:\claude-workspace\unturned-godot\game\content"
def comps(tt):
    out=[]
    for c in tt.get("m_Component", []):
        cc=c.get("component",c) if isinstance(c,dict) else c
        co=by_id.get(cc.get("m_PathID") if isinstance(cc,dict) else None)
        if co: out.append(co)
    return out
def trs_local(tt):
    for c in comps(tt):
        if c.type.name=="Transform":
            t=c.read_typetree(); p=t.get("m_LocalPosition",{})
            return (p.get("x",0),p.get("y",0),p.get("z",0))
    return None
def main_tex(mat_obj):
    try:
        for e in mat_obj.read_typetree().get("m_SavedProperties",{}).get("m_TexEnvs",[]):
            n,te=(e if isinstance(e,(list,tuple)) and len(e)==2 else (e.get("first"),e.get("second")))
            if n=="_MainTex" and isinstance(te,dict): return te.get("m_Texture",{}).get("m_PathID")
    except: pass
    return None
done=set()
for p,o in env.container.items():
    if "/vehicles/" in p and p.endswith("vehicle.prefab") and o.type.name=="GameObject":
        veh=p.split("/vehicles/")[-1].split("/")[0]
        def walk(pid, worldy=0.0, path=""):
            go=by_id.get(pid)
            if not go: return
            tt=go.read_typetree(); nm=tt.get("m_Name","")
            if "exhaust" in nm.lower():
                lp=trs_local(tt)
                hasps=any(c.type.name in ("ParticleSystem","ParticleSystemRenderer") for c in comps(tt))
                # tex
                tex=None
                for c in comps(tt):
                    if c.type.name=="ParticleSystemRenderer":
                        mats=c.read_typetree().get("m_Materials",[])
                        if mats:
                            tp=main_tex(by_id.get(mats[0].get("m_PathID")))
                            if tp and tp in by_id:
                                try:
                                    img=by_id[tp].read().image
                                    if img and "exhaust" not in done: img.save(os.path.join(OUT,"veh_exhaust.png")); done.add("exhaust"); tex=(by_id[tp].read().m_Name,img.size)
                                except: pass
                print(f"{veh}: node '{nm}' localpos={lp} hasPS={hasps} tex={tex}")
            tr=[c for c in comps(tt) if c.type.name=="Transform"]
            if tr:
                for ch in tr[0].read_typetree().get("m_Children",[]):
                    cc=by_id.get(ch.get("m_PathID"))
                    if cc: walk(cc.read_typetree().get("m_GameObject",{}).get("m_PathID"))
        walk(o.path_id)
    if "exhaust" in done and False: break
print("saved veh_exhaust.png:", "exhaust" in done)
