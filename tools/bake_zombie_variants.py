from PIL import Image
import UnityPy, os, re, glob
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
ITEMS = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
env = UnityPy.load(MB)
d = r"C:\claude-workspace\unturned-godot\game\content"   # atlases go straight to the game content dir
os.makedirs(d, exist_ok=True)

# clothing folder-name (lower) -> item ID, scanned from the loose .dat files
id_of = {}
for datf in glob.iglob(os.path.join(ITEMS, "**", "*.dat"), recursive=True):
    try:
        txt = open(datf, errors="ignore").read()
    except Exception:
        continue
    m = re.search(r"(?m)^\s*ID\s+(\d+)", txt)
    if not m:
        continue
    iid = int(m.group(1))
    id_of[os.path.splitext(os.path.basename(datf))[0].lower()] = iid
    id_of[os.path.basename(os.path.dirname(datf)).lower()] = iid

SKIP = ["boss", "mega", "coalition", "military", "ghillie", "acid", "skull", "mime", "fallback", "premium", "police", "spec"]
def cid(pl):
    parts = pl.rstrip("/").split("/")
    return id_of.get(parts[-2], 999999) if len(parts) >= 2 else 999999

# master: zombies should wear LOW-ID (base-game) clothing, not the high-id DLC packs. Sort by ID, take the lowest.
shirts, pants = [], []
for path, obj in dict(env.container).items():
    if obj.type.name != "Texture2D":
        continue
    pl = str(path).lower(); fn = pl.rsplit("/", 1)[-1]
    if any(s in pl for s in SKIP):
        continue
    if fn == "shirt.png":
        shirts.append((cid(pl), pl, obj))
    elif fn == "pants.png":
        pants.append((cid(pl), pl, obj))
shirts.sort(key=lambda t: t[0]); pants.sort(key=lambda t: t[0])
shirts = shirts[:8]; pants = pants[:8]
print("picked shirts:", [(s[0], s[1].split('/')[-2]) for s in shirts])
print("picked pants:", [(p[0], p[1].split('/')[-2]) for p in pants])

face = None
for path, obj in dict(env.container).items():
    if obj.type.name == "Texture2D" and "faces/19/" in str(path).lower():
        face = obj.read().image.convert("RGBA"); break
SKINS = [(150, 158, 128), (140, 150, 120), (158, 150, 132), (132, 145, 118), (146, 156, 122), (152, 148, 130)]
FX = (int(0.254 * 128), int(0.371 * 128)); FY = (int(0.563 * 128), int(0.625 * 128))
face_r = face.resize((FX[1] - FX[0], FY[1] - FY[0]), Image.NEAREST)
N = 6
for i in range(N):
    atlas = Image.new("RGBA", (128, 128), SKINS[i % len(SKINS)] + (255,))
    atlas.alpha_composite(pants[i % len(pants)][2].read().image.convert("RGBA"))
    atlas.alpha_composite(shirts[i % len(shirts)][2].read().image.convert("RGBA"))
    atlas.alpha_composite(face_r, (FX[0], FY[0]))
    atlas.save(os.path.join(d, f"zombie_atlas_{i}.png"))
print(f"baked {N} low-id variants -> {d}")
