import struct, os, sys, re
from collections import Counter
MAPBASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps"
mapname = sys.argv[1] if len(sys.argv) > 1 else "PEI"
p = os.path.join(MAPBASE, mapname, "Level", "Objects.dat")
d = open(p, "rb").read()
pos = 0
def u8():
    global pos; v = d[pos]; pos += 1; return v
def u16():
    global pos; v = struct.unpack_from("<H", d, pos)[0]; pos += 2; return v
def u32():
    global pos; v = struct.unpack_from("<I", d, pos)[0]; pos += 4; return v
def f32():
    global pos; v = struct.unpack_from("<f", d, pos)[0]; pos += 4; return v
def vec3():
    return (f32(), f32(), f32())
def guid():
    global pos
    ln = struct.unpack_from("<H", d, pos)[0]; pos += 2
    g = d[pos:pos+ln]; pos += ln
    return g
def netguid(g):
    return (g[0:4][::-1] + g[4:6][::-1] + g[6:8][::-1] + g[8:16]).hex() if len(g) == 16 else g.hex()

version = u8(); avail = u32()
objs = []
for x in range(64):
    for y in range(64):
        count = u16()
        for i in range(count):
            point = vec3(); euler = vec3(); scale = vec3()
            oid = u16(); g = guid(); origin = u8(); inst = u32()
            mg = guid(); mi = u32(); cull = u8()
            objs.append((netguid(g), point, euler, scale))

print("map=%r version=%d objects=%d consumed=%d/%d leftover=%d" % (mapname, version, len(objs), pos, len(d), len(d) - pos))
out = r"C:\claude-workspace\unturned-godot\game\content\objects"
os.makedirs(out, exist_ok=True)
key = re.sub(r'[^A-Za-z0-9]', '', mapname)
fn = "placements.txt" if mapname == "PEI" else ("placements_%s.txt" % key)
with open(os.path.join(out, fn), "w") as f:
    for gid, pt, eu, sc in objs:
        if len(gid) != 32:
            continue
        f.write("%s %.3f %.3f %.3f %.3f %.3f %.3f %.3f %.3f %.3f\n" % (
            gid, pt[0], pt[1], pt[2], eu[0], eu[1], eu[2], sc[0], sc[1], sc[2]))
print("wrote", fn)
# how many resolve to a mesh we already extracted?
g2m = set()
gmp = os.path.join(out, "guid_mesh.txt")
if os.path.exists(gmp):
    for line in open(gmp):
        parts = line.split()
        if parts: g2m.add(parts[0])
valid = [o[0] for o in objs if len(o[0]) == 32]
resolved = sum(1 for g in valid if g in g2m)
print("valid-guid objects=%d  resolve-to-known-mesh=%d  (%d unknown -> auto-skip)" % (len(valid), resolved, len(valid) - resolved))
print("TOP unknown GUIDs:")
for gid, c in Counter(g for g in valid if g not in g2m).most_common(8):
    print("  %s x%d" % (gid, c))
