import os
import econ_info   # cosmetics / boxes / keys ship no English.dat: their names + descriptions are Steam econ itemdefs (EconInfo.bin)
ECON = econ_info.load()
filled_names = filled_descs = 0
BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\items_catalog.tsv"

def parse_kv(path):
    d = {}
    try:
        with open(path, encoding='utf-8-sig', errors='replace') as f:   # utf-8-sig strips the BOM so the line-1 "GUID" key isn't read as "﻿GUID"
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

seen = {}
types = {}
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
        if iid <= 0 or iid > 65535 or iid in seen:
            continue
        eng = parse_kv(os.path.join(root, 'English.dat'))
        guid = d.get('GUID', '').strip().lower()
        econ = ECON.get(guid)
        name = eng.get('Name')
        if not name and econ: name = econ[0]; filled_names += 1   # 656 items had NO English.dat (2026-09-05 audit): the econ name is what retail shows
        if not name: name = os.path.splitext(fn)[0]
        desc = eng.get('Description', '')
        if not desc and econ and econ[1]: desc = econ[1]; filled_descs += 1
        desc = desc.replace('\t', ' ').replace('\r', ' ').replace('\n', ' ')
        typ = d.get('Type', 'Generic')
        types[typ] = types.get(typ, 0) + 1
        seen[iid] = (iid, name.strip(), typ, d.get('Rarity', 'Common'),
                     d.get('Size_X', '1'), d.get('Size_Y', '1'), desc.strip(),
                     d.get('GUID', '').strip())   # col 7: item's own GUID -> resolve blueprint ingredient GUIDs to ids

rows = sorted(seen.values())
# MERGE, never clobber: the committed .tsv carries hand edits (renamed shotgun shells, rows for port-only items) that a
# plain regeneration would erase (found 2026-09-05: 736 diffs, only ~650 of them the econ fill). So an existing OUT is
# the base -- a fresh row only REPLACES a name that is still the filename fallback (Foo_Bar_0) or fills an empty
# description; every other column and every extra row stays as committed. Delete OUT to regenerate from scratch.
import re
_filename_like = re.compile(r'^[A-Za-z0-9]+(?:_[A-Za-z0-9]+)+$')
if os.path.exists(OUT):
    old_rows = {}
    with open(OUT, encoding='utf-8') as f:
        for line in f:
            p = line.rstrip('\n').split('\t')
            if len(p) >= 8 and p[0].isdigit(): old_rows[int(p[0])] = p
    merged = 0; added = 0
    out_rows = []
    new_by_id = {r[0]: r for r in rows}
    for iid in sorted(set(old_rows) | set(new_by_id)):
        if iid in old_rows:
            p = list(old_rows[iid]); r = new_by_id.get(iid)
            if r:
                if _filename_like.match(p[1]) and not _filename_like.match(r[1]) and r[1] != p[1]: p[1] = r[1]; merged += 1
                if not p[6].strip() and r[6].strip(): p[6] = r[6]; merged += 1
            out_rows.append(tuple(p))
        else:
            out_rows.append(new_by_id[iid]); added += 1
    rows = out_rows
    print("merge into existing catalog: fields filled", merged, "| rows added", added, "| rows kept", len(old_rows))
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
    for r in rows:
        f.write('\t'.join(str(x) for x in r) + '\n')
print("items written:", len(rows), "| names from econ:", filled_names, "| descriptions from econ:", filled_descs)
print("distinct Types:", len(types))
print("top types:", sorted(types.items(), key=lambda kv: -kv[1])[:20])
print("sample:")
for r in rows[:6]:
    print("  ", r[:6], "|", r[6][:40])
