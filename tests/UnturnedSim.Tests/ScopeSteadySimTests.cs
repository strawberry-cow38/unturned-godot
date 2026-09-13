using System;
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

        /// <summary>Seconds until SwayScale has covered <paramref name="frac"/> of the distance from where it
        /// started to <paramref name="dest"/>. Measured in REAL UNITS on the public accessor, because that is
        /// the number the feel argument is actually about -- an assertion on the internal Blend would pass
        /// even if SwayScale stopped reading it.</summary>
        static float SecondsToCover(ScopeSteadySim s, ref float ox, bool wants, float dest, float frac,
                                    float capSeconds = 10f)
        {
            float from = s.SwayScale;
            float mark = from + (dest - from) * frac;
            bool falling = dest < from;
            float t = 0f;
            while (t < capSeconds)
            {
                s.Step(wants, ref ox, Tick);
                t += Tick;
                if (falling ? s.SwayScale <= mark : s.SwayScale >= mark) return t;
            }
            return float.NaN;   // never got there -- the caller's Is.EqualTo will say so loudly
        }

        [Test]
        public void Holding_Steadies_And_Almost_Kills_The_Sway()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            Assert.That(s.Step(true, ref ox, Tick), Is.True);
            RunHeld(s, ref ox, 3f);                      // let the envelope finish; see the transition tests
            // Bounded on BOTH sides, and the upper bound is the point. "Almost nothing" needs a large
            // reduction; "not frozen" needs a residual a player can actually see. Measured at 4x, 0.06 was
            // 1.3 px of wander -- indistinguishable from zero, so the lower bound was the only one doing
            // work and it passed a value that failed the intent.
            Assert.That(s.SwayScale, Is.LessThan(0.30f), "almost nothing: a large reduction");
            Assert.That(s.SwayScale, Is.GreaterThan(0.10f),
                        "but visibly moving -- below ~0.10 the residual is 1-2 px at 4x, which is frozen");
        }

        // ---- the transition ------------------------------------------------------------------------------
        // Every one of these is bounded on BOTH sides on purpose. The last constant on this feature shipped
        // wrong behind a one-sided `Is.LessThan(0.1f)` -- on a quantity whose failure mode was "too small",
        // so the bound faced away from the failure. A rate has the same shape: "at least this slow" passes a
        // transition that never arrives, and "at most this slow" passes a snap.

        [Test]
        public void It_Does_Not_Snap_On_The_First_Tick()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            s.Step(true, ref ox, Tick);
            Assert.That(s.Steadying, Is.True, "the STATE is immediate");
            Assert.That(s.SwayScale, Is.GreaterThan(0.9f),
                        "...but the SWAY is not -- one tick of a 0.9s settle is barely any of it");
        }

        [Test]
        public void The_Engage_Takes_The_Designed_Time()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            float t = SecondsToCover(s, ref ox, true, ScopeSteadySim.SteadySwayScale, 0.9f);
            Assert.That(t, Is.EqualTo(ScopeSteadySim.EngageSeconds).Within(0.05f),
                        "90% of the settle should land on EngageSeconds -- that is what the constant means");
        }

        [Test]
        public void The_Release_Takes_The_Designed_Time()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            RunHeld(s, ref ox, 3f);                                  // fully steadied
            float t = SecondsToCover(s, ref ox, false, 1f, 0.9f);
            Assert.That(t, Is.EqualTo(ScopeSteadySim.ReleaseSeconds).Within(0.05f));
        }

        // THE DESIGN CLAIM, as a test rather than as a comment. Symmetric was the inherited behaviour and it
        // is the thing being replaced, so a regression back to it must fail here.
        [Test]
        public void Losing_The_Breath_Is_Much_Faster_Than_Taking_It()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            float engage = SecondsToCover(s, ref ox, true, ScopeSteadySim.SteadySwayScale, 0.9f);
            RunHeld(s, ref ox, 1f);
            float release = SecondsToCover(s, ref ox, false, 1f, 0.9f);

            float ratio = engage / release;
            Assert.That(ratio, Is.GreaterThan(3f),
                        $"asymmetric by design: {engage:F2}s in vs {release:F2}s out is barely a difference");
            Assert.That(ratio, Is.LessThan(8f),
                        "but the release is still a transition -- past ~8x it is a snap with extra steps");
        }

        // The other half of "designed": it must not be the HOUSE rate. Viewmodel's general position smoothing
        // is Lerp(target, delta*4) == tau 0.25s == 0.55s to 90%, and while the envelope was inherited from it
        // the breath-hold settled at exactly the speed every other optic disturbance settles. Copying that
        // number back in is the specific regression this guards.
        [Test]
        public void The_Engage_Is_Distinct_From_The_House_Smoothing()
        {
            const float HouseNinety = 0.55f;   // measured, and independently confirmed by cow tools
            Assert.That(ScopeSteadySim.EngageSeconds, Is.GreaterThan(HouseNinety * 1.4f),
                        "a deliberate breath must read as slower than ordinary settling, not the same");
            Assert.That(ScopeSteadySim.EngageSeconds, Is.LessThan(2.0f),
                        "and not so slow the scope is still settling after the shot has been taken");
        }

        // `1 - exp(-dt/tau)` instead of `dt * k`. The linear form the house smoothing uses moves a FRACTION
        // per frame, so a 30 fps player gets a different transition from a 144 fps one -- and at long frames
        // it can overshoot outright. This is the test that fails if anyone "simplifies" it back.
        [Test]
        public void The_Transition_Takes_The_Same_Wall_Time_At_Any_Frame_Rate()
        {
            // ROUND, not truncate. `(int)(0.5f / (1f/30f))` is 14, because 1f/30f is a hair ABOVE 1/30 in
            // float -- so the 30 fps arm silently simulated 0.467s against the others' 0.500s and reported a
            // 0.02 frame-rate dependence that was entirely mine. The first version of this test failed for
            // that reason and the sim was correct throughout.
            float At(float dt)
            {
                var s = new ScopeSteadySim();
                float ox = 1f;
                int ticks = (int)MathF.Round(0.5f / dt);
                for (int i = 0; i < ticks; i++) s.Step(true, ref ox, dt);
                return s.SwayScale;
            }
            float fast = At(1f / 144f), slow = At(1f / 30f), mid = At(1f / 50f);
            Assert.That(slow, Is.EqualTo(fast).Within(0.01f), "30 fps and 144 fps must reach the same place");
            Assert.That(mid, Is.EqualTo(fast).Within(0.01f));
        }

        [Test]
        public void A_Cut_Out_At_The_Floor_Eases_Back_Rather_Than_Snapping()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            // Step to the cut-out and stop THERE. The first cut of this ran a further full second past it and
            // then asserted the sway had not returned yet -- which of course it had, five release-times ago.
            int guard = 0;
            while (s.Step(true, ref ox, Tick) && ++guard < 5000) { }
            Assert.That(s.Steadying, Is.False, "it cut out at the floor");
            // The cut-out tick eases too, so this is ONE release tick past steadied (0.18 -> ~0.32), not the
            // steadied value itself. Bounding it at 0.18 would be asserting that the release had not started,
            // which is a different claim and a wrong one.
            Assert.That(s.SwayScale, Is.LessThan(0.40f), "the sway is still down at the instant it cuts out");

            RunHeld(s, ref ox, ScopeSteadySim.ReleaseSeconds * 0.5f);
            Assert.That(s.SwayScale, Is.InRange(0.30f, 0.95f),
                        "and comes back through the middle -- running out is a release, not a snap");
            RunHeld(s, ref ox, 1f);
            Assert.That(s.SwayScale, Is.EqualTo(1f).Within(0.01f), "...and it arrives");
        }

        [Test]
        public void The_Envelope_Stays_In_Range_And_Reset_Clears_It()
        {
            var s = new ScopeSteadySim();
            float ox = 1f;
            for (int i = 0; i < 400; i++)
            {
                s.Step(i % 7 < 3, ref ox, Tick);     // chattering the control
                Assert.That(s.Blend, Is.InRange(0f, 1f), $"tick {i}");
                Assert.That(s.SwayScale, Is.InRange(ScopeSteadySim.SteadySwayScale, 1f), $"tick {i}");
            }
            s.Reset();
            Assert.That(s.Blend, Is.EqualTo(0f));
            Assert.That(s.SwayScale, Is.EqualTo(1f), "a fresh life starts un-steadied, mid-transition or not");
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
