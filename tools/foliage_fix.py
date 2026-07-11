import UnityPy, os, struct
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
BLOB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Foliage.blob"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\foliage"
os.makedirs(OUT, exist_ok=True)

# --- 1. exact mesh + exact PEI material texture, resolved by container path (what the asset references) ---
env = UnityPy.load(BUND)
for path, obj in env.container.items():
    lp = path.lower()
    if lp.endswith("terrain/foliage/grass/grass_00_mesh.fbx") and obj.type.name == "Mesh":
        m = obj.read()
        open(os.path.join(OUT, "Grass_00.obj"), "w").write(m.export())
        aabb = obj.read_typetree().get("m_LocalAABB", {})
        print("MESH  Grass_00.obj  name=", getattr(m, "m_Name", "?"), " AABB extent=", aabb.get("m_Extent"))
    if lp.endswith("terrain/foliage/grass/pei/grass_00.png") and obj.type.name == "Texture2D":
        img = obj.read().image
        img.save(os.path.join(OUT, "Grass_00_Albedo.png"))
        print("TEX   Grass_00_Albedo.png (pei/grass_00.png)  size=", img.size)

# --- 2. re-export blob asset 1 (grass) with full baked transform: 9 basis + 3 pos = 12 floats/instance ---
d = open(BLOB, "rb").read(); p = [0]
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0] += 4; return v
def i64():
    v = struct.unpack_from("<q", d, p[0])[0]; p[0] += 8; return v
def f32():
    v = struct.unpack_from("<f", d, p[0])[0]; p[0] += 4; return v
ver = i32(); tc = i32()
tiles = [(i32(), i32(), i64()) for _ in range(tc)]
ac = i32(); p[0] += ac * 16; hdr = p[0]
rows = []
for (tx, ty, off) in tiles:
    p[0] = hdr + off; ic = i32()
    for _ in range(ic):
        ai = i32(); mc = i32()
        for _ in range(mc):
            m = [f32() for _ in range(16)]; p[0] += 1  # clearWhenBaked
            if ai == 1:  # grass
                rows.append((m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], m[12], m[13], m[14]))
with open(os.path.join(OUT, "grass.bin"), "wb") as f:
    f.write(struct.pack("<i", len(rows)))
    for r in rows:
        f.write(struct.pack("<12f", *r))
print(f"grass.bin  {len(rows)} instances, 12 floats each (Unity basis cols X/Y/Z + pos)")
