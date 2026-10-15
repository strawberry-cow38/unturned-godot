"""Appearance presentation follows the real profile and worn inventory replicas."""
import unittest
import test_bridge

class ProfileTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump

    def test_profile_is_server_sanitized_and_face_is_validated(self):
        profile=self.call('snapshot')['state']['player']['profile']
        self.assertEqual(dict(name='Survivor',face=0,avatarHash=0),profile)
        self.call('action',name='setProfile',args=dict(name='  Scout\nName  ',avatarPng=[],face=7))
        self.assertEqual(dict(name='ScoutName',face=7,avatarHash=0),self.pump()['player']['profile'])
        self.pump(100) # original profile command rate gate
        self.call('action',name='setProfile',args=dict(name='Scout',avatarPng=[],face=255))
        self.assertEqual(0,self.pump()['player']['profile']['face'])

    def test_worn_quality_comes_from_accepted_pickup_and_clothing_transaction(self):
        self.call('init',spawn=[0,0,0])
        net_id=self.call('worldSpawn',items=[dict(itemId=3,quality=27,position=[0,0,-.5])])['result']['entities'][0]['netId']
        self.pump();self.call('action',name='pickupItem',args=dict(netId=net_id));state=self.pump()
        self.assertEqual(3,state['inventory']['worn']['shirt']) # original pickup auto-wears an empty clothing slot
        self.assertEqual(27,state['inventory']['wornQuality']['shirt'])
        self.call('action',name='unwearClothing',args=dict(slot=2));state=self.pump()
        page=next(p for p in state['inventory']['pages'] if any(i['id']==3 for i in p['items']))
        item=next(i for i in page['items'] if i['id']==3)
        self.call('action',name='wearClothing',args=dict(page=page['index'],x=item['x'],y=item['y'],slot=2))
        state=self.pump();self.assertEqual(3,state['inventory']['worn']['shirt']);self.assertEqual(27,state['inventory']['wornQuality']['shirt'])
        catalog={i['id']:i for i in self.call('catalog')['result']['items']}
        self.assertTrue(catalog[3]['hasCondition']);self.assertFalse(catalog[6]['hasCondition'])
        self.call('action',name='unwearClothing',args=dict(slot=2))
        state=self.pump();self.assertEqual(0,state['inventory']['worn']['shirt']);self.assertEqual(0,state['inventory']['wornQuality']['shirt'])
        self.assertEqual(27,next(i['quality'] for p in state['inventory']['pages'] for i in p['items'] if i['id']==3))
