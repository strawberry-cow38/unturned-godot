import UnityPy, os, re, glob
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
SIGHTS = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items\Sights"
content = r"C:\claude-workspace\unturned-godot\game\content"
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont = env.container
def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names: return co
    return None
def sight_color(folder):
    p = next((o for pa, o in cont.items() if f"/sights/{folder}/sight.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not p: return (0.3, 0.3, 0.3)
    tr = comp_of(p.read_typetree(), ("Transform",))
    for ch in tr.read_typetree().get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct: continue
        ctt = ct.read_typetree(); cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if cgo and cgo.read_typetree().get("m_Name") == "Model_0":
            mr = comp_of(cgo.read_typetree(), ("MeshRenderer",))
            if mr:
                mats = mr.read_typetree().get("m_Materials", [])
                mo = by_id.get(mats[0].get("m_PathID")) if mats else None
                if mo:
                    for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_Colors", []):
                        nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
                        if nm == "_Color": return (round(val["r"], 3), round(val["g"], 3), round(val["b"], 3))
    return (0.3, 0.3, 0.3)
smap = {}
for dat in glob.glob(SIGHTS + r"\*\*.dat"):
    m = re.search(r"^\s*ID\s+(\d+)", open(dat, encoding="utf-8-sig", errors="ignore").read(), re.M)
    if m: smap[m.group(1)] = os.path.basename(os.path.dirname(dat)).lower()
for ln in open(content + r"\sights.tsv"):
    if not ln.strip(): continue
    gun = ln.split("\t")[0]
    dat = os.path.join(content, gun + ".dat"); col = (0.3, 0.3, 0.3)
    if os.path.exists(dat):
        m = re.search(r"^\s*Sight\s+(\d+)", open(dat, encoding="utf-8-sig", errors="ignore").read(), re.M)
        if m and smap.get(m.group(1)): col = sight_color(smap[m.group(1)])
    print(f"{gun}\t{col[0]},{col[1]},{col[2]}")
