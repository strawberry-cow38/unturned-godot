#!/usr/bin/env python3
"""Shoot every fluid device from several sides and tile the results into one sheet per device.

strawberry 2026-09-10: "give astra multiple angles of its props."

One three-quarter view is exactly the wrong number of views for this job -- it hides the far side, and
the far side is where the open ends, the unattached spigots and the seams between a flat pipe and a
round body actually are. Every finding in the last pass that a render could have caught was on a face
the single view did not show.
"""
import os, subprocess, sys
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / 'notes/fluid_angles')
DEVICES = [int(x) for x in os.environ.get('FLUID_DEVICES',
           '9110,9111,9112,9113,9114,9115,9116,9117,9119,9120,9121').split(',')]
# front, three-quarter, side, and behind -- plus one low angle, because the underside of a skid and the
# bottom of a bowl are where an unclosed solid shows and no level view ever looks there.
# Sixth view looks UP from below the base with the ground hidden: +6 degrees still looks down.
VIEWS = [(0, 22), (35, 28), (90, 22), (200, 26), (145, 6), (145, -22)]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = ROOT / '.shots/fluid_angles'
    tmp.mkdir(parents=True, exist_ok=True)
    for dev in DEVICES:
        frames = []
        for az, el in VIEWS:
            png = tmp / f'{dev}_{az}_{el}.png'
            png.unlink(missing_ok=True)  # a failed render must never reuse a previous pass's frame
            env = dict(os.environ, DEVICE=str(dev), ANGLE=str(az), ELEV=str(el),
                       UG_FLUIDUNDERSIDE='1' if el < 0 else '0')
            r = subprocess.run([sys.executable, str(ROOT / 'tools/shot.py'), 'fluiddevice', '-o', str(png)],
                               env=env, capture_output=True, text=True, timeout=400)
            if r.returncode == 0 and png.exists():
                frames.append(Image.open(png).convert('RGB'))
            else:
                print(f'  FAILED {dev} @ {az}deg: {r.stderr.strip()[-200:]}', file=sys.stderr)
        if len(frames) != len(VIEWS):
            raise RuntimeError(f'{dev}: only {len(frames)} of {len(VIEWS)} fresh views rendered')
        w, h = frames[0].size
        sheet = Image.new('RGB', (w * len(frames), h), (90, 100, 82))
        for i, f in enumerate(frames):
            sheet.paste(f, (i * w, 0))
        sheet = sheet.resize((sheet.width // 2, sheet.height // 2), Image.LANCZOS)
        sheet.save(OUT / f'{dev}_angles.png')
        print(f'{dev}: {len(frames)} views -> {OUT / f"{dev}_angles.png"}', flush=True)


if __name__ == '__main__':
    main()
