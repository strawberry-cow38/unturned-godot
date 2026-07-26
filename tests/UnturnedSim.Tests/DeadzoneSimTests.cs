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
            Assert.That(r.Damage, Is.EqualTo(0f), "a corner-clip should not be instantly punishing");
            Assert.That(r.Radiation, Is.EqualTo(0f));
        }

        [Test]
        public void Standing_In_It_Unprotected_Hurts_And_Irradiates()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, Nothing());
            var r = sim.Step(zone, Nothing(), 1f);

            Assert.That(r.Protected, Is.False);
            Assert.That(r.Damage, Is.EqualTo(zone.UnprotectedDamagePerSecond).Within(0.001f));
            Assert.That(r.Radiation, Is.EqualTo(zone.RadiationPerSecond).Within(0.001f));
        }

        [Test]
        public void A_Filtered_Mask_Holds_But_Still_Costs_You()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, MaskOnly());
            var r = sim.Step(zone, MaskOnly(), 1f);

            Assert.That(r.Protected, Is.True);
            Assert.That(r.Damage, Is.EqualTo(zone.ProtectedDamagePerSecond).Within(0.001f));
            Assert.That(r.Radiation, Is.EqualTo(0f), "a holding suit should keep the virus out");
            Assert.That(r.Damage, Is.LessThan(zone.UnprotectedDamagePerSecond), "the suit has to be worth wearing");
        }

        [Test]
        public void A_Spent_Filter_Protects_Nothing()
        {
            var zone = DeadzoneDef.Default();
            var sim = Settled(zone, MaskOnly(quality: 0));
            var r = sim.Step(zone, MaskOnly(quality: 0), 1f);

            Assert.That(r.Protected, Is.False, "a mask with no filter left is a hat");
            Assert.That(r.Damage, Is.EqualTo(zone.UnprotectedDamagePerSecond).Within(0.001f));
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
            Assert.That(sim.Step(zone, Nothing(), 0.5f).Damage, Is.GreaterThan(0f), "test setup: settled");

            sim.Exit();
            Assert.That(sim.IsInside, Is.False);
            Assert.That(sim.Step(zone, Nothing(), 0.2f).Damage, Is.EqualTo(0f),
                "re-entering should start a fresh grace, not resume mid-tick");
        }

        [Test]
        public void Damage_Scales_With_Time_Not_With_Call_Count()
        {
            // A caller stepping at 50 Hz and one stepping at 10 Hz must reach the same total.
            var zone = DeadzoneDef.Default();
            var fast = Settled(zone, Nothing());
            var slow = Settled(zone, Nothing());

            float fastTotal = 0f, slowTotal = 0f;
            for (int i = 0; i < 50; i++) fastTotal += fast.Step(zone, Nothing(), 0.02f).Damage;
            for (int i = 0; i < 10; i++) slowTotal += slow.Step(zone, Nothing(), 0.10f).Damage;

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
    }
}
