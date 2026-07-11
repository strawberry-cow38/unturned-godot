import UnityPy, os
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
kws = ["grass", "pine", "birch", "maple", "bush", "fern", "clay", "dead", "cane",
       "foliage", "shrub", "flower", "plant", "wheat", "reed"]
print("=== Mesh objects matching foliage keywords ===")
seen = set()
for obj in env.objects:
    if obj.type.name == "Mesh":
        try:
            nm = obj.read_typetree().get("m_Name", "")
            low = nm.lower()
            if any(k in low for k in kws) and nm not in seen:
                seen.add(nm); print(f"  Mesh: {nm}")
        except Exception:
            pass
print("=== container paths with grass/foliage ===")
n = 0
for path, obj in env.container.items():
    pl = path.lower()
    if ("grass" in pl or "foliage" in pl) and obj.type.name in ("Mesh", "GameObject", "MonoBehaviour"):
        print(f"  {obj.type.name}: {path}"); n += 1
        if n > 25:
            break
