using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 for the airdrop schedule and trajectory. Engine-free because both have to agree ACROSS
    // machines: the server decides when and where, and every client renders a crate that matches. A
    // trajectory that accumulated velocity per frame would drift between a 60fps client and a 50Hz
    // server, so descent is a closed-form function of elapsed time and this suite pins that.
    [TestFixture]
    public class AirdropSimTests
    {
        static readonly Vector3 Where = new Vector3(100f, 30f, -50f);
        static AirdropSim NewSim(double first = 10.0) => new AirdropSim(first) { IntervalSeconds = 100f };

        /// <summary>Advance until the plane releases the crate. A drop now begins with a FLIGHT, so a
        /// test about the fall has to get past the flight first -- and asserting the release happened
        /// is itself a check that the plane arrives at all.</summary>
        static void FlyToRelease(AirdropSim s, double dt = 0.02)
        {
            for (int i = 0; i < 200000 && s.Phase == AirdropPhase.Inbound; i++) s.Step(dt, () => Where);
            Assert.That(s.Phase, Is.Not.EqualTo(AirdropPhase.Inbound), "plane never released the crate");
        }

        static bool StepBy(AirdropSim s, double seconds, double dt = 0.02)
        {
            bool fired = false;
            for (double t = 0; t < seconds; t += dt)
                if (s.Step(dt, () => Where)) fired = true;
            return fired;
        }

        [Test]
        public void No_Drop_Before_The_First_Interval()
        {
            var s = NewSim(first: 10.0);
            Assert.That(StepBy(s, 5.0), Is.False);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.None));
        }

        [Test]
        public void A_Drop_Fires_Once_The_Interval_Elapses()
        {
            var s = NewSim(first: 10.0);
            Assert.That(StepBy(s, 11.0), Is.True, "the drop should have begun");
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Inbound), "a drop begins as a PLANE, not a crate");
            Assert.That(s.PlaneVelocity, Is.Not.EqualTo(UnityEngine.Vector3.zero), "the plane must be moving");
            FlyToRelease(s);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Falling));
            Assert.That(s.Target.x, Is.EqualTo(Where.x).Within(1.0f), "released over the target");
            Assert.That(s.Target.z, Is.EqualTo(Where.z).Within(1.0f));
        }

        [Test]
        public void A_Drop_Fires_Exactly_Once_Not_Every_Tick_After()
        {
            // The bug this guards: a "clock past due" test with no reschedule fires on every single
            // tick thereafter, which would spawn a crate 50 times a second.
            var s = NewSim(first: 10.0);
            int fires = 0;
            for (double t = 0; t < 30.0; t += 0.02)
                if (s.Step(0.02, () => Where)) fires++;
            Assert.That(fires, Is.EqualTo(1), "one drop, not one per tick");
        }

        [Test]
        public void A_Second_Drop_Is_Suppressed_While_One_Is_Still_Falling()
        {
            // Two crates in the sky at once reads as a bug. Suppressed rather than queued, so a paused
            // server cannot dump a dozen crates the moment it resumes.
            var s = NewSim(first: 1.0);
            s.IntervalSeconds = 2f;          // interval far shorter than the fall
            s.DropHeight = 220f; s.FallSpeed = 18f;   // ~12 s of descent

            int fires = 0;
            for (double t = 0; t < 8.0; t += 0.02)
                if (s.Step(0.02, () => Where)) fires++;

            Assert.That(fires, Is.EqualTo(1), "only one drop may be in the air");
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Inbound).Or.EqualTo(AirdropPhase.Falling),
                "suppression must cover the plane phase too, not just the fall");
        }

        [Test]
        public void The_Crate_Starts_High_And_Reaches_The_Target()
        {
            var s = NewSim(first: 0.5);
            StepBy(s, 1.0);
            FlyToRelease(s);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Falling), "test setup");

            // Sampled at the drop's OWN start instant, not at "now". The first version of this test
            // read CurrentPosition after the loop had already run half a second past the trigger, so
            // the crate had legitimately fallen ~9 m and the assertion failed on correct behaviour --
            // a bug in the test, not the sim.
            var start = s.PositionAt(s.StartedAt);
            Assert.That(start.y, Is.EqualTo(Where.y + s.DropHeight).Within(0.001f), "starts a drop-height up");
            Assert.That(start.x, Is.EqualTo(Where.x).Within(0.001f), "and directly above the target");
            Assert.That(start.z, Is.EqualTo(Where.z).Within(0.001f));

            StepBy(s, s.FallSeconds + 1.0);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Landed));
            Assert.That(s.CurrentPosition.y, Is.EqualTo(Where.y).Within(0.001f), "and settles exactly on it");
        }

        [Test]
        public void Descent_Is_Closed_Form_So_Two_Machines_Agree()
        {
            // The same drop sampled at the same clock must give the same height regardless of how the
            // caller got there -- one big step, or many small ones. This is what stops a 60fps client
            // and a 50Hz server disagreeing about where the crate is.
            var coarse = NewSim(first: 0.0);
            var fine = NewSim(first: 0.0);
            coarse.Step(0.1, () => Where);
            fine.Step(0.1, () => Where);
            FlyToRelease(coarse); FlyToRelease(fine);

            StepBy(coarse, 6.0, dt: 0.5);      // 12 big steps
            StepBy(fine, 6.0, dt: 0.01);       // 600 small ones

            Assert.That(coarse.CurrentPosition.y, Is.EqualTo(fine.CurrentPosition.y).Within(0.5f),
                "height must depend on elapsed TIME, not on how many steps were taken");
        }

        [Test]
        public void The_Crate_Never_Falls_Below_Its_Target()
        {
            var s = NewSim(first: 0.0);
            s.Step(0.1, () => Where);
            FlyToRelease(s);
            StepBy(s, s.FallSeconds * 3.0);   // long past landing
            Assert.That(s.CurrentPosition.y, Is.GreaterThanOrEqualTo(Where.y - 0.001f),
                "an unclamped descent would sink the crate through the world forever");
        }

        [Test]
        public void A_Landing_Is_Reported_Even_When_The_Next_Drop_Starts_On_The_Same_Tick()
        {
            // The bug this pins, found by the MP test rather than by reading the code: Step resolves a
            // landing BEFORE it considers the next drop, so with a short interval both happen on one
            // tick and the phase goes Falling -> Landed -> Falling within a single call. Any caller
            // comparing Phase before and after sees Falling -> Falling, loses the landing entirely, and
            // the crate never lands for a client. JustLanded exists so the transition cannot be missed.
            var s = new AirdropSim(0.0) { IntervalSeconds = 1f, DropHeight = 40f, FallSpeed = 40f };
            var where = new Vector3(5f, 0f, 5f);
            s.Step(0.02, () => where);                       // drop 1 begins (plane launches)
            for (int i = 0; i < 200000 && s.Phase == AirdropPhase.Inbound; i++) s.Step(0.02, () => where);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Falling), "test setup");

            bool sawLanding = false;
            var before = AirdropPhase.None;
            var after = AirdropPhase.None;
            for (double t = 0; t < 2.0; t += 0.02)
            {
                var pre = s.Phase;
                s.Step(0.02, () => where);
                if (s.JustLanded)
                {
                    sawLanding = true;
                    before = pre; after = s.Phase;
                    break;
                }
            }

            Assert.That(sawLanding, Is.True, "the landing must be reported");
            if (before == AirdropPhase.Falling && after == AirdropPhase.Falling)
                Assert.Pass("landing and the next launch shared a tick -- exactly the case a "
                          + "before/after phase comparison cannot see, and JustLanded still caught it");
        }

        [Test]
        public void The_Plane_Keeps_Flying_After_It_Drops_The_Crate()
        {
            // The renderer used to draw the plane only while Phase == Inbound, so it blinked out of
            // existence on the very tick it released -- watched by whoever had been tracking it to work
            // out where the drop was going. Retail keeps the model until it is clean off the far side.
            var s = NewSim(first: 0.0);
            s.Step(0.1, () => Where);
            Assert.That(s.PlaneVisible, Is.True, "test setup: the plane launched");
            FlyToRelease(s);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Falling), "test setup: the crate is away");
            Assert.That(s.PlaneVisible, Is.True, "the aircraft does not evaporate when it lets go");

            // Still flying a good while later, and still moving.
            var justAfter = s.PlanePositionAt(s.Clock);
            StepBy(s, 5.0);
            Assert.That(s.PlaneVisible, Is.True, "still overhead five seconds past the release");
            var later = s.PlanePositionAt(s.Clock);
            Assert.That((later - justAfter).magnitude, Is.GreaterThan(1f), "and it is still moving");
        }

        [Test]
        public void The_Plane_Survives_The_Whole_Inbound_Leg_And_Then_Leaves_Past_The_Edge()
        {
            var s = new AirdropSim(0.0) { IntervalSeconds = 100000f, MapHalfSize = 200f, ApproachRunway = 50f };
            s.Step(0.1, () => Where);
            Assert.That(s.PlaneVisible, Is.True, "test setup");

            float edge = s.MapHalfSize + s.ApproachRunway;
            var v = s.PlaneVelocity;
            while (s.Phase == AirdropPhase.Inbound)
            {
                s.Step(0.02, () => Where);
                Assert.That(s.PlaneVisible, Is.True, "it must not vanish on the way in");
            }

            int guard = 0;                                   // bounded so a broken exit test fails, not hangs
            while (s.PlaneVisible && guard++ < 200000) s.Step(0.02, () => Where);
            Assert.That(s.PlaneVisible, Is.False, "the plane must eventually leave");

            var at = s.PlanePositionAt(s.Clock);
            float alongX = at.x * (v.x < 0f ? -1f : 1f), alongZ = at.z * (v.z < 0f ? -1f : 1f);
            Assert.That(alongX > edge || alongZ > edge, Is.True,
                        $"it left at ({at.x:0}, {at.z:0}) heading ({v.x:0.00}, {v.z:0.00}) -- edge {edge:0}");
        }

        [Test]
        public void A_Plane_Adopted_From_Outside_The_Map_Is_Not_Deleted_On_Arrival()
        {
            // Why the exit test measures position ALONG the heading rather than as a distance.
            //
            // For a plane this sim launched the two forms agree -- LaunchPlaneToward never starts one
            // further out than half-the-map plus the runway, so the coordinate only exceeds the edge on
            // the way OUT. AdoptPlane is the case where they part company: a client joining mid-event is
            // handed the server's plane wherever it is, and on a big map retail's own comment notes the
            // aircraft starts 2 km outside the coordinate range. A symmetric |x| > edge check deletes
            // that plane the instant the client adopts it -- it is far away, but on the wrong side --
            // and the joiner sees a crate materialise out of an empty sky.
            var s = new AirdropSim(100000.0) { MapHalfSize = 200f, ApproachRunway = 50f };
            float edge = s.MapHalfSize + s.ApproachRunway;
            s.AdoptPlane(new Vector3(-(edge + 400f), 300f, 0f), new Vector3(80f, 0f, 0f),
                         launchedAt: 0.0, releaseAt: 20.0, groundY: 0f);

            Assert.That(s.PlaneVisible, Is.True, "adopted while still outside the map, heading in");
            s.Step(0.02, () => Where);
            Assert.That(s.PlaneVisible, Is.True,
                        $"still inbound at x={s.PlanePositionAt(s.Clock).x:0} (edge {edge:0}) -- it has not crossed");
        }

        [Test]
        public void ForceDrop_Works_But_Refuses_While_One_Is_Airborne()
        {
            var s = NewSim(first: 1000.0);    // nothing scheduled soon
            Assert.That(s.ForceDrop(Where), Is.True);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Falling), "ForceDrop skips the flight on purpose");
            Assert.That(s.ForceDrop(Where), Is.False, "no second crate while one is still coming down");
        }

        [Test]
        public void Clearing_A_Collected_Drop_Lets_The_Cycle_Resume()
        {
            var s = NewSim(first: 0.0);
            s.Step(0.1, () => Where);
            FlyToRelease(s);
            StepBy(s, s.FallSeconds + 1.0);
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.Landed));
            s.Clear();
            Assert.That(s.Phase, Is.EqualTo(AirdropPhase.None));
            Assert.That(s.ForceDrop(Where), Is.True, "a cleared drop must not block the next one");
        }
    }
}
