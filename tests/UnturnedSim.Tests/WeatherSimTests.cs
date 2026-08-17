using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 for the weather scheduler (src LightingManager scheduled-weather machine + the ripped PEI table).
    // The whole point of this class being engine-free: a real forecast is 2.3-5.6 game-days away, so these
    // invariants are unobservable at play speed and only testable here.
    [TestFixture]
    public class WeatherSimTests
    {
        static WeatherSim Pei(int seed = 1, float cycle = WeatherSim.DefaultCycleSeconds)
            => new WeatherSim(WeatherSim.PeiTypes(), WeatherSim.PeiSchedule(), seed, cycle);

        // Run the sim forward in fixed steps, returning the seconds elapsed.
        static float Advance(WeatherSim w, float seconds, float dt = 1f)
        {
            float t = 0f;
            while (t < seconds) { w.Step(dt); t += dt; }
            return t;
        }

        [Test]
        public void StartsClear_AndSchedulesAForecast()
        {
            var w = Pei();
            Assert.That(w.Stage, Is.EqualTo(WeatherStage.None));
            w.Step(1f);
            Assert.That(w.Stage, Is.EqualTo(WeatherStage.Forecast), "the first step should pick the next weather");
            Assert.That(w.BlendAlpha, Is.EqualTo(0f), "a forecast is not visible yet");
            Assert.That(w.IsRaining, Is.False);
        }

        [Test]
        public void ForecastWindowMatchesPeiTable()
        {
            // PEI: 2.3-5.6 cycles at 3600 s/cycle => 8280 s .. 20160 s. Check across many seeds.
            for (int seed = 0; seed < 40; seed++)
            {
                var w = Pei(seed);
                w.Step(0.001f);
                Assert.That(w.ForecastTimer, Is.InRange(2.3f * 3600f - 1f, 5.6f * 3600f + 1f),
                            $"seed {seed}: forecast outside PEI's 2.3-5.6 cycle band");
            }
        }

        [Test]
        public void ActiveWindowMatchesPeiTable()
        {
            // PEI duration 0.05-0.15 cycles => 180 s .. 540 s.
            for (int seed = 0; seed < 40; seed++)
            {
                var w = Pei(seed);
                w.Step(0.001f);
                Assert.That(w.ActiveTimer, Is.InRange(0.05f * 3600f - 1f, 0.15f * 3600f + 1f),
                            $"seed {seed}: duration outside PEI's 0.05-0.15 cycle band");
            }
        }

        [Test]
        public void CycleLengthScalesTheSchedule()
        {
            // the src multiplies by `cycle`, so a 60 s day makes weather 60x more frequent
            var fast = new WeatherSim(WeatherSim.PeiTypes(), WeatherSim.PeiSchedule(), 7, cycleSeconds: 60f);
            fast.Step(0.001f);
            Assert.That(fast.ForecastTimer, Is.InRange(2.3f * 60f - 1f, 5.6f * 60f + 1f));
        }

        [Test]
        public void FrequencyMultiplierIsApplied()
        {
            var half = new WeatherSim(WeatherSim.PeiTypes(), WeatherSim.PeiSchedule(), 3,
                                      cycleSeconds: 3600f, frequencyMultiplier: 0.5f);
            half.Step(0.001f);
            Assert.That(half.ForecastTimer, Is.InRange(2.3f * 3600f * 0.5f - 1f, 5.6f * 3600f * 0.5f + 1f));
        }

        [Test]
        public void ForecastElapses_ThenWeatherGoesActiveAndVisible()
        {
            var w = Pei(seed: 5);
            w.Step(0.001f);
            float forecast = w.ForecastTimer;
            Advance(w, forecast + 1f, dt: 5f);
            Assert.That(w.Stage, Is.EqualTo(WeatherStage.Active));
            Advance(w, 25f, dt: 1f);   // past the 20 s fade-in
            Assert.That(w.IsRaining, Is.True);
            Assert.That(w.BlendAlpha, Is.GreaterThan(0.9f), "fully faded in after the 20 s Fade_In_Duration");
        }

        [Test]
        public void BlendRampsUpOverFadeInDuration()
        {
            var w = Pei(seed: 11);
            w.ForecastImmediately(0);                       // Default Rain, fade-in 20 s
            w.Step(0.001f);
            Assert.That(w.BlendAlpha, Is.LessThan(0.05f), "starts clear");
            Advance(w, 10f, dt: 1f);
            Assert.That(w.BlendAlpha, Is.InRange(0.35f, 0.65f), "roughly half-faded at 10 s of a 20 s fade");
            Advance(w, 12f, dt: 1f);
            Assert.That(w.BlendAlpha, Is.GreaterThan(0.95f));
        }

        [Test]
        public void BlendRampsDownIntoTheEnd_AndClearsAfter()
        {
            var w = Pei(seed: 13);
            w.ForecastImmediately(0);
            w.Step(0.001f);
            float total = w.ActiveTimer;
            Advance(w, total - 10f, dt: 1f);                // 10 s left of a 20 s fade-out
            Assert.That(w.BlendAlpha, Is.InRange(0.35f, 0.65f), "half-faded out with 10 s to go");

            Advance(w, 15f, dt: 1f);
            Assert.That(w.Stage, Is.Not.EqualTo(WeatherStage.Active));
            Assert.That(w.BlendAlpha, Is.EqualTo(0f), "fully clear once the window closes");
            Assert.That(w.IsRaining, Is.False);
        }

        [Test]
        public void ShortWindowSplitsTheFades_NeverExceedsOne()
        {
            // a 10 s window can't fit a 20 s fade-in AND a 20 s fade-out; the ramps must not overlap into >1
            var types = new[] { new WeatherType { Name = "T", FadeInDuration = 20f, FadeOutDuration = 20f, FogDensity = 1f, FishBiteIntervalMultiplier = 0.5f } };
            var sched = new[] { new WeatherSchedule { TypeIndex = 0, MinFrequency = 1f, MaxFrequency = 1f, MinDuration = 10f, MaxDuration = 10f } };
            var w = new WeatherSim(types, sched, seed: 2, cycleSeconds: 1f);   // duration = 10 s
            w.ForecastImmediately(0);
            float peak = 0f;
            for (int i = 0; i < 12; i++)
            {
                w.Step(1f);
                Assert.That(w.BlendAlpha, Is.InRange(0f, 1f), $"blend left [0,1] at step {i}");
                if (w.BlendAlpha > peak) peak = w.BlendAlpha;
            }
            // The teeth: WITHOUT the proportional split, a 10 s window against a 20 s fade-in would peak at
            // ~0.25 and the rain would never be more than a faint drizzle. This is not hypothetical -- on this
            // port's 120 s day EVERY PEI shower (0.05-0.15 cycles = 6-18 s) is shorter than fadeIn + fadeOut.
            Assert.That(peak, Is.GreaterThan(0.9f),
                        $"a short shower must still reach full strength at its midpoint (peaked at {peak:0.00})");
        }

        [Test]
        public void FishBiteMultiplierTracksTheBlend()
        {
            var w = Pei(seed: 17);
            Assert.That(w.FishBiteIntervalMultiplier, Is.EqualTo(1f), "clear weather does not change fishing");

            w.ForecastImmediately(1);                       // Heavy Rain: 0.8
            Advance(w, 25f, dt: 1f);                        // past fade-in
            Assert.That(w.FishBiteIntervalMultiplier, Is.InRange(0.79f, 0.83f),
                        "heavy rain pulls the bite interval to the asset's 0.8 (fish bite sooner)");
        }

        [Test]
        public void HeavyRainCarriesTheRippedLightningValues()
        {
            var heavy = WeatherSim.PeiTypes()[1];
            Assert.That(heavy.HasLightning, Is.True);
            Assert.That(heavy.MinLightningInterval, Is.EqualTo(15f));
            Assert.That(heavy.MaxLightningInterval, Is.EqualTo(60f));

            var light = WeatherSim.PeiTypes()[0];
            Assert.That(light.HasLightning, Is.False, "Default Rain has no lightning in the .asset");
        }

        [Test]
        public void PeiHasRainOnly_NoSnow()
        {
            // Regression against the obvious wrong guess: PEI.asset schedules two RAIN types, no snow.
            var types = WeatherSim.PeiTypes();
            Assert.That(types.Length, Is.EqualTo(2));
            foreach (var t in types)
                Assert.That(t.Name.ToLowerInvariant(), Does.Contain("rain"), $"{t.Name} is not rain -- PEI has no snow");
        }

        [Test]
        public void WindAndFogAreZeroWhenClear_AndScaleWithBlend()
        {
            var w = Pei(seed: 23);
            Assert.That(w.WindMain, Is.EqualTo(0f));
            Assert.That(w.FogDensity, Is.EqualTo(0f));

            w.ForecastImmediately(1);                       // Heavy Rain: wind 0.5, fog 1.0
            Advance(w, 25f, dt: 1f);
            Assert.That(w.WindMain, Is.InRange(0.48f, 0.52f));
            Assert.That(w.FogDensity, Is.InRange(0.95f, 1.01f));
        }

        [Test]
        public void PerpetualWeatherNeverTimesOut()
        {
            var w = Pei(seed: 29);
            w.SetPerpetual(0);
            Advance(w, 5000f, dt: 10f);
            Assert.That(w.Stage, Is.EqualTo(WeatherStage.PerpetuallyActive));
            Assert.That(w.BlendAlpha, Is.EqualTo(1f));
        }

        [Test]
        public void ClearStopsEverything()
        {
            var w = Pei(seed: 31);
            w.ForecastImmediately(0);
            Advance(w, 25f, dt: 1f);
            Assert.That(w.IsRaining, Is.True);
            w.Clear();
            Assert.That(w.Stage, Is.EqualTo(WeatherStage.None));
            Assert.That(w.BlendAlpha, Is.EqualTo(0f));
            Assert.That(w.FishBiteIntervalMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void WeatherRepeats_ManyCyclesWithoutSticking()
        {
            // the machine must return to None and re-schedule, not latch after one storm
            var w = new WeatherSim(WeatherSim.PeiTypes(), WeatherSim.PeiSchedule(), 41, cycleSeconds: 60f);
            int activations = 0;
            bool wasActive = false;
            for (int i = 0; i < 20000; i++)
            {
                w.Step(1f);
                bool active = w.Stage == WeatherStage.Active;
                if (active && !wasActive) activations++;
                wasActive = active;
            }
            Assert.That(activations, Is.GreaterThan(3), $"expected repeated weather over 20000 s, got {activations}");
        }

        [Test]
        public void EmptyScheduleStaysClearForever()
        {
            var w = new WeatherSim(WeatherSim.PeiTypes(), new WeatherSchedule[0], seed: 1);
            Advance(w, 5000f, dt: 10f);
            Assert.That(w.Stage, Is.EqualTo(WeatherStage.None));
            Assert.That(w.IsRaining, Is.False);
        }
    }
}
