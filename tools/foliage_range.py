import struct
from collections import Counter
BLOB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Foliage.blob"
d = open(BLOB, "rb").read()
p = [0]
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0] += 4; return v
def i64():
    v = struct.unpack_from("<q", d, p[0])[0]; p[0] += 8; return v
def f32():
    v = struct.unpack_from("<f", d, p[0])[0]; p[0] += 4; return v
version = i32(); tileCount = i32()
tiles = [(i32(), i32(), i64()) for _ in range(tileCount)]
assetCount = i32()
p[0] += assetCount * 16
hdr = p[0]
minx = miny = minz = 1e9; maxx = maxy = maxz = -1e9
tcx = Counter()  # tile coord x range
for (tx, ty, off) in tiles:
    tcx[tx] += 1
    p[0] = hdr + off
    ic = i32()
    for _ in range(ic):
        i32()  # assetIndex
        mc = i32()
        for _ in range(mc):
            m = [f32() for _ in range(16)]
            p[0] += 1  # clearWhenBaked
            x, y, z = m[12], m[13], m[14]
            minx = min(minx, x); maxx = max(maxx, x)
            miny = min(miny, y); maxy = max(maxy, y)
            minz = min(minz, z); maxz = max(maxz, z)
txs = [t[0] for t in tiles]; tys = [t[1] for t in tiles]
print(f"tile coord x: {min(txs)}..{max(txs)}   y: {min(tys)}..{max(tys)}")
print(f"instance WORLD X: {minx:.1f}..{maxx:.1f}")
print(f"instance WORLD Y: {miny:.1f}..{maxy:.1f}")
print(f"instance WORLD Z: {minz:.1f}..{maxz:.1f}")
print("(objects use world X/Z ~ -1024..1024 centered; if foliage matches, no offset needed)")
