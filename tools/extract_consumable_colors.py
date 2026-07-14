#!/usr/bin/env python3
"""For every food/water/medical consumable whose held-mesh material has NO _MainTex, capture its flat _Color
(the item's real look -- cheese=yellow, potato=brown). -> content/consumable_colors.tsv (mesh<TAB>r g b).
Textured items are skipped (their texture is the look). Fixes the gray no-texture consumables (master)."""
import UnityPy, os, re
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
OUT = r"C:\claude-workspace\unturned-godot\game\content\consumable_colors.tsv"
env = UnityPy.load(MB); by_id={o.path_id:o for o in env.objects}
def comp_of(tt,names):
    for e in tt.get("m_Component",[]):
        c=e.get("component",e) if isinstance(e,dict) else e
        co=by_id.get(c.get("m_PathID") if isinstance(c,dict) else None)
        if co and co.type.name in names: return co
    return None
def pptr(v): return by_id.get(v.get("m_PathID")) if isinstance(v,dict) else None
rows=[]
seen=set()
for path,o in env.container.items():
    m=re.search(r"items/(food|medical|water|refills)/([^/]+)/item\.prefab$",str(path).lower())
    if not (m and o.type.name=="GameObject" and m.group(2) not in seen): continue
    nm=m.group(2); seen.add(nm)
    tr=comp_of(o.read_typetree(),("Transform",)); m0=None
    for ch in (tr.read_typetree().get("m_Children",[]) if tr else []):
        ct=by_id.get(ch.get("m_PathID"))
        if not ct: continue
        cgo=by_id.get(ct.read_typetree().get("m_GameObject",{}).get("m_PathID"))
        if cgo and cgo.read_typetree().get("m_Name")=="Model_0": m0=cgo
    src=m0 or o
    mr=comp_of(src.read_typetree(),("MeshRenderer",))
    if not mr: continue
    mats=mr.read_typetree().get("m_Materials",[]); mo=pptr(mats[0]) if mats else None
    if not mo: continue
    props=mo.read_typetree().get("m_SavedProperties",{})
    hasmain=False
    for p in props.get("m_TexEnvs",[]):
        fn=p[0] if isinstance(p,(list,tuple)) else p.get('first'); val=p[1] if isinstance(p,(list,tuple)) else p.get('second')
        if fn=="_MainTex" and isinstance(val,dict) and pptr(val.get("m_Texture",{})) is not None: hasmain=True
    if hasmain: continue   # textured -> the texture is the look
    col=None
    for p in props.get("m_Colors",[]):
        fn=p[0] if isinstance(p,(list,tuple)) else p.get('first'); val=p[1] if isinstance(p,(list,tuple)) else p.get('second')
        if fn=="_Color" and isinstance(val,dict): col=(val.get('r',1),val.get('g',1),val.get('b',1))
    if col and (col[0]+col[1]+col[2])>0.01: rows.append((nm,)+tuple(round(c,4) for c in col))
rows.sort()
with open(OUT,"w") as f:
    for r in rows: f.write("\t".join(str(x) for x in r)+"\n")
print(f"wrote {len(rows)} no-texture consumable colors")
for want in ("cheese","potato","canned_beans"):
    print(f"  {want}: {next((r for r in rows if r[0]==want), 'HAS TEXTURE / no color')}")
