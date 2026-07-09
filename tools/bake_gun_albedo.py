"""Bake the gun paint colours into the albedo.

Unturned gun albedos are white (paintable) + PURE BLACK (metal). The metal being pure
black renders flat-black under any light/metallic/tint (the real game uses a compiled
gun shader we don't have). So we bake the final look: for each pixel, brightness t
(0 = black metal .. 1 = white paintable) picks lerp(gunmetal, paint_colour). Runs on
the 4080 (PIL); reads the pristine bundle albedos from guntex\, writes game/content\.
"""
from PIL import Image

GUNTEX = r"C:\claude-workspace\guntex"
CONTENT = r"C:\claude-workspace\unturned-godot\game\content"
METAL = (0.16, 0.16, 0.17)   # visible dark gunmetal for the black metal regions
PAINTS = {
    "eaglefire":   (0.42, 0.40, 0.38),   # neutral gunmetal furniture
    "maplestrike": (0.44, 0.40, 0.28),   # tan/olive
    "masterkey":   (0.46, 0.30, 0.15),   # wood stock/pump
}

for gun, paint in PAINTS.items():
    img = Image.open(rf"{GUNTEX}\{gun}__Albedo_Base.png").convert("RGB")
    w, h = img.size
    px = img.load()
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            t = (r + g + b) / 765.0
            px[x, y] = tuple(int(255 * (METAL[i] * (1 - t) + paint[i] * t)) for i in range(3))
    img.save(rf"{CONTENT}\{gun}_albedo.png")
    print(gun, "baked", w, "x", h)
print("DONE")
