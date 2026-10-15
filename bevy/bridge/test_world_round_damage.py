"""Original loaded-round, wear and travel costs at authoritative world contacts."""
import unittest

import test_attachment_actions
import test_bridge


class WorldRoundDamageTests(unittest.TestCase):
    setUp = test_bridge.BridgeTests.setUp
    tearDown = test_bridge.BridgeTests.tearDown
    call = test_bridge.BridgeTests.call
    pump = test_bridge.BridgeTests.pump
    configure = test_attachment_actions.AttachmentActionTests.configure
    options = test_attachment_actions.AttachmentActionTests.options
    request = test_attachment_actions.AttachmentActionTests.request

    def tree(self, distance=4):
        self.call('worldServices', resourceCount=1, resources=[dict(
            index=0, kind='tree', name='Pine_0', position=[0, 0, -distance],
            health=100, resetSeconds=60)])
        self.call('worldGeometry', meshes=[dict(resourceIndex=0,
            vertices=[[-20, -20, -distance], [20, -20, -distance],
                      [20, 20, -distance], [-20, 20, -distance]],
            indices=[0, 1, 2, 0, 2, 3])])

    def fire(self, ticks=20):
        self.call('action', name='fire', args=dict(
            origin=[0, 1.1, 0], dir=[0, 0, -1], visual=dict(aimAlpha=1)))
        state = self.pump(ticks)
        hits = [event['data'] for event in state['events'] if event['kind'] == 'resourceHit']
        self.assertEqual(1, len(hits))
        return hits[0], state

    def test_tree_uses_the_loaded_slug_instead_of_the_guns_buckshot_damage(self):
        self.configure(gun=dict(Id=112, GunAmmo=6, GunMagId=113),
                       items=[(5000, 0, 0, 1, 100)])
        self.assertTrue(self.request('chooseShellType', itemId=5000)['queued'])
        self.pump(180)
        self.tree()
        hit, state = self.fire()
        self.assertEqual(40, hit['damage'])
        self.assertEqual(60, hit['health'])
        self.assertEqual(0, state['player']['ammo'])

    def test_tree_receives_launch_condition_once(self):
        self.configure(gun=dict(Quality=25))
        self.tree()
        hit, _ = self.fire()
        self.assertAlmostEqual(15, hit['damage'], places=4)
        self.assertAlmostEqual(85, hit['health'], places=4)

    def test_tree_uses_distance_flown_by_the_actual_accepted_rifle_round(self):
        self.tree(160)
        hit, state = self.fire()
        impact = next(event['data'] for event in state['events'] if event['kind'] == 'impact')
        fire = next(event['data'] for event in state['events'] if event['kind'] == 'fire')
        origin = fire['origin']
        point = impact['pos']
        distance = sum((point[axis] - origin[axis]) ** 2 for axis in ('x', 'y', 'z')) ** .5
        # Original Eaglefire .dat falloff113..212m to.65; TreeTrunk gets the
        # same b.Damage * b.FalloffAt(point) as original wildlife contacts.
        expected = 20 * (1 - .35 * min(1, max(0, (distance - 113) / (212 - 113))))
        self.assertAlmostEqual(expected, hit['damage'], delta=.005)
        self.assertAlmostEqual(100 - expected, hit['health'], delta=.005)
