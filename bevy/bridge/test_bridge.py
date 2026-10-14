#!/usr/bin/env python3
"""Protocol + authority integration tests against the running managed executable."""
import json
from pathlib import Path
import subprocess
import struct
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

    def test_events_survive_result_only_calls_and_ack(self):
        self.call('action', name='console', args=dict(text='xp 5'))
        events = self.pump()['events']
        self.assertTrue(any(e['kind'] == 'console' for e in events))
        last = events[-1]['seq']
        self.call('simulate', input=dict(moveY=0, grounded=True))
        self.assertEqual(events, self.call('snapshot')['state']['events'])
        self.assertEqual([], self.call('snapshot', ackEvents=last)['state']['events'])

    def test_headroom_uses_original_stance_gate(self):
        self.call('simulate', input=dict(stance='crouch', grounded=True))
        result = self.call('simulate', input=dict(stance='stand', grounded=True, capsuleHeight=1.2, headroom=False))['result']
        self.assertEqual('crouch', result['stance'])
        self.assertAlmostEqual(1.2, result['height'])

    def test_authoritative_bullet_hits_registered_wall(self):
        self.call('worldGeometry', meshes=[dict(vertices=[[-2, 0, -2], [2, 0, -2], [0, 4, -2]])])
        self.call('action', name='fire', args={})
        state = self.pump()
        impacts = [e for e in state['events'] if e['kind'] == 'impact']
        self.assertTrue(impacts, state['events'])
        self.assertAlmostEqual(-2, impacts[0]['data']['pos']['z'], places=2)
        self.assertEqual(29, state['player']['ammo'])

    def test_heightfield_hole_and_nearest_triangle(self):
        self.call('worldGeometry', terrainPatches=[dict(origin=[0, 0], cells=1, spacing=2, heights=[1, 1, 1, 1])])
        hit = self.call('worldRay', **{'from': [1, 5, 1], 'to': [1, -2, 1]})['result']
        self.assertTrue(hit['hit'])
        self.assertAlmostEqual(1, hit['point'][1])
        self.call('worldGeometry', terrainPatches=[dict(origin=[0, 0], cells=1, spacing=2, heights=[1, 1, 1, 1], holes=[True])])
        self.assertFalse(self.call('worldRay', **{'from': [1, 5, 1], 'to': [1, -2, 1]})['result']['hit'])

    def test_original_catalog_overrides_and_generated_recipes(self):
        catalog = self.call('catalog')['result']['items']
        water = next(i for i in catalog if i['id'] == 14)
        guns = [i for i in catalog if i.get('mesh', '').endswith('_gun.txt')]
        source_guns = [p for p in (ROOT / 'game/content').glob('*.dat')
                       if p.with_name(p.stem + '_gun.txt').exists()]
        self.assertGreater(len(source_guns), 50)
        self.assertEqual(len(source_guns), len(guns))
        eaglefire = next(i for i in guns if i['id'] == 4)
        self.assertEqual('eaglefire', eaglefire['asset'])
        self.assertEqual(30, eaglefire['gunAmmoMax'])
        self.assertEqual(9, len([i for i in guns if i['movementMultiplier'] < .999]))
        self.assertTrue(water['isFluidContainer'])
        self.assertTrue(water['icon'].startswith('items/icons/'))
        recipes = self.call('snapshot')['state']['recipes']
        self.assertTrue(any(r['name'].startswith('Split ') for r in recipes))
        self.assertTrue(any(r['name'].startswith('Salvage ') for r in recipes))
        steady = self.call('tick')['state']
        self.assertNotIn('recipes', steady)

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

    def test_clock_replication_restores_day_and_duration(self):
        initial = self.call('snapshot')['state']['worldClock']
        self.assertEqual(1440, initial['dayLengthSeconds'])
        self.assertAlmostEqual(.35, initial['timeOfDay'], places=3)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'clock.json'
            self.call('save', path=str(path))
            saved = json.loads(path.read_text())
            saved.update(Day=7, TimeOfDay01=.999, DayLengthSeconds=1)
            path.write_text(json.dumps(saved))
            self.call('load', path=str(path))
            state = self.pump()['worldClock']
            self.assertEqual(8, state['day'])
            self.assertEqual(1, state['dayLengthSeconds'])
            self.call('save', path=str(path))
            saved = json.loads(path.read_text())
            self.assertEqual(8, saved['Day'])
            self.assertEqual(1, saved['DayLengthSeconds'])

    def test_world_services_forage_reach_regrowth_and_save(self):
        self.call('worldServices', resourceCount=2, resources=[
            dict(index=0, rewardItemId=13, position=[0, 0, -1], resetSeconds=.4),
            dict(index=1, rewardItemId=13, position=[50, 0, 0], resetSeconds=.4)])
        self.call('action', name='forageResource', args=dict(index=1))
        self.assertEqual([], self.pump()['resources']['dead'])
        self.call('action', name='forageResource', args=dict(index=0))
        state = self.pump()
        self.assertEqual([0], state['resources']['dead'])
        self.assertTrue(any(e['kind'] == 'resourceHarvested' for e in state['events']))
        self.assertEqual([], self.pump(25)['resources']['dead'])
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'services.json')
            self.call('save', path=path)
            self.call('load', path=path)
            self.call('action', name='forageResource', args=dict(index=0))
            self.assertEqual([0], self.pump()['resources']['dead'])

    def test_destructible_bullet_damage_removes_authoritative_occlusion(self):
        self.call('worldServices', destructibleCount=1,
                  destructibles=[dict(index=0, health=25, resetSeconds=.6)])
        self.call('worldGeometry', meshes=[dict(destructibleIndex=0,
                  vertices=[[-2, 0, -2], [2, 0, -2], [0, 4, -2]])])
        self.call('action', name='fire', args={})
        self.assertEqual([0], self.pump()['destructibles']['dead'])
        ray = {'from': [0, 1.6, 0], 'to': [0, 1.6, -3]}
        self.assertFalse(self.call('worldRay', **ray)['result']['hit'])
        self.assertEqual([], self.pump(40)['destructibles']['dead'])
        self.assertTrue(self.call('worldRay', **ray)['result']['hit'])

    def test_real_map_services_register_all_and_do_not_duplicate_after_load(self):
        request = dict(mapName='PEI', mapRoot='')
        result = self.call('mapServices', **request)['result']
        self.assertEqual(696, result['containerCount'])
        self.assertEqual(11, result['fixtureCount'])
        self.assertEqual(1, result['deadzoneCount'])
        self.assertEqual(len(result['resources']), result['resourceCount'])
        self.assertGreater(result['destructibleCount'], 1000)
        self.assertEqual(result, self.call('mapServices', **request)['result'])
        state = self.pump(10)
        self.assertEqual(result['containerCount'], len(state['containers']))
        containers = [o for o in result['objects'] if 'containerNetId' in o]
        self.assertEqual(result['containerCount'], len(containers))
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'world.json')
            self.call('save', path=path)
            self.call('load', path=path)
            restored = self.call('mapServices', **request)['result']
            self.assertEqual(result['containerCount'], restored['containerCount'])
            self.assertEqual(result['containerCount'], len(self.pump(10)['containers']))

    def test_loot_save_load_preserves_taken_points_and_streaming(self):
        # Minimal real retail binary format, so this exercises the original table
        # parser/roll and adapter lifecycle without requiring a Steam install.
        with tempfile.TemporaryDirectory() as folder:
            spawns = Path(folder) / 'Spawns'
            spawns.mkdir()
            table = bytes([4, 1, 255, 255, 255, 4]) + b'Food'
            table += struct.pack('<HBB', 0, 1, 4) + b'Food'
            table += struct.pack('<fBH', 1., 1, 13)
            (spawns / 'Items.dat').write_bytes(table)
            points = [(0., 0., 1.), (1., 0., 1.), (1000., 0., 0.)]
            jars = bytes([1]) + struct.pack('<H', len(points))
            jars += b''.join(struct.pack('<Bfff', 0, *p) for p in points)
            jars += bytes(2 * (64 * 64 - 1))
            (spawns / 'Jars.dat').write_bytes(jars)
            # Roof is combat-solid, while loot settles against terrain only.
            self.call('worldGeometry', meshes=[
                dict(terrain=True, vertices=[[-10, .5, -10], [10, .5, -10], [0, .5, 10]]),
                dict(vertices=[[-10, 10, -10], [10, 10, -10], [0, 10, 10]])])
            self.assertEqual(2, self.call('worldLoot', mapRoot=folder)['result']['live'])
            state = self.pump()
            self.assertEqual(2, len(state['worldItems']))
            self.assertAlmostEqual(.55, state['worldItems'][0]['position'][1], places=2)
            self.call('action', name='pickupItem', args=dict(netId=state['worldItems'][0]['netId']))
            self.assertEqual(1, len(self.pump()['worldItems']))
            path = str(Path(folder) / 'saved.json')
            self.call('save', path=path)
            self.call('load', path=path)
            self.assertEqual(1, len(self.pump(25)['worldItems']))
            self.assertEqual(1, self.call('worldLoot', mapRoot=folder)['result']['live'])
            self.call('action', name='console', args=dict(text='teleport 1000 0 0'))
            far = self.pump(25)['worldItems']
            self.assertEqual(1, len(far))
            self.assertAlmostEqual(1000, far[0]['position'][0])
            self.call('action', name='console', args=dict(text='teleport 0 0 0'))
            near = self.pump(25)['worldItems']
            self.assertEqual(1, len(near))
            self.assertAlmostEqual(1, near[0]['position'][0])


if __name__ == '__main__':
    unittest.main()
