using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Hold-breath-to-steady. Written against the four things the owner actually specified -- it works, it
    // costs oxygen, it stops before harm, and it locks out while empty and for a bit after -- plus the
    // frame-rate cases that decide whether those hold at the boundary.
    [TestFixture]
    public class ScopeSteadySimTests
    {
        const float Tick = 1f / 50f;   // the sim tick this actually runs at

        static float RunHeld(ScopeSteadySim s, ref float ox, float seconds, bool wants = true)
        {
            float t = 0f;
            for (int i = 0; i < (int)(seconds / Tick); i++) { s.Step(wants, ref ox, Tick); t += Tick; }
            return t;
        }

        [Test]
        public void Holding_Steadies_And_Almost_Kills_The_Sway()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            Assert.That(s.Step(true, ref ox, Tick), Is.True);
            Assert.That(s.SwayScale, Is.LessThan(0.1f), "almost nothing");
            Assert.That(s.SwayScale, Is.GreaterThan(0f), "but not frozen -- a dead-still optic reads as a paused game");
        }

        [Test]
        public void Not_Holding_Costs_Nothing_And_Leaves_Sway_Alone()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            Assert.That(s.Step(false, ref ox, Tick), Is.False);
            Assert.That(ox, Is.EqualTo(1f), "no drain when not asking");
            Assert.That(s.SwayScale, Is.EqualTo(1f));
        }

        [Test]
        public void It_Drains_Oxygen_At_The_Advertised_Budget()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 1f);
            float spent = 1f - ox;
            Assert.That(spent, Is.EqualTo(ScopeSteadySim.DrainPerSecond).Within(0.005f),
                        "one second of steady costs one second of the budget");
        }

        // THE GUARANTEE. Hold it forever and the bar must stop at the reserve, never reach zero.
        [Test]
        public void It_Stops_At_The_Floor_And_Never_Goes_Below()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            for (int i = 0; i < 5000; i++)
            {
                s.Step(true, ref ox, Tick);
                Assert.That(ox, Is.GreaterThanOrEqualTo(ScopeSteadySim.SteadyFloor - 1e-5f),
                            $"went below the reserve on tick {i}");
            }
            Assert.That(ox, Is.EqualTo(ScopeSteadySim.SteadyFloor).Within(1e-4f), "and settles exactly on it");
        }

        // The floor has to hold on a BAD frame too -- a 2-second hitch must not spend straight through it.
        [Test]
        public void A_Long_Frame_Cannot_Spend_Past_The_Floor()
        {
            var s = new ScopeSteadySim();
            float ox = 0.30f;                       // just above the floor
            s.Step(true, ref ox, 2.0f);             // a hitch worth ~13 seconds of budget
            Assert.That(ox, Is.EqualTo(ScopeSteadySim.SteadyFloor).Within(1e-4f));
            Assert.That(s.Steadying, Is.False, "and it cut out rather than continuing for free");
        }

        [Test]
        public void Cutting_Out_Starts_The_Lockout()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 20f);                // long past the floor
            Assert.That(s.Steadying, Is.False);
            Assert.That(s.Lockout, Is.GreaterThan(0f), "'and for a bit after'");
        }

        // "prevents steadying while empty" -- refilling alone must not re-arm it.
        [Test]
        public void Air_Alone_Does_Not_Re_Arm_It_While_The_Lockout_Runs()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 20f);
            ox = 1f;                                 // surfaced, bar instantly full -- but still holding
            Assert.That(s.Step(true, ref ox, Tick), Is.False, "the timer has not started, let alone expired");
            Assert.That(ox, Is.EqualTo(1f), "and a refused steady costs nothing");
        }

        // The flaw the first cut had: the lockout decayed while the control was still held, so holding
        // through the empty period burned it off and a patient player was penalised LESS than one who let
        // go immediately. Both must wait the same time from release.
        [Test]
        public void Holding_Through_The_Empty_Period_Earns_No_Discount()
        {
            var a = new ScopeSteadySim(); float oxA = 1f;
            RunHeld(a, ref oxA, 20f);                       // bottoms out, then KEEPS holding
            var b = new ScopeSteadySim(); float oxB = 1f;
            RunHeld(b, ref oxB, 7f);                        // bottoms out and stops asking
            RunHeld(b, ref oxB, 13f, wants: false);

            Assert.That(a.Lockout, Is.GreaterThan(0f), "still held: the timer has not started");
            Assert.That(b.Lockout, Is.EqualTo(0f), "released: served in full");
        }

        // ...and the timer alone does not re-arm it either, while there is still no air.
        [Test]
        public void The_Timer_Alone_Does_Not_Re_Arm_It_While_Still_Empty()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 20f);
            float parked = ox;                       // sitting at the floor, underwater, not refilling
            RunHeld(s, ref parked, ScopeSteadySim.LockoutSeconds + 1f, wants: false);   // release: serve the timer
            Assert.That(s.Lockout, Is.EqualTo(0f), "timer expired");
            Assert.That(s.Step(true, ref parked, Tick), Is.False, "but there is still no air to spend");
        }

        [Test]
        public void Both_Conditions_Met_Re_Arms_It()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 20f);
            ox = ScopeSteadySim.SteadyRearm + 0.01f;
            RunHeld(s, ref ox, ScopeSteadySim.LockoutSeconds + 0.2f, wants: false);
            Assert.That(s.Step(true, ref ox, Tick), Is.True, "air back AND the wait served");
        }

        // THE HYSTERESIS TEST, and the reason SteadyRearm exists at all. Sitting a hair above the floor with
        // the lockout expired must not produce a scope that strobes on and off at frame rate.
        [Test]
        public void It_Does_Not_Strobe_Just_Above_The_Floor()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 20f);                                     // bottom out
            RunHeld(s, ref ox, ScopeSteadySim.LockoutSeconds + 0.2f, wants: false);   // serve the timer
            ox = ScopeSteadySim.SteadyFloor + 0.005f;                    // a hair of air, well below re-arm

            int flips = 0; bool last = s.Steadying;
            for (int i = 0; i < 200; i++)
            {
                s.Step(true, ref ox, Tick);
                if (s.Steadying != last) flips++;
                last = s.Steadying;
            }
            Assert.That(flips, Is.EqualTo(0), "a single threshold would flicker here every frame");
            Assert.That(s.Steadying, Is.False, "and it stays off until the bar is genuinely back");
        }

        [Test]
        public void Reset_Clears_A_Lockout_So_It_Cannot_Outlive_The_Life_That_Earned_It()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 20f);
            Assert.That(s.Lockout, Is.GreaterThan(0f));
            s.Reset();
            Assert.That(s.Lockout, Is.EqualTo(0f));
            ox = 1f;
            Assert.That(s.Step(true, ref ox, Tick), Is.True, "a fresh life steadies immediately");
        }

        [Test]
        public void The_Reserve_Sits_Above_Any_Future_Drowning_Threshold()
        {
            // The floor is the "never takes damage" guarantee. There is no drowning damage today, so this
            // asserts the INVARIANT rather than a behaviour: the reserve is a real, non-trivial amount of
            // air, and the re-arm point is strictly above it. If drowning damage is added at or above the
            // floor, this test is the one that should be made to fail.
            Assert.That(ScopeSteadySim.SteadyFloor, Is.GreaterThan(0.1f), "a token reserve is not a reserve");
            Assert.That(ScopeSteadySim.SteadyRearm, Is.GreaterThan(ScopeSteadySim.SteadyFloor),
                        "re-arm must be strictly above the floor or the hysteresis is nil");
        }
    }
}
