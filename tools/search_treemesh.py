import UnityPy
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(BUND)
print("=== container entries under /trees/{birch_0,pine_0,bush_0}/ ===")
for path, obj in sorted(env.container.items(), key=lambda kv: kv[0]):
    lp = path.lower()
    if "/trees/" in lp and any(s in lp for s in ["birch_0/", "pine_0/", "bush_0/"]):
        print(f"  {obj.type.name:12s} {path}")
print()
print("=== any Mesh whose name mentions birch/pine/maple (first 40) ===")
n = 0
for obj in env.objects:
    if obj.type.name == "Mesh":
        try:
            nm = obj.read_typetree().get("m_Name", "")
        except Exception:
            continue
        if any(s in nm.lower() for s in ["birch", "pine", "maple", "bush"]):
            print(f"  {nm}")
            n += 1
            if n >= 40:
                break
