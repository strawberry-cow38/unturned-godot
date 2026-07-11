import struct, os
BLOB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Foliage.blob"
OUT = r"C:\claude-workspace\unturned-godot\game\content\foliage\grass.bin"
d = open(BLOB, "rb").read(); p = [0]
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0] += 4; return v
def i64():
    v = struct.unpack_from("<q", d, p[0])[0]; p[0] += 8; return v
def f32():
    v = struct.unpack_from("<f", d, p[0])[0]; p[0] += 4; return v
version = i32(); tileCount = i32()
tiles = [(i32(), i32(), i64()) for _ in range(tileCount)]
assetCount = i32(); p[0] += assetCount * 16
hdr = p[0]
pts = []
for (tx, ty, off) in tiles:
    p[0] = hdr + off
    ic = i32()
    for _ in range(ic):
        ai = i32(); mc = i32()
        for _ in range(mc):
            m = [f32() for _ in range(16)]
            p[0] += 1  # clearWhenBaked
            if ai == 1:  # asset 1 = grass
                pts.append((m[12], m[13], m[14]))
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "wb") as f:
    f.write(struct.pack("<i", len(pts)))
    for (x, y, z) in pts:
        f.write(struct.pack("<3f", x, y, z))
print(f"exported {len(pts)} grass positions -> grass.bin ({4 + len(pts)*12} bytes)")
