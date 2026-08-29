using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for roof shapes: the arithmetic that decides what surfaces a roof turns into.
    //
    // These assert SCALARS and relationships. Whether the planes physically meet along the ridge is checked
    // in-engine instead (buildtool.roof_planes_meet), because proving it here would mean rebuilding Godot's
    // yaw-and-pitch transform in the test -- and a test that reimplements the convention it is checking
    // agrees with the code when the convention is wrong, which is the one case that matters.
    [TestFixture]
    public class RoofShapesTests
    {
        static RoofSpec Spec(RoofKind kind, float w = 20f, float d = 12f, float pitch = 30f)
            => new RoofSpec
            {
                Kind = kind, MinX = 0f, MaxX = w, MinZ = 0f, MaxZ = d,
                TopY = 100f, PitchDeg = pitch, Thickness = 0.5f, Material = 0, Texel = -1,
            };

        [Test]
        public void AFlatRoofIsOneSlabLyingOnTheWallHeads()
        {
            var s = Spec(RoofKind.Flat);
            var p = RoofShapes.Planes(s);
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(p[0].PitchDeg, Is.EqualTo(-90f).Within(1e-3f), "-90 is lying flat");
            Assert.That(p[0].Length, Is.EqualTo(20f).Within(1e-3f));
            Assert.That(p[0].Height, Is.EqualTo(12f).Within(1e-3f));
            // Thickness is centred, so the walking surface lands on the head rather than half a slab above it.
            Assert.That(p[0].Y, Is.EqualTo(100.25f).Within(1e-3f));
            Assert.That(RoofShapes.RidgeHeight(s), Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void AGableIsTwoSlopesAndNothingElse()
        {
            var p = RoofShapes.Planes(Spec(RoofKind.Gable));
            Assert.That(p.Count, Is.EqualTo(2));
            foreach (var q in p)
            {
                Assert.That(q.Length, Is.EqualTo(20f).Within(1e-3f), "each slope runs the length of the ridge");
                Assert.That(q.InsetL1 + q.InsetR1, Is.EqualTo(0f).Within(1e-4f), "a gable slope is a rectangle");
            }
        }

        // A HIP IS FOUR PLANES THAT SHARE ONE SLOPE LENGTH. That equality is the whole thing: the ends rise
        // over the same run at the same pitch as the sides, which is what makes all four arrive at the ridge
        // together. Slope lengths that differ meet only at a point and leave two open wedges.
        //
        // BREAK IT: derive the end planes' height from the LONG span -> the ends overshoot the ridge.
        [Test]
        public void AHipIsFourPlanesSharingOneSlopeLength()
        {
            var s = Spec(RoofKind.Hip);
            var p = RoofShapes.Planes(s);
            Assert.That(p.Count, Is.EqualTo(4));
            foreach (var q in p)
                Assert.That(q.Height, Is.EqualTo(s.Slope).Within(1e-3f),
                    "every hip plane rises the same distance up its slope");
        }

        // The sides are TRAPEZOIDS shortened to the ridge, the ends are TRIANGLES closing to a point. Getting
        // the inset wrong on the sides leaves the ridge the full length of the roof, which is a gable wearing
        // two extra planes.
        [Test]
        public void AHipsSidesAreTrapezoidsAndItsEndsAreTriangles()
        {
            var s = Spec(RoofKind.Hip);            // 20 x 12, so the run is 6 and the ridge is 8
            var p = RoofShapes.Planes(s);

            var sides = p.FindAll(q => q.Length > 19f);
            var ends = p.FindAll(q => q.Length < 13f);
            Assert.That(sides.Count, Is.EqualTo(2), "two long sides");
            Assert.That(ends.Count, Is.EqualTo(2), "two short ends");

            foreach (var q in sides)
            {
                Assert.That(q.InsetL1, Is.EqualTo(6f).Within(1e-3f), "inset by one run at each end");
                Assert.That(q.InsetR1, Is.EqualTo(6f).Within(1e-3f));
                Assert.That(q.Length - q.InsetL1 - q.InsetR1, Is.EqualTo(8f).Within(1e-3f),
                    "the top edge IS the ridge");
                Assert.That(q.InsetL0 + q.InsetR0, Is.EqualTo(0f).Within(1e-4f), "the eave is full width");
            }
            foreach (var q in ends)
            {
                Assert.That(q.Length - q.InsetL1 - q.InsetR1, Is.EqualTo(0f).Within(1e-3f),
                    "an end closes to a point");
                Assert.That(q.InsetL1, Is.EqualTo(q.InsetR1).Within(1e-4f), "and the point is centred");
            }
        }

        // A square hip is a PYRAMID, and that falls out rather than being special-cased. If it needed a guard
        // the formula would be wrong somewhere else too.
        [Test]
        public void ASquareHipIsAPyramid()
        {
            var s = Spec(RoofKind.Hip, 12f, 12f);
            Assert.That(RoofShapes.RidgeLength(s), Is.EqualTo(0f).Within(1e-3f));
            foreach (var q in RoofShapes.Planes(s))
                Assert.That(q.Length - q.InsetL1 - q.InsetR1, Is.EqualTo(0f).Within(1e-3f),
                    "every face of a pyramid closes to the apex");
        }

        // The ridge runs the LONG way, whichever way the building is drawn. A roof whose ridge runs across
        // the short span is instantly wrong-looking and is what you get from assuming X.
        //
        // BREAK IT: hardcode ridgeAlongX = true -> the rotated case keeps a 20 m ridge on a 12 m span.
        [Test]
        public void TheRidgeRunsTheLongWayEitherWayRound()
        {
            var wide = Spec(RoofKind.Gable, 20f, 12f);
            var tall = Spec(RoofKind.Gable, 12f, 20f);
            Assert.That(wide.RidgeAlongX, Is.True);
            Assert.That(tall.RidgeAlongX, Is.False);
            Assert.That(RoofShapes.RidgeLength(wide), Is.EqualTo(20f).Within(1e-3f));
            Assert.That(RoofShapes.RidgeLength(tall), Is.EqualTo(20f).Within(1e-3f));
            // ...and the run is half the SHORT span in both.
            Assert.That(wide.HalfRun, Is.EqualTo(6f).Within(1e-3f));
            Assert.That(tall.HalfRun, Is.EqualTo(6f).Within(1e-3f));

            var hip = Spec(RoofKind.Hip, 12f, 20f);
            Assert.That(RoofShapes.RidgeLength(hip), Is.EqualTo(8f).Within(1e-3f),
                "a rotated hip shortens the ridge on the long axis, not the short one");
        }

        // Rise and slope are DERIVED from the span and the pitch, and the two agree with each other. A
        // Pythagoras check is worth more than restating either formula: it fails if only one of them is
        // wrong, which is exactly how a roof ends up meeting its gable end at the apex only.
        [Test]
        public void RiseAndSlopeAgreeAtEveryPitch()
        {
            foreach (float pitch in new[] { 5f, 15f, 30f, 45f, 60f, 70f })
                foreach (var kind in new[] { RoofKind.Gable, RoofKind.Hip })
                {
                    var s = Spec(kind, 20f, 12f, pitch);
                    Assert.That(s.Rise, Is.EqualTo(6f * MathF.Tan(pitch * MathF.PI / 180f)).Within(1e-3f),
                        $"{kind} at {pitch}");
                    Assert.That(s.Slope * s.Slope, Is.EqualTo(s.HalfRun * s.HalfRun + s.Rise * s.Rise).Within(1e-2f),
                        $"{kind} at {pitch}: slope, run and rise must close the triangle");
                }
        }

        // A steeper roof is a taller roof. Trivial to state and the thing a botched clamp or a degrees/radians
        // slip breaks silently -- tan(30) read as radians gives a rise that falls with pitch over part of the
        // range and still looks like a number.
        [Test]
        public void SteeperMeansTaller()
        {
            float last = -1f;
            foreach (float pitch in new[] { 5f, 15f, 30f, 45f, 60f, 70f })
            {
                float rise = Spec(RoofKind.Gable, 20f, 12f, pitch).Rise;
                Assert.That(rise, Is.GreaterThan(last), $"pitch {pitch} gave {rise}");
                last = rise;
            }
        }

        [Test]
        public void PitchIsClampedToSomethingBuildable()
        {
            Assert.That(Spec(RoofKind.Gable, 20f, 12f, 500f).Clamped, Is.EqualTo(RoofSpec.MaxPitch));
            Assert.That(Spec(RoofKind.Gable, 20f, 12f, -20f).Clamped, Is.EqualTo(RoofSpec.MinPitch));
        }

        // GABLE ENDS GO ON THE WALLS ACROSS THE RIDGE ONLY. A peak on all four walls is the classic
        // wrong-looking roof, and a hip needs none at all because its own end planes close it -- so a hip
        // that still raised gables would push two triangles up through its own roof.
        //
        // BREAK IT: return true for Hip -> two gable ends poke through the hipped ends.
        [Test]
        public void OnlyWallsAcrossAGableRidgeGetGableEnds()
        {
            var gable = Spec(RoofKind.Gable, 20f, 12f);      // ridge along X
            Assert.That(RoofShapes.WallGetsGable(gable, true), Is.False, "a wall along the ridge stays flat");
            Assert.That(RoofShapes.WallGetsGable(gable, false), Is.True, "a wall across it is a gable end");

            foreach (bool alongX in new[] { true, false })
            {
                Assert.That(RoofShapes.WallGetsGable(Spec(RoofKind.Hip, 20f, 12f), alongX), Is.False,
                    "a hip closes its own ends");
                Assert.That(RoofShapes.WallGetsGable(Spec(RoofKind.Flat, 20f, 12f), alongX), Is.False,
                    "a flat roof has no ends to close");
            }
        }

        // The gable triangle's slope has to be the ROOF's slope, so it is set by the wall's own half-length
        // rather than the roof footprint's -- those differ the moment the roof overhangs, and using the
        // footprint's rise made the triangle 3.01 deg steeper on a measured 9 m wall, meeting the roof at
        // the apex only and opening a 0.27 m wedge down both edges. The band makes up the difference.
        //
        // BREAK IT: return s.Rise from GableRiseForWall -> the triangle stops matching the wall it sits on
        // and the band goes to zero, which is the original bug exactly.
        [Test]
        public void AGableEndMatchesTheRoofsSlopeNotItsRise()
        {
            var s = Spec(RoofKind.Gable, 20f, 12f, 20f);
            // A 9 m wall under a roof whose footprint spans 12: the wall is shorter than the span it sits in.
            float tri = RoofShapes.GableRiseForWall(s, 9f);
            float band = RoofShapes.GableBandForWall(s, 9f);

            float roofSlopeTan = MathF.Tan(20f * MathF.PI / 180f);
            Assert.That(tri / 4.5f, Is.EqualTo(roofSlopeTan).Within(1e-3f),
                "the triangle's own pitch must equal the roof's");
            Assert.That(tri + band, Is.EqualTo(s.Rise).Within(1e-3f),
                "triangle plus band has to reach the ridge");
            Assert.That(band, Is.GreaterThan(0f), "an overhanging roof leaves a band to fill");

            // A wall that spans the whole footprint needs no band at all.
            Assert.That(RoofShapes.GableBandForWall(s, 12f), Is.EqualTo(0f).Within(1e-3f));
            Assert.That(RoofShapes.GableRiseForWall(s, 12f), Is.EqualTo(s.Rise).Within(1e-3f));
        }

        [Test]
        public void ARoofWithNoFootprintIsNoPlanes()
        {
            var s = Spec(RoofKind.Hip);
            s.MaxX = s.MinX;
            Assert.That(RoofShapes.Planes(s), Is.Empty);
        }
    }
}
