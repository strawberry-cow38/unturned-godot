import UnityPy, os, struct, re
UNT  = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
BUND = UNT + r"\Bundles\core.masterbundle"
BLOB = UNT + r"\Maps\PEI\Foliage.blob"
FOL  = UNT + r"\Bundles\Assets\Landscapes\Foliage"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\foliage"
os.makedirs(OUT, exist_ok=True)

# blob asset index -> (FoliageInstancedMeshInfoAsset .asset file, short name)
assets = {
 0: (FOL + r"\Pebbles\Pebble_Sand_00_Foliage.asset", "pebble_sand_00"),
 1: (FOL + r"\Grass\PEI\PEI_Grass_00_Foliage.asset",  "grass_00"),
 2: (FOL + r"\Grass\PEI\PEI_Flowers_01_Foliage.asset", "flowers_01"),
 3: (FOL + r"\Pebbles\Pebble_00_Foliage.asset",       "pebble_00"),
 4: (FOL + r"\Grass\PEI\PEI_Flowers_02_Foliage.asset", "flowers_02"),
 5: (FOL + r"\Grass\PEI\PEI_Flowers_00_Foliage.asset", "flowers_00"),
 6: (FOL + r"\Grass\PEI\PEI_Flowers_03_Foliage.asset", "flowers_03"),
}

def parse_asset(path):
    txt = open(path, "r", errors="ignore").read()
    def block(key):
        m = re.search(key + r"\s*\{([^}]*)\}", txt, re.S); return m.group(1) if m else ""
    def field(blk, k):
        m = re.search(k + r"\s+(\S+)", blk); return m.group(1) if m else None
    return field(block("Mesh"), "Path"), field(block("Material"), "Path")

env = UnityPy.load(BUND)
cont = {p.lower(): o for p, o in env.container.items()}

for idx, (apath, nm) in sorted(assets.items()):
    mesh_path, mat_path = parse_asset(apath)
    print(f"asset{idx} {nm}: mesh={mesh_path}  mat={mat_path}")
    # mesh by container path
    mp = mesh_path.replace("\\", "/").lower()
    o = cont.get("assets/coremasterbundle/" + mp) or next((v for k, v in cont.items() if k.endswith(mp) and v.type.name == "Mesh"), None)
    if o and o.type.name == "Mesh":
        open(os.path.join(OUT, nm + ".obj"), "w").write(o.read().export())
    else:
        print(f"   !! MESH not found for {mp}")
    # texture: material path (…_material.mat) -> sibling .png
    tex = re.sub(r"_[Mm]aterial\.mat$", ".png", mat_path.replace("\\", "/")).lower()
    to = cont.get("assets/coremasterbundle/" + tex) or next((v for k, v in cont.items() if k.endswith(tex) and v.type.name == "Texture2D"), None)
    if to and to.type.name == "Texture2D":
        img = to.read().image; img.save(os.path.join(OUT, nm + "_tex.png"))
        print(f"   tex -> {nm}_tex.png {img.size}")
    else:
        print(f"   !! TEX not found for {tex}")

# parse blob, bucket instances (full transform, 12f) by asset index
d = open(BLOB, "rb").read(); p = [0]
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0] += 4; return v
def i64():
    v = struct.unpack_from("<q", d, p[0])[0]; p[0] += 8; return v
def f32():
    v = struct.unpack_from("<f", d, p[0])[0]; p[0] += 4; return v
i32(); tc = i32(); tiles = [(i32(), i32(), i64()) for _ in range(tc)]
ac = i32(); p[0] += ac * 16; hdr = p[0]
buckets = {i: [] for i in assets}
for (tx, ty, off) in tiles:
    p[0] = hdr + off; ic = i32()
    for _ in range(ic):
        ai = i32(); mc = i32()
        for _ in range(mc):
            m = [f32() for _ in range(16)]; p[0] += 1
            if ai in buckets:
                buckets[ai].append((m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], m[12], m[13], m[14]))
for idx, rows in sorted(buckets.items()):
    nm = assets[idx][1]
    with open(os.path.join(OUT, nm + ".bin"), "wb") as f:
        f.write(struct.pack("<i", len(rows)))
        for r in rows:
            f.write(struct.pack("<12f", *r))
    print(f"asset{idx} {nm}.bin: {len(rows)} instances")
