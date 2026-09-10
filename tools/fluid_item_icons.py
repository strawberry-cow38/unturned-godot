#!/usr/bin/env python3
"""Render all twelve inventory icons through shot.py, then tile them for review."""
import argparse
import os
import subprocess
import sys
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sheet-only', action='store_true', help='tile the existing rendered icons')
    parser.add_argument('--devices', default='9111,9112,9113,9114,9115,9116,9117,9118,9119,9120,9121',
                        help='comma-separated IDs to render; signed-off 9110 is always preserved')
    args = parser.parse_args()
    devices={int(x) for x in args.devices.split(',')}
    if 9110 in devices: parser.error('9110 icon is frozen')
    sheet = Image.new('RGB', (4*280, 3*292), (68, 73, 77))
    draw = ImageDraw.Draw(sheet)
    for i, item in enumerate(range(9110, 9122)):
        path = ROOT/f'game/content/items/icons/{item}.png'
        if not args.sheet_only and item in devices:
            subprocess.run([sys.executable, str(ROOT/'tools/shot.py'), 'fluidicon', '-o', str(path)],
                           env=dict(os.environ, DEVICE=str(item)), check=True)
        icon = Image.open(path).convert('RGBA')
        x, y = (i%4)*280, (i//4)*292
        sheet.paste(icon, (x+12, y+8), icon)
        draw.text((x+12, y+272), str(item), fill='white')
    sheet.save(ROOT/'notes/fluid_art/item_icons.png')


if __name__ == '__main__':
    main()
