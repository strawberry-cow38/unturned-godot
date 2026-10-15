#!/usr/bin/env python3
"""Attachment transactions through the real listen-host and inventory replicas."""
import json
import tempfile
import unittest
from pathlib import Path
import test_bridge

class AttachmentActionTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump

    def configure(self,gun=None,items=(),size=(5,3),secondary=None):
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'inventory.json');self.call('save',path=path)
            saved=json.loads(Path(path).read_text());pages=saved['Players'][0]['Pages']
            if gun:pages[0]['Items'][0].update(gun)
            if secondary:pages[1]['Items']=[dict(pages[0]['Items'][0],**secondary)]
            pages[2]=dict(Width=size[0],Height=size[1],Items=[dict(X=x,Y=y,Rot=0,Id=i,Amount=n,Quality=q) for i,x,y,n,q in items])
            for p in pages[3:7]:p['Items']=[];p['Width']=p['Height']=0
            Path(path).write_text(json.dumps(saved));self.call('load',path=path)
        self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));return self.pump()

    def options(self):return self.call('attachmentOptions')['result']
    def slot(self,options,name):return next(s for s in options['slots'] if s['name']==name)
    def request(self,action,options=None,**args):
        options=options or self.options()
        return self.call('action',name=action,args=dict(selectionVersion=options['selectionVersion'],gunToken=options['gunToken'],**args))['result']
    def fit(self,name,index=0,options=None):
        options=options or self.options();choice=self.slot(options,name)['choices'][index]
        return self.request('installAttachment',options,slot=name,**{k:choice[k] for k in ['page','x','y','instanceToken']})
    def bag(self,state):return [i for p in state['inventory']['pages'][2:7] for i in p['items']]

    def test_swaps_exact_selected_attachment_returns_old_once_and_restores_factory_irons(self):
        self.configure(items=[(21,0,0,1,90),(22,2,0,1,80)])
        self.assertTrue(self.fit('Sight')['queued']);state=self.pump()
        self.assertEqual(21,state['weapon']['attachments']['sightId'])
        self.assertEqual([22],[i['id'] for i in self.bag(state)],'factory irons never mint a bag item')
        self.assertTrue(self.fit('Sight')['queued']);state=self.pump()
        self.assertEqual(22,state['weapon']['attachments']['sightId']);self.assertEqual([21],[i['id'] for i in self.bag(state)])
        self.assertTrue(self.request('removeAttachment',slot='Sight')['queued']);state=self.pump()
        self.assertEqual(5,state['weapon']['attachments']['sightId']);self.assertEqual([21,22],sorted(i['id'] for i in self.bag(state)))
        self.assertFalse(self.request('removeAttachment',slot='Sight')['queued'])
        self.assertEqual(3,sum(e['kind']=='attachmentChanged' for e in state['events']))

    def test_full_bag_refuses_swap_and_detach_without_even_relocating_incoming(self):
        before=self.configure(gun=dict(GunSightId=21),items=[(5,0,0,1,47)],size=(1,1))
        options=self.options();self.assertTrue(self.fit('Sight',options=options)['queued']);after=self.pump()
        self.assertEqual(before['inventory'],after['inventory']);self.assertEqual(21,after['weapon']['attachments']['sightId'])
        rejected=[e['data'] for e in after['events'] if e['kind']=='attachmentRejected'];self.assertIn('No room',rejected[-1]['reason']);self.assertEqual(rejected[-1]['reason'],rejected[-1]['text'])
        self.assertTrue(self.request('removeAttachment',slot='Sight')['queued']);after=self.pump()
        self.assertEqual(before['inventory'],after['inventory']);self.assertEqual(21,after['weapon']['attachments']['sightId'])

    def test_same_id_magazines_conserve_selected_rounds_and_chamber_then_remove_and_rack(self):
        self.configure(items=[(6,0,0,3,40),(6,2,0,12,90)])
        options=self.options();choices=self.slot(options,'Magazine')['choices'];self.assertEqual([3,12],[c['amount'] for c in choices])
        self.assertNotEqual(choices[0]['instanceToken'],choices[1]['instanceToken'])
        self.assertTrue(self.fit('Magazine',1,options)['queued']);state=self.pump()
        self.assertEqual(13,state['player']['ammo']);self.assertTrue(state['weapon']['chambered'])
        self.assertEqual([3,29],sorted(i['amount'] for i in self.bag(state)));self.assertTrue(state['weapon']['reloading'])
        self.assertFalse(self.request('removeAttachment',slot='Magazine')['queued'],'source animation blocks a second swap')
        state=self.pump(120);self.assertFalse(state['weapon']['reloading'])
        self.assertTrue(self.request('removeAttachment',slot='Magazine')['queued']);state=self.pump()
        self.assertEqual(1,state['player']['ammo']);self.assertEqual(0,state['weapon']['attachments']['magazineId'])
        self.assertEqual([3,12,29],sorted(i['amount'] for i in self.bag(state)))
        self.pump(120);self.assertTrue(self.request('rackGun')['queued']);state=self.pump()
        self.assertEqual(0,state['player']['ammo']);self.assertEqual(1,sum(i['amount'] for i in self.bag(state) if i['id']==5004))

    def test_magazine_remove_drops_actual_rounds_when_bag_is_full(self):
        self.configure(items=[(67,0,0,1,100)],size=(1,1))
        self.assertTrue(self.request('removeAttachment',slot='Magazine')['queued']);state=self.pump()
        self.assertEqual(1,state['player']['ammo']);self.assertEqual([(6,29)],[(i['itemId'],i['amount']) for i in state['worldItems']])

    def test_queued_request_rechecks_exact_gun_selection_and_item_identity(self):
        self.configure(gun=dict(Id=97,GunAmmo=7,GunMagId=98),items=[(151,0,0,1,100)],secondary=dict(Id=97,GunAmmo=6,GunMagId=98))
        options=self.options();self.assertTrue(self.fit('Tactical',options=options)['queued'])
        self.call('action',name='holdItem',args=dict(page=1,x=0,y=0));state=self.pump()
        self.assertEqual(1,state['weapon']['held']['page']);self.assertEqual([151],[i['id'] for i in self.bag(state)])
        self.assertTrue(any(e['kind']=='attachmentRejected' for e in state['events']))
        self.assertFalse(self.fit('Tactical',options=options)['queued'],'same asset ID in another slot is a different gun')
        options=self.options();self.call('action',name='dropItem',args=dict(page=2,x=0,y=0));self.pump()
        self.assertFalse(self.fit('Tactical',options=options)['queued'],'stale physical option cannot mint an attachment')

    def test_source_capabilities_integral_sight_missing_grip_and_cartridge_fit(self):
        self.configure(gun=dict(Id=1362,GunAmmo=30,GunMagId=9140),items=[(21,0,0,1,100),(8,2,0,1,100)])
        options=self.options();self.assertFalse(self.slot(options,'Sight')['available']);self.assertFalse(self.slot(options,'Grip')['available'])
        self.assertFalse(self.request('removeAttachment',slot='Sight')['queued'])
        self.configure(gun=dict(Id=97,GunAmmo=7,GunMagId=98),items=[(7,0,0,1,100)])
        self.assertEqual([],self.slot(self.options(),'Barrel')['choices'],'5.56-only suppressor cannot fit a .45 pistol')

    def test_shell_picker_returns_old_type_and_consumes_only_carried_new_type(self):
        self.configure(gun=dict(Id=380,GunAmmo=2,GunMagId=381),items=[(5000,0,0,1,100)])
        # Masterkey is 20ga, so a 12ga slug never becomes a valid selection.
        self.assertFalse(self.request('chooseShellType',itemId=5000)['queued'])
        self.configure(gun=dict(Id=112,GunAmmo=6,GunMagId=113),items=[(5000,0,0,1,100)])
        options=self.options();self.assertEqual('shell',self.slot(options,'Magazine')['kind'])
        self.assertEqual([5000],[s['id'] for s in self.slot(options,'Magazine')['shells']])
        self.assertTrue(self.request('chooseShellType',itemId=5000)['queued']);state=self.pump(180)
        self.assertEqual(1,state['player']['ammo']);self.assertEqual(5000,state['weapon']['shellItemId'])
        self.assertEqual(6,sum(i['amount'] for i in self.bag(state) if i['id']==113));self.assertFalse(any(i['id']==5000 for i in self.bag(state)))
        self.assertTrue(self.request('unloadShells')['queued']);state=self.pump(180)
        self.assertEqual(0,state['player']['ammo']);self.assertEqual(1,sum(i['amount'] for i in self.bag(state) if i['id']==5000))

    def test_shell_selection_and_installed_attachment_survive_save_without_stale_menu_authority(self):
        self.configure(gun=dict(Id=112,GunAmmo=6,GunMagId=113),items=[(5000,0,0,2,100),(21,1,0,1,100)])
        self.assertTrue(self.fit('Sight')['queued']);self.pump()
        self.assertTrue(self.request('chooseShellType',itemId=5000)['queued']);self.pump(180)
        stale=self.options()
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'attachments.json');self.call('save',path=path);self.call('load',path=path)
            self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));state=self.pump()
        self.assertEqual(21,state['weapon']['attachments']['sightId']);self.assertEqual(5000,state['weapon']['shellItemId'])
        self.assertEqual(2,state['player']['ammo']);self.assertFalse(self.request('removeAttachment',stale,slot='Sight')['queued'])

    def test_shot_and_melee_facts_keep_actual_source_weapon_after_holstering(self):
        self.configure(gun=dict(Id=116,GunAmmo=30,GunMagId=6,GunBarrelId=-1))
        self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[0,0,-1]));state=self.pump()
        fire=next(e['data'] for e in state['events'] if e['kind']=='fire')
        self.assertTrue(fire['silenced']);self.assertFalse(fire['barrelSilenced'],'source integral suppression does not hide the bare-barrel flash')
        self.configure(gun=dict(Id=490))
        self.call('action',name='melee',args=dict(strong=False,yawDegrees=0));state=self.pump()
        melee=next(e['data'] for e in state['events'] if e['kind']=='melee');self.assertEqual(490,melee['itemId']);self.assertTrue(melee['repeated'])
        self.call('tick',input=dict(heldItemId=0));state=self.pump()
        self.assertEqual(490,next(e['data'] for e in state['events'] if e['kind']=='melee')['itemId'])

if __name__=='__main__':unittest.main()
