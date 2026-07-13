# Extracts the FARM (crop) items from the retail item .dats -> content/farms.tsv (additive, like clothing_armor.tsv).
# A Type=Farm item is a SEED you plant: it grows for `Growth` seconds then yields item `Grow` on harvest
# (source ItemFarmAsset / InteractableFarm). Columns: id  growth(secs)  grow(yield item id)  ignoreSoil(0/1).
import os
BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\farms.tsv"

def parse_kv(path):
    d = {}
    try:
        with open(path, encoding='utf-8-sig', errors='replace') as f:
            for line in f:
                s = line.strip()
                if not s or s in ('{', '}', '[', ']'):
                    continue
                s = s.replace('"', ' ')
                parts = s.split(None, 1)
                if not parts:
                    continue
                k = parts[0]
                v = parts[1].strip() if len(parts) > 1 else ''
                if k not in d:
                    d[k] = v
    except Exception:
        pass
    return d

rows = []
for root, dirs, files in os.walk(BASE):
    for fn in files:
        low = fn.lower()
        if not low.endswith('.dat') or low == 'english.dat':
            continue
        d = parse_kv(os.path.join(root, fn))
        if d.get('Type', '').strip().lower() != 'farm':
            continue
        try:
            iid = int(d['ID']); growth = int(d.get('Growth', '0')); grow = int(d.get('Grow', '0'))
        except (KeyError, ValueError):
            continue
        if iid <= 0 or growth <= 0:
            continue
        ignore_soil = 1 if ('Ignore_Soil_Restrictions' in d and str(d.get('Ignore_Soil_Restrictions', '')).strip().lower() not in ('false', '0')) else 0
        rows.append((iid, growth, grow, ignore_soil))

rows.sort()
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
    for iid, g, gr, isl in rows:
        f.write(f"{iid}\t{g}\t{gr}\t{isl}\n")
print("farm/crop items:", len(rows))
for r in rows[:12]:
    print("  ", r)
