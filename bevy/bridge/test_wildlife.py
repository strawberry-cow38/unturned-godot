#!/usr/bin/env python3
"""Source wildlife behavior over the real managed authority/client session."""
import json
import math
import struct
import tempfile
import unittest
from pathlib import Path
import test_bridge

class WildlifeTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    reject=test_bridge.BridgeTests.reject
    pump=test_bridge.BridgeTests.pump

    def fauna(self, folder, points, animal=7):
        directory=Path(folder)/'Spawns';directory.mkdir(parents=True,exist_ok=True)
        name=b'Wild'; tier=b'Common'
        raw=bytearray([3,1,255,255,255,len(name)])+name+struct.pack('<H',0)+bytes([1,len(tier)])+tier+struct.pack('<fBH',1.,1,animal)
        raw+=struct.pack('<H',len(points))
        for x,y,z in points:raw+=struct.pack('<Bfff',0,x,y,-z)
        (directory/'Fauna.dat').write_bytes(raw)

    def ground(self, layers=None, origin=(-64,-64), spacing=(4,4), side=33):
        self.call('worldGeometry',terrainPatches=[dict(origin=list(origin),cells=side-1,spacing=abs(spacing[0]),heights=[0.]*(side*side))],
            terrainLayers=[dict(origin=list(origin),width=side,height=side,spacing=list(spacing),layers=layers or [2]*(side*side))])

    def test_original_fauna_streams_deterministically_and_wanders(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.fauna(folder,[(0,0,-8),(200,0,0)])
            r=self.call('worldWildlife',mapRoot=folder)['result']
            self.assertEqual(2,r['points']);self.assertTrue(r['sourceExists'])
            state=self.pump(6);self.assertEqual(1,len(state['animals']))
            animal=state['animals'][0]
            self.assertEqual(7,animal['animalId']);self.assertEqual(170,animal['health'])
            self.assertEqual([0,0,-8],animal['position']);self.assertEqual(270,animal['rigYawFix'])
            self.assertEqual('horse_rig.json',animal['rig'])
            state=self.pump(500)
            self.assertNotEqual(animal['position'],state['animals'][0]['position'])
            self.assertEqual(170,state['animals'][0]['health'])

    def test_chunked_geometry_replacement_never_grounds_existing_animals_at_missing_height(self):
        self.call('worldGeometry',terrainPatches=[dict(origin=[-16,-16],cells=8,spacing=4,heights=[20.]*81)])
        with tempfile.TemporaryDirectory() as folder:
            self.fauna(folder,[(0,0,-3)]);self.call('worldWildlife',mapRoot=folder)
            before=self.pump()['animals'][0];self.assertEqual(20,before['hostPosition'][1])
            self.call('worldGeometry',replace=True,finalize=False)
            during=self.pump()['animals'][0]
            self.assertEqual(before['hostPosition'],during['hostPosition'])
            self.call('worldGeometry',replace=False,terrainPatches=[dict(origin=[-16,-16],cells=8,spacing=4,heights=[30.]*81)])
            after=self.pump()['animals'][0];self.assertEqual(before['netId'],after['netId']);self.assertEqual(30,after['hostPosition'][1])

    def test_actual_bullet_flee_death_and_persisted_corpse_without_loot(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.fauna(folder,[(0,0,-3)])
            self.call('worldWildlife',mapRoot=folder);state=self.pump(6)
            initial=state['animals'][0]
            self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[0,0,-1]))
            state=self.pump(6)
            hits=[e for e in state['events'] if e['kind']=='animalHit']
            self.assertEqual(1,len(hits));self.assertLess(state['animals'][0]['health'],170)
            self.assertTrue(any(e['kind']=='animalPanic' for e in state['events']))
            self.assertEqual('Run',state['animals'][0]['clip'])
            # The actual server validates aim and resolves each new ray against the moving capsule.
            for _ in range(20):
                animal=state['animals'][0]
                if animal['dead']:break
                p=animal['hostPosition'];origin=[0,1,0];direction=[p[0],p[1]+.9-1,p[2]]
                length=math.sqrt(sum(v*v for v in direction));direction=[v/length for v in direction]
                self.call('action',name='fire',args=dict(origin=origin,dir=direction));state=self.pump(10)
            animal=state['animals'][0];self.assertTrue(animal['dead'])
            self.assertEqual(initial['netId'],animal['netId']);self.assertEqual(1,len([e for e in state['events'] if e['kind']=='animalDied']))
            self.assertEqual([],state['worldItems'])
            path=str(Path(folder)/'save.json');self.call('save',path=path)
            saved=json.loads(Path(path).read_text())['BevyBridge']['Wildlife']['Animals'][0]['Brain']
            restored=self.call('load',path=path)['state']['animals'][0]
            self.assertTrue(restored['dead']);self.assertEqual(saved['CorpseRemaining']-6,restored['corpseRemainingTicks'])
            self.assertEqual(animal['deathImpulse'],restored['deathImpulse'])
            self.assertEqual([],self.call('snapshot')['state']['worldItems'])
            corpse_id=restored['netId'];state=self.pump(restored['corpseRemainingTicks']+30)
            self.assertEqual(1,len(state['animals']));self.assertFalse(state['animals'][0]['dead'])
            self.assertNotEqual(corpse_id,state['animals'][0]['netId'],'original point respawns after corpse cleanup and the next streaming sweep')
            self.assertEqual([],state['worldItems'])

    def test_bullets_block_on_world_and_melee_retains_original_proximity_cone(self):
        for blocked in [False,True]:
            with self.subTest(blocked=blocked),tempfile.TemporaryDirectory() as folder:
                self.call('init',starterKit=True,spawn=[0,0,0]);self.ground()
                if blocked:self.call('worldGeometry',replace=False,meshes=[dict(vertices=[[-3,0,-.6],[3,0,-.6],[3,4,-.6],[-3,4,-.6]],indices=[0,1,2,0,2,3])])
                self.fauna(folder,[(0,0,-1.5)]);self.call('worldWildlife',mapRoot=folder);self.pump()
                if blocked:
                    self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[0,0,-1]));state=self.pump(10)
                    self.assertEqual(170,state['animals'][0]['health'])
                    self.assertFalse(any(e['kind']=='animalHit' for e in state['events']))
                self.call('tick',input=dict(heldItemId=0));self.pump()
                self.call('action',name='melee',args=dict(strong=True,yawDegrees=0));state=self.pump(40)
                self.assertTrue(any(e['kind']=='animalHit' for e in state['events']),
                    'source animal cone has no ordinary-wall ray gate; typed resource/prop hits take precedence')

    def test_accepted_grenade_uses_original_falloff_and_world_occlusion(self):
        for blocked in [False,True]:
            with self.subTest(blocked=blocked),tempfile.TemporaryDirectory() as folder:
                self.call('init',starterKit=True);self.ground()
                if blocked:self.call('worldGeometry',replace=False,meshes=[dict(vertices=[[-20,0,-2],[20,0,-2],[20,20,-2],[-20,20,-2]],indices=[0,1,2,0,2,3])])
                self.fauna(folder,[(0,0,-3)]);self.call('worldWildlife',mapRoot=folder);self.pump()
                self.call('action',name='console',args=dict(text='give 254'));self.pump()
                self.call('tick',input=dict(heldItemId=254));self.pump()
                self.call('action',name='grenade',args=dict(origin=[0,.2,-1],velocity=[0,0,0],itemId=254));state=self.pump(170)
                self.assertTrue(any(e['kind']=='throwAccepted' for e in state['events']))
                self.assertEqual(not blocked,any(e['kind']=='animalHit' for e in state['events']))

    def test_source_sand_proxy_uses_reversed_rows_rounding_and_edge_clamp(self):
        self.ground()
        # Source PEI rows run toward negative Z. Nearest-grid midpoint ties round
        # to even, and positions beyond the map clamp to its edge layer.
        self.call('worldGeometry',replace=False,terrainLayers=[dict(origin=[0,0],width=2,height=2,spacing=[4,-4],layers=[5,2,2,5])])
        # Replace the default layer field so this is the actual authoritative map.
        self.call('worldGeometry',terrainPatches=[dict(origin=[-64,-64],cells=32,spacing=4,heights=[0.]*1089)],
            terrainLayers=[dict(origin=[0,0],width=2,height=2,spacing=[4,-4],layers=[5,2,2,5])])
        with tempfile.TemporaryDirectory() as folder:
            points=[(0,0,0),(4,0,0),(0,0,-4),(4,0,-4),(2,0,0),(2.01,0,0),(10,0,0),(10,0,-10)]
            self.fauna(folder,points);self.call('worldWildlife',mapRoot=folder)
            animals=self.pump(6)['animals'];self.assertEqual(4,len(animals))
            self.assertEqual({(4,0),(0,-4),(2.01,0),(10,0)},
                {(round(a['hostPosition'][0],2),round(a['hostPosition'][2],2)) for a in animals})

    def test_source_sight_cone_and_world_occlusion(self):
        for front,blocked,panic in [(True,False,True),(False,False,False),(True,True,False)]:
            with self.subTest(front=front,blocked=blocked),tempfile.TemporaryDirectory() as folder:
                self.call('init',starterKit=True);self.ground()
                if blocked:self.call('worldGeometry',replace=False,meshes=[dict(vertices=[[-3,0,1.5],[3,0,1.5],[3,4,1.5],[-3,4,1.5]],indices=[0,1,2,0,2,3])])
                self.fauna(folder,[(0,0,3 if front else -3)]);self.call('worldWildlife',mapRoot=folder)
                state=self.pump(15);self.assertEqual(panic,any(e['kind']=='animalPanic' for e in state['events']))
                if panic:
                    event=next(e for e in state['events'] if e['kind']=='animalPanic')
                    self.assertEqual('horse',event['data']['rig']);self.assertEqual('Run',state['animals'][0]['clip'])

    def test_stream_cap_rebase_save_rng_continuity_and_generated_coordinates(self):
        self.call('init',spawn=[1025,0,0],starterKit=True);self.ground(origin=(960,-64))
        with tempfile.TemporaryDirectory() as folder:
            self.fauna(folder,[]) # Generated points already use the host frame; no retail Z flip.
            points=[[1025+i*.2,0,-10] for i in range(40)]
            self.call('worldWildlife',mapRoot=folder,points=points);before=self.pump(6)
            self.assertEqual(36,len(before['animals']))
            path=str(Path(folder)/'before.json');self.call('save',path=path)
            saved=json.loads(Path(path).read_text())['BevyBridge']['Wildlife']
            rebased=self.call('rebase',shift=[1024,0,0],expectedEpoch=0)['state']
            self.assertEqual(before['tick'],rebased['tick'])
            for a,b in zip(before['animals'],rebased['animals']):
                self.assertEqual(a['netId'],b['netId']);self.assertAlmostEqual(a['hostPosition'][0]-1024,b['hostPosition'][0],places=4)
            afterpath=str(Path(folder)/'after.json');self.call('save',path=afterpath)
            shifted=json.loads(Path(afterpath).read_text())['BevyBridge']['Wildlife']
            for a,b in zip(saved['Animals'],shifted['Animals']):
                for key in ['Seed','IdleTimer','FleeTimer','SeeCheck','FaceDir']:
                    self.assertEqual(a['Brain'][key],b['Brain'][key])
                self.assertAlmostEqual(a['Brain']['Home'][0]-1024,b['Brain']['Home'][0],places=4)
            expected=self.pump(6)['animals']
            restored=self.call('load',path=afterpath)['state']['animals']
            self.assertEqual(36,len(restored))
            for a,b in zip(expected,restored):
                self.assertEqual(a['hostPosition'],b['hostPosition']);self.assertEqual(a['clip'],b['clip'])

    def test_source_console_golden_angle_persists_and_respects_cheat_gate(self):
        self.call('action',name='console',args=dict(text='spawnanimal horse'))
        self.assertEqual([],self.pump()['animals'],'no field until terrain registration')
        self.ground()
        for _ in range(3):self.call('action',name='console',args=dict(text='spawnanimal ho'))
        animals=self.pump()['animals'];self.assertEqual(3,len(animals))
        self.assertEqual(3,len({tuple(a['hostPosition']) for a in animals}),'consecutive single spawns do not stack')
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'animals.json');self.call('save',path=path)
            self.call('load',path=path);self.call('action',name='console',args=dict(text='spawnanimal horse'))
            animals=self.pump()['animals'];self.assertEqual(4,len(animals));self.assertEqual(4,len({tuple(a['hostPosition']) for a in animals}))
        self.call('action',name='console',args=dict(text='spawnanimal horse 13'));self.assertEqual(4,len(self.pump()['animals']))
        self.call('init',allowCheats=False);self.ground();self.call('action',name='console',args=dict(text='spawnanimal horse'))
        self.assertEqual([],self.pump()['animals'])

if __name__=='__main__':unittest.main()
