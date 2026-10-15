#!/usr/bin/env python3
"""Current ZombieChunkField/Body rules through authoritative MemTransport."""
import json
import math
import struct
import tempfile
import unittest
from pathlib import Path
import test_bridge
import test_wildlife

class ZombieTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    reject=test_bridge.BridgeTests.reject
    pump=test_bridge.BridgeTests.pump
    ground=test_wildlife.WildlifeTests.ground

    def register(self,folder,points):
        return self.call('worldZombies',mapRoot=folder,points=points)['result']

    def shoot(self):
        self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[0,0,-1]))
        return self.pump(10)

    def test_hot_pose_is_native_owned_and_warm_population_stays_data(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[0,0,-3],[180,0,0]])
            state=self.pump(10);self.assertEqual(1,len(state['zombies']))
            body=state['zombies'][0];self.assertEqual([0,4.5],body['desiredVelocity'])
            self.assertEqual([0,0,-3],body['hostPosition'])
            self.assertTrue(body['hotBody']);self.assertEqual(2,state['zombieField']['population'])
            self.call('worldZombieState',bodies=[dict(netId=body['netId'],position=[0,0,-2.5],velocity=[0,0,4.5],yaw=180,
                motion=dict(windowPosition=[0,0,-3],windowTime=.2,escapeTime=.4,positionInitialized=True))])
            after=self.pump(8)['zombies'][0]
            self.assertEqual([0,0,-2.5],after['hostPosition']);self.assertEqual([0,0,-2.5],after['position'])
            self.assertEqual(180,after['hostYaw']);self.assertEqual(.4,after['motion']['escapeTime'])
            self.pump(200);self.assertEqual(100,self.call('snapshot')['state']['player']['hp'],
                'current source has no zombie attack loop; do not revive obsolete ZombieSim combat')

    def test_source_spawn_chance_cap_and_nearest_visible_body_limit(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            points=[[x+i*.01,0,z] for x,z in [(2,2),(-2,2),(2,-2),(-2,-2)] for i in range(100)]
            result=self.register(folder,points);self.assertEqual(96,result['population'])
            state=self.pump(10);self.assertEqual(48,len(state['zombies']))
            self.assertTrue(all(z['hotBody'] for z in state['zombies']))

    def test_retained_bullet_damage_corpse_and_save_preserve_source_lifecycle(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[0,0,-3]]);self.pump(6)
            state=self.shoot();self.assertEqual(80,state['zombies'][0]['health'],'canonical Eaglefire damage20, not obsolete ZombieDamage99')
            event=next(e for e in state['events'] if e['kind']=='zombieHit')
            self.assertEqual('bullet',event['data']['source']);self.assertTrue(event['data']['impactEmitted'])
            for _ in range(4):state=self.shoot()
            z=state['zombies'][0];self.assertTrue(z['dead']);self.assertFalse(z['hotBody']);self.assertTrue(z['hasBody'])
            self.assertGreater(z['corpseRemainingTicks'],380);self.assertEqual(1,state['player']['kills'])
            self.assertEqual([],state['worldItems']);self.assertEqual(0,state['zombieField']['population'])
            self.assertEqual(0,state['skills']['experience'],'current source increments kill count without awarding experience')
            path=str(Path(folder)/'save.json');self.call('save',path=path)
            restored=self.call('load',path=path)['state']['zombies'][0]
            self.assertTrue(restored['dead']);self.assertEqual(z['corpseRemainingTicks']-6,restored['corpseRemainingTicks'])
            self.assertEqual(z['deathImpulse'],restored['deathImpulse'])
            state=self.pump(restored['corpseRemainingTicks']+20);self.assertEqual([],state['zombies'])
            self.assertEqual(0,state['zombieField']['population']);self.assertEqual([],state['worldItems'])

    def test_world_blocks_bullets_but_original_melee_has_no_ordinary_wall_gate(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[0,0,-1.5]]);self.pump()
            self.call('worldGeometry',replace=False,meshes=[dict(vertices=[[-3,0,-.6],[3,0,-.6],[3,4,-.6],[-3,4,-.6]],indices=[0,1,2,0,2,3])])
            state=self.shoot();self.assertEqual(100,state['zombies'][0]['health'])
            self.call('tick',input=dict(heldItemId=0));self.pump()
            self.call('action',name='melee',args=dict(strong=False,yawDegrees=0));state=self.pump(40)
            event=next(e for e in state['events'] if e['kind']=='zombieHit')
            self.assertEqual('melee',event['data']['source']);self.assertFalse(event['data']['impactEmitted'])
            self.assertLess(state['zombies'][0]['health'],100)

    def test_hot_to_frozen_teleport_retires_bodies_and_remembers_killed_slots(self):
        self.ground(origin=(-512,-512),spacing=(64,64),side=17)
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[0,0,-3]]);self.pump()
            original=self.call('snapshot')['state']['zombies'][0]
            self.call('action',name='console',args=dict(text='teleport 500 0 0'));state=self.pump(30)
            self.assertEqual([],state['zombies'],'HOT->FROZEN cannot orphan a native body when Live is cleared')
            self.call('action',name='console',args=dict(text='teleport 0 0 0'));state=self.pump(30)
            self.assertEqual(1,len(state['zombies']));self.assertNotEqual(original['netId'],state['zombies'][0]['netId'])
            self.assertEqual(original['outfit'],state['zombies'][0]['outfit'])
            for _ in range(5):state=self.shoot()
            self.assertTrue(state['zombies'][0]['dead'])
            self.call('action',name='console',args=dict(text='teleport 500 0 0'));self.pump(30)
            self.call('action',name='console',args=dict(text='teleport 0 0 0'));state=self.pump(30)
            self.assertTrue(all(z['dead'] for z in state['zombies']))
            self.assertEqual(0,state['zombieField']['population'])

    def test_sight_noise_radius_and_occlusion_drive_exact_flow(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[0,0,-30]]);state=self.pump(30)
            self.assertEqual([0,0],state['zombies'][0]['desiredVelocity'])
            self.call('worldZombieNoise',position=[0,0,0],radius=5);state=self.pump(20)
            self.assertEqual([0,0],state['zombies'][0]['desiredVelocity'])
            self.call('worldZombieNoise',position=[0,0,0],radius=48);state=self.pump(20)
            self.assertGreater(state['zombies'][0]['desiredVelocity'][1],4)
            self.assertGreater(state['zombieField']['fieldCells'],0)
            state=self.pump(420);self.assertEqual([0,0],state['zombies'][0]['desiredVelocity'])

    def test_only_accepted_unsuppressed_shots_alert_outside_sight(self):
        for barrel,silenced in [(7,True),(149,False),(-1,False)]:
            with self.subTest(barrel=barrel),tempfile.TemporaryDirectory() as folder:
                self.call('init',starterKit=True);self.ground();self.register(folder,[[0,0,-30]])
                path=str(Path(folder)/'save.json');self.call('save',path=path)
                saved=json.loads(Path(path).read_text());item=saved['Players'][0]['Pages'][0]['Items'][0]
                item['GunBarrelId']=barrel;Path(path).write_text(json.dumps(saved))
                self.call('load',path=path);self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));self.pump()
                self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[1,0,0]));state=self.pump(30)
                self.assertEqual(not silenced,state['zombieField']['alert'])
                self.assertEqual(not silenced,state['zombies'][0]['desiredVelocity'][1]>4)
        self.call('tick',input=dict(heldItemId=0));self.pump(420)
        self.call('action',name='fire',args={});self.assertFalse(self.pump()['zombieField']['alert'])

    def test_actual_grenade_death_credits_kill_but_no_flesh_impact_or_drops(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[0,0,-3]]);self.pump()
            self.call('action',name='console',args=dict(text='give 254'));self.pump()
            self.call('tick',input=dict(heldItemId=254));self.pump()
            self.call('action',name='grenade',args=dict(origin=[0,.2,-1],velocity=[0,0,0],itemId=254));self.pump(4)
            self.call('action',name='console',args=dict(text='teleport 20 0 0'));state=self.pump(166)
            hit=next(e['data'] for e in state['events'] if e['kind']=='zombieHit')
            self.assertEqual('blast',hit['source']);self.assertFalse(hit['impactEmitted']);self.assertTrue(hit['killed'])
            self.assertEqual(1,state['player']['kills']);self.assertEqual([],state['worldItems'])

    def test_source_melee_hits_one_zombie_then_one_animal_in_same_cone(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[0,0,-1.5]])
            test_wildlife.WildlifeTests.fauna(self,folder,[(0,0,-1.5)])
            self.call('worldWildlife',mapRoot=folder);self.pump()
            self.call('tick',input=dict(heldItemId=0));self.pump()
            self.call('action',name='melee',args=dict(strong=False,yawDegrees=0));state=self.pump(40)
            hits=[e['kind'] for e in state['events'] if e['kind'] in ['zombieHit','animalHit']]
            self.assertEqual(['zombieHit','animalHit'],hits,
                'PlayerController.ApplyMeleeHit breaks the zombie loop then enters the independent animal loop')

    def test_source_walkability_box_overlaps_triangle_surface_above_ground(self):
        for height,blocked in [(0,True),(-10,False)]:
            with self.subTest(height=height),tempfile.TemporaryDirectory() as folder:
                self.call('init',starterKit=True);self.ground()
                self.call('worldGeometry',replace=False,meshes=[dict(vertices=[[-12,height,-15],[12,height,-15],[0,height+5,-15]])])
                self.register(folder,[[0,0,-30]]);self.call('worldZombieNoise',position=[0,0,0],radius=48)
                state=self.pump(20)
                self.assertEqual(blocked,state['zombieField']['blockedCells']>0)

    def test_rebase_save_preserves_outfits_flow_native_window_and_damage(self):
        self.call('init',spawn=[1025,0,0],starterKit=True);self.ground(origin=(960,-64))
        with tempfile.TemporaryDirectory() as folder:
            self.register(folder,[[1025,0,-3]]);state=self.pump(20);z=state['zombies'][0]
            self.call('worldZombieState',bodies=[dict(netId=z['netId'],position=[1025,0,-3],velocity=[0,0,2],yaw=90,
                motion=dict(windowPosition=[1025,0,-4],windowTime=.2,escapeTime=.7,positionInitialized=True))])
            self.call('worldZombieNoise',position=[1025,0,0],radius=48);state=self.pump(20)
            shifted=self.call('rebase',shift=[1024,0,0],expectedEpoch=0)['state']['zombies'][0]
            self.assertEqual(z['netId'],shifted['netId']);self.assertEqual([1,0,-3],shifted['hostPosition'])
            self.assertEqual([1,0,-4],shifted['motion']['windowPosition'])
            path=str(Path(folder)/'save.json');self.call('save',path=path);restored=self.call('load',path=path)['state']['zombies'][0]
            for key in ['hostPosition','hostYaw','motion','outfit','variant','shirtId','pantsId','hatId','gearId','desiredVelocity']:
                self.assertEqual(shifted[key],restored[key],key)

    def test_retail_binary_parser_keeps_table_and_flips_z_filters_source_sand(self):
        self.ground()
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder)/'Spawns';p.mkdir()
            raw=bytes([1])+struct.pack('<H',1)+struct.pack('<Bfff',12,0,99,3)+bytes(4095*2)
            (p/'Animals.dat').write_bytes(raw)
            self.call('worldZombies',mapRoot=folder);z=self.pump()['zombies'][0]
            self.assertEqual(12,z['table']);self.assertEqual([0,0,-3],z['hostPosition'])

if __name__=='__main__':unittest.main()
