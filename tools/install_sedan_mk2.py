#!/usr/bin/env python3
"""Install approved V17 OBJ bytes under a new prefix; never touch stock sedan.
No mesh processing, rescaling, triangulation, texture conversion, or Godot invocation.
Use --check for a read-only byte/triangle/UV audit. Source roots may be supplied explicitly.
"""
import argparse
import hashlib
import json
from pathlib import Path


def sha(data):
    return hashlib.sha256(data).hexdigest()


def entries(manifest, art, frozen):
    for item in [manifest['frame'], *manifest['moving_parts'], *manifest['glass'],
                 *manifest['reused_parts'], *manifest['interior_parts']]:
        # Parts builder treats ANY name containing 'steer' as the rotating steering wheel.
        name = item['name'].replace('sedan_', 'sedan_mk2_', 1)
        if item['name'] == 'sedan_steering_column':
            name = 'sedan_mk2_column_support'
        yield art / item['file'], name + '.txt', item.get('triangles')
    yield frozen / 'sedan_palette.png', 'sedan_mk2_palette.png', None


def main():
    repo = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--art', type=Path, default=repo.parent / 'ug-vehicle-study/models/sedanV17')
    parser.add_argument('--frozen', type=Path, default=repo.parent / 'ug-vehicle-study/sources/sedan')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    if args.check:
        proof = json.loads((repo / 'docs/SEDAN_MK2_ASSETS.json').read_text())
        for entry in proof['assets']:
            dest = repo / 'game/content' / entry['file']
            if not dest.is_file() or sha(dest.read_bytes()) != entry['sha256']:
                raise SystemExit('Missing/different installed asset: ' + str(dest))
            if entry['triangles'] is not None:
                count = sum(line.startswith('f ') for line in dest.read_text().splitlines())
                assert count == entry['triangles'], dest
        for name, expected in proof['original_assets'].items():
            assert sha((repo / 'game/content' / name).read_bytes()) == expected, ('stock changed', name)
        print(f"{len(proof['assets'])} installed assets and {len(proof['original_assets'])} unchanged stock assets verified")
        if not (args.art / 'manifest.json').is_file():
            return # portable installed-asset audit; the private art workspace is not required
    manifest = json.loads((args.art / 'manifest.json').read_text())
    planned = []
    for source, name, triangles in entries(manifest, args.art, args.frozen):
        assert name.startswith('sedan_mk2_') and '/' not in name
        data = source.read_bytes()
        if triangles is not None:
            text = data.decode('utf-8')
            faces = [line.split()[1:] for line in text.splitlines() if line.startswith('f ')]
            assert len(faces) == triangles and all(len(f) == 3 for f in faces), source
        planned.append((source, repo / 'game/content' / name, data))
    # All source validation happens before any writes. Diagnostics/no-lips/housing audit meshes
    # are deliberately excluded: approved frame already contains the complete housings.
    for source, dest, data in planned:
        if args.check:
            if not dest.is_file() or dest.read_bytes() != data:
                raise SystemExit('Missing/different installed asset: ' + str(dest))
        else:
            dest.write_bytes(data)
        print(f'{dest.name}\t{sha(data)}\t{source.name}')
    print(f'{len(planned)} exact assets {"verified" if args.check else "installed"}; '
          f'manifest SHA256={sha((args.art / "manifest.json").read_bytes())}')


if __name__ == '__main__':
    main()
