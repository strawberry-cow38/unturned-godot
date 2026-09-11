using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for contaminated ground. These exist partly to give ClothingDef.proofRadiation something
    // that can fail: the flag has been parsed from item data all along with no hazard to protect against,
    // so nothing could tell whether it worked.
    [TestFixture]
    public class DeadzoneSimTests
    {
        static RadiationGear Suit(int quality = 100) =>
            new RadiationGear { MaskProofs = true, MaskQuality = quality, ShirtProofs = true, PantsProofs = true };
        static RadiationGear MaskOnly(int quality = 100) =>
            new RadiationGear { MaskProofs = true, MaskQuality = quality, ShirtProofs = false, PantsProofs = false };
        static RadiationGear Nothing() => new RadiationGear();

        // Step past the entry grace, discarding what it returns, so a test can measure a settled zone.
        static DeadzoneSim Settled(in DeadzoneDef zone, in RadiationGear gear)
        {
            var sim = new DeadzoneSim();
            sim.Step(zone, gear, DeadzoneSim.EntryGrace);
            return sim;
        }

        [Test]
        public void Clipping_The_Edge_Of_A_Zone_Costs_Nothing()
        {
            var sim = new DeadzoneSim();
            var r = sim.Step(DeadzoneDef.Default(), Nothing(), 0.2f);
            Assert.That(r.Radiation, Is.EqualTo(0f), "a corner-clip should not be instantly punishing");
        }

        [Test]
        public void Standing_In_It_Unprotected_Irradiates()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, Nothing());
            var r = sim.Step(zone, Nothing(), 1f);

            Assert.That(r.Protected, Is.False);
            Assert.That(r.Radiation, Is.EqualTo(zone.UnprotectedRadiationPerSecond).Within(0.001f));
        }

        [Test]
        public void A_Filtered_Mask_Holds_But_Still_Costs_You()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, MaskOnly());
            var r = sim.Step(zone, MaskOnly(), 1f);

            Assert.That(r.Protected, Is.True);
            // A SUIT SLOWS THE DOSE, IT DOES NOT STOP IT (strawberry 2026-09-11: deadzones deal infection, not
            // health). The old version asserted a holding suit kept the virus out entirely and took health
            // instead; with health off the table that would make a sealed suit total immunity, and the zone
            // stops being a hazard the moment you own one.
            Assert.That(r.Radiation, Is.EqualTo(zone.ProtectedRadiationPerSecond).Within(0.001f));
            Assert.That(r.Radiation, Is.LessThan(zone.UnprotectedRadiationPerSecond), "the suit has to be worth wearing");
        }

        [Test]
        public void A_Spent_Filter_Protects_Nothing()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, MaskOnly(quality: 0));
            var r = sim.Step(zone, MaskOnly(quality: 0), 1f);

            Assert.That(r.Protected, Is.False, "a mask with no filter left is a hat");
            Assert.That(r.Radiation, Is.EqualTo(zone.UnprotectedRadiationPerSecond).Within(0.001f));
        }

        [Test]
        public void A_Mask_Alone_Is_Not_Enough_For_A_Full_Suit_Zone()
        {
            var zone = DeadzoneDef.Default(DeadzoneKind.FullSuitRadiation);
            Assert.That(DeadzoneSim.IsProtected(zone, MaskOnly()), Is.False);
            Assert.That(DeadzoneSim.IsProtected(zone, Suit()), Is.True);

            // ...and the same mask IS enough for the ordinary kind, which is what distinguishes them.
            var plain = DeadzoneDef.Default();
            Assert.That(DeadzoneSim.IsProtected(plain, MaskOnly()), Is.True);
        }

        [Test]
        public void A_Full_Suit_Zone_Wants_Every_Piece()
        {
            var zone = DeadzoneDef.Default(DeadzoneKind.FullSuitRadiation);
            var noShirt = Suit(); noShirt.ShirtProofs = false;
            var noPants = Suit(); noPants.PantsProofs = false;
            Assert.That(DeadzoneSim.IsProtected(zone, noShirt), Is.False);
            Assert.That(DeadzoneSim.IsProtected(zone, noPants), Is.False);
        }

        [Test]
        public void The_Filter_Burns_Down_While_It_Is_Working()
        {
            var zone = DeadzoneDef.Default();
            var gear = MaskOnly();
            var sim = Settled(zone, gear);

            int burned = 0;
            for (int i = 0; i < 50; i++) burned += sim.Step(zone, gear, 0.1f).MaskQualityLost;

            // 5 s at 2 quality/s -- allow a point of rounding on the fractional carry.
            Assert.That(burned, Is.EqualTo(10).Within(1), $"burned {burned}");
        }

        [Test]
        public void Filter_Wear_Never_Exceeds_What_The_Mask_Has_Left()
        {
            var zone = DeadzoneDef.Default();
            zone.MaskFilterLossPerSecond = 1000f;      // a brutal zone
            var gear = MaskOnly(quality: 3);
            var sim = Settled(zone, gear);

            var r = sim.Step(zone, gear, 1f);
            Assert.That(r.MaskQualityLost, Is.LessThanOrEqualTo(3), "cannot burn filter the mask does not have");
        }

        [Test]
        public void An_Unprotected_Player_Burns_No_Filter()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, Nothing());
            var r = sim.Step(zone, Nothing(), 1f);
            Assert.That(r.MaskQualityLost, Is.EqualTo(0));
        }

        [Test]
        public void Leaving_Resets_The_Grace_So_Re_Entry_Starts_Clean()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, Nothing());
            Assert.That(sim.Step(zone, Nothing(), 0.5f).Radiation, Is.GreaterThan(0f), "test setup: settled");

            sim.Exit();
            Assert.That(sim.IsInside, Is.False);
            Assert.That(sim.Step(zone, Nothing(), 0.2f).Radiation, Is.EqualTo(0f),
                "re-entering should start a fresh grace, not resume mid-tick");
        }

        [Test]
        public void Dose_Scales_With_Time_Not_With_Call_Count()
        {
            // A caller stepping at 50 Hz and one stepping at 10 Hz must reach the same total.
            var zone = DeadzoneDef.Default();
            var fast = Settled(zone, Nothing());
            var slow = Settled(zone, Nothing());

            float fastTotal = 0f, slowTotal = 0f;
            for (int i = 0; i < 50; i++) fastTotal += fast.Step(zone, Nothing(), 0.02f).Radiation;
            for (int i = 0; i < 10; i++) slowTotal += slow.Step(zone, Nothing(), 0.10f).Radiation;

            Assert.That(fastTotal, Is.EqualTo(slowTotal).Within(0.01f));
        }

        [Test]
        public void A_Volume_Knows_What_Is_Inside_It()
        {
            var v = new DeadzoneVolumeDef
            {
                Center = new Vector3(100f, 0f, -50f),
                HalfExtent = new Vector3(20f, 10f, 20f),
                Zone = DeadzoneDef.Default(),
            };
            Assert.That(v.Contains(new Vector3(100f, 0f, -50f)), Is.True);
            Assert.That(v.Contains(new Vector3(119f, 9f, -31f)), Is.True);
            Assert.That(v.Contains(new Vector3(121f, 0f, -50f)), Is.False);
            Assert.That(v.Contains(new Vector3(100f, 11f, -50f)), Is.False, "the height bound has to count too");
        }
    
        // ---- INFECTION IS THE ONLY AXIS (strawberry 2026-09-11: "wire deadzones to deal infection damage
        // instead of hp") ----

        [Test]
        public void A_Deadzone_Has_Exactly_One_Way_To_Hurt_You()
        {
            // The claim the whole change rests on, asserted structurally rather than by reading the struct:
            // whatever a step returns, nothing in it can reduce health directly. If a Damage field is ever
            // reintroduced this stops compiling, which is the loudest failure available and the right one --
            // a second damage path is exactly what this change existed to remove.
            var fields = typeof(DeadzoneTickResult).GetFields();
            Assert.That(System.Array.Exists(fields, f => f.Name == "Radiation"), Is.True);
            Assert.That(System.Array.Exists(fields, f => f.Name == "Damage"), Is.False,
                "a deadzone must not have a health path any more -- PlayerVitalsSim owns what infection costs");
        }

        [Test]
        public void Unprotected_Exposure_Reaches_Fatal_Infection_In_A_Sane_Time()
        {
            // Pins the RATE against the scale it actually lives on. Infection is 0..1 and fatal at 1.0, so a
            // rate ported straight from the old health numbers (8/second) would kill in an eighth of a second.
            // Asserted as a window rather than an exact figure so retuning does not come back here, while a
            // rate off by an order of magnitude in either direction still trips it.
            var zone = DeadzoneDef.Default();
            float secondsToFatal = 1f / zone.UnprotectedRadiationPerSecond;
            Assert.That(secondsToFatal, Is.GreaterThan(15f), "instantly lethal contamination is a wall, not a hazard");
            Assert.That(secondsToFatal, Is.LessThan(120f), "...and two minutes of standing in poison should not be survivable");
        }

        [Test]
        public void A_Suit_Buys_Time_Rather_Than_Immunity()
        {
            // The pair to the check above, and the reason a sealed suit is not just a toggle: it has to be
            // meaningfully slower AND still finite, or the zone stops being a place you have to leave.
            var zone = DeadzoneDef.Default();
            float suited = 1f / zone.ProtectedRadiationPerSecond;
            float bare = 1f / zone.UnprotectedRadiationPerSecond;
            Assert.That(zone.ProtectedRadiationPerSecond, Is.GreaterThan(0f), "immunity would make the zone scenery");
            Assert.That(suited, Is.GreaterThan(bare * 4f), "the suit has to be clearly worth wearing");
        }

        [Test]
        public void A_Short_Visit_Stays_Under_The_Self_Clearing_Mark()
        {
            // The interaction that makes brief trips survivable BY DESIGN: PlayerVitalsSim drains infection
            // back down below 0.5, so a dose that stops short of halfway heals off. Ten seconds unprotected
            // -- long enough to grab something and run -- has to land under that mark, or "duck in and out"
            // is not a playable option and the zone is a wall after all.
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, Nothing());
            float dose = 0f;
            for (int i = 0; i < 10; i++) dose += sim.Step(zone, Nothing(), 1f).Radiation;
            Assert.That(dose, Is.LessThan(0.5f), $"10 s unprotected doses {dose:0.###}, which must stay under the 0.5 self-clear mark");
        }
}
}
