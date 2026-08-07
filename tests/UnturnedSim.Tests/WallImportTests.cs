using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the retail -> editable-format translator.
    //
    // The claim under test is that importing and generating are the SAME operation run in opposite
    // directions, so the strongest thing available is a round trip: a wall with known openings, partitioned
    // into panels, must come back out of the importer as the wall and openings it started as. A test that
    // re-derived the expected holes by hand would only prove the hand arithmetic.
    [TestFixture]
    public class WallImportTests
    {
        const float W = 12f, H = 4.25f;

        static List<WallSolid> Panels(float w, float h, List<WallOpening> openings) =>
            WallOpenings.Solids(w, h, openings);

        static bool Matches(List<WallOpening> got, WallOpening want, float tol = 1e-2f)
        {
            foreach (var g in got)
                if (Math.Abs(g.U - want.U) < tol && Math.Abs(g.V - want.V) < tol
                    && Math.Abs(g.Width - want.Width) < tol && Math.Abs(g.Height - want.Height) < tol) return true;
            return false;
        }

        // BREAK IT: pass the panels as panels rather than as holes -- the complement comes back as the wall
        // itself and no opening is recovered.
        [Test]
        public void AWallWithOpeningsRoundTripsBackToTheSameOpenings()
        {
            var want = new List<WallOpening>
            {
                new WallOpening(1f, 0f, 2.5f, 3.75f),          // a door
                new WallOpening(5f, 1f, 3f, 2.75f),            // a window
                new WallOpening(9f, 1f, 2.5f, 2.75f),          // another
            };
            var rec = WallImport.FromPanels(Panels(W, H, want));

            Assert.That(rec.Width, Is.EqualTo(W).Within(1e-3f), "the wall is the panels' bounding box");
            Assert.That(rec.Height, Is.EqualTo(H).Within(1e-3f));
            Assert.That(rec.Openings.Count, Is.EqualTo(want.Count), "one opening back per opening in");
            foreach (var o in want)
                Assert.That(Matches(rec.Openings, o), Is.True, $"opening at u={o.U} came back");
        }

        // The area identity, which holds however the partition chooses to cut things up.
        // BREAK IT: any complement bug at all moves the total.
        [Test]
        public void TheRecoveredHoleAreaMatchesWhatWasRemoved()
        {
            var rng = new Random(20260807);
            for (int iter = 0; iter < 120; iter++)
            {
                var ops = new List<WallOpening>();
                int n = rng.Next(1, 4);
                for (int k = 0; k < n; k++)
                {
                    float u = (float)rng.NextDouble() * (W - 3f);
                    float v = (float)rng.NextDouble() * (H - 2f);
                    ops.Add(new WallOpening(u, v, 1f + (float)rng.NextDouble() * 2f, 1f + (float)rng.NextDouble()));
                }
                var panels = Panels(W, H, ops);
                float solid = 0f;
                foreach (var p in panels) solid += p.Area;

                var rec = WallImport.FromPanels(panels, minOpening: 0f);
                float holes = 0f;
                foreach (var o in rec.Openings) holes += o.Width * o.Height;
                Assert.That(solid + holes, Is.EqualTo(W * H).Within(2e-2f), $"iter {iter}: solid + holes = the wall");
            }
        }

        // BREAK IT: keep every complement rectangle -> a ripped wall imports with hundreds of hairline
        // "openings" that are really just seams between panels.
        [Test]
        public void HairlineGapsBetweenPanelsAreNotImportedAsOpenings()
        {
            // two panels with a 4 mm gap between them: a seam, not a window
            var panels = new List<WallSolid>
            {
                new WallSolid(0f, 0f, 5.998f, H),
                new WallSolid(6.002f, 0f, W, H),
            };
            var rec = WallImport.FromPanels(panels);
            Assert.That(rec.Openings.Count, Is.EqualTo(0), "a 4 mm seam is not an opening");

            var withHole = new List<WallSolid>
            {
                new WallSolid(0f, 0f, 4f, H),
                new WallSolid(7f, 0f, W, H),
            };
            Assert.That(WallImport.FromPanels(withHole).Openings.Count, Is.EqualTo(1), "a 3 m gap still is");
        }

        // BREAK IT: snap with no tolerance limit -> a genuinely odd-sized opening gets dragged onto the ladder
        // and an imported building quietly stops matching the one it came from.
        [Test]
        public void SnappingPullsRippedSizesOntoTheMeasuredLadderButOnlyJustOff()
        {
            var ripped = new WallOpening(3f, 0.988f, 2.4997f, 3.2712f);   // a retail door, a hair off
            var snapped = WallImport.SnapToRetail(ripped);
            Assert.That(snapped.Width, Is.EqualTo(2.5f).Within(1e-3f), "width lands on the measured 2.5");
            Assert.That(snapped.V, Is.EqualTo(1.0f).Within(1e-3f), "sill lands on the measured 1.00");
            Assert.That(snapped.V + snapped.Height, Is.EqualTo(4.25f).Within(1e-3f), "head lands on the measured 4.25");

            var odd = new WallOpening(3f, 2.0f, 6.4f, 1.2f);   // nothing on the ladder is near this
            var left = WallImport.SnapToRetail(odd);
            Assert.That(left.Width, Is.EqualTo(6.4f).Within(1e-3f), "an unusual size is left alone");
            Assert.That(left.V, Is.EqualTo(2.0f).Within(1e-3f));
        }

        // BREAK IT: assume the panels start at the origin -> a wall recovered from a plane that does not is
        // offset by its own position, and every opening in it lands somewhere else.
        [Test]
        public void OpeningsAreReturnedRelativeToTheWallNotToTheWorld()
        {
            var want = new List<WallOpening> { new WallOpening(2f, 1f, 3f, 2f) };
            var panels = Panels(W, H, want);
            var shifted = new List<WallSolid>();
            foreach (var p in panels) shifted.Add(new WallSolid(p.U0 + 40f, p.V0 + 7f, p.U1 + 40f, p.V1 + 7f));

            var rec = WallImport.FromPanels(shifted);
            Assert.That(rec.U0, Is.EqualTo(40f).Within(1e-3f), "the wall knows where it is");
            Assert.That(rec.V0, Is.EqualTo(7f).Within(1e-3f));
            Assert.That(rec.Openings.Count, Is.EqualTo(1));
            Assert.That(rec.Openings[0].U, Is.EqualTo(2f).Within(1e-2f), "and the opening is in WALL space");
            Assert.That(rec.Openings[0].V, Is.EqualTo(1f).Within(1e-2f));
        }

        [Test]
        public void EmptyAndDegenerateInputDoNotThrow()
        {
            Assert.That(WallImport.FromPanels(null).Openings.Count, Is.EqualTo(0));
            Assert.That(WallImport.FromPanels(new List<WallSolid>()).Openings.Count, Is.EqualTo(0));
            var flat = WallImport.FromPanels(new List<WallSolid> { new WallSolid(1f, 1f, 1f, 5f) });
            Assert.That(flat.Openings.Count, Is.EqualTo(0), "a zero-width plane has no openings, and does not throw");
        }
    }
}
