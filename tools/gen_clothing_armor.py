# Extracts clothing PROTECTION multipliers from the retail item .dats into an ADDITIVE table the port loads on top of
# items_catalog.tsv (ItemCatalog.WireClothingArmor) -- kept separate so it never risks the main 1937-item catalog.
# Columns: id  Armor  Armor_Explosion  Falling_Damage_Multiplier. Source ItemClothingAsset: all default 1.0 (no cut);
# Armor_Explosion defaults to Armor if unspecified. Only items with a non-default value are emitted.
import os
BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\clothing_armor.tsv"

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

def fv(x, default):
    try:
        return float(x)
    except (TypeError, ValueError):
        return default

rows = []
for root, dirs, files in os.walk(BASE):
    for fn in files:
        low = fn.lower()
        if not low.endswith('.dat') or low == 'english.dat':
            continue
        d = parse_kv(os.path.join(root, fn))
        if 'ID' not in d:
            continue
        try:
            iid = int(d['ID'])
        except ValueError:
            continue
        if iid <= 0 or iid > 65535:
            continue
        armor = fv(d.get('Armor'), 1.0)
        aexp  = fv(d.get('Armor_Explosion'), armor)   # source: Armor_Explosion defaults to Armor
        fall  = fv(d.get('Falling_Damage_Multiplier'), 1.0)
        # Prevents_Falling_Broken_Bones is a bool FLAG (key present = true unless explicitly false/0) -> source ParseBool
        bone  = 1 if ('Prevents_Falling_Broken_Bones' in d and str(d.get('Prevents_Falling_Broken_Bones', '')).strip().lower() not in ('false', '0')) else 0
        if abs(armor - 1.0) < 1e-6 and abs(aexp - 1.0) < 1e-6 and abs(fall - 1.0) < 1e-6 and bone == 0:
            continue
        rows.append((iid, armor, aexp, fall, bone))

rows.sort()
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
    for iid, a, ae, fl, bn in rows:
        f.write(f"{iid}\t{a:g}\t{ae:g}\t{fl:g}\t{bn}\n")
print("clothing armor rows:", len(rows))
for r in rows[:10]:
    print("  ", r)
