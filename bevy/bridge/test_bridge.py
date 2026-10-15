#!/usr/bin/env python3
"""Protocol + authority integration tests against the running managed executable."""
import json
import os
from pathlib import Path
import subprocess
import struct
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
DLL = Path(os.environ.get('UG_BRIDGE_DLL', ROOT / 'bevy/bridge/bin/Debug/net8.0/UnturnedBevy.Bridge.dll'))
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

    def reject(self, op, **args):
        self.seq += 1
        self.process.stdin.write(json.dumps(dict(id=self.seq, op=op, **args)) + '\n')
        self.process.stdin.flush()
        response = json.loads(self.process.stdout.readline())
        self.assertEqual(self.seq, response['id'])
        self.assertFalse(response['ok'], response)
        return response

    def pump(self, count=6):
        for _ in range(count):
            result = self.call('tick')
        return result['state']

    def test_rebase_drains_old_frame_actions_preserves_clock_ids_and_geometry(self):
        self.call('init', spawn=[1025, 0, 0], starterKit=True)
        self.call('worldServices', objectDoors=[dict(netId=900, position=[1025, 0, -1])],
                  resourceCount=1, resources=[dict(index=0, position=[1025, 0, 1], rewardItemId=13, resetSeconds=10)])
        vehicle = self.call('worldVehicles', vehicles=[dict(typeId=1,key='origin-car',position=[1025,0,-2],health=300,healthMax=300,fuel=10)])['result']['vehicles'][0]['netId']
        triangle = [[1020,-1,-3],[1030,-1,-3],[1025,5,-3]]
        self.call('worldGeometry', meshes=[dict(vertices=triangle)])
        self.call('worldDynamicGeometry', key='car', meshes=[dict(vehicleNetId=vehicle,vertices=[[1020,-1,-2],[1030,-1,-2],[1025,5,-2]])])
        before = self.pump()
        item = before['inventory']['pages'][2]['items'][0]
        self.call('action',name='dropItem',args=dict(page=2,x=item['x'],y=item['y']))
        # Reliable drop is still queued when the atomic coordinate barrier begins.
        result = self.call('rebase', shift=[1024,0,0], expectedEpoch=0)
        state = result['state']
        self.assertEqual(before['tick'], state['tick'])
        self.assertEqual(before['worldClock'], state['worldClock'])
        self.assertAlmostEqual(before['player']['food'],state['player']['food'],places=4) # barrier can catch up one pending replica
        self.assertEqual(before['player']['correctionVersion'],state['player']['correctionVersion'])
        self.assertEqual([1,0,0], state['player']['position'])
        self.assertEqual(dict(epoch=1,x=1024,z=0),state['origin'])
        self.assertEqual(vehicle,state['vehicles'][0]['netId'])
        self.assertEqual([1,0,-2],state['vehicles'][0]['position'])
        self.assertEqual(1,len(state['worldItems']))
        self.assertLess(abs(state['worldItems'][0]['position'][0]-1),4) # original drop includes scatter
        hit = self.call('worldRay',**dict(**{'from':[1,1,0]},to=[1,1,-4]))['result']
        self.assertTrue(hit['hit']); self.assertEqual(vehicle,hit['vehicleNetId'])
        self.call('action',name='toggleObjectDoor',args=dict(netId=900),originEpoch=1)
        self.call('action',name='forageResource',args=dict(index=0),originEpoch=1)
        state=self.pump()
        self.assertTrue(state['interactables']['objectDoors'][0]['open'])
        self.assertEqual([0],state['resources']['dead'])
        self.assertEqual([1,0,0],state['player']['position'],'translated recovery anchor accepts next local pose')
        self.reject('tick', input=dict(position=[1025,0,0]),originEpoch=0)
        self.reject('rebase',shift=[1024,0,0],expectedEpoch=0)
        self.assertEqual(state['tick'],self.call('snapshot')['state']['tick'])

    def test_rebase_translates_retained_fire_facts_and_pending_grenade_without_restarting_fuse(self):
        self.call('init',spawn=[1025,0,0],starterKit=True)
        self.call('action',name='console',args=dict(text='give 254'))
        self.call('tick',input=dict(heldItemId=4))
        self.pump()
        self.call('action',name='fire',args=dict(origin=[1025,1.6,0],dir=[0,0,-1]))
        self.pump(3)
        self.call('action',name='grenade',args=dict(origin=[1025,1.6,0],velocity=[0,1,-2],itemId=254))
        state=self.call('rebase',shift=[1024,0,0],expectedEpoch=0)['state']
        fire=next(e for e in state['events'] if e['kind']=='fire')
        self.assertAlmostEqual(1,fire['data']['origin']['x'])
        state=self.pump(5)
        self.assertEqual(1,len(state['projectiles']))
        projectile=state['projectiles'][0]; self.assertAlmostEqual(1,projectile['position'][0])
        age=state['tick']
        state=self.call('rebase',shift=[-1024,0,0],expectedEpoch=1)['state']
        self.assertEqual(age,state['tick'])
        self.assertEqual(projectile['netId'],state['projectiles'][0]['netId'])
        self.assertAlmostEqual(1025,state['projectiles'][0]['position'][0])
        self.pump(150)
        events=self.call('snapshot')['state']['events']
        self.assertEqual(1,len([e for e in events if e['kind']=='throwAccepted']))
        explosion=next(e for e in events if e['kind']=='grenadeExploded')
        self.assertAlmostEqual(1025,explosion['data']['pos']['x'])

    def test_rebase_save_keeps_absolute_origin_and_distant_authoritative_items(self):
        large=256_000_000.0
        self.call('init',spawn=[1025,0,0],origin=[large,-large],starterKit=True)
        self.call('worldSpawn',items=[dict(itemId=67,position=[-12000.125,2,15000.25])])
        self.call('worldServices',objectDoors=[dict(netId=901,position=[1025,0,-1])])
        self.pump()
        self.call('rebase',shift=[1024,0,0],expectedEpoch=0)
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'origin.json')
            self.call('save',path=path)
            saved=json.loads(Path(path).read_text())
            self.assertEqual(large+1024,saved['BevyBridge']['OriginX'])
            self.assertEqual(-13024.125,saved['WorldItems'][0]['X'])
            self.call('rebase',shift=[-1024,0,0],expectedEpoch=1)
            state=self.call('load',path=path)['state']
            self.assertEqual(large+1024,state['origin']['x'])
            self.assertEqual(3,state['origin']['epoch'])
            self.assertEqual([1,0,0],state['player']['position'])
            self.call('action',name='toggleObjectDoor',args=dict(netId=901))
            self.assertTrue(self.pump()['interactables']['objectDoors'][0]['open'])
            self.call('save',path=path)
            saved=json.loads(Path(path).read_text())
            self.assertEqual(-13024.125,saved['WorldItems'][0]['X'],'loading far objects never clamps stored authority to the wire window')
            self.assertEqual(15000.25,saved['WorldItems'][0]['Z'])

    def test_distant_respawn_rebases_to_original_absolute_spawn_before_recovery_packet(self):
        self.call('init',spawn=[0,0,0],starterKit=True)
        for epoch in range(5):
            self.call('action',name='console',args=dict(text='tp 1025 0 0'))
            state=self.pump(8)
            self.assertAlmostEqual(1025,state['player']['position'][0])
            self.call('rebase',shift=[1024,0,0],expectedEpoch=epoch)
        state=self.call('snapshot')['state']
        self.assertEqual(5120,state['origin']['x'])
        self.call('action',name='console',args=dict(text='give 254'))
        self.pump()
        self.call('action',name='grenade',args=dict(origin=[1,1.6,0],velocity=[0,0,0],itemId=254))
        state=self.pump(155)
        self.assertFalse(state['player']['alive'])
        state=self.call('action',name='respawn',args={})['state']
        self.assertEqual(0,state['origin']['x'])
        state=self.pump(8)
        self.assertTrue(state['player']['alive'])
        self.assertEqual([0,0,0],state['player']['position'],'actual original spawn survives five origin shifts')

    def test_host_vehicle_damage_preserves_split_pools_and_rejects_invalid_parts(self):
        vehicle=self.call('worldVehicles',vehicles=[dict(typeId=1,key='damage',position=[0,0,-2],health=300,healthMax=300,
            engineRegion=dict(rear=False,threshold=0,health=120),hitRegions=[dict(bodyPart='mainRotor',center=[0,1,0],size=[1,1,1],health=50)])])['result']['vehicles'][0]['netId']
        self.call('worldVehicleDamage',netId=vehicle,amount=10,bodyPart='mainRotor')
        self.call('worldVehicleDamage',netId=vehicle,amount=20,bodyPart='hull',engineShare=.5)
        state=self.pump(); car=state['vehicles'][0]
        self.assertEqual(280,car['health']); self.assertEqual(110,car['engineHealth']); self.assertEqual(40,car['mainRotorHealth'])
        self.reject('worldVehicleDamage',netId=vehicle,amount=1,bodyPart='tire:20')
        self.call('worldVehicleDamage',netId=vehicle,amount=1,bodyPart='tire:1')
        self.assertEqual(2,self.pump()['vehicles'][0]['poppedTireMask'])
        self.call('worldVehicleState', vehicles=[dict(netId=vehicle,position=[0,0,-3])])
        self.assertEqual([0,0,-3], self.pump()['vehicles'][0]['position'], 'host write after prior snapshot is dirtied for the next snapshot')

    def test_map_mapping_positions_keep_camel_case_and_saved_frame_after_rebase(self):
        request = dict(mapName='PEI', mapRoot='/tmp/origin-map')
        self.call('worldServices', mapKey='PEI|/tmp/origin-map',
            mapObjects=[dict(SourceIndex=7, Name='Container_0', Position=[1025,0,0])],
            mapResources=[dict(index=0,name='Pine',instance=3,position=[1026,0,0])])
        self.call('rebase',shift=[1024,0,0],expectedEpoch=0)
        def check():
            mapping = self.call('mapServices', **request)['result']
            self.assertEqual([1,0,0],mapping['objects'][0]['position'])
            self.assertEqual(7,mapping['objects'][0]['sourceIndex'])
            self.assertEqual([2,0,0],mapping['resources'][0]['position'])
        check()
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder)/'mapping.json'); self.call('save',path=path)
            saved = json.loads(Path(path).read_text())['BevyBridge']['Services'][0]
            self.assertEqual([1,0,0],saved['mapObjects'][0]['Position'])
            self.assertEqual([2,0,0],saved['mapResources'][0]['position'])
            self.call('load',path=path); check()

    def test_rebase_delivers_queued_console_teleport_recovery_before_shifting(self):
        before=self.call('init',spawn=[1025,40,12])['state']
        self.call('action',name='console',args=dict(text='teleport 1030 42 18'))
        state=self.call('rebase',shift=[1024,0,0],expectedEpoch=0)['state']
        self.assertEqual(before['tick'],state['tick'])
        self.assertEqual([6,42,18],state['player']['position'])
        self.assertEqual(before['player']['correctionVersion']+1,state['player']['correctionVersion'])
        self.assertEqual([6,42,18],self.pump()['player']['position'])

    def test_host_aircraft_immediate_explosion_and_rotor_prop_damage(self):
        self.call('worldServices',destructibleCount=1,destructibles=[dict(index=0,health=30,resetSeconds=0)])
        spec = dict(typeId=1,key='crash',position=[0,0,-20],health=300,healthMax=300,
                    hitRegions=[dict(bodyPart='mainRotor',center=[0,1,0],size=[1,1,1],health=50)])
        ident = self.call('worldVehicles',vehicles=[spec])['result']['vehicles'][0]['netId']
        self.reject('worldVehicleDamage',netId=ident,amount=7,bodyPart='mainRotor',destructibles=[dict(index=1,amount=34)])
        self.assertEqual(50,self.pump()['vehicles'][0]['mainRotorHealth'],'malformed strike is rejected before any mutation')
        self.call('worldVehicleDamage',netId=ident,amount=7,bodyPart='mainRotor',destructibles=[dict(index=0,amount=34)])
        state=self.pump(); self.assertEqual([0],state['destructibles']['dead'])
        self.assertEqual(43,state['vehicles'][0]['mainRotorHealth'])
        self.call('worldVehicleDamage',netId=ident,amount=300)
        self.assertFalse(self.pump(6)['vehicles'][0]['flags'] & 32,'ordinary damage preserves source four-second fuse')
        self.call('worldVehicleDamage',netId=ident,explodeNow=True)
        state=self.pump(6)
        self.assertTrue(state['vehicles'][0]['flags'] & 32)
        self.assertEqual(1,len([e for e in state['events'] if e['kind']=='vehicleExploded']))
        self.assertFalse(self.call('worldVehicleDamage',netId=ident,explodeNow=True)['result']['accepted'])

    def test_rotor_console_requires_driver_and_both_dead_start_normal_hull_fuse(self):
        spec=dict(typeId=1,key='heli',isHeli=True,position=[0,0,-1],health=300,healthMax=300,
                  seatOffsets=[[0,0,0]],engineRegion=dict(rear=False,threshold=0,health=120),
                  hitRegions=[dict(bodyPart=part,center=[0,1,0],size=[1,1,1],health=hp)
                              for part,hp in [('mainRotor',50),('tailRotor',30)]])
        ident=self.call('worldVehicles',vehicles=[spec])['result']['vehicles'][0]['netId']
        self.call('action',name='console',args=dict(text='hurtmain'))
        self.assertEqual(50,self.pump()['vehicles'][0]['mainRotorHealth'])
        self.call('action',name='enterVehicle',args=dict(netId=ident,seat=0)); self.pump()
        self.call('action',name='console',args=dict(text='hurtmain'))
        car=self.pump()['vehicles'][0]
        self.assertEqual(24,car['mainRotorHealth']); self.assertEqual(300,car['health']); self.assertEqual(120,car['engineHealth'])
        self.call('action',name='console',args=dict(text='killtail')); self.pump()
        self.call('action',name='console',args=dict(text='killmain'))
        car=self.pump()['vehicles'][0]
        self.assertEqual(0,car['health']); self.assertTrue(car['onFire']); self.assertFalse(car['flags'] & 32)

    def test_projectile_age_and_remaining_lifetime_survive_save_without_second_spend(self):
        self.call('action',name='console',args=dict(text='give 255')); self.pump()
        self.call('action',name='grenade',args=dict(origin=[0,1.6,0],velocity=[0,1,-2],itemId=255))
        state=self.pump(20); flight=state['projectiles'][0]
        self.assertEqual(255,flight['itemId']); self.assertEqual(3450,flight['lifetimeTicks'])
        self.assertEqual(flight['retireTick']-state['tick'],flight['remainingTicks'])
        self.assertEqual(3450,flight['elapsedTicks']+flight['remainingTicks'])
        facts=[e for e in state['events'] if e['kind']=='throwAccepted']
        self.assertEqual(1,len(facts)); self.assertGreater(facts[0]['tick'],0)
        self.assertLessEqual(facts[0]['tick'],state['tick'])
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'flight.json'); self.call('save',path=path)
            stored=json.loads(Path(path).read_text())['BevyBridge']['Projectiles'][0]
            self.assertEqual(flight['remainingTicks'],stored['RemainingTicks'])
            restored=self.call('load',path=path)['state']; again=restored['projectiles'][0]
            self.assertEqual(flight['remainingTicks']-6,again['remainingTicks'])
            self.assertEqual(flight['elapsedTicks']+6,again['elapsedTicks'])
            self.assertEqual(3450,again['lifetimeTicks'])
            self.assertFalse(any(e['kind']=='throwAccepted' for e in restored['events']))
            self.assertFalse(any(i['id']==255 for p in restored['inventory']['pages'] for i in p['items']))

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

    def test_retained_fire_fact_captures_shot_condition_before_holster_and_weapon_wear(self):
        self.call('init',spawn=[0,0,0])
        self.call('worldSpawn',items=[dict(itemId=4,quality=25,position=[0,0,-.5])])
        item=self.pump()['worldItems'][0]['netId']
        self.call('action',name='pickupItem',args=dict(netId=item));self.pump()
        self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));before=self.pump()
        self.assertEqual(25,before['inventory']['pages'][0]['items'][0]['quality'])
        self.call('action',name='fire',args={});self.pump(2)
        self.call('tick',input=dict(heldItemId=0));after=self.pump()
        self.assertNotIn('held',after['weapon'])
        facts=[e for e in after['events'] if e['kind']=='fire'];self.assertEqual(1,len(facts))
        shot=facts[0]['data'];self.assertEqual(25,shot['quality']);self.assertEqual(4,shot['itemId'])
        self.assertEqual(1.5,shot['handlingPenalty']);self.assertEqual(1.5,shot['recoilMultiplier'])
        self.assertEqual(before['weapon']['selectionVersion'],shot['selectionVersion'])
        self.assertLessEqual(shot['acceptedTick'],facts[0]['tick'])
        self.call('snapshot',ackEvents=after['events'][-1]['seq'])
        self.call('action',name='fire',args={});self.assertFalse(any(e['kind']=='fire' for e in self.pump()['events']))
        self.call('action',name='holdItem',args=dict(page=0,x=0,y=0));before=self.pump()
        quality=before['inventory']['pages'][0]['items'][0]['quality']
        self.call('action',name='fire',args={});after=self.pump()
        shots=[e for e in after['events'] if e['kind']=='fire'];self.assertEqual(1,len(shots))
        self.assertEqual(quality,shots[0]['data']['quality'],'rejected trigger did not leave a stale shot fact')

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
        # The source menu labels these by their first output, not the internal
        # generator name ("Split ..." / "Salvage ..."). Retain actual recipes.
        self.assertTrue(any(r['name'] == 'Firewood x2' and
                            any(i['id'] == 16 and not i['consume'] for i in r['inputs'])
                            for r in recipes))
        self.assertTrue(any(r['name'] == 'Asbestos' and
                            any(i['id'] == 233 for i in r['inputs']) and
                            [o['id'] for o in r['outputs']] == [9340, 66, 67]
                            for r in recipes))
        steady = self.call('tick')['state']
        self.assertNotIn('recipes', steady)

    def test_recipe_titles_resolve_original_outputs_including_unnamed_rows(self):
        recipes = self.call('snapshot')['state']['recipes']
        self.assertEqual(['Birch Plank x2', 'Birch Stick x2', 'Maple Plank x2',
                          'Maple Stick x2', 'Pine Plank x2', 'Pine Stick x2'],
                         [r['name'] for r in recipes[:6]])
        self.assertTrue(all(r['name'] for r in recipes))
        self.assertEqual(list(range(len(recipes))), [r['index'] for r in recipes])

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

    def test_ammo_is_owned_persistent_and_reload_conserves_magazines(self):
        self.call('action', name='fire', args={})
        self.assertEqual(29, self.pump()['player']['ammo'])
        self.call('action', name='reload', args={})
        self.assertFalse(self.pump()['weapon']['reloading'])
        self.assertEqual(29, self.pump(100)['player']['ammo'], 'no magazine means no free ammo')
        self.call('worldSpawn', items=[dict(itemId=6, amount=12, position=[0, 0, -1])])
        item = self.pump()['worldItems'][0]
        self.call('action', name='pickupItem', args=dict(netId=item['netId']))
        self.pump()
        self.call('action', name='reload', args={})
        self.assertTrue(self.pump()['weapon']['reloading'])
        state = self.pump(100)
        self.assertEqual(13, state['player']['ammo'], '12 from magazine plus retained chamber')
        mags = [j for p in state['inventory']['pages'] for j in p['items'] if j['id'] == 6]
        self.assertEqual([28], [j['amount'] for j in mags])
        gun = next(j for p in state['inventory']['pages'] for j in p['items'] if j['id'] == 4)
        self.assertEqual(13, gun['gunAmmo'])
        self.call('tick', input=dict(heldItemId=0))
        self.pump()
        self.assertEqual(0, self.call('snapshot')['state']['player']['ammo'])
        before = len([e for e in self.call('snapshot')['state']['events'] if e['kind'] == 'fire'])
        self.call('action', name='fire', args={})
        self.assertEqual(before, len([e for e in self.pump()['events'] if e['kind'] == 'fire']))
        self.call('tick', input=dict(heldItemId=4))
        self.assertEqual(13, self.pump()['player']['ammo'])
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'ammo.json')
            self.call('save', path=path)
            self.call('action', name='fire', args={})
            self.assertEqual(12, self.pump()['player']['ammo'])
            self.call('load', path=path)
            self.assertEqual(13, self.pump()['player']['ammo'])

    def test_resource_geometry_uses_separate_index_and_dead_filter(self):
        self.call('worldServices', resourceCount=1,
                  resources=[dict(index=0, rewardItemId=13, position=[0, 0, -1], resetSeconds=1)])
        self.call('worldGeometry', meshes=[dict(resourceIndex=0,
                  vertices=[[-2, 0, -2], [2, 0, -2], [0, 4, -2]])])
        ray = {'from': [0, 1.6, 0], 'to': [0, 1.6, -3]}
        self.assertTrue(self.call('worldRay', **ray)['result']['hit'])
        self.call('action', name='forageResource', args=dict(index=0))
        self.pump()
        self.assertFalse(self.call('worldRay', **ray)['result']['hit'])

    def test_firemode_validates_modes_roundtrips_and_safety_blocks_shots(self):
        self.assertEqual(1, self.call('snapshot')['state']['weapon']['firemode'])
        self.assertFalse(self.call('action', name='setFiremode', args=dict(mode=2))['result']['queued'])
        self.assertTrue(self.call('action', name='setFiremode', args=dict(mode=0))['result']['queued'])
        self.assertEqual(0, self.pump()['weapon']['firemode'])
        self.call('action', name='fire', args={})
        self.assertEqual(30, self.pump()['weapon']['ammo'])
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'selector.json')
            self.call('save', path=path)
            self.call('action', name='setFiremode', args=dict(mode=3))
            self.assertEqual(3, self.pump()['weapon']['firemode'])
            self.call('load', path=path)
            self.assertEqual(0, self.pump()['weapon']['firemode'])

    def test_tree_delays_original_rewards_preserves_timer_and_regrows(self):
        self.call('worldServices', resourceCount=1, resources=[dict(index=0, kind='tree', name='Birch_0',
                  position=[0, 0, -1], health=1, resetSeconds=14, trunkLength=8)])
        self.call('worldGeometry', meshes=[dict(resourceIndex=0,
                  vertices=[[-2, 0, -1], [2, 0, -1], [0, 4, -1]])])
        self.call('action', name='fire', args={})
        state = self.pump()
        self.assertEqual([0], state['resources']['dead'])
        self.assertEqual([], state['worldItems'], 'logs wait for original 11-second debris cleanup')
        self.assertTrue(any(e['kind'] == 'resourceFelled' for e in state['events']))
        self.pump(250)
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'tree.json')
            self.call('save', path=path)
            self.call('load', path=path)
            state = self.pump(310)
            self.assertEqual(7, len(state['worldItems']), 'source index-zero Birch reward count')
            self.assertTrue(all(i['itemId'] in [37, 38] for i in state['worldItems']))
            self.assertEqual([0], state['resources']['dead'])
            state = self.pump(160)
            self.assertEqual([], state['resources']['dead'])
            self.assertEqual(7, len(state['worldItems']), 'one reward event survives reload, never duplicates')

    def test_ore_ignores_guns_and_fists_uses_pickaxe_resource_damage(self):
        self.call('worldServices', resourceCount=1, resources=[dict(index=0, kind='ore', name='Metal_0',
                  position=[0, 0, -1], health=100, resetSeconds=2)])
        self.call('worldGeometry', meshes=[dict(resourceIndex=0,
                  vertices=[[-2, 0, -1], [2, 0, -1], [0, 4, -1]])])
        self.call('action', name='fire', args={})
        self.assertEqual([], self.pump()['resources']['dead'])
        self.call('tick', input=dict(heldItemId=0))
        self.pump()
        self.call('action', name='melee', args=dict(strong=False, yawDegrees=0))
        self.assertEqual([], self.pump(30)['resources']['dead'])
        self.call('worldSpawn', items=[dict(itemId=1198, position=[0, 0, -.5])])
        item = self.pump()['worldItems'][0]
        self.call('action', name='pickupItem', args=dict(netId=item['netId']))
        self.pump()
        self.call('action', name='holdItem', args=dict(page=1, x=0, y=0))
        self.pump()
        self.call('action', name='melee', args=dict(strong=True, yawDegrees=0))
        state = self.pump(30)
        self.assertEqual([0], state['resources']['dead'])
        self.assertEqual([67, 67], [i['itemId'] for i in state['worldItems']])
        last = [e for e in state['events'] if e['kind'] == 'resourceHit'][-1]
        self.assertEqual(100, last['data']['damage'], 'ore uses unmultiplied Resource_Damage')
        self.assertEqual([], self.pump(100)['resources']['dead'])

    def test_hand_catalog_and_equipping_exact_duplicate_backing_items(self):
        catalog = self.call('catalog')['result']
        items = {i['id']: i for i in catalog['items']}
        self.assertEqual('gun', items[4]['handKind'])
        self.assertFalse(items[4]['canEquipFromBag'])
        self.assertEqual('fluid', items[14]['handKind'])
        self.assertEqual('tool', items[65]['handKind'])
        self.assertEqual('umbrella', items[1103]['handKind'])
        self.assertEqual('none', items[1346]['handKind'], 'unsupported flashbang must not turn into frag')
        self.assertGreater(items[13]['consumeAnimation']['seconds'], 0)
        self.assertAlmostEqual(1.63, catalog['throwMotion']['useSeconds'], delta=.02)
        self.call('action', name='fire', args={})
        first_selection = self.pump()['weapon']
        self.assertEqual(29, first_selection['ammo'])
        self.call('worldSpawn', items=[dict(itemId=253, position=[0, 0, -.5])])
        pack = self.pump()['worldItems'][0]['netId']
        self.call('action', name='pickupItem', args=dict(netId=pack))
        self.pump()
        self.call('worldSpawn', items=[dict(itemId=4, position=[0, 0, -.5])])
        net_id = self.pump()['worldItems'][0]['netId']
        self.call('action', name='pickupItem', args=dict(netId=net_id))
        state = self.pump()
        page, twin = next((p['index'], i) for p in state['inventory']['pages'] if p['index'] >= 2 for i in p['items'] if i['id'] == 4)
        address = dict(page=page, x=twin['x'], y=twin['y'])
        self.assertFalse(self.call('action', name='holdItem', args=address)['result']['queued'])
        self.assertTrue(self.call('action', name='equipAndHold', args=address)['result']['queued'])
        state = self.pump()
        self.assertEqual(30, state['weapon']['ammo'], 'selected the requested fresh twin')
        self.assertGreater(state['weapon']['selectionVersion'], first_selection['selectionVersion'])
        self.assertEqual(0, state['weapon']['held']['page'])
        self.call('action', name='fire', args={})
        self.pump(8)
        self.call('action', name='fire', args={})
        self.assertEqual(28, self.pump()['weapon']['ammo'])
        self.call('action', name='dropItem', args=dict(page=0, x=0, y=0))
        state = self.pump()
        self.assertEqual(0, state['player']['heldItemId'])
        self.assertNotIn('held', state['weapon'])
        self.assertEqual(29, next(i['gunAmmo'] for p in state['inventory']['pages'] for i in p['items'] if i['id'] == 4))
        beans = next((p['index'], i) for p in state['inventory']['pages'] for i in p['items'] if i['id'] == 13)
        self.assertTrue(self.call('action', name='equipAndHold', args=dict(page=beans[0], x=beans[1]['x'], y=beans[1]['y']))['result']['queued'])
        self.assertEqual('consumable', self.pump()['weapon']['held']['kind'])

    def test_legacy_unload_cannot_bypass_weapon_authority_or_restore_rounds(self):
        self.call('action', name='gunUnload', args=dict(page=0, x=0, y=0, roundId=5004, count=10))
        self.assertEqual(30, self.pump()['weapon']['ammo'])
        self.call('action', name='fire', args={})
        state = self.pump()
        self.assertEqual(29, state['weapon']['ammo'])
        self.assertEqual(0, sum(i['amount'] for p in state['inventory']['pages'] for i in p['items'] if i['id'] == 5004))

    def test_interactables_authority_reach_occupancy_claim_and_save(self):
        self.call('worldServices', objectDoors=[dict(netId=1, position=[0, 0, -1], open=False, swingSide=[0, 0, 1]),
                  dict(netId=2, position=[20, 0, 0])], seats=[dict(netId=1, position=[0, .5, -1])],
                  beds=[dict(netId=1, position=[0, 0, -2], yaw=90)], doors=[dict(netId=1, position=[0, 0, -1], owner=0)])
        self.pump()
        self.call('action', name='toggleObjectDoor', args=dict(netId=2))
        self.assertFalse(self.pump()['interactables']['objectDoors'][1]['open'])
        self.call('action', name='toggleObjectDoor', args=dict(netId=1))
        self.call('action', name='toggleDoor', args=dict(netId=1))
        self.call('action', name='sitSeat', args=dict(netId=1))
        self.call('action', name='claimBed', args=dict(netId=1))
        state = self.pump()
        self.assertTrue(state['interactables']['objectDoors'][0]['open'])
        self.assertTrue(state['interactables']['objectDoors'][0]['reverse'])
        self.assertTrue(state['interactables']['doors'][0]['open'])
        self.assertNotEqual(0, state['interactables']['seats'][0]['occupant'])
        self.assertNotEqual(0, state['interactables']['beds'][0]['owner'])
        self.call('action', name='sitSeat', args=dict(netId=0))
        self.assertEqual(0, self.pump()['interactables']['seats'][0]['occupant'])
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'doors.json')
            self.call('save', path=path)
            self.call('action', name='toggleObjectDoor', args=dict(netId=1))
            self.pump()
            self.call('load', path=path)
            door = self.pump()['interactables']['objectDoors'][0]
            self.assertTrue(door['open'])
            self.assertTrue(door['reverse'])

    def test_vehicle_hit_fuse_explosion_and_cooling_survive_save(self):
        spec = dict(typeId=1, key='testcar', position=[0, 0, -2], health=20, fuel=100000, battery=100)
        ident = self.call('worldVehicles', vehicles=[spec])['result']['vehicles'][0]['netId']
        self.call('worldDynamicGeometry', key=f'vehicle:{ident}', meshes=[dict(vehicleNetId=ident,
                  vertices=[[-2, 0, -2], [2, 0, -2], [0, 4, -2]])])
        self.call('action', name='fire', args={})
        state = self.pump()
        self.assertEqual(0, state['vehicles'][0]['health'])
        self.assertTrue(state['vehicles'][0]['onFire'])
        self.assertFalse(state['vehicles'][0]['flags'] & 32)
        self.assertTrue(any(e['kind'] == 'vehicleDamage' for e in state['events']))
        self.pump(100)
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'fuse.json')
            self.call('save', path=path)
            self.call('load', path=path)
            self.assertFalse(self.pump(30)['vehicles'][0]['flags'] & 32)
            state = self.pump(80)
            self.assertTrue(state['vehicles'][0]['flags'] & 32)
            self.assertTrue(any(e['kind'] == 'vehicleExploded' for e in state['events']))
            self.assertFalse(state['vehicles'][0]['wreckSalvageable'])
            self.pump(200)
            self.call('save', path=path)
            self.call('load', path=path)
            self.assertGreater(self.pump()['vehicles'][0]['burnSeconds'], 4)
            state = self.pump(2800)
            self.assertTrue(state['vehicles'][0]['wreckSalvageable'])
            # Native loader rebinds meshes because original save restoration can
            # mint different entity IDs. Old dynamic geometry is discarded.
            current_id = state['vehicles'][0]['netId']
            self.call('worldDynamicGeometry', key=f'vehicle:{current_id}', meshes=[dict(vehicleNetId=current_id,
                      vertices=[[-2,0,-2],[2,0,-2],[0,4,-2]])])
            self.call('action', name='respawn', args={})
            self.pump()
            self.call('action', name='console', args=dict(text='give 76'))
            state = self.pump()
            page, item = next((p['index'],i) for p in state['inventory']['pages'] for i in p['items'] if i['id']==76)
            self.call('action',name='equipAndHold',args=dict(page=page,x=item['x'],y=item['y']))
            self.pump()
            for _ in range(20):
                self.call('action',name='toolUse',args=dict(active=True))
                state = self.pump(4)
            self.assertEqual(1,len(state['vehicles']), 'half a source hold is not enough')
            self.call('action',name='toolUse',args=dict(active=False))
            self.pump(6)
            for _ in range(40):
                self.call('action',name='toolUse',args=dict(active=True))
                state = self.pump(4)
            self.assertEqual([],state['vehicles'])
            self.assertEqual(3,sum(i['amount'] for i in state['worldItems'] if i['itemId']==67))

    def test_vehicle_engine_region_does_not_subtract_hull_and_world_wall_blocks(self):
        spec = dict(typeId=1, key='engine', position=[0, 0, -2], health=100, fuel=10, battery=10,
                    engineRegion=dict(rear=True, threshold=-1, health=100))
        ident = self.call('worldVehicles', vehicles=[spec])['result']['vehicles'][0]['netId']
        mesh = dict(vehicleNetId=ident, vertices=[[-2, 0, -2], [2, 0, -2], [0, 4, -2]])
        self.call('worldDynamicGeometry', key='car', meshes=[mesh])
        self.call('worldGeometry', meshes=[dict(vertices=[[-2, 0, -1], [2, 0, -1], [0, 4, -1]])], replace=False)
        self.call('action', name='fire', args={})
        state = self.pump()
        self.assertFalse(any(e['kind'] == 'vehicleDamage' for e in state['events']))
        self.call('worldGeometry', meshes=[])
        self.call('worldDynamicGeometry', key='car', meshes=[mesh])
        self.call('action', name='fire', args={})
        state = self.pump()
        damage = next(e['data'] for e in state['events'] if e['kind'] == 'vehicleDamage')
        self.assertEqual('engine', damage['bodyPart'])
        self.assertEqual(100, state['vehicles'][0]['health'])
        self.assertEqual(65, state['vehicles'][0]['engineHealth'])
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'engine.json')
            self.call('save', path=path)
            self.call('load', path=path)
            self.assertEqual(65, self.pump()['vehicles'][0]['engineHealth'])

    def test_blowtorch_repairs_at_source_rate_only_while_held_and_aimed(self):
        ident = self.call('worldVehicles', vehicles=[dict(typeId=1, key='repair', position=[0,0,-2], health=100, healthMax=300, fuel=10, battery=10)])['result']['vehicles'][0]['netId']
        self.call('worldDynamicGeometry', key='repair', meshes=[dict(vehicleNetId=ident,
                  vertices=[[-2,0,-2],[2,0,-2],[0,4,-2]])])
        self.assertFalse(self.call('action', name='toolUse', args=dict(active=True))['result']['queued'])
        self.assertEqual(100, self.pump()['vehicles'][0]['health'])
        self.call('action', name='console', args=dict(text='give 76'))
        state = self.pump()
        page, item = next((p['index'],i) for p in state['inventory']['pages'] for i in p['items'] if i['id']==76)
        self.call('action',name='equipAndHold',args=dict(page=page,x=item['x'],y=item['y']))
        self.pump()
        for _ in range(50):
            self.call('action',name='toolUse',args=dict(active=True))
            state = self.pump(1)
        self.assertGreaterEqual(state['vehicles'][0]['health'],128, '25Hz replica can trail the final repair tick')
        self.call('action',name='toolUse',args=dict(active=False))
        repaired = self.pump()
        self.assertAlmostEqual(130, repaired['vehicles'][0]['health'], delta=.51, msg='original vehicle wire retains half-HP steps')
        self.assertAlmostEqual(30, sum(e['data']['amount'] for e in repaired['events'] if e['kind'] == 'vehicleRepaired'), delta=.001)
        # Turning away still runs the source torch visual, but cannot heal a car behind.
        for _ in range(10):
            self.call('action',name='toolUse',args=dict(active=True))
            self.call('tick',input=dict(yaw=180))
        self.assertLessEqual(self.pump()['vehicles'][0]['health'],131)

    def test_deployable_damage_breaks_nonburning_and_generator_obeys_fuse(self):
        self.call('action', name='console', args=dict(text='give 458'))
        self.pump()
        self.call('action', name='placeDeployable', args=dict(defId=458, pos=[0, 0, -2], yawDegrees=0))
        state = self.pump()
        ident = state['deployables'][0]['netId']
        self.call('worldDynamicGeometry', key=f'deployable:{ident}', meshes=[dict(deployableNetId=ident,
                  vertices=[[-2, 0, -2], [2, 0, -2], [0, 4, -2]])])
        for _ in range(13):
            self.call('action', name='fire', args={})
            state = self.pump()
        self.assertEqual(0, state['deployables'][0]['health'])
        self.assertTrue(state['deployables'][0]['onFire'])
        self.call('action', name='salvageDeployable', args=dict(netId=ident))
        self.assertEqual(1, len(self.pump()['deployables']), 'hot wreck cannot bypass source cooldown')
        state = self.pump(200)
        self.assertTrue(any(e['kind'] == 'deployableExploded' for e in state['events']))
        self.assertGreaterEqual(state['deployables'][0]['burnSeconds'], 0)
        # Non-generators shatter immediately, without inventing a fire/fuse.
        self.call('init', contentRoot=str(ROOT / 'game/content'), starterKit=True, spawn=[0, 0, 0])
        self.call('action', name='console', args=dict(text='give 459'))
        self.pump()
        self.call('action', name='placeDeployable', args=dict(defId=459, pos=[0, 0, -2], yawDegrees=0))
        ident = self.pump()['deployables'][0]['netId']
        self.call('worldDynamicGeometry', key='lamp', meshes=[dict(deployableNetId=ident,
                  vertices=[[-2, 0, -2], [2, 0, -2], [0, 4, -2]])])
        for _ in range(9):
            self.call('action', name='fire', args={})
            state = self.pump()
        self.assertEqual([], state['deployables'])
        self.assertTrue(any(e['kind'] == 'deployableBroken' for e in state['events']))
        self.assertFalse(self.call('worldRay', **{'from':[0,1.6,0],'to':[0,1.6,-5]})['result']['hit'])

    def test_consumable_and_throw_facts_require_real_spent_item(self):
        state = self.call('snapshot')['state']
        page, item = next((p['index'], i) for p in state['inventory']['pages'] for i in p['items'] if i['id'] == 13)
        self.assertEqual((0, 0, 0), (item['frozen'], item['cooked'], item['cookStyle']))
        args = dict(page=page, x=item['x'], y=item['y'])
        self.call('action', name='consume', args=args)
        self.call('action', name='consume', args=args)
        state = self.pump()
        facts = [e for e in state['events'] if e['kind'] == 'consumeAccepted']
        self.assertEqual(1, len(facts))
        self.assertEqual(13, facts[0]['data']['itemId'])
        self.call('action', name='grenade', args=dict(origin=[0, 1.6, 0], velocity=[0, 1, -2], itemId=254))
        self.assertEqual([], self.pump()['projectiles'], 'absent grenade cannot be thrown for free')
        self.call('action', name='console', args=dict(text='give 254'))
        self.pump()
        self.call('action', name='grenade', args=dict(origin=[0, 1.6, 0], velocity=[0, 1, -2], itemId=254))
        state = self.pump()
        self.assertEqual(1, len(state['projectiles']))
        self.assertEqual(0, state['projectiles'][0]['kind'])
        self.assertEqual(1, len([e for e in state['events'] if e['kind'] == 'throwAccepted']))
        self.assertFalse(any(i['id'] == 254 for p in state['inventory']['pages'] for i in p['items']))

    def test_dynamic_geometry_replaces_only_the_named_body(self):
        wall = dict(vertices=[[-2, 0, -4], [2, 0, -4], [0, 4, -4]])
        leaf = dict(vertices=[[-2, 0, -1], [2, 0, -1], [0, 4, -1]])
        self.call('worldGeometry', meshes=[wall])
        self.call('worldDynamicGeometry', key='door:1', meshes=[leaf])
        ray = {'from': [0, 1.6, 0], 'to': [0, 1.6, -5]}
        self.assertAlmostEqual(-1, self.call('worldRay', **ray)['result']['point'][2])
        self.call('worldDynamicGeometry', key='door:1', meshes=[])
        self.assertAlmostEqual(-4, self.call('worldRay', **ray)['result']['point'][2])

    def test_chainsaw_accepts_repeated_weak_cuts_but_never_strong(self):
        self.call('worldServices', resourceCount=1, resources=[dict(index=0, kind='tree', name='Birch_0',
                  position=[0, 0, -1], health=10000)])
        self.call('worldGeometry', meshes=[dict(resourceIndex=0,
                  vertices=[[-2, 0, -1], [2, 0, -1], [0, 4, -1]])])
        self.call('worldSpawn', items=[dict(itemId=490, position=[0, 0, -.5])])
        net_id = self.pump()['worldItems'][0]['netId']
        self.call('action', name='pickupItem', args=dict(netId=net_id))
        self.pump()
        self.assertTrue(self.call('action', name='holdItem', args=dict(page=1, x=0, y=0))['result']['queued'])
        self.pump()
        self.call('action', name='melee', args=dict(strong=True, yawDegrees=0))
        self.assertFalse(any(e['kind'] == 'resourceHit' for e in self.pump()['events']))
        for _ in range(30):
            self.call('action', name='melee', args=dict(strong=False, yawDegrees=0), includeState=False)
            state = self.pump(1)
        hits = [e for e in state['events'] if e['kind'] == 'resourceHit']
        self.assertEqual(3, len(hits), '50Hz source 0.225s cadence accepts at ticks 0,12,24')
        self.assertTrue(all(h['data']['damage'] > 0 for h in hits))

    def test_vehicle_authority_seats_recovery_and_blocked_exit(self):
        spec = dict(typeId=0, key='test:0', variant=2, position=[0, 0, -2], yaw=0,
                    health=4500, fuel=50000, battery=10000, speedMax=35, seatCount=2, hasDoors=True)
        net_id = self.call('worldVehicles', vehicles=[spec])['result']['vehicles'][0]['netId']
        same = self.call('worldVehicles', replace=False, vehicles=[spec])['result']['vehicles'][0]['netId']
        self.assertEqual(net_id, same)
        self.call('action', name='enterVehicle', args=dict(netId=net_id, seat=0))
        state = self.pump()
        self.assertEqual(net_id, state['player']['vehicleNetId'])
        self.assertEqual(0, state['player']['vehicleSeat'])
        self.assertEqual(50000, state['vehicles'][0]['fuel'])
        self.call('action', name='vehicleState', args=dict(vehicleNetId=net_id,
                  pos=[1000, 0, -2], eulerDegrees=[0, 0, 0], linVel=[0, 0, 0], angVel=[0, 0, 0],
                  steerDegrees=0, throttle=1, steer=0, handbrake=False, flags=1, recovAck=0))
        state = self.pump()
        self.assertLess(abs(state['vehicles'][0]['position'][0]), .1)
        self.assertGreater(state['vehicles'][0]['recovCounter'], 0)
        self.assertTrue(any(e['kind'] == 'vehicleRecov' for e in state['events']))
        self.call('worldVehicleState', vehicles=[dict(netId=net_id, position=[500, 0, 0], fuel=48000)])
        self.assertLess(abs(self.pump()['vehicles'][0]['position'][0]), .1)
        self.call('action', name='exitVehicle', args={})
        self.assertEqual(net_id, self.pump()['player']['vehicleNetId'])
        self.call('worldVehicleExit', netId=net_id, seat=0, clear=True, position=[2, 0, -2])
        self.call('action', name='exitVehicle', args={})
        state = self.pump()
        self.assertEqual(0, state['player']['vehicleNetId'])
        self.assertAlmostEqual(2, state['player']['position'][0], places=2)
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / 'vehicle.json')
            self.call('save', path=path)
            self.call('load', path=path)
            self.assertEqual(1, len(self.pump()['vehicles']))
            self.assertEqual(48000, self.call('snapshot')['state']['vehicles'][0]['fuel'])

    def test_vehicle_telemetry_preserves_authoritative_explosion_and_vitals(self):
        vehicle = dict(typeId=0, key='wreck', position=[0, 0, -2], health=0, fuel=12345, battery=50,
                       flags=32, speedMax=35, seatCount=1)
        net_id = self.call('worldVehicles', vehicles=[vehicle])['result']['vehicles'][0]['netId']
        self.call('worldVehicleState', vehicles=[dict(netId=net_id, position=[0, 0, -2], flags=0)])
        state = self.pump()['vehicles'][0]
        self.assertEqual(32, state['flags'] & 32)
        self.assertEqual(0, state['health'])
        self.assertEqual(12345, state['fuel'])

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

    def test_long_prop_door_reach_uses_leaf_centre_not_distant_prop_root(self):
        self.call('init',contentRoot=str(ROOT/'game/content'),spawn=[-476.445,32.5,-111.1])
        manifest = self.call('mapServices',mapName='PEI',mapRoot='')['result']
        door = next(d for d in manifest['objectDoors'] if d['name']=='Container_0' and abs(d['position'][0]+476.445)<.1)
        self.assertGreater(abs(door['position'][2]+111.1),6)
        self.call('action',name='toggleObjectDoor',args=dict(netId=door['netId']))
        state = self.pump()
        replica = next(d for d in state['interactables']['objectDoors'] if d['netId']==door['netId'])
        self.assertNotEqual(door['open'],replica['open'])

    def test_real_map_services_register_all_and_do_not_duplicate_after_load(self):
        request = dict(mapName='PEI', mapRoot='')
        result = self.call('mapServices', **request)['result']
        self.assertGreater(len(result['containerDoors']), 300)
        self.assertTrue(all('lidRisesWhenLit' in d and d['netId'] for d in result['containerDoors']))
        self.assertEqual(696, result['containerCount'])
        self.assertEqual(11, result['fixtureCount'])
        self.assertEqual(1, result['deadzoneCount'])
        self.assertEqual(len(result['resources']), result['resourceCount'])
        self.assertGreater(result['destructibleCount'], 1000)
        self.assertGreater(len(result['objectDoors']), 0)
        self.assertGreater(len(result['seats']), 0)
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
