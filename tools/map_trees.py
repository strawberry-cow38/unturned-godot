import struct, uuid, collections, os, glob
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
    n = u16(); g = d[p[0]:p[0]+n]; p[0] += n; return g

version = u8(); count = i32()
gc = collections.Counter()
for i in range(count):
    g = rguid(); [f32() for _ in range(9)]; u8()
    gc[str(uuid.UUID(bytes_le=g)).replace("-", "")] += 1
guids = set(gc)

root = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
found, namemap = {}, {}
for datf in glob.iglob(os.path.join(root, "**", "*.dat"), recursive=True):
    try:
        low = open(datf, "r", errors="ignore").read(4000).lower()
    except Exception:
        continue
    for g in guids:
        if g in low:
            found[g] = os.path.relpath(datf, root)
            namemap[g] = os.path.basename(os.path.dirname(datf))
            break

print(f"mapped {len(found)} of {len(guids)} tree GUIDs")
for g, c in gc.most_common():
    print(f"  {g} x{c:4d} -> {namemap.get(g,'???'):16s} [{found.get(g,'NOT FOUND')}]")
