"""Source debug commands must enter real server damage/death and honor cheats."""
import unittest
import test_bridge


class PlayerDebugTests(unittest.TestCase):
    setUp = test_bridge.BridgeTests.setUp
    tearDown = test_bridge.BridgeTests.tearDown
    call = test_bridge.BridgeTests.call
    pump = test_bridge.BridgeTests.pump

    def command(self, text):
        return self.call('action', name='console', args=dict(text=text))

    def test_damage_is_queued_accepted_replicated_and_cannot_heal(self):
        self.command('sethp 37')
        self.assertEqual(100, self.call('snapshot')['state']['player']['hp'])
        self.assertEqual(37, self.pump()['player']['hp'])
        for text in ('sethp 100', 'sethp NaN', 'sethp Infinity', 'sethp'):
            self.command(text)
            self.assertEqual(37, self.pump()['player']['hp'])
        self.command('kill')
        state = self.pump()
        self.assertFalse(state['player']['alive'])
        deaths = [e for e in state['events'] if e['kind'] == 'death']
        self.assertEqual(1, len(deaths))
        self.command('kill')
        self.assertEqual(1, sum(e['kind'] == 'death' for e in self.pump()['events']))
        self.assertTrue(self.pump(180)['player']['alive'])

    def test_disabled_cheats_leave_actual_health_and_alive_unchanged(self):
        self.call('init', allowCheats=False, spawn=[0, 0, 0])
        for text in ('kill', 'sethp 0'):
            self.command(text)
            state = self.pump()
            self.assertTrue(state['player']['alive'])
            self.assertEqual(100, state['player']['hp'])
            self.assertIn('disabled', state['events'][-1]['data']['text'])
