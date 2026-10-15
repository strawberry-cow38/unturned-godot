"""Original presentation diagnostics and authority weather asset metadata."""
import os
import unittest
from unittest.mock import patch
import test_bridge as host

class WeatherTests(unittest.TestCase):
    def test_source_seed_then_actual_weather_evolves_and_exposes_asset_lightning(self):
        with patch.dict(os.environ, UG_SNOWCOVER='0.65', UG_WEATHER='snow'):
            h=host.BridgeTests(); h.setUp()
        try:
            before=h.call('snapshot')['state']['weather']
            self.assertAlmostEqual(0.65,before['snowCover'],places=3)
            self.assertFalse(before['hasLightning'])
            after=h.pump(30)['weather']
            self.assertGreater(after['snowCover'],before['snowCover'])
            with patch.dict(os.environ, UG_SNOWCOVER='2', UG_WEATHER='tempest'):
                other=host.BridgeTests();other.setUp()
            try:
                weather=other.call('snapshot')['state']['weather']
                self.assertLessEqual(weather['snowCover'],1)
                self.assertGreater(weather['snowCover'],0.999) # real warm-weather melt already advances during handshake
                self.assertTrue(weather['hasLightning'])
                self.assertEqual((8,28),(weather['minLightningInterval'],weather['maxLightningInterval']))
            finally: other.tearDown()
        finally: h.tearDown()

    def test_original_console_weather_and_strike_are_authorized_and_retained_once(self):
        h=host.BridgeTests();h.setUp()
        try:
            h.call('action',name='console',args=dict(text='weather lightning'))
            state=h.call('snapshot')['state']
            strikes=[e for e in state['events'] if e['kind']=='weatherStrike']
            self.assertEqual(1,len(strikes))
            self.assertTrue(any(e['kind']=='console' and e['data']['text']=='weather -> lightning' for e in state['events']))
            self.assertEqual(strikes,[e for e in h.pump(3)['events'] if e['kind']=='weatherStrike'])
            h.call('action',name='console',args=dict(text='weather heavy'))
            self.assertEqual('Heavy Rain',h.call('snapshot')['state']['weather']['name'])
            h.call('action',name='console',args=dict(text='weather clear'))
            self.assertEqual('Clear',h.call('snapshot')['state']['weather']['name'])
            h.call('init',allowCheats=False)
            h.call('action',name='console',args=dict(text='weather lightning'))
            h.call('action',name='console',args=dict(text='weather heavy'))
            state=h.call('snapshot')['state']
            self.assertEqual('Clear',state['weather']['name'])
            self.assertFalse(any(e['kind']=='weatherStrike' for e in state['events']))
            self.assertTrue(any(e['kind']=='console' and 'disabled' in e['data']['text'] for e in state['events']))
        finally:h.tearDown()
