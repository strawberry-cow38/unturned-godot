from PIL import Image
import UnityPy, os
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(MB)
d = r"C:\claude-workspace\zombie_skin"
os.makedirs(d, exist_ok=True)

# real ZombieClothing randomises shirt/pants per zombie. Pull a spread of civilian-ish tops/bottoms
# (skip military / boss / themed) so the horde isn't all in one uniform.
SKIP = ["boss", "mega", "coalition", "military", "ghillie", "acid", "skull", "mime", "fallback", "premium"]
shirts, pants = [], []
for path, obj in dict(env.container).items():
    if obj.type.name != "Texture2D":
        continue
    pl = str(path).lower(); fn = pl.rsplit("/", 1)[-1]
    if any(s in pl for s in SKIP):
        continue
    if fn == "shirt.png" and len(shirts) < 8:
        shirts.append((pl, obj))
    elif fn == "pants.png" and len(pants) < 8:
        pants.append((pl, obj))

face = None
for path, obj in dict(env.container).items():
    if obj.type.name == "Texture2D" and "faces/19/" in str(path).lower():
        face = obj.read().image.convert("RGBA"); break

SKINS = [(150, 158, 128), (140, 150, 120), (158, 150, 132), (132, 145, 118), (146, 156, 122), (152, 148, 130)]
FX = (int(0.254 * 128), int(0.371 * 128)); FY = (int(0.563 * 128), int(0.625 * 128))  # head-front quad
face_r = face.resize((FX[1] - FX[0], FY[1] - FY[0]), Image.NEAREST)

N = 6
for i in range(N):
    atlas = Image.new("RGBA", (128, 128), SKINS[i % len(SKINS)] + (255,))
    atlas.alpha_composite(pants[i % len(pants)][1].read().image.convert("RGBA"))
    atlas.alpha_composite(shirts[i % len(shirts)][1].read().image.convert("RGBA"))
    atlas.alpha_composite(face_r, (FX[0], FY[0]))
    atlas.save(os.path.join(d, f"zombie_atlas_{i}.png"))
    print(f"atlas_{i}: shirt={shirts[i % len(shirts)][0].split('/')[-2]} pants={pants[i % len(pants)][0].split('/')[-2]}")
print(f"baked {N} variants")
