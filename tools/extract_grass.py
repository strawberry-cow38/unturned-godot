import UnityPy, os
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT = r"C:\claude-workspace\unturned-godot\game\content\foliage"
os.makedirs(OUT, exist_ok=True)
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))

# extract the grass mesh (asset 1 = 612K instances)
got_mesh = False
for obj in env.objects:
    if obj.type.name == "Mesh":
        try:
            nm = obj.read_typetree().get("m_Name", "")
        except Exception:
            continue
        if nm == "Grass_00_LOD_0":
            m = obj.read()
            open(os.path.join(OUT, "Grass_00.obj"), "w").write(m.export())
            # bounds
            aabb = obj.read_typetree().get("m_LocalAABB", {})
            print(f"extracted Grass_00.obj from {nm}; AABB={aabb.get('m_Extent')}")
            got_mesh = True
            break
if not got_mesh:
    print("Grass_00_LOD_0 mesh NOT found")

# grass textures
for obj in env.objects:
    if obj.type.name == "Texture2D":
        try:
            nm = obj.read_typetree().get("m_Name", "")
        except Exception:
            continue
        if "grass" in nm.lower():
            try:
                img = obj.read().image
                img.save(os.path.join(OUT, nm + ".png"))
                print(f"grass texture: {nm}.png ({img.width}x{img.height})")
            except Exception as e:
                print(f"grass tex {nm} err {e}")
