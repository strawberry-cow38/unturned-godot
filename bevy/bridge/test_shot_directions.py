"""Source scatter is sampled once and owns actual contacts, not just tracer pictures."""
import math
import unittest
import test_attachment_actions
import test_bridge


class ShotDirectionTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump
    configure=test_attachment_actions.AttachmentActionTests.configure

    def launch(self,origin=(0,1.5,0),visual=None):
        sequence=self.call('action',name='fire',args=dict(origin=origin,dir=[0,0,-1],visual=visual or {}))['result']['value']
        for _ in range(8):
            state=self.call('tick')['state']
            launch=next((e['data'] for e in state['events'] if e['kind']=='bulletLaunched' and e['data']['sequence']==sequence),None)
            if launch:return launch,state
        self.fail('accepted source launch did not arrive')

    def test_actual_shotgun_contacts_follow_each_visual_sample_and_only_spend_one_shell(self):
        self.configure(gun=dict(Id=112,GunAmmo=6,GunMagId=113))
        self.call('worldGeometry',meshes=[dict(vertices=[[-100,-100,-20],[100,-100,-20],[0,100,-20]])])
        launch,_=self.launch();state=self.pump(20)
        impacts=[e['data'] for e in state['events'] if e['kind']=='impact']
        self.assertEqual(launch['pelletCount'],len(impacts));self.assertGreater(len(impacts),1)
        for impact in impacts:
            velocity=next(b['velocity'] for b in launch['bullets'] if b['pellet']==impact['pellet'])
            # X has no gravity. At the actual Z plane each authoritative contact
            # must follow its accepted sample, rather than the old center ray.
            expected=velocity[0]*(-20/velocity[2])
            self.assertAlmostEqual(expected,impact['pos']['x'],delta=.018)
            self.assertEqual(launch['sequence'],impact['sequence'])
        self.assertGreater(max(abs(i['pos']['x']) for i in impacts),.02)
        self.assertEqual(5,state['player']['ammo'])

    def test_off_axis_sample_damages_a_real_target_that_the_center_ray_misses(self):
        self.configure(gun=dict(Id=112,GunAmmo=6,GunMagId=113))
        self.call('worldServices',destructibleCount=1,destructibles=[dict(index=0,health=1,resetSeconds=0)])
        launch,_=self.launch()
        sample=max(launch['bullets'],key=lambda b:abs(b['velocity'][0]/b['velocity'][2]))
        x=sample['velocity'][0]*(-28/sample['velocity'][2]);half=abs(x)*.1
        self.assertGreater(half,1e-6,'source RNG must produce a measurable nonzero pellet')
        self.call('worldGeometry',meshes=[dict(destructibleIndex=0,
            vertices=[[x-half,-4,-28],[x+half,-4,-28],[x+half,6,-28],[x-half,6,-28]],indices=[0,1,2,0,2,3])])
        state=self.pump(15)
        self.assertEqual([0],state['destructibles']['dead'],'only the sampled direction reaches this real target')
        impact=next(e['data'] for e in state['events'] if e['kind']=='impact')
        self.assertAlmostEqual(x,impact['pos']['x'],delta=half+.018)

    def test_rejected_requests_do_not_advance_pattern_and_original_idle_gap_resets_it(self):
        self.call('action',name='fire',args=dict(origin=[100,1.5,0],dir=[0,0,-1]))
        self.pump(8)
        first,_=self.launch(visual=dict(aimAlpha=1));self.assertEqual(1,first['patternShot'])
        self.pump(6)
        second,_=self.launch(visual=dict(aimAlpha=1,patternShot=0));self.assertEqual(2,second['patternShot'],'visual hints cannot reset authority scatter')
        self.pump(20)
        third,state=self.launch(visual=dict(aimAlpha=1));self.assertEqual(1,third['patternShot'])
        self.assertEqual(3,sum(e['kind']=='bulletLaunched' for e in state['events']))
        for launch in [first,second,third]:
            velocity=launch['bullets'][0]['velocity']
            self.assertAlmostEqual(launch['muzzleVelocity'],math.sqrt(sum(v*v for v in velocity)),places=3)
            self.assertEqual(1,launch['aimAlpha'])
