using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Layer 1 of the temperature work (strawberry 2026-09-10: "wire a universal temperature on the map;
    // changes with weather, time of day, season"). Pure curve, so every claim here is checkable without a
    // running game -- which is the whole reason the season/diurnal maths does not live in DayNightCycle.
    [TestFixture]
    public class WorldTemperatureTests
    {
        const float Noon = 0.5f;

        [Test]
        public void DayOfYear_WrapsAcrossTheNewYear()
        {
            // Start in mid-December, run 30 days: lands in mid-January, not day 379.
            Assert.That(WorldTemperature.DayOfYear(350, 30), Is.EqualTo(15));
        }

        [Test]
        public void DayOfYear_ClampsNegativeElapsed_RatherThanWrappingBackwards()
        {
            // DayNightCycle.Day is monotonic, so a negative is a caller bug. Wrapping it would put the world
            // in a plausible-looking WRONG season, which is the failure that hides.
            Assert.That(WorldTemperature.DayOfYear(100, -5), Is.EqualTo(100));
        }

        [Test]
        public void SeasonPhase_IsMinusOneAtTheColdestDay_AndPlusOneHalfAYearLater()
        {
            Assert.That(WorldTemperature.SeasonPhase(WorldTemperature.ColdestDayOfYear), Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(WorldTemperature.SeasonPhase(WorldTemperature.ColdestDayOfYear + 182), Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void Diurnal_IsColdestAtDawn_NotAtMidnight()
        {
            // The anchor that matters: ground keeps radiating all night, so the minimum is at ~05:00. A curve
            // anchored at midnight would have dawn already warming, which is backwards.
            float dawn = WorldTemperature.DiurnalPhase(WorldTemperature.ColdestTimeOfDay01);
            float midnight = WorldTemperature.DiurnalPhase(0f);
            Assert.That(dawn, Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(dawn, Is.LessThan(midnight));
        }

        [Test]
        public void WinterNight_IsColderThanSummerAfternoon_ByRoughlyTheFullSwing()
        {
            float winterNight = WorldTemperature.AmbientC(WorldTemperature.ColdestDayOfYear, WorldTemperature.ColdestTimeOfDay01);
            float summerDay = WorldTemperature.AmbientC(WorldTemperature.ColdestDayOfYear + 182, WorldTemperature.ColdestTimeOfDay01 + 0.5f);
            Assert.That(winterNight, Is.EqualTo(WorldTemperature.BaseMeanC - 20f).Within(0.05f));
            Assert.That(summerDay, Is.EqualTo(WorldTemperature.BaseMeanC + 20f).Within(0.05f));
        }

        [Test]
        public void SameHour_DifferentSeason_ActuallyDiffers()
        {
            // Guards the mistake of wiring the diurnal term and forgetting the seasonal one: both readings are
            // at the same clock time, so only the date can separate them.
            float jan = WorldTemperature.AmbientC(15, Noon);
            float jul = WorldTemperature.AmbientC(197, Noon);
            Assert.That(jul - jan, Is.EqualTo(2f * WorldTemperature.SeasonAmplitude).Within(0.2f));
        }

        [Test]
        public void Weather_CoolsAndScalesWithItsBlend()
        {
            Assert.That(WorldTemperature.WeatherOffsetC("Rain", 1f), Is.EqualTo(-5f).Within(1e-4f));
            Assert.That(WorldTemperature.WeatherOffsetC("Rain", 0.5f), Is.EqualTo(-2.5f).Within(1e-4f));
            Assert.That(WorldTemperature.WeatherOffsetC("Rain", 0f), Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void Weather_UnknownArchetype_ReadsZero_NotAGuess()
        {
            Assert.That(WorldTemperature.WeatherOffsetC("Aurora", 1f), Is.EqualTo(0f));
            Assert.That(WorldTemperature.WeatherOffsetC(null, 1f), Is.EqualTo(0f));
        }

        [Test]
        public void Weather_BlendIsClamped_SoAnOutOfRangeAlphaCannotAmplifyIt()
        {
            Assert.That(WorldTemperature.WeatherOffsetC("Snow", 5f), Is.EqualTo(-10f).Within(1e-4f));
            Assert.That(WorldTemperature.WeatherOffsetC("Snow", -3f), Is.EqualTo(0f).Within(1e-4f));
        }
    }
}
