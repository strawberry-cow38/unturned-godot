"""Scope the missing props for a map: which placed-object GUIDs aren't in guid_mesh.txt yet, by instance
count, resolved to their Bundles/Objects folder name so extract_object_named can rip them.
  python wash_prop_scope.py Washington"""
import struct, os, glob, re, sys
from collections import Counter
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths
MAPBASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps"
BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()
mapname = sys.argv[1] if len(sys.argv) > 1 else "Washington"

d = open(os.path.join(MAPBASE, mapname, "Level", "Objects.dat"), "rb").read()
pos = 0
def u8():
    global pos; v = d[pos]; pos += 1; return v
def u16():
    global pos; v = struct.unpack_from("<H", d, pos)[0]; pos += 2; return v
def u32():
    global pos; v = struct.unpack_from("<I", d, pos)[0]; pos += 4; return v
def f32():
    global pos; v = struct.unpack_from("<f", d, pos)[0]; pos += 4; return v
def vec3(): return (f32(), f32(), f32())
def rguid():
    global pos
    ln = struct.unpack_from("<H", d, pos)[0]; pos += 2
    g = d[pos:pos+ln]; pos += ln; return g
def netguid(g):
    return (g[0:4][::-1] + g[4:6][::-1] + g[6:8][::-1] + g[8:16]).hex() if len(g) == 16 else g.hex()

version = u8(); avail = u32()
cnt = Counter()
for x in range(64):
    for y in range(64):
        c = u16()
        for i in range(c):
            vec3(); vec3(); vec3()
            oid = u16(); g = rguid(); origin = u8(); inst = u32()
            mg = rguid(); mi = u32(); cull = u8()
            gh = netguid(g)
            if len(gh) == 32: cnt[gh] += 1

known = set()
for line in open(os.path.join(OUT, "guid_mesh.txt")):
    pp = line.split()
    if pp: known.add(pp[0].lower())

g2name = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    g2name[m.group(1).lower()] = os.path.basename(os.path.dirname(datp))

unknown = [g for g in cnt if g.lower() not in known]
unknown.sort(key=lambda g: -cnt[g])
resolvable = sum(1 for g in unknown if g.lower() in g2name)
print("distinct=%d known=%d unknown=%d unknown-instances=%d resolvable-to-name=%d/%d" % (
    len(cnt), len(cnt) - len(unknown), len(unknown), sum(cnt[g] for g in unknown), resolvable, len(unknown)))
print("TOP UNKNOWN (count  guid  name):")
for g in unknown[:70]:
    print("  %5d  %s  %s" % (cnt[g], g, g2name.get(g.lower(), "??UNRESOLVED")))
# dump the resolvable names in count order to a file, for a batch extractor to consume
names = []
for g in unknown:
    nm = g2name.get(g.lower())
    if nm and nm not in names: names.append(nm)
open(os.path.join(OUT, "wash_missing_names.txt"), "w").write("\n".join(names) + "\n")
print("wrote wash_missing_names.txt (%d distinct names, count-ordered)" % len(names))
