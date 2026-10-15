"""Accepted player hits use the source cartridge/loaded-round damage over UDP."""
import socket
import time
import unittest

import test_attachment_actions
import test_bridge


class PlayerDamageTests(unittest.TestCase):
    setUp = test_bridge.BridgeTests.setUp
    tearDown = test_bridge.BridgeTests.tearDown
    call = test_bridge.BridgeTests.call
    pump = test_bridge.BridgeTests.pump
    configure = test_attachment_actions.AttachmentActionTests.configure
    options = test_attachment_actions.AttachmentActionTests.options
    request = test_attachment_actions.AttachmentActionTests.request

    def hit_remote(self, height, expected, slug=False):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        self.call('init', contentRoot=str(test_bridge.ROOT / 'game/content'),
                  listenPort=port, starterKit=True, allowCheats=True, spawn=[0, 0, 0])
        if slug:
            self.configure(gun=dict(Id=112, GunAmmo=6, GunMagId=113),
                           items=[(5000, 0, 0, 1, 100)])
            self.assertTrue(self.request('chooseShellType', itemId=5000)['queued'])
            loaded = self.pump(180)
            self.assertEqual(5000, loaded['weapon']['shellItemId'])
            self.assertEqual(1, loaded['weapon']['ammo'])
        remote = test_bridge.BridgeTests()
        remote.setUp()
        try:
            remote.call('init', contentRoot=str(test_bridge.ROOT / 'game/content'),
                        connectHost='127.0.0.1', connectPort=port)

            def pump(count):
                for _ in range(count):
                    self.call('tick')
                    state = remote.call('tick')['state']
                    time.sleep(.001)
                return state

            self.assertTrue(pump(40)['connected'])
            remote.call('action', name='console', args=dict(text='teleport 0 0 -4'))
            before = pump(20)
            self.assertAlmostEqual(-4, before['player']['position'][2], places=2)
            ammo = self.call('snapshot')['state']['player']['ammo']
            self.call('action', name='fire',
                      args=dict(origin=[0, height, 0], dir=[0, 0, -1], visual=dict(aimAlpha=1)))
            after = pump(30)
            hits = [event for event in after['events'] if event['kind'] == 'hurt']
            self.assertEqual(1, len(hits), 'One accepted bullet reaches this owner exactly once')
            hit = hits[0]['data']
            self.assertAlmostEqual(expected, hit['damage'], places=3)
            self.assertAlmostEqual(before['player']['hp'] - expected, after['player']['hp'], places=3)
            self.assertTrue(hit['hasSource'])
            self.assertGreater(hit['sourcePos']['z'], after['player']['position'][2])
            self.assertEqual(ammo - 1, self.call('snapshot')['state']['player']['ammo'])
            # Further replica ticks must not replay damage or spend ammunition.
            later = pump(20)
            self.assertEqual(after['player']['hp'], later['player']['hp'])
            self.assertEqual(1, sum(event['kind'] == 'hurt' for event in later['events']))
        finally:
            remote.tearDown()

    def test_current_cartridge_damage_reaches_all_existing_player_zones(self):
        # Eaglefire's current .dat is Damage20; Player_Damage is absent. The
        # original shared Humanoid/ServerGunProfile zone table is 2/1/.6.
        for height, damage in [(1.6, 40), (1.1, 20), (.3, 12)]:
            with self.subTest(height=height):
                self.hit_remote(height, damage)

    def test_loaded_slug_uses_its_override_and_spends_one_shell(self):
        # Bluntforce buckshot is12 per pellet; original ItemCatalog slug5000
        # carries40 in one projectile. Merely fixing Catalog.Damage misses this.
        self.hit_remote(1.1, 40, slug=True)
