import UnityPy
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(BUND)
cont = {p.lower(): o for p, o in env.container.items()}
for mat_rel, nm in [("terrain/foliage/pebbles/pebble_00_material.mat", "pebble_00"),
                    ("terrain/foliage/pebbles/pebble_shore_00_material.mat", "pebble_sand_00")]:
    mo = cont.get("assets/coremasterbundle/" + mat_rel) or next((v for k, v in cont.items() if k.endswith(mat_rel)), None)
    if not mo:
        print("no mat", mat_rel); continue
    tt = mo.read_typetree()
    print(f"=== {nm}  shader-driven color props ===")
    for entry in tt.get("m_SavedProperties", {}).get("m_Colors", []):
        name = entry[0] if isinstance(entry, (list, tuple)) else entry.get("first")
        val  = entry[1] if isinstance(entry, (list, tuple)) else entry.get("second")
        print(f"  {name} = {val}")
