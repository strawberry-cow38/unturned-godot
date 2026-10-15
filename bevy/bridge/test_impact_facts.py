"""One reliable impact carries the exact accepted contact, without a second FX event."""
import tempfile
import unittest
import test_attachment_actions
import test_bridge
import test_wildlife


class ImpactFactsTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump
    configure=test_attachment_actions.AttachmentActionTests.configure
    ground=test_wildlife.WildlifeTests.ground
    fauna=test_wildlife.WildlifeTests.fauna

    def test_water_then_world_contacts_keep_distinct_metadata_in_one_event_each(self):
        self.call('worldCosmeticGeometry',waterBoxes=[dict(center=[0,0,0],size=[100,.2,100])])
        self.call('worldGeometry',meshes=[dict(vertices=[[-100,-100,-10],[100,-100,-10],[0,100,-10]])])
        sequence=self.call('action',name='fire',args=dict(origin=[0,1.5,0],dir=[0,-.4,-.916515]))['result']['value']
        state=self.pump(20)
        impacts=[e for e in state['events'] if e['kind']=='impact']
        self.assertEqual(2,len(impacts),'piercing must enrich, not duplicate, the original wire impacts')
        water,wall=[e['data'] for e in impacts]
        self.assertTrue(water['water']);self.assertFalse(wall['water'])
        self.assertEqual([0,1,0],water['normal']);self.assertEqual([0,0,1],wall['normal'])
        self.assertLess(water['acceptedTick'],wall['acceptedTick'])
        for event in impacts:
            fact=event['data']
            self.assertEqual(1,fact['surface']);self.assertFalse(fact['thin'])
            self.assertEqual(sequence,fact['sequence']);self.assertEqual(0,fact['pellet'])
            self.assertEqual(state['playerId'],fact['playerId'])
            self.assertLessEqual(fact['acceptedTick'],event['tick'])

    def test_same_position_shotgun_pellets_match_ordered_contacts_without_spatial_dedup(self):
        self.configure(gun=dict(Id=112,GunAmmo=6,GunMagId=113))
        self.call('worldGeometry',meshes=[dict(thin=True,vertices=[[-100,-100,-5],[100,-100,-5],[0,100,-5]])])
        sequence=self.call('action',name='fire',args=dict(origin=[0,1.5,0],dir=[0,0,-1]))['result']['value']
        state=self.pump(15)
        launch=next(e['data'] for e in state['events'] if e['kind']=='bulletLaunched')
        contacts=[e['data'] for e in state['events'] if e['kind']=='impact']
        self.assertGreater(launch['pelletCount'],1)
        self.assertEqual(list(range(launch['pelletCount'])),sorted(c['pellet'] for c in contacts))
        self.assertEqual(1,len({tuple(c['pos'].values()) for c in contacts}),'actual authority pellets share the accepted command ray')
        for fact in contacts:
            self.assertEqual(sequence,fact['sequence']);self.assertTrue(fact['thin']);self.assertFalse(fact['water'])
            self.assertEqual([0,0,1],fact['normal'])

    def test_animal_flesh_keeps_original_surface_and_actual_capsule_normal(self):
        self.ground()
        with tempfile.TemporaryDirectory() as directory:
            self.fauna(directory,[(0,0,-3)]);self.call('worldWildlife',mapRoot=directory);self.pump()
            self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[0,0,-1]));state=self.pump(12)
        hit=next(e['data'] for e in state['events'] if e['kind']=='animalHit')
        impacts=[e['data'] for e in state['events'] if e['kind']=='impact']
        self.assertEqual(1,len(impacts));fact=impacts[0]
        self.assertEqual(0,fact['surface']);self.assertFalse(fact['water']);self.assertFalse(fact['thin'])
        self.assertAlmostEqual(1,sum(n*n for n in fact['normal']),places=5)
        self.assertTrue(hit['impactEmitted'],'native animal feedback must not make a second blood burst')

    def test_origin_barrier_drains_contact_facts_then_shifts_only_positions(self):
        self.call('init',starterKit=True,spawn=[1025,0,0])
        self.call('worldGeometry',meshes=[dict(vertices=[[1000,-10,-5],[1050,-10,-5],[1025,10,-5]])])
        self.call('action',name='fire',args=dict(origin=[1025,1.5,0],dir=[0,0,-1]))
        for _ in range(8):
            before=self.call('tick')['state']
            if self.call('diagnostics')['result']['liveBullets']==0 and any(e['kind']=='bulletRetired' for e in before['events']):break
        state=self.call('rebase',shift=[1024,0,0],expectedEpoch=0)['state']
        impacts=[e for e in state['events'] if e['kind']=='impact']
        self.assertEqual(1,len(impacts));fact=impacts[0]['data']
        self.assertAlmostEqual(1,fact['pos']['x'],places=3);self.assertEqual([0,0,1],fact['normal'])
        self.assertEqual(before['tick'],state['tick'])
        self.assertLessEqual(fact['acceptedTick'],state['tick'])
        self.assertEqual(1,sum(e['kind']=='impact' for e in self.pump(8)['events']))
