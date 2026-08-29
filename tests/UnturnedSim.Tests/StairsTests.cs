using System;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the stair derivation.
    //
    // The whole point of deriving a flight instead of picking a step count is that it lands EXACTLY on the
    // floor above at any storey height -- so that is what these assert, swept across heights rather than
    // checked at the one value the kit happens to use today. A staircase that ends half a step short is a
    // staircase you cannot walk up, and it would only appear when someone rescales the kit.
    //
    // Every test names the mutation that should break it. A test nobody has watched fail is a guess.
    [TestFixture]
    public class StairsTests
    {
        // The kit's own storey, plus heights nobody tuned against -- including a deliberately awkward one.
        static readonly float[] Heights = { 2.0f, 3.0f, WallOpenings.DoorHeight, 4.75f, 6.0f, 7.3f, 12.0f };

        // BREAK IT: replace `rise / steps` with a fixed step rise (say StairRiseTarget) -> the flight
        // overshoots or undershoots on every height that is not an exact multiple of it, which is most.
        [Test]
        public void AFlightLandsExactlyOnTheFloorAbove()
        {
            foreach (float h in Heights)
            {
                int steps = WallOpenings.StairSteps(h);
                float stepRise = WallOpenings.StairStepRise(h);   // the PRODUCTION derivation, not ours
                float top = steps * stepRise;
                Assert.That(top, Is.EqualTo(h).Within(1e-4f),
                    $"storey {h}: {steps} steps of {stepRise} reached {top}, not {h}");
            }
        }

        // BREAK IT: drop the Math.Max(2, ...) -> a short storey derives 1 step, or 0, and "a flight" becomes
        // a kerb you cannot climb. 2.0 / 0.38 rounds to 5, so the guard needs a genuinely short storey to
        // bite; assert the guard itself rather than trusting the sample.
        [Test]
        public void AFlightIsNeverFewerThanTwoSteps()
        {
            foreach (float h in new[] { 0.01f, 0.2f, 0.5f, 0.76f })
                Assert.That(WallOpenings.StairSteps(h), Is.GreaterThanOrEqualTo(2), $"storey {h}");
        }

        // BREAK IT: return rise / StairPitchTangent unrounded -> a flight no longer lines up with the walls
        // it sits between, which is the whole reason wall runs snap in the first place.
        [Test]
        public void TheDefaultRunSnapsToTheLattice()
        {
            foreach (float h in Heights)
            {
                float run = WallOpenings.StairDefaultRun(h);
                float steps = run / WallOpenings.LatticeStep;
                Assert.That(steps, Is.EqualTo(MathF.Round(steps)).Within(1e-4f),
                    $"storey {h}: run {run} is not a whole number of {WallOpenings.LatticeStep} lattice steps");
                Assert.That(run, Is.GreaterThanOrEqualTo(WallOpenings.LatticeStep), $"storey {h}");
            }
        }

        // The scale-invariance claim itself, and the reason any of this is derived: a taller storey must give
        // a LONGER flight, not a steeper one. This is the property that a hardcoded run silently destroys.
        //
        // BREAK IT: make StairDefaultRun return a constant -> pitch climbs with every extra metre of storey
        // and the tall cases walk straight out of the band.
        [Test]
        public void PitchStaysDomesticAcrossStoreyHeights()
        {
            foreach (float h in Heights)
            {
                float run = WallOpenings.StairDefaultRun(h);
                float deg = MathF.Atan(h / run) * 180f / MathF.PI;
                // Real staircases live around 30-40 degrees. The lattice quantises the run, so short storeys
                // land shallower than the target rather than steeper -- the bound that matters is the upper
                // one, because that is the direction that becomes a ladder.
                Assert.That(deg, Is.LessThan(45f), $"storey {h}: {deg:0.0} deg off a {run} run is a ladder");
                Assert.That(deg, Is.GreaterThan(15f), $"storey {h}: {deg:0.0} deg off a {run} run is a ramp");
            }
        }

        // The going has to stay walkable too -- a flight can land flush and still be unusable if each tread
        // is a sliver. BREAK IT: derive the run from a much steeper tangent -> going collapses.
        [Test]
        public void TreadGoingStaysWalkable()
        {
            foreach (float h in Heights)
            {
                int steps = WallOpenings.StairSteps(h);
                float going = WallOpenings.StairDefaultRun(h) / steps;
                float stepRise = WallOpenings.StairStepRise(h);
                Assert.That(going, Is.GreaterThan(stepRise * 0.9f),
                    $"storey {h}: going {going:0.000} against rise {stepRise:0.000} is climbing, not walking");
            }
        }
    }
}
