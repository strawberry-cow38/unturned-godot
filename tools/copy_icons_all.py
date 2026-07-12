import os, glob, re, shutil
from PIL import Image
U = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned"
ICONS = os.path.join(U, r"Extras\Icons")
OUT = r"C:\claude-workspace\item_out\icons"
REPO = r"C:\claude-workspace\unturned-godot\game\content\items\icons"
TSV = r"C:\claude-workspace\unturned-godot\game\content\items_catalog.tsv"
os.makedirs(OUT, exist_ok=True); os.makedirs(REPO, exist_ok=True)

# ground-truth icons indexed by trailing _<id>.png
icon_by_id = {}
for p in glob.glob(os.path.join(ICONS, "*.png")):
    m = re.search(r"_(\d+)\.png$", os.path.basename(p))
    if m: icon_by_id.setdefault(int(m.group(1)), p)

# catalog ids = col0 of the TSV (the full 1937-item registry the inventory can hold)
ids = []
for line in open(TSV, encoding="utf-8", errors="replace"):
    line = line.strip()
    if not line: continue
    c0 = line.split("\t")[0]
    if c0.isdigit(): ids.append(int(c0))
ids = sorted(set(ids))

MAXD = 256
n = 0; miss = []
for i in ids:
    src = icon_by_id.get(i)
    if not src: miss.append(i); continue
    dst = os.path.join(OUT, f"{i}.png")
    if not os.path.exists(dst):
        try:
            im = Image.open(src).convert("RGBA"); w, h = im.size; s = MAXD / max(w, h)
            if s < 1.0: im = im.resize((max(1, round(w*s)), max(1, round(h*s))), Image.LANCZOS)
            im.save(dst)
        except Exception:
            miss.append(i); continue
    shutil.copy2(dst, REPO); n += 1
tot = sum(os.path.getsize(f) for f in glob.glob(os.path.join(REPO, "*.png")))
print(f"catalog ids={len(ids)}  icons in repo now={len(glob.glob(os.path.join(REPO,'*.png')))}  copied/verified={n}  no-icon={len(miss)}")
print(f"repo icons total {tot/1e6:.1f} MB; missing sample {miss[:12]}")
