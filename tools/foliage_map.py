#!/usr/bin/env python3
# MAP-AWARE foliage bake (generalises foliage_all.py, which hardcoded PEI's asset table).
#
# A map's Foliage.blob buckets per-instance transforms by an asset INDEX; the blob header carries the
# ac asset GUIDs (16 bytes each) that those indices point at. Instead of a hand-written index->asset
# table, we read those GUIDs and resolve each against every FoliageInstancedMeshInfoAsset .asset under
# Bundles\Assets\Landscapes\Foliage (each carries Metadata{GUID ...}). That makes ANY map bake-able --
# Washington uses the SAME grass MESH as PEI but a washington-tinted MATERIAL, and 2 grass types not 1.
#
#   python foliage_map.py Washington   -> game\content\foliage_washington\*.{bin,obj,_tex.png}
#   python foliage_map.py PEI          -> game\content\foliage\  (back-compat path)
import UnityPy, os, struct, re, sys, glob

MAP  = sys.argv[1] if len(sys.argv) > 1 else "PEI"
SUB  = int(sys.argv[2]) if len(sys.argv) > 2 else 1   # keep 1/SUB of instances (2 = half density) -- some maps bake absurdly dense grass
UNT  = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
BUND = UNT + r"\Bundles\core.masterbundle"
BLOB = UNT + rf"\Maps\{MAP}\Foliage.blob"
FOL  = UNT + r"\Bundles\Assets\Landscapes\Foliage"
OUT  = (r"C:\claude-workspace\unturned-godot\game\content\foliage"
        if MAP.upper() == "PEI" else
        rf"C:\claude-workspace\unturned-godot\game\content\foliage_{MAP.lower()}")
os.makedirs(OUT, exist_ok=True)

def parse_asset(path):
    txt = open(path, "r", errors="ignore").read()
    def block(key):
        m = re.search(key + r"\s*\{([^}]*)\}", txt, re.S); return m.group(1) if m else ""
    def field(blk, k):
        m = re.search(k + r"\s+(\S+)", blk); return m.group(1) if m else None
    return field(block("Mesh"), "Path"), field(block("Material"), "Path")

def short_name(path):   # PEI_Grass_00 / Washington_Grass_00 -> grass_00 ; Pebble_Sand_00 -> pebble_sand_00
    b = os.path.basename(path).replace("_Foliage.asset", "").replace(".asset", "").lower()
    return re.sub(r"^(pei|washington|russia|germany|yukon)_", "", b)

# GUID (32-hex, lowercase) -> (asset path, short name), only instance assets (skip *_Collection)
guid_map = {}
for ap in glob.glob(FOL + r"\**\*.asset", recursive=True):
    try: txt = open(ap, "r", errors="ignore").read()
    except Exception: continue
    if "FoliageInstancedMeshInfoAsset" not in txt: continue
    m = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if m: guid_map[m.group(1).lower()] = (ap, short_name(ap))
print(f"[guidmap] indexed {len(guid_map)} FoliageInstancedMeshInfoAsset(s)")

d = open(BLOB, "rb").read(); p = [0]
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0] += 4; return v
def i64():
    v = struct.unpack_from("<q", d, p[0])[0]; p[0] += 8; return v
def f32():
    v = struct.unpack_from("<f", d, p[0])[0]; p[0] += 4; return v
i32(); tc = i32(); tiles = [(i32(), i32(), i64()) for _ in range(tc)]
ac = i32()
blob_guids = []
for _ in range(ac):
    blob_guids.append(d[p[0]:p[0]+16]); p[0] += 16
hdr = p[0]

def variants(b):   # blob stores 16 raw bytes; try common encodings vs the .asset's 32-hex GUID
    net = (b[0:4][::-1] + b[4:6][::-1] + b[6:8][::-1] + b[8:16]).hex()   # .NET System.Guid (groups 1-3 LE, 4-5 BE)
    return [net, b.hex(), b[::-1].hex()]

names = {}   # blob asset index -> (name, mesh_path, mat_path)
for i, g in enumerate(blob_guids):
    hit = next((guid_map[v] for v in variants(g) if v in guid_map), None)
    if hit:
        mp, mtp = parse_asset(hit[0]); names[i] = (hit[1], mp, mtp)
        print(f"  blob[{i}] {g.hex()} -> {hit[1]}  mesh={mp}")
    else:
        print(f"  blob[{i}] {g.hex()} -> UNRESOLVED")

env = UnityPy.load(BUND)
cont = {p2.lower(): o for p2, o in env.container.items()}
def find(path, typ):
    k = path.replace("\\", "/").lower()
    return cont.get("assets/coremasterbundle/" + k) or next((v for kk, v in cont.items() if kk.endswith(k) and v.type.name == typ), None)

for i, (nm, mp, mtp) in names.items():
    o = find(mp, "Mesh")
    if o: open(os.path.join(OUT, nm + ".obj"), "w").write(o.read().export())
    else: print(f"   !! MESH miss {nm} {mp}")
    tex = re.sub(r"_[Mm]aterial\.mat$", ".png", mtp.replace("\\", "/")).lower()
    to = find(tex, "Texture2D")
    if to: to.read().image.save(os.path.join(OUT, nm + "_tex.png"))
    else: print(f"   !! TEX miss {nm} {tex}")

buckets = {i: [] for i in names}
for (tx, ty, off) in tiles:
    p[0] = hdr + off; ic = i32()
    for _ in range(ic):
        ai = i32(); mc = i32()
        for _ in range(mc):
            m = [f32() for _ in range(16)]; p[0] += 1
            if ai in buckets:
                buckets[ai].append((m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10], m[12], m[13], m[14]))
for i, rows in sorted(buckets.items()):
    if SUB > 1: rows = rows[::SUB]   # uniform decimation -- tile-ordered, so spatial spread is preserved
    with open(os.path.join(OUT, names[i][0] + ".bin"), "wb") as f:
        f.write(struct.pack("<i", len(rows)))
        for r in rows: f.write(struct.pack("<12f", *r))
    print(f"asset{i} {names[i][0]}.bin: {len(rows)} instances (sub={SUB})")
print("DONE ->", OUT)
