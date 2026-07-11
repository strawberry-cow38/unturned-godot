import struct, uuid, collections
PATH = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Terrain\Trees.dat"
d = open(PATH, "rb").read(); p = [0]
def u8():
    v = d[p[0]]; p[0] += 1; return v
def u16():
    v = struct.unpack_from("<H", d, p[0])[0]; p[0] += 2; return v
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0] += 4; return v
def f32():
    v = struct.unpack_from("<f", d, p[0])[0]; p[0] += 4; return v
def rguid():
    n = u16(); g = d[p[0]:p[0]+n]; p[0] += n; return n, g

version = u8(); count = i32()
print(f"version={version}  count={count}")
gc = collections.Counter()
bad = 0
for i in range(count):
    n, g = rguid()
    if n != 16: bad += 1
    point = (f32(), f32(), f32())
    euler = (f32(), f32(), f32())
    scale = (f32(), f32(), f32())
    isgen = u8()
    gc[str(uuid.UUID(bytes_le=g)).replace("-", "")] += 1
print(f"consumed {p[0]} of {len(d)} bytes  (bad-guid-len: {bad})")
print(f"unique tree GUIDs: {len(gc)}")
for g, c in gc.most_common():
    print(f"  {g}  x{c}")
