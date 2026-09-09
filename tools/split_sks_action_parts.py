#!/usr/bin/env python3
"""Separate existing SKS OBJ faces into static body and reciprocating carrier.

Every vertex/normal/UV and face is copied verbatim; the original stays intact.
The resulting two parts reconstruct exactly the original closed rifle.
"""
from pathlib import Path
import hashlib
import json

ROOT = Path(__file__).resolve().parents[1]
CONTENT = ROOT / 'game/content'
MOVING = {'exposed_bolt_carrier', 'charging_handle'}


def main():
    source = CONTENT / 'sks_gun.txt'
    original = source.read_bytes()
    lines = original.decode().splitlines(keepends=True)
    outputs = {False: [], True: []}
    faces = {False: [], True: []}
    group = None
    groups = set()
    original_faces = []
    for line in lines:
        if line.startswith(('o ', 'g ')):
            group = line.strip().split(maxsplit=1)[1]
            groups.add(group)
            outputs[group in MOVING].append(line)
        elif line.startswith('f '):
            outputs[group in MOVING].append(line)
            faces[group in MOVING].append(line)
            original_faces.append(line)
        else:
            for out in outputs.values():
                out.append(line)
    assert MOVING <= groups
    assert sorted(faces[False] + faces[True]) == sorted(original_faces)
    assert source.read_bytes() == original
    for moving, name in [(False, 'sks_action_body.txt'), (True, 'sks_action_bolt.txt')]:
        (CONTENT / name).write_text(''.join(outputs[moving]))
    print(json.dumps({'original_sha256': hashlib.sha256(original).hexdigest(),
                      'static_faces': len(faces[False]), 'moving_faces': len(faces[True]),
                      'all_original_faces_preserved': True, 'moving_groups': sorted(MOVING)}, indent=2))


if __name__ == '__main__':
    main()
