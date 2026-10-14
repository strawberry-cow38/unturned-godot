#!/usr/bin/env python3
"""Protocol + authority integration tests against the running managed executable."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / 'bevy/bridge/bin/Debug/net8.0/UnturnedBevy.Bridge.dll'
DOTNET = str(Path.home() / '.dotnet/dotnet')


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.errors = tempfile.TemporaryFile(mode='w+')
        self.process = subprocess.Popen([DOTNET, str(DLL)], cwd=ROOT, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=self.errors, text=True)
        self.seq = 0
        self.call('init', contentRoot=str(ROOT / 'game/content'), starterKit=True, spawn=[0, 0, 0])

    def tearDown(self):
        self.call('shutdown')
        self.process.wait(timeout=5)
        self.process.stdin.close()
        self.process.stdout.close()
        self.errors.close()

    def call(self, op, **args):
        self.seq += 1
        self.process.stdin.write(json.dumps(dict(id=self.seq, op=op, **args)) + '\n')
        self.process.stdin.flush()
        response = json.loads(self.process.stdout.readline())
        self.assertEqual(self.seq, response['id'])
        self.assertTrue(response['ok'], response)
        return response

    def pump(self, count=6):
        for _ in range(count):
            result = self.call('tick')
        return result['state']

    def test_movement_preserves_stance_speed_and_yaw(self):
        r = self.call('simulate', input=dict(moveX=0, moveY=1, grounded=True, stance='sprint', yaw=90))
        velocity = r['result']['velocity']
        self.assertAlmostEqual(velocity[0], -7, places=4)
        self.assertAlmostEqual(velocity[2], 0, places=4)
        r = self.call('simulate', input=dict(moveX=1, moveY=1, grounded=True, stance='crouch', yaw=0))
        v = r['result']['velocity']
        self.assertAlmostEqual((v[0] ** 2 + v[2] ** 2) ** .5, 2.5, places=4)

    def test_drop_pickup_is_real_replica_roundtrip(self):
        s = self.call('snapshot')['state']
        item = s['inventory']['pages'][2]['items'][0]
        self.call('action', name='dropItem', args=dict(page=2, x=item['x'], y=item['y']))
        s = self.pump()
        self.assertEqual(1, len(s['worldItems']))
        self.assertEqual(item['id'], s['worldItems'][0]['itemId'])
        self.call('action', name='pickupItem', args=dict(netId=s['worldItems'][0]['netId']))
        s = self.pump()
        self.assertEqual([], s['worldItems'])
        self.assertTrue(any(j['id'] == item['id'] for p in s['inventory']['pages'] for j in p['items']))

    def test_server_rejects_impossible_item_move(self):
        before = self.call('snapshot')['state']['inventory']
        self.call('action', name='moveItem', args=dict(page0=2, x0=0, y0=0, page1=2, x1=254, y1=254, rot1=0))
        after = self.pump()['inventory']
        self.assertEqual(before, after)

    def test_authority_rejects_teleport(self):
        self.call('tick', input=dict(position=[1000, 0, 1000], velocity=[0, 0, 0]))
        state = self.pump()
        self.assertGreater(state['player']['correctionVersion'], 0)
        self.assertLess(abs(state['player']['position'][0]), 1)

    def test_save_uses_original_format_and_restores_inventory(self):
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'world.json')
            before = self.call('snapshot')['state']['inventory']
            self.call('save', path=path)
            saved = json.loads(Path(path).read_text())
            self.assertEqual(2, saved['Version'])
            self.assertGreater(len(saved['Players']), 0)
            item = before['pages'][2]['items'][0]
            self.call('action', name='dropItem', args=dict(page=2, x=item['x'], y=item['y']))
            self.pump()
            self.call('load', path=path)
            self.assertEqual(before, self.pump()['inventory'])


if __name__ == '__main__':
    unittest.main()
