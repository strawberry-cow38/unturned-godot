# rip_faces.py -- export the 32 retail player faces (core.masterbundle Items/Faces/<n>/Texture.png + Emission.png) to game/content/faces/. Run on the box from tools/ (paths below are the 4080 layout).
import UnityPy, os, re, sys
bundle = r"C:/Program Files (x86)/Steam/steamapps/common/Unturned/Bundles/core.masterbundle"
out = r"C:/claude-workspace/unturned-godot/game/content/faces"
os.makedirs(out, exist_ok=True)
env = UnityPy.load(bundle)
n = 0
for path, obj in env.container.items():
    m = re.search(r"/faces/(\d+)/(texture|emission)\.png$", path.lower())
    if not m or obj.type.name != "Texture2D": continue
    idx, kind = int(m.group(1)), m.group(2)
    d = obj.read()
    img = d.image
    # emission: skip if fully black (nothing glows)
    if kind == "emission":
        ext = img.convert("RGB").getextrema()
        if all(hi == 0 for _, hi in ext): continue
    fn = os.path.join(out, f"face_{idx}.png" if kind == "texture" else f"face_{idx}_emission.png")
    img.save(fn); n += 1
    print(path, d.m_Width, d.m_Height, "->", os.path.basename(fn))
print("faces exported:", n)
