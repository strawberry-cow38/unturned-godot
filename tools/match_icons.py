import os, glob, json, re
U = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
ICONS = os.path.join(U, r"Extras\Icons")
loot = json.load(open(r"C:\claude-workspace\pei_loot_items.json"))["resolved"]

# index icons by trailing _<id>.png
icon_by_id = {}
for p in glob.glob(os.path.join(ICONS, "*.png")):
    m = re.search(r"_(\d+)\.png$", os.path.basename(p))
    if m:
        icon_by_id.setdefault(int(m.group(1)), p)   # first wins
print(f"icons on disk: indexed {len(icon_by_id)} unique ids")

matched = []; missing = []
for iid, meta in loot.items():
    i = int(iid)
    if i in icon_by_id: matched.append((i, meta["name"], os.path.basename(icon_by_id[i])))
    else: missing.append((i, meta["name"], meta["type"]))

print(f"\nLOOT ICON MATCH: {len(matched)}/{len(loot)} matched, {len(missing)} missing")
print("sample matches (id -> icon file):")
for i, nm, f in matched[:14]:
    print(f"   {i:5} {nm:22} -> {f}")
if missing:
    print("missing:")
    for i, nm, t in missing[:20]:
        print(f"   {i:5} {nm:22} ({t})")
# icon file dims (sanity)
try:
    from PIL import Image
    for i, nm, f in matched[:4]:
        im = Image.open(icon_by_id[i]); print(f"   dims {nm}: {im.size}")
except Exception as e:
    print("PIL?", e)
