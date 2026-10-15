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

    def test_original_metadata_and_advisory_refusal_do_not_optimistically_pose(self):
        rows=self.call('catalog')['result']['gestures'];by_id={row['id']:row for row in rows}
        self.assertEqual(list(range(18)),[row['id'] for row in rows])
        self.assertEqual(('Gesture_Surrender',True,7,True,None),tuple(by_id[6].get(k) for k in ('clip','loops','stops','requestable','requiredStance')))
        self.assertEqual('crouch',by_id[13]['requiredStance']);self.assertEqual('stand',by_id[16]['requiredStance'])
        self.assertFalse(by_id[11]['requestable']);self.assertFalse(by_id[1]['broadcast']);self.assertIsNone(by_id[16].get('clip'))
        reply=self.call('action',name='requestGesture',args=dict(gesture=6))
        self.assertEqual(dict(queued=False,reason='Not with something in your hands.'),reply['result'])
        self.assertEqual(0,reply['state']['player']['gesture'])
        self.assertTrue(any(e['kind']=='gestureRejected' and e['data']['text']==reply['result']['reason'] for e in reply['state']['events']))
        self.call('tick',input=dict(heldItemId=0));self.pump(5)
        reply=self.call('action',name='requestGesture',args=dict(gesture=13))
        self.assertEqual('Only while crouch.',reply['result']['reason']);self.assertFalse(reply['result']['queued'])
        reply=self.call('action',name='requestGesture',args=dict(gesture=11))
        self.assertEqual("That one isn't yours to ask for.",reply['result']['reason'])
        reply=self.call('action',name='requestGesture',args=dict(gesture=6))
        self.assertTrue(reply['result']['queued']);self.assertIsNone(reply['result'].get('reason'))
        self.assertEqual(0,reply['state']['player']['gesture'],'advisory admission never mutates the replica')
        self.assertEqual(6,self.pump(14)['player']['gesture'])
