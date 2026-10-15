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
