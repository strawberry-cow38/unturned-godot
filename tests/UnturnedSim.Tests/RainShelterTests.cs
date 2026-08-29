using System.Collections.Generic;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the shelter query. strawberry_cow: "make floors/roofs occlude rain ... just add the necessary
    // framework." Nothing here spawns rain; these pin down what counts as cover.
    [TestFixture]
    public class RainShelterTests
    {
        /// <summary>A flat slab: pitch -90 lays Height out along -Z, so this covers x in [X, X+Length] and
        /// z in [Z-Height, Z].</summary>
        static WallPlan Slab(float x, float y, float z, float len = 12f, float depth = 12f)
            => new WallPlan { X = x, Y = y, Z = z, Yaw = 0f, Pitch = -90f,
                              Length = len, Height = depth, Kind = SurfaceKind.Floor };

        static WallPlan Wall(float x, float y, float z, float len = 12f, float h = 4.25f)
            => new WallPlan { X = x, Y = y, Z = z, Yaw = 0f, Pitch = 0f,
                              Length = len, Height = h, Kind = SurfaceKind.Wall };

        [Test]
        public void AFlatSlabShelltersWhatIsUnderIt()
        {
            var slab = Slab(0f, 10f, 0f);
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 6f, 0f, -6f), Is.True, "under the middle");
            Assert.That(RainShelter.CoverAbove(new[] { slab }, 6f, 0f, -6f, out float y), Is.True);
            Assert.That(y, Is.EqualTo(10f).Within(1e-3f), "the cover is reported at the slab's height");
        }

        [Test]
        public void OutsideTheFootprintIsNotSheltered()
        {
            var slab = Slab(0f, 10f, 0f);
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 20f, 0f, -6f), Is.False, "past the +X edge");
            Assert.That(RainShelter.IsSheltered(new[] { slab }, -5f, 0f, -6f), Is.False, "past the -X edge");
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 6f, 0f, 5f), Is.False, "past the +Z edge");
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 6f, 0f, -20f), Is.False, "past the -Z edge");
        }

        [Test]
        public void AWallShelltersNothing()
        {
            // THE TEST THAT KEEPS THE REST HONEST. If a vertical surface counted, every point inside any
            // building would report sheltered for the wrong reason and every other test here would still
            // pass. A wall's horizontal footprint is a line -- rain falls past it.
            var wall = Wall(0f, 0f, 0f);
            Assert.That(RainShelter.Occludes(wall), Is.False);
            Assert.That(RainShelter.IsSheltered(new[] { wall }, 6f, 0f, 0f), Is.False);
            Assert.That(RainShelter.IsSheltered(new[] { wall }, 6f, -1f, 0f), Is.False, "nor from below it");
        }

        [Test]
        public void CoverHasToBeAboveYou()
        {
            // The floor you are STANDING ON does not keep the rain off. Counting it would report every
            // outdoor spot on a slab as sheltered -- which is every spot a player can stand.
            var slab = Slab(0f, 10f, 0f);
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 6f, 20f, -6f), Is.False, "standing above it");
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 6f, 10f, -6f), Is.False, "standing on it");
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 6f, 9.9f, -6f), Is.True, "just under it");
        }

        [Test]
        public void TheLowestCoverWins()
        {
            // Which ceiling you are under, not merely whether there is one -- the moment anything wants to
            // know where to STOP a drop, the nearest one is the answer.
            var plans = new[] { Slab(0f, 10f, 0f), Slab(0f, 20f, 0f), Slab(0f, 5f, 0f) };
            Assert.That(RainShelter.CoverAbove(plans, 6f, 0f, -6f, out float y), Is.True);
            Assert.That(y, Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void AStairwellLetsRainThrough()
        {
            // An opening is a hole. This is why the query cannot be an AABB test.
            var slab = Slab(0f, 10f, 0f);
            slab.Openings.Add(new WallOpening(4f, 4f, 4f, 4f));      // u 4..8, v 4..8
            var plans = new[] { slab };

            // v runs along -Z, so v in [4,8] is z in [-8,-4].
            Assert.That(RainShelter.IsSheltered(plans, 6f, 0f, -6f), Is.False, "under the stairwell");
            Assert.That(RainShelter.IsSheltered(plans, 1f, 0f, -1f), Is.True, "beside it, still covered");
        }

        [Test]
        public void IntactGlassKeepsRainOutButBrokenGlassDoesNot()
        {
            // A skylight is a window: glazed it sheltters, smashed it does not. Uses the SAME HasGlass the
            // renderer and the shard preset use, so a window that looks broken behaves broken.
            var slab = Slab(0f, 10f, 0f);
            slab.Openings.Add(new WallOpening(4f, 4f, 4f, 4f) { Glazed = true });
            Assert.That(RainShelter.IsSheltered(new[] { slab }, 6f, 0f, -6f), Is.True, "glazed skylight");

            var broken = Slab(0f, 10f, 0f);
            broken.Openings.Add(new WallOpening(4f, 4f, 4f, 4f) { Glazed = true, GlassBroken = true });
            Assert.That(RainShelter.IsSheltered(new[] { broken }, 6f, 0f, -6f), Is.False, "smashed skylight");
        }

        [Test]
        public void AShutDoorKeepsRainOutAnOpenOneDoesNot()
        {
            var shut = Slab(0f, 10f, 0f);
            shut.Openings.Add(new WallOpening(4f, 4f, 4f, 4f) { DoorProp = "hatch", DoorOpen = false });
            Assert.That(RainShelter.IsSheltered(new[] { shut }, 6f, 0f, -6f), Is.True);

            var ajar = Slab(0f, 10f, 0f);
            ajar.Openings.Add(new WallOpening(4f, 4f, 4f, 4f) { DoorProp = "hatch", DoorOpen = true });
            Assert.That(RainShelter.IsSheltered(new[] { ajar }, 6f, 0f, -6f), Is.False);
        }

        [Test]
        public void APitchedRoofStillShelters()
        {
            // A roof is not flat, and a query that only understood flat slabs would shelter nothing under
            // any real roof.
            var roof = new WallPlan { X = 0f, Y = 10f, Z = 0f, Yaw = 0f, Pitch = -45f,
                                      Length = 12f, Height = 12f, Kind = SurfaceKind.Roof };
            Assert.That(RainShelter.Occludes(roof), Is.True);
            // v runs down-slope; at v=4 the plane has risen 4*sin(45) and run 4*cos(45) along -Z.
            float run = 4f * 0.70710678f;
            Assert.That(RainShelter.CoverAbove(new[] { roof }, 6f, 0f, -run, out float y), Is.True);
            Assert.That(y, Is.EqualTo(10f + run).Within(0.05f), "the cover rises with the slope");
        }

        [Test]
        public void YawIsRespected()
        {
            // A slab turned 90 degrees covers a different patch of ground. Without the yaw term the whole
            // query is right only for buildings that happen to face north.
            var turned = new WallPlan { X = 0f, Y = 10f, Z = 0f, Yaw = 90f, Pitch = -90f,
                                        Length = 12f, Height = 4f, Kind = SurfaceKind.Floor };
            // yaw 90 sends local +X to -Z, so Length runs along -Z and Height (depth) along -X.
            Assert.That(RainShelter.IsSheltered(new[] { turned }, -2f, 0f, -6f), Is.True, "inside the turned slab");
            Assert.That(RainShelter.IsSheltered(new[] { turned }, 6f, 0f, -2f), Is.False, "where it would be unturned");
        }

        [Test]
        public void AHipEndSheltersItsTrapezoidNotItsBoundingBox()
        {
            // Hip ends and cross-wing valleys are genuine trapezoids. Sheltering out to the bounding
            // rectangle would leave a dry strip hanging in mid-air beside every hip roof.
            var hip = new WallPlan { X = 0f, Y = 10f, Z = 0f, Yaw = 0f, Pitch = -90f,
                                     Length = 12f, Height = 12f, Kind = SurfaceKind.Roof,
                                     InsetL0 = 0f, InsetL1 = 5f, InsetR0 = 0f, InsetR1 = 5f };
            var plans = new[] { hip };
            Assert.That(RainShelter.IsSheltered(plans, 6f, 0f, -1f), Is.True, "wide end");
            Assert.That(RainShelter.IsSheltered(plans, 1f, 0f, -11f), Is.False, "cut away at the narrow end");
            Assert.That(RainShelter.IsSheltered(plans, 6f, 0f, -11f), Is.True, "but the middle is still covered");
        }

        [Test]
        public void NothingAndNullAreNotShelter()
        {
            Assert.That(RainShelter.IsSheltered(null, 0f, 0f, 0f), Is.False);
            Assert.That(RainShelter.IsSheltered(new List<WallPlan>(), 0f, 0f, 0f), Is.False);
            Assert.That(RainShelter.Occludes(null), Is.False);
            Assert.That(RainShelter.CoverAbove(new WallPlan[] { null }, 0f, 0f, 0f, out _), Is.False);
        }
    }
}
