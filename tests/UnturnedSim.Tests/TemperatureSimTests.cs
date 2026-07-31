using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 for the temperature field. Engine-free because burning DAMAGES you: a client and a dedicated
    // server have to resolve the same point to the same answer, or one of them kills a player the other
    // thinks is fine.
    [TestFixture]
    public class TemperatureSimTests
    {
        static readonly Vector3 Origin = new Vector3(0f, 0f, 0f);

        [Test]
        public void A_Point_Outside_Every_Bubble_Is_Untouched()
        {
            var t = new TemperatureSim();
            t.Register(new Vector3(10f, 0f, 0f), 3f, PlayerTemperature.Warm);
            Assert.That(t.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.None));
        }

        [Test]
        public void The_Radius_Is_Exclusive_At_Its_Edge()
        {
            // Retail compares sqrMagnitude < sqrRadius. Standing EXACTLY on the boundary is outside.
            var t = new TemperatureSim();
            t.Register(Origin, 5f, PlayerTemperature.Warm);
            Assert.That(t.Resolve(new Vector3(4.99f, 0f, 0f), false), Is.EqualTo(PlayerTemperature.Warm));
            Assert.That(t.Resolve(new Vector3(5f, 0f, 0f), false), Is.EqualTo(PlayerTemperature.None));
        }

        [Test]
        public void Acid_Outranks_Everything_And_Stops_The_Search()
        {
            var t = new TemperatureSim();
            t.Register(Origin, 5f, PlayerTemperature.Acid);
            t.Register(Origin, 5f, PlayerTemperature.Burning);
            Assert.That(t.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.Acid));

            // ...and in the other order, because a max() over the enum would give BURNING here.
            var u = new TemperatureSim();
            u.Register(Origin, 5f, PlayerTemperature.Burning);
            u.Register(Origin, 5f, PlayerTemperature.Acid);
            Assert.That(u.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.Acid));
        }

        [Test]
        public void Burning_Survives_A_Later_Warm_Bubble_But_Not_The_Other_Way_Round()
        {
            // The asymmetry IS the rule: burning is sticky once seen, everything else is last-wins.
            var a = new TemperatureSim();
            a.Register(Origin, 5f, PlayerTemperature.Burning);
            a.Register(Origin, 5f, PlayerTemperature.Warm);
            Assert.That(a.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.Burning),
                        "a warm bubble registered later must not cool a fire");

            var b = new TemperatureSim();
            b.Register(Origin, 5f, PlayerTemperature.Warm);
            b.Register(Origin, 5f, PlayerTemperature.Burning);
            Assert.That(b.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.Burning));
        }

        [Test]
        public void Non_Burning_Bubbles_Are_Last_Wins_So_Order_Matters()
        {
            // Documented, not admired. Anyone tidying Resolve into a max() over the enum breaks this,
            // and it would show up as "standing between a cold spot and a warm one feels wrong".
            var t = new TemperatureSim();
            t.Register(Origin, 5f, PlayerTemperature.Warm);
            t.Register(Origin, 5f, PlayerTemperature.Cold);
            Assert.That(t.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.Cold));
        }

        [Test]
        public void Fireproof_Skips_The_Fire_Rather_Than_Merely_Surviving_It()
        {
            // Retail checks the suit BEFORE the radius, so the burning bubble is not just harmless --
            // it is not there at all, and a warm bubble underneath it becomes the answer.
            var t = new TemperatureSim();
            t.Register(Origin, 5f, PlayerTemperature.Burning);
            t.Register(Origin, 5f, PlayerTemperature.Warm);
            Assert.That(t.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.Burning));
            Assert.That(t.Resolve(Origin, true), Is.EqualTo(PlayerTemperature.Warm),
                        "fireproof must let the warm bubble through, not report NONE");
        }

        [Test]
        public void Fireproof_Does_Not_Protect_From_Acid()
        {
            var t = new TemperatureSim();
            t.Register(Origin, 5f, PlayerTemperature.Acid);
            Assert.That(t.Resolve(Origin, true), Is.EqualTo(PlayerTemperature.Acid));
        }

        [Test]
        public void A_Bubble_Can_Move_And_Be_Taken_Away()
        {
            var t = new TemperatureSim();
            int id = t.Register(Origin, 4f, PlayerTemperature.Warm);
            Assert.That(t.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.Warm));

            t.Move(id, new Vector3(50f, 0f, 0f));       // the fire drove away
            Assert.That(t.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.None));

            t.Move(id, Origin);
            Assert.That(t.Deregister(id), Is.True);      // it went out
            Assert.That(t.Resolve(Origin, false), Is.EqualTo(PlayerTemperature.None));
            Assert.That(t.Count, Is.Zero);
        }

        // ---- the per-player side ----

        [Test]
        public void Standing_In_Fire_Burns_On_A_Fixed_Cadence_Not_Every_Step()
        {
            // The bug this pins is the obvious implementation: damage whenever the state is BURNING.
            // At 60 fps that is 600 damage a second instead of 12.5.
            var p = new PlayerTemperatureSim();
            float total = 0f;
            for (int i = 0; i < 600; i++)          // 10 s at 60 fps
            {
                p.Step(1f / 60f, PlayerTemperature.Burning);
                total += p.Damage;
            }
            Assert.That(p.Temperature, Is.EqualTo(PlayerTemperature.Burning));
            // 10 s / 0.8 s = 12 or 13 ticks of 10 damage, depending where the step boundary lands.
            Assert.That(total, Is.InRange(120f, 130f), $"took {total} over 10 s of standing in a fire");
        }

        [Test]
        public void Stepping_Out_Of_The_Fire_Does_Not_Bank_Progress_Toward_The_Next_Tick()
        {
            // Dipping in and out for less than a tick each time must cost nothing. An accumulator that
            // is not reset lets a player be hurt by a fire they were never in for a full interval.
            var p = new PlayerTemperatureSim();
            float total = 0f;
            for (int i = 0; i < 200; i++)
            {
                p.Step(0.2f, PlayerTemperature.Burning);   // 0.2 s in, then straight out
                total += p.Damage;
                p.Step(0.2f, PlayerTemperature.None);
                total += p.Damage;
            }
            Assert.That(total, Is.Zero, "40 s of dipping in and out never completed a burn interval");
        }

        [Test]
        public void Carried_Warmth_Reads_As_Warm_Until_It_Runs_Out()
        {
            var p = new PlayerTemperatureSim();
            p.AddWarmth(3f);
            p.Step(1f, PlayerTemperature.Cold);
            Assert.That(p.Temperature, Is.EqualTo(PlayerTemperature.Warm), "warmth beats a cold ambient");
            p.Step(1f, PlayerTemperature.Cold);
            p.Step(1f, PlayerTemperature.Cold);
            p.Step(0.1f, PlayerTemperature.Cold);
            Assert.That(p.Temperature, Is.EqualTo(PlayerTemperature.Cold), "and stops once it is spent");
        }

        [Test]
        public void Warmth_Does_Not_Save_You_From_Fire_Or_Acid()
        {
            var p = new PlayerTemperatureSim();
            p.AddWarmth(60f);
            p.Step(0.1f, PlayerTemperature.Burning);
            Assert.That(p.Temperature, Is.EqualTo(PlayerTemperature.Burning));
            p.Step(0.1f, PlayerTemperature.Acid);
            Assert.That(p.Temperature, Is.EqualTo(PlayerTemperature.Acid));
        }

        [Test]
        public void A_Change_Is_Reported_Once()
        {
            var p = new PlayerTemperatureSim();
            p.Step(0.1f, PlayerTemperature.Warm);
            Assert.That(p.JustChanged, Is.True);
            p.Step(0.1f, PlayerTemperature.Warm);
            Assert.That(p.JustChanged, Is.False, "a HUD must not be told every step that nothing happened");
        }
    }
}
