#!/usr/bin/env python3
"""Regenerate every horse/fleet review view through shot.py, with fresh rig caches."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
VIEWS = [
    ('deer-side', 'deer', 'side', 'Idle', 0),
    ('pig-side', 'pig', 'side', 'Idle', 0),
    ('cow-rest-side', 'cow', 'side', 'rest', 0),
    ('fleet-top', 'deer,pig,cow', 'top', 'Idle', 0),
    *[(f'horse-{v}', 'horse,cow,deer', v, 'Idle', 0)
      for v in ('side', 'other', 'top', 'quarter', 'front', 'rear')],
    ('horse-walk', 'horse,cow,deer', 'quarter', 'Walk', .16),
    ('horse-eat', 'horse,cow,deer', 'side', 'Eat', 1.8),
    ('horse-low', 'horse', 'low', 'Idle', 0),
    ('horse-lowrear', 'horse', 'lowrear', 'Idle', 0),
    ('horse-walk-low', 'horse', 'low', 'Walk', .16),
    ('horse-eat-lowrear', 'horse', 'lowrear', 'Eat', 1.8),
    ('horse-run-low', 'horse', 'low', 'Run', .133333),
    ('horse-startle-lowrear', 'horse', 'lowrear', 'Startle', .316667),
]


def main():
    output = ROOT/'notes/horse_views'
    work = ROOT/'.shots/horse-pass2'
    work.mkdir(parents=True, exist_ok=True)
    # A fresh per-run XDG directory bypasses the loader's file-size-only cache.
    data = tempfile.mkdtemp(prefix='render-data-', dir=work)
    for name, species, camera, clip, time in VIEWS:
        stage = work/name
        stage.mkdir(exist_ok=True)
        env = dict(os.environ, ANIMAL=species, UG_ANIMALCAM=camera,
                   UG_ANIMALCLIP=clip, UG_ANIMALTIME=str(time), UG_ANIMALYAW='270',
                   XDG_DATA_HOME=data, XDG_CONFIG_HOME=str(work/'config'))
        path = stage/(name+'.png')
        path.unlink(missing_ok=True)
        subprocess.run([sys.executable, str(ROOT/'tools/shot.py'), 'animal', '-o', str(path)],
                       env=env, cwd=ROOT, check=True)
        shutil.copy2(path, output/path.name)
        print(f'SAVED {path.name}: {camera}, {clip}@{time}', flush=True)


if __name__ == '__main__':
    main()
