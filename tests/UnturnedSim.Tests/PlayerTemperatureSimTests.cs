using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Layer 2 of the temperature work: one world ambient turned into what THIS player feels, given their
    // coat, their exertion and how wet they are (strawberry 2026-09-10).
    [TestFixture]
    public class PlayerTemperatureSimTests
    {
        const float Dt = 0.02f;
        const float Comfort = (PlayerTemperatureSim.ComfortLowC + PlayerTemperatureSim.ComfortHighC) * 0.5f;

        static PlayerTemperatureSim Run(float ambient, float seconds, bool exerting = false, bool raining = false,
                                        bool sheltered = false, bool waterproof = false,
                                        float cold = 0f, float heat = 0f, float source = 0f)
        {
            var t = new PlayerTemperatureSim();
            for (int i = 0; i < (int)(seconds / Dt); i++)
                t.Step(ambient, source, exerting, raining, sheltered, waterproof, cold, heat, Dt);
            return t;
        }

        [Test]
        public void Bands_SplitAtTheStatedEdges()
        {
            Assert.That(PlayerTemperatureSim.BandFor(-10f), Is.EqualTo(PlayerTemperatureSim.Band.Freezing));
            Assert.That(PlayerTemperatureSim.BandFor(0f), Is.EqualTo(PlayerTemperatureSim.Band.Cold));
            Assert.That(PlayerTemperatureSim.BandFor(20f), Is.EqualTo(PlayerTemperatureSim.Band.Comfortable));
            Assert.That(PlayerTemperatureSim.BandFor(35f), Is.EqualTo(PlayerTemperatureSim.Band.Hot));
            Assert.That(PlayerTemperatureSim.BandFor(50f), Is.EqualTo(PlayerTemperatureSim.Band.Boiling));
        }

        [Test]
        public void ColdAmbient_ChillsYou_TowardIt()
        {
            var t = Run(-20f, 300f);
            Assert.That(t.BodyC, Is.LessThan(PlayerTemperatureSim.FreezingBelowC));
        }

        [Test]
        public void Insulation_ClosesTheGap_ButNeverOvershootsPastComfortable()
        {
            // THE cap that matters. A plain additive bonus would turn -30 C plus 50 C of parka into +20 C, so
            // stacking coats would make a blizzard nicer than a spring day. Best case is "comfortable".
            float t = PlayerTemperatureSim.TargetC(-30f, 0f, false, 0f, insulationColdC: 50f, insulationHeatC: 0f);
            Assert.That(t, Is.EqualTo(PlayerTemperatureSim.ComfortLowC).Within(1e-3f));
        }

        [Test]
        public void ColdInsulation_DoesNothingInTheHeat()
        {
            // Two separate values is the point of the spec: a parka must not help in a desert.
            float bare = PlayerTemperatureSim.TargetC(45f, 0f, false, 0f, 0f, 0f);
            float parka = PlayerTemperatureSim.TargetC(45f, 0f, false, 0f, insulationColdC: 40f, insulationHeatC: 0f);
            Assert.That(parka, Is.EqualTo(bare).Within(1e-4f));
        }

        [Test]
        public void Exertion_AddsHeat_AndIsAppliedBeforeClothing()
        {
            // Ordering check: a coat cannot insulate you from heat you generate inside it. Running in a parka
            // at the comfort ceiling must end up HOTTER than standing in it, not equal.
            float still = PlayerTemperatureSim.TargetC(28f, 0f, exerting: false, 0f, 40f, 0f);
            float working = PlayerTemperatureSim.TargetC(28f, 0f, exerting: true, 0f, 40f, 0f);
            Assert.That(working, Is.GreaterThan(still));
        }

        [Test]
        public void Rain_SoaksYou_AndShelterDriesYouOut()
        {
            var wet = Run(15f, 60f, raining: true);
            Assert.That(wet.Wetness, Is.GreaterThan(0.7f));

            for (int i = 0; i < 60f / Dt; i++) wet.Step(15f, 0f, false, true, sheltered: true, false, 0f, 0f, Dt);
            Assert.That(wet.Wetness, Is.LessThan(0.7f), "under cover you dry out even while it rains");
        }

        [Test]
        public void Waterproof_KeepsYouDry_InTheOpen()
        {
            var t = Run(15f, 120f, raining: true, waterproof: true);
            Assert.That(t.Wetness, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void BeingSoaked_ReadsColder_ThanBeingDry()
        {
            float dry = PlayerTemperatureSim.TargetC(12f, 0f, false, wetness: 0f, 0f, 0f);
            float soaked = PlayerTemperatureSim.TargetC(12f, 0f, false, wetness: 1f, 0f, 0f);
            Assert.That(dry - soaked, Is.EqualTo(PlayerTemperatureSim.SoakedChillC).Within(1e-3f));
        }

        [Test]
        public void BeingSoaked_HelpsInAHeatwave()
        {
            // Falls out of applying the chill at any ambient rather than gating it to cold. Asserted because
            // it is the kind of emergent behaviour someone would later "fix" as a bug.
            float dry = PlayerTemperatureSim.TargetC(45f, 0f, false, 0f, 0f, 0f);
            float soaked = PlayerTemperatureSim.TargetC(45f, 0f, false, 1f, 0f, 0f);
            Assert.That(soaked, Is.LessThan(dry));
        }

        [Test]
        public void HeatSource_WarmsYou_AndACoolingSourceDoesTheOpposite()
        {
            var fire = Run(-10f, 300f, source: 25f);
            var bare = Run(-10f, 300f);
            Assert.That(fire.BodyC, Is.GreaterThan(bare.BodyC));

            var chiller = Run(30f, 300f, source: -20f);
            var unchilled = Run(30f, 300f);
            Assert.That(chiller.BodyC, Is.LessThan(unchilled.BodyC));
        }

        [Test]
        public void Approach_IsTickRateIndependent()
        {
            // Same wall-clock, different tick sizes: an exponential approach must land in the same place. A
            // fixed per-tick step would not, and the difference only shows on a machine running a different
            // rate from the one it was tuned on.
            var fast = new PlayerTemperatureSim();
            for (int i = 0; i < 5000; i++) fast.Step(-20f, 0f, false, false, false, false, 0f, 0f, 0.02f);
            var slow = new PlayerTemperatureSim();
            for (int i = 0; i < 1000; i++) slow.Step(-20f, 0f, false, false, false, false, 0f, 0f, 0.10f);
            Assert.That(fast.BodyC, Is.EqualTo(slow.BodyC).Within(0.05f));
        }

        [Test]
        public void FreshSim_StartsComfortable_NotAtAbsoluteZero()
        {
            Assert.That(new PlayerTemperatureSim().CurrentBand, Is.EqualTo(PlayerTemperatureSim.Band.Comfortable));
            Assert.That(new PlayerTemperatureSim().BodyC, Is.EqualTo(Comfort).Within(1e-4f));
        }
    
        // ---- the HUD reading (strawberry 2026-09-11: "middle is comfortable, the bar goes left/down for
        // cold, and right/up for hot") ----

        [Test]
        public void Comfort_IsZeroAcrossTheWholeComfortableBand()
        {
            // Not zero at one exact degree: a player who is fine must see a bar sitting still, not one
            // twitching either side of centre all day.
            Assert.That(PlayerTemperatureSim.ComfortFor(PlayerTemperatureSim.ComfortLowC), Is.EqualTo(0f));
            Assert.That(PlayerTemperatureSim.ComfortFor(20f), Is.EqualTo(0f));
            Assert.That(PlayerTemperatureSim.ComfortFor(PlayerTemperatureSim.ComfortHighC), Is.EqualTo(0f));
        }

        [Test]
        public void Comfort_GoesNegativeForCold_AndPositiveForHot()
        {
            Assert.That(PlayerTemperatureSim.ComfortFor(0f), Is.LessThan(0f));
            Assert.That(PlayerTemperatureSim.ComfortFor(35f), Is.GreaterThan(0f));
        }

        [Test]
        public void Comfort_IsFullExactlyWhereTheBandTurnsLethal()
        {
            // "The bar is maxed" and "this is hurting me" have to be the same fact, or the player has two
            // things to learn instead of one.
            Assert.That(PlayerTemperatureSim.ComfortFor(PlayerTemperatureSim.FreezingBelowC), Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(PlayerTemperatureSim.ComfortFor(PlayerTemperatureSim.BoilingAboveC), Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void Comfort_ClampsBeyondTheLethalEdges()
        {
            Assert.That(PlayerTemperatureSim.ComfortFor(-40f), Is.EqualTo(-1f));
            Assert.That(PlayerTemperatureSim.ComfortFor(80f), Is.EqualTo(1f));
        }

        [Test]
        public void Comfort_IsMonotonic_AcrossTheWholeRange()
        {
            // Guards a sign or span error that would make the bar jump the wrong way somewhere in the middle
            // -- which no single-point assertion above would catch.
            float prev = -2f;
            for (float c = -40f; c <= 80f; c += 0.5f)
            {
                float v = PlayerTemperatureSim.ComfortFor(c);
                Assert.That(v, Is.GreaterThanOrEqualTo(prev), $"comfort fell at {c} C");
                prev = v;
            }
        }
}
}
