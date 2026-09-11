using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    /// <summary>The hidden absorbed dose (strawberry 2026-09-11: "radiation is a separate hidden thing
    /// different from infection").
    ///
    /// The whole reason radiation is not just more infection is that the two behave OPPOSITELY, so these
    /// tests are mostly about the difference rather than about either one: the dose washes out and gives you
    /// your legs back, the scarring it left does not. A single stat could satisfy neither half.</summary>
    [TestFixture]
    public class RadiationSimTests
    {
        static PlayerVitalsSim.Multipliers None => PlayerVitalsSim.Multipliers.None;

        /// <summary>Step only the decay: no sprint, no submersion, no survival drain, no bleed.</summary>
        static void Idle(PlayerVitalsSim v, float seconds, float dt = 0.1f)
        {
            for (float t = 0f; t < seconds; t += dt) v.Step(false, false, false, false, dt, None);
        }

        [Test]
        public void Leaving_Washes_The_Dose_Out_But_The_Infection_Stays()
        {
            // The headline claim of the whole feature, in one test.
            var v = new PlayerVitalsSim { Radiation = 1f, Infection = 0.8f };
            Idle(v, 80f);

            Assert.That(v.Radiation, Is.EqualTo(0f), "a full dose should wash out in ~66 s of being outside");
            // 0.8 sits ABOVE InfectionSelfClearBelow, so the virus holds -- which is the point. If this ever
            // reads clean, radiation has started un-infecting people and the two stats have collapsed back
            // into one.
            Assert.That(v.Infection, Is.EqualTo(0.8f).Within(0.001f), "the scar must outlive the dose");
        }

        [Test]
        public void The_Dose_Decays_From_Any_Level_Unlike_Infection()
        {
            // Infection holds above 0.5; radiation deliberately has no such threshold, so a dose taken deep
            // in a zone is still survivable if you walk out.
            var high = new PlayerVitalsSim { Radiation = 0.9f };
            var low = new PlayerVitalsSim { Radiation = 0.2f };
            Idle(high, 10f);
            Idle(low, 10f);

            Assert.That(high.Radiation, Is.EqualTo(0.9f - 0.15f).Within(0.01f));
            Assert.That(low.Radiation, Is.EqualTo(0.2f - 0.15f).Within(0.01f));
        }

        [Test]
        public void Major_Radiation_Is_The_Sprint_Gate()
        {
            var v = new PlayerVitalsSim();
            v.Radiation = PlayerVitalsSim.MajorRadiation - 0.01f;
            Assert.That(v.MajorlyIrradiated, Is.False);
            v.Radiation = PlayerVitalsSim.MajorRadiation;
            Assert.That(v.MajorlyIrradiated, Is.True, "at the threshold itself, not merely past it");
        }

        [Test]
        public void Sprint_Comes_Back_As_The_Dose_Falls()
        {
            // "sprinting/jumping is restored" -- the gate has to reopen on its own, or leaving a zone would be
            // a permanent injury rather than a recovery.
            var v = new PlayerVitalsSim { Radiation = 0.7f };
            Assert.That(v.MajorlyIrradiated, Is.True);
            Idle(v, 15f);
            Assert.That(v.MajorlyIrradiated, Is.False, "0.7 -> below 0.6 after 15 s outside");
        }

        [Test]
        public void Scarring_Scales_With_The_Dose_You_Are_CARRYING()
        {
            // Not with the zone's rate: someone who just stepped in and someone saturated take the same dose
            // per second and must NOT take the same infection per second.
            var fresh = new PlayerVitalsSim { Radiation = 0.05f };
            var saturated = new PlayerVitalsSim { Radiation = 0.95f };
            fresh.AbsorbDose(0.01f, 1f);
            saturated.AbsorbDose(0.01f, 1f);

            Assert.That(saturated.Infection, Is.GreaterThan(fresh.Infection * 5f),
                "a full body burden should scar far faster than a fresh one");
        }

        [Test]
        public void Irradiate_And_AbsorbDose_Agree()
        {
            // Irradiate is now a thin wrapper. If these ever diverge, one of the two call sites (SP shell vs
            // the deadzone sink) is silently taking a different dose than the other.
            var a = new PlayerVitalsSim();
            var b = new PlayerVitalsSim();
            a.Irradiate(0.4f, 0.25f);
            b.AbsorbDose(0.4f * 0.25f, 0.25f);

            Assert.That(a.Radiation, Is.EqualTo(b.Radiation).Within(1e-6f));
            Assert.That(a.Infection, Is.EqualTo(b.Infection).Within(1e-6f));
        }

        [Test]
        public void A_Zero_Length_Step_Absorbs_Nothing()
        {
            var v = new PlayerVitalsSim();
            v.AbsorbDose(0.5f, 0f);
            Assert.That(v.Radiation, Is.EqualTo(0f), "dt<=0 must be a no-op, not a free dose");
        }

        // ---- edge falloff (strawberry: "rads at the edge of the zone are tamer ... a warning to turn around")

        static DeadzoneVolumeDef Box() => new DeadzoneVolumeDef
        {
            Center = new Vector3(0f, 0f, 0f),
            HalfExtent = new Vector3(10f, 10f, 10f),
            Zone = DeadzoneDef.Default(),
        };

        [Test]
        public void The_Boundary_Still_Builds_Just_Slower()
        {
            var box = Box();
            float edge = box.Intensity(new Vector3(10f, 0f, 0f));   // exactly on the face
            float middle = box.Intensity(new Vector3(0f, 0f, 0f));

            // NOT ZERO is the assertion that matters. A falloff reaching 0 would make the boundary a free
            // perch to loot from, which is the opposite of a warning to turn around.
            Assert.That(edge, Is.EqualTo(DeadzoneVolumeDef.EdgeFloor).Within(0.001f));
            Assert.That(edge, Is.GreaterThan(0f));
            Assert.That(middle, Is.EqualTo(1f).Within(0.001f));
            Assert.That(edge, Is.LessThan(middle * 0.5f), "the edge has to be meaningfully tamer to read as one");
        }

        [Test]
        public void Outside_The_Box_Is_Zero()
        {
            Assert.That(Box().Intensity(new Vector3(10.1f, 0f, 0f)), Is.EqualTo(0f));
        }

        [Test]
        public void Depth_Is_Measured_On_The_WORST_Axis()
        {
            // A metre from one face is shallow however far you are from the others -- otherwise the middle of
            // a long thin zone would read as "deep" while you stand against its side.
            var thin = new DeadzoneVolumeDef
            {
                Center = new Vector3(0f, 0f, 0f),
                HalfExtent = new Vector3(2f, 100f, 100f),
                Zone = DeadzoneDef.Default(),
            };
            float againstTheSide = thin.Intensity(new Vector3(1.99f, 0f, 0f));
            Assert.That(againstTheSide, Is.LessThan(0.3f),
                "hugging the near face is shallow no matter how central the other axes are");
        }

        /// <summary>Drive a player standing at one spot in a zone: poll the deadzone at the real 0.25 s
        /// interval, step the vitals every 50 Hz tick, for `seconds`.</summary>
        static PlayerVitalsSim Stand(DeadzoneVolumeDef volume, Vector3 pos, RadiationGear gear, float seconds)
        {
            var dz = new DeadzoneSim();
            var v = new PlayerVitalsSim();
            const float tick = 0.02f, poll = 0.25f;
            float acc = 0f;
            for (float t = 0f; t < seconds; t += tick)
            {
                acc += tick;
                if (acc >= poll)
                {
                    var r = dz.Step(volume.Zone, gear, acc, volume.Intensity(pos));
                    if (r.Radiation > 0f) v.AbsorbDose(r.Radiation, acc);
                    acc = 0f;
                }
                v.Step(false, false, false, false, tick, None);
            }
            return v;
        }

        [Test]
        public void The_Boundary_Actually_Accumulates_A_Dose()
        {
            // ⚠ THE TEST THAT WAS MISSING. The falloff tests above assert Intensity is non-zero at the edge,
            // and it always was -- but a flat washout running everywhere cancelled any accrual slower than
            // itself, so the boundary of every zone was completely inert while every one of those assertions
            // stayed green. "Non-zero intensity" is not "accumulates a dose"; only standing there and reading
            // the dose afterwards tells you which.
            var box = Box();
            var edge = Stand(box, new Vector3(9.9f, 0f, 0f), new RadiationGear(), 60f);

            Assert.That(edge.Radiation, Is.GreaterThan(0.05f),
                "the edge has to BUILD -- a boundary that never accrues is a safe perch, not a warning");
        }

        [Test]
        public void The_Boundary_Builds_Far_Slower_Than_The_Middle()
        {
            var box = Box();
            var edge = Stand(box, new Vector3(9.9f, 0f, 0f), new RadiationGear(), 60f);
            var middle = Stand(box, new Vector3(0f, 0f, 0f), new RadiationGear(), 60f);

            Assert.That(edge.Radiation, Is.LessThan(middle.Radiation * 0.25f), "tamer...");
            Assert.That(middle.MajorlyIrradiated, Is.True, "...and the middle is not tame at all");
            Assert.That(edge.MajorlyIrradiated, Is.False, "a minute at the boundary must not cost you your legs");
        }

        [Test]
        public void The_Dose_Does_Not_Wash_Out_While_You_Are_Still_Standing_In_It()
        {
            // Decay is what LEAVING does. If it ran while dosing it would silently subtract from every zone's
            // rate, which is both a tuning lie and the mechanism that made the edge inert.
            var v = new PlayerVitalsSim();
            v.AbsorbDose(0.2f, 0.25f);
            float afterDose = v.Radiation;
            Assert.That(v.AbsorbingDose, Is.True);

            // Two ticks, no further dose but well inside the hold window.
            v.Step(false, false, false, false, 0.02f, None);
            v.Step(false, false, false, false, 0.02f, None);
            Assert.That(v.Radiation, Is.EqualTo(afterDose).Within(1e-6f), "no washout while still in the ground");

            // ...and once the hold lapses, it washes out.
            for (float t = 0f; t < 2f; t += 0.02f) v.Step(false, false, false, false, 0.02f, None);
            Assert.That(v.Radiation, Is.LessThan(afterDose), "leaving has to start the washout");
            Assert.That(v.AbsorbingDose, Is.False);
        }

        [Test]
        public void The_Hold_Window_Clears_The_Real_Poll_Interval()
        {
            // Both DeadzoneField and ServerDeadzones poll at 0.25 s. A hold shorter than that would let an
            // ordinary gap between polls start a washout on a player who has not moved.
            Assert.That(PlayerVitalsSim.DoseHoldSeconds, Is.GreaterThan(0.25f * 2f),
                "the hold must survive a missed poll, not merely a punctual one");
        }

        [Test]
        public void A_Short_Trip_Leaves_Nothing_Permanent_But_A_Long_One_Scars()
        {
            // The shape strawberry asked for: brief exposure is survivable, standing in it is not, and what a
            // long dose leaves behind does NOT wash out with the dose.
            var box = Box();
            var brief = Stand(box, new Vector3(0f, 0f, 0f), new RadiationGear(), 20f);
            var lengthy = Stand(box, new Vector3(0f, 0f, 0f), new RadiationGear(), 45f);
            Idle(brief, 200f);
            Idle(lengthy, 200f);

            Assert.That(brief.Radiation, Is.EqualTo(0f), "the dose always washes out");
            Assert.That(lengthy.Radiation, Is.EqualTo(0f));
            Assert.That(brief.Infection, Is.EqualTo(0f).Within(0.001f), "a 20 s trip costs nothing lasting");
            Assert.That(lengthy.Infection, Is.GreaterThan(0.5f),
                "a 45 s trip leaves infection that is past the self-clear line and never comes off");
        }

        [Test]
        public void Intensity_Scales_The_Dose_The_Zone_Delivers()
        {
            var zone = DeadzoneDef.Default();
            var gear = new RadiationGear();

            var deep = new DeadzoneSim(); deep.Step(zone, gear, DeadzoneSim.EntryGrace);
            var shallow = new DeadzoneSim(); shallow.Step(zone, gear, DeadzoneSim.EntryGrace);

            float deepDose = deep.Step(zone, gear, 1f, 1f).Radiation;
            float edgeDose = shallow.Step(zone, gear, 1f, DeadzoneVolumeDef.EdgeFloor).Radiation;

            Assert.That(edgeDose, Is.EqualTo(deepDose * DeadzoneVolumeDef.EdgeFloor).Within(1e-5f));
            Assert.That(edgeDose, Is.GreaterThan(0f), "tamer, not free");
        }
    }
}
