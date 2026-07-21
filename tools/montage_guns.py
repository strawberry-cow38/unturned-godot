#!/usr/bin/env python3
"""montage_guns.py -- render a batch of gun viewmodels (ADS frame) into one contact sheet, so a whole
arsenal's aim/positioning can be eyeballed in a single image. Renders each gun to a .avi via the --vm
harness (the gun composites in the movie, not the still), pulls the ADS frame, labels + grids them.
Run on the box: python tools\\montage_guns.py [gun1 gun2 ...]  (default = the aim-fixed arsenal + eaglefire ref)."""
import subprocess, os, sys, imageio.v2 as iio
from PIL import Image, ImageDraw
WS = "C:/claude-workspace"
GAME = r"C:\claude-workspace\unturned-godot\game"
ADS_FRAME = 66   # equip settles ~49, ADS ~57-87 -> 66 is mid-ADS (sights centered if the aim hook is right)
GUNS = sys.argv[1:] or ("eaglefire zubeknakov timberwolf sabertooth snayperskya augewehr bluntforce "
                        "heartbreaker sportshot schofield hawkhound dragonfang shadowstalker honeybadger "
                        "crossbow nightraider nykorev grizzly").split()
for g in GUNS:
    avi = f"{WS}/g_{g}.avi"
    subprocess.run(["godot", "--rendering-driver", "opengl3", "--write-movie", avi, "--fixed-fps", "30",
                    "--quit-after", "72", "--", "--vm=" + WS, "--gun=" + g], cwd=GAME, capture_output=True)
th, cols = (360, 203), 4
tiles = []
for g in GUNS:
    avi = f"{WS}/g_{g}.avi"
    im = None
    if os.path.exists(avi):
        try: im = Image.fromarray(iio.get_reader(avi).get_data(ADS_FRAME)).resize(th)
        except Exception as e: print("READ FAIL", g, e)
    tiles.append((g, im))
rows = (len(tiles) + cols - 1) // cols
sheet = Image.new("RGB", (cols * th[0], rows * th[1]), (28, 28, 30))
d = ImageDraw.Draw(sheet)
for i, (g, im) in enumerate(tiles):
    x, y = (i % cols) * th[0], (i // cols) * th[1]
    if im: sheet.paste(im, (x, y))
    d.rectangle([x, y, x + th[0] - 1, y + th[1] - 1], outline=(70, 70, 70))
    d.text((x + 6, y + 5), g, fill=(255, 235, 0))
sheet.save(f"{WS}/sheet.png")
print("SHEET", sheet.size, "guns", len(tiles))
