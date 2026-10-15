"""Seated action carrier preserves real weapon validation and seat position ownership."""
import unittest
import test_bridge

class SeatedCombatTests(unittest.TestCase):
    setUp=test_bridge.BridgeTests.setUp
    tearDown=test_bridge.BridgeTests.tearDown
    call=test_bridge.BridgeTests.call
    pump=test_bridge.BridgeTests.pump

    def vehicle(self):
        return self.call('worldVehicles',vehicles=[dict(typeId=1,key='armed-passenger',position=[0,0,0],
            health=300,fuel=10,seatOffsets=[[0,0,0],[0,0,0]])])['result']['vehicles'][0]['netId']

    def test_driver_rejects_passenger_fires_once_and_reloads_owned_magazine(self):
        self.call('worldSpawn',items=[dict(itemId=6,amount=12,position=[0,0,-1])])
        item=self.pump()['worldItems'][0]
        self.call('action',name='pickupItem',args=dict(netId=item['netId']));self.pump()
        vehicle=self.vehicle()
        self.call('action',name='enterVehicle',args=dict(netId=vehicle,seat=0));state=self.pump(12)
        self.assertEqual(0,state['player']['vehicleSeat'])
        self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[0,0,-1]))
        state=self.pump(12);self.assertEqual(30,state['player']['ammo'])
        self.assertFalse(any(e['kind']=='fire' for e in state['events']))
        self.call('action',name='exitVehicle',args={});self.pump(12)
        # Move to the legitimate exit point before requesting the adjacent seat.
        self.call('action',name='enterVehicle',args=dict(netId=vehicle,seat=1));state=self.pump(12)
        self.assertEqual(1,state['player']['vehicleSeat'])
        self.call('action',name='fire',args=dict(origin=[0,1,0],dir=[1,0,0]))
        state=self.pump(12);self.assertEqual(29,state['player']['ammo'])
        self.assertEqual(1,len([e for e in state['events'] if e['kind']=='fire']))
        self.call('action',name='reload',args={});state=self.pump(130)
        self.assertEqual(13,state['player']['ammo'],'12 owned rounds plus chamber, no free seated reload')
        self.assertEqual(1,state['player']['vehicleSeat'])
        mags=[i for p in state['inventory']['pages'] for i in p['items'] if i['id']==6]
        self.assertEqual([28],[i['amount'] for i in mags])

    def test_vehicle_melee_rejected_and_furniture_melee_completes(self):
        vehicle=self.vehicle()
        self.call('action',name='enterVehicle',args=dict(netId=vehicle,seat=1));self.pump(12)
        self.call('tick',input=dict(heldItemId=0));self.pump()
        self.call('action',name='melee',args=dict(strong=False,yawDegrees=0));state=self.pump(40)
        self.assertFalse(any(e['kind']=='melee' for e in state['events']))
        self.call('action',name='exitVehicle',args={});state=self.pump(12)
        pos=state['player']['position']
        self.call('worldServices',seats=[dict(netId=900,position=pos)])
        self.call('action',name='sitSeat',args=dict(netId=900));self.pump(12)
        self.call('action',name='melee',args=dict(strong=False,yawDegrees=0));state=self.pump(40)
        self.assertTrue(any(e['kind']=='melee' for e in state['events']))
        self.assertEqual(state['playerId'],state['interactables']['seats'][0]['occupant'])
