#!/usr/bin/env python3
"""montage_guns.py -- render a batch of gun viewmodels (ADS frame) into one contact sheet, so a whole
arsenal's aim/positioning can be eyeballed in a single image. Renders each gun to a .avi via the --vm
harness (the gun composites in the movie, not the still), pulls its ADS frame, labels + grids them.

PER-GUN ADS FRAME: guns have different equip-clip lengths (eaglefire 1.633s, nykorev 2.3s), and the --vm
script only aims AFTER the equip finishes. A fixed frame catches long-equip guns still mid-equip (looks
"off"). So parse each gun's "[vm] equip length" from stdout and grab a frame ~mid-ADS (equip + settle + hold).

Run on the box: python tools\\montage_guns.py [gun1 gun2 ...]  (default = the aim-fixed arsenal + eaglefire ref)."""
import subprocess, os, sys, re, imageio.v2 as iio
from PIL import Image, ImageDraw
WS = "C:/claude-workspace"
GAME = r"C:\claude-workspace\unturned-godot\game"
GUNS = sys.argv[1:] or ("eaglefire zubeknakov timberwolf sabertooth snayperskya augewehr bluntforce "
                        "heartbreaker sportshot schofield hawkhound dragonfang shadowstalker honeybadger "
                        "crossbow nightraider nykorev grizzly").split()

def render_and_frame(gun):
    """render the gun's --vm .avi, parse its equip length, return the mid-ADS frame index."""
    avi = f"{WS}/g_{gun}.avi"
    r = subprocess.run(["godot", "--rendering-driver", "opengl3", "--write-movie", avi, "--fixed-fps", "30",
                        "--quit-after", "100", "--", "--vm=" + WS, "--gun=" + gun],
                       cwd=GAME, capture_output=True, text=True)
    m = re.search(r"equip .*length = ([\d.]+)s", r.stdout or "")
    eq = float(m.group(1)) if m else 1.633
    return min(97, int(eq * 30) + 13)   # equip-done frame + 8-frame aim settle + ~5 into the 30-frame ADS hold

th, cols = (360, 203), 4
tiles = []
for g in GUNS:
    fr = render_and_frame(g)
    avi = f"{WS}/g_{g}.avi"
    im = None
    if os.path.exists(avi):
        try: im = Image.fromarray(iio.get_reader(avi).get_data(fr)).resize(th)
        except Exception as e: print("READ FAIL", g, e)
    tiles.append((f"{g} f{fr}", im))
rows = (len(tiles) + cols - 1) // cols
sheet = Image.new("RGB", (cols * th[0], rows * th[1]), (28, 28, 30))
d = ImageDraw.Draw(sheet)
for i, (label, im) in enumerate(tiles):
    x, y = (i % cols) * th[0], (i // cols) * th[1]
    if im: sheet.paste(im, (x, y))
    d.rectangle([x, y, x + th[0] - 1, y + th[1] - 1], outline=(70, 70, 70))
    d.text((x + 6, y + 5), label, fill=(255, 235, 0))
sheet.save(f"{WS}/sheet.png")
print("SHEET", sheet.size, "guns", len(tiles))
