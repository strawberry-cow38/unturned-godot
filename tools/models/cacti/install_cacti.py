#!/usr/bin/env python3
"""Install the approved 14 cactus models as an asset library, without placing any.

Usage: python3 tools/models/cacti/author_variants.py
       python3 tools/models/cacti/install_cacti.py
"""
from pathlib import Path
import argparse,hashlib,json,shutil

ROOT=Path(__file__).resolve().parent
EXPECTED={f'Cactus_{family}_{variant}' for family in ('Column','Branched','Barrel','Paddles','Cluster') for variant in (0,1,2)}-{'Cactus_Barrel_1'}
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()

def install(repo,source):
    manifest=json.loads((source/'manifest.json').read_text())
    entries=manifest['assets']
    if {e['name'] for e in entries}!=EXPECTED or len(entries)!=14:
        raise ValueError('Expected exactly the 14 approved models; low/broad Barrel_1 is excluded.')
    output=repo/'game/content/resources'
    output.mkdir(parents=True,exist_ok=True)
    protected=output/'resources.txt'
    original=sha(protected)
    palette=source/'models/Cactus_Palette.png'
    runtime=[]
    for e in entries:
        name=e['name'];native=source/'models'/(name+'.obj')
        lines=native.read_text().splitlines()
        if any(line.startswith('o ') and 'spine' in line.lower() for line in lines):
            raise ValueError(f'Spine geometry remains in {name}')
        # Runtime materials are bound by ResourceField, not Wavefront MTL references. Geometry is untouched.
        text='\n'.join(line for line in lines if not line.startswith(('mtllib ','usemtl ')))+'\n'
        mesh=output/(name+'_0.obj');texture=output/(name+'_0_tex.png')
        mesh.write_text(text);shutil.copyfile(palette,texture)
        runtime.append({'name':name,'family':e['family'],'variant':e['variant'],'label':e['label'],
                        'part_count':1,'obj':mesh.name,'texture':texture.name,'triangles':e['triangles'],
                        'height_above_ground_m':e['height_above_ground_m'],'bounds':e['bounds'],
                        'obj_sha256':sha(mesh),'texture_sha256':sha(texture)})
    # Explicitly removed by the owner. Do not renumber surviving variant B (_2).
    for suffix in ('_0.obj','_0_tex.png'):
        unwanted=output/('Cactus_Barrel_1'+suffix)
        if unwanted.exists():unwanted.unlink()
    (output/'cacti.txt').write_text('# Approved cactus MODEL inventory, not a spawn manifest.\n'
                                  '# Single-part resource naming; deliberately NOT appended to resources.txt.\n'
                                  +'\n'.join(e['name']+' 1 NONE' for e in runtime)+'\n')
    result={'schema':'unturned-godot.cactus-model-library.v1','models':14,
            'excluded':['Cactus_Barrel_1'],'native_up':'+Y','metres_scale':1,'ground_datum_y':0,
            'no_recentre':True,'spikes':0,'spawning':'none; inventory only; no bin files or gameplay IDs',
            'resource_manifest_unchanged_sha256':original,'entries':runtime}
    (output/'cacti_manifest.json').write_text(json.dumps(result,indent=2)+'\n')
    assert sha(protected)==original,'Do not change resource manifest order/implicit network indices.'
    assert not any((output/(e['name']+'.bin')).exists() for e in runtime),'Shipping models must not add placements.'
    return result

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--repo',type=Path,default=ROOT.parents[2])
    parser.add_argument('--source',type=Path,default=ROOT)
    args=parser.parse_args()
    result=install(args.repo.resolve(),args.source.resolve())
    print(f"Installed {len(result['entries'])} spike-free cactus models; no world placements or resource-manifest changes.")