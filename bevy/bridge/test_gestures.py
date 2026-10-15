"""The original server appearance publisher resolves real gesture requests."""
import unittest
import test_bridge


class GestureTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump

    def request(self,gesture):
        self.call('action',name='requestGesture',args=dict(gesture=gesture))
        return self.pump(14)

    def test_surrender_start_stop_is_replicated_and_system_arrest_cannot_be_forged(self):
        self.assertEqual(0,self.request(6)['player'].get('gesture',0),'source refuses a gesture with a gun in hand')
        self.call('tick',input=dict(heldItemId=0));self.pump(5)
        state=self.request(6);self.assertEqual(6,state['player'].get('gesture',0));self.assertFalse(state['player']['arrested'])
        self.assertEqual(6,self.request(11)['player']['gesture'],'ARREST_START is applied by the captor, never requested')
        self.assertEqual(6,self.request(12)['player']['gesture'],'ARREST_STOP cannot clear surrender')
        self.call('action',name='struggle',args=dict(side=1));self.assertEqual(6,self.pump(14)['player']['gesture'])
        self.assertEqual(0,self.request(7)['player']['gesture'])

    def test_one_shot_is_an_event_and_rest_keeps_original_stance_gate(self):
        self.call('tick',input=dict(heldItemId=0));self.pump(5)
        state=self.request(9);waves=[e for e in state['events'] if e['kind']=='gesture']
        self.assertEqual(1,len(waves));self.assertEqual(9,waves[0]['data']['gesture'])
        self.assertEqual(state['playerId'],waves[0]['data']['playerId']);self.assertEqual(0,state['player']['gesture'])
        self.assertEqual(0,self.request(13)['player']['gesture'],'rest requires crouch')
        self.call('simulate',input=dict(stance='crouch'));self.pump(5)
        self.assertEqual(13,self.request(13)['player']['gesture'])
        self.assertEqual(0,self.request(14)['player']['gesture'])
        state=self.request(1);self.assertEqual(0,state['player']['gesture'],'inventory start stays local, not a remote appearance')

    def test_pending_old_world_gesture_does_not_leak_into_reused_player_id(self):
        self.call('tick',input=dict(heldItemId=0));state=self.pump(5)
        while state['tick']%10!=2:state=self.call('tick')['state']
        self.call('action',name='requestGesture',args=dict(gesture=6));self.pump(2)
        state=self.call('init',starterKit=False,spawn=[0,0,0])['state']
        self.assertEqual(0,state['player']['gesture']);self.assertFalse(state['player']['arrested'])
        self.assertEqual(0,self.pump(20)['player']['gesture'])
