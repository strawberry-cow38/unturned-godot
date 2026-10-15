"""Authority reaches targets behind source pierceable surfaces with actual damage loss."""
import math
import unittest
import test_bridge
import test_attachment_actions

class PiercingTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump
    configure=test_attachment_actions.AttachmentActionTests.configure

    def slab(self,z,**tags):
        return dict(vertices=[[-100,-100,z],[100,-100,z],[0,100,z]],**tags)

    def fire(self,direction=(0,0,-1)):
        self.call('action',name='fire',args=dict(origin=[0,1.5,0],dir=direction))
        return self.pump(20)

    def vehicle(self):
        return self.call('worldVehicles',vehicles=[dict(typeId=1,key='pierce-target',position=[0,0,-10],health=1000,healthMax=1000,fuel=0,seatOffsets=[[0,0,0]])])['result']['vehicles'][0]['netId']

    def test_real_target_behind_thin_surface_takes_reduced_damage_instead_of_only_a_tracer(self):
        ident=self.vehicle()
        self.call('worldGeometry',meshes=[self.slab(-5,thin=True),self.slab(-10,vehicleNetId=ident)])
        state=self.fire();vehicle=next(v for v in state['vehicles'] if v['netId']==ident)
        self.assertAlmostEqual(1000-35*.7,vehicle['health'],delta=1.1)
        self.assertEqual(2,sum(e['kind']=='impact' for e in state['events']))
        pierced=next(e['data'] for e in state['events'] if e['kind']=='bulletPierced')
        self.assertTrue(pierced['thin']);self.assertEqual(.75,pierced['velocityKeep']);self.assertEqual(.7,pierced['damageKeep'])

    def test_water_slab_halves_damage_then_does_not_hit_its_backface_or_block_blast_shelter(self):
        ident=self.vehicle()
        self.call('worldGeometry',meshes=[self.slab(-10,vehicleNetId=ident)])
        self.call('worldCosmeticGeometry',waterBoxes=[dict(center=[0,0,0],size=[100,.2,100])])
        state=self.fire((0,-.4,-.916515));vehicle=next(v for v in state['vehicles'] if v['netId']==ident)
        self.assertAlmostEqual(1000-35*.5,vehicle['health'],delta=1.1)
        pierced=[e['data'] for e in state['events'] if e['kind']=='bulletPierced']
        self.assertEqual(1,len(pierced));self.assertTrue(pierced[0]['water']);self.assertEqual([0,1,0],pierced[0]['normal'])
        self.assertEqual(.45,pierced[0]['velocityKeep']);self.assertEqual(.5,pierced[0]['damageKeep'])
        # Water belongs only to bullet ray queries; movement/world shelter geometry stays clear.
        self.assertFalse(self.call('worldRay',**{'from':[50,2,0],'to':[50,-2,0]})['result']['hit'])

    def test_source_two_surface_cap_and_untagged_solid_stop_damage(self):
        ident=self.vehicle()
        self.call('worldGeometry',meshes=[self.slab(z,thin=True) for z in [-2,-4,-6]]+[self.slab(-10,vehicleNetId=ident)])
        state=self.fire();self.assertEqual(1000,next(v for v in state['vehicles'] if v['netId']==ident)['health'])
        self.assertEqual(2,sum(e['kind']=='bulletPierced' for e in state['events']))
        self.assertEqual(3,sum(e['kind']=='impact' for e in state['events']))
        self.call('worldGeometry',meshes=[self.slab(-2),self.slab(-10,vehicleNetId=ident)])
        before=max(e['seq'] for e in state['events']);state=self.fire()
        new=[e for e in state['events'] if e['seq']>before]
        self.assertEqual(1,sum(e['kind']=='impact' for e in new));self.assertFalse(any(e['kind']=='bulletPierced' for e in new))

    def test_bullet_water_query_does_not_enter_melee_collision_mask(self):
        self.call('worldServices',destructibleCount=1,destructibles=[dict(index=0,health=20,resetSeconds=0)])
        self.call('worldGeometry',meshes=[self.slab(-1.5,destructibleIndex=0)])
        self.call('worldCosmeticGeometry',waterBoxes=[dict(center=[0,1.2,-1],size=[1,.2,1])])
        self.call('tick',input=dict(heldItemId=0));self.pump()
        self.call('action',name='melee',args=dict(strong=False,yawDegrees=0));state=self.pump(35)
        self.assertEqual([0],state['destructibles']['dead'],'source melee mask excludes the splash slab')

    def test_water_boundary_tangent_waits_until_gravity_makes_an_actual_entry(self):
        # Actual crossbow ADS spread is below the original 0.0001rad sampling
        # threshold, so this remains an exactly tangent shot after scatter ports.
        self.configure(gun=dict(Id=346,GunAmmo=1,GunMagId=347))
        self.call('worldCosmeticGeometry',waterBoxes=[dict(center=[0,-.1,0],size=[100,.2,100])])
        self.call('action',name='fire',args=dict(origin=[0,0,0],dir=[0,0,-1],visual=dict(aimAlpha=1)));state=self.pump(12)
        fire=next(e['data'] for e in state['events'] if e['kind']=='fire')
        contact=next(e for e in state['events'] if e['kind']=='bulletPierced')
        self.assertGreater(contact['tick'],fire['acceptedTick'],'parallel boundary travel has no surface normal or t=0 contact')

    def test_bolt_crosses_thin_sheet_before_sticking_and_rocket_detonates_at_water(self):
        self.configure(gun=dict(Id=346,GunAmmo=1,GunMagId=347))
        self.call('worldGeometry',meshes=[self.slab(-2,thin=True),self.slab(-4)])
        state=self.fire();arrows=[w for w in state['worldItems'] if w['itemId']==347]
        self.assertEqual(1,len(arrows));self.assertAlmostEqual(-3.95,arrows[0]['position'][2],places=2)
        self.assertEqual(1,sum(e['kind']=='projectileStuck' for e in state['events']))
        self.call('init',starterKit=True,spawn=[0,0,0]);self.configure(gun=dict(Id=519,GunAmmo=1,GunMagId=520))
        self.call('worldCosmeticGeometry',waterBoxes=[dict(center=[0,0,0],size=[100,.2,100])])
        state=self.fire((0,-.4,-.916515));boom=next(e['data'] for e in state['events'] if e['kind']=='bulletExplosion')
        self.assertAlmostEqual(.1,boom['position'][1],places=4)
        self.assertFalse(any(e['kind']=='bulletPierced' for e in state['events']))
