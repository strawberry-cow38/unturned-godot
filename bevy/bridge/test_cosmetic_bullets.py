#!/usr/bin/env python3
"""Source cosmetic launch/flight never becomes a second damage authority."""
import json
import math
import tempfile
import unittest
from pathlib import Path
import test_bridge
import test_attachment_actions

class CosmeticBulletTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump
    configure=test_attachment_actions.AttachmentActionTests.configure

    def launch(self,**args):
        result=self.call('action',name='fire',args=dict(origin=[0,1.5,0],dir=[0,0,-1],**args))['result']
        for _ in range(6):
            state=self.call('tick')['state']
            if state['bullets']:return state,result['value']
        self.fail('accepted fire did not create source cosmetic flight')

    def test_accepted_sequence_owns_context_and_first_step_uses_shared_ballistics(self):
        self.call('action',name='fire',args=dict(origin=[100,1,0],dir=[0,0,-1],visual=dict(aimAlpha=0,muzzleAnchor=[99,0,0])))
        self.assertEqual([],self.pump()['bullets'])
        state,seq=self.launch(visual=dict(aimAlpha=1,patternShot=4,muzzleAnchor=[.2,1.4,-.6]))
        launch=next(e['data'] for e in state['events'] if e['kind']=='bulletLaunched')
        self.assertEqual(seq,launch['sequence']);self.assertEqual(1,launch['aimAlpha']);self.assertEqual(4,launch['patternShot'])
        self.assertEqual([.2,1.4,-.6],launch['muzzleAnchor']);self.assertEqual(1,len(state['bullets']))
        first=state['bullets'][0];self.assertEqual(first['origin'],first['position'],'source advances existing bullets before accepting this tick’s shot')
        second=self.call('tick')['state']['bullets'][0]
        for axis in range(3):self.assertAlmostEqual(first['position'][axis]+first['velocity'][axis]*.02,second['position'][axis],places=4)
        self.assertAlmostEqual(first['velocity'][1]+first['gravity']*.02,second['velocity'][1],places=4)
        self.assertEqual(first['remainingSteps']-1,second['remainingSteps']);self.assertEqual(first['id'],second['id'])
        self.assertAlmostEqual(.032,first['tracerWidth']);self.assertEqual([1.9,.85,.2],first['tracerColor'])
        self.assertEqual(29,state['player']['ammo'],'cosmetic launch consumes no extra ammunition')

    def test_visual_launch_retains_original_local_precision_before_wire_quantization(self):
        origin=[.012345,1.512345,.023456];direction=[.123456,.003456,-.992345]
        self.call('action',name='fire',args=dict(origin=origin,dir=direction,visual=dict(aimAlpha=1)))
        state=self.pump(6)
        launch=next(e['data'] for e in state['events'] if e['kind']=='bulletLaunched')
        wire=next(e['data'] for e in state['events'] if e['kind']=='fire')
        length=math.sqrt(sum(v*v for v in direction))
        for axis in range(3):
            self.assertAlmostEqual(origin[axis],launch['origin'][axis],places=6)
            self.assertAlmostEqual(direction[axis]/length,launch['direction'][axis],places=6)
        packed=wire['origin']
        if isinstance(packed,dict):packed=[packed[k] for k in ['x','y','z']]
        self.assertGreater(max(abs(a-b) for a,b in zip(packed,origin)),.0001,
            'authority remains the actual packed command; local cosmetics retain source float precision')

    def test_cosmetic_pellets_retire_on_wall_without_duplicate_impact_or_damage(self):
        self.configure(gun=dict(Id=112,GunAmmo=6,GunMagId=113))
        self.call('worldGeometry',meshes=[dict(vertices=[[-100,-100,-20],[100,-100,-20],[100,100,-20],[-100,100,-20]],indices=[0,1,2,0,2,3])])
        state,_=self.launch();count=len(state['bullets']);self.assertGreater(count,1)
        launch=next(e['data'] for e in state['events'] if e['kind']=='bulletLaunched');self.assertEqual(count,launch['pelletCount'])
        self.assertEqual(count,len(set(tuple(b['velocity']) for b in state['bullets'])),'original shotgun cosmetics scatter separately')
        state=self.pump(30);self.assertEqual([],state['bullets'])
        self.assertEqual(count,sum(e['kind']=='impact' for e in state['events']),'only actual server pellet impacts emit FX')
        self.assertEqual(count,sum(e['kind']=='bulletRetired' and e['data']['reason']=='impact' for e in state['events']))
        self.assertEqual(5,state['player']['ammo'])

    def test_suppression_and_rocket_style_are_captured_before_switching(self):
        self.configure(gun=dict(GunBarrelId=7))
        state,_=self.launch();b=state['bullets'][0]
        self.assertTrue(b['silenced']);self.assertAlmostEqual(752,math.sqrt(sum(v*v for v in b['velocity'])),places=3)
        self.call('tick',input=dict(heldItemId=0));state=self.call('snapshot')['state'];self.assertTrue(state['bullets'][0]['silenced'])
        self.configure(gun=dict(Id=519,GunAmmo=1,GunMagId=520,GunBarrelId=-1))
        state,_=self.launch();self.assertTrue(state['bullets'][0]['rocket'])
        catalog=self.call('catalog')['result']['bulletVisuals'];self.assertEqual('rocket_projectile.txt',catalog['rocketMesh'])
        self.assertEqual([-90,0,0],catalog['rocketMeshRotation']);self.assertEqual(16,catalog['segments']);self.assertEqual(10,catalog['maxLength'])

    def test_thin_surfaces_reduce_only_cosmetic_velocity_and_stop_at_original_pierce_cap(self):
        self.call('worldGeometry',meshes=[dict(thin=True,vertices=[[-100,-100,z],[100,-100,z],[0,100,z]]) for z in [-5,-10,-15]])
        state,_=self.launch();initial=state['bullets'][0];state=self.call('tick')['state'];first=state['bullets'][0]
        self.assertEqual(1,first['pierced']);self.assertAlmostEqual(initial['velocity'][2]*.75,first['velocity'][2],places=3)
        state=self.pump(10);self.assertEqual([],state['bullets'])
        retired=next(e['data'] for e in state['events'] if e['kind']=='bulletRetired');self.assertAlmostEqual(-15,retired['position'][2],places=3)
        self.assertEqual(1,sum(e['kind']=='impact' for e in state['events']),'existing authority still stops at its first surface')

    def test_water_uses_source_splash_slab_and_pierces_once_from_outside(self):
        self.call('worldCosmeticGeometry',waterBoxes=[dict(center=[0,0,0],size=[100,.2,100])])
        self.call('action',name='fire',args=dict(origin=[0,1.5,0],dir=[0,-.4,-.916515]))
        for _ in range(6):
            state=self.call('tick')['state']
            if state['bullets']:break
        first=state['bullets'][0];after=self.call('tick')['state']['bullets'][0]
        self.assertEqual(1,after['pierced']);self.assertAlmostEqual(first['velocity'][2]*.45,after['velocity'][2],places=3)
        after=self.call('tick')['state']['bullets'][0];self.assertEqual(1,after['pierced'],'starting inside splash slab must not hit its back face')
        self.assertFalse(any(e['kind']=='impact' for e in state['events']),'a cosmetic-only water probe cannot emit authority impact')

    def test_origin_barrier_translates_live_flight_and_unprocessed_anchor_without_aging(self):
        self.call('init',starterKit=True,spawn=[1025,0,0])
        self.call('action',name='fire',args=dict(origin=[1025,1.5,0],dir=[0,0,-1],visual=dict(muzzleAnchor=[1025.2,1.4,-.6])))
        self.call('rebase',shift=[1024,0,0],expectedEpoch=0)
        for _ in range(6):
            state=self.call('tick')['state']
            if state['bullets']:break
        b=state['bullets'][0];self.assertAlmostEqual(1,b['origin'][0],places=2);self.assertAlmostEqual(1.2,b['muzzleAnchor'][0],places=3)
        shifted=self.call('rebase',shift=[-1024,0,0],expectedEpoch=1)['state'];a=shifted['bullets'][0]
        self.assertEqual(state['tick'],shifted['tick']);self.assertEqual(b['id'],a['id']);self.assertEqual(b['remainingSteps'],a['remainingSteps'])
        self.assertAlmostEqual(b['position'][0]+1024,a['position'][0],places=3)
        event=next(e['data'] for e in shifted['events'] if e['kind']=='bulletLaunched');self.assertAlmostEqual(1025.2,event['muzzleAnchor'][0],places=3)

    def test_transient_flight_expires_and_save_retains_water_geometry_without_restarting_shot(self):
        state,_=self.launch();remaining=state['bullets'][0]['remainingSteps'];state=self.pump(remaining)
        self.assertEqual([],state['bullets']);self.assertTrue(any(e['kind']=='bulletRetired' and e['data']['reason']=='expired' for e in state['events']))
        self.call('worldCosmeticGeometry',waterBoxes=[dict(center=[0,0,0],size=[100,.2,100])])
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'cosmetics.json');self.call('save',path=path);self.call('load',path=path)
            self.assertEqual([],self.call('snapshot')['state']['bullets'])
            self.assertEqual(1,len(json.loads(Path(path).read_text())['BevyBridge']['CosmeticWater']))
            self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));self.pump()
            self.call('action',name='fire',args=dict(origin=[0,1.5,0],dir=[0,-.4,-.916515]));state=self.pump(3)
            self.assertEqual(1,state['bullets'][0]['pierced'])

if __name__=='__main__':unittest.main()
