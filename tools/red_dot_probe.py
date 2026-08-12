"""Dump the red-dot / halo / kobra SIGHT prefab structure: nodes, meshes, submeshes, materials
(name/shader/_Color/transparency/textures) + local transforms -- to separate housing / glass lens / reticle
and get the real mount, so we can render clear glass + a glowing red dot instead of a solid black disc."""
import UnityPy, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
try: sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception: pass
BUND = ug_paths.bundles()
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
cont = env.container
TARGETS = [t.lower() for t in (sys.argv[1:] or ["dot", "halo", "kobra"])]

prefabs = []
for path, obj in cont.items():
    pl = path.lower()
    if "/sights/" in pl and pl.endswith("sight.prefab") and obj.type.name == "GameObject" and any(t in pl for t in TARGETS):
        prefabs.append((path, obj))
print("sight prefabs:", [p for p, _ in prefabs])

def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co: yield co

def mat_info(mat):
    mt = mat.read_typetree(); mname = mt.get("m_Name", "?")
    sh = by_id.get(mt.get("m_Shader", {}).get("m_PathID")); shader = "?"
    if sh:
        try:
            st = sh.read_typetree(); shader = st.get("m_ParsedForm", {}).get("m_Name") or st.get("m_Name", "?")
        except Exception: pass
    cols = []
    for e in mt.get("m_SavedProperties", {}).get("m_Colors", []):
        if isinstance(e, (list, tuple)) and len(e) == 2: cn, cv = e
        elif isinstance(e, dict): cn, cv = e.get("first", e.get("Key")), e.get("second", e.get("Value"))
        else: continue
        if isinstance(cv, dict) and "r" in cv: cols.append(f"{cn}=({cv['r']:.2f},{cv['g']:.2f},{cv['b']:.2f},a{cv['a']:.2f})")
    texs = []
    for e in mt.get("m_SavedProperties", {}).get("m_TexEnvs", []):
        if isinstance(e, (list, tuple)) and len(e) == 2: tn, tv = e
        elif isinstance(e, dict): tn, tv = e.get("first", e.get("Key")), e.get("second", e.get("Value"))
        else: continue
        if not isinstance(tv, dict): continue
        tx = by_id.get((tv.get("m_Texture") or {}).get("m_PathID"))
        if tx and tx.type.name == "Texture2D":
            try: im = tx.read().image; texs.append(f"{tn}={im.width}x{im.height}")
            except Exception: texs.append(f"{tn}=?")
    # render queue hints transparency
    rq = mt.get("m_CustomRenderQueue", -1)
    return mname, shader, cols, texs, rq

def walk(pid, depth=0):
    go = by_id.get(pid)
    if not go or depth > 12: return
    tt = go.read_typetree(); nm = tt.get("m_Name", "")
    mr = mf = tr = None
    for co in comps(tt):
        if co.type.name in ("MeshRenderer", "SkinnedMeshRenderer"): mr = co
        if co.type.name == "MeshFilter": mf = co
        if co.type.name == "Transform": tr = co
    lp = None
    if tr:
        lpd = tr.read_typetree().get("m_LocalPosition", {})
        lp = (round(lpd.get("x", 0), 4), round(lpd.get("y", 0), 4), round(lpd.get("z", 0), 4))
    if mr:
        mats = mr.read_typetree().get("m_Materials", [])
        subn = mstr = None
        meshpid = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
        if meshpid and meshpid in by_id:
            try:
                mt = by_id[meshpid].read_typetree(); subn = len(mt.get("m_SubMeshes", [])); mstr = mt.get("m_Name", "?")
            except Exception: pass
        print(f"  NODE '{nm}' localPos={lp} mesh='{mstr}' submeshes={subn} mats={len(mats)}")
        for i, mref in enumerate(mats):
            mat = by_id.get(mref.get("m_PathID"))
            if not mat: print(f"    submesh[{i}] <missing mat>"); continue
            mn, shd, cols, texs, rq = mat_info(mat)
            print(f"    submesh[{i}] mat='{mn}' shader={shd} rq={rq}")
            if cols: print(f"        colors: {cols}")
            if texs: print(f"        textures: {texs}")
    elif nm:
        print(f"  node '{nm}' localPos={lp} (no renderer -- hook/empty)")
    if tr:
        for ch in tr.read_typetree().get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), depth + 1)

for path, prefab in prefabs:
    print("\n===", path, "===")
    walk(prefab.path_id)
