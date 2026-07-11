import UnityPy, os
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
rows = []
for obj in env.objects:
    if obj.type.name == "Mesh":
        try:
            tt = obj.read_typetree()
        except Exception:
            continue
        nm = tt.get("m_Name", "")
        low = nm.lower()
        if any(low.startswith(s) for s in ["pine_", "birch_", "maple_", "bush_", "dead_", "sugarcane", "cane_"]):
            ext = tt.get("m_LocalAABB", {}).get("m_Extent", {})
            ex, ey, ez = ext.get("x", 0), ext.get("y", 0), ext.get("z", 0)
            rows.append((ey, nm, ex, ey, ez))
# tallest first = full trees; small = debris/stump/foliage bits
rows.sort(reverse=True)
print("=== tree/bush meshes by height (tallest = full trees) ===")
for h, nm, ex, ey, ez in rows[:40]:
    print(f"  {nm:28s} extent {ex:5.1f} x {ey:5.1f} x {ez:5.1f}")
print(f"... {len(rows)} total tree/bush meshes")
