"""pitch_sweep.py -- render a gun's --vm at a range of UG_GUNPITCH (mesh-local pitch) values into one labeled
strip, to find the pitch that LEVELS a tilted extracted-arsenal gun. The default frame is the ADS frame.
Run on the box: python tools\\pitch_sweep.py [gun] [frame] [p0 p1 ...]"""
import subprocess, os, sys, imageio.v2 as iio
from PIL import Image, ImageDraw
GAME = r"C:\claude-workspace\unturned-godot\game"; WS = "C:/claude-workspace"
gun = sys.argv[1] if len(sys.argv) > 1 else "cobra"
frame = int(sys.argv[2]) if len(sys.argv) > 2 else 45
pitches = [int(x) for x in sys.argv[3:]] or [-25, -15, -5, 5, 15, 25]
tiles = []
for p in pitches:
    env = dict(os.environ, UG_GUNPITCH=str(p))
    avi = f"{WS}/sw_{gun}_{p}.avi"
    subprocess.run(["godot", "--rendering-driver", "opengl3", "--write-movie", avi, "--fixed-fps", "30",
                    "--quit-after", str(frame + 8), "--", "--vm=" + WS, "--gun=" + gun],
                   cwd=GAME, env=env, capture_output=True)
    im = None
    try: im = Image.fromarray(iio.get_reader(avi).get_data(frame)).resize((360, 203))
    except Exception as e: print("FAIL", p, e)
    tiles.append((f"{gun} {p:+d} f{frame}", im))
sheet = Image.new("RGB", (len(tiles) * 360, 203), (28, 28, 30)); d = ImageDraw.Draw(sheet)
for i, (label, im) in enumerate(tiles):
    if im: sheet.paste(im, (i * 360, 0))
    d.rectangle([i * 360, 0, i * 360 + 359, 202], outline=(70, 70, 70)); d.text((i * 360 + 6, 5), label, fill=(255, 235, 0))
sheet.save(f"{WS}/sweep.png"); print("SWEEP", sheet.size)
