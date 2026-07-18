"""List PEI's item drop tables (Spawns/Items.dat) by index + name + id count -- to pick a sensible
default loot table for converted store shelves (parse format = LootTables.cs / LootField)."""
import struct
p = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Spawns\Items.dat"
b = open(p, "rb").read(); o = [0]
def u8(): v = b[o[0]]; o[0] += 1; return v
def u16(): v = struct.unpack_from("<H", b, o[0])[0]; o[0] += 2; return v
def f32(): v = struct.unpack_from("<f", b, o[0])[0]; o[0] += 4; return v
def rstr():
    n = u8(); s = b[o[0]:o[0]+n].decode("utf-8", "ignore"); o[0] += n; return s
ver = u8()
if 1 < ver < 3: o[0] += 8
tc = u8()
print(f"ver={ver} tables={tc}\n idx  name                       tiers  total_ids")
for t in range(tc):
    o[0] += 3  # rgb
    name = rstr().replace("_", " ")
    if ver > 3: o[0] += 2  # tableID
    tiers = u8(); total = 0
    for _ in range(tiers):
        rstr(); f32(); sc = u8()
        for _ in range(sc): u16()
        total += sc
    print(f" {t:3d}  {name:26s} {tiers:5d}  {total}")
