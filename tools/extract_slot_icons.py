import UnityPy, os
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
OUT = r"C:\claude-workspace\slot_icons"
os.makedirs(OUT, exist_ok=True)
env = UnityPy.load(MB)
want = {"sight", "tactical", "grip", "barrel", "magazine"}

# list every Texture2D/Sprite named exactly like a slot, with its container path + size
cands = {w: [] for w in want}
cont = {}
for path, obj in env.container.items():
    cont[obj.path_id] = path
for obj in env.objects:
    if obj.type.name in ("Texture2D", "Sprite"):
        try:
            data = obj.read()
        except Exception:
            continue
        nm = (data.m_Name or "").lower()
        if nm in want:
            path = cont.get(obj.path_id, "<no-container>")
            w = getattr(data, "m_Width", None)
            h = getattr(data, "m_Height", None)
            cands[nm].append((obj.type.name, path, w, h, obj))

for w in want:
    print(f"=== {w} : {len(cands[w])} candidate(s) ===")
    for tn, path, ww, hh, _ in cands[w]:
        print(f"   [{tn}] {ww}x{hh}  {path}")

# save the one whose container path points at the useable-gun icons (fallback: smallest square)
for w in want:
    picks = cands[w]
    if not picks:
        print("MISSING", w); continue
    ui = [p for p in picks if "useable" in p[1].lower() or "playeruseablegun" in p[1].lower()]
    chosen = ui[0] if ui else sorted(picks, key=lambda p: (p[2] or 9999))[0]
    tn, path, ww, hh, obj = chosen
    data = obj.read()
    try:
        img = data.image
        img.save(os.path.join(OUT, w.capitalize() + ".png"))
        print(f"SAVED {w.capitalize()}.png  ({tn} {ww}x{hh}) from {path}")
    except Exception as e:
        print("save-fail", w, e)
