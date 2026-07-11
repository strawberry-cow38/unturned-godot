import struct, uuid
BLOB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Foliage.blob"
d = open(BLOB, "rb").read(); p = [0]
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0] += 4; return v
version = i32(); tileCount = i32()
p[0] += tileCount * 16
assetCount = i32()
print(f"version={version} tileCount={tileCount} assetCount={assetCount}")
for i in range(assetCount):
    g = d[p[0]:p[0]+16]; p[0] += 16
    le = str(uuid.UUID(bytes_le=g)).replace("-", "")
    be = str(uuid.UUID(bytes=g)).replace("-", "")
    print(f"asset {i}: raw={g.hex()}  le={le}  be={be}")
