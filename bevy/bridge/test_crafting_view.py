"""Original browsing and accepted owner queue through the real bridge/UDP path."""
import socket
import time
import unittest

import test_bridge


class CraftingViewTests(unittest.TestCase):
    setUp = test_bridge.BridgeTests.setUp
    tearDown = test_bridge.BridgeTests.tearDown
    call = test_bridge.BridgeTests.call
    pump = test_bridge.BridgeTests.pump

    def test_remote_browsing_queue_cancel_and_refund_follow_accepted_owner_only(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        self.call('init', contentRoot=str(test_bridge.ROOT / 'game/content'), listenPort=port,
                  allowCheats=True, spawn=[0, 0, 0])
        peer = test_bridge.BridgeTests()
        peer.setUp()
        try:
            peer.call('init', contentRoot=str(test_bridge.ROOT / 'game/content'),
                      connectHost='127.0.0.1', connectPort=port)

            def pump(n=12):
                for _ in range(n):
                    self.call('tick')
                    state = peer.call('tick')['state']
                    time.sleep(.001)
                return state

            self.assertTrue(pump(40)['connected'])
            view = peer.call('craftingView')['result']['view']
            self.assertEqual(len(view['tiles']), next(c['count'] for c in view['categories'] if c['name'] == 'All'))
            tile = next(t for t in view['tiles'] if t['name'] == 'Birch Plank x2')
            index = tile['index']
            selected = peer.call('craftingView', selected=index)['result']['view']['selected']
            for ingredient in selected['ingredients']:
                amount = ingredient['amount'] * (2 if ingredient['consume'] else 1)
                for _ in range(amount):
                    peer.call('action', name='console', args=dict(text=f"give {ingredient['id']}"))
            pump()
            before = peer.call('snapshot')['state']

            def count(state, item):
                return sum(j['amount'] for page in state['inventory']['pages'] if page['index'] < 7
                           for j in page['items'] if j['id'] == item)

            selected = peer.call('craftingView', selected=index, quantity=99)['result']['view']['selected']
            self.assertEqual(selected['maxQuantity'], selected['quantity'])
            self.assertGreaterEqual(selected['quantity'], 2)
            self.assertTrue(selected['craftable'])
            peer.call('action', name='craft', args=dict(index=index))
            self.assertEqual([], peer.call('craftingView')['result']['queue'], 'request is not queue acceptance')
            spent = pump()
            queue = peer.call('craftingView')['result']
            self.assertEqual(1, queue['total'])
            self.assertEqual(index, queue['queue'][0]['index'])
            self.assertEqual(0, queue['queue'][0]['slot'])
            self.assertEqual(8, queue['queue'][0]['total'])
            self.assertEqual([], self.call('craftingView')['result']['queue'], 'another owner never receives this queue')
            for ingredient in selected['ingredients']:
                cost = ingredient['amount'] if ingredient['consume'] else 0
                self.assertEqual(count(before, ingredient['id']) - cost, count(spent, ingredient['id']))
            peer.call('action', name='craftCancel', args=dict(slot=0))
            self.assertEqual(1, peer.call('craftingView')['result']['total'])
            restored = pump()
            self.assertEqual([], peer.call('craftingView')['result']['queue'])
            for ingredient in selected['ingredients']:
                self.assertEqual(count(before, ingredient['id']), count(restored, ingredient['id']))
        finally:
            peer.tearDown()


if __name__ == '__main__':
    unittest.main()
