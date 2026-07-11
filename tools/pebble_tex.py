import UnityPy, os
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\foliage"
env = UnityPy.load(BUND)
cont = {p.lower(): o for p, o in env.container.items()}

print("=== container entries with 'pebble' ===")
for p, o in sorted((k, v) for k, v in cont.items() if "pebble" in k):
    print(f"  {o.type.name:10s} {p}")

# read each pebble material's _MainTex texture and save it
for mat_rel, nm in [("terrain/foliage/pebbles/pebble_00_material.mat", "pebble_00"),
                    ("terrain/foliage/pebbles/pebble_shore_00_material.mat", "pebble_sand_00")]:
    mo = cont.get("assets/coremasterbundle/" + mat_rel) or next((v for k, v in cont.items() if k.endswith(mat_rel)), None)
    if not mo:
        print(f"!! material not found: {mat_rel}"); continue
    tt = mo.read_typetree()
    texenvs = tt.get("m_SavedProperties", {}).get("m_TexEnvs", [])
    for entry in texenvs:
        # entry is (name, texenv) or {'first':name,'second':texenv}
        name = entry[0] if isinstance(entry, (list, tuple)) else entry.get("first")
        te   = entry[1] if isinstance(entry, (list, tuple)) else entry.get("second")
        if name != "_MainTex":
            continue
        pid = te.get("m_Texture", {}).get("m_PathID")
        print(f"{nm}: _MainTex pathID={pid}")
        if pid:
            for o in env.objects:
                if o.path_id == pid and o.type.name == "Texture2D":
                    img = o.read().image
                    img.save(os.path.join(OUT, nm + "_tex.png"))
                    print(f"   saved {nm}_tex.png {img.size} (name={o.read().m_Name})")
