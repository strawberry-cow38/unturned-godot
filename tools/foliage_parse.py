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
def u8():
    v = d[p[0]]; p[0] += 1; return v
def guid16():
    g = d[p[0]:p[0]+16]; p[0] += 16
    raw = g.hex()
    net = (g[0:4][::-1] + g[4:6][::-1] + g[6:8][::-1] + g[8:16]).hex()
    return raw, net

version = i32()
tileCount = i32()
tiles = []
for _ in range(tileCount):
    x = i32(); y = i32(); off = i64()
    tiles.append((x, y, off))
assets = []
if version >= 2:
    assetCount = i32()
    for _ in range(assetCount):
        assets.append(guid16())
tileBlobHeaderOffset = p[0]

assetCnt = Counter(); total = 0; samples = []
for (x, y, off) in tiles:
    p[0] = tileBlobHeaderOffset + off
    instanceCount = i32()
    for _ in range(instanceCount):
        ai = i32() if version >= 2 else (guid16(), -1)[1]
        mc = i32()
        for _ in range(mc):
            m = [f32() for _ in range(16)]
            u8()  # clearWhenBaked
            assetCnt[ai] += 1; total += 1
            if len(samples) < 6:
                samples.append((ai, (round(m[12], 1), round(m[13], 1), round(m[14], 1))))
print(f"version={version} tiles={tileCount} assets={len(assets)} totalInstances={total} consumed={p[0]}/{len(d)} leftover={len(d)-p[0]}")
print("per-asset instance counts:", dict(assetCnt.most_common(15)))
print("sample (assetIdx, pos):", samples)
print("asset GUIDs (raw / net):")
for i, (raw, net) in enumerate(assets):
    print(f"  [{i}] raw={raw}  net={net}")
