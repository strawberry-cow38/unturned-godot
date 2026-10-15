"""Original scope steady shell prediction and real server oxygen round trip."""
import unittest
import test_bridge

class ScopeSteadyTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump

    def intent(self,key=True,aiming=True,zoom=4):
        self.call('simulate',input=dict(steadyKey=key,aiming=aiming,scopeZoom=zoom,equipmentAllowsSprint=not aiming))

    def test_magnifying_ads_gate_and_original_network_oxygen_cost(self):
        for key,aiming,zoom in [(True,True,1),(True,False,4),(False,True,4)]:
            self.intent(key,aiming,zoom);state=self.pump(25)
            self.assertEqual(1,state['player']['oxygen']);self.assertFalse(state['steady']['active'])
        self.intent();state=self.pump(100)
        # Repeated snapshots would restore1 if only the presentation copy drained.
        self.assertAlmostEqual(1-.8/6*2,state['player']['oxygen'],delta=.008)
        self.assertTrue(state['steady']['active']);self.assertLess(state['steady']['rateScale'],.186)
        self.assertEqual('stand',state['player']['stance'])

    def test_floor_releases_sway_and_held_key_cannot_burn_off_lockout(self):
        self.intent();states=[self.call('tick')['state'] for _ in range(320)]
        self.assertGreaterEqual(min(s['player']['oxygen'] for s in states),.199)
        state=states[-1];self.assertFalse(state['steady']['active']);self.assertEqual(2.5,state['steady']['lockout'])
        # This fails if the shell discards its predicted drain between quantized
        # replicas: the floor is skipped and the scope remains active forever.
        state=self.pump(200)
        self.assertEqual(1,state['player']['oxygen']);self.assertFalse(state['steady']['active'])
        self.assertEqual(2.5,state['steady']['lockout']);self.assertEqual(1,state['steady']['rateScale'])
        self.assertEqual(100,state['player']['hp'])
        self.intent(key=False);self.pump(100);self.intent();state=self.pump(5)
        self.assertFalse(state['steady']['active']);self.assertAlmostEqual(.5,state['steady']['lockout'],places=4)
        self.intent(key=False);self.pump(30);self.intent();state=self.pump(20)
        self.assertTrue(state['steady']['active']);self.assertLess(state['player']['oxygen'],.97)

    def test_session_reset_clears_transient_hold_and_lockout(self):
        self.intent();self.pump(320)
        state=self.call('init')['state']
        self.assertEqual(dict(active=False,blend=0,rateScale=1,lockout=0),state['steady'])
        self.assertEqual(1,state['player']['oxygen'])
