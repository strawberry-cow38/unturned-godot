import UnityPy
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
NAMES = ["Birch_0","Birch_1","Maple_0","Maple_1","Pine_0","Pine_1","Bush_0","Bush_1",
         "Bush_Mauve","Cane_00","Metal_2","Clay_0","Mushroom_Red_0","Snow_Pile_00"]
env = UnityPy.load(BUND)
by_id = {o.path_id: o for o in env.objects}
cont  = {p.lower(): o for p, o in env.container.items()}
def tt(o): return o.read_typetree()
def comp_pids(go_tt):
    for c in go_tt.get("m_Component", []):
        d = c.get("component", c) if isinstance(c, dict) else c
        yield d.get("m_PathID")
def get_transform(go_tt):
    for pid in comp_pids(go_tt):
        o = by_id.get(pid)
        if o and o.type.name in ("Transform", "RectTransform"): return o
    return None
def go_of(tr_tt): return by_id.get(tr_tt["m_GameObject"]["m_PathID"])
COL = ("CapsuleCollider", "BoxCollider", "SphereCollider", "MeshCollider")
def scan(tr, name, path):
    trtt = tt(tr); go = go_of(trtt); gott = tt(go); gname = gott.get("m_Name", "?")
    for pid in comp_pids(gott):
        o = by_id.get(pid)
        if o and o.type.name in COL:
            c = tt(o)
            print(f"  {name}: {o.type.name} on '{path}/{gname}' r={c.get('m_Radius')} h={c.get('m_Height')} center={c.get('m_Center')} trigger={c.get('m_IsTrigger')} convex={c.get('m_Convex')}")
    for ch in trtt.get("m_Children", []):
        c2 = by_id.get(ch["m_PathID"])
        if c2: scan(c2, name, path + "/" + gname)
for name in NAMES:
    go = cont.get(f"assets/coremasterbundle/trees/{name.lower()}/resource.prefab")
    if not go:
        print(f"{name}: NO prefab"); continue
    root = get_transform(tt(go))
    before = "  "
    print(f"{name}:")
    scan(root, name, "")
