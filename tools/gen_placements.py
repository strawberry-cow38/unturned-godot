import struct, os, sys, re, glob
from collections import Counter
MAPBASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps"
UNT = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
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

# --- UnityEngine.Random (xorshift128) -- reproduces LevelObject.GetMaterialOverride's per-instance pick.
# materialIndexOverride is -1 for every PEI placement, so the game rolls: InitState((int)instanceID); Range(0, count).
MASK = 0xFFFFFFFF
def unity_range_count(instance_id, count):
    if count <= 0: return 0
    x = instance_id & MASK
    y = (1812433253 * x + 1) & MASK
    z = (1812433253 * y + 1) & MASK
    w = (1812433253 * z + 1) & MASK
    t = (x ^ ((x << 11) & MASK)) & MASK          # one XORShift = one Random.Range draw
    w = (w ^ (w >> 19) ^ t ^ (t >> 8)) & MASK
    return w % count

# --- material-palette maps: object GUID -> palette GUID -> ordered [variant material names] ---
def read_txt(fp):
    try: return open(fp, encoding="utf-8", errors="ignore").read()
    except Exception: return ""
pal_mats = {}   # palette GUID -> [material basenames in index order]
for ap in glob.glob(os.path.join(UNT, "Bundles", "Assets", "Material_Palettes", "*.asset")):
    t = read_txt(ap)
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", t)
    if not m: continue
    names = re.findall(r"Path\s+(?:\S*/)?([^/\s]+)\.mat", t)   # ordered, index 0..n
    if names: pal_mats[m.group(1).lower()] = names
guid_pal = {}   # object GUID -> palette GUID
for dp in glob.glob(os.path.join(UNT, "Bundles", "Objects", "**", "*.dat"), recursive=True):
    t = read_txt(dp)
    gm = re.search(r"GUID\s+([0-9a-fA-F]{32})", t)
    pm = re.search(r"Material_Palette\s+([0-9a-fA-F]{32})", t)
    if gm and pm: guid_pal[gm.group(1).lower()] = pm.group(1).lower()
print("palettes=%d  paletted-objects=%d" % (len(pal_mats), len(guid_pal)))

def variant_for(gid_hex, inst, mi):
    pg = guid_pal.get(gid_hex.lower())
    if not pg: return None
    names = pal_mats.get(pg)
    if not names or len(names) < 2: return None            # single-material palette = no colour choice
    n = len(names)
    # LevelObject.GetMaterialOverride: materialIndexOverride==-1 -> rng by instanceID; else the forced editor pick (clamped).
    # mi is read as unsigned u32, so -1 shows as 0xFFFFFFFF (>= 0x80000000 covers any negative = "not forced").
    if mi is None or mi >= 0x80000000:
        idx = unity_range_count(inst, n)
    else:
        idx = min(mi, n - 1)
    cand = names[idx]
    # apply ONLY if the variant's texture is already extracted, else keep the mesh's own material (no regression)
    if os.path.exists(os.path.join(OUT, cand + "_tex.png")): return cand
    return None

version = u8(); avail = u32()
objs = []
for x in range(64):
    for y in range(64):
        count = u16()
        for i in range(count):
            point = vec3(); euler = vec3(); scale = vec3()
            oid = u16(); g = guid(); origin = u8(); inst = u32()
            mg = guid(); mi = u32(); cull = u8()
            gh = netguid(g)
            mv = variant_for(gh, inst, mi) if len(gh) == 32 else None
            objs.append((gh, point, euler, scale, mv))

print("map=%r version=%d objects=%d consumed=%d/%d leftover=%d" % (mapname, version, len(objs), pos, len(d), len(d) - pos))
os.makedirs(OUT, exist_ok=True)
key = re.sub(r'[^A-Za-z0-9]', '', mapname)
fn = "placements.txt" if mapname == "PEI" else ("placements_%s.txt" % key)
nvar = 0
with open(os.path.join(OUT, fn), "w") as f:
    for gid, pt, eu, sc, mv in objs:
        if len(gid) != 32:
            continue
        base = "%s %.3f %.3f %.3f %.3f %.3f %.3f %.3f %.3f %.3f" % (
            gid, pt[0], pt[1], pt[2], eu[0], eu[1], eu[2], sc[0], sc[1], sc[2])
        if mv:
            f.write(base + " " + mv + "\n"); nvar += 1
        else:
            f.write(base + "\n")
print("wrote", fn, " placements-with-palette-variant=%d" % nvar)
# how many resolve to a mesh we already extracted?
g2m = set()
gmp = os.path.join(OUT, "guid_mesh.txt")
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
