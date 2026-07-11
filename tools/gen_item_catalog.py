import os
BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\items_catalog.tsv"

def parse_kv(path):
    d = {}
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
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
        name = eng.get('Name') or os.path.splitext(fn)[0]
        desc = eng.get('Description', '').replace('\t', ' ').replace('\r', ' ')
        typ = d.get('Type', 'Generic')
        types[typ] = types.get(typ, 0) + 1
        seen[iid] = (iid, name.strip(), typ, d.get('Rarity', 'Common'),
                     d.get('Size_X', '1'), d.get('Size_Y', '1'), desc.strip())

rows = sorted(seen.values())
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
    for r in rows:
        f.write('\t'.join(str(x) for x in r) + '\n')
print("items written:", len(rows))
print("distinct Types:", len(types))
print("top types:", sorted(types.items(), key=lambda kv: -kv[1])[:20])
print("sample:")
for r in rows[:6]:
    print("  ", r[:6], "|", r[6][:40])
