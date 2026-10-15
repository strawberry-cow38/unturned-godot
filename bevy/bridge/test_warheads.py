"""Real accepted warheads and frozen recoverable ammunition, never cosmetic damage."""
import json
import math
from pathlib import Path
import tempfile
import unittest
import test_bridge
import test_attachment_actions
import test_wildlife


class WarheadTests(unittest.TestCase):
    setUp = test_bridge.BridgeTests.setUp
    tearDown = test_bridge.BridgeTests.tearDown
    call = test_bridge.BridgeTests.call
    pump = test_bridge.BridgeTests.pump
    configure = test_attachment_actions.AttachmentActionTests.configure
    ground = test_wildlife.WildlifeTests.ground
    fauna = test_wildlife.WildlifeTests.fauna

    def wall(self, z=-3, x=0):
        return dict(vertices=[[x-100,-10,z],[x+100,-10,z],[x+100,2,z],[x-100,2,z]],indices=[0,1,2,0,2,3])

    def fire(self, origin=(0,1.5,0), direction=(0,0,-1)):
        self.call('action',name='fire',args=dict(origin=origin,dir=direction))

    def test_warhead_is_captured_before_switch_and_blast_has_real_vehicle_falloff(self):
        self.configure(gun=dict(Id=519,GunAmmo=1,GunMagId=520,GunBarrelId=-1))
        vehicle=self.call('worldVehicles',vehicles=[dict(typeId=1,key='blast-neighbor',position=[2,0,-30],health=1000,healthMax=1000,fuel=0,seatOffsets=[[0,0,0]])])['result']['vehicles'][0]['netId']
        self.call('worldGeometry',meshes=[self.wall(-30)])
        self.fire(origin=(100,1.5,0));state=self.pump()
        self.assertFalse(any(e['kind']=='bulletExplosion' for e in state['events']))
        self.fire()
        for _ in range(6):
            state=self.call('tick')['state']
            if state['bullets']:break
        self.assertTrue(state['bullets']);self.call('tick',input=dict(heldItemId=0));state=self.pump(20)
        facts=[e['data'] for e in state['events'] if e['kind']=='bulletExplosion']
        self.assertEqual(1,len(facts));boom=facts[0]
        self.assertEqual(519,boom['itemId']);self.assertEqual('launcher_rocket',boom['gun']);self.assertEqual(9,boom['radius'])
        v=next(v for v in state['vehicles'] if v['netId']==vehicle)
        distance=math.dist(v['position'],boom['position'])
        self.assertAlmostEqual(1000-300*(1-distance/9),v['health'],delta=1.1)
        self.assertFalse(any(e['kind']=='explosion' for e in state['events']),'no fake grenade identity')
        self.assertEqual([],state['worldItems'],'cartridges and rockets are not recoverable arrows')

    def test_nearby_rocket_blast_uses_squared_player_damage_and_wall_occlusion(self):
        self.configure(gun=dict(Id=519,GunAmmo=1,GunMagId=520))
        self.call('worldGeometry',meshes=[self.wall(-7)]);self.fire();state=self.pump(12)
        boom=next(e['data'] for e in state['events'] if e['kind']=='bulletExplosion')
        expected=max(0,100-200*(1-(math.dist(state['player']['position'],boom['position'])/9)**2))
        self.assertAlmostEqual(expected,state['player']['hp'],delta=1.1)
        self.assertLess(state['player']['hp'],30)
        self.call('init',starterKit=True,spawn=[0,0,0])
        self.configure(gun=dict(Id=519,GunAmmo=1,GunMagId=520))
        cover=dict(vertices=[[.75,-1,-5],[.75,5,-5],[.75,5,-2],[.75,-1,-2]],indices=[0,1,2,0,2,3])
        self.call('worldGeometry',meshes=[self.wall(-7),cover])
        self.fire(origin=(1.5,1.5,0));state=self.pump(12)
        self.assertTrue(any(e['kind']=='bulletExplosion' for e in state['events']))
        self.assertEqual(100,state['player']['hp'],'original chest-height World ray shields the player')

    def test_crossbow_sticks_once_rebases_saves_loads_and_uses_ordinary_pickup(self):
        self.configure(gun=dict(Id=346,GunAmmo=1,GunMagId=347,GunBarrelId=-1))
        self.call('worldGeometry',meshes=[self.wall()]);self.fire();state=self.pump(30)
        arrows=[w for w in state['worldItems'] if w['itemId']==347]
        self.assertEqual(1,len(arrows));arrow=arrows[0]
        self.assertEqual((1,100,True),(arrow['amount'],arrow['quality'],arrow['settled']))
        self.assertAlmostEqual(-2.95,arrow['position'][2],places=2)
        self.assertEqual([0,0,1],arrow['stuckNormal']);self.assertLess(arrow['stuckDirection'][2],-.99)
        before=arrow['position'];state=self.call('rebase',shift=[1024,0,0],expectedEpoch=state['origin']['epoch'])['state']
        shifted=next(w for w in state['worldItems'] if w['netId']==arrow['netId'])
        self.assertAlmostEqual(before[0]-1024,shifted['position'][0],places=2)
        self.assertEqual(arrow['stuckDirection'],shifted['stuckDirection'])
        with tempfile.TemporaryDirectory() as directory:
            path=str(Path(directory)/'arrows.json');self.call('save',path=path)
            saved=json.loads(Path(path).read_text());self.assertEqual(1,len(saved['BevyBridge']['StuckProjectiles']))
            state=self.call('load',path=path)['state']
        arrows=[w for w in state['worldItems'] if w['itemId']==347]
        self.assertEqual(1,len(arrows));self.assertTrue(arrows[0]['settled']);self.assertEqual(arrow['stuckDirection'],arrows[0]['stuckDirection'])
        self.call('action',name='pickupItem',args=dict(netId=arrows[0]['netId']));state=self.pump(12)
        self.assertEqual([],state['worldItems']);self.assertEqual(1,sum(i['amount'] for p in state['inventory']['pages'] for i in p['items'] if i['id']==347))

    def test_nailgun_does_not_mint_a_whole_magazine_for_each_nail(self):
        self.configure(gun=dict(Id=1165,GunAmmo=20,GunMagId=1166))
        self.call('worldGeometry',meshes=[self.wall()]);self.fire();state=self.pump(30)
        self.assertEqual(19,state['player']['ammo']);self.assertEqual([],state['worldItems'])
        self.assertFalse(any(e['kind']=='projectileStuck' for e in state['events']))

    def test_vertical_bolt_preserves_source_random_spawn_pose_instead_of_invalid_look_at(self):
        self.configure(gun=dict(Id=346,GunAmmo=1,GunMagId=347))
        self.call('worldGeometry',meshes=[dict(vertices=[[-10,-1,-10],[10,-1,-10],[10,-1,10],[-10,-1,10]],indices=[0,1,2,0,2,3])])
        self.fire(direction=(0,-1,0));state=self.pump(30)
        arrow=next(w for w in state['worldItems'] if w['itemId']==347)
        self.assertIsNone(arrow.get('stuckDirection'));self.assertEqual([0,1,0],arrow['stuckNormal'])
        angles=arrow['stuckEuler'];self.assertEqual(3,len(angles))
        self.assertTrue(math.radians(75)<=angles[0]<=math.radians(105))
        self.assertTrue(0<=angles[1]<=math.tau)
        self.assertTrue(math.radians(-15)<=angles[2]<=math.radians(15))

    def test_bolt_uses_actual_animal_capsule_normal_and_stays_in_world_after_target_moves(self):
        self.configure(gun=dict(Id=346,GunAmmo=1,GunMagId=347));self.ground()
        with tempfile.TemporaryDirectory() as directory:
            self.fauna(directory,[(0,0,-3)]);self.call('worldWildlife',mapRoot=directory);self.pump()
            self.fire(origin=(0,1,0));state=self.pump(12)
            hit=next(e['data'] for e in state['events'] if e['kind']=='animalHit')
            arrow=next(w for w in state['worldItems'] if w['itemId']==347)
            self.assertAlmostEqual(1,math.sqrt(sum(n*n for n in arrow['stuckNormal'])),places=5)
            for axis in range(3):
                self.assertAlmostEqual(hit['point'][axis]+.05*arrow['stuckNormal'][axis],arrow['position'][axis],places=2)
            state=self.pump(200);later=next(w for w in state['worldItems'] if w['netId']==arrow['netId'])
            self.assertEqual(arrow['position'],later['position'])
