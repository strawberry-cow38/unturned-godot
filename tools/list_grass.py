import UnityPy
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(BUND)
print("=== container paths containing 'foliage/grass' ===")
for path, obj in env.container.items():
    lp = path.lower()
    if "foliage/grass" in lp or "grass_00" in lp:
        print(f"  {path}  ->  {obj.type.name}")
print("=== Mesh objects with 'grass' in name ===")
for obj in env.objects:
    if obj.type.name == "Mesh":
        try:
            tt = obj.read_typetree()
        except Exception:
            continue
        nm = tt.get("m_Name", "")
        if "grass" in nm.lower():
            sub = tt.get("m_SubMeshes", [])
            idx = sub[0].get("indexCount", "?") if sub else "?"
            print(f"  name={nm!r}   submeshIndexCount={idx}   pathID={obj.path_id}")
