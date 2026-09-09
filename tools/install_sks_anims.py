#!/usr/bin/env python3
"""Append authored SKS clips without reserializing a single existing rig byte.

python3 tools/install_sks_anims.py --clips /tmp/snow/guns/sks_anims.json
The pristine reference is deliberately mandatory for verification (default below).
"""
import argparse
import hashlib
import json
import math
from pathlib import Path

PRISTINE_SHA = 'bfce460116ff9217cb08fc6ac0ca037f4cbeea9b7bc48ab113cfd3aad06f95a9'
ROOT = Path(__file__).resolve().parents[1]
DECODER = json.JSONDecoder()


def members(text, start=0):
    """Return ordered member name -> raw value span, and the closing brace offset."""
    assert text[start] == '{'
    at = start + 1
    spans = {}
    while True:
        while text[at].isspace():
            at += 1
        if text[at] == '}':
            return spans, at
        key, at = DECODER.raw_decode(text, at)
        assert key not in spans, f'duplicate key: {key}'
        while text[at].isspace():
            at += 1
        assert text[at] == ':'
        at += 1
        while text[at].isspace():
            at += 1
        begin = at
        _, at = DECODER.raw_decode(text, at)
        spans[key] = (begin, at)
        while text[at].isspace():
            at += 1
        if text[at] == ',':
            at += 1
        else:
            assert text[at] == '}'
            return spans, at


def verify(pristine, current):
    assert hashlib.sha256(pristine).hexdigest() == PRISTINE_SHA
    p, c = pristine.decode('utf-8'), current.decode('utf-8')
    ps, _ = members(p)
    cs, _ = members(c)
    assert list(ps) == list(cs), 'top-level keys/order changed'
    unchanged_sections = 0
    for key in ps:
        if key != 'anims':
            assert p[slice(*ps[key])] == c[slice(*cs[key])], key
            unchanged_sections += 1
    pa, pend = members(p, ps['anims'][0])
    ca, cend = members(c, cs['anims'][0])
    assert len(pa) == 569
    assert list(ca)[:len(pa)] == list(pa), 'old clip keys/order changed'
    for key, span in pa.items():
        assert p[slice(*span)] == c[slice(*ca[key])], key
    extra = list(ca)[len(pa):]
    assert all(key.startswith('Sks_') for key in extra), extra
    # Stronger than decoded equality: removing just the insertion recovers the
    # ENTIRE pristine file, including all whitespace and every existing key.
    assert c[:pend] + c[cend:] == p, 'bytes outside the insertion changed'
    return {'pre_existing_clips_identical': f'{len(pa)}/{len(pa)}',
            'non_anims_sections_identical': unchanged_sections,
            'entire_pristine_recovered_by_removing_insertion': True,
            'pristine_sha256': PRISTINE_SHA, 'added_clips': extra}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pristine', type=Path, default=Path('/tmp/snow/guns/rig.json.PRISTINE'))
    parser.add_argument('--rig', type=Path, default=ROOT / 'game/content/rig.json')
    parser.add_argument('--clips', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    pristine = args.pristine.read_bytes()
    current = args.rig.read_bytes()
    # Never overwrite another contributor's edit, even when re-running authoring.
    report = verify(pristine, current)
    if args.clips:
        clips = json.loads(args.clips.read_bytes())
        rig = json.loads(pristine)
        bone_names = {bone['name'] for bone in rig['bones']}
        for name, clip in clips.items():
            assert name.startswith('Sks_') and name not in rig['anims']
            assert clip['fps'] == 30.0 and clip['length'] > 0 and clip['loop'] is False
            assert set(clip['tracks']) == bone_names
            for bone, tracks in clip['tracks'].items():
                for kind, keys in tracks.items():
                    previous = -1
                    for key in keys:
                        assert all(math.isfinite(v) for v in key), (name, bone, kind)
                        assert previous < key[0] <= clip['length'] + 1e-6, (name, bone, kind, key[0])
                        previous = key[0]
                        if kind == 'rot':
                            assert len(key) == 5 and abs(sum(v*v for v in key[1:]) - 1) < 1e-4
                        else:
                            assert len(key) == 4
        text = pristine.decode('utf-8')
        root, _ = members(text)
        _, end = members(text, root['anims'][0])
        insertion = ', ' + ', '.join(json.dumps(k) + ': ' + json.dumps(v, allow_nan=False) for k, v in clips.items())
        updated = (text[:end] + insertion + text[end:]).encode('utf-8')
        report = verify(pristine, updated)
        temp = args.rig.with_suffix('.sks.tmp')
        temp.write_bytes(updated)
        assert args.rig.read_bytes() == current, 'rig changed during authoring'
        temp.replace(args.rig)
        report = verify(pristine, args.rig.read_bytes())
        report['clips'] = {k: {'fps': v['fps'], 'length': v['length']} for k, v in clips.items()}
    print(f"{report['pre_existing_clips_identical']} pre-existing clips identical")
    print(json.dumps(report, indent=2))
    if args.report:
        args.report.write_text(json.dumps(report, indent=2) + '\n')


if __name__ == '__main__':
    main()
