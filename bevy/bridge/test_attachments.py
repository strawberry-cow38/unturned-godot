#!/usr/bin/env python3
"""Original attachment state and accepted-shot facts over real combat authority."""
import json
import tempfile
import unittest
from pathlib import Path
import test_bridge

class AttachmentTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump

    def fitted(self,folder,barrel,**fields):
        path=str(Path(folder)/'gun.json');self.call('save',path=path)
        saved=json.loads(Path(path).read_text());item=saved['Players'][0]['Pages'][0]['Items'][0]
        item['GunBarrelId']=barrel;item.update(fields);Path(path).write_text(json.dumps(saved))
        self.call('load',path=path);self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));return self.pump()

    def wall(self):
        self.call('worldGeometry',meshes=[dict(vertices=[[-4,0,-17],[4,0,-17],[4,4,-17],[-4,4,-17]],indices=[0,1,2,0,2,3])])

    def shot(self):
        before=self.call('snapshot')['state']['events']
        if before:self.call('snapshot',ackEvents=before[-1]['seq'])
        self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[0,0,-1]));return self.pump(10)

    def test_actual_projectile_speed_and_sound_follow_source_barrel(self):
        for barrel,silenced,clip,volume in [(-1,False,'eaglefire_shoot.ogg',1),(7,True,'military_suppressor_shoot.ogg',.3),(149,False,'eaglefire_shoot.ogg',1)]:
            with self.subTest(barrel=barrel),tempfile.TemporaryDirectory() as folder:
                self.call('init',starterKit=True);self.wall();self.fitted(folder,barrel)
                state=self.shot();event=next(e for e in state['events'] if e['kind']=='fire');shot=event['data']
                self.assertEqual(barrel,shot['barrelId']);self.assertEqual(silenced,shot['silenced'])
                self.assertEqual(clip,shot['shootClip']);self.assertAlmostEqual(volume,shot['shotVolume'])
                self.assertAlmostEqual(752 if silenced else 940,shot['muzzleVelocity'],places=3)
                impact=next(e for e in state['events'] if e['kind']=='impact')
                self.assertEqual(1 if silenced else 0,impact['tick']-shot['acceptedTick'],
                    '17m wall is beyond the first silenced15.04m step but inside the original18.8m step')
                self.assertEqual(barrel,state['weapon']['attachments']['barrelId'])

    def test_bottle_silences_its_own_shot_then_breaks_without_reappearing_after_save(self):
        self.wall()
        with tempfile.TemporaryDirectory() as folder:
            self.fitted(folder,9400,GunAttach=25)
            first=self.shot();shot=next(e['data'] for e in first['events'] if e['kind']=='fire')
            self.assertEqual(9400,shot['barrelId']);self.assertTrue(shot['barrelBroken']);self.assertTrue(shot['silenced'])
            self.assertEqual('makeshift_muffler_shoot.ogg',shot['shootClip']);self.assertEqual(.6,shot['shotVolume'])
            self.assertAlmostEqual(752,shot['muzzleVelocity'],places=3)
            self.assertEqual(-1,first['weapon']['attachments']['barrelId']);self.assertEqual(17,first['weapon']['attachments']['mask'])
            self.assertFalse(first['weapon']['silenced']);self.assertEqual(1,sum(e['kind']=='barrelBroken' for e in first['events']))
            impact=next(e for e in first['events'] if e['kind']=='impact')
            self.assertEqual(1,impact['tick']-shot['acceptedTick'],'breaking the barrel cannot change the projectile being spawned')
            self.call('tick',input=dict(heldItemId=0));retained=self.pump()
            self.assertTrue(next(e['data'] for e in retained['events'] if e['kind']=='fire')['silenced'])
            path=str(Path(folder)/'after.json');self.call('save',path=path);self.call('load',path=path)
            self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));self.pump()
            second=self.shot();shot=next(e['data'] for e in second['events'] if e['kind']=='fire')
            self.assertFalse(shot['silenced']);self.assertAlmostEqual(940,shot['muzzleVelocity'],places=3);self.assertEqual('eaglefire_shoot.ogg',shot['shootClip'])
            self.assertFalse(any(e['kind']=='barrelBroken' for e in second['events']))

    def test_rejected_shot_never_spends_single_use_barrel(self):
        with tempfile.TemporaryDirectory() as folder:
            self.fitted(folder,9400)
            self.call('action',name='fire',args=dict(origin=[100,1,0],dir=[0,0,-1]));state=self.pump()
            self.assertFalse(any(e['kind'] in ['fire','barrelBroken'] for e in state['events']))
            self.assertEqual(9400,state['weapon']['attachments']['barrelId'])
            self.assertEqual(30,state['player']['ammo'])
            state=self.shot();self.assertEqual(-1,state['weapon']['attachments']['barrelId'])

    def test_factory_irons_seed_once_and_saved_scope_remains_installed(self):
        state=self.pump();self.assertEqual(5,state['weapon']['attachments']['sightId']);self.assertTrue(state['weapon']['attachments']['seeded'])
        with tempfile.TemporaryDirectory() as folder:
            state=self.fitted(folder,-1,GunSightId=21,GunAttachSeeded=True)
            self.assertEqual(21,state['weapon']['attachments']['sightId'])
            self.call('tick',input=dict(heldItemId=0));self.pump()
            self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));state=self.pump()
            self.assertEqual(21,state['weapon']['attachments']['sightId'])
            path=str(Path(folder)/'scope.json');self.call('save',path=path)
            item=json.loads(Path(path).read_text())['Players'][0]['Pages'][0]['Items'][0]
            self.assertEqual(21,item['GunSightId']);self.assertTrue(item['GunAttachSeeded'])

    def test_selected_second_pistol_condition_does_not_use_broken_first_twin(self):
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'twins.json');self.call('save',path=path)
            saved=json.loads(Path(path).read_text());pages=saved['Players'][0]['Pages']
            item=pages[0]['Items'][0];item.update(Id=97,Quality=0,GunAmmo=7,GunMagId=98)
            pages[1]['Items']=[dict(item,Quality=100)];Path(path).write_text(json.dumps(saved))
            self.call('load',path=path);self.call('action',name='holdItem',args=dict(page=1,x=0,y=0));self.pump()
            state=self.shot();shot=next(e['data'] for e in state['events'] if e['kind']=='fire')
            self.assertEqual(97,shot['itemId']);self.assertEqual(100,shot['quality'])
            self.assertEqual(6,state['player']['ammo']);self.assertEqual(1,state['weapon']['held']['page'])
            self.assertEqual(7,state['inventory']['pages'][0]['items'][0]['gunAmmo'])

if __name__=='__main__':unittest.main()
