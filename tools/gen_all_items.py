#!/usr/bin/env python3
"""gen_all_items.py -- build the FULL droppable-item list (every item with a .dat ID),
in the same {"resolved": {iid: {name, type, folder}}} shape extract_items.py consumes.

pei_loot_items.json is only the PEI loot-table subset (402); items outside it (many mags,
attachments, clothing, etc.) have no dropped model and fall back to the rarity box. Feeding
this list to extract_items.py fills every gap that has a world-model prefab (no-prefab items
just report NO_PREFAB and stay on the box, harmless).
"""
import os, json
BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT  = r"C:\claude-workspace\all_items.json"

def parse_kv(path):
    d = {}
    try:
        with open(path, encoding='utf-8-sig', errors='replace') as f:   # utf-8-sig strips BOM
            for line in f:
                s = line.strip()
                if not s or s in ('{', '}', '[', ']'):
                    continue
                s = s.replace('"', ' ')
                parts = s.split(None, 1)
                if not parts:
                    continue
                k = parts[0]; v = parts[1].strip() if len(parts) > 1 else ''
                if k not in d:
                    d[k] = v
    except Exception:
        pass
    return d

resolved = {}
for root, dirs, files in os.walk(BASE):
    for fn in files:
        low = fn.lower()
        if not low.endswith('.dat') or low == 'english.dat':
            continue
        d = parse_kv(os.path.join(root, fn))
        if 'ID' not in d or 'Type' not in d:
            continue
        try:
            iid = int(d['ID'])
        except ValueError:
            continue
        if iid <= 0 or iid > 65535 or str(iid) in resolved:
            continue
        eng = parse_kv(os.path.join(root, 'English.dat'))
        name = (eng.get('Name') or os.path.splitext(fn)[0]).strip()
        resolved[str(iid)] = {"name": name, "type": d.get('Type', 'Generic'), "folder": root}

json.dump({"resolved": resolved}, open(OUT, 'w'), indent=0)
print("all_items resolved:", len(resolved))
