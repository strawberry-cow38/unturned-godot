using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 for the per-day weather outlook (strawberry 2026-10-10: "consider time of year ... the weather for
    // today and tomorrow"). Engine-free on purpose: the claims here are about a YEAR of days, which nothing
    // running at play speed could ever observe.
    [TestFixture]
    public class WeatherOutlookTests
    {
        static WeatherType[] Types()
        {
            var t = new System.Collections.Generic.List<WeatherType>(WeatherSim.PeiTypes());
            t.AddRange(WeatherSim.VariantTypes());
            return t.ToArray();
        }

        static WeatherSchedule[] Sched()
        {
            var s = new System.Collections.Generic.List<WeatherSchedule>(WeatherSim.PeiSchedule());
            s.AddRange(WeatherSim.VariantSchedule(WeatherSim.PeiTypes().Length));
            return s.ToArray();
        }

        static WeatherOutlook.Day Day(int seed, int day)
        {
            int doy = WorldTemperature.DayOfYear(WorldTemperature.StartDayOfYear, day);
            return WeatherOutlook.ForDay(seed, day, Types(), Sched(), WorldTemperature.SeasonPhase(doy));
        }

        /// <summary>Wet fraction over a whole year with the season PINNED, so the only variable is the phase.</summary>
        static float WetFractionAt(int seed, float seasonPhase, int days = 2000)
        {
            int wet = 0;
            for (int d = 0; d < days; d++)
                if (WeatherOutlook.ForDay(seed, d, Types(), Sched(), seasonPhase).Wet) wet++;
            return wet / (float)days;
        }

        [Test]
        public void TheSameDayAlwaysGivesTheSameAnswer()
        {
            // ⭐⭐ THE PROPERTY THE WHOLE FEATURE RESTS ON. The radio reads this function and so does the sim;
            // if it were not pure, the forecast and the weather could disagree and the radio would be lying.
            for (int d = 0; d < 400; d++)
            {
                var a = Day(7, d);
                var b = Day(7, d);
                Assert.That(b.Wet, Is.EqualTo(a.Wet), $"day {d} flipped wet/clear between two reads");
                Assert.That(b.TypeIndex, Is.EqualTo(a.TypeIndex), $"day {d} changed type between two reads");
            }
        }

        [Test]
        public void TomorrowIsAskableTodayAndIsTheNextDaysAnswer()
        {
            // "today and tomorrow" only means anything if asking ahead gives the same answer you get on arrival.
            for (int d = 0; d < 200; d++)
                Assert.That(Day(11, d + 1).TypeIndex, Is.EqualTo(Day(11, d + 1).TypeIndex));
            var tomorrow = Day(11, 51);
            Assert.That(Day(11, 51).Name, Is.EqualTo(tomorrow.Name), "day 51 read twice disagreed");
        }

        [Test]
        public void DifferentSeedsGiveDifferentYears()
        {
            int same = 0;
            for (int d = 0; d < 365; d++) if (Day(1, d).TypeIndex == Day(2, d).TypeIndex) same++;
            // Not a cryptographic claim -- just that two worlds are not the same world. With ~7 outcomes,
            // chance agreement is well under half.
            Assert.That(same, Is.LessThan(250), $"two seeds agreed on {same}/365 days");
        }

        [Test]
        public void ConsecutiveDaysAreNotCorrelated()
        {
            // ⚠ THE REASON THE HASH IS FNV AND NOT `new Random(seed + day)`. Seeds differing by one give
            // System.Random near-identical early sequences, which would make a week read as one long drizzle.
            int runs = 1, prev = Day(3, 0).TypeIndex;
            for (int d = 1; d < 365; d++)
            {
                int t = Day(3, d).TypeIndex;
                if (t != prev) runs++;
                prev = t;
            }
            Assert.That(runs, Is.GreaterThan(120), $"only {runs} changes in a year -- days look correlated");
        }

        [Test]
        public void WinterIsWetterThanSummer()
        {
            float winter = WetFractionAt(5, -1f);
            float summer = WetFractionAt(5, +1f);
            Assert.That(winter, Is.GreaterThan(summer + 0.2f),
                        $"winter {winter:0.00} vs summer {summer:0.00} -- the season barely moved it");
            // and it lands near the authored dials rather than wherever the maths happened to go
            Assert.That(winter, Is.EqualTo(WeatherOutlook.WinterWetChance).Within(0.06f));
            Assert.That(summer, Is.EqualTo(WeatherOutlook.SummerWetChance).Within(0.06f));
        }

        [Test]
        public void WinterLeansOnTheHEAVIERWeathers()
        {
            // Season changes WHAT, not just HOW OFTEN. Compare mean severity of the WET days only, so this
            // cannot be satisfied by winter simply raining more.
            float SevMean(float phase)
            {
                float sum = 0f; int n = 0;
                for (int d = 0; d < 2000; d++)
                {
                    var o = WeatherOutlook.ForDay(9, d, Types(), Sched(), phase);
                    if (o.Wet) { sum += o.Severity; n++; }
                }
                return n > 0 ? sum / n : 0f;
            }
            float w = SevMean(-1f), s = SevMean(+1f);
            Assert.That(w, Is.GreaterThan(s),
                        $"winter mean severity {w:0.000} is not above summer's {s:0.000}");
        }

        [Test]
        public void AClearDayReportsNoTypeAndNoSeverity()
        {
            bool sawClear = false;
            for (int d = 0; d < 365 && !sawClear; d++)
            {
                var o = Day(13, d);
                if (o.Wet) continue;
                sawClear = true;
                Assert.That(o.TypeIndex, Is.EqualTo(-1));
                Assert.That(o.ScheduleIndex, Is.EqualTo(-1), "a clear day must not name a schedule entry");
                Assert.That(o.Severity, Is.EqualTo(0f));
                Assert.That(o.Name, Is.EqualTo("Clear"));
            }
            Assert.That(sawClear, Is.True, "no clear day in a year -- the test proved nothing");
        }

        [Test]
        public void EveryWetDayNamesARealScheduleEntry()
        {
            var sched = Sched();
            var types = Types();
            for (int d = 0; d < 500; d++)
            {
                var o = Day(17, d);
                if (!o.Wet) continue;
                Assert.That(o.ScheduleIndex, Is.InRange(0, sched.Length - 1));
                Assert.That(o.TypeIndex, Is.EqualTo(sched[o.ScheduleIndex].TypeIndex),
                            $"day {d}: type and schedule entry disagree");
                Assert.That(o.Name, Is.EqualTo(types[o.TypeIndex].Name));
            }
        }

        // ---- SNOW (strawberry 2026-10-10: "add snowfall") --------------------------------------------------
        static WeatherType[] TypesWithSnow()
        {
            var t = new System.Collections.Generic.List<WeatherType>(Types());
            t.AddRange(WeatherSim.SnowTypes());
            return t.ToArray();
        }

        static WeatherSchedule[] SchedWithSnow(out int snowIdx)
        {
            var s = new System.Collections.Generic.List<WeatherSchedule>(Sched());
            snowIdx = s.Count;
            s.AddRange(WeatherSim.SnowSchedule(Types().Length));
            return s.ToArray();
        }

        [Test]
        public void AFreezingWetDayFallsAsSnow()
        {
            var types = TypesWithSnow();
            var sched = SchedWithSnow(out int snowIdx);
            int wet = 0, snow = 0;
            for (int d = 0; d < 400; d++)
            {
                var o = WeatherOutlook.ForDay(21, d, types, sched, -1f, dayMeanC: -6f, snowScheduleIndex: snowIdx);
                if (!o.Wet) continue;
                wet++;
                if (o.Snow) snow++;
            }
            Assert.That(wet, Is.GreaterThan(50), "no wet days to judge");
            Assert.That(snow, Is.EqualTo(wet), $"only {snow} of {wet} freezing wet days fell as snow");
        }

        [Test]
        public void AWARMWetDayNeverFallsAsSnow()
        {
            // ⚠ THE CONTROL. Without it "every wet day is snow" passes a function that ignores temperature
            // entirely -- which is exactly the bug worth catching, since the whole claim is that the
            // TEMPERATURE decides the form.
            var types = TypesWithSnow();
            var sched = SchedWithSnow(out int snowIdx);
            int wet = 0, snow = 0;
            for (int d = 0; d < 400; d++)
            {
                var o = WeatherOutlook.ForDay(21, d, types, sched, -1f, dayMeanC: 14f, snowScheduleIndex: snowIdx);
                if (!o.Wet) continue;
                wet++;
                if (o.Snow) snow++;
            }
            Assert.That(wet, Is.GreaterThan(50));
            Assert.That(snow, Is.Zero, $"{snow} warm days fell as snow");
        }

        [Test]
        public void SnowNamesTheSnowTypeAndStaysInTheSchedule()
        {
            var types = TypesWithSnow();
            var sched = SchedWithSnow(out int snowIdx);
            var o = WeatherOutlook.ForDay(21, 3, types, sched, -1f, dayMeanC: -6f, snowScheduleIndex: snowIdx);
            Assert.That(o.Wet, Is.True);
            Assert.That(o.Snow, Is.True);
            Assert.That(o.ScheduleIndex, Is.EqualTo(snowIdx));
            Assert.That(o.TypeIndex, Is.EqualTo(sched[snowIdx].TypeIndex));
            Assert.That(o.Name, Is.EqualTo("Snowfall"));
            Assert.That(types[o.TypeIndex].HasLightning, Is.False, "thundersnow is not the default");
        }

        [Test]
        public void AWorldWithNoSnowTypeIsUnAFFECTEDByTheCold()
        {
            // ⚠ The second control: pass no snow index (the default) and a freezing day must behave exactly as
            // it did before snow existed, so the infinite world and any map without the type are untouched.
            for (int d = 0; d < 120; d++)
            {
                var warm = WeatherOutlook.ForDay(33, d, Types(), Sched(), -1f);
                var cold = WeatherOutlook.ForDay(33, d, Types(), Sched(), -1f, dayMeanC: -20f);
                Assert.That(cold.Wet, Is.EqualTo(warm.Wet), $"day {d} changed wetness with no snow type available");
                Assert.That(cold.TypeIndex, Is.EqualTo(warm.TypeIndex), $"day {d} changed type with no snow type");
                Assert.That(cold.Snow, Is.False);
            }
        }

        [Test]
        public void AnEmptyScheduleIsClearRatherThanACrash()
        {
            var o = WeatherOutlook.ForDay(1, 1, Types(), new WeatherSchedule[0], 0f);
            Assert.That(o.Wet, Is.False);
            Assert.That(o.Name, Is.EqualTo("Clear"));
        }

        [Test]
        public void TheSimKeepsItsUniformRollUntilAChooserIsSet()
        {
            // ⚠ CONTROL ON THE OPT-IN. The infinite world and the other sim tests run without a chooser, so
            // the default path must be untouched -- a seasonal outlook imposed from inside WeatherSim would
            // change their weather silently.
            var sim = new WeatherSim(Types(), Sched(), seed: 42);
            Assert.That(sim.ScheduleChooser, Is.Null);
            sim.Step(1f);
            Assert.That(sim.Stage, Is.Not.EqualTo(WeatherStage.None), "default path stopped scheduling");

            var forced = new WeatherSim(Types(), Sched(), seed: 42) { ScheduleChooser = () => -1 };
            forced.Step(1f);
            Assert.That(forced.Stage, Is.EqualTo(WeatherStage.None), "-1 should mean a clear day");
            Assert.That(forced.ActiveTypeIndex, Is.EqualTo(-1));

            var pinned = new WeatherSim(Types(), Sched(), seed: 42) { ScheduleChooser = () => 3 };
            pinned.Step(1f);
            Assert.That(pinned.ActiveTypeIndex, Is.EqualTo(Sched()[3].TypeIndex), "chooser was ignored");
        }
    }
}
