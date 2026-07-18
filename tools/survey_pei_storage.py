"""Survey: which container/furniture-candidate objects does PEI actually PLACE on the map, and how many?
Informs the "loot distribution on shelves / convert props to containers" vision -- if the map already
places fridges/cabinets/shelves as decoration, those are the auto-lootable-container candidates.
Read-only: parses the retail PEI Objects.dat + names objects via Bundles/Objects .dat (like extract_objects_v2)."""
import struct, os, glob, re
from collections import Counter

BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OBJDAT = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Level\Objects.dat"

d = open(OBJDAT, "rb").read(); p = [0]
def u8(): v = d[p[0]]; p[0] += 1; return v
def u16(): v = struct.unpack_from("<H", d, p[0])[0]; p[0] += 2; return v
def u32(): v = struct.unpack_from("<I", d, p[0])[0]; p[0] += 4; return v
def skf(n=1): p[0] += 4 * n
def g():
    ln = struct.unpack_from("<H", d, p[0])[0]; p[0] += 2; gg = d[p[0]:p[0]+ln]; p[0] += ln; return gg
def ng(x): return (x[0:4][::-1]+x[4:6][::-1]+x[6:8][::-1]+x[8:16]).hex() if len(x) == 16 else x.hex()

u8(); u32(); cnt = Counter()
for _x in range(64):
    for _y in range(64):
        for _i in range(u16()):
            skf(9); u16(); gg = g(); u8(); u32(); g(); u32(); u8()
            cnt[ng(gg)] += 1

# GUID -> folder name (+ category path) from the object .dat files
guid2name, guid2cat = {}, {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not m: continue
    folder = os.path.basename(os.path.dirname(datp))
    cat = os.path.relpath(os.path.dirname(datp), os.path.join(BUND, "Objects")).replace("\\", "/")
    guid2name[m.group(1).lower()] = folder
    guid2cat[m.group(1).lower()] = cat

KW = ("shelf", "cabinet", "fridge", "freezer", "locker", "wardrobe", "crate", "box",
      "rack", "case", "counter", "cupboard", "drawer", "desk", "table", "bookcase",
      "container", "dumpster", "trash", "bin", "safe", "toolbox", "chest")
print(f"PEI total object placements: {sum(cnt.values())} ({len(cnt)} unique GUIDs)\n")
print("--- placed FURNITURE category objects (count) ---")
rows = []
for guid, c in cnt.items():
    nm = guid2name.get(guid); cat = guid2cat.get(guid, "")
    if not nm: continue
    if "furniture" in cat.lower() or any(k in nm.lower() for k in KW):
        rows.append((c, nm, cat))
for c, nm, cat in sorted(rows, reverse=True):
    print(f"  {c:5d}  {nm:22s} [{cat}]")
print(f"\ntotal furniture/container-candidate placements: {sum(r[0] for r in rows)} across {len(rows)} types")
