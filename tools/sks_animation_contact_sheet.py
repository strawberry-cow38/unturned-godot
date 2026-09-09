#!/usr/bin/env python3
"""Contact-sheet actual Godot captures (Pillow); no synthetic animation frames.

Pass the same comma-separated frame list as UG_VMCAPS. Raw PNGs remain intact.
The sheet crops only the renderer's overscan to the actual first-person screen.
"""
import argparse
import json
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageOps


def font(size):
    return ImageFont.truetype('/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf', size)


def make_sheet(directory, frames, action, output, start=60, selected=None):
    available = [(i, frame) for i, frame in enumerate(frames)
                 if (directory / f'vpraw_{i:02}.png').exists() and (selected is None or i in selected)]
    assert available, 'No captures available'
    columns, width, height = 4, 640, 405
    canvas = Image.new('RGB', (columns * width, 100 + math.ceil(len(available) / columns) * height), '#14191f')
    draw = ImageDraw.Draw(canvas)
    draw.text((22, 14), f'SKS / {action.upper()} / first-person Godot Vulkan captures', fill='white', font=font(27))
    draw.text((22, 57), '25 fps playback. Clean viewport cropped to the screen; source PNGs preserved.', fill='#a7b5c2', font=font(19))
    for tile, (index, frame) in enumerate(available):
        raw = Image.open(directory / f'vpraw_{index:02}.png').convert('RGBA')
        sw, sh = round(raw.width / 1.15), round(raw.height / 1.45)
        x, y = (raw.width - sw) // 2, (raw.height - sh) // 2
        view = raw.crop((x, y, x + sw, y + sh))
        background = Image.new('RGBA', view.size, '#252d37')
        background.alpha_composite(view)
        view = ImageOps.contain(background.convert('RGB'), (width - 12, height - 48), Image.Resampling.LANCZOS)
        x, y = (tile % columns) * width, 100 + (tile // columns) * height
        canvas.paste(view, (x + 6, y + 40))
        state_file = directory / f'sks_state_{index:02}.json'
        state = json.loads(state_file.read_text()) if state_file.exists() else None
        t = state['clip_time'] if state and state['action'] else (frame - start) / 25
        label = f'f{frame:03} | ' + (f'{t:.2f}s' if (state and state['action']) or frame >= start else 'equip / ready')
        if action == 'handling':
            if frame < start:
                phase = 'equip / ready'
            elif frame < start + 35:
                phase = f'rack {t:.2f}s'
            elif frame < start + 48:
                phase = 'ready'
            elif frame < start + 78:
                phase = f'sprint carry {(frame - start - 48) / 25:.2f}s'
            else:
                phase = f'sprint return {(frame - start - 78) / 25:.2f}s'
            label = f'f{frame:03} | {phase}'
        if state and state['action'] == 'reload':
            label += f" | {state['visible_rounds']} rounds on clip"
        draw.text((x + 12, y + 9), label, fill='white', font=font(18))
    canvas.save(output)
    print(output)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('directory', type=Path)
    p.add_argument('--frames', required=True)
    p.add_argument('--action', default='reload')
    p.add_argument('--start', type=int, default=60)
    p.add_argument('--select', help='Optional comma-separated capture indices')
    p.add_argument('--output', type=Path)
    a = p.parse_args()
    make_sheet(a.directory, [int(x) for x in a.frames.split(',')], a.action,
               a.output or a.directory / 'contact_sheet.png', a.start,
               set(map(int, a.select.split(','))) if a.select else None)


if __name__ == '__main__':
    main()
